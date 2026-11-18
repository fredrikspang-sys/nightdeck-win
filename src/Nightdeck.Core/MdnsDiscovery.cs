using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nightdeck.Core;

/// <summary>
/// One-shot mDNS browse for _musc._tcp, _musp._tcp and _musz._tcp (bluplay discovery).
/// The query goes out on every up IPv4 LAN interface, not only the default multicast route,
/// because VPN adapters (Tailscale, ZeroTier, OpenVPN) often own that route on Windows.
/// When NIGHTDECK_MDNS_ADDR is set to host:port, send the query there instead of 224.0.0.251:5353.
/// Failures return an empty list.
/// </summary>
public static class MdnsDiscovery
{
    const int DnsPort = 5353;
    static readonly string[] Services = ["_musc._tcp.local", "_musp._tcp.local", "_musz._tcp.local"];

    public static Task<IReadOnlyList<DiscoveredPlayer>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (timeout <= TimeSpan.Zero) return Task.FromResult<IReadOnlyList<DiscoveredPlayer>>([]);
        return Task.Run(() => Discover(timeout, ct), ct);
    }

    public static IReadOnlyList<DiscoveredPlayer> Parse(ReadOnlySpan<byte> message, string sourceAddress)
    {
        var bag = new Bag();
        Ingest(message, sourceAddress ?? "", bag);
        return Resolve(bag);
    }

    static IReadOnlyList<DiscoveredPlayer> Discover(TimeSpan timeout, CancellationToken ct)
    {
        var bag = new Bag();
        Socket? udp = null;
        try
        {
            var dest = Destination();
            udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(IPAddress.Any, 0));
            try { udp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255); }
            catch (SocketException) { /* unicast test hooks do not need it */ }

            byte[] query = BuildQuery();
            IReadOnlyList<IPAddress> ifaces = IPAddress.IsLoopback(dest.Address) || !IsMulticast(dest.Address)
                ? []
                : MulticastInterfaces();
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
                    SendAll(udp, query, dest, ifaces);
                    sent++;
                }
                long next = t0 + budget;
                if (sent == 1) next = Math.Min(next, t0 + 300);
                else if (sent == 2) next = Math.Min(next, t0 + 1000);
                int waitMs = (int)Math.Max(1, Math.Min(next - now, 250));
                if (!udp.Poll(waitMs * 1000, SelectMode.SelectRead)) continue;
                if (udp.Available <= 0) continue;
                EndPoint from = remote;
                var buf = new byte[Math.Min(udp.Available, 9000)];
                int n;
                try { n = udp.ReceiveFrom(buf, ref from); }
                catch (SocketException) { continue; }
                if (n <= 0) continue;
                string src = from is IPEndPoint ip && ip.Address.AddressFamily == AddressFamily.InterNetwork
                    ? ip.Address.ToString()
                    : "";
                Ingest(buf.AsSpan(0, n), src, bag);
            }
        }
        catch (OperationCanceledException) { /* return whatever arrived */ }
        catch (SocketException) { /* no network, or the port is unusable */ }
        finally { udp?.Dispose(); }
        return Resolve(bag);
    }

    static void SendAll(Socket udp, byte[] query, IPEndPoint dest, IReadOnlyList<IPAddress> ifaces)
    {
        if (ifaces.Count == 0)
        {
            try { udp.SendTo(query, dest); }
            catch (SocketException) { /* keep listening for anything already in flight */ }
            return;
        }
        foreach (var local in ifaces)
        {
            try
            {
                udp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                udp.SendTo(query, dest);
            }
            catch (SocketException) { /* this adapter cannot send multicast; try the next */ }
        }
    }

    static bool IsMulticast(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && b[0] >= 224 && b[0] <= 239;
    }

    /// <summary>True for an IPv4 address an mDNS query can usefully leave from (not loopback, not 169.254 link-local).</summary>
    public static bool IsUsableLocal(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) return false;
        var b = ip.GetAddressBytes();
        if (b[0] == 169 && b[1] == 254) return false;
        if (b[0] == 0 || b[0] >= 224) return false;
        return true;
    }

    /// <summary>IPv4 addresses of up, multicast-capable, non-loopback interfaces. Interfaces with a gateway come first.</summary>
    public static IReadOnlyList<IPAddress> MulticastInterfaces()
    {
        var withGateway = new List<IPAddress>();
        var others = new List<IPAddress>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                if (!nic.SupportsMulticast) continue;
                System.Net.NetworkInformation.IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); }
                catch (System.Net.NetworkInformation.NetworkInformationException) { continue; }
                bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    if (!IsUsableLocal(ua.Address)) continue;
                    if (withGateway.Contains(ua.Address) || others.Contains(ua.Address)) continue;
                    (gw ? withGateway : others).Add(ua.Address);
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { /* fall back to the default route */ }
        withGateway.AddRange(others);
        return withGateway.Count > 16 ? withGateway.GetRange(0, 16) : withGateway;
    }

    static IPEndPoint Destination()
    {
        var dest = new IPEndPoint(IPAddress.Parse("224.0.0.251"), DnsPort);
        string? ov = Environment.GetEnvironmentVariable("NIGHTDECK_MDNS_ADDR");
        if (string.IsNullOrWhiteSpace(ov)) return dest;
        int colon = ov.LastIndexOf(':');
        if (colon <= 0) return dest;
        if (!IPAddress.TryParse(ov[..colon].Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return dest;
        if (!int.TryParse(ov[(colon + 1)..].Trim(), out int port) || port is <= 0 or > 65535)
            return dest;
        return new IPEndPoint(ip, port);
    }

    static byte[] BuildQuery()
    {
        var q = new List<byte>(128) { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        int nq = 0;
        foreach (string svc in Services)
        {
            WriteName(q, svc);
            q.Add(0); q.Add(12);       // PTR
            q.Add(0x80); q.Add(1);     // IN, unicast-response bit
            nq++;
        }
        q[5] = (byte)nq;
        return q.ToArray();
    }

    static void WriteName(List<byte> q, string dotted)
    {
        foreach (string label in dotted.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var raw = Encoding.ASCII.GetBytes(label);
            q.Add((byte)raw.Length);
            q.AddRange(raw);
        }
        q.Add(0);
    }

    sealed class Rec
    {
        public string Inst = "";
        public string Label = "";
        public string Svc = "";
        public string Target = "";
        public string Src = "";
        public int Port;
    }

    sealed class Addr
    {
        public string Host = "";
        public string Ip = "";
    }

    sealed class Bag
    {
        public readonly List<Rec> Recs = [];
        public readonly List<Addr> Addrs = [];
    }

    static void Ingest(ReadOnlySpan<byte> m, string src, Bag bag)
    {
        if (m.Length < 12 || (m[2] & 0x80) == 0) return;
        int qd = (m[4] << 8) | m[5];
        int rr = ((m[6] << 8) | m[7]) + ((m[8] << 8) | m[9]) + ((m[10] << 8) | m[11]);
        int off = 12;
        for (int i = 0; i < qd; i++)
        {
            if (!ReadName(m, off, out off, out _, out _)) return;
            if (off + 4 > m.Length) return;
            off += 4;
        }
        for (int i = 0; i < rr && off < m.Length; i++)
        {
            if (!ReadName(m, off, out int nameEnd, out string owner, out string first)) return;
            off = nameEnd;
            if (off + 10 > m.Length) return;
            int type = (m[off] << 8) | m[off + 1];
            int rdl = (m[off + 8] << 8) | m[off + 9];
            int rd = off + 10;
            if (rd + rdl > m.Length) return;
            if (type == 12)
            {
                for (int s = 0; s < Services.Length; s++)
                {
                    if (!owner.Equals(Services[s], StringComparison.OrdinalIgnoreCase)) continue;
                    if (!ReadName(m, rd, out _, out string inst, out string lab)) break;
                    var rec = FindOrAdd(bag, inst, lab, src);
                    rec.Svc = Services[s][..5];
                }
            }
            else if (type == 33 && rdl > 6 && ReadName(m, rd + 6, out _, out string target, out _))
            {
                var rec = FindOrAdd(bag, owner, first, src);
                rec.Port = (m[rd + 4] << 8) | m[rd + 5];
                rec.Target = target;
            }
            else if (type == 1 && rdl == 4 && bag.Addrs.Count < 64)
            {
                bag.Addrs.Add(new Addr
                {
                    Host = owner,
                    Ip = m[rd] + "." + m[rd + 1] + "." + m[rd + 2] + "." + m[rd + 3],
                });
            }
            off = rd + rdl;
        }
    }

    static Rec FindOrAdd(Bag bag, string inst, string label, string src)
    {
        foreach (var rec in bag.Recs)
            if (rec.Inst.Equals(inst, StringComparison.OrdinalIgnoreCase))
                return rec;
        var created = new Rec { Inst = inst, Label = Clean(label), Src = src };
        if (bag.Recs.Count < 64) bag.Recs.Add(created);
        return created;
    }

    static IReadOnlyList<DiscoveredPlayer> Resolve(Bag bag)
    {
        var list = new List<DiscoveredPlayer>();
        foreach (string svc in Services)
        {
            string prefix = svc[..5];
            foreach (var rec in bag.Recs)
            {
                if (!rec.Svc.Equals(prefix, StringComparison.Ordinal)) continue;
                string host = rec.Src;
                foreach (var a in bag.Addrs)
                {
                    if (rec.Target.Length > 0 && a.Host.Equals(rec.Target, StringComparison.OrdinalIgnoreCase))
                    {
                        host = a.Ip;
                        break;
                    }
                }
                int port = rec.Port > 0 ? rec.Port : 11000;
                if (host.Length == 0 || port <= 0 || port > 65535) continue;
                bool seen = false;
                foreach (var have in list)
                {
                    if (have.Host == host && have.Port == port)
                    {
                        seen = true;
                        if (have.Name.Length == 0 && rec.Label.Length > 0)
                            list[list.IndexOf(have)] = new DiscoveredPlayer { Name = rec.Label, Host = host, Port = port, Service = rec.Svc };
                        break;
                    }
                }
                if (seen || list.Count >= 16) continue;
                list.Add(new DiscoveredPlayer
                {
                    Name = rec.Label.Length > 0 ? rec.Label : host,
                    Host = host,
                    Port = port,
                    Service = rec.Svc,
                });
            }
        }
        return list;
    }

    static string Clean(string label)
    {
        if (label.Length == 0) return "";
        if (label.Length > 127) label = label[..127];
        var chars = label.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] < 32) chars[i] = ' ';
        return new string(chars).Trim();
    }

    static bool ReadName(ReadOnlySpan<byte> m, int off, out int end, out string dotted, out string first)
    {
        end = 0;
        dotted = "";
        first = "";
        var sb = new StringBuilder();
        int jumps = 0;
        int labs = 0;
        while (true)
        {
            if ((uint)off >= (uint)m.Length) return false;
            int c = m[off];
            if (c == 0)
            {
                if (end == 0) end = off + 1;
                dotted = sb.ToString();
                return true;
            }
            if ((c & 0xC0) == 0xC0)
            {
                if (off + 1 >= m.Length || ++jumps > 32) return false;
                if (end == 0) end = off + 2;
                off = ((c & 0x3F) << 8) | m[off + 1];
                continue;
            }
            if ((c & 0xC0) != 0 || off + 1 + c > m.Length) return false;
            string label = Encoding.UTF8.GetString(m.Slice(off + 1, c));
            if (labs == 0) first = label.Length > 127 ? label[..127] : label;
            if (sb.Length > 0 && sb.Length < 290) sb.Append('.');
            if (sb.Length < 290) sb.Append(label.Length > 63 ? label[..63] : label);
            labs++;
            off += 1 + c;
        }
    }
}
