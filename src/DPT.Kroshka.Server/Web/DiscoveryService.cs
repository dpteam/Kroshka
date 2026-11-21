using System.Net;
using System.Net.Sockets;
using System.Text;
using DPT.Kroshka.Shared;

namespace DPT.Kroshka.Server.Web;

/// <summary>
/// Answers "is there a Kroshka server on this network?" probes.
///
/// A client cannot scan 254 addresses politely: a TCP connect per address is
/// slow, triggers firewall logs, and mostly times out. Instead the client sends
/// one UDP broadcast and every server that hears it answers with a tiny JSON
/// beacon. One packet out, one packet per server back, no scanning.
///
/// This is opt-in (server.discoverable). A server on a public address should not
/// announce itself to whatever is on the wire, and a family that wants privacy
/// can turn it off and type the address by hand.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    /// <summary>
    /// The exact request payload. A magic string, not an empty datagram, so
    /// unrelated broadcast traffic on the same port is ignored instead of
    /// answered.
    /// </summary>
    private const string ProbeText = "KROSHKA_DISCOVER_V1";

    private readonly KroshkaConfig _cfg;
    private UdpClient _socket;
    private CancellationTokenSource _cts;
    private Task _loop;

    public DiscoveryService(KroshkaConfig cfg)
    {
        _cfg = cfg;
    }

    public bool Started { get; private set; }

    public void Start()
    {
        if (!_cfg.Discoverable)
        {
            Log.Info("DISCOVERY", "disabled by config (server.discoverable = false)");
            return;
        }

        try
        {
            _cts = new CancellationTokenSource();

            // ReuseAddress lets a restart rebind while the old socket is still in
            // TIME_WAIT. ExclusiveAddressUse=false is the same thing said the other
            // way round and is required on Windows before ReuseAddress takes effect.
            _socket = new UdpClient();
            _socket.ExclusiveAddressUse = false;
            _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, _cfg.DiscoveryPort));
            _socket.EnableBroadcast = true;

            Started = true;
            _loop = Task.Run(() => ListenAsync(_cts.Token));

            Log.Info("DISCOVERY", $"listening for probes on udp/{_cfg.DiscoveryPort}");
        }
        catch (Exception ex)
        {
            // A busy port or a blocked socket must never stop the chat server.
            Log.Warn("DISCOVERY", "cannot start: " + ex.Message);
            Started = false;
        }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult result = await _socket.ReceiveAsync(token);
                string text = Encoding.UTF8.GetString(result.Buffer).Trim();

                if (!text.Equals(ProbeText, StringComparison.Ordinal)) continue;

                byte[] reply = Encoding.UTF8.GetBytes(BuildBeacon());
                await _socket.SendAsync(reply, reply.Length, result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                // One malformed packet must not kill the listener.
                Log.Debug("DISCOVERY", "receive error: " + ex.Message);

                try { await Task.Delay(250, token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// The beacon deliberately carries no user data: a name, a port and a version.
    /// Anyone on the network can read it, so it must not leak who is chatting.
    /// </summary>
    private string BuildBeacon()
    {
        return Json.Serialize(new
        {
            app = "kroshka",
            v = 1,
            name = _cfg.SiteName,
            port = _cfg.Port,
            version = Protocol.Version,
            authMode = _cfg.AuthMode.ToString().ToLowerInvariant()
        });
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }

        try { _socket?.Dispose(); } catch { }
        _socket = null;

        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts?.Dispose();
    }
}
