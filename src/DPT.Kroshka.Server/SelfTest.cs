using System.Diagnostics;
using System.Text;
using DPT.Kroshka.Server.Storage;
using DPT.Kroshka.Shared;

namespace DPT.Kroshka.Server;

/// <summary>
/// A dependency-free test suite. It runs in a throwaway data directory, so it is safe
/// on a live install, and it is what CI executes on every push. If this passes, the
/// parts that lose data (config parsing, atomic writes, NDJSON recovery, dedupe,
/// sessions) are behaving.
/// </summary>
public static class SelfTest
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> _failures = new();

    public static int Run()
    {
        Stopwatch sw = Stopwatch.StartNew();
        string root = Path.Combine(Path.GetTempPath(), "kroshka-selftest-" + Crypto.NewId(4));

        Console.WriteLine("Kroshka self-test");
        Console.WriteLine("  temp: " + root);
        Console.WriteLine();

        try
        {
            TestIni();
            TestConfigDefaults();
            TestConfigValidation();
            TestCrypto();
            TestAtomicWrite(root);
            TestNdjsonRecovery(root);
            TestMessageLog(root);
            TestBlobDedupe(root);
            TestStore(root);
            TestSessionLifecycle(root);
        }
        catch (Exception ex)
        {
            _failed++;
            _failures.Add("unhandled: " + ex);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        sw.Stop();

        Console.WriteLine();
        foreach (string failure in _failures) Console.WriteLine("  FAIL " + failure);
        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed in {sw.ElapsedMilliseconds} ms");

        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ helpers

    private static void Check(string name, bool condition, string detail = "")
    {
        if (condition)
        {
            _passed++;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  ok   " + name);
            Console.ResetColor();
        }
        else
        {
            _failed++;
            _failures.Add(name + (detail.Length > 0 ? ": " + detail : ""));
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  FAIL " + name + (detail.Length > 0 ? ": " + detail : ""));
            Console.ResetColor();
        }
    }

    private static void Section(string name)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(name);
        Console.ResetColor();
    }

    // -------------------------------------------------------------------- tests

    private static void TestIni()
    {
        Section("ini");

        Ini ini = Ini.Parse("""
            ; comment
            [site]
            name = Kroshka
            tagline = "Мессенджер для своих"
            accent = #0088FF   ; brand colour, must survive
            [server]
            port = 6767
            portFallbacks = 6768, 6769
            [weird]
            broken line without equals
            """);

        Check("reads plain value", ini.Get("site", "name", "") == "Kroshka");
        Check("strips quotes", ini.Get("site", "tagline", "") == "Мессенджер для своих");
        Check("keeps hex colour", ini.Get("site", "accent", "") == "#0088FF", ini.Get("site", "accent", ""));
        Check("reads int", ini.GetInt("server", "port", 0) == 6767);
        Check("reads int list", ini.GetIntList("server", "portFallbacks", Array.Empty<int>()).Length == 2);
        Check("reports broken line", ini.Warnings.Count >= 1);

        Ini empty = Ini.Parse("");
        Check("empty file is safe", empty.Get("site", "name", "fallback") == "fallback");

        Ini broken = Ini.Parse("[unclosed\nkey=1");
        Check("unclosed section does not throw", broken.Warnings.Count >= 1);
    }

    private static void TestConfigDefaults()
    {
        Section("config");

        string dir = Path.Combine(Path.GetTempPath(), "kroshka-cfg-" + Crypto.NewId(4));
        Directory.CreateDirectory(dir);

        try
        {
            KroshkaConfig defaults = KroshkaConfig.Load(dir);
            Check("default port 6767", defaults.Port == 6767, defaults.Port.ToString());
            Check("default admin port 6768", defaults.AdminPort == 6768);
            Check("default auth open", defaults.AuthMode == AuthMode.Open);
            Check("default accent", defaults.Accent == "#0088FF");

            File.WriteAllText(Path.Combine(dir, "config.ini"), """
                [site]
                name = Test Chat
                [server]
                port = 9999
                [branding]
                accent = #123456
                [auth]
                mode = password
                """);

            KroshkaConfig cfg = KroshkaConfig.Load(dir);
            Check("overrides name", cfg.SiteName == "Test Chat");
            Check("overrides port", cfg.Port == 9999);
            Check("overrides accent", cfg.Accent == "#123456");
            Check("password mode without password falls back to open", cfg.AuthMode == AuthMode.Open, cfg.AuthMode.ToString());
            Check("warns about missing password", cfg.Warnings.Any(w => w.Contains("auth.password")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void TestConfigValidation()
    {
        Section("config validation");

        string dir = Path.Combine(Path.GetTempPath(), "kroshka-cfg2-" + Crypto.NewId(4));
        Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllText(Path.Combine(dir, "config.ini"), """
                [server]
                port = 99999
                [branding]
                accent = not-a-colour
                [admin]
                port = 99999
                """);

            KroshkaConfig cfg = KroshkaConfig.Load(dir);
            Check("clamps bad port", cfg.Port == 6767, cfg.Port.ToString());
            Check("clamps bad admin port", cfg.AdminPort == 6768);
            Check("rejects bad colour", cfg.Accent == "#0088FF");
            Check("collects warnings", cfg.Warnings.Count >= 2);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void TestCrypto()
    {
        Section("crypto");

        string salt = Crypto.NewSalt();
        string hash = Crypto.HashPassword("correct horse", salt, 10000);

        Check("verifies right password", Crypto.VerifyPassword("correct horse", salt, hash));
        Check("rejects wrong password", !Crypto.VerifyPassword("wrong horse", salt, hash));
        Check("rejects empty password", !Crypto.VerifyPassword("", salt, hash));
        Check("rejects garbage hash", !Crypto.VerifyPassword("correct horse", salt, "garbage"));
        Check("salt is random", Crypto.NewSalt() != Crypto.NewSalt());
        Check("token is random", Crypto.NewToken() != Crypto.NewToken());
        Check("token is url safe", Crypto.NewToken().All(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        Check("sha256 stable", Crypto.Sha256Hex("abc") == Crypto.Sha256Hex("abc"));
        Check("sha256 differs", Crypto.Sha256Hex("abc") != Crypto.Sha256Hex("abd"));
        Check("fixed equals same", Crypto.FixedEquals("abc", "abc"));
        Check("fixed equals different", !Crypto.FixedEquals("abc", "abd"));
        Check("fixed equals length", !Crypto.FixedEquals("abc", "abcd"));
    }

    private static void TestAtomicWrite(string root)
    {
        Section("atomic write");

        string dir = Path.Combine(root, "atomic");
        string path = Path.Combine(dir, "doc.json");

        FileStore.WriteAtomic(path, "{\"a\":1}");
        Check("creates file", File.Exists(path));
        Check("creates directory", Directory.Exists(dir));

        FileStore.WriteAtomic(path, "{\"a\":2}");
        Check("overwrites", File.ReadAllText(path).Contains("2"));
        Check("keeps backup", File.Exists(path + ".bak"));
        Check("no temp left", !File.Exists(path + ".tmp"));

        File.Delete(path);
        string recovered = FileStore.ReadTextWithBackup(path, out bool fromBackup);
        Check("recovers from bak", recovered != null && fromBackup);

        FileStore.Quarantine(path + ".bak");
        Check("quarantine moves file", Directory.GetFiles(dir, "*.bad-*").Length == 1);
    }

    private static void TestNdjsonRecovery(string root)
    {
        Section("ndjson recovery");

        string path = Path.Combine(root, "ndjson", "2026-11.ndjson");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        FileStore.AppendLine(path, new { id = "a", n = 1 });
        FileStore.AppendLine(path, new { id = "b", n = 2 });
        File.AppendAllText(path, "{\"id\":\"truncated\",\"n\":");   // simulate a power cut

        List<dynamic> items = FileStore.ReadLines<dynamic>(path, out int skipped);
        Check("reads good lines", items.Count == 2, items.Count.ToString());
        Check("counts skipped line", skipped == 1, skipped.ToString());

        int dropped = FileStore.CompactNdjson(path);
        Check("compact drops bad line", dropped == 1);
        Check("compact keeps good lines", File.ReadAllLines(path).Length == 2);

        FileStore.AppendLine(path, new { id = "c", n = 3 });
        Check("append after compact works", File.ReadAllLines(path).Length == 3);
    }

    private static void TestMessageLog(string root)
    {
        Section("message log");

        string dir = Path.Combine(root, "msglog", "msg");
        MessageLog log = new(dir, 0);

        Message first = log.Append(new Message
        {
            Id = "m1",
            AuthorId = "u1",
            Text = "привет",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-10)
        });

        Check("assigns seq 1", first.Seq == 1, first.Seq.ToString());

        Message second = log.Append(new Message
        {
            Id = "m2",
            AuthorId = "u1",
            Text = "как дела",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-9)
        });

        Check("assigns seq 2", second.Seq == 2);

        Message third = log.Append(new Message
        {
            Id = "m3",
            AuthorId = "u2",
            Text = "отлично",
            ClientId = "client-abc",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-8)
        });

        Check("finds by id", log.Find("m2")?.Text == "как дела");
        Check("finds by client id", log.FindByClientId("client-abc")?.Id == "m3");
        Check("search finds text", log.Search("дела", 10).Count == 1);
        Check("search is case insensitive", log.Search("ДЕЛА", 10).Count == 1);

        List<Message> page = log.Page(0, 2);
        Check("page returns newest first pair", page.Count == 2 && page[^1].Id == "m3");
        Check("page before seq", log.Page(3, 10).All(m => m.Seq < 3));

        third.Text = "отлично!";
        third.EditedUtc = DateTime.UtcNow;
        Check("update works", log.Update(third));
        Check("edit persisted to disk", File.ReadAllText(Path.Combine(dir, FileStore.DayKey(third.CreatedUtc) + ".ndjson")).Contains("отлично!"));

        // Reload from disk to prove the file is the source of truth.
        MessageLog reloaded = new(dir, 0);
        Check("reload sees 3 messages", reloaded.Page(0, 100).Count == 3, reloaded.Page(0, 100).Count.ToString());
        Check("reload keeps edit", reloaded.Find("m3")?.Text == "отлично!");

        int removed = log.Prune(DateTime.UtcNow.AddMinutes(-8.5));
        Check("prune removes older than cutoff", removed == 2, removed.ToString());

        MessageLog afterPrune = new(dir, 0);
        Check("prune persisted", afterPrune.Page(0, 100).Count == 1, afterPrune.Page(0, 100).Count.ToString());
    }

    private static void TestBlobDedupe(string root)
    {
        Section("blob store");

        KroshkaConfig cfg = new()
        {
            BaseDir = root,
            DataDir = "blobdata",
            BlobDir = "blobdata/blobs"
        };

        BlobStore blobs = new(cfg);
        byte[] png = MakeTinyPng(4, 3);

        BlobInfo a = blobs.Put(png, "cat.png", "image/png", "u1");
        BlobInfo b = blobs.Put(png, "cat-copy.png", "image/png", "u2");

        Check("same content same id", a.Id == b.Id);
        Check("measures png width", a.Width == 4, a.Width.ToString());
        Check("measures png height", a.Height == 3, a.Height.ToString());
        Check("stores extension", a.Ext == "png");
        Check("data file exists", File.Exists(blobs.DataPath(a)));
        Check("only one data file", Directory.GetFiles(Path.GetDirectoryName(blobs.DataPath(a))!, "*.png").Length == 1);

        byte[] other = Encoding.UTF8.GetBytes("different bytes");
        BlobInfo c = blobs.Put(other, "notes.txt", "text/plain", "u1");
        Check("different content different id", c.Id != a.Id);

        (int files, long bytes) = blobs.Stats();
        Check("stats counts blobs", files == 2, files.ToString());
        Check("stats counts bytes", bytes == png.Length + other.Length);

        (int removed, _) = blobs.SweepUnreferenced(new HashSet<string> { a.Id });
        Check("sweep keeps referenced", removed == 1, removed.ToString());
        Check("sweep kept the right one", blobs.Get(a.Id) != null);
        Check("sweep removed the orphan", blobs.Get(c.Id) == null);
    }

    private static void TestStore(string root)
    {
        Section("store");

        KroshkaConfig cfg = new()
        {
            BaseDir = root,
            DataDir = "store",
            BlobDir = "store/blobs",
            BackupDir = "store/backups"
        };

        KroshkaStore store = new(cfg);
        store.Load();

        Check("empty store has no users", store.UserCount == 0);

        User alice = store.CreateUser("alice", "Алиса", UserRole.Owner);
        User bob = store.CreateUser("bob", "Боб", UserRole.Member);

        Check("creates users", store.UserCount == 2);
        Check("nick index works", store.FindByNick("ALICE")?.Id == alice.Id);
        Check("nick lookup is case insensitive", store.FindByNick("bob")?.Id == bob.Id);
        Check("detects taken nick", store.NickTaken("alice"));
        Check("detects free nick", !store.NickTaken("carol"));

        Room room = store.CreateRoom("Общий", "главная", "", false, alice.Id, new List<string>());
        Check("creates room", store.GetRoom(room.Id) != null);
        Check("room has creator", room.Members.Contains(alice.Id));

        Direct dm = store.GetOrCreateDm(alice.Id, bob.Id);
        Direct again = store.GetOrCreateDm(bob.Id, alice.Id);
        Check("dm id is symmetric", dm.Id == again.Id, dm.Id + " vs " + again.Id);
        Check("dm stores ordered pair",
            string.CompareOrdinal(dm.UserA, dm.UserB) < 0,
            dm.UserA + " / " + dm.UserB);
        Check("dm pair matches its id", dm.Id == $"dm:{dm.UserA}:{dm.UserB}");

        MessageLog log = store.Messages(room.Id);
        Message m = log.Append(new Message { Id = "m1", AuthorId = alice.Id, Text = "привет всем", CreatedUtc = DateTime.UtcNow });
        store.TouchConversation(room.Id, m);

        Check("room last seq updated", store.GetRoom(room.Id)!.LastSeq == 1);
        Check("room preview updated", store.GetRoom(room.Id)!.LastMessagePreview == "привет всем");

        store.UpdateUser(bob.Id, u => u.DisplayName = "Борис");
        Check("update user persists", store.GetUser(bob.Id)!.DisplayName == "Борис");

        store.SaveAll();

        // Fresh instance must see exactly the same world.
        KroshkaStore reloaded = new(cfg);
        reloaded.Load();
        Check("reload sees users", reloaded.UserCount == 2);
        Check("reload sees rooms", reloaded.GetRoom(room.Id) != null);
        Check("reload sees dms", reloaded.GetDm(dm.Id) != null);
        Check("reload sees messages", reloaded.Messages(room.Id).Page(0, 10).Count == 1);

        // Corrupt a user file and make sure it is quarantined, not fatal.
        string alicePath = Path.Combine(store.UsersDir, alice.Id, "user.json");
        File.WriteAllText(alicePath, "{ not json");
        File.Delete(alicePath + ".bak");

        KroshkaStore damaged = new(cfg);
        damaged.Load();
        Check("corrupt user quarantined", Directory.GetFiles(Path.Combine(store.UsersDir, alice.Id), "*.bad-*").Length == 1);
        Check("other users survive", damaged.GetUser(bob.Id) != null);
    }

    private static void TestSessionLifecycle(string root)
    {
        Section("sessions");

        KroshkaConfig cfg = new()
        {
            BaseDir = root,
            DataDir = "sessions",
            BlobDir = "sessions/blobs",
            BackupDir = "sessions/backups",
            RememberDays = 365,
            SessionDays = 30
        };

        KroshkaStore store = new(cfg);
        store.Load();

        User user = store.CreateUser("dave", "Дэйв", UserRole.Member);

        (Session remember, string rawToken) = store.CreateSession(user.Id, "desktop", "127.0.0.1", remember: true);

        Check("returns raw token", rawToken.Length > 20);
        Check("stores hash not token", remember.TokenHash != rawToken, "token leaked into session record");

        store.SaveAll();

        KroshkaStore reloaded = new(cfg);
        reloaded.Load();

        Session found = reloaded.FindSessionByToken(rawToken);
        Check("token resolves after reload", found != null);
        Check("resolves to right user", found?.UserId == user.Id);
        Check("remember extends expiry", found != null && (found.ExpiresUtc - DateTime.UtcNow).TotalDays > 300);

        Check("wrong token rejected", reloaded.FindSessionByToken("nope") == null);

        Session shortLived = reloaded.CreateSession(user.Id, "phone", "127.0.0.1", remember: false).session;
        Check("short session is 30 days", (shortLived.ExpiresUtc - DateTime.UtcNow).TotalDays < 31);

        reloaded.DeleteSession(found.Id);
        reloaded.SaveAll();

        KroshkaStore final = new(cfg);
        final.Load();
        Check("deleted session gone", final.FindSessionByToken(rawToken) == null);
    }

    // --------------------------------------------------------------- utilities

    /// <summary>Builds a minimal valid PNG header so ImageProbe has real bytes to read.</summary>
    private static byte[] MakeTinyPng(int width, int height)
    {
        List<byte> bytes = new()
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D,
            (byte)'I', (byte)'H', (byte)'D', (byte)'R'
        };

        bytes.AddRange(BitConverter.GetBytes(width).Reverse());
        bytes.AddRange(BitConverter.GetBytes(height).Reverse());
        bytes.AddRange(new byte[] { 8, 6, 0, 0, 0 });

        // filler so the blob has a realistic size and a distinct hash
        bytes.AddRange(Encoding.UTF8.GetBytes(new string('x', 64)));
        return bytes.ToArray();
    }
}
