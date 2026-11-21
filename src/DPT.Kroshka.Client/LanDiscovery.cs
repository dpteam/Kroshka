using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DPT.Kroshka.Client;

/// <summary>One server that answered a discovery probe.</summary>
public sealed class FoundServer
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Version { get; set; } = "";
    public string AuthMode { get; set; } = "";

    /// <summary>Canonical base URL the settings screen can store directly.</summary>
    public string Url => $"http://{Host}:{Port}";

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? Host : $"{Name} — {Host}:{Port}";
}

/// <summary>
/// Finds Kroshka servers on the local network.
///
/// One UDP broadcast goes out, every server that hears it replies with a small
/// beacon. That is dramatically cheaper and less suspicious than probing 254 TCP
/// ports, and it works across the same subnets a family actually shares.
///
/// The scan is bounded and silent: it never throws, and it returns whatever it
/// collected within the timeout, which may be nothing.
/// </summary>
public static class LanDiscovery
{
    private const string ProbeText = "KROSHKA_DISCOVER_V1";

    /// <summary>
    /// Broadcasts a probe and collects replies.
    /// </summary>
    /// <param name="timeout">How long to wait for answers.</param>
    /// <param name="discoveryPort">UDP port to probe; matches the server's.</param>
    /// <param name="cancellationToken">Lets the UI cancel when the window closes.</param>
    public static async Task<List<FoundServer>> ScanAsync(
        TimeSpan timeout,
        int discoveryPort = 6767,
        CancellationToken cancellationToken = default)
    {
        List<FoundServer> found = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        UdpClient socket = null;

        try
        {
            socket = new UdpClient
            {
                EnableBroadcast = true
            };

            // Bind to any free port: replies come back to whatever we sent from,
            // and using the server's port would clash with a local server.
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            byte[] probe = Encoding.UTF8.GetBytes(ProbeText);

            // Broadcast to the limited address, and also to each interface's own
            // subnet broadcast. The limited broadcast is dropped by some routers
            // and by Windows on a multi-homed machine, so both are needed.
            foreach (IPEndPoint target in BroadcastTargets(discoveryPort))
            {
                try { await socket.SendAsync(probe, probe.Length, target); }
                catch { /* one interface failing must not stop the scan */ }
            }

            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);

            while (!deadline.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(deadline.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }

                FoundServer server = Parse(result.Buffer, result.RemoteEndPoint);
                if (server == null) continue;

                if (seen.Add(server.Url)) found.Add(server);
            }
        }
        catch
        {
            // Discovery is a convenience; any failure just means "nothing found".
        }
        finally
        {
            try { socket?.Dispose(); } catch { }
        }

        // One server answers from every interface it has: LAN, ZeroTier, a VM
        // bridge. They are the same server, so collapse them to one row and keep
        // the address most likely to work for someone on the LAN.
        return found
            .GroupBy(s => s.Name + "|" + s.Port, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(s => AddressRank(s.Host)).First())
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Lower is better. A plain private LAN address is what a family member on
    /// the same Wi-Fi can actually reach; ZeroTier and virtual bridges come next.
    /// </summary>
    private static int AddressRank(string host)
    {
        if (!IPAddress.TryParse(host, out IPAddress address)) return 9;

        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return 8;

        if (bytes[0] == 192 && bytes[1] == 168) return 0;
        if (bytes[0] == 10) return 1;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return 2;
        if (bytes[0] == 169 && bytes[1] == 254) return 7;

        return 5;
    }    private static IEnumerable<IPEndPoint> BroadcastTargets(int port)
    {
        // Always try the limited broadcast first: on a simple flat LAN it is enough.
        yield return new IPEndPoint(IPAddress.Broadcast, port);

        System.Net.NetworkInformation.NetworkInterface[] interfaces;
        try
        {
            interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            yield break;
        }

        foreach (System.Net.NetworkInformation.NetworkInterface nic in interfaces)
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

            System.Net.NetworkInformation.IPInterfaceProperties properties;
            try { properties = nic.GetIPProperties(); }
            catch { continue; }

            foreach (System.Net.NetworkInformation.UnicastIPAddressInformation address in properties.UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                byte[] bytes = address.Address.GetAddressBytes();
                byte[] mask = address.IPv4Mask?.GetAddressBytes();
                if (mask == null || mask.Length != 4) continue;

                byte[] broadcast = new byte[4];
                for (int i = 0; i < 4; i++) broadcast[i] = (byte)(bytes[i] | ~mask[i]);

                yield return new IPEndPoint(new IPAddress(broadcast), port);
            }
        }
    }

    private static FoundServer Parse(byte[] data, IPEndPoint from)
    {
        try
        {
            // 169.254.x.x is the "no DHCP" address Windows invents when an
            // interface is not really on a network. A server that answers from
            // one of those is not reachable by anyone else, so listing it would
            // only mislead.
            byte[] address = from.Address.GetAddressBytes();
            if (address.Length == 4 && address[0] == 169 && address[1] == 254) return null;

            string text = Encoding.UTF8.GetString(data).Trim();
            if (!text.StartsWith('{')) return null;

            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("app", out JsonElement app)) return null;
            if (!string.Equals(app.GetString(), "kroshka", StringComparison.OrdinalIgnoreCase)) return null;

            return new FoundServer
            {
                Name = ReadString(root, "name"),
                Host = from.Address.ToString(),
                Port = ReadInt(root, "port", 6767),
                Version = ReadString(root, "version"),
                AuthMode = ReadString(root, "authMode")
            };
        }
        catch
        {
            return null;
        }
    }

    private static string ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static int ReadInt(JsonElement element, string name, int fallback)
    {
        return element.TryGetProperty(name, out JsonElement value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out int number)
            ? number
            : fallback;
    }
}
