using System.Globalization;
using System.Text;

namespace DPT.Kroshka.Shared;

/// <summary>
/// Hand written INI reader. No App.config, no XML, no NuGet: the file is parsed
/// line by line, unknown keys are ignored and bad values fall back to defaults so
/// a broken config never stops the server from starting.
/// </summary>
public sealed class Ini
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> Warnings { get; } = new();

    public static Ini Parse(string text)
    {
        Ini ini = new();
        if (string.IsNullOrEmpty(text)) return ini;

        string section = "root";
        ini._sections[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line[0] == ';' || line[0] == '#') continue;

            if (line[0] == '[')
            {
                int close = line.IndexOf(']');
                if (close < 2)
                {
                    ini.Warnings.Add("bad section header: " + line);
                    continue;
                }
                section = line[1..close].Trim();
                if (!ini._sections.ContainsKey(section))
                    ini._sections[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                ini.Warnings.Add("bad line: " + line);
                continue;
            }

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            value = StripInlineComment(value);
            value = Unquote(value);
            ini._sections[section][key] = value;
        }

        return ini;
    }

    private static string StripInlineComment(string value)
    {
        // A ';' or '#' starts a comment only when it follows whitespace, so values
        // such as "#0088FF" (a colour) and "abc#def" (a secret) survive untouched.
        for (int i = 1; i < value.Length; i++)
        {
            if (value[i] != ';' && value[i] != '#') continue;
            if (char.IsWhiteSpace(value[i - 1]))
                return value[..i].TrimEnd();
        }
        return value;
    }

    private static string Unquote(string value)
    {
        if (value.Length < 2) return value;
        char a = value[0], b = value[^1];
        if ((a == '"' && b == '"') || (a == '\'' && b == '\''))
            return value[1..^1];
        return value;
    }

    public bool Has(string section, string key)
    {
        return _sections.TryGetValue(section, out Dictionary<string, string> s) && s.ContainsKey(key);
    }

    public string Get(string section, string key, string fallback)
    {
        if (_sections.TryGetValue(section, out Dictionary<string, string> s) &&
            s.TryGetValue(key, out string value) && value.Length > 0)
            return value;
        return fallback;
    }

    public int GetInt(string section, string key, int fallback)
    {
        string v = Get(section, key, null);
        if (v == null) return fallback;
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : fallback;
    }

    public long GetLong(string section, string key, long fallback)
    {
        string v = Get(section, key, null);
        if (v == null) return fallback;
        return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : fallback;
    }

    public double GetDouble(string section, string key, double fallback)
    {
        string v = Get(section, key, null);
        if (v == null) return fallback;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : fallback;
    }

    public bool GetBool(string section, string key, bool fallback)
    {
        string v = Get(section, key, null);
        if (v == null) return fallback;
        return v.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" or "y" => true,
            "0" or "false" or "no" or "off" or "n" => false,
            _ => fallback
        };
    }

    public int[] GetIntList(string section, string key, int[] fallback)
    {
        string v = Get(section, key, null);
        if (v == null) return fallback;
        List<int> result = new();
        foreach (string part in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 && n <= 65535)
                result.Add(n);
        return result.Count > 0 ? result.ToArray() : fallback;
    }

    public string[] GetList(string section, string key, string[] fallback)
    {
        string v = Get(section, key, null);
        if (v == null) return fallback;
        string[] parts = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 ? parts : fallback;
    }

    public IEnumerable<string> SectionNames => _sections.Keys;
}

public enum AuthMode
{
    /// <summary>LAN default: pick a nickname and you are in. No password at all.</summary>
    Open = 0,
    /// <summary>Nickname plus one shared password for the household.</summary>
    Password = 1,
    /// <summary>Nickname plus an invite code handed out by the owner.</summary>
    Invite = 2
}

public enum LogLevel
{
    Error = 0,
    Warn = 1,
    Info = 2,
    Debug = 3,
    Verbose = 4
}

/// <summary>
/// Everything the messenger can be configured with. Every feature has a switch:
/// the point of Kroshka is that a family can shape it, not that it ships opinions.
/// </summary>
public sealed class KroshkaConfig
{
    // ------------------------------------------------------------------ paths
    public string BaseDir = ".";
    public string ConfigPath = "";
    public string WebRoot = "web";
    public string DataDir = "data";
    public string BlobDir = "data/blobs";
    public string BackupDir = "data/backups";
    public string LogDir = "logs";

    // ----------------------------------------------------------------- server
    public int Port = 6767;
    public int[] PortFallbacks = { 6768, 6769, 6770 };
    public string Bind = "0.0.0.0";
    public string PublicUrl = "";
    public int MaxUploadMb = 64;
    public int RequestTimeoutSec = 60;
    public bool TrustProxy = false;
    public int Backlog = 512;

    // ------------------------------------------------------------- discovery
    /// <summary>
    /// Answer LAN probes so clients can find this server without typing an IP.
    /// Off by default on a public host: announcing yourself to every device on
    /// the wire is not something a stranger should opt you into.
    /// </summary>
    public bool Discoverable = true;

    /// <summary>UDP port the probe travels on. Shares the number with TCP by design.</summary>
    public int DiscoveryPort = 6767;

    // ------------------------------------------------------------------- site
    public string SiteName = "Kroshka";
    public string SiteShort = "K";
    public string SiteTagline = "Мессенджер для своих";
    public string SiteDescription = "";
    public string Language = "ru";
    public string DefaultTheme = "light";
    public bool DefaultSound = true;
    public string Motd = "";
    public string Footer = "";
    public string SupportEmail = "";

    // --------------------------------------------------------------- branding
    public string BrandLogo = "";
    public string Accent = "#0088FF";
    public string AccentHi = "#0080FF";
    public string AccentLo = "#0066CC";
    public string LightBg = "#FFFFFF";
    public string LightPanel = "#F6F8FB";
    public string LightPanel2 = "#EDF1F7";
    public string LightText = "#0B1622";
    public string DarkBg = "#0A0F1A";
    public string DarkPanel = "#111A29";
    public string DarkPanel2 = "#16202F";
    public string DarkText = "#E6EDF7";
    public string OledBg = "#000000";
    public string OledPanel = "#05080D";
    public string OledPanel2 = "#0A1018";
    public string OledText = "#E8EEF6";

    // ------------------------------------------------------------------- auth
    public AuthMode AuthMode = AuthMode.Open;
    public bool AllowRegister = true;
    public string SharedPassword = "";
    public string InviteCode = "";
    public int RememberDays = 365;
    public int SessionDays = 30;
    public int MaxNickLen = 24;
    public bool AllowNickChange = true;
    public bool FirstUserAdmin = true;
    public int MaxFailedLoginsPerIp = 30;
    public int LoginLockSeconds = 120;

    // ----------------------------------------------------------------- limits
    public int MaxMessageLen = 8000;
    public int MessagesPerMin = 120;
    public int UploadsPerMin = 30;
    public int MaxAvatarKb = 4096;
    public int MaxVoiceSec = 300;
    public int MessagesPageSize = 50;

    // ---------------------------------------------------------------- storage
    public int KeepBackups = 10;
    public int PruneMonths = 0;
    public int AutoBackupHours = 24;

    // ------------------------------------------------------------------ admin
    public bool AdminEnabled = true;
    public int AdminPort = 6768;
    public int[] AdminPortFallbacks = { 6769, 6770, 6771 };
    public string AdminBind = "0.0.0.0";
    public string AdminPassword = "";
    public bool AdminLocalOnly = false;

    // -------------------------------------------------------------------- log
    public LogLevel LogLevel = LogLevel.Info;
    public int LogMaxFileMb = 16;
    public int LogRetentionDays = 30;
    public bool ApiTrace = true;
    public int LogRingSize = 2000;

    // --------------------------------------------------------------- features
    public bool FeaturesReactions = true;
    public bool FeaturesReplies = true;
    public bool FeaturesPins = true;
    public bool FeaturesVoice = true;
    public bool FeaturesFiles = true;
    public bool FeaturesSearch = true;
    public bool FeaturesTyping = true;
    public bool FeaturesPresence = true;
    public bool FeaturesEdit = true;
    public bool FeaturesDelete = true;
    public bool FeaturesRooms = true;
    public bool FeaturesDm = true;
    public int EditWindowMin = 0;
    public int DeleteWindowMin = 0;
    public bool CallsEnabled = false;

    public List<string> Warnings { get; } = new();

    public string WebRootPath => Path.GetFullPath(Path.Combine(BaseDir, WebRoot));
    public string DataDirPath => Path.GetFullPath(Path.Combine(BaseDir, DataDir));
    public string BlobDirPath => Path.GetFullPath(Path.Combine(BaseDir, BlobDir));
    public string BackupDirPath => Path.GetFullPath(Path.Combine(BaseDir, BackupDir));
    public string LogDirPath => Path.GetFullPath(Path.Combine(BaseDir, LogDir));
    public string OverridesPath => Path.Combine(DataDirPath, "overrides.json");

    public static KroshkaConfig Load(string baseDir, string configPath = null)
    {
        KroshkaConfig cfg = new() { BaseDir = Path.GetFullPath(baseDir) };

        if (string.IsNullOrEmpty(configPath))
        {
            string local = Path.Combine(cfg.BaseDir, "config.ini");
            string example = Path.Combine(cfg.BaseDir, "config.example.ini");
            if (File.Exists(local)) configPath = local;
            else if (File.Exists(example)) configPath = example;
        }

        Ini ini = new();
        if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
        {
            try
            {
                ini = Ini.Parse(File.ReadAllText(configPath, Encoding.UTF8));
                cfg.ConfigPath = Path.GetFullPath(configPath);
            }
            catch (Exception ex)
            {
                cfg.Warnings.Add("cannot read config " + configPath + ": " + ex.Message);
            }
        }
        else if (!string.IsNullOrEmpty(configPath))
        {
            cfg.Warnings.Add("config file not found: " + configPath);
        }

        cfg.Warnings.AddRange(ini.Warnings);
        cfg.Apply(ini);
        cfg.ApplyOverrides();
        cfg.Validate();
        return cfg;
    }

    private void Apply(Ini ini)
    {
        // ------------------------------------------------------------- server
        Port = ini.GetInt("server", "port", Port);
        PortFallbacks = ini.GetIntList("server", "portFallbacks", PortFallbacks);
        Bind = ini.Get("server", "bind", Bind);
        PublicUrl = ini.Get("server", "publicUrl", PublicUrl);
        MaxUploadMb = ini.GetInt("server", "maxUploadMb", MaxUploadMb);
        RequestTimeoutSec = ini.GetInt("server", "requestTimeoutSec", RequestTimeoutSec);
        TrustProxy = ini.GetBool("server", "trustProxy", TrustProxy);
        Backlog = ini.GetInt("server", "backlog", Backlog);

        // ---------------------------------------------------------- discovery
        Discoverable = ini.GetBool("server", "discoverable", Discoverable);
        DiscoveryPort = ini.GetInt("server", "discoveryPort", DiscoveryPort);

        // --------------------------------------------------------------- site
        SiteName = ini.Get("site", "name", SiteName);
        SiteShort = ini.Get("site", "shortName", SiteShort);
        SiteTagline = ini.Get("site", "tagline", SiteTagline);
        SiteDescription = ini.Get("site", "description", SiteDescription);
        Language = ini.Get("site", "language", Language);
        DefaultTheme = ini.Get("site", "theme", DefaultTheme);
        DefaultSound = ini.GetBool("site", "defaultSound", DefaultSound);
        Motd = ini.Get("site", "motd", Motd);
        Footer = ini.Get("site", "footer", Footer);
        SupportEmail = ini.Get("site", "supportEmail", SupportEmail);

        // ----------------------------------------------------------- branding
        BrandLogo = ini.Get("branding", "logo", BrandLogo);
        Accent = ini.Get("branding", "accent", Accent);
        AccentHi = ini.Get("branding", "accentHi", AccentHi);
        AccentLo = ini.Get("branding", "accentLo", AccentLo);
        LightBg = ini.Get("branding", "lightBg", LightBg);
        LightPanel = ini.Get("branding", "lightPanel", LightPanel);
        LightPanel2 = ini.Get("branding", "lightPanel2", LightPanel2);
        LightText = ini.Get("branding", "lightText", LightText);
        DarkBg = ini.Get("branding", "darkBg", DarkBg);
        DarkPanel = ini.Get("branding", "darkPanel", DarkPanel);
        DarkPanel2 = ini.Get("branding", "darkPanel2", DarkPanel2);
        DarkText = ini.Get("branding", "darkText", DarkText);
        OledBg = ini.Get("branding", "oledBg", OledBg);
        OledPanel = ini.Get("branding", "oledPanel", OledPanel);
        OledPanel2 = ini.Get("branding", "oledPanel2", OledPanel2);
        OledText = ini.Get("branding", "oledText", OledText);

        // --------------------------------------------------------------- auth
        string mode = ini.Get("auth", "mode", "open").Trim().ToLowerInvariant();
        AuthMode = mode switch
        {
            "password" => AuthMode.Password,
            "invite" => AuthMode.Invite,
            "open" => AuthMode.Open,
            _ => AuthMode.Open
        };
        if (mode is not ("open" or "password" or "invite"))
            Warnings.Add("auth.mode '" + mode + "' is unknown, using 'open'");

        AllowRegister = ini.GetBool("auth", "allowRegister", AllowRegister);
        SharedPassword = ini.Get("auth", "password", SharedPassword);
        InviteCode = ini.Get("auth", "inviteCode", InviteCode);
        RememberDays = ini.GetInt("auth", "rememberDays", RememberDays);
        SessionDays = ini.GetInt("auth", "sessionDays", SessionDays);
        MaxNickLen = ini.GetInt("auth", "maxNickLen", MaxNickLen);
        AllowNickChange = ini.GetBool("auth", "allowNickChange", AllowNickChange);
        FirstUserAdmin = ini.GetBool("auth", "firstUserAdmin", FirstUserAdmin);
        MaxFailedLoginsPerIp = ini.GetInt("auth", "maxFailedLoginsPerIp", MaxFailedLoginsPerIp);
        LoginLockSeconds = ini.GetInt("auth", "loginLockSeconds", LoginLockSeconds);

        // ------------------------------------------------------------- limits
        MaxMessageLen = ini.GetInt("limits", "maxMessageLen", MaxMessageLen);
        MessagesPerMin = ini.GetInt("limits", "messagesPerMin", MessagesPerMin);
        UploadsPerMin = ini.GetInt("limits", "uploadsPerMin", UploadsPerMin);
        MaxAvatarKb = ini.GetInt("limits", "maxAvatarKb", MaxAvatarKb);
        MaxVoiceSec = ini.GetInt("limits", "maxVoiceSec", MaxVoiceSec);
        MessagesPageSize = ini.GetInt("limits", "pageSize", MessagesPageSize);

        // The upload cap is documented under [server] but the admin board exposes it
        // as limits.maxUploadMb, so both spellings must land in the same field.
        MaxUploadMb = ini.GetInt("limits", "maxUploadMb", MaxUploadMb);

        // ------------------------------------------------------------ storage
        DataDir = ini.Get("storage", "dataDir", DataDir);
        BlobDir = ini.Get("storage", "blobDir", BlobDir);
        BackupDir = ini.Get("storage", "backupDir", BackupDir);
        KeepBackups = ini.GetInt("storage", "keepBackups", KeepBackups);
        PruneMonths = ini.GetInt("storage", "pruneMonths", PruneMonths);
        AutoBackupHours = ini.GetInt("storage", "autoBackupHours", AutoBackupHours);

        // -------------------------------------------------------------- admin
        AdminEnabled = ini.GetBool("admin", "enabled", AdminEnabled);
        AdminPort = ini.GetInt("admin", "port", AdminPort);
        AdminPortFallbacks = ini.GetIntList("admin", "portFallbacks", AdminPortFallbacks);
        AdminBind = ini.Get("admin", "bind", AdminBind);
        AdminPassword = ini.Get("admin", "password", AdminPassword);
        AdminLocalOnly = ini.GetBool("admin", "localOnly", AdminLocalOnly);

        // ---------------------------------------------------------------- log
        string lvl = ini.Get("log", "level", "info").Trim().ToLowerInvariant();
        LogLevel = lvl switch
        {
            "error" => LogLevel.Error,
            "warn" or "warning" => LogLevel.Warn,
            "debug" => LogLevel.Debug,
            "verbose" or "trace" => LogLevel.Verbose,
            _ => LogLevel.Info
        };
        LogDir = ini.Get("log", "dir", LogDir);
        LogMaxFileMb = ini.GetInt("log", "maxFileMb", LogMaxFileMb);
        LogRetentionDays = ini.GetInt("log", "retentionDays", LogRetentionDays);
        ApiTrace = ini.GetBool("log", "apiTrace", ApiTrace);
        LogRingSize = ini.GetInt("log", "ringSize", LogRingSize);

        // ----------------------------------------------------------- features
        FeaturesReactions = ini.GetBool("features", "reactions", FeaturesReactions);
        FeaturesReplies = ini.GetBool("features", "replies", FeaturesReplies);
        FeaturesPins = ini.GetBool("features", "pins", FeaturesPins);
        FeaturesVoice = ini.GetBool("features", "voice", FeaturesVoice);
        FeaturesFiles = ini.GetBool("features", "files", FeaturesFiles);
        FeaturesSearch = ini.GetBool("features", "search", FeaturesSearch);
        FeaturesTyping = ini.GetBool("features", "typing", FeaturesTyping);
        FeaturesPresence = ini.GetBool("features", "presence", FeaturesPresence);
        FeaturesEdit = ini.GetBool("features", "edit", FeaturesEdit);
        FeaturesDelete = ini.GetBool("features", "delete", FeaturesDelete);
        FeaturesRooms = ini.GetBool("features", "rooms", FeaturesRooms);
        FeaturesDm = ini.GetBool("features", "dm", FeaturesDm);
        EditWindowMin = ini.GetInt("features", "editWindowMin", EditWindowMin);
        DeleteWindowMin = ini.GetInt("features", "deleteWindowMin", DeleteWindowMin);
        CallsEnabled = ini.GetBool("features", "calls", CallsEnabled);
    }

    /// <summary>
    /// Runtime overrides written by the admin board (data/overrides.json). Keys look
    /// like "auth.mode" or "branding.accent" and win over config.ini, so an owner can
    /// flip a switch from the browser without losing the comments in the file.
    /// </summary>
    private void ApplyOverrides()
    {
        try
        {
            if (!File.Exists(OverridesPath)) return;
            Dictionary<string, string> map = Json.Deserialize<Dictionary<string, string>>(File.ReadAllText(OverridesPath));
            if (map == null) return;

            foreach (KeyValuePair<string, string> kv in map)
            {
                int dot = kv.Key.IndexOf('.');
                if (dot <= 0) continue;
                Ini single = Ini.Parse("[" + kv.Key[..dot] + "]\n" + kv.Key[(dot + 1)..] + "=" + kv.Value);
                Apply(single);
            }
        }
        catch (Exception ex)
        {
            Warnings.Add("cannot apply overrides: " + ex.Message);
        }
    }

    private void Validate()
    {
        if (Port <= 0 || Port > 65535) { Warnings.Add("server.port out of range, using 6767"); Port = 6767; }
        if (AdminPort <= 0 || AdminPort > 65535) { Warnings.Add("admin.port out of range, using 6768"); AdminPort = 6768; }
        if (AdminPort == Port) { Warnings.Add("admin.port equals server.port, admin board disabled"); AdminEnabled = false; }
        if (string.IsNullOrWhiteSpace(Bind)) Bind = "0.0.0.0";
        if (string.IsNullOrWhiteSpace(AdminBind)) AdminBind = Bind;

        MaxUploadMb = Math.Clamp(MaxUploadMb, 1, 1024);
        DiscoveryPort = Math.Clamp(DiscoveryPort, 1024, 65535);
        MaxMessageLen = Math.Clamp(MaxMessageLen, 200, 100000);
        MessagesPageSize = Math.Clamp(MessagesPageSize, 10, 200);
        KeepBackups = Math.Clamp(KeepBackups, 1, 365);
        MaxNickLen = Math.Clamp(MaxNickLen, 2, 64);
        SessionDays = Math.Clamp(SessionDays, 1, 3650);
        RememberDays = Math.Clamp(RememberDays, 1, 3650);
        LogRingSize = Math.Clamp(LogRingSize, 100, 50000);

        if (!IsHexColor(Accent)) { Warnings.Add("branding.accent is not a hex colour, using #0088FF"); Accent = "#0088FF"; }
        if (!IsHexColor(AccentHi)) AccentHi = Accent;
        if (!IsHexColor(AccentLo)) AccentLo = Accent;

        if (AuthMode == AuthMode.Password && string.IsNullOrWhiteSpace(SharedPassword))
        {
            Warnings.Add("auth.mode=password but auth.password is empty; the server stays open");
            AuthMode = AuthMode.Open;
        }
        if (AuthMode == AuthMode.Invite && string.IsNullOrWhiteSpace(InviteCode))
        {
            Warnings.Add("auth.mode=invite but auth.inviteCode is empty; the server stays open");
            AuthMode = AuthMode.Open;
        }
    }

    public static bool IsHexColor(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 7 || value[0] != '#') return false;
        for (int i = 1; i < 7; i++)
            if (!Uri.IsHexDigit(value[i])) return false;
        return true;
    }

    /// <summary>Values the admin board may change at runtime.</summary>
    public static readonly string[] EditableKeys =
    {
        "site.name", "site.tagline", "site.motd", "site.footer", "site.theme", "site.defaultSound",
        "auth.mode", "auth.allowRegister", "auth.password", "auth.inviteCode", "auth.allowNickChange",
        "branding.accent", "branding.accentHi", "branding.accentLo",
        "branding.darkBg", "branding.darkPanel", "branding.oledBg",
        "features.reactions", "features.replies", "features.pins", "features.voice",
        "features.files", "features.search", "features.typing", "features.presence",
        "features.edit", "features.delete", "features.rooms", "features.dm",
        "limits.maxMessageLen", "limits.maxUploadMb",
        "storage.pruneMonths", "storage.keepBackups",
        "server.discoverable"
    };
}
