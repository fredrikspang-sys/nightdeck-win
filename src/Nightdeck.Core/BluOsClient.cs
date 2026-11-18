using System.Net;
using System.Text;

namespace Nightdeck.Core;

public sealed class BluOsClient : IBluOsClient
{
    readonly HttpClient _http;
    readonly SocketsHttpHandler _handler;

    public BluOsClient(string host, int port)
    {
        _handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        _http = new HttpClient(_handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Nightdeck/1.0.0");
        SetEndpoint(host, port);
    }

    public string Host { get; private set; } = "";
    public int Port { get; private set; }
    public string BaseUrl => "http://" + Host + ":" + Port + "/";

    public void SetEndpoint(string host, int port)
    {
        Host = (host ?? "").Trim();
        Port = port is > 0 and <= 65535 ? port : 11000;
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        _handler.Dispose();
        await Task.CompletedTask;
    }

    public async Task<PlayerStatus> GetStatusAsync(CancellationToken ct = default)
        => BluOsXml.ParseStatus(await Xml("/Status", ct, 8, 4_000_000).ConfigureAwait(false));

    public async Task<PlayerStatus> PollStatusAsync(string etag, int timeoutSeconds, CancellationToken ct = default)
    {
        if (timeoutSeconds < 1) timeoutSeconds = 1;
        if (timeoutSeconds > 120) timeoutSeconds = 120;
        string path = "/Status?timeout=" + timeoutSeconds.ToString() + "&etag=" + Enc(etag ?? "");
        return BluOsXml.ParseStatus(await Xml(path, ct, timeoutSeconds + 8, 4_000_000).ConfigureAwait(false));
    }

    public async Task<PlayQueue> GetQueueAsync(CancellationToken ct = default)
        => BluOsXml.ParseQueue(await Xml("/Playlist", ct).ConfigureAwait(false));

    public async Task<(int Length, string Id)> GetQueueMetaAsync(CancellationToken ct = default)
        => BluOsXml.ParseQueueMeta(await Xml("/Playlist?length=1", ct).ConfigureAwait(false));

    public async Task<BrowseResult> BrowseAsync(string key, string? query = null, CancellationToken ct = default)
    {
        string path = "/Browse?key=" + Enc(key);
        if (!string.IsNullOrEmpty(query)) path += "&q=" + Enc(query);
        return BluOsXml.ParseBrowse(await Xml(path, ct).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<BrowseItem>> BrowseAllAsync(string key, string? query = null, int maxPages = 200, CancellationToken ct = default)
    {
        if (maxPages < 1) maxPages = 1;
        var all = new List<BrowseItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? q = query;
        for (int i = 0; i < maxPages; i++)
        {
            if (!seen.Add(key)) break;
            var page = await BrowseAsync(key, i == 0 ? q : null, ct).ConfigureAwait(false);
            all.AddRange(page.Items);
            if (page.NextKey.Length == 0) break;
            key = page.NextKey;
        }
        return all;
    }

    public Task SendPathAsync(string pathOrAbsoluteUrl, CancellationToken ct = default)
        => Send(pathOrAbsoluteUrl, ct);

    public Task PlayAsync(CancellationToken ct = default) => Send("/Play", ct);
    public Task PauseToggleAsync(CancellationToken ct = default) => Send("/Pause?toggle=1", ct);
    public Task PauseAsync(CancellationToken ct = default) => Send("/Pause", ct);
    public Task StopAsync(CancellationToken ct = default) => Send("/Stop", ct);
    public Task SkipAsync(CancellationToken ct = default) => Send("/Skip", ct);
    public Task BackAsync(CancellationToken ct = default) => Send("/Back", ct);
    public Task SeekAsync(int seconds, CancellationToken ct = default) => Send("/Play?seek=" + seconds.ToString(), ct);
    public Task PlaySongAsync(int id, CancellationToken ct = default) => Send("/Play?id=" + id.ToString(), ct);
    public Task SetVolumeAsync(int level, CancellationToken ct = default) => Send("/Volume?level=" + level.ToString(), ct);
    public Task NudgeVolumeDbAsync(int deltaDb, CancellationToken ct = default) => Send("/Volume?db=" + Enc(deltaDb.ToString()), ct);
    public Task SetMuteAsync(bool mute, CancellationToken ct = default) => Send("/Volume?mute=" + (mute ? "1" : "0"), ct);
    public Task SetShuffleAsync(bool on, CancellationToken ct = default) => Send("/Shuffle?state=" + (on ? "1" : "0"), ct);
    public Task SetRepeatAsync(int state, CancellationToken ct = default) => Send("/Repeat?state=" + state.ToString(), ct);
    public Task DeleteSongAsync(int id, CancellationToken ct = default) => Send("/Delete?id=" + id.ToString(), ct);
    public Task ClearQueueAsync(CancellationToken ct = default) => Send("/Clear", ct);
    public Task MoveSongAsync(int oldIndex, int newIndex, CancellationToken ct = default)
        => Send("/Move?old=" + oldIndex.ToString() + "&new=" + newIndex.ToString(), ct);

    public Task<int> AddItemAsync(BrowseItem item, bool playNow, CancellationToken ct = default)
        => Add(item, playNow, 0, new Counter(), ct);

    public async Task<int> StepSleepAsync(CancellationToken ct = default)
        => BluOsXml.ParseSleepMinutes(await Xml("/Sleep", ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<Preset>> GetPresetsAsync(CancellationToken ct = default)
        => BluOsXml.ParsePresets(await Xml("/Presets", ct).ConfigureAwait(false));

    public Task PlayPresetAsync(string id, CancellationToken ct = default) => Send("/Preset?id=" + Enc(id), ct);

    public async Task<int> SavePlaylistAsync(string name, CancellationToken ct = default)
        => BluOsXml.ParseEntries(await Xml("/Save?name=" + Enc(name), ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<BrowseItem>> ListPlaylistsAsync(CancellationToken ct = default)
    {
        var items = await BrowseAllAsync(BluOs.PlaylistRoot, null, 20, ct).ConfigureAwait(false);
        return items.Where(i => i.Text.Length > 0 && i.PlayUrl.Length > 0).ToList();
    }

    public Task<byte[]?> GetBytesAsync(string pathOrAbsoluteUrl, int maxBytes, CancellationToken ct = default)
        => Fetch(pathOrAbsoluteUrl, ct, 8, maxBytes < 1 ? 1 : maxBytes, nullOn404: true);

    public async Task<SyncStatus> GetSyncStatusAsync(CancellationToken ct = default)
        => BluOsXml.ParseSyncStatus(await Xml("/SyncStatus", ct).ConfigureAwait(false));

    public Task AddSlaveAsync(string host, int port, CancellationToken ct = default)
        => Slave("/AddSlave", host, port, ct);

    public Task RemoveSlaveAsync(string host, int port, CancellationToken ct = default)
        => Slave("/RemoveSlave", host, port, ct);

    async Task<int> Add(BrowseItem item, bool playNow, int depth, Counter nodes, CancellationToken ct)
    {
        if (++nodes.N > 1500) throw new BluOsException("too many items to add (max 1500)");
        if (item.ContextMenuKey.Length > 0)
        {
            var menu = await BrowseAllAsync(item.ContextMenuKey, null, 8, ct).ConfigureAwait(false);
            string a = playNow ? "add-now" : "add-last";
            string b = playNow ? "addAll-now" : "addAll-last";
            foreach (var entry in menu)
            {
                if ((entry.Type == a || entry.Type == b) && entry.ActionUrl.Length > 0)
                {
                    await SendPathAsync(entry.ActionUrl, ct).ConfigureAwait(false);
                    return 1;
                }
            }
        }
        if (item.IsContainer && depth < 3 && item.Type is not ("section" or "link" or "genre"))
        {
            var children = await BrowseAllAsync(item.BrowseKey, null, 200, ct).ConfigureAwait(false);
            int n = 0;
            foreach (var child in children)
            {
                int added = await Add(child, playNow && n == 0, depth + 1, nodes, ct).ConfigureAwait(false);
                n += added;
            }
            return n;
        }
        if (item.PlayUrl.Length > 0)
        {
            await SendPathAsync(QueueUrl(item.PlayUrl, playNow), ct).ConfigureAwait(false);
            return 1;
        }
        return 0;
    }

    static string QueueUrl(string url, bool playNow)
    {
        if (playNow)
        {
            if (!url.Contains("clear=", StringComparison.Ordinal)) url += "&clear=0";
            if (!url.Contains("where=", StringComparison.Ordinal)) url += "&where=next";
            return url;
        }
        var sb = new StringBuilder(url.Length + 16);
        int i = 0;
        while (true)
        {
            int at = url.IndexOf("playnow=1", i, StringComparison.Ordinal);
            if (at < 0)
            {
                sb.Append(url, i, url.Length - i);
                break;
            }
            sb.Append(url, i, at - i).Append("playnow=-1");
            i = at + 9;
        }
        string result = sb.ToString();
        if (!result.Contains("where=", StringComparison.Ordinal)) result += "&where=last";
        return result;
    }

    Task Slave(string path, string host, int port, CancellationToken ct)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0) throw new BluOsException("empty host");
        if (port is < 1 or > 65535) port = 11000;
        return Send(path + "?slave=" + Enc(host) + "&port=" + port.ToString(), ct);
    }

    async Task Send(string path, CancellationToken ct)
    {
        BluOsXml.EnsureOk(await Xml(path, ct).ConfigureAwait(false));
    }

    async Task<string> Xml(string path, CancellationToken ct, int seconds = 8, int maxBytes = 8_000_000)
    {
        var bytes = await Fetch(path, ct, seconds, maxBytes, nullOn404: false).ConfigureAwait(false);
        return BluOsXml.Decode(bytes);
    }

    async Task<byte[]?> Fetch(string path, CancellationToken ct, int seconds, int maxBytes, bool nullOn404)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, seconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Absolute(path));
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (nullOn404 && resp.StatusCode == HttpStatusCode.NotFound) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            int cap = (int)resp.StatusCode >= 400 ? Math.Min(maxBytes, 65_536) : maxBytes;
            var ms = new MemoryStream();
            var buf = new byte[8192];
            while (true)
            {
                int n = await stream.ReadAsync(buf, linked.Token).ConfigureAwait(false);
                if (n == 0) break;
                if (ms.Length + n > cap) throw new BluOsException("response from player too large");
                ms.Write(buf, 0, n);
            }
            if ((int)resp.StatusCode >= 400)
            {
                string? detail = BluOsXml.ErrorDetail(BluOsXml.Decode(ms.ToArray()));
                if (detail != null) throw new BluOsException(detail.Length == 0 ? "error" : detail);
                throw new BluOsException("HTTP Error " + (int)resp.StatusCode);
            }
            return ms.ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new BluOsException("timeout"); }
        catch (BluOsException) { throw; }
        catch (Exception ex) { throw new BluOsException(ex.Message); }
    }

    string Absolute(string path)
    {
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return path;
        if (path.Length == 0 || path[0] != '/') path = "/" + path;
        return "http://" + Host + ":" + Port + path;
    }

    static string Enc(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (byte c in bytes)
        {
            if (c is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9') or (byte)'_' or (byte)'.' or (byte)'-' or (byte)'~')
                sb.Append((char)c);
            else if (c == (byte)' ') sb.Append('+');
            else sb.Append('%').Append(c.ToString("X2"));
        }
        return sb.ToString();
    }

    sealed class Counter { public int N; }
}
