namespace DPT.Kroshka.Shared;

/// <summary>Where a conversation lives. Rooms are public to the server, DMs are a pair.</summary>
public enum ConversationKind
{
    Room = 0,
    Dm = 1
}

public enum UserRole
{
    Member = 0,
    Admin = 1,
    Owner = 2
}

public sealed class User
{
    public string Id { get; set; } = "";
    public string Nick { get; set; } = "";
    public string NickLower { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Bio { get; set; } = "";
    public string AvatarBlob { get; set; } = "";
    /// <summary>Hex accent the person picked for themselves; empty means brand accent.</summary>
    public string Accent { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Member;
    public bool Banned { get; set; }
    public string BanReason { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public string Theme { get; set; } = "";
    public bool Sound { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public string LastReadJson { get; set; } = "{}";
}

public sealed class Room
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Topic { get; set; } = "";
    public string Emoji { get; set; } = "";
    public bool IsPrivate { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<string> Members { get; set; } = new();
    /// <summary>Only these may post when the room is read-only for everyone else.</summary>
    public List<string> Moderators { get; set; } = new();
    public bool ReadOnly { get; set; }
    public bool Archived { get; set; }
    public string PinnedMessageId { get; set; } = "";
    public long LastSeq { get; set; }
    public string LastMessagePreview { get; set; } = "";
    public DateTime? LastMessageUtc { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>
/// A direct conversation between two people. The id is deterministic
/// ("dm:" + smaller user id + ":" + bigger user id) so both sides resolve the
/// same folder without a lookup table that could drift.
/// </summary>
public sealed class Direct
{
    public string Id { get; set; } = "";
    public string UserA { get; set; } = "";
    public string UserB { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public long LastSeq { get; set; }
    public string LastMessagePreview { get; set; } = "";
    public DateTime? LastMessageUtc { get; set; }
    public string PinnedMessageId { get; set; } = "";
}

public enum MessageKind
{
    Text = 0,
    File = 1,
    Voice = 2,
    Image = 3,
    System = 4
}

public sealed class Attachment
{
    public string BlobId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mime { get; set; } = "";
    public long Size { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double DurationSec { get; set; }
    public int? WaveformPeaks { get; set; }
    public string Waveform { get; set; } = "";
}

public sealed class Message
{
    public string Id { get; set; } = "";
    public long Seq { get; set; }
    public string ConversationId { get; set; } = "";
    public ConversationKind Kind { get; set; }
    public string AuthorId { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EditedUtc { get; set; }
    public string Text { get; set; } = "";
    public MessageKind Type { get; set; } = MessageKind.Text;
    public Attachment Attachment { get; set; }
    public string ReplyToId { get; set; } = "";
    public string ReplyToAuthor { get; set; } = "";
    public string ReplyToPreview { get; set; } = "";
    public Dictionary<string, List<string>> Reactions { get; set; } = new();
    public bool Deleted { get; set; }
    public string DeletedBy { get; set; } = "";
    /// <summary>Client generated id, used to dedupe an optimistic send that retried.</summary>
    public string ClientId { get; set; } = "";
}

public sealed class Session
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    /// <summary>SHA-256 of the device token. The raw token never touches the disk.</summary>
    public string TokenHash { get; set; } = "";
    public string Device { get; set; } = "";
    public string Ip { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; } = DateTime.UtcNow.AddDays(30);
    public bool Remember { get; set; }
}

public sealed class BlobInfo
{
    public string Id { get; set; } = "";
    /// <summary>File extension without the dot; empty for extensionless blobs.</summary>
    public string Ext { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mime { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public int Width { get; set; }
    public int Height { get; set; }
    public double DurationSec { get; set; }
}
