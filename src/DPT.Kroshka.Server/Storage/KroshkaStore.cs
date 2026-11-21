using DPT.Kroshka.Shared;

namespace DPT.Kroshka.Server.Storage;

/// <summary>
/// Everything the server knows, kept in folders on disk.
///
///   data/users/&lt;id&gt;/user.json
///   data/rooms/&lt;id&gt;/room.json + msg/YYYY-MM.ndjson
///   data/dm/&lt;id&gt;/dm.json     + msg/YYYY-MM.ndjson
///   data/sessions.json, data/invites.json
///
/// One lock guards the index maps; the message logs carry their own locks so a long
/// history page never blocks a login.
/// </summary>
public sealed class KroshkaStore
{
    private readonly object _sync = new();
    private readonly KroshkaConfig _cfg;

    private readonly Dictionary<string, User> _users = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _nickIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Direct> _dms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MessageLog> _logs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _invites = new(StringComparer.Ordinal);

    public KroshkaConfig Config => _cfg;
    public string UsersDir => Path.Combine(_cfg.DataDirPath, "users");
    public string RoomsDir => Path.Combine(_cfg.DataDirPath, "rooms");
    public string DmDir => Path.Combine(_cfg.DataDirPath, "dm");
    private string SessionsPath => Path.Combine(_cfg.DataDirPath, "sessions.json");
    private string InvitesPath => Path.Combine(_cfg.DataDirPath, "invites.json");

    public KroshkaStore(KroshkaConfig cfg)
    {
        _cfg = cfg;
    }

    // ---------------------------------------------------------------- loading

    public void Load()
    {
        Directory.CreateDirectory(_cfg.DataDirPath);
        Directory.CreateDirectory(UsersDir);
        Directory.CreateDirectory(RoomsDir);
        Directory.CreateDirectory(DmDir);
        Directory.CreateDirectory(_cfg.BlobDirPath);
        Directory.CreateDirectory(_cfg.BackupDirPath);

        LoadUsers();
        LoadRooms();
        LoadDms();
        LoadSessions();
        LoadInvites();

        Log.Info("STORE", $"loaded {_users.Count} users, {_rooms.Count} rooms, {_dms.Count} DMs, {_sessions.Count} sessions");
    }

    private void LoadUsers()
    {
        foreach (string dir in SafeDirs(UsersDir))
        {
            string path = Path.Combine(dir, "user.json");
            User user = FileStore.ReadJson<User>(path);
            if (user == null || string.IsNullOrEmpty(user.Id))
            {
                if (File.Exists(path)) FileStore.Quarantine(path);
                continue;
            }
            _users[user.Id] = user;
            _nickIndex[user.NickLower.Length > 0 ? user.NickLower : user.Nick.ToLowerInvariant()] = user.Id;
        }
    }

    private void LoadRooms()
    {
        foreach (string dir in SafeDirs(RoomsDir))
        {
            string path = Path.Combine(dir, "room.json");
            Room room = FileStore.ReadJson<Room>(path);
            if (room == null || string.IsNullOrEmpty(room.Id)) continue;
            _rooms[room.Id] = room;
        }
    }

    private void LoadDms()
    {
        foreach (string dir in SafeDirs(DmDir))
        {
            string path = Path.Combine(dir, "dm.json");
            Direct dm = FileStore.ReadJson<Direct>(path);
            if (dm == null || string.IsNullOrEmpty(dm.Id)) continue;
            _dms[dm.Id] = dm;
        }
    }

    private void LoadSessions()
    {
        List<Session> list = FileStore.ReadJson<List<Session>>(SessionsPath, new List<Session>());
        DateTime now = DateTime.UtcNow;
        foreach (Session s in list)
        {
            if (s.ExpiresUtc <= now) continue;
            if (!_users.ContainsKey(s.UserId)) continue;
            _sessions[s.Id] = s;
        }
    }

    private void LoadInvites()
    {
        Dictionary<string, List<string>> raw = FileStore.ReadJson<Dictionary<string, List<string>>>(InvitesPath, new());
        foreach (KeyValuePair<string, List<string>> kv in raw)
            _invites[kv.Key] = new HashSet<string>(kv.Value, StringComparer.Ordinal);
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch { return Array.Empty<string>(); }
    }

    // ------------------------------------------------------------------ users

    public User GetUser(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_sync) { return _users.TryGetValue(id, out User u) ? u : null; }
    }

    public User FindByNick(string nick)
    {
        if (string.IsNullOrWhiteSpace(nick)) return null;
        lock (_sync)
        {
            return _nickIndex.TryGetValue(nick.Trim(), out string id) && _users.TryGetValue(id, out User u) ? u : null;
        }
    }

    public List<User> AllUsers()
    {
        lock (_sync) { return _users.Values.ToList(); }
    }

    public int UserCount { get { lock (_sync) { return _users.Count; } } }

    public bool NickTaken(string nick)
    {
        lock (_sync) { return _nickIndex.ContainsKey(nick.Trim()); }
    }

    public User CreateUser(string nick, string displayName, UserRole role)
    {
        lock (_sync)
        {
            User user = new()
            {
                Id = "u" + Crypto.NewId(8),
                Nick = nick.Trim(),
                NickLower = nick.Trim().ToLowerInvariant(),
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? nick.Trim() : displayName.Trim(),
                Role = role,
                CreatedUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
                Sound = _cfg.DefaultSound,
                Theme = ""
            };

            _users[user.Id] = user;
            _nickIndex[user.NickLower] = user.Id;
            SaveUser(user);
            return user;
        }
    }

    public void SaveUser(User user)
    {
        string dir = Path.Combine(UsersDir, user.Id);
        FileStore.WriteAtomic(Path.Combine(dir, "user.json"), user);
    }

    public void UpdateUser(string id, Action<User> mutate)
    {
        lock (_sync)
        {
            if (!_users.TryGetValue(id, out User user)) return;

            string oldNick = user.NickLower;
            mutate(user);
            user.NickLower = user.Nick.ToLowerInvariant();

            if (!string.Equals(oldNick, user.NickLower, StringComparison.OrdinalIgnoreCase))
            {
                _nickIndex.Remove(oldNick);
                _nickIndex[user.NickLower] = user.Id;
            }

            SaveUser(user);
        }
    }

    public bool DeleteUser(string id)
    {
        lock (_sync)
        {
            if (!_users.TryGetValue(id, out User user)) return false;
            _users.Remove(id);
            _nickIndex.Remove(user.NickLower);

            foreach (Session s in _sessions.Values.Where(s => s.UserId == id).ToList())
                _sessions.Remove(s.Id);

            try { Directory.Delete(Path.Combine(UsersDir, id), true); } catch { }

            foreach (Room room in _rooms.Values)
            {
                if (room.Members.Remove(id)) SaveRoom(room);
            }

            SaveSessions();
            return true;
        }
    }

    // ------------------------------------------------------------------ rooms

    public List<Room> AllRooms()
    {
        lock (_sync) { return _rooms.Values.ToList(); }
    }

    public Room GetRoom(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_sync) { return _rooms.TryGetValue(id, out Room r) ? r : null; }
    }

    public Room CreateRoom(string name, string topic, string emoji, bool isPrivate, string createdBy, List<string> members)
    {
        lock (_sync)
        {
            Room room = new()
            {
                Id = "r" + Crypto.NewId(8),
                Name = name.Trim(),
                Topic = topic?.Trim() ?? "",
                Emoji = emoji ?? "",
                IsPrivate = isPrivate,
                CreatedBy = createdBy,
                CreatedUtc = DateTime.UtcNow,
                SortOrder = _rooms.Count
            };

            room.Members.Add(createdBy);
            foreach (string m in members ?? new List<string>())
                if (_users.ContainsKey(m) && !room.Members.Contains(m)) room.Members.Add(m);

            _rooms[room.Id] = room;
            SaveRoom(room);
            return room;
        }
    }

    public void SaveRoom(Room room)
    {
        FileStore.WriteAtomic(Path.Combine(RoomsDir, room.Id, "room.json"), room);
    }

    public bool DeleteRoom(string id)
    {
        lock (_sync)
        {
            if (!_rooms.Remove(id)) return false;
            _logs.Remove(id);
            try { Directory.Delete(Path.Combine(RoomsDir, id), true); } catch { }
            return true;
        }
    }

    // -------------------------------------------------------------------- dms

    public static string DmId(string a, string b)
    {
        return string.CompareOrdinal(a, b) <= 0 ? $"dm:{a}:{b}" : $"dm:{b}:{a}";
    }

    public List<Direct> AllDms()
    {
        lock (_sync) { return _dms.Values.ToList(); }
    }

    public Direct GetDm(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_sync) { return _dms.TryGetValue(id, out Direct d) ? d : null; }
    }

    public Direct GetOrCreateDm(string a, string b)
    {
        lock (_sync)
        {
            string id = DmId(a, b);
            if (_dms.TryGetValue(id, out Direct existing)) return existing;

            Direct dm = new()
            {
                Id = id,
                UserA = string.CompareOrdinal(a, b) <= 0 ? a : b,
                UserB = string.CompareOrdinal(a, b) <= 0 ? b : a,
                CreatedUtc = DateTime.UtcNow
            };

            _dms[id] = dm;
            FileStore.WriteAtomic(Path.Combine(DmDir, id.Replace(':', '_'), "dm.json"), dm);
            return dm;
        }
    }

    public void SaveDm(Direct dm)
    {
        FileStore.WriteAtomic(Path.Combine(DmDir, dm.Id.Replace(':', '_'), "dm.json"), dm);
    }

    // ------------------------------------------------------------- conversations

    /// <summary>Resolves "r..." / "dm:..." into the folder that holds its messages.</summary>
    public string ConversationDir(string conversationId)
    {
        if (conversationId.StartsWith("dm:", StringComparison.Ordinal))
            return Path.Combine(DmDir, conversationId.Replace(':', '_'), "msg");
        return Path.Combine(RoomsDir, conversationId, "msg");
    }

    public MessageLog Messages(string conversationId)
    {
        lock (_sync)
        {
            if (_logs.TryGetValue(conversationId, out MessageLog existing)) return existing;

            long hint = 0;
            if (conversationId.StartsWith("dm:", StringComparison.Ordinal))
            {
                if (_dms.TryGetValue(conversationId, out Direct dm)) hint = dm.LastSeq;
            }
            else if (_rooms.TryGetValue(conversationId, out Room room))
            {
                hint = room.LastSeq;
            }

            MessageLog log = new(ConversationDir(conversationId), hint);
            _logs[conversationId] = log;
            return log;
        }
    }

    /// <summary>Persists the conversation header after a new message bumps LastSeq.</summary>
    public void TouchConversation(string conversationId, Message message)
    {
        string preview = BuildPreview(message);

        if (conversationId.StartsWith("dm:", StringComparison.Ordinal))
        {
            Direct dm = GetDm(conversationId);
            if (dm == null) return;
            dm.LastSeq = message.Seq;
            dm.LastMessagePreview = preview;
            dm.LastMessageUtc = message.CreatedUtc;
            SaveDm(dm);
        }
        else
        {
            Room room = GetRoom(conversationId);
            if (room == null) return;
            room.LastSeq = message.Seq;
            room.LastMessagePreview = preview;
            room.LastMessageUtc = message.CreatedUtc;
            SaveRoom(room);
        }
    }

    public static string BuildPreview(Message message)
    {
        if (message.Deleted) return "";
        return message.Type switch
        {
            MessageKind.Image => "Фото",
            MessageKind.Voice => "Голосовое сообщение",
            MessageKind.File => string.IsNullOrEmpty(message.Attachment?.Name) ? "Файл" : message.Attachment.Name,
            MessageKind.System => message.Text ?? "",
            _ => Truncate(message.Text, 160)
        };
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string flat = text.Replace("\r", "").Replace("\n", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    // --------------------------------------------------------------- sessions

    /// <summary>
    /// Creates a session and returns it together with the raw device token. The raw
    /// token is handed to the caller once and only its SHA-256 is persisted, so a copy
    /// of data/sessions.json cannot be replayed as a login.
    /// </summary>
    public (Session session, string token) CreateSession(string userId, string device, string ip, bool remember)
    {
        lock (_sync)
        {
            string token = Crypto.NewToken();
            int days = remember ? _cfg.RememberDays : _cfg.SessionDays;

            Session session = new()
            {
                Id = "s" + Crypto.NewId(8),
                UserId = userId,
                TokenHash = Crypto.Sha256Hex(token),
                Device = device ?? "",
                Ip = ip ?? "",
                CreatedUtc = DateTime.UtcNow,
                LastUsedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddDays(days),
                Remember = remember
            };

            _sessions[session.Id] = session;
            SaveSessions();

            return (session, token);
        }
    }

    public Session FindSessionByToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        string hash = Crypto.Sha256Hex(token);
        lock (_sync)
        {
            foreach (Session s in _sessions.Values)
                if (Crypto.FixedEquals(s.TokenHash, hash)) return s;
            return null;
        }
    }

    public void TouchSession(Session session)
    {
        session.LastUsedUtc = DateTime.UtcNow;
    }

    public void DeleteSession(string sessionId)
    {
        lock (_sync)
        {
            if (_sessions.Remove(sessionId)) SaveSessions();
        }
    }

    public List<Session> SessionsOf(string userId)
    {
        lock (_sync) { return _sessions.Values.Where(s => s.UserId == userId).ToList(); }
    }

    public int SessionCount { get { lock (_sync) { return _sessions.Count; } } }

    public void SaveSessions()
    {
        lock (_sync)
        {
            List<Session> list = _sessions.Values.ToList();
            FileStore.WriteAtomic(SessionsPath, list);
        }
    }

    public int PruneSessions()
    {
        lock (_sync)
        {
            DateTime now = DateTime.UtcNow;
            List<string> dead = _sessions.Values
                .Where(s => s.ExpiresUtc <= now || !_users.ContainsKey(s.UserId))
                .Select(s => s.Id)
                .ToList();

            foreach (string id in dead) _sessions.Remove(id);
            if (dead.Count > 0) SaveSessions();
            return dead.Count;
        }
    }

    // ---------------------------------------------------------------- invites

    public void AddInvite(string code, string userId)
    {
        lock (_sync)
        {
            if (!_invites.TryGetValue(code, out HashSet<string> users))
                _invites[code] = users = new HashSet<string>(StringComparer.Ordinal);
            users.Add(userId);
            SaveInvites();
        }
    }

    public bool InviteUsedBy(string code, string userId)
    {
        lock (_sync)
        {
            return _invites.TryGetValue(code, out HashSet<string> users) && users.Contains(userId);
        }
    }

    private void SaveInvites()
    {
        lock (_sync)
        {
            Dictionary<string, List<string>> raw = _invites.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
            FileStore.WriteAtomic(InvitesPath, raw);
        }
    }

    // ---------------------------------------------------------------- recovery

    /// <summary>Drops cached logs so the next read re-parses from disk (after a compact).</summary>
    public void InvalidateLogs()
    {
        lock (_sync) { _logs.Clear(); }
    }

    public void SaveAll()
    {
        lock (_sync)
        {
            foreach (User u in _users.Values) SaveUser(u);
            foreach (Room r in _rooms.Values) SaveRoom(r);
            foreach (Direct d in _dms.Values) SaveDm(d);
            SaveSessions();
        }
    }
}
