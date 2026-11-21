using System.Buffers.Binary;
using DPT.Kroshka.Shared;

namespace DPT.Kroshka.Server.Storage;

/// <summary>
/// Content addressed file storage. A blob is stored under its SHA-256, so sending the
/// same photo to three rooms costs one copy on disk. Nothing is ever overwritten:
/// writing an existing hash is a no-op.
///
///   data/blobs/&lt;ab&gt;/&lt;sha256&gt;.&lt;ext&gt;   the bytes
///   data/blobs/&lt;ab&gt;/&lt;sha256&gt;.json        name, mime, size, dimensions
/// </summary>
public sealed class BlobStore
{
    private readonly KroshkaConfig _cfg;
    private readonly string _root;

    public BlobStore(KroshkaConfig cfg)
    {
        _cfg = cfg;
        _root = cfg.BlobDirPath;
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    private string ShardFor(string id) => Path.Combine(_root, id.Length >= 2 ? id[..2] : "00");

    private string MetaPath(string id) => Path.Combine(ShardFor(id), id + ".json");

    public string DataPath(BlobInfo info)
    {
        return Path.Combine(ShardFor(info.Id), info.Id + (string.IsNullOrEmpty(info.Ext) ? "" : "." + info.Ext));
    }

    public BlobInfo Get(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length < 16) return null;
        foreach (char c in id)
            if (!Uri.IsHexDigit(c)) return null;
        return FileStore.ReadJson<BlobInfo>(MetaPath(id));
    }

    /// <summary>Stores bytes and returns the metadata. Duplicate content is deduped.</summary>
    public BlobInfo Put(byte[] data, string fileName, string mime, string ownerId)
    {
        string sha = Crypto.Sha256Hex(data);
        string ext = GuessExtension(fileName, mime);
        (int width, int height) = ImageProbe.Measure(data, mime);

        BlobInfo info = new()
        {
            Id = sha,
            Ext = ext,
            Name = SanitizeName(fileName),
            Mime = string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime,
            Size = data.Length,
            Sha256 = sha,
            OwnerId = ownerId,
            CreatedUtc = DateTime.UtcNow,
            Width = width,
            Height = height
        };

        string path = DataPath(info);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, true);
        }

        FileStore.WriteAtomic(MetaPath(sha), info);
        return info;
    }

    public bool Delete(string id)
    {
        BlobInfo info = Get(id);
        if (info == null) return false;
        try { File.Delete(DataPath(info)); } catch { }
        try { File.Delete(MetaPath(id)); } catch { }
        try { File.Delete(MetaPath(id) + ".bak"); } catch { }
        return true;
    }

    /// <summary>Removes blobs that no message references. Used by the admin compactor.</summary>
    public (int removed, long bytes) SweepUnreferenced(HashSet<string> referenced)
    {
        int removed = 0;
        long bytes = 0;

        foreach (string shard in Directory.GetDirectories(_root))
        {
            foreach (string metaFile in Directory.GetFiles(shard, "*.json"))
            {
                string id = Path.GetFileNameWithoutExtension(metaFile);
                if (referenced.Contains(id)) continue;

                BlobInfo info = FileStore.ReadJson<BlobInfo>(metaFile);
                if (info != null)
                {
                    bytes += info.Size;
                    try { File.Delete(DataPath(info)); } catch { }
                }
                try { File.Delete(metaFile); } catch { }
                // The .bak is a recovery copy, not a resurrection path: a swept blob
                // must not come back the next time Get() falls back to it.
                try { File.Delete(metaFile + ".bak"); } catch { }
                removed++;
            }
        }

        return (removed, bytes);
    }

    public (int files, long bytes) Stats()
    {
        int files = 0;
        long bytes = 0;
        try
        {
            foreach (string shard in Directory.GetDirectories(_root))
            {
                foreach (string file in Directory.GetFiles(shard, "*.json"))
                {
                    BlobInfo info = FileStore.ReadJson<BlobInfo>(file);
                    if (info == null) continue;
                    files++;
                    bytes += info.Size;
                }
            }
        }
        catch { }
        return (files, bytes);
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "file";
        string clean = Path.GetFileName(name);
        foreach (char c in Path.GetInvalidFileNameChars())
            clean = clean.Replace(c, '_');
        return clean.Length > 200 ? clean[..200] : clean;
    }

    private static string GuessExtension(string fileName, string mime)
    {
        string ext = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
        if (ext.Length is > 0 and <= 8 && ext.All(char.IsLetterOrDigit)) return ext;

        return (mime ?? "").ToLowerInvariant() switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpg",
            "image/gif" => "gif",
            "image/webp" => "webp",
            "image/svg+xml" => "svg",
            "audio/webm" => "webm",
            "audio/ogg" => "ogg",
            "audio/mpeg" => "mp3",
            "audio/wav" or "audio/x-wav" => "wav",
            "video/mp4" => "mp4",
            "video/webm" => "webm",
            "application/pdf" => "pdf",
            "text/plain" => "txt",
            _ => "bin"
        };
    }
}

/// <summary>
/// Reads image dimensions straight from the header. No System.Drawing, so the same
/// code runs on Linux and does not pull a native library into a self-contained build.
/// </summary>
public static class ImageProbe
{
    public static (int width, int height) Measure(byte[] data, string mime)
    {
        try
        {
            if (data == null || data.Length < 24) return (0, 0);

            // PNG: 89 50 4E 47 ... IHDR at 16
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            {
                int w = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4));
                int h = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4));
                return (w, h);
            }

            // GIF: "GIF8" then little endian at 6
            if (data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
            {
                int w = data[6] | (data[7] << 8);
                int h = data[8] | (data[9] << 8);
                return (w, h);
            }

            // BMP
            if (data[0] == 'B' && data[1] == 'M')
            {
                int w = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18, 4));
                int h = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22, 4)));
                return (w, h);
            }

            // JPEG: walk the segments until a start-of-frame marker
            if (data[0] == 0xFF && data[1] == 0xD8)
            {
                int i = 2;
                while (i + 9 < data.Length)
                {
                    if (data[i] != 0xFF) { i++; continue; }
                    byte marker = data[i + 1];
                    if (marker == 0xD8 || marker == 0xD9 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                    {
                        i += 2;
                        continue;
                    }
                    int len = (data[i + 2] << 8) | data[i + 3];
                    if (len < 2) break;
                    bool isSof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                    if (isSof && i + 9 < data.Length)
                    {
                        int h = (data[i + 5] << 8) | data[i + 6];
                        int w = (data[i + 7] << 8) | data[i + 8];
                        return (w, h);
                    }
                    i += 2 + len;
                }
            }

            // WebP (VP8X / VP8 / VP8L)
            if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' &&
                data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
            {
                if (data[12] == 'V' && data[13] == 'P' && data[14] == '8' && data[15] == 'X')
                {
                    int w = 1 + (data[24] | (data[25] << 8) | (data[26] << 16));
                    int h = 1 + (data[27] | (data[28] << 8) | (data[29] << 16));
                    return (w, h);
                }
                if (data[12] == 'V' && data[13] == 'P' && data[14] == '8' && data[15] == ' ')
                {
                    int w = (data[26] | (data[27] << 8)) & 0x3FFF;
                    int h = (data[28] | (data[29] << 8)) & 0x3FFF;
                    return (w, h);
                }
            }

            return (0, 0);
        }
        catch
        {
            return (0, 0);
        }
    }
}
