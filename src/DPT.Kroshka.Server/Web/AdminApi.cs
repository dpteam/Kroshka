using System.Security.Cryptography;
using System.Text;
using DPT.Kroshka.Server.Storage;
using DPT.Kroshka.Shared;

namespace DPT.Kroshka.Server.Web;

/// <summary>
/// The admin board. Runs on its own port so /api/admin/* simply does not exist on the
/// public listener - the same split DPT Social uses, and the reason a leaked public
/// URL is not a leaked admin panel.
///
/// Auth is a separate password (admin.password) held in a cookie, not a user session:
/// an owner can hand out member accounts without also handing out the board.
/// </summary>
public sealed class AdminApi
{
    private const string CookieName = "kroshka_admin";
    private const int MaxAttempts = 10;

    private readonly KroshkaConfig _cfg;
    private readonly KroshkaStore _store;
    private readonly BlobStore _blobs;
    private readonly WsHub _hub;
    private int _failedAttempts;

    public AdminApi(KroshkaConfig cfg, KroshkaStore store, BlobStore blobs, WsHub hub)
    {
        _cfg = cfg;
        _store = store;
        _blobs = blobs;
        _hub = hub;
    }

    public void Map(WebApplication app)
    {
        app.MapGet("/api/admin/health", () => Results.Json(new
        {
            ok = true,
            admin = true,
            version = Protocol.Version,
            uptimeSec = (long)(DateTime.UtcNow - App.StartedUtc).TotalSeconds,
            // Lets the login screen explain *why* it cannot work instead of just
            // rejecting a password the owner never had a chance to set.
            passwordConfigured = !string.IsNullOrEmpty(_cfg.AdminPassword)
        }));

        app.MapPost("/api/admin/login", (HttpContext ctx) => Login(ctx));
        app.MapPost("/api/admin/logout", (HttpContext ctx) => Logout(ctx));

        app.MapGet("/api/admin/state", (HttpContext ctx) => Guarded(ctx, () => Results.Json(BuildState())));

        app.MapGet("/api/admin/logs", (HttpContext ctx) => Guarded(ctx, () =>
        {
            int lines = ParseInt(ctx.Request.Query["lines"], 200);
            List<LogEntry> entries = Log.Tail(0, Math.Clamp(lines, 10, 2000), null, ctx.Request.Query["category"].ToString());
            return Results.Json(new
            {
                entries = entries.Select(e => new
                {
                    seq = e.Seq,
                    time = e.TimeText,
                    level = e.Level.ToString().ToLowerInvariant(),
                    category = e.Category,
                    message = e.Message
                }),
                file = Log.CurrentFile
            });
        }));

        app.MapPost("/api/admin/users", (HttpContext ctx) => Guarded(ctx, () => CreateUser(ctx)));
        app.MapPatch("/api/admin/users/{id}", (HttpContext ctx, string id) => Guarded(ctx, () => UpdateUser(ctx, id)));
        app.MapDelete("/api/admin/users/{id}", (HttpContext ctx, string id) => Guarded(ctx, () => DeleteUser(id)));

        app.MapPost("/api/admin/rooms", (HttpContext ctx) => Guarded(ctx, () => CreateRoom(ctx)));
        app.MapDelete("/api/admin/rooms/{id}", (HttpContext ctx, string id) => Guarded(ctx, () => DeleteRoom(id)));

        app.MapPost("/api/admin/maintenance/backup", (HttpContext ctx) => Guarded(ctx, Backup));
        app.MapPost("/api/admin/maintenance/compact", (HttpContext ctx) => Guarded(ctx, Compact));
        app.MapPost("/api/admin/maintenance/prune", (HttpContext ctx) => Guarded(ctx, () => Prune(ctx)));
        app.MapPost("/api/admin/maintenance/sweep", (HttpContext ctx) => Guarded(ctx, Sweep));
        app.MapGet("/api/admin/backups", (HttpContext ctx) => Guarded(ctx, ListBackups));

        app.MapGet("/api/admin/config", (HttpContext ctx) => Guarded(ctx, () => Results.Json(new
        {
            editable = KroshkaConfig.EditableKeys,
            values = CurrentValues()
        })));

        app.MapPost("/api/admin/config", (HttpContext ctx) => Guarded(ctx, () => SaveConfig(ctx)));
    }

    // ==================================================================== auth

    private static IResult Guarded(HttpContext ctx, Func<IResult> body)
    {
        if (!IsAdmin(ctx))
            return Results.Json(new ApiError("unauthorized", "Нужен вход в админку"), statusCode: 401);

        try
        {
            return body();
        }
        catch (Exception ex)
        {
            Log.Error("ADMIN", ex);
            return Results.Json(new ApiError("internal", "Внутренняя ошибка"), statusCode: 500);
        }
    }

    private static bool IsAdmin(HttpContext ctx)
    {
        return ctx.Request.Cookies.TryGetValue(CookieName, out string value)
               && !string.IsNullOrEmpty(value)
               && AdminPasswordMatches(value);
    }

    private static bool AdminPasswordMatches(string presented)
    {
        // The cookie holds the same derived value as the configured password, so the
        // plain password never travels back and forth after login.
        KroshkaConfig cfg = App.CurrentConfig;
        if (cfg == null || string.IsNullOrEmpty(cfg.AdminPassword)) return false;
        return Crypto.FixedEquals(presented, DerivedToken(cfg.AdminPassword));
    }

    private static string DerivedToken(string password)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("kroshka-admin:" + password))).ToLowerInvariant();
    }

    private IResult Login(HttpContext ctx)
    {
        LoginRequest req;
        try
        {
            req = System.Text.Json.JsonSerializer.Deserialize<LoginRequest>(ReadBody(ctx), Json.Options);
        }
        catch
        {
            return Results.Json(new ApiError("bad-json", "Некорректный запрос"), statusCode: 400);
        }

        string ip = Api.ClientIp(ctx);

        if (string.IsNullOrEmpty(_cfg.AdminPassword))
            return Results.Json(new ApiError("no-password", "admin.password не задан в config.ini"), statusCode: 403);

        if (Interlocked.CompareExchange(ref _failedAttempts, 0, 0) >= MaxAttempts)
        {
            Log.Warn("ADMIN", $"too many failed admin logins from {ip}");
            return Results.Json(new ApiError("locked", "Слишком много попыток, перезапустите сервер"), statusCode: 429);
        }

        if (!Crypto.FixedEquals(req?.Password ?? "", _cfg.AdminPassword))
        {
            Interlocked.Increment(ref _failedAttempts);
            Log.Warn("ADMIN", $"wrong admin password from {ip}");
            return Results.Json(new ApiError("password", "Неверный пароль"), statusCode: 403);
        }

        Interlocked.Exchange(ref _failedAttempts, 0);

        ctx.Response.Cookies.Append(CookieName, DerivedToken(_cfg.AdminPassword), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = TimeSpan.FromHours(12)
        });

        Log.Info("ADMIN", $"admin signed in from {ip}");
        return Results.Json(new { ok = true });
    }

    private IResult Logout(HttpContext ctx)
    {
        ctx.Response.Cookies.Delete(CookieName);
        return Results.Json(new { ok = true });
    }

    // =================================================================== state

    private object BuildState()
    {
        (int blobFiles, long blobBytes) = _blobs.Stats();

        long dataBytes = FileStore.DirectorySize(_cfg.DataDirPath);
        long logBytes = FileStore.DirectorySize(_cfg.LogDirPath);

        List<object> users = _store.AllUsers()
            .OrderBy(u => u.CreatedUtc)
            .Select(u => (object)new
            {
                id = u.Id,
                nick = u.Nick,
                displayName = u.DisplayName,
                role = u.Role.ToString().ToLowerInvariant(),
                banned = u.Banned,
                createdUtc = u.CreatedUtc,
                lastSeenUtc = u.LastSeenUtc,
                online = _hub.IsOnline(u.Id)
            })
            .ToList();

        List<object> rooms = _store.AllRooms()
            .OrderBy(r => r.SortOrder)
            .Select(r => (object)new
            {
                id = r.Id,
                name = r.Name,
                isPrivate = r.IsPrivate,
                readOnly = r.ReadOnly,
                archived = r.Archived,
                members = r.Members.Count,
                messages = r.LastSeq,
                lastUtc = r.LastMessageUtc,
                createdUtc = r.CreatedUtc
            })
            .ToList();

        long totalMessages = _store.AllRooms().Sum(r => r.LastSeq) + _store.AllDms().Sum(d => d.LastSeq);

        return new
        {
            ok = true,
            server = new
            {
                name = _cfg.SiteName,
                version = Protocol.Version,
                port = _cfg.Port,
                adminPort = _cfg.AdminPort,
                authMode = _cfg.AuthMode.ToString().ToLowerInvariant(),
                uptimeSec = (long)(DateTime.UtcNow - App.StartedUtc).TotalSeconds,
                startedUtc = App.StartedUtc,
                dataDir = _cfg.DataDirPath,
                logDir = _cfg.LogDirPath,
                logFile = Log.CurrentFile
            },
            stats = new
            {
                users = _store.UserCount,
                rooms = _store.AllRooms().Count,
                dms = _store.AllDms().Count,
                messages = totalMessages,
                sessions = _store.SessionCount,
                online = _hub.ConnectionCount,
                blobFiles,
                blobMb = Math.Round(blobBytes / 1024.0 / 1024.0, 1),
                dataMb = Math.Round(dataBytes / 1024.0 / 1024.0, 1),
                logMb = Math.Round(logBytes / 1024.0 / 1024.0, 1)
            },
            users,
            rooms,
            warnings = _cfg.Warnings
        };
    }

    // =================================================================== users

    private IResult CreateUser(HttpContext ctx)
    {
        AdminCreateUser req = System.Text.Json.JsonSerializer.Deserialize<AdminCreateUser>(ReadBody(ctx), Json.Options);
        if (req == null || string.IsNullOrWhiteSpace(req.Nick))
            return Results.Json(new ApiError("nick", "Укажите ник"), statusCode: 400);

        if (_store.NickTaken(req.Nick.Trim()))
            return Results.Json(new ApiError("taken", "Такой ник уже занят"), statusCode: 409);

        UserRole role = req.Role?.ToLowerInvariant() switch
        {
            "admin" => UserRole.Admin,
            "owner" => UserRole.Owner,
            _ => UserRole.Member
        };

        User user = _store.CreateUser(req.Nick, req.DisplayName ?? req.Nick, role);
        Log.Info("ADMIN", $"created user {user.Nick} role={role}");
        return Results.Json(new { ok = true, user = Api.Public(user) });
    }

    private IResult UpdateUser(HttpContext ctx, string id)
    {
        User user = _store.GetUser(id);
        if (user == null) return Results.Json(new ApiError("not-found", "Пользователь не найден"), statusCode: 404);

        AdminUpdateUser req = System.Text.Json.JsonSerializer.Deserialize<AdminUpdateUser>(ReadBody(ctx), Json.Options);
        if (req == null) return Results.Json(new ApiError("bad-json", "Пустой запрос"), statusCode: 400);

        _store.UpdateUser(id, u =>
        {
            if (req.Role != null)
            {
                u.Role = req.Role.ToLowerInvariant() switch
                {
                    "owner" => UserRole.Owner,
                    "admin" => UserRole.Admin,
                    _ => UserRole.Member
                };
            }

            if (req.Banned.HasValue)
            {
                u.Banned = req.Banned.Value;
                u.BanReason = req.BanReason ?? "";
                if (req.Banned.Value)
                {
                    foreach (Session s in _store.SessionsOf(id)) _store.DeleteSession(s.Id);
                }
            }

            if (req.DisplayName != null) u.DisplayName = req.DisplayName;
        });

        User updated = _store.GetUser(id);
        _hub.BroadcastAll(WsFrame.Make(Protocol.Events.UserUpdated) with { UserId = id, Data = Api.Public(updated) });

        return Results.Json(new { ok = true, user = Api.Public(updated) });
    }

    private IResult DeleteUser(string id)
    {
        User user = _store.GetUser(id);
        if (user == null) return Results.Json(new ApiError("not-found", "Пользователь не найден"), statusCode: 404);

        // Never let the board empty itself out of an owner.
        if (user.Role == UserRole.Owner && _store.AllUsers().Count(u => u.Role == UserRole.Owner) <= 1)
            return Results.Json(new ApiError("last-owner", "Нельзя удалить последнего владельца"), statusCode: 409);

        _store.DeleteUser(id);
        Log.Warn("ADMIN", $"deleted user {user.Nick}");
        return Results.Json(new { ok = true });
    }

    // =================================================================== rooms

    private IResult CreateRoom(HttpContext ctx)
    {
        CreateRoomRequest req = System.Text.Json.JsonSerializer.Deserialize<CreateRoomRequest>(ReadBody(ctx), Json.Options);
        if (req == null || string.IsNullOrWhiteSpace(req.Name))
            return Results.Json(new ApiError("name", "Укажите название"), statusCode: 400);

        Room room = _store.CreateRoom(req.Name, req.Topic ?? "", req.Emoji ?? "", req.IsPrivate, "", req.Members);
        _hub.BroadcastAll(WsFrame.Make(Protocol.Events.RoomCreated) with { ConversationId = room.Id, Data = room });

        Log.Info("ADMIN", $"created room {room.Name}");
        return Results.Json(new { ok = true, room });
    }

    private IResult DeleteRoom(string id)
    {
        Room room = _store.GetRoom(id);
        if (room == null) return Results.Json(new ApiError("not-found", "Комната не найдена"), statusCode: 404);

        _hub.BroadcastToConversation(id, WsFrame.Make(Protocol.Events.RoomDeleted) with { ConversationId = id });
        _hub.ForgetConversation(id);
        _store.DeleteRoom(id);

        Log.Warn("ADMIN", $"deleted room {room.Name}");
        return Results.Json(new { ok = true });
    }

    // ============================================================= maintenance

    private IResult Backup()
    {
        try
        {
            Directory.CreateDirectory(_cfg.BackupDirPath);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string target = Path.Combine(_cfg.BackupDirPath, stamp);
            Directory.CreateDirectory(target);

            _store.SaveAll();
            FileStore.CopyDirectory(_cfg.DataDirPath, target);

            PruneBackups();

            Log.Info("ADMIN", "backup created: " + stamp);
            return Results.Json(new { ok = true, path = target, name = stamp });
        }
        catch (Exception ex)
        {
            Log.Error("ADMIN", ex);
            return Results.Json(new ApiError("backup", ex.Message), statusCode: 500);
        }
    }

    private void PruneBackups()
    {
        try
        {
            string[] dirs = Directory.GetDirectories(_cfg.BackupDirPath);
            Array.Sort(dirs, StringComparer.Ordinal);
            int remove = dirs.Length - _cfg.KeepBackups;
            for (int i = 0; i < remove; i++)
            {
                try { Directory.Delete(dirs[i], true); } catch { }
            }
        }
        catch { }
    }

    private IResult ListBackups()
    {
        List<object> list = new();
        try
        {
            foreach (string dir in Directory.GetDirectories(_cfg.BackupDirPath))
            {
                DirectoryInfo info = new(dir);
                list.Add(new
                {
                    name = info.Name,
                    createdUtc = info.CreationTimeUtc,
                    sizeMb = Math.Round(FileStore.DirectorySize(dir) / 1024.0 / 1024.0, 1)
                });
            }
        }
        catch { }

        return Results.Json(new { backups = list.OrderByDescending(b => b.GetType().GetProperty("name")?.GetValue(b)?.ToString()).ToList() });
    }

    /// <summary>Rewrites every message log, dropping malformed lines and orphaned months.</summary>
    private IResult Compact()
    {
        int dropped = 0;

        foreach (Room room in _store.AllRooms())
            dropped += _store.Messages(room.Id).Compact();

        foreach (Direct dm in _store.AllDms())
            dropped += _store.Messages(dm.Id).Compact();

        _store.InvalidateLogs();
        _store.SaveAll();

        Log.Info("ADMIN", $"compact finished, dropped {dropped} malformed lines");
        return Results.Json(new { ok = true, droppedLines = dropped });
    }

    /// <summary>Deletes messages older than storage.pruneMonths. 0 means "keep everything".</summary>
    private IResult Prune(HttpContext ctx)
    {
        PruneRequest req = System.Text.Json.JsonSerializer.Deserialize<PruneRequest>(ReadBody(ctx), Json.Options);
        int months = req?.Months ?? _cfg.PruneMonths;

        if (months <= 0)
            return Results.Json(new ApiError("months", "Укажите срок в месяцах (0 = хранить всё)"), statusCode: 400);

        DateTime cutoff = DateTime.UtcNow.AddMonths(-months);
        int removed = 0;

        foreach (Room room in _store.AllRooms())
        {
            removed += _store.Messages(room.Id).Prune(cutoff);
            long lastSeq = _store.Messages(room.Id).LastSeq;
            if (room.LastSeq != lastSeq)
            {
                room.LastSeq = lastSeq;
                _store.SaveRoom(room);
            }
        }

        foreach (Direct dm in _store.AllDms())
        {
            removed += _store.Messages(dm.Id).Prune(cutoff);
            long lastSeq = _store.Messages(dm.Id).LastSeq;
            if (dm.LastSeq != lastSeq)
            {
                dm.LastSeq = lastSeq;
                _store.SaveDm(dm);
            }
        }

        Log.Warn("ADMIN", $"pruned {removed} messages older than {months} months");
        return Results.Json(new { ok = true, removed, cutoff = cutoff.ToString("O") });
    }

    /// <summary>Deletes blobs no message points at any more.</summary>
    private IResult Sweep()
    {
        HashSet<string> referenced = new(StringComparer.Ordinal);

        foreach (Room room in _store.AllRooms())
            foreach (Message m in _store.Messages(room.Id).All())
                if (m.Attachment?.BlobId != null) referenced.Add(m.Attachment.BlobId);

        foreach (Direct dm in _store.AllDms())
            foreach (Message m in _store.Messages(dm.Id).All())
                if (m.Attachment?.BlobId != null) referenced.Add(m.Attachment.BlobId);

        foreach (User user in _store.AllUsers())
            if (!string.IsNullOrEmpty(user.AvatarBlob)) referenced.Add(user.AvatarBlob);

        (int removed, long bytes) = _blobs.SweepUnreferenced(referenced);

        Log.Info("ADMIN", $"swept {removed} orphan blobs, freed {bytes / 1024 / 1024} MB");
        return Results.Json(new
        {
            ok = true,
            removed,
            freedMb = Math.Round(bytes / 1024.0 / 1024.0, 2)
        });
    }

    // ================================================================== config

    private Dictionary<string, string> CurrentValues()
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);

        foreach (string key in KroshkaConfig.EditableKeys)
        {
            int dot = key.IndexOf('.');
            string section = key[..dot];
            string name = key[(dot + 1)..];

            string value = (section, name) switch
            {
                ("site", "name") => _cfg.SiteName,
                ("site", "tagline") => _cfg.SiteTagline,
                ("site", "motd") => _cfg.Motd,
                ("site", "footer") => _cfg.Footer,
                ("site", "theme") => _cfg.DefaultTheme,
                ("site", "defaultSound") => _cfg.DefaultSound ? "true" : "false",
                ("auth", "mode") => _cfg.AuthMode.ToString().ToLowerInvariant(),
                ("auth", "allowRegister") => _cfg.AllowRegister ? "true" : "false",
                ("auth", "password") => _cfg.SharedPassword,
                ("auth", "inviteCode") => _cfg.InviteCode,
                ("auth", "allowNickChange") => _cfg.AllowNickChange ? "true" : "false",
                ("branding", "accent") => _cfg.Accent,
                ("branding", "accentHi") => _cfg.AccentHi,
                ("branding", "accentLo") => _cfg.AccentLo,
                ("branding", "darkBg") => _cfg.DarkBg,
                ("branding", "darkPanel") => _cfg.DarkPanel,
                ("branding", "oledBg") => _cfg.OledBg,
                ("features", "reactions") => _cfg.FeaturesReactions ? "true" : "false",
                ("features", "replies") => _cfg.FeaturesReplies ? "true" : "false",
                ("features", "pins") => _cfg.FeaturesPins ? "true" : "false",
                ("features", "voice") => _cfg.FeaturesVoice ? "true" : "false",
                ("features", "files") => _cfg.FeaturesFiles ? "true" : "false",
                ("features", "search") => _cfg.FeaturesSearch ? "true" : "false",
                ("features", "typing") => _cfg.FeaturesTyping ? "true" : "false",
                ("features", "presence") => _cfg.FeaturesPresence ? "true" : "false",
                ("features", "edit") => _cfg.FeaturesEdit ? "true" : "false",
                ("features", "delete") => _cfg.FeaturesDelete ? "true" : "false",
                ("features", "rooms") => _cfg.FeaturesRooms ? "true" : "false",
                ("features", "dm") => _cfg.FeaturesDm ? "true" : "false",
                ("limits", "maxMessageLen") => _cfg.MaxMessageLen.ToString(),
                ("limits", "maxUploadMb") => _cfg.MaxUploadMb.ToString(),
                ("storage", "pruneMonths") => _cfg.PruneMonths.ToString(),
                ("storage", "keepBackups") => _cfg.KeepBackups.ToString(),
                ("server", "discoverable") => _cfg.Discoverable ? "true" : "false",
                _ => ""
            };

            values[key] = value;
        }

        return values;
    }

    private IResult SaveConfig(HttpContext ctx)
    {
        Dictionary<string, string> incoming;
        try
        {
            incoming = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(ReadBody(ctx), Json.Options);
        }
        catch
        {
            return Results.Json(new ApiError("bad-json", "Некорректный запрос"), statusCode: 400);
        }

        if (incoming == null) return Results.Json(new ApiError("bad-json", "Пустой запрос"), statusCode: 400);

        Dictionary<string, string> overrides = FileStore.ReadJson<Dictionary<string, string>>(_cfg.OverridesPath, new());

        foreach (KeyValuePair<string, string> kv in incoming)
        {
            if (!KroshkaConfig.EditableKeys.Contains(kv.Key)) continue;
            if (kv.Key is "branding.accent" or "branding.accentHi" or "branding.accentLo" or "branding.darkBg" or "branding.darkPanel" or "branding.oledBg")
            {
                if (!KroshkaConfig.IsHexColor(kv.Value) && !string.IsNullOrEmpty(kv.Value)) continue;
            }
            overrides[kv.Key] = kv.Value ?? "";
        }

        FileStore.WriteAtomic(_cfg.OverridesPath, overrides);

        Log.Info("ADMIN", "config overrides saved: " + string.Join(", ", incoming.Keys));
        return Results.Json(new { ok = true, restartRequired = true, saved = incoming.Keys.ToList() });
    }

    // ================================================================= helpers

    private static string ReadBody(HttpContext ctx)
    {
        // BufferJsonBody already consumed the stream; reading it again would block.
        if (ctx.Items.TryGetValue(App.BufferedBodyKey, out object buffered) && buffered is string text)
            return text;

        using StreamReader reader = new(ctx.Request.Body, System.Text.Encoding.UTF8, false, 1024, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static int ParseInt(string value, int fallback) => int.TryParse(value, out int n) ? n : fallback;

    public sealed class AdminCreateUser
    {
        public string Nick { get; set; }
        public string DisplayName { get; set; }
        public string Role { get; set; }
    }

    public sealed class AdminUpdateUser
    {
        public string Role { get; set; }
        public bool? Banned { get; set; }
        public string BanReason { get; set; }
        public string DisplayName { get; set; }
    }

    public sealed class PruneRequest
    {
        public int? Months { get; set; }
    }
}
