using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using DPT.Kroshka.Server.Storage;
using DPT.Kroshka.Shared;

namespace DPT.Kroshka.Server.Web;

/// <summary>
/// Live connections. One socket per app window; a person may have several (desktop,
/// phone, browser) and all of them receive the same events.
///
/// Backpressure policy: a slow client never blocks the sender. Its queue is bounded,
/// and when it overflows the socket is closed so the client reconnects and resyncs
/// instead of silently falling behind forever.
/// </summary>
public sealed class WsHub
{
    private const int MaxQueue = 512;
    private const int MaxFrameBytes = 1 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _byUser = new(StringComparer.Ordinal);
    private readonly KroshkaConfig _cfg;
    private readonly KroshkaStore _store;
    private readonly ConcurrentDictionary<string, DateTime> _typing = new(StringComparer.Ordinal);

    public WsHub(KroshkaConfig cfg, KroshkaStore store)
    {
        _cfg = cfg;
        _store = store;
    }

    public int ConnectionCount => _connections.Count;

    public List<string> OnlineUserIds()
    {
        return _byUser.Where(kv => !kv.Value.IsEmpty).Select(kv => kv.Key).ToList();
    }

    public bool IsOnline(string userId) => _byUser.TryGetValue(userId, out ConcurrentDictionary<string, byte> set) && !set.IsEmpty;

    // -------------------------------------------------------------- lifecycle

    public async Task AcceptAsync(HttpContext context, string userId)
    {
        WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        Connection conn = new(socket, userId);
        _connections[conn.Id] = conn;
        _byUser.GetOrAdd(userId, _ => new ConcurrentDictionary<string, byte>())[conn.Id] = 0;

        bool firstConnection = _byUser[userId].Count == 1;

        Log.Info("WS", $"connected {conn.Id} user={userId} ip={context.Connection.RemoteIpAddress}");

        try
        {
            if (firstConnection) BroadcastPresence(userId, true);

            // Auto-subscribe to everything this person can see, so unread badges and
            // new-message toasts work without the client asking per conversation.
            foreach (string id in AccessibleConversations(userId))
                conn.Subscriptions.TryAdd(id, 0);

            conn.Send(Json.Serialize(new
            {
                ok = true,
                userId,
                conversations = conn.Subscriptions.Keys.ToList()
            }), WsFrame.Make(Protocol.Events.Ready));

            await Task.WhenAll(PumpAsync(conn), ReceiveAsync(conn, context));
        }
        catch (Exception ex)
        {
            Log.Debug("WS", $"session {conn.Id} ended: {ex.Message}");
        }
        finally
        {
            await CloseAsync(conn);
        }
    }

    private async Task CloseAsync(Connection conn)
    {
        _connections.TryRemove(conn.Id, out _);
        if (_byUser.TryGetValue(conn.UserId, out ConcurrentDictionary<string, byte> set))
        {
            set.TryRemove(conn.Id, out _);
            if (set.IsEmpty)
            {
                _byUser.TryRemove(conn.UserId, out _);
                BroadcastPresence(conn.UserId, false);
            }
        }

        conn.Complete();
        Log.Info("WS", $"disconnected {conn.Id} user={conn.UserId}");
        await Task.CompletedTask;
    }

    private async Task PumpAsync(Connection conn)
    {
        try
        {
            while (conn.Socket.State == WebSocketState.Open)
            {
                string payload = await conn.DequeueAsync();
                if (payload == null) break;
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                await conn.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("WS", $"send failed for {conn.Id}: {ex.Message}");
        }
    }

    private async Task ReceiveAsync(Connection conn, HttpContext context)
    {
        byte[] buffer = new byte[16 * 1024];
        MemoryStream message = new();

        try
        {
            while (conn.Socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await conn.Socket.ReceiveAsync(buffer, CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try { await conn.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                    catch { }
                    break;
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxFrameBytes)
                {
                    Log.Warn("WS", $"oversized frame from {conn.UserId}, dropping");
                    break;
                }
                if (!result.EndOfMessage) continue;

                string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                HandleClientFrame(conn, text);
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug("WS", $"receive failed for {conn.Id}: {ex.Message}");
        }
        finally
        {
            message.Dispose();
        }
    }

    private void HandleClientFrame(Connection conn, string text)
    {
        WsFrame frame = WsFrame.Parse(text);
        if (frame == null || string.IsNullOrEmpty(frame.T)) return;

        switch (frame.T)
        {
            case Protocol.Commands.Ping:
                conn.Send("{}", WsFrame.Make(Protocol.Events.Pong));
                break;

            case Protocol.Commands.Subscribe:
                if (!string.IsNullOrEmpty(frame.ConversationId) && CanAccess(conn.UserId, frame.ConversationId))
                    conn.Subscriptions.TryAdd(frame.ConversationId, 0);
                break;

            case Protocol.Commands.Unsubscribe:
                if (!string.IsNullOrEmpty(frame.ConversationId))
                    conn.Subscriptions.TryRemove(frame.ConversationId, out _);
                break;

            case Protocol.Commands.Typing:
                HandleTyping(conn, frame);
                break;

            case Protocol.Commands.CallSignal:
                // Signalling is relayed verbatim; the server never inspects SDP.
                if (_cfg.CallsEnabled && !string.IsNullOrEmpty(frame.ConversationId))
                    BroadcastToConversation(frame.ConversationId, WsFrame.Make(Protocol.Events.CallSignal) with
                    {
                        UserId = conn.UserId,
                        Data = frame.Data
                    }, exceptConnectionId: conn.Id);
                break;
        }
    }

    private void HandleTyping(Connection conn, WsFrame frame)
    {
        if (!_cfg.FeaturesTyping || string.IsNullOrEmpty(frame.ConversationId)) return;
        if (!CanAccess(conn.UserId, frame.ConversationId)) return;

        string key = conn.UserId + "|" + frame.ConversationId;

        if (frame.On)
        {
            _typing[key] = DateTime.UtcNow.AddSeconds(6);
        }
        else
        {
            _typing.TryRemove(key, out _);
        }

        BroadcastToConversation(frame.ConversationId, WsFrame.Make(Protocol.Events.Typing) with
        {
            ConversationId = frame.ConversationId,
            UserId = conn.UserId,
            On = frame.On
        }, exceptUserId: conn.UserId);
    }

    // ---------------------------------------------------------------- sending

    public void SendToUser(string userId, WsFrame frame)
    {
        string payload = frame.ToJson();
        if (!_byUser.TryGetValue(userId, out ConcurrentDictionary<string, byte> set)) return;

        foreach (string connId in set.Keys)
            if (_connections.TryGetValue(connId, out Connection conn))
                conn.Send(payload, frame);
    }

    public void BroadcastToConversation(string conversationId, WsFrame frame, string exceptUserId = null, string exceptConnectionId = null)
    {
        string payload = frame.ToJson();
        foreach (Connection conn in _connections.Values)
        {
            if (conn.Id == exceptConnectionId) continue;
            if (exceptUserId != null && conn.UserId == exceptUserId) continue;
            if (!conn.Subscriptions.ContainsKey(conversationId)) continue;
            conn.Send(payload, frame);
        }
    }

    public void BroadcastAll(WsFrame frame)
    {
        string payload = frame.ToJson();
        foreach (Connection conn in _connections.Values) conn.Send(payload, frame);
    }

    private void BroadcastPresence(string userId, bool online)
    {
        if (!_cfg.FeaturesPresence) return;
        BroadcastAll(WsFrame.Make(Protocol.Events.Presence) with { UserId = userId, On = online });
    }

    public void SendError(string userId, string message)
    {
        SendToUser(userId, WsFrame.Make(Protocol.Events.Error) with { Text = message });
    }

    /// <summary>Called by the store when a room is deleted, so nobody keeps a ghost.</summary>
    public void ForgetConversation(string conversationId)
    {
        foreach (Connection conn in _connections.Values)
            conn.Subscriptions.TryRemove(conversationId, out _);
    }

    public void SubscribeUser(string userId, string conversationId)
    {
        if (!_byUser.TryGetValue(userId, out ConcurrentDictionary<string, byte> set)) return;
        foreach (string connId in set.Keys)
            if (_connections.TryGetValue(connId, out Connection conn))
                conn.Subscriptions.TryAdd(conversationId, 0);
    }

    /// <summary>
    /// Subscribes every live connection to a conversation. Used when a public room is
    /// created: it is visible to all, so all must start receiving its events.
    /// </summary>
    public void SubscribeAll(string conversationId)
    {
        foreach (Connection conn in _connections.Values)
            conn.Subscriptions.TryAdd(conversationId, 0);
    }

    public void UnsubscribeUser(string userId, string conversationId)
    {
        if (!_byUser.TryGetValue(userId, out ConcurrentDictionary<string, byte> set)) return;
        foreach (string connId in set.Keys)
            if (_connections.TryGetValue(connId, out Connection conn))
                conn.Subscriptions.TryRemove(conversationId, out _);
    }

    // ----------------------------------------------------------------- access

    public bool CanAccess(string userId, string conversationId)
    {
        if (conversationId.StartsWith("dm:", StringComparison.Ordinal))
        {
            Direct dm = _store.GetDm(conversationId);
            return dm != null && (dm.UserA == userId || dm.UserB == userId);
        }

        Room room = _store.GetRoom(conversationId);
        return room != null && (!room.IsPrivate || room.Members.Contains(userId));
    }

    public IEnumerable<string> AccessibleConversations(string userId)
    {
        List<string> result = new();

        foreach (Room room in _store.AllRooms())
        {
            if (room.Archived) continue;
            if (room.IsPrivate && !room.Members.Contains(userId)) continue;
            result.Add(room.Id);
        }

        foreach (Direct dm in _store.AllDms())
            if (dm.UserA == userId || dm.UserB == userId) result.Add(dm.Id);

        return result;
    }

    /// <summary>Clears typing flags that were never explicitly cancelled.</summary>
    public void SweepTyping()
    {
        DateTime now = DateTime.UtcNow;
        foreach (KeyValuePair<string, DateTime> kv in _typing)
        {
            if (kv.Value > now) continue;
            if (!_typing.TryRemove(kv.Key, out _)) continue;

            int bar = kv.Key.IndexOf('|');
            if (bar <= 0) continue;

            string userId = kv.Key[..bar];
            string conversationId = kv.Key[(bar + 1)..];
            BroadcastToConversation(conversationId, WsFrame.Make(Protocol.Events.Typing) with
            {
                ConversationId = conversationId,
                UserId = userId,
                On = false
            }, exceptUserId: userId);
        }
    }

    // ------------------------------------------------------------ connection

    private sealed class Connection
    {
        private readonly ConcurrentQueue<string> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);
        private int _queued;
        private volatile bool _dead;

        public string Id { get; } = Crypto.NewId(8);
        public string UserId { get; }
        public WebSocket Socket { get; }
        public ConcurrentDictionary<string, byte> Subscriptions { get; } = new(StringComparer.Ordinal);
        public DateTime ConnectedUtc { get; } = DateTime.UtcNow;

        public Connection(WebSocket socket, string userId)
        {
            Socket = socket;
            UserId = userId;
        }

        public void Send(string payload, WsFrame frame)
        {
            if (_dead) return;
            if (Interlocked.Increment(ref _queued) > MaxQueue)
            {
                // The client cannot keep up. Killing the socket is honest: it will
                // reconnect and fetch the history it missed.
                _dead = true;
                Log.Warn("WS", $"queue overflow for {Id}, closing");
                try { Socket.Abort(); } catch { }
                Interlocked.Decrement(ref _queued);
                return;
            }

            _queue.Enqueue(payload);
            try { _signal.Release(); } catch { }
        }

        public async Task<string> DequeueAsync()
        {
            while (!_dead)
            {
                if (_queue.TryDequeue(out string payload))
                {
                    Interlocked.Decrement(ref _queued);
                    return payload;
                }
                await _signal.WaitAsync(TimeSpan.FromSeconds(30));
            }
            return null;
        }

        public void Complete()
        {
            _dead = true;
            try { _signal.Release(); } catch { }
        }
    }
}
