using System.Globalization;
using DPT.Kroshka.Shared;

namespace DPT.Kroshka.Server.Storage;

/// <summary>
/// One conversation's message log, held in memory once touched. Rooms and DMs share
/// this class because paging, reactions, pins and search do not care which they are.
/// </summary>
public sealed class MessageLog
{
    private readonly object _sync = new();
    private readonly string _dir;
    private readonly List<Message> _messages = new();
    private bool _loaded;
    private long _lastSeq;

    public MessageLog(string dir, long lastSeqHint)
    {
        _dir = dir;
        _lastSeq = lastSeqHint;
    }

    public long LastSeq { get { lock (_sync) { return _lastSeq; } } }
    public int Count { get { lock (_sync) { return _messages.Count; } } }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _messages.Clear();

        if (!Directory.Exists(_dir)) return;

        string[] files;
        try { files = Directory.GetFiles(_dir, "*.ndjson"); }
        catch { return; }

        Array.Sort(files, StringComparer.Ordinal);
        foreach (string file in files)
        {
            foreach (Message m in FileStore.ReadLines<Message>(file, out int skipped))
            {
                _messages.Add(m);
                if (m.Seq > _lastSeq) _lastSeq = m.Seq;
            }
        }

        _messages.Sort((a, b) => a.Seq.CompareTo(b.Seq));
    }

    private string FileFor(DateTime utc)
    {
        return Path.Combine(_dir, FileStore.DayKey(utc) + ".ndjson");
    }

    public Message Append(Message message)
    {
        lock (_sync)
        {
            EnsureLoaded();
            message.Seq = ++_lastSeq;
            _messages.Add(message);
            FileStore.AppendLine(FileFor(message.CreatedUtc), message);
            return message;
        }
    }

    /// <summary>Newest page, oldest first. beforeSeq = 0 means "the latest".</summary>
    public List<Message> Page(long beforeSeq, int limit)
    {
        lock (_sync)
        {
            EnsureLoaded();
            List<Message> result = new();
            for (int i = _messages.Count - 1; i >= 0 && result.Count < limit; i--)
            {
                Message m = _messages[i];
                if (beforeSeq > 0 && m.Seq >= beforeSeq) continue;
                result.Add(m);
            }
            result.Reverse();
            return result;
        }
    }

    public List<Message> After(long seq, int limit)
    {
        lock (_sync)
        {
            EnsureLoaded();
            List<Message> result = new();
            foreach (Message m in _messages)
            {
                if (m.Seq <= seq) continue;
                result.Add(m);
                if (result.Count >= limit) break;
            }
            return result;
        }
    }

    public Message Find(string id)
    {
        lock (_sync)
        {
            EnsureLoaded();
            foreach (Message m in _messages)
                if (m.Id == id) return m;
            return null;
        }
    }

    public Message FindByClientId(string clientId)
    {
        if (string.IsNullOrEmpty(clientId)) return null;
        lock (_sync)
        {
            EnsureLoaded();
            foreach (Message m in _messages)
                if (m.ClientId == clientId) return m;
            return null;
        }
    }

    /// <summary>
    /// Rewrites only the month file that contains the message. Editing in place inside
    /// NDJSON would corrupt the file on a partial write, so we compact the affected
    /// month and leave the others alone.
    /// </summary>
    public bool Update(Message message)
    {
        lock (_sync)
        {
            EnsureLoaded();
            Message existing = null;
            foreach (Message m in _messages)
            {
                if (m.Id == message.Id) { existing = m; break; }
            }
            if (existing == null) return false;

            message.Seq = existing.Seq;
            message.CreatedUtc = existing.CreatedUtc;

            int index = _messages.IndexOf(existing);
            _messages[index] = message;

            RewriteMonth(FileStore.DayKey(message.CreatedUtc));
            return true;
        }
    }

    private void RewriteMonth(string dayKey)
    {
        string path = Path.Combine(_dir, dayKey + ".ndjson");
        List<string> lines = new();
        foreach (Message m in _messages)
        {
            if (FileStore.DayKey(m.CreatedUtc) != dayKey) continue;
            lines.Add(Json.Serialize(m));
        }
        FileStore.WriteAtomic(path, lines.Count == 0 ? "" : string.Join("\n", lines) + "\n");
    }

    public List<Message> Search(string query, int limit)
    {
        lock (_sync)
        {
            EnsureLoaded();
            List<Message> result = new();
            string needle = query.ToLowerInvariant();
            for (int i = _messages.Count - 1; i >= 0 && result.Count < limit; i--)
            {
                Message m = _messages[i];
                if (m.Deleted) continue;
                if (m.Text == null) continue;
                if (m.Text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    result.Add(m);
            }
            return result;
        }
    }

    /// <summary>Drops every message older than the cutoff. Used by the admin pruner.</summary>
    public int Prune(DateTime cutoffUtc)
    {
        lock (_sync)
        {
            EnsureLoaded();

            Dictionary<string, List<Message>> byMonth = new(StringComparer.Ordinal);
            int removed = 0;

            _messages.RemoveAll(m =>
            {
                bool drop = m.CreatedUtc < cutoffUtc;
                if (drop) removed++;
                return drop;
            });

            if (removed == 0) return 0;

            string[] files;
            try { files = Directory.GetFiles(_dir, "*.ndjson"); }
            catch { return removed; }

            foreach (string file in files)
            {
                string dayKey = Path.GetFileNameWithoutExtension(file);
                List<string> lines = new();
                foreach (Message m in _messages)
                    if (FileStore.DayKey(m.CreatedUtc) == dayKey) lines.Add(Json.Serialize(m));

                if (lines.Count == 0)
                {
                    try { File.Delete(file); } catch { }
                }
                else
                {
                    FileStore.WriteAtomic(file, string.Join("\n", lines) + "\n");
                }
            }

            return removed;
        }
    }

    /// <summary>Repairs truncated or malformed lines across all months.</summary>
    public int Compact()
    {
        lock (_sync)
        {
            if (!Directory.Exists(_dir)) return 0;
            int dropped = 0;
            string[] files;
            try { files = Directory.GetFiles(_dir, "*.ndjson"); }
            catch { return 0; }

            foreach (string file in files) dropped += FileStore.CompactNdjson(file);

            _loaded = false;
            EnsureLoaded();
            return dropped;
        }
    }

    public IEnumerable<Message> All()
    {
        lock (_sync)
        {
            EnsureLoaded();
            return new List<Message>(_messages);
        }
    }
}
