using System.Globalization;
using System.Text;

namespace DPT.Kroshka.Shared;

/// <summary>
/// Crash tolerant file helpers. No database, no NuGet: a message log is an
/// append-only NDJSON file per day, and every small document is written atomically
/// with a .bak fallback so a power cut costs at most the last line, never the file.
/// </summary>
public static class FileStore
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static string ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null; }
        catch { return null; }
    }

    public static string ReadTextWithBackup(string path, out bool fromBackup)
    {
        fromBackup = false;
        string text = ReadText(path);
        if (text != null) return text;

        string bak = path + ".bak";
        text = ReadText(bak);
        if (text != null) { fromBackup = true; return text; }
        return null;
    }

    /// <summary>Write via temp file + replace. Never leaves a half written document.</summary>
    public static void WriteAtomic(string path, string text)
    {
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        byte[] bytes = Utf8.GetBytes(text ?? "");

        using (FileStream fs = new(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush();
            try { fs.Flush(true); } catch { }
        }

        if (!File.Exists(path))
        {
            File.Move(tmp, path);
            try { File.Copy(path, path + ".bak", true); } catch { }
            return;
        }

        try
        {
            File.Replace(tmp, path, path + ".bak", true);
            return;
        }
        catch { }

        try
        {
            string bak = path + ".bak";
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(path, bak);
            File.Move(tmp, path);
            return;
        }
        catch { }

        File.Copy(tmp, path, true);
        try { File.Delete(tmp); } catch { }
    }

    public static void WriteAtomic<T>(string path, T value)
    {
        WriteAtomic(path, Json.Serialize(value));
    }

    public static T ReadJson<T>(string path, T fallback = default)
    {
        string text = ReadTextWithBackup(path, out bool fromBackup);
        if (text == null) return fallback;

        T value = Json.TryDeserialize<T>(text, fallback);
        if (value == null) return fallback;

        if (fromBackup)
            Log.Warn("DATA", "recovered " + Path.GetFileName(path) + " from .bak");

        return value;
    }

    /// <summary>Move an unreadable file aside instead of deleting evidence.</summary>
    public static void Quarantine(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            string bad = path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            File.Move(path, bad);
            Log.Warn("DATA", "quarantined " + Path.GetFileName(path) + " -> " + Path.GetFileName(bad));
        }
        catch { }
    }

    // ------------------------------------------------------------ NDJSON log

    /// <summary>
    /// Appends one JSON object as a line. Open/append/close per call keeps the file
    /// readable by any other process (an admin compactor, a text editor) and survives
    /// a hard kill between messages. Throughput is fine: a family chat is not Kafka.
    /// </summary>
    public static void AppendLine(string path, string json)
    {
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using FileStream fs = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        byte[] bytes = Utf8.GetBytes(json.Replace("\r", "").Replace("\n", " ") + "\n");
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush();
    }

    public static void AppendLine<T>(string path, T value) => AppendLine(path, Json.Serialize(value));

    /// <summary>
    /// Reads an NDJSON file, skipping (and counting) malformed lines. A truncated last
    /// line from a crash is expected and simply ignored.
    /// </summary>
    public static List<T> ReadLines<T>(string path, out int skipped)
    {
        skipped = 0;
        List<T> result = new();
        if (!File.Exists(path)) return result;

        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(fs, Encoding.UTF8);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                try
                {
                    T item = Json.Deserialize<T>(line);
                    if (item != null) result.Add(item);
                    else skipped++;
                }
                catch { skipped++; }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("DATA", "cannot read " + Path.GetFileName(path) + ": " + ex.Message);
        }

        return result;
    }

    /// <summary>Rewrites a log file atomically, dropping malformed lines.</summary>
    public static int CompactNdjson(string path)
    {
        if (!File.Exists(path)) return 0;
        List<string> keep = new();
        int dropped = 0;

        try
        {
            using (FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new(fs, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    try
                    {
                        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(line);
                        keep.Add(line);
                    }
                    catch { dropped++; }
                }
            }

            if (dropped > 0)
                WriteAtomic(path, string.Join("\n", keep) + (keep.Count > 0 ? "\n" : ""));
        }
        catch (Exception ex)
        {
            Log.Warn("DATA", "compact failed for " + Path.GetFileName(path) + ": " + ex.Message);
            return 0;
        }

        return dropped;
    }

    public static string DayKey(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);
    public static string DayKeyFull(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, target));
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, target), true);
    }

    public static long DirectorySize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
        }
        catch { }
        return total;
    }
}
