namespace DPT.Kroshka.Shared;

/// <summary>
/// The wire contract. REST carries commands and history, WebSocket carries live
/// events. Both use these shapes, so the desktop client, the browser and the future
/// Android client share one mental model.
/// </summary>
public static class Protocol
{
    public const string Version = "1.0.0";
    public const string WsPath = "/ws";

    /// <summary>Server to client event names.</summary>
    public static class Events
    {
        public const string Ready = "ready";
        public const string Message = "message";
        public const string MessageEdited = "message.edited";
        public const string MessageDeleted = "message.deleted";
        public const string Reaction = "reaction";
        public const string Typing = "typing";
        public const string Presence = "presence";
        public const string RoomUpdated = "room.updated";
        public const string RoomCreated = "room.created";
        public const string RoomDeleted = "room.deleted";
        public const string DmCreated = "dm.created";
        public const string UserUpdated = "user.updated";
        public const string Read = "read";
        public const string Pinned = "pinned";
        public const string CallSignal = "call.signal";
        public const string ServerNotice = "notice";
        public const string Pong = "pong";
        public const string Error = "error";
    }

    /// <summary>Client to server commands.</summary>
    public static class Commands
    {
        public const string Subscribe = "subscribe";
        public const string Unsubscribe = "unsubscribe";
        public const string Typing = "typing";
        public const string Ping = "ping";
        public const string CallSignal = "call.signal";
    }
}

/// <summary>Envelope for a WebSocket frame in either direction.</summary>
public sealed record WsFrame
{
    public string T { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Id { get; set; } = "";
    public long Seq { get; set; }
    public bool On { get; set; }
    public string Text { get; set; } = "";
    public string ClientId { get; set; } = "";
    public object Data { get; set; }

    public static WsFrame Make(string t) => new() { T = t };

    public string ToJson() => Json.Serialize(this);

    public static WsFrame Parse(string json) => Json.TryDeserialize<WsFrame>(json);
}

// --------------------------------------------------------------------- auth

public sealed class LoginRequest
{
    public string Nick { get; set; } = "";
    public string Password { get; set; } = "";
    public string InviteCode { get; set; } = "";
    public string Device { get; set; } = "";
    public bool Remember { get; set; } = true;
}

public sealed class LoginResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public string Token { get; set; } = "";
    public string UserId { get; set; } = "";
    public User User { get; set; }
    public string ServerName { get; set; } = "";
    public DateTime? ExpiresUtc { get; set; }
}

public sealed class BootstrapDto
{
    public string Version { get; set; } = Protocol.Version;
    public string ServerName { get; set; } = "";
    public string ServerShort { get; set; } = "";
    public string Tagline { get; set; } = "";
    public string Motd { get; set; } = "";
    public string Footer { get; set; } = "";
    public string SupportEmail { get; set; } = "";
    public string AuthMode { get; set; } = "open";
    public bool AllowRegister { get; set; }
    public int MaxNickLen { get; set; } = 24;
    public string Theme { get; set; } = "light";
    public bool DefaultSound { get; set; } = true;
    public string Language { get; set; } = "ru";
    public BrandDto Brand { get; set; } = new();
    public FeatureDto Features { get; set; } = new();
    public int MaxUploadMb { get; set; } = 64;
    public int MaxMessageLen { get; set; } = 8000;
    public int MaxVoiceSec { get; set; } = 300;
    public int PageSize { get; set; } = 50;
    public int UserCount { get; set; }
    public bool FirstUserAdmin { get; set; }
    public string ServerTime { get; set; } = "";
    public string LogoUrl { get; set; } = "";
}

public sealed class BrandDto
{
    public string Accent { get; set; } = "#0088FF";
    public string AccentHi { get; set; } = "#0080FF";
    public string AccentLo { get; set; } = "#0066CC";
    public string LightBg { get; set; } = "#FFFFFF";
    public string LightPanel { get; set; } = "#F6F8FB";
    public string LightPanel2 { get; set; } = "#EDF1F7";
    public string LightText { get; set; } = "#0B1622";
    public string DarkBg { get; set; } = "#0A0F1A";
    public string DarkPanel { get; set; } = "#111A29";
    public string DarkPanel2 { get; set; } = "#16202F";
    public string DarkText { get; set; } = "#E6EDF7";
    public string OledBg { get; set; } = "#000000";
    public string OledPanel { get; set; } = "#05080D";
    public string OledPanel2 { get; set; } = "#0A1018";
    public string OledText { get; set; } = "#E8EEF6";
}

public sealed class FeatureDto
{
    public bool Reactions { get; set; } = true;
    public bool Replies { get; set; } = true;
    public bool Pins { get; set; } = true;
    public bool Voice { get; set; } = true;
    public bool Files { get; set; } = true;
    public bool Search { get; set; } = true;
    public bool Typing { get; set; } = true;
    public bool Presence { get; set; } = true;
    public bool Edit { get; set; } = true;
    public bool Delete { get; set; } = true;
    public bool Rooms { get; set; } = true;
    public bool Dm { get; set; } = true;
    public bool Calls { get; set; }
}

// ----------------------------------------------------------------- messages

public sealed class SendMessageRequest
{
    public string Text { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ReplyToId { get; set; } = "";
    public string AttachmentId { get; set; } = "";
    public MessageKind Type { get; set; } = MessageKind.Text;
}

public sealed class EditMessageRequest
{
    public string Text { get; set; } = "";
}

public sealed class ReactionRequest
{
    public string Emoji { get; set; } = "";
}

public sealed class PageRequest
{
    public string ConversationId { get; set; } = "";
    public long BeforeSeq { get; set; }
    public int Limit { get; set; }
}

public sealed class MessagePage
{
    public List<Message> Messages { get; set; } = new();
    public Dictionary<string, User> Users { get; set; } = new();
    public bool HasMore { get; set; }
}

public sealed class ConversationDto
{
    public string Id { get; set; } = "";
    public ConversationKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Emoji { get; set; } = "";
    public string AvatarBlob { get; set; } = "";
    public string PeerId { get; set; } = "";
    public bool IsPrivate { get; set; }
    public bool ReadOnly { get; set; }
    public bool Archived { get; set; }
    public long LastSeq { get; set; }
    public string Preview { get; set; } = "";
    public DateTime? LastUtc { get; set; }
    public int Unread { get; set; }
    public long LastReadSeq { get; set; }
    public int Members { get; set; }
    public string PinnedMessageId { get; set; } = "";
    public List<string> MemberIds { get; set; } = new();
}

public sealed class SyncDto
{
    public List<ConversationDto> Conversations { get; set; } = new();
    public Dictionary<string, User> Users { get; set; } = new();
    public User Me { get; set; }
    public List<string> Online { get; set; } = new();
}

public sealed class ReadRequest
{
    public string ConversationId { get; set; } = "";
    public long Seq { get; set; }
}

public sealed class TypingRequest
{
    public string ConversationId { get; set; } = "";
    public bool On { get; set; } = true;
}

public sealed class CreateRoomRequest
{
    public string Name { get; set; } = "";
    public string Topic { get; set; } = "";
    public string Emoji { get; set; } = "";
    public bool IsPrivate { get; set; }
    public List<string> Members { get; set; }
}

public sealed class UpdateRoomRequest
{
    public string Name { get; set; }
    public string Topic { get; set; }
    public string Emoji { get; set; }
    public bool? ReadOnly { get; set; }
    public bool? Archived { get; set; }
    public List<string> Members { get; set; }
}

public sealed class UpdateProfileRequest
{
    public string DisplayName { get; set; }
    public string Bio { get; set; }
    public string Accent { get; set; }
    public string Theme { get; set; }
    public bool? Sound { get; set; }
    public bool? Notifications { get; set; }
}

public sealed class ApiError
{
    public bool Ok { get; set; }
    public string Code { get; set; } = "error";
    public string Error { get; set; } = "";

    public ApiError() { }
    public ApiError(string code, string message) { Code = code; Error = message; }
}

public sealed class OkResult
{
    public bool Ok { get; set; } = true;
}
