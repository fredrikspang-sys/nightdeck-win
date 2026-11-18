using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Nightdeck.Core;

/// <summary>
/// Lenbrook Service Discovery Protocol (UDP port 11430).
/// The query is sent to the directed broadcast of every usable IPv4 interface,
/// not only the default route, so a VPN adapter cannot hide the LAN player.
/// When NIGHTDECK_LSDP_ADDR is set to host:port, send the query only there.
/// </summary>
public static class LsdpDiscovery
{
    public const int Port = 11430;
    static readonly byte[] Query =
    [
        0x06, (byte)'L', (byte)'S', (byte)'D', (byte)'P', 0x01,
        0x05, 0x51, 0x01, 0xFF, 0xFF,
    ];

    public static byte[] QueryPacket() => (byte[])Query.Clone();

    public static DiscoveredPlayer? Parse(ReadOnlySpan<byte> message, string sourceAddress)
    {
        var cur = message;
        if (!Take(ref cur, 1, out var hl) || hl[0] != 6) return null;
        if (!Take(ref cur, 4, out var magic) || !magic.SequenceEqual("LSDP"u8)) return null;
        if (!Take(ref cur, 1, out var ver) || ver[0] != 1) return null;
        if (!Take(ref cur, 1, out _) || !Take(ref cur, 1, out var typ) || typ[0] != 0x41) return null;
        if (!LenPrefixed(ref cur, out _)) return null;
        if (!LenPrefixed(ref cur, out var ip)) return null;

        string host = "";
        if (ip.Length == 4 && (ip[0] | ip[1] | ip[2] | ip[3]) != 0)
            host = ip[0] + "." + ip[1] + "." + ip[2] + "." + ip[3];
        if (host.Length == 0) host = sourceAddress ?? "";
        if (host.Length == 0) return null;

        if (!Take(ref cur, 1, out var nrecb)) return null;
        int nrec = nrecb[0];
        string name = "";
        int port = 0;
        bool physical = false;
        for (int r = 0; r < nrec; r++)
        {
            if (!Take(ref cur, 2, out var clsb)) return null;
            int cls = (clsb[0] << 8) | clsb[1];
            bool isPhysical = cls is 1 or 2 or 3 or 6 or 8;
            if (!Take(ref cur, 1, out var nfb)) return null;
            int nf = nfb[0];
            string recName = "";
            int recPort = 0;
            for (int f = 0; f < nf; f++)
            {
                if (!LenPrefixed(ref cur, out var key) || !LenPrefixed(ref cur, out var val)) return null;
                string k = Encoding.UTF8.GetString(key);
                string v = Encoding.UTF8.GetString(val);
                if (k == "name" && recName.Length == 0) recName = v;
                if (k == "port" && recPort == 0 && int.TryParse(v, out int p) && p is > 0 and <= 65535) recPort = p;
            }
            if (!isPhysical) continue;
            physical = true;
            if (name.Length == 0) name = recName;
            if (port == 0) port = recPort;
        }
        if (!physical) return null;
        if (name.Length == 0) name = host;
        if (port == 0) port = 11000;
        return new DiscoveredPlayer { Name = name, Host = host, Port = port, Service = "lsdp" };
    }

    public static Task<IReadOnlyList<DiscoveredPlayer>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (timeout <= TimeSpan.Zero) return Task.FromResult<IReadOnlyList<DiscoveredPlayer>>([]);
        return Task.Run(() => Discover(timeout, ct), ct);
    }

    static IReadOnlyList<DiscoveredPlayer> Discover(TimeSpan timeout, CancellationToken ct)
    {
        var found = new List<DiscoveredPlayer>();
        Socket? udp = null;
        try
        {
            var dests = Destinations();
            if (dests.Count == 0) return found;
            udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try { udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); }
            catch (SocketException) { }
            try { udp.ExclusiveAddressUse = false; }
            catch (SocketException) { }
            try { udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true); }
            catch (SocketException) { /* the unicast test hook does not need it */ }
            // Players answer on UDP 11430, not the ephemeral source port.
            try { udp.Bind(new IPEndPoint(IPAddress.Any, Port)); }
            catch (SocketException)
            {
                udp.Bind(new IPEndPoint(IPAddress.Any, 0));
            }

            long t0 = Environment.TickCount64;
            long budget = (long)timeout.TotalMilliseconds;
            int sent = 0;
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (!ct.IsCancellationRequested)
            {
                long now = Environment.TickCount64;
                if (now - t0 >= budget) break;
                if (sent == 0 || (sent == 1 && now - t0 >= 300) || (sent == 2 && now - t0 >= 1000))
                {
                    foreach (var dest in dests)
                    {
                        try { udp.SendTo(Query, dest); }
                        catch (SocketException) { }
                    }
                    sent++;
                }
                long next = t0 + budget;
                if (sent == 1) next = Math.Min(next, t0 + 300);
                else if (sent == 2) next = Math.Min(next, t0 + 1000);
                int waitMs = (int)Math.Max(1, Math.Min(next - now, 250));
                if (!udp.Poll(waitMs * 1000, SelectMode.SelectRead)) continue;
                if (udp.Available <= 0) continue;
                EndPoint from = remote;
                var buf = new byte[Math.Min(Math.Max(udp.Available, 1), 9000)];
                int n;
                try { n = udp.ReceiveFrom(buf, ref from); }
                catch (SocketException) { continue; }
                if (n <= 0) continue;
                string src = from is IPEndPoint ip && ip.Address.AddressFamily == AddressFamily.InterNetwork
                    ? ip.Address.ToString()
                    : "";
                var player = Parse(buf.AsSpan(0, n), src);
                if (player != null && found.Count < 16 && !found.Any(h => h.Host == player.Host && h.Port == player.Port))
                    found.Add(player);
            }
        }
        catch (OperationCanceledException) { /* return whatever arrived */ }
        catch (SocketException) { /* no network, or the port is unusable */ }
        finally { udp?.Dispose(); }
        return found;
    }

    public static IReadOnlyList<IPEndPoint> BroadcastTargets()
    {
        var list = new List<IPEndPoint>();
        var seen = new HashSet<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); }
                catch (NetworkInformationException) { continue; }
                foreach (var ua in props.UnicastAddresses)
                {
                    if (!MdnsDiscovery.IsUsableLocal(ua.Address)) continue;
                    IPAddress mask;
                    try { mask = ua.IPv4Mask; }
                    catch (Exception) { continue; }
                    if (mask == null || mask.AddressFamily != AddressFamily.InterNetwork) continue;
                    var bcast = Broadcast(ua.Address, mask);
                    var ep = new IPEndPoint(bcast, Port);
                    string key = ep.ToString();
                    if (!seen.Add(key)) continue;
                    list.Add(ep);
                    if (list.Count >= 16) return list;
                }
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }

    static IPAddress Broadcast(IPAddress address, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        var b = new byte[4];
        for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
        return new IPAddress(b);
    }

    static IReadOnlyList<IPEndPoint> Destinations()
    {
        var hook = Hook();
        if (hook != null) return [hook];
        return BroadcastTargets();
    }

    static IPEndPoint? Hook()
    {
        string? ov = Environment.GetEnvironmentVariable("NIGHTDECK_LSDP_ADDR");
        if (string.IsNullOrWhiteSpace(ov)) return null;
        int colon = ov.LastIndexOf(':');
        if (colon <= 0) return null;
        if (!IPAddress.TryParse(ov[..colon].Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return null;
        if (!int.TryParse(ov[(colon + 1)..].Trim(), out int port) || port is <= 0 or > 65535)
            return null;
        return new IPEndPoint(ip, port);
    }

    static bool Take(ref ReadOnlySpan<byte> cur, int n, out ReadOnlySpan<byte> slice)
    {
        if (cur.Length < n) { slice = default; return false; }
        slice = cur[..n];
        cur = cur[n..];
        return true;
    }

    static bool LenPrefixed(ref ReadOnlySpan<byte> cur, out ReadOnlySpan<byte> slice)
    {
        if (!Take(ref cur, 1, out var lenb)) { slice = default; return false; }
        return Take(ref cur, lenb[0], out slice);
    }
}
