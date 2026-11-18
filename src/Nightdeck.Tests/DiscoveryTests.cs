using System.Net;
using System.Net.Sockets;
using System.Text;
using Nightdeck.Core;
using Nightdeck.Mock;

namespace Nightdeck.Tests;

static class DiscoveryTests
{
    public static void Run()
    {
        TestHarness.Equal(MdnsDiscovery.Parse([], "192.0.2.1").Count, 0, "empty packet");
        TestHarness.Equal(MdnsDiscovery.Parse(new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0 }, "192.0.2.1").Count, 0, "truncated packet");
        TestHarness.Equal(MdnsDiscovery.Parse(QueryPacket(), "192.0.2.1").Count, 0, "query is not a response");

        var found = MdnsDiscovery.Parse(Uncompressed(), "198.51.100.9");
        TestHarness.Equal(found.Count, 1, "one player");
        if (found.Count == 1)
        {
            TestHarness.Equal(found[0].Name, "Living-Room", "instance label");
            TestHarness.Equal(found[0].Host, "192.0.2.10", "A record address");
            TestHarness.Equal(found[0].Port, 11000, "SRV port");
            TestHarness.Equal(found[0].Service, "_musc", "service");
        }

        var fallback = MdnsDiscovery.Parse(WithoutAddress(), "198.51.100.9");
        TestHarness.Equal(fallback.Count, 1, "source fallback count");
        if (fallback.Count == 1)
            TestHarness.Equal(fallback[0].Host, "198.51.100.9", "packet source when no A");

        var ordered = MdnsDiscovery.Parse(MuszThenMusc(), "203.0.113.5");
        TestHarness.Equal(ordered.Count, 2, "two services");
        if (ordered.Count == 2)
        {
            TestHarness.Equal(ordered[0].Service, "_musc", "musc before musz");
            TestHarness.Equal(ordered[0].Name, "Den", "musc name");
            TestHarness.Equal(ordered[1].Name, "Hall", "musz name");
        }

        var compressed = MdnsDiscovery.Parse(Compressed(), "203.0.113.8");
        TestHarness.Equal(compressed.Count, 1, "compressed names");
        if (compressed.Count == 1)
        {
            TestHarness.Equal(compressed[0].Name, "Living-Room", "compressed label");
            TestHarness.Equal(compressed[0].Host, "192.0.2.10", "compressed A");
            TestHarness.Equal(compressed[0].Port, 11000, "compressed port");
        }

        var utf8 = MdnsDiscovery.Parse(Utf8Instance(), "198.51.100.9");
        TestHarness.Equal(utf8.Count, 1, "utf-8 instance");
        if (utf8.Count == 1)
            TestHarness.Equal(utf8[0].Name, "Skuggsjá", "utf-8 instance label");

        var none = MdnsDiscovery.DiscoverAsync(TimeSpan.Zero).GetAwaiter().GetResult();
        TestHarness.Equal(none.Count, 0, "zero timeout skips the network");

        InterfaceRules();
        UnicastResponder();
        ScanMock().GetAwaiter().GetResult();
    }

    static void InterfaceRules()
    {
        TestHarness.Check(!MdnsDiscovery.IsUsableLocal(IPAddress.Parse("169.254.1.7")), "link-local adapter is not used for mDNS");
        TestHarness.Check(!MdnsDiscovery.IsUsableLocal(IPAddress.Loopback), "loopback is not used for mDNS");
        TestHarness.Check(MdnsDiscovery.IsUsableLocal(IPAddress.Parse("192.168.1.20")), "LAN address is used for mDNS");
        TestHarness.Check(MdnsDiscovery.MulticastInterfaces().All(MdnsDiscovery.IsUsableLocal), "every multicast interface is usable");

        TestHarness.Check(SubnetScan.IsPrivateLan(IPAddress.Parse("192.168.1.20")), "192.168/16 is LAN");
        TestHarness.Check(SubnetScan.IsPrivateLan(IPAddress.Parse("172.20.0.5")), "172.16/12 is LAN");
        TestHarness.Check(!SubnetScan.IsPrivateLan(IPAddress.Parse("100.64.0.10")), "Tailscale CGNAT is not scanned");
        TestHarness.Check(!SubnetScan.IsPrivateLan(IPAddress.Parse("8.8.8.8")), "public address is not scanned");

        var hosts = SubnetScan.HostsIn24(IPAddress.Parse("192.168.1.20"));
        TestHarness.Equal(hosts.Count, 253, "/24 host count without self");
        TestHarness.Equal(hosts[0].ToString(), "192.168.1.1", "/24 first host");
        TestHarness.Equal(hosts[^1].ToString(), "192.168.1.254", "/24 last host");
        TestHarness.Check(!hosts.Any(h => h.ToString() == "192.168.1.20"), "/24 skips own address");
    }

    static void UnicastResponder()
    {
        using var fake = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        fake.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)fake.LocalEndPoint!).Port;
        var reply = Uncompressed();
        var responder = Task.Run(() =>
        {
            var buf = new byte[2048];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            if (!fake.Poll(3_000_000, SelectMode.SelectRead)) return false;
            int n = fake.ReceiveFrom(buf, ref from);
            bool asksMusc = Encoding.ASCII.GetString(buf, 0, n).Contains("_musc", StringComparison.Ordinal);
            fake.SendTo(reply, from);
            return asksMusc;
        });
        string? old = Environment.GetEnvironmentVariable("NIGHTDECK_MDNS_ADDR");
        Environment.SetEnvironmentVariable("NIGHTDECK_MDNS_ADDR", "127.0.0.1:" + port);
        try
        {
            var found = PlayerDiscovery.DiscoverAsync(TimeSpan.FromMilliseconds(800), scanFallback: false).GetAwaiter().GetResult();
            TestHarness.Check(responder.GetAwaiter().GetResult(), "query asks for _musc");
            TestHarness.Equal(found.Count, 1, "unicast responder found");
            if (found.Count == 1)
            {
                TestHarness.Equal(found[0].Name, "Living-Room", "unicast responder name");
                TestHarness.Equal(found[0].Host, "192.0.2.10", "unicast responder A record");
            }
        }
        finally { Environment.SetEnvironmentVariable("NIGHTDECK_MDNS_ADDR", old); }
    }

    static async Task ScanMock()
    {
        using var mock = new MockPlayer { Name = "Scan Room" };
        mock.Start();
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        int closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        int silentPort = ((IPEndPoint)silent.LocalEndpoint).Port;
        var accept = Task.Run(async () =>
        {
            try { using var c = await silent.AcceptTcpClientAsync(); c.Close(); } catch { }
        });
        try
        {
            var targets = new List<IPEndPoint>
            {
                new(IPAddress.Loopback, closedPort),
                new(IPAddress.Loopback, mock.Port),
                new(IPAddress.Loopback, silentPort),
            };
            var found = await SubnetScan.ProbeAsync(targets, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(8));
            TestHarness.Equal(found.Count, 1, "scan finds only the BluOS mock");
            if (found.Count == 1)
            {
                TestHarness.Equal(found[0].Name, "Scan Room", "scan uses SyncStatus name");
                TestHarness.Equal(found[0].Host, "127.0.0.1", "scan host");
                TestHarness.Equal(found[0].Port, mock.Port, "scan port");
                TestHarness.Equal(found[0].Service, "scan", "scan service tag");
            }
            TestHarness.Equal(mock.Count("/SyncStatus"), 1, "scan sends one GET /SyncStatus");
            TestHarness.Equal(mock.Count("/Status"), 0, "scan sends no /Status");
            TestHarness.Equal(mock.Count("/Volume"), 0, "scan sends no volume command");
            TestHarness.Equal(mock.Count("/Play") + mock.Count("/Pause") + mock.Count("/Stop"), 0, "scan sends no transport command");
        }
        finally
        {
            silent.Stop();
            await accept;
        }
    }

    static byte[] Utf8Instance() => Packet(
        PtrUtf8("_musc._tcp.local", "Skuggsjá._musc._tcp.local"),
        SrvUtf8("Skuggsjá._musc._tcp.local", 11000, "player.local"),
        A("player.local", 192, 0, 2, 10));

    static byte[] QueryPacket()
    {
        var b = new List<byte> { 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        AddName(b, "_musc._tcp.local");
        b.Add(0); b.Add(12); b.Add(0x80); b.Add(1);
        return b.ToArray();
    }

    static byte[] Uncompressed() => Packet(
        Ptr("_musc._tcp.local", "Living-Room._musc._tcp.local"),
        Srv("Living-Room._musc._tcp.local", 11000, "Living-Room.local"),
        A("Living-Room.local", 192, 0, 2, 10));

    static byte[] WithoutAddress() => Packet(
        Ptr("_musc._tcp.local", "Living-Room._musc._tcp.local"),
        Srv("Living-Room._musc._tcp.local", 11000, "Living-Room.local"));

    static byte[] MuszThenMusc() => Packet(
        Ptr("_musz._tcp.local", "Hall._musz._tcp.local"),
        Srv("Hall._musz._tcp.local", 11000, "hall.local"),
        A("hall.local", 203, 0, 113, 20),
        Ptr("_musc._tcp.local", "Den._musc._tcp.local"),
        Srv("Den._musc._tcp.local", 11000, "den.local"),
        A("den.local", 203, 0, 113, 21));

    static byte[] Compressed()
    {
        var b = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, 3, 0, 0, 0, 0 };
        int svcAt = b.Count;
        AddName(b, "_musc._tcp.local");
        b.Add(0); b.Add(12); b.Add(0x80); b.Add(1);
        b.Add(0); b.Add(0); b.Add(0); b.Add(120);
        int rdlenAt = b.Count;
        b.Add(0); b.Add(0);
        int instAt = b.Count;
        AddLabel(b, "Living-Room");
        b.Add(0xC0); b.Add((byte)svcAt);
        int rdLen = b.Count - instAt;
        b[rdlenAt] = (byte)(rdLen >> 8);
        b[rdlenAt + 1] = (byte)rdLen;

        b.Add(0xC0); b.Add((byte)instAt);
        b.Add(0); b.Add(33); b.Add(0x80); b.Add(1);
        b.Add(0); b.Add(0); b.Add(0); b.Add(120);
        int hostAt = b.Count + 8;
        var host = new List<byte>();
        AddName(host, "player.local");
        var srvRdata = new List<byte> { 0, 0, 0, 0, 0x2A, 0xF8 };
        srvRdata.AddRange(host);
        b.Add((byte)(srvRdata.Count >> 8));
        b.Add((byte)srvRdata.Count);
        int hostName = b.Count + 6;
        b.AddRange(srvRdata);
        if (hostAt != hostName) throw new InvalidOperationException("host offset " + hostName);

        b.Add(0xC0); b.Add((byte)hostName);
        b.Add(0); b.Add(1); b.Add(0x80); b.Add(1);
        b.Add(0); b.Add(0); b.Add(0); b.Add(120);
        b.Add(0); b.Add(4);
        b.Add(192); b.Add(0); b.Add(2); b.Add(10);
        return b.ToArray();
    }

    static byte[] Packet(params byte[][] records)
    {
        var b = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, (byte)records.Length, 0, 0, 0, 0 };
        foreach (var r in records) b.AddRange(r);
        return b.ToArray();
    }

    static byte[] Ptr(string service, string instance)
    {
        var rdata = new List<byte>();
        AddName(rdata, instance);
        return Rr(service, 12, rdata.ToArray());
    }

    static byte[] Srv(string owner, int port, string target)
    {
        var rdata = new List<byte> { 0, 0, 0, 0, (byte)(port >> 8), (byte)port };
        AddName(rdata, target);
        return Rr(owner, 33, rdata.ToArray());
    }

    static byte[] A(string owner, byte a, byte b, byte c, byte d) => Rr(owner, 1, [a, b, c, d]);

    static byte[] PtrUtf8(string service, string instance)
    {
        var rdata = new List<byte>();
        AddNameUtf8(rdata, instance);
        return Rr(service, 12, rdata.ToArray());
    }

    static byte[] SrvUtf8(string owner, int port, string target)
    {
        var rdata = new List<byte> { 0, 0, 0, 0, (byte)(port >> 8), (byte)port };
        AddName(rdata, target);
        return RrUtf8(owner, 33, rdata.ToArray());
    }

    static byte[] Rr(string owner, ushort type, byte[] rdata)
    {
        var b = new List<byte>();
        AddName(b, owner);
        b.Add((byte)(type >> 8)); b.Add((byte)type);
        b.Add(0x80); b.Add(1);
        b.Add(0); b.Add(0); b.Add(0); b.Add(120);
        b.Add((byte)(rdata.Length >> 8)); b.Add((byte)rdata.Length);
        b.AddRange(rdata);
        return b.ToArray();
    }

    static void AddName(List<byte> b, string dotted)
    {
        foreach (string label in dotted.Split('.')) AddLabel(b, label);
        b.Add(0);
    }

    static void AddLabel(List<byte> b, string label)
    {
        var raw = Encoding.ASCII.GetBytes(label);
        b.Add((byte)raw.Length);
        b.AddRange(raw);
    }

    static byte[] RrUtf8(string owner, ushort type, byte[] rdata)
    {
        var b = new List<byte>();
        AddNameUtf8(b, owner);
        b.Add((byte)(type >> 8)); b.Add((byte)type);
        b.Add(0x80); b.Add(1);
        b.Add(0); b.Add(0); b.Add(0); b.Add(120);
        b.Add((byte)(rdata.Length >> 8)); b.Add((byte)rdata.Length);
        b.AddRange(rdata);
        return b.ToArray();
    }

    static void AddNameUtf8(List<byte> b, string dotted)
    {
        var labels = dotted.Split('.');
        for (int i = 0; i < labels.Length; i++)
        {
            var raw = i == 0 ? Encoding.UTF8.GetBytes(labels[i]) : Encoding.ASCII.GetBytes(labels[i]);
            b.Add((byte)raw.Length);
            b.AddRange(raw);
        }
        b.Add(0);
    }
}
