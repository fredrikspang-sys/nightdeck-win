using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Nightdeck.Core;

/// <summary>
/// Fallback when mDNS finds nothing: TCP-probe port 11000 on the /24 of each LAN interface
/// that has an IPv4 gateway, then confirm each open port with one read-only GET /SyncStatus.
/// Nothing else is ever sent to a host.
/// </summary>
public static class SubnetScan
{
    public const int DefaultPort = 11000;

    /// <summary>Hosts .1 to .254 in the /24 around <paramref name="local"/>, without <paramref name="local"/> itself.</summary>
    public static IReadOnlyList<IPAddress> HostsIn24(IPAddress local)
    {
        if (local.AddressFamily != AddressFamily.InterNetwork) return [];
        var b = local.GetAddressBytes();
        var list = new List<IPAddress>(254);
        for (int i = 1; i <= 254; i++)
        {
            if (i == b[3]) continue;
            list.Add(new IPAddress(new[] { b[0], b[1], b[2], (byte)i }));
        }
        return list;
    }

    /// <summary>True for RFC 1918 private IPv4 addresses (10/8, 172.16/12, 192.168/16).</summary>
    public static bool IsPrivateLan(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>
    /// Private IPv4 addresses of up, non-tunnel interfaces that have a gateway and a prefix of /16 to /30.
    /// VPN overlays (Tailscale /32, ZeroTier without a gateway) are skipped.
    /// </summary>
    public static IReadOnlyList<IPAddress> LanAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                var type = nic.NetworkInterfaceType;
                if (type is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel
                    or System.Net.NetworkInformation.NetworkInterfaceType.Ppp) continue;
                System.Net.NetworkInformation.IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); }
                catch (System.Net.NetworkInformation.NetworkInformationException) { continue; }
                bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                if (!gw) continue;
                foreach (var ua in props.UnicastAddresses)
                {
                    if (!IsPrivateLan(ua.Address)) continue;
                    if (ua.PrefixLength is < 16 or > 30) continue;
                    if (!list.Contains(ua.Address)) list.Add(ua.Address);
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        return list.Count > 4 ? list.GetRange(0, 4) : list;
    }

    public static async Task<IReadOnlyList<DiscoveredPlayer>> DiscoverAsync(CancellationToken ct = default)
    {
        var targets = new List<IPEndPoint>();
        foreach (var local in LanAddresses())
            foreach (var host in HostsIn24(local))
                targets.Add(new IPEndPoint(host, DefaultPort));
        if (targets.Count == 0) return [];
        return await ProbeAsync(targets, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Connects to each target with <paramref name="connectTimeout"/>; for each open port, sends GET /SyncStatus
    /// and keeps the host when the reply is a SyncStatus document. Stops after <paramref name="total"/>.
    /// </summary>
    public static async Task<IReadOnlyList<DiscoveredPlayer>> ProbeAsync(IReadOnlyList<IPEndPoint> targets, TimeSpan connectTimeout, TimeSpan total, CancellationToken ct = default)
    {
        using var deadline = new CancellationTokenSource(total);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var token = linked.Token;
        using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(2), UseProxy = false, AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 512 * 1024 };
        using var gate = new SemaphoreSlim(64);
        var found = new DiscoveredPlayer?[targets.Count];

        async Task One(int i)
        {
            var ep = targets[i];
            try { await gate.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                if (!await IsOpenAsync(ep, connectTimeout, token).ConfigureAwait(false)) return;
                string url = "http://" + ep.Address + ":" + ep.Port + "/SyncStatus";
                string xml = await http.GetStringAsync(url, token).ConfigureAwait(false);
                var sync = BluOsXml.ParseSyncStatus(xml);
                string host = ep.Address.ToString();
                found[i] = new DiscoveredPlayer
                {
                    Name = sync.Name.Length > 0 ? sync.Name : host,
                    Host = host,
                    Port = ep.Port,
                    Service = "scan",
                };
            }
            catch (Exception) { /* closed, slow, or not a BluOS player */ }
            finally { gate.Release(); }
        }

        var tasks = new Task[targets.Count];
        for (int i = 0; i < targets.Count; i++) tasks[i] = One(i);
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (Exception) { }
        var list = new List<DiscoveredPlayer>();
        foreach (var p in found)
            if (p != null && list.Count < 16 && !list.Any(h => h.Host == p.Host && h.Port == p.Port)) list.Add(p);
        return list;
    }

    static async Task<bool> IsOpenAsync(IPEndPoint ep, TimeSpan timeout, CancellationToken ct)
    {
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await sock.ConnectAsync(ep, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception) { return false; }
    }
}

/// <summary>mDNS and LSDP together; when both find nothing, the /24 SyncStatus scan.</summary>
public static class PlayerDiscovery
{
    public static async Task<IReadOnlyList<DiscoveredPlayer>> DiscoverAsync(TimeSpan mdnsTimeout, bool scanFallback = true, CancellationToken ct = default)
    {
        var mdnsTask = MdnsDiscovery.DiscoverAsync(mdnsTimeout, ct);
        var lsdpTask = LsdpDiscovery.DiscoverAsync(mdnsTimeout, ct);
        await Task.WhenAll(mdnsTask, lsdpTask).ConfigureAwait(false);
        var found = Merge(mdnsTask.Result, lsdpTask.Result);
        if (found.Count > 0 || !scanFallback || ct.IsCancellationRequested) return found;
        return await SubnetScan.DiscoverAsync(ct).ConfigureAwait(false);
    }

    public static List<DiscoveredPlayer> Merge(IEnumerable<DiscoveredPlayer> mdns, IEnumerable<DiscoveredPlayer> lsdp)
    {
        var list = new List<DiscoveredPlayer>();
        foreach (var p in mdns.Concat(lsdp))
        {
            if (list.Count >= 16) break;
            if (!list.Any(h => h.Host == p.Host && h.Port == p.Port)) list.Add(p);
        }
        return list;
    }
}
