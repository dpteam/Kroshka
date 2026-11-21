using System.Text.Json;
using DPT.Kroshka.Server.Storage;
using DPT.Kroshka.Shared;

namespace DPT.Kroshka.Server.Web;

/// <summary>
/// The REST surface. Every handler is small on purpose: validate, touch the store,
/// emit an event over the socket, return JSON. Business rules live in one place each.
/// </summary>
public sealed class Api
{
    public const string CookieName = "kroshka_sid";

    private readonly KroshkaConfig _cfg;
    private readonly KroshkaStore _store;
    private readonly BlobStore _blobs;
    private readonly WsHub _hub;
    private readonly RateLimiter _limiter = new();

    public Api(KroshkaConfig cfg, KroshkaStore store, BlobStore blobs, WsHub hub)
    {
        _cfg = cfg;
        _store = store;
        _blobs = blobs;
        _hub = hub;
    }

    // ============================================================ registration

    /// <summary>
    /// Adapter that keeps every handler a plain <c>Func&lt;HttpContext, IResult&gt;</c>
    /// while still binding as a RequestDelegate. Without it a single HttpContext
    /// parameter matches the RequestDelegate overload and the result is discarded.
    /// </summary>
    private static RequestDelegate AsDelegate(Func<HttpContext, IResult> handler)
    {
        return async ctx =>
        {
            IResult result = handler(ctx);
            if (result != null) await result.ExecuteAsync(ctx);
        };
    }

    private static RequestDelegate AsDelegate(Func<HttpContext, Task<IResult>> handler)
    {
        return async ctx =>
        {
            IResult result = await handler(ctx);
            if (result != null) await result.ExecuteAsync(ctx);
        };
    }

    public void Map(WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Json(new
        {
            ok = true,
            version = Protocol.Version,
            uptimeSec = (long)(DateTime.UtcNow - App.StartedUtc).TotalSeconds,
            users = _store.UserCount,
            online = _hub.ConnectionCount
        }));

        app.MapGet("/api/bootstrap", () => Results.Json(BuildBootstrap()));

        app.MapPost("/api/login", AsDelegate(LoginAsync));
        app.MapPost("/api/logout", AsDelegate(Logout));
        app.MapGet("/api/me", AsDelegate(ctx => Authed(ctx, user => Results.Json(new { ok = true, user, me = user }))));

        app.MapPatch("/api/me", AsDelegate(UpdateProfile));
        app.MapPost("/api/me/avatar", AsDelegate(UploadAvatarAsync));
        app.MapGet("/api/me/sessions", AsDelegate(Sessions));
        app.MapDelete("/api/me/sessions/{id}", (HttpContext ctx, string id) => RevokeSession(ctx, id));

        app.MapGet("/api/sync", AsDelegate(Sync));
        app.MapGet("/api/users", AsDelegate(Users));
        app.MapGet("/api/users/{id}", (HttpContext ctx, string id) => UserById(ctx, id));

        app.MapGet("/api/messages", AsDelegate(Page));
        app.MapPost("/api/messages/{conversationId}", (HttpContext ctx, string conversationId) => SendAsync(ctx, conversationId));
        app.MapPatch("/api/messages/{id}", (HttpContext ctx, string id) => Edit(ctx, id));
        app.MapDelete("/api/messages/{id}", (HttpContext ctx, string id) => Delete(ctx, id));
        app.MapPost("/api/messages/{id}/reactions", (HttpContext ctx, string id) => React(ctx, id));
        app.MapPost("/api/messages/{id}/pin", (HttpContext ctx, string id) => Pin(ctx, id));
        app.MapPost("/api/read", AsDelegate(Read));

        app.MapGet("/api/rooms", AsDelegate(Rooms));
        app.MapPost("/api/rooms", AsDelegate(CreateRoom));
        app.MapPatch("/api/rooms/{id}", (HttpContext ctx, string id) => UpdateRoom(ctx, id));
        app.MapDelete("/api/rooms/{id}", (HttpContext ctx, string id) => DeleteRoom(ctx, id));
        app.MapPost("/api/dm/{userId}", (HttpContext ctx, string userId) => OpenDm(ctx, userId));

        app.MapGet("/api/search", AsDelegate(Search));
        app.MapPost("/api/upload", AsDelegate(UploadAsync));
        app.MapGet("/api/blob/{id}", (HttpContext ctx, string id) => Blob(ctx, id));

        app.Map("/ws", async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }

            AuthResult auth = Authenticate(ctx);
            if (auth == null)
            {
                ctx.Response.StatusCode = 401;
                return;
            }

            await _hub.AcceptAsync(ctx, auth.User.Id);
        });
    }

    // ==================================================================== auth

    public sealed class AuthResult
    {
        public User User;
        public Session Session;
        public bool ViaCookie;
    }

    public AuthResult Authenticate(HttpContext ctx)
    {
        string token = null;
        bool viaCookie = false;

        if (ctx.Request.Cookies.TryGetValue(CookieName, out string cookie) && !string.IsNullOrEmpty(cookie))
        {
            token = cookie;
            viaCookie = true;
        }
        else
        {
            string header = ctx.Request.Headers.Authorization.ToString();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = header[7..].Trim();
            else if (ctx.Request.Query.TryGetValue("t", out Microsoft.Extensions.Primitives.StringValues q))
                token = q.ToString();
        }

        if (string.IsNullOrEmpty(token)) return null;

        Session session = _store.FindSessionByToken(token);
        if (session == null) return null;
        if (session.ExpiresUtc <= DateTime.UtcNow) return null;

        User user = _store.GetUser(session.UserId);
        if (user == null || user.Banned) return null;

        _store.TouchSession(session);
        return new AuthResult { User = user, Session = session, ViaCookie = viaCookie };
    }

    /// <summary>Wraps a handler that needs a signed-in person.</summary>
    private IResult Authed(HttpContext ctx, Func<User, IResult> handler)
    {
        AuthResult auth = Authenticate(ctx);
        if (auth == null) return Unauthorized();
        return Guard(() => handler(auth.User));
    }

    /// <summary>Drops expired rate-limit buckets so the dictionary cannot grow forever.</summary>
    public void SweepRateLimits() => _limiter.Sweep(TimeSpan.FromMinutes(10));

    /// <summary>
    /// One place that turns an unexpected exception into a logged 500 instead of a
    /// dead connection. This is the "dynamic fault tolerance" the whole app leans on.
    /// </summary>
    private static IResult Guard(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (ApiException ex)
        {
            return Fail(ex.Status, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("API", ex);
            return Fail(500, "internal", "Внутренняя ошибка сервера");
        }
    }

    private static IResult Fail(int status, string code, string message)
    {
        return Results.Json(new ApiError(code, message), statusCode: status);
    }

    private static IResult Unauthorized() => Fail(401, "unauthorized", "Нужно войти заново");

    private static IResult Ok(object payload = null)
    {
        if (payload == null) return Results.Json(new OkResult());
        return Results.Json(payload);
    }

    private static void Require(bool condition, int status, string code, string message)
    {
        if (!condition) throw new ApiException(status, code, message);
    }

    // ==================================================================== login

    private async Task<IResult> LoginAsync(HttpContext ctx)
    {
        LoginRequest req;
        try
        {
            req = JsonSerializer.Deserialize<LoginRequest>(ReadBody(ctx), Json.Options);
        }
        catch (JsonException)
        {
            return Fail(400, "bad-json", "Некорректный запрос");
        }

        if (req == null) return Fail(400, "bad-json", "Пустой запрос");

        string nick = (req.Nick ?? "").Trim();
        if (nick.Length < 2) return Fail(400, "nick", "Ник слишком короткий");
        if (nick.Length > _cfg.MaxNickLen) return Fail(400, "nick", $"Ник длиннее {_cfg.MaxNickLen} символов");
        if (!nick.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))
            return Fail(400, "nick", "В нике можно буквы, цифры, _ - .");

        string ip = ClientIp(ctx);
        if (!_limiter.Allow("login:" + ip, _cfg.MaxFailedLoginsPerIp, TimeSpan.FromMinutes(1)))
            return Fail(429, "slow-down", "Слишком много попыток, подождите минуту");

        User user = _store.FindByNick(nick);
        bool isNew = user == null;

        if (isNew && !_cfg.AllowRegister)
            return Fail(403, "registration-closed", "Регистрация закрыта. Попросите владельца создать вам аккаунт");

        // ---- gate: open / shared password / invite
        if (_cfg.AuthMode == AuthMode.Password)
        {
            if (!Crypto.FixedEquals(req.Password ?? "", _cfg.SharedPassword))
            {
                Log.Warn("AUTH", $"wrong household password for nick={nick} ip={ip}");
                return Fail(403, "password", "Неверный пароль");
            }
        }
        else if (_cfg.AuthMode == AuthMode.Invite)
        {
            bool alreadyIn = !isNew && _store.InviteUsedBy(_cfg.InviteCode, user.Id);
            if (!alreadyIn && !Crypto.FixedEquals((req.InviteCode ?? "").Trim(), _cfg.InviteCode.Trim()))
            {
                Log.Warn("AUTH", $"wrong invite for nick={nick} ip={ip}");
                return Fail(403, "invite", "Неверный код приглашения");
            }
        }

        if (isNew)
        {
            UserRole role = _cfg.FirstUserAdmin && _store.UserCount == 0 ? UserRole.Owner : UserRole.Member;
            user = _store.CreateUser(nick, nick, role);
            if (_cfg.AuthMode == AuthMode.Invite) _store.AddInvite(_cfg.InviteCode, user.Id);
            Log.Info("AUTH", $"registered {user.Nick} as {role} from {ip}");
            EnsureDefaultRooms(user);
        }
        else
        {
            if (user.Banned) return Fail(403, "banned", string.IsNullOrEmpty(user.BanReason) ? "Вы заблокированы" : user.BanReason);
        }

        _store.UpdateUser(user.Id, u => u.LastSeenUtc = DateTime.UtcNow);

        (Session session, string rawToken) = _store.CreateSession(user.Id, req.Device, ip, req.Remember);

        ctx.Response.Cookies.Append(CookieName, rawToken, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = false,              // LAN over plain HTTP; flip when served behind TLS
            Path = "/",
            MaxAge = TimeSpan.FromDays(req.Remember ? _cfg.RememberDays : _cfg.SessionDays)
        });

        Log.Info("AUTH", $"{user.Nick} signed in from {ip} device='{req.Device}'");

        return Ok(new LoginResult
        {
            Ok = true,
            Token = rawToken,
            UserId = user.Id,
            User = user,
            ServerName = _cfg.SiteName,
            ExpiresUtc = session.ExpiresUtc
        });
    }

    private IResult Logout(HttpContext ctx)
    {
        AuthResult auth = Authenticate(ctx);
        if (auth != null)
        {
            _store.DeleteSession(auth.Session.Id);
            Log.Info("AUTH", $"{auth.User.Nick} signed out");
        }

        ctx.Response.Cookies.Delete(CookieName);
        return Ok();
    }

    /// <summary>
    /// A fresh server with no rooms is a blank wall. Give the first household member
    /// a general room so the app is usable the second it opens.
    /// </summary>
    private void EnsureDefaultRooms(User creator)
    {
        if (_store.AllRooms().Count > 0) return;

        Room general = _store.CreateRoom("Общий", "Главная комната", "", false, creator.Id, new List<string>());
        general.Members.Clear();     // public: membership is implicit
        _store.SaveRoom(general);

        Log.Info("STORE", "created default room 'Общий'");
    }

    // ==================================================================== sync

    private IResult Sync(HttpContext ctx)
    {
        return Authed(ctx, user =>
        {
            Dictionary<string, User> users = new(StringComparer.Ordinal);
            List<ConversationDto> conversations = new();

            foreach (Room room in _store.AllRooms().OrderBy(r => r.SortOrder).ThenBy(r => r.CreatedUtc))
            {
                if (room.Archived) continue;
                if (room.IsPrivate && !room.Members.Contains(user.Id)) continue;

                conversations.Add(new ConversationDto
                {
                    Id = room.Id,
                    Kind = ConversationKind.Room,
                    Title = room.Name,
                    Subtitle = room.Topic,
                    Emoji = room.Emoji,
                    IsPrivate = room.IsPrivate,
                    ReadOnly = room.ReadOnly,
                    Archived = room.Archived,
                    LastSeq = room.LastSeq,
                    Preview = room.LastMessagePreview,
                    LastUtc = room.LastMessageUtc,
                    Members = room.Members.Count,
                    MemberIds = room.Members.ToList(),
                    PinnedMessageId = room.PinnedMessageId,
                    LastReadSeq = LastReadOf(user, room.Id),
                    Unread = UnreadOf(user, room.Id, room.LastSeq)
                });
            }

            foreach (Direct dm in _store.AllDms())
            {
                if (dm.UserA != user.Id && dm.UserB != user.Id) continue;

                string peerId = dm.UserA == user.Id ? dm.UserB : dm.UserA;
                User peer = _store.GetUser(peerId);
                if (peer == null) continue;

                users[peer.Id] = Public(peer);

                conversations.Add(new ConversationDto
                {
                    Id = dm.Id,
                    Kind = ConversationKind.Dm,
                    Title = peer.DisplayName.Length > 0 ? peer.DisplayName : peer.Nick,
                    Subtitle = "",
                    AvatarBlob = peer.AvatarBlob,
                    PeerId = peer.Id,
                    LastSeq = dm.LastSeq,
                    Preview = dm.LastMessagePreview,
                    LastUtc = dm.LastMessageUtc,
                    Members = 2,
                    MemberIds = new List<string> { dm.UserA, dm.UserB },
                    PinnedMessageId = dm.PinnedMessageId,
                    LastReadSeq = LastReadOf(user, dm.Id),
                    Unread = UnreadOf(user, dm.Id, dm.LastSeq)
                });
            }

            // Every author of a room message the person will see, so names render
            // immediately instead of trickling in one request at a time.
            foreach (User u in _store.AllUsers())
            {
                if (u.Banned) continue;
                users[u.Id] = Public(u);
            }

            return Ok(new SyncDto
            {
                Conversations = conversations.OrderByDescending(c => c.LastUtc ?? DateTime.MinValue).ToList(),
                Users = users,
                Me = user,
                Online = _hub.OnlineUserIds()
            });
        });
    }

    private static long LastReadOf(User user, string conversationId)
    {
        Dictionary<string, long> map = Json.TryDeserialize<Dictionary<string, long>>(user.LastReadJson);
        return map != null && map.TryGetValue(conversationId, out long seq) ? seq : 0;
    }

    /// <summary>
    /// Advances the read cursor without a round trip from the client. Used when the
    /// server itself knows the person has seen something (they just wrote it).
    /// </summary>
    private void MarkReadUpTo(User user, string conversationId, long seq)
    {
        Dictionary<string, long> map = Json.TryDeserialize<Dictionary<string, long>>(user.LastReadJson) ?? new();
        long current = map.TryGetValue(conversationId, out long value) ? value : 0;
        if (seq <= current) return;

        map[conversationId] = seq;
        _store.UpdateUser(user.Id, u => u.LastReadJson = Json.Serialize(map));
    }

    /// <summary>
    /// How many messages arrived after the person last read. Computed from sequence
    /// numbers rather than by scanning the log: a family chat does not delete
    /// messages often enough for the difference to mislead, and this stays O(1) on
    /// a sync that already touches every conversation.
    /// </summary>
    private static int UnreadOf(User user, string conversationId, long lastSeq)
    {
        long lastRead = LastReadOf(user, conversationId);
        if (lastSeq <= lastRead) return 0;

        long delta = lastSeq - lastRead;

        // A room that was quiet for months would otherwise show a huge number; the
        // badge caps at 99 anyway, so clamping here keeps the JSON small.
        return delta > 999 ? 999 : (int)delta;
    }

    // ================================================================ messages

    private IResult Page(HttpContext ctx)
    {
        return Authed(ctx, user =>
        {
            string conversationId = ctx.Request.Query["conversationId"].ToString();
            long beforeSeq = ParseLong(ctx.Request.Query["beforeSeq"]);
            int limit = Math.Clamp(ParseInt(ctx.Request.Query["limit"], _cfg.MessagesPageSize), 1, 200);

            Require(!string.IsNullOrEmpty(conversationId), 400, "conversation", "Не указан диалог");
            Require(_hub.CanAccess(user.Id, conversationId), 403, "forbidden", "Нет доступа к диалогу");

            MessageLog log = _store.Messages(conversationId);
            List<Message> messages = log.Page(beforeSeq, limit);

            HashSet<string> authorIds = new(StringComparer.Ordinal);
            foreach (Message m in messages) authorIds.Add(m.AuthorId);

            Dictionary<string, User> users = new(StringComparer.Ordinal);
            foreach (string id in authorIds)
            {
                User author = _store.GetUser(id);
                if (author != null) users[id] = Public(author);
            }

            bool hasMore = messages.Count == limit && log.Count > limit;

            return Ok(new MessagePage { Messages = messages, Users = users, HasMore = hasMore });
        });
    }

    private async Task<IResult> SendAsync(HttpContext ctx, string conversationId)
    {
        AuthResult auth = Authenticate(ctx);
        if (auth == null) return Unauthorized();
        User user = auth.User;

        try
        {
            if (!_limiter.Allow("send:" + user.Id, _cfg.MessagesPerMin, TimeSpan.FromMinutes(1)))
                return Fail(429, "slow-down", "Слишком часто. Секунду");

            if (!_hub.CanAccess(user.Id, conversationId))
                return Fail(403, "forbidden", "Нет доступа к диалогу");

            Room room = _store.GetRoom(conversationId);
            if (room != null)
            {
                if (room.ReadOnly && !CanModerate(user, room) && room.CreatedBy != user.Id)
                    return Fail(403, "read-only", "Комната только для чтения");
            }

            SendMessageRequest req = JsonSerializer.Deserialize<SendMessageRequest>(ReadBody(ctx), Json.Options);
            if (req == null) return Fail(400, "bad-json", "Пустой запрос");

            string text = (req.Text ?? "").Trim();
            Require(text.Length <= _cfg.MaxMessageLen, 400, "too-long", $"Сообщение длиннее {_cfg.MaxMessageLen} символов");

            MessageKind kind = req.Type;
            Attachment attachment = null;

            if (!string.IsNullOrEmpty(req.AttachmentId))
            {
                BlobInfo blob = _blobs.Get(req.AttachmentId);
                Require(blob != null, 404, "no-blob", "Файл не найден");

                attachment = new Attachment
                {
                    BlobId = blob.Id,
                    Name = blob.Name,
                    Mime = blob.Mime,
                    Size = blob.Size,
                    Width = blob.Width,
                    Height = blob.Height,
                    DurationSec = blob.DurationSec
                };

                if (kind == MessageKind.Text)
                    kind = blob.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? MessageKind.Image
                         : blob.Mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? MessageKind.Voice
                         : MessageKind.File;
            }

            Require(text.Length > 0 || attachment != null, 400, "empty", "Пустое сообщение");

            MessageLog log = _store.Messages(conversationId);

            // Idempotency: a retried send with the same client id returns the original.
            if (!string.IsNullOrEmpty(req.ClientId))
            {
                Message existing = log.FindByClientId(req.ClientId);
                if (existing != null) return Ok(existing);
            }

            Message message = new()
            {
                Id = "m" + Crypto.NewId(9),
                ConversationId = conversationId,
                Kind = conversationId.StartsWith("dm:", StringComparison.Ordinal) ? ConversationKind.Dm : ConversationKind.Room,
                AuthorId = user.Id,
                CreatedUtc = DateTime.UtcNow,
                Text = text,
                Type = kind,
                Attachment = attachment,
                ClientId = req.ClientId ?? ""
            };

            if (_cfg.FeaturesReplies && !string.IsNullOrEmpty(req.ReplyToId))
            {
                Message quoted = log.Find(req.ReplyToId);
                if (quoted != null)
                {
                    message.ReplyToId = quoted.Id;
                    User quotedAuthor = _store.GetUser(quoted.AuthorId);
                    message.ReplyToAuthor = quotedAuthor?.DisplayName ?? "кто-то";
                    message.ReplyToPreview = KroshkaStore.BuildPreview(quoted);
                }
            }

            log.Append(message);
            _store.TouchConversation(conversationId, message);

            // Sending is reading: without this the author's own message would show up
            // as unread on their next sync, which is both wrong and confusing.
            MarkReadUpTo(user, conversationId, message.Seq);

            WsFrame frame = WsFrame.Make(Protocol.Events.Message) with
            {
                ConversationId = conversationId,
                Id = message.Id,
                Seq = message.Seq,
                Data = message
            };

            _hub.BroadcastToConversation(conversationId, frame);

            Log.Api("MSG", $"{user.Nick} -> {conversationId} seq={message.Seq} type={kind}");
            return Ok(message);
        }
        catch (ApiException ex)
        {
            return Fail(ex.Status, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("API", ex);
            return Fail(500, "internal", "Не удалось отправить сообщение");
        }
    }

    private IResult Edit(HttpContext ctx, string id)
    {
        return Authed(ctx, user =>
        {
            Require(_cfg.FeaturesEdit, 403, "disabled", "Редактирование отключено");

            Message message = FindMessage(id);
            Require(message != null, 404, "not-found", "Сообщение не найдено");
            Require(message.AuthorId == user.Id, 403, "forbidden", "Можно править только свои сообщения");
            Require(message.Type != MessageKind.System, 403, "forbidden", "Системные сообщения не редактируются");

            if (_cfg.EditWindowMin > 0 &&
                (DateTime.UtcNow - message.CreatedUtc).TotalMinutes > _cfg.EditWindowMin)
                return Fail(403, "window", "Время на редактирование истекло");

            EditMessageRequest req = JsonSerializer.Deserialize<EditMessageRequest>(ReadBody(ctx), Json.Options);
            string text = (req?.Text ?? "").Trim();

            Require(text.Length > 0, 400, "empty", "Пустой текст");
            Require(text.Length <= _cfg.MaxMessageLen, 400, "too-long", "Слишком длинное сообщение");

            MessageLog log = _store.Messages(message.ConversationId);
            message.Text = text;
            message.EditedUtc = DateTime.UtcNow;
            log.Update(message);
            _store.TouchConversation(message.ConversationId, message);

            _hub.BroadcastToConversation(message.ConversationId, WsFrame.Make(Protocol.Events.MessageEdited) with
            {
                ConversationId = message.ConversationId,
                Id = message.Id,
                Seq = message.Seq,
                Data = message
            });

            return Ok(message);
        });
    }

    private IResult Delete(HttpContext ctx, string id)
    {
        return Authed(ctx, user =>
        {
            Require(_cfg.FeaturesDelete, 403, "disabled", "Удаление отключено");

            Message message = FindMessage(id);
            Require(message != null, 404, "not-found", "Сообщение не найдено");

            Room room = _store.GetRoom(message.ConversationId);
            bool canModerate = room != null && CanModerate(user, room);
            bool isAuthor = message.AuthorId == user.Id;

            Require(isAuthor || canModerate, 403, "forbidden", "Нельзя удалить чужое сообщение");

            if (isAuthor && !canModerate && _cfg.DeleteWindowMin > 0 &&
                (DateTime.UtcNow - message.CreatedUtc).TotalMinutes > _cfg.DeleteWindowMin)
                return Fail(403, "window", "Время на удаление истекло");

            MessageLog log = _store.Messages(message.ConversationId);
            message.Deleted = true;
            message.DeletedBy = user.Id;
            message.Text = "";
            message.Attachment = null;
            message.Reactions.Clear();
            log.Update(message);
            _store.TouchConversation(message.ConversationId, message);

            _hub.BroadcastToConversation(message.ConversationId, WsFrame.Make(Protocol.Events.MessageDeleted) with
            {
                ConversationId = message.ConversationId,
                Id = message.Id,
                Seq = message.Seq
            });

            return Ok();
        });
    }

    private IResult React(HttpContext ctx, string id)
    {
        return Authed(ctx, user =>
        {
            Require(_cfg.FeaturesReactions, 403, "disabled", "Реакции отключены");

            Message message = FindMessage(id);
            Require(message != null, 404, "not-found", "Сообщение не найдено");
            Require(_hub.CanAccess(user.Id, message.ConversationId), 403, "forbidden", "Нет доступа");

            ReactionRequest req = JsonSerializer.Deserialize<ReactionRequest>(ReadBody(ctx), Json.Options);
            string emoji = (req?.Emoji ?? "").Trim();

            Require(emoji.Length is > 0 and <= 24, 400, "emoji", "Некорректная реакция");

            message.Reactions ??= new Dictionary<string, List<string>>();

            if (message.Reactions.TryGetValue(emoji, out List<string> who) && who.Contains(user.Id))
            {
                who.Remove(user.Id);
                if (who.Count == 0) message.Reactions.Remove(emoji);
            }
            else
            {
                if (!message.Reactions.TryGetValue(emoji, out who))
                    message.Reactions[emoji] = who = new List<string>();
                who.Add(user.Id);
            }

            MessageLog log = _store.Messages(message.ConversationId);
            log.Update(message);

            _hub.BroadcastToConversation(message.ConversationId, WsFrame.Make(Protocol.Events.Reaction) with
            {
                ConversationId = message.ConversationId,
                Id = message.Id,
                Seq = message.Seq,
                Data = message.Reactions
            });

            return Ok(message.Reactions);
        });
    }

    private IResult Pin(HttpContext ctx, string id)
    {
        return Authed(ctx, user =>
        {
            Require(_cfg.FeaturesPins, 403, "disabled", "Закрепление отключено");

            Message message = FindMessage(id);
            Require(message != null, 404, "not-found", "Сообщение не найдено");

            Room room = _store.GetRoom(message.ConversationId);
            bool canModerate = room != null && CanModerate(user, room);
            Require(canModerate || room == null, 403, "forbidden", "Недостаточно прав");

            string newPin = "";

            if (room != null)
            {
                newPin = room.PinnedMessageId == id ? "" : id;
                room.PinnedMessageId = newPin;
                _store.SaveRoom(room);
            }
            else
            {
                Direct dm = _store.GetDm(message.ConversationId);
                Require(dm != null, 404, "not-found", "Диалог не найден");
                newPin = dm.PinnedMessageId == id ? "" : id;
                dm.PinnedMessageId = newPin;
                _store.SaveDm(dm);
            }

            _hub.BroadcastToConversation(message.ConversationId, WsFrame.Make(Protocol.Events.Pinned) with
            {
                ConversationId = message.ConversationId,
                Id = newPin
            });

            return Ok(new { pinnedMessageId = newPin });
        });
    }

    private IResult Read(HttpContext ctx)
    {
        return Authed(ctx, user =>
        {
            ReadRequest req = JsonSerializer.Deserialize<ReadRequest>(ReadBody(ctx), Json.Options);
            Require(req != null && !string.IsNullOrEmpty(req.ConversationId), 400, "conversation", "Не указан диалог");
            Require(_hub.CanAccess(user.Id, req.ConversationId), 403, "forbidden", "Нет доступа");

            MarkReadUpTo(user, req.ConversationId, req.Seq);

            long applied = LastReadOf(_store.GetUser(user.Id), req.ConversationId);

            _hub.BroadcastToConversation(req.ConversationId, WsFrame.Make(Protocol.Events.Read) with
            {
                ConversationId = req.ConversationId,
                UserId = user.Id,
                Seq = applied
            });

            return Ok();
        });
    }

    // =================================================================== rooms

    private IResult Rooms(HttpContext ctx)
    {
        return Authed(ctx, user =>
        {
            List<object> list = new();
            foreach (Room room in _store.AllRooms())
            {
                if (room.Archived) continue;
                if (room.IsPrivate && !room.Members.Contains(user.Id)) continue;

                list.Add(new
                {
                    id = room.Id,
                    name = room.Name,
                    topic = room.Topic,
                    emoji = room.Emoji,
                    isPrivate = room.IsPrivate,
                    readOnly = room.ReadOnly,
                    createdBy = room.CreatedBy,
                    members = room.Members.Count,
                    lastSeq = room.LastSeq,
                    lastUtc = room.LastMessageUtc
                });
            }
            return Ok(new { rooms = list });
        });
    }

    private IResult CreateRoom(HttpContext ctx)
    {
        return Authed(ctx, user =>
        {
            Require(_cfg.FeaturesRooms, 403, "disabled", "Комнаты отключены");
            Require(user.Role != UserRole.Member || _cfg.FeaturesRooms, 403, "forbidden", "Нет прав");

            CreateRoomRequest req = JsonSerializer.Deserialize<CreateRoomRequest>(ReadBody(ctx), Json.Options);
            Require(req != null, 400, "bad-json", "Пустой запрос");

            string name = (req.Name ?? "").Trim();
            Require(name.Length is >= 1 and <= 64, 400, "name", "Название от 1 до 64 символов");

            Room room = _store.CreateRoom(name, req.Topic ?? "", req.Emoji ?? "", req.IsPrivate, user.Id, req.Members);

            WsFrame frame = WsFrame.Make(Protocol.Events.RoomCreated) with { ConversationId = room.Id, Data = room };

            if (room.IsPrivate)
            {
                foreach (string member in room.Members)
                {
                    _hub.SubscribeUser(member, room.Id);
                    _hub.SendToUser(member, frame);
                }
            }
            else
            {
                // A public room is visible to everyone who is already connected, so
                // every live socket must subscribe to it. Without this the room shows
                // up in the sidebar but its messages never arrive until a reconnect.
                _hub.SubscribeAll(room.Id);
                _hub.BroadcastAll(frame);
            }

            Log.Info("ROOM", $"{user.Nick} created '{room.Name}' private={room.IsPrivate}");
            return Ok(room);
        });
    }

    private IResult UpdateRoom(HttpContext ctx, string id)
    {
        return Authed(ctx, user =>
        {
            Room room = _store.GetRoom(id);
            Require(room != null, 404, "not-found", "Комната не найдена");
            Require(CanModerate(user, room), 403, "forbidden", "Нужны права модератора");

            UpdateRoomRequest req = JsonSerializer.Deserialize<UpdateRoomRequest>(ReadBody(ctx), Json.Options);
            Require(req != null, 400, "bad-json", "Пустой запрос");

            if (req.Name != null) room.Name = req.Name.Trim();
            if (req.Topic != null) room.Topic = req.Topic.Trim();
            if (req.Emoji != null) room.Emoji = req.Emoji;
            if (req.ReadOnly.HasValue) room.ReadOnly = req.ReadOnly.Value;
            if (req.Archived.HasValue) room.Archived = req.Archived.Value;

            if (req.Members != null)
            {
                room.Members = req.Members.Where(m => _store.GetUser(m) != null).Distinct().ToList();
                foreach (string member in room.Members) _hub.SubscribeUser(member, room.Id);
            }

            _store.SaveRoom(room);

            _hub.BroadcastToConversation(room.Id, WsFrame.Make(Protocol.Events.RoomUpdated) with
            {
                ConversationId = room.Id,
                Data = room
            });

            return Ok(room);
        });
    }

    private IResult DeleteRoom(HttpContext ctx, string id)
    {
        return Authed(ctx, user =>
        {
            Room room = _store.GetRoom(id);
            Require(room != null, 404, "not-found", "Комната не найдена");
            Require(CanModerate(user, room), 403, "forbidden", "Нужны права модератора");

            _hub.BroadcastToConversation(id, WsFrame.Make(Protocol.Events.RoomDeleted) with { ConversationId = id });
            _hub.ForgetConversation(id);
            _store.DeleteRoom(id);

            Log.Info("ROOM", $"{user.Nick} deleted room '{room.Name}'");
            return Ok();
        });
    }

    private IResult OpenDm(HttpContext ctx, string userId)
    {
        return Authed(ctx, user =>
        {
            Require(_cfg.FeaturesDm, 403, "disabled", "Личные сообщения отключены");

            User peer = _store.GetUser(userId);
            Require(peer != null, 404, "not-found", "Пользователь не найден");
            Require(peer.Id != user.Id, 400, "self", "Нельзя открыть личку с собой");

            Direct dm = _store.GetOrCreateDm(user.Id, peer.Id);

            WsFrame frame = WsFrame.Make(Protocol.Events.DmCreated) with { ConversationId = dm.Id, Data = dm };
            _hub.SubscribeUser(user.Id, dm.Id);
            _hub.SubscribeUser(peer.Id, dm.Id);
            _hub.SendToUser(user.Id, frame);
            _hub.SendToUser(peer.Id, frame);

            return Ok(dm);
        });
    }

    // =================================================================== users

    private IResult Users(HttpContext ctx)
    {
        return Authed(ctx, _ =>
        {
            List<object> list = _store.AllUsers()
                .Where(u => !u.Banned)
                .OrderBy(u => u.DisplayName)
                .Select(u => (object)new
                {
                    id = u.Id,
                    nick = u.Nick,
                    displayName = u.DisplayName,
                    avatarBlob = u.AvatarBlob,
                    accent = u.Accent,
                    role = u.Role.ToString().ToLowerInvariant(),
                    bio = u.Bio,
                    online = _hub.IsOnline(u.Id),
                    lastSeenUtc = u.LastSeenUtc
                })
                .ToList();

            return Ok(new { users = list });
        });
    }

    private IResult UserById(HttpContext ctx, string id)
    {
        return Authed(ctx, _ =>
        {
            User u = _store.GetUser(id);
            Require(u != null, 404, "not-found", "Пользователь не найден");
            return Ok(Public(u));
        });
    }

    private IResult UpdateProfile(HttpContext ctx)
    {
        return Authed(ctx, user =>
        {
            UpdateProfileRequest req = JsonSerializer.Deserialize<UpdateProfileRequest>(ReadBody(ctx), Json.Options);
            Require(req != null, 400, "bad-json", "Пустой запрос");

            _store.UpdateUser(user.Id, u =>
            {
                if (req.DisplayName != null)
                {
                    string dn = req.DisplayName.Trim();
                    if (dn.Length is >= 1 and <= 64) u.DisplayName = dn;
                }
                if (req.Bio != null) u.Bio = req.Bio.Trim().Length > 300 ? req.Bio.Trim()[..300] : req.Bio.Trim();
                if (req.Accent != null) u.Accent = KroshkaConfig.IsHexColor(req.Accent) ? req.Accent : "";
                if (req.Theme != null) u.Theme = req.Theme is "light" or "dark" or "oled" ? req.Theme : "";
                if (req.Sound.HasValue) u.Sound = req.Sound.Value;
                if (req.Notifications.HasValue) u.Notifications = req.Notifications.Value;
            });

            _hub.BroadcastAll(WsFrame.Make(Protocol.Events.UserUpdated) with
            {
                UserId = user.Id,
                Data = Public(_store.GetUser(user.Id))
            });

            return Ok(_store.GetUser(user.Id));
        });
    }

    private async Task<IResult> UploadAvatarAsync(HttpContext ctx)
    {
        AuthResult auth = Authenticate(ctx);
        if (auth == null) return Unauthorized();

        try
        {
            if (!ctx.Request.HasFormContentType) return Fail(400, "no-file", "Нужен файл");

            IFormCollection form = await ctx.Request.ReadFormAsync();
            IFormFile file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file == null || file.Length == 0) return Fail(400, "no-file", "Файл не получен");

            if (file.Length > _cfg.MaxAvatarKb * 1024L)
                return Fail(413, "too-big", $"Аватар больше {_cfg.MaxAvatarKb} КБ");

            byte[] data = new byte[file.Length];
            using (Stream stream = file.OpenReadStream())
                await stream.ReadExactlyAsync(data);

            BlobInfo blob = _blobs.Put(data, file.FileName, file.ContentType, auth.User.Id);
            _store.UpdateUser(auth.User.Id, u => u.AvatarBlob = blob.Id);

            _hub.BroadcastAll(WsFrame.Make(Protocol.Events.UserUpdated) with
            {
                UserId = auth.User.Id,
                Data = Public(_store.GetUser(auth.User.Id))
            });

            return Ok(new { avatarBlob = blob.Id });
        }
        catch (Exception ex)
        {
            Log.Error("API", ex);
            return Fail(500, "internal", "Не удалось сохранить аватар");
        }
    }

    private IResult Sessions(HttpContext ctx)
    {
        return Authed(ctx, user =>
        {
            List<object> list = _store.SessionsOf(user.Id)
                .Where(s => s.ExpiresUtc > DateTime.UtcNow)
                .Select(s => (object)new
                {
                    id = s.Id,
                    device = s.Device,
                    ip = s.Ip,
                    createdUtc = s.CreatedUtc,
                    lastUsedUtc = s.LastUsedUtc,
                    expiresUtc = s.ExpiresUtc,
                    remember = s.Remember
                })
                .ToList();

            return Ok(new { sessions = list });
        });
    }

    private IResult RevokeSession(HttpContext ctx, string id)
    {
        return Authed(ctx, user =>
        {
            List<Session> mine = _store.SessionsOf(user.Id);
            Session target = mine.FirstOrDefault(s => s.Id == id);
            Require(target != null, 404, "not-found", "Сессия не найдена");
            _store.DeleteSession(id);
            return Ok();
        });
    }

    // ================================================================== search

    private IResult Search(HttpContext ctx)
    {
        return Authed(ctx, user =>
        {
            Require(_cfg.FeaturesSearch, 403, "disabled", "Поиск отключён");

            string query = (ctx.Request.Query["q"].ToString() ?? "").Trim();
            string scope = ctx.Request.Query["conversationId"].ToString();

            Require(query.Length >= 2, 400, "query", "Запрос слишком короткий");

            if (!_limiter.Allow("search:" + user.Id, 30, TimeSpan.FromMinutes(1)))
                return Fail(429, "slow-down", "Слишком много запросов поиска");

            List<Message> hits = new();
            List<string> scopeIds = string.IsNullOrEmpty(scope)
                ? _hub.AccessibleConversations(user.Id).ToList()
                : new List<string> { scope };

            foreach (string conversationId in scopeIds)
            {
                if (!_hub.CanAccess(user.Id, conversationId)) continue;
                if (hits.Count >= 100) break;
                hits.AddRange(_store.Messages(conversationId).Search(query, 40));
            }

            Dictionary<string, string> titles = new(StringComparer.Ordinal);
            foreach (string id in hits.Select(h => h.ConversationId).Distinct())
            {
                Room room = _store.GetRoom(id);
                if (room != null) { titles[id] = room.Name; continue; }
                Direct dm = _store.GetDm(id);
                if (dm != null)
                {
                    string peerId = dm.UserA == user.Id ? dm.UserB : dm.UserA;
                    titles[id] = _store.GetUser(peerId)?.DisplayName ?? "Личка";
                }
            }

            Dictionary<string, User> authors = new(StringComparer.Ordinal);
            foreach (string id in hits.Select(h => h.AuthorId).Distinct())
            {
                User author = _store.GetUser(id);
                if (author != null) authors[id] = Public(author);
            }

            return Ok(new { query, total = hits.Count, messages = hits, titles, users = authors });
        });
    }

    // ================================================================== upload

    private async Task<IResult> UploadAsync(HttpContext ctx)
    {
        AuthResult auth = Authenticate(ctx);
        if (auth == null) return Unauthorized();
        User user = auth.User;

        try
        {
            Require(_cfg.FeaturesFiles, 403, "disabled", "Загрузка файлов отключена");

            if (!_limiter.Allow("upload:" + user.Id, _cfg.UploadsPerMin, TimeSpan.FromMinutes(1)))
                return Fail(429, "slow-down", "Слишком много загрузок");

            if (!ctx.Request.HasFormContentType) return Fail(400, "no-file", "Нужен multipart/form-data");

            IFormCollection form = await ctx.Request.ReadFormAsync();
            IFormFile file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            Require(file != null && file.Length > 0, 400, "no-file", "Файл не получен");

            long max = _cfg.MaxUploadMb * 1024L * 1024L;
            Require(file.Length <= max, 413, "too-big", $"Файл больше {_cfg.MaxUploadMb} МБ");

            byte[] data = new byte[file.Length];
            using (Stream stream = file.OpenReadStream())
                await stream.ReadExactlyAsync(data);

            BlobInfo blob = _blobs.Put(data, file.FileName, file.ContentType, user.Id);

            double duration = ParseDouble(form["duration"]);
            if (duration > 0)
            {
                blob.DurationSec = Math.Min(duration, 3600);
                FileStore.WriteAtomic(Path.Combine(_blobs.Root, blob.Id[..2], blob.Id + ".json"), blob);
            }

            Log.Api("BLOB", $"{user.Nick} uploaded {blob.Name} ({blob.Size} bytes)");

            return Ok(new
            {
                id = blob.Id,
                name = blob.Name,
                mime = blob.Mime,
                size = blob.Size,
                width = blob.Width,
                height = blob.Height,
                durationSec = blob.DurationSec,
                url = "/api/blob/" + blob.Id
            });
        }
        catch (ApiException ex)
        {
            return Fail(ex.Status, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("API", ex);
            return Fail(500, "internal", "Не удалось загрузить файл");
        }
    }

    private IResult Blob(HttpContext ctx, string id)
    {
        AuthResult auth = Authenticate(ctx);
        if (auth == null) return Unauthorized();

        BlobInfo info = _blobs.Get(id);
        if (info == null) return Results.NotFound();

        string path = _blobs.DataPath(info);
        if (!File.Exists(path)) return Results.NotFound();

        // Inline for media so <img>/<audio> can render it; download for everything else.
        bool inline = info.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                   || info.Mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                   || info.Mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                   || info.Mime == "application/pdf";

        string disposition = inline ? "inline" : "attachment";
        ctx.Response.Headers.ContentDisposition = $"{disposition}; filename*=UTF-8''{Uri.EscapeDataString(info.Name)}";
        ctx.Response.Headers.CacheControl = "private, max-age=31536000, immutable";

        return Results.File(path, info.Mime, enableRangeProcessing: true);
    }

    // ================================================================= helpers

    public static User Public(User user)
    {
        if (user == null) return null;
        // Copy without the password material so a profile object is always safe to send.
        return new User
        {
            Id = user.Id,
            Nick = user.Nick,
            NickLower = "",
            DisplayName = user.DisplayName,
            Bio = user.Bio,
            AvatarBlob = user.AvatarBlob,
            Accent = user.Accent,
            Role = user.Role,
            Banned = user.Banned,
            CreatedUtc = user.CreatedUtc,
            LastSeenUtc = user.LastSeenUtc,
            Theme = user.Theme,
            Sound = user.Sound,
            Notifications = user.Notifications
        };
    }

    private BootstrapDto BuildBootstrap()
    {
        return new BootstrapDto
        {
            ServerName = _cfg.SiteName,
            ServerShort = _cfg.SiteShort,
            Tagline = _cfg.SiteTagline,
            Motd = _cfg.Motd,
            Footer = _cfg.Footer,
            SupportEmail = _cfg.SupportEmail,
            AuthMode = _cfg.AuthMode.ToString().ToLowerInvariant(),
            AllowRegister = _cfg.AllowRegister,
            MaxNickLen = _cfg.MaxNickLen,
            Theme = _cfg.DefaultTheme,
            DefaultSound = _cfg.DefaultSound,
            Language = _cfg.Language,
            UserCount = _store.UserCount,
            FirstUserAdmin = _cfg.FirstUserAdmin,
            ServerTime = DateTime.UtcNow.ToString("O"),
            LogoUrl = string.IsNullOrEmpty(_cfg.BrandLogo) ? "" : _cfg.BrandLogo,
            MaxUploadMb = _cfg.MaxUploadMb,
            MaxMessageLen = _cfg.MaxMessageLen,
            MaxVoiceSec = _cfg.MaxVoiceSec,
            PageSize = _cfg.MessagesPageSize,
            Brand = new BrandDto
            {
                Accent = _cfg.Accent,
                AccentHi = _cfg.AccentHi,
                AccentLo = _cfg.AccentLo,
                LightBg = _cfg.LightBg,
                LightPanel = _cfg.LightPanel,
                LightPanel2 = _cfg.LightPanel2,
                LightText = _cfg.LightText,
                DarkBg = _cfg.DarkBg,
                DarkPanel = _cfg.DarkPanel,
                DarkPanel2 = _cfg.DarkPanel2,
                DarkText = _cfg.DarkText,
                OledBg = _cfg.OledBg,
                OledPanel = _cfg.OledPanel,
                OledPanel2 = _cfg.OledPanel2,
                OledText = _cfg.OledText
            },
            Features = new FeatureDto
            {
                Reactions = _cfg.FeaturesReactions,
                Replies = _cfg.FeaturesReplies,
                Pins = _cfg.FeaturesPins,
                Voice = _cfg.FeaturesVoice,
                Files = _cfg.FeaturesFiles,
                Search = _cfg.FeaturesSearch,
                Typing = _cfg.FeaturesTyping,
                Presence = _cfg.FeaturesPresence,
                Edit = _cfg.FeaturesEdit,
                Delete = _cfg.FeaturesDelete,
                Rooms = _cfg.FeaturesRooms,
                Dm = _cfg.FeaturesDm,
                Calls = _cfg.CallsEnabled
            }
        };
    }

    private Message FindMessage(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        foreach (Room room in _store.AllRooms())
        {
            Message m = _store.Messages(room.Id).Find(id);
            if (m != null) return m;
        }

        foreach (Direct dm in _store.AllDms())
        {
            Message m = _store.Messages(dm.Id).Find(id);
            if (m != null) return m;
        }

        return null;
    }

    private static bool CanModerate(User user, Room room)
    {
        if (user.Role is UserRole.Admin or UserRole.Owner) return true;
        if (room.CreatedBy == user.Id) return true;
        return room.Moderators.Contains(user.Id);
    }

    private static string ReadBody(HttpContext ctx)
    {
        // BufferJsonBody already consumed the stream; reading it again would block.
        if (ctx.Items.TryGetValue(App.BufferedBodyKey, out object buffered) && buffered is string text)
            return text;

        using StreamReader reader = new(ctx.Request.Body, System.Text.Encoding.UTF8, false, 1024, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static long ParseLong(string value) => long.TryParse(value, out long n) ? n : 0;
    private static int ParseInt(string value, int fallback) => int.TryParse(value, out int n) ? n : fallback;
    private static double ParseDouble(string value) => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n) ? n : 0;

    public static string ClientIp(HttpContext ctx)
    {
        string forwarded = ctx.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrEmpty(forwarded))
        {
            int comma = forwarded.IndexOf(',');
            return (comma > 0 ? forwarded[..comma] : forwarded).Trim();
        }
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private sealed class ApiException : Exception
    {
        public int Status { get; }
        public string Code { get; }

        public ApiException(int status, string code, string message) : base(message)
        {
            Status = status;
            Code = code;
        }
    }
}

/// <summary>
/// Token bucket per key. Deliberately simple: a family chat needs "stop the spammer"
/// not a distributed quota service.
/// </summary>
public sealed class RateLimiter
{
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    private sealed class Bucket
    {
        public DateTime WindowStart;
        public int Count;
    }

    public bool Allow(string key, int limit, TimeSpan window)
    {
        if (limit <= 0) return true;

        lock (_sync)
        {
            DateTime now = DateTime.UtcNow;

            if (!_buckets.TryGetValue(key, out Bucket bucket))
            {
                _buckets[key] = new Bucket { WindowStart = now, Count = 1 };
                return true;
            }

            if (now - bucket.WindowStart >= window)
            {
                bucket.WindowStart = now;
                bucket.Count = 1;
                return true;
            }

            if (bucket.Count >= limit) return false;

            bucket.Count++;
            return true;
        }
    }

    /// <summary>Drops expired buckets so the dictionary cannot grow forever.</summary>
    public void Sweep(TimeSpan window)
    {
        lock (_sync)
        {
            DateTime now = DateTime.UtcNow;
            List<string> dead = _buckets.Where(kv => now - kv.Value.WindowStart > window).Select(kv => kv.Key).ToList();
            foreach (string key in dead) _buckets.Remove(key);
        }
    }
}
