using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace Nightdeck.Mock;

/// <summary>Loopback BluOS stand-in for tests. Long-polls block until the state changes.</summary>
public sealed class MockPlayer : IDisposable
{
    public const string LibraryRoot = "LocalMusic:";
    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    static readonly int[] Sleeps = [15, 30, 45, 60, 90, 0];
    static readonly Album[] Albums =
    [
        new("Alpha", "Artist A", ["01 Alpha t1", "02 Alpha t2"]),
        new("Bravo", "Artist B", ["01 Bravo t1"]),
        new("Charlie", "Artist C", ["01 Charlie t1", "02 Charlie t2", "03 Charlie t3"]),
        new("Delta", "Artist D", ["01 Delta t1"]),
        new("Echo", "Artist E", ["01 Echo t1"]),
        new("Foxtrot", "Artist F", ["01 Foxtrot t1"]),
        new("Golf", "Artist G", ["01 Golf t1"]),
        new("Hotel", "Artist H", ["01 Hotel t1", "02 Hotel t2"]),
    ];

    readonly object _gate = new();
    readonly List<string> _paths = [];
    readonly Dictionary<string, string> _queries = new(StringComparer.Ordinal);
    readonly List<(string Id, int Port)> _slaves = [];
    readonly List<Song> _queue = [];
    readonly Dictionary<string, List<Song>> _saved = new(StringComparer.Ordinal);
    HttpListener? _listener;
    Task? _loop;
    bool _stop;
    string _state = "play";
    int _volume = 40;
    int _song;
    int _pid = 1;
    int _shuffle;
    int _repeat = 2;
    int _mute;
    int _sleep;
    double _base;
    DateTime _t0 = DateTime.UtcNow;

    public int Port { get; private set; }
    public string BaseUrl => "http://127.0.0.1:" + Port + "/";
    public string Name { get; set; } = "Mock";
    public bool FailGroup { get; set; }

    public int Count(string path)
    {
        lock (_gate)
        {
            int n = 0;
            foreach (string p in _paths)
                if (p == path) n++;
            return n;
        }
    }

    public string LastQuery(string absolutePath)
    {
        lock (_gate)
            return _queries.TryGetValue(absolutePath, out string? query) ? query : "";
    }

    public MockPlayer()
    {
        for (int i = 1; i <= 4; i++)
            _queue.Add(new Song("Track " + i, "Queue Artist", "Queue Album", "/mnt/music/queue/0" + i + ".flac"));
    }

    public void Start()
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                listener.Start();
                _listener = listener;
                Port = port;
                _loop = Task.Run(Loop);
                return;
            }
            catch { Thread.Sleep(20); }
        }
        throw new InvalidOperationException("could not bind the mock player");
    }

    public void Dispose()
    {
        lock (_gate) { _stop = true; Monitor.PulseAll(_gate); }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        try { _loop?.Wait(1500); } catch { }
    }

    async Task Loop()
    {
        var listener = _listener;
        if (listener == null) return;
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() =>
            {
                try { Handle(ctx); }
                catch { try { ctx.Response.Abort(); } catch { } }
            });
        }
    }

    void Handle(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url?.AbsolutePath ?? "/";
        var qs = Query(ctx.Request.Url?.Query);
        byte[] body;
        string type;
        lock (_gate)
        {
            _paths.Add(path);
            string raw = ctx.Request.Url?.Query ?? "";
            if (raw.Length > 0 && raw[0] == '?') raw = raw[1..];
            _queries[path] = raw;
            if (path == "/Artwork")
            {
                body = Png;
                type = "image/png";
            }
            else
            {
                string xml = path == "/Status" ? WaitStatus(qs) : Route(path, qs);
                body = Encoding.UTF8.GetBytes("""<?xml version="1.0" encoding="UTF-8"?>""" + xml);
                type = "text/xml; charset=utf-8";
            }
        }
        try
        {
            ctx.Response.StatusCode = body.Length == 0 ? 404 : 200;
            ctx.Response.ContentType = type;
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body, 0, body.Length);
            ctx.Response.Close();
        }
        catch { }
    }

    string WaitStatus(Dictionary<string, string> qs)
    {
        qs.TryGetValue("etag", out string? etag);
        double timeout = qs.TryGetValue("timeout", out string? t) && double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0;
        var until = DateTime.UtcNow.AddSeconds(timeout);
        while (!_stop && timeout > 0 && !string.IsNullOrEmpty(etag) && etag == Etag() && DateTime.UtcNow < until)
        {
            int ms = (int)Math.Min(250, Math.Max(1, (until - DateTime.UtcNow).TotalMilliseconds));
            Monitor.Wait(_gate, ms);
        }
        return StatusXml();
    }

    string Route(string path, Dictionary<string, string> qs)
    {
        switch (path)
        {
            case "/Playlist" when qs.ContainsKey("length"):
                return "<playlist><length>" + _queue.Count + "</length><id>" + _pid + "</id></playlist>";
            case "/Playlist":
                return PlaylistXml();
            case "/Browse":
                return Browse(qs.GetValueOrDefault("key", LibraryRoot), qs.GetValueOrDefault("q", ""));
            case "/Play":
                if (qs.TryGetValue("seek", out string? seek) && double.TryParse(seek, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double at))
                    SetPos(at);
                else if (qs.TryGetValue("id", out string? id) && int.TryParse(id, out int song) && song >= 0 && song < _queue.Count)
                {
                    _song = song;
                    SetPos(0);
                }
                else SetPos(Secs());
                _state = "play";
                Pulse();
                return "<state>play</state>";
            case "/Pause":
                SetPos(Secs());
                if (qs.GetValueOrDefault("toggle") == "1") _state = _state == "play" ? "pause" : "play";
                else if (_state == "play") _state = "pause";
                Pulse();
                return "<state>" + _state + "</state>";
            case "/Stop":
                SetPos(0);
                _state = "stop";
                Pulse();
                return "<state>stop</state>";
            case "/Skip":
                if (_queue.Count > 0) _song = (_song + 1) % _queue.Count;
                SetPos(0);
                Pulse();
                return "<id>" + _song + "</id>";
            case "/Back":
                SetPos(0);
                Pulse();
                return "<id>" + _song + "</id>";
            case "/Volume" when qs.ContainsKey("mute"):
                _mute = qs["mute"] == "1" ? 1 : 0;
                Pulse();
                return _mute == 1 ? "<volume db=\"-100\" mute=\"1\">0</volume>" : "<volume db=\"" + Db + "\" mute=\"0\">" + _volume + "</volume>";
            case "/Volume":
                if (qs.TryGetValue("level", out string? level) && int.TryParse(level, out int lv))
                {
                    _volume = Math.Clamp(lv, 0, 100);
                }
                else if (qs.TryGetValue("db", out string? delta) && int.TryParse(delta, out int db))
                {
                    int next = Math.Clamp(Db + db, -80, 0);
                    _volume = next + 80;
                }
                Pulse();
                return "<volume db=\"" + Db + "\">" + _volume + "</volume>";
            case "/Shuffle":
                _shuffle = qs.GetValueOrDefault("state") == "1" ? 1 : 0;
                Pulse();
                return "<playlist shuffle=\"" + _shuffle + "\"/>";
            case "/Repeat":
                _repeat = int.TryParse(qs.GetValueOrDefault("state"), out int r) ? r : 0;
                Pulse();
                return "<playlist repeat=\"" + _repeat + "\"/>";
            case "/Delete" when qs.ContainsKey("name"):
                _saved.Remove(qs["name"]);
                return "<deleted>" + Esc(qs["name"]) + "</deleted>";
            case "/Delete":
                if (int.TryParse(qs.GetValueOrDefault("id"), out int del) && del >= 0 && del < _queue.Count)
                {
                    _queue.RemoveAt(del);
                    if (del < _song) _song--;
                    if (_song >= _queue.Count) _song = _queue.Count - 1;
                    _pid++;
                    Pulse();
                }
                return "<deleted>" + qs.GetValueOrDefault("id") + "</deleted>";
            case "/Clear":
                _queue.Clear();
                _song = -1;
                _pid++;
                Pulse();
                return "<playlist/>";
            case "/Move":
                if (int.TryParse(qs.GetValueOrDefault("old"), out int oldI) && int.TryParse(qs.GetValueOrDefault("new"), out int newI)
                    && oldI >= 0 && newI >= 0 && oldI < _queue.Count && newI < _queue.Count && oldI != newI)
                {
                    var item = _queue[oldI];
                    _queue.RemoveAt(oldI);
                    _queue.Insert(newI, item);
                    if (_song == oldI) _song = newI;
                    else if (oldI < _song && _song <= newI) _song--;
                    else if (newI <= _song && _song < oldI) _song++;
                    _pid++;
                    Pulse();
                }
                return "<moved>moved</moved>";
            case "/Add":
                Add(qs);
                return "<addsong/>";
            case "/Sleep":
                int idx = Array.IndexOf(Sleeps, _sleep);
                if (idx < 0) idx = Sleeps.Length - 1;
                _sleep = Sleeps[(idx + 1) % Sleeps.Length];
                Pulse();
                return "<sleep>" + (_sleep == 0 ? "" : _sleep.ToString()) + "</sleep>";
            case "/Presets":
                return "<presets><preset id=\"1\" name=\"Radio One\"/><preset id=\"2\" name=\"Jazz &amp; Blues\"/></presets>";
            case "/Preset":
                string preset = qs.GetValueOrDefault("id") == "2" ? "Jazz & Blues" : "Radio One";
                _queue.Clear();
                _queue.Add(new Song(preset, "Preset", "Radio", ""));
                _song = 0;
                _state = "play";
                SetPos(0);
                _pid++;
                Pulse();
                return "<state>play</state>";
            case "/Save":
                string name = qs.GetValueOrDefault("name") ?? "";
                _saved[name] = _queue.Select(s => s).ToList();
                _pid++;
                Pulse();
                return "<saved><entries>" + _queue.Count + "</entries></saved>";
            case "/Load":
                string load = qs.GetValueOrDefault("name") ?? "";
                _queue.Clear();
                if (_saved.TryGetValue(load, out var copy)) _queue.AddRange(copy);
                _song = _queue.Count > 0 ? 0 : -1;
                _state = _queue.Count > 0 ? "play" : "stop";
                SetPos(0);
                _pid++;
                Pulse();
                return "<loaded><entries>" + _queue.Count + "</entries></loaded>";
            case "/Playlists":
                return "<playlists>" + string.Concat(_saved.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => "<name>" + Esc(k) + "</name>")) + "</playlists>";
            case "/SyncStatus":
                return SyncXml();
            case "/AddSlave":
            case "/RemoveSlave":
                return Group(path, qs);
            default:
                return "";
        }
    }

    string SyncXml()
    {
        var sb = new StringBuilder("<SyncStatus name=\"" + Esc(Name) + "\" modelName=\"FAKE\">");
        foreach (var slave in _slaves)
            sb.Append("<slave id=\"").Append(Esc(slave.Id)).Append("\" port=\"").Append(slave.Port).Append("\"/>");
        return sb.Append("</SyncStatus>").ToString();
    }

    string Group(string path, Dictionary<string, string> qs)
    {
        if (FailGroup) return "<error><detail>refused</detail></error>";
        string slave = qs.GetValueOrDefault("slave") ?? "";
        if (string.IsNullOrWhiteSpace(slave)) return "<error><detail>missing slave</detail></error>";
        int port = 11000;
        if (qs.TryGetValue("port", out string? text) && int.TryParse(text, out int parsed)) port = parsed;
        int at = -1;
        for (int i = 0; i < _slaves.Count; i++)
        {
            if (_slaves[i].Port == port && string.Equals(_slaves[i].Id, slave, StringComparison.OrdinalIgnoreCase))
            {
                at = i;
                break;
            }
        }
        if (path == "/AddSlave")
        {
            if (at < 0) _slaves.Add((slave, port));
        }
        else if (at >= 0)
            _slaves.RemoveAt(at);
        return "<addSlave/>";
    }

    void Add(Dictionary<string, string> qs)
    {
        if (qs.TryGetValue("album", out string? album) && int.TryParse(album, out int ai) && ai >= 0 && ai < Albums.Length)
        {
            var a = Albums[ai];
            for (int t = 0; t < a.Tracks.Length; t++)
                _queue.Add(new Song(a.Tracks[t], a.Artist, a.Name, "/mnt/music/" + a.Name + "/" + a.Tracks[t] + ".flac"));
        }
        else if (qs.TryGetValue("file", out string? file))
        {
            string title = Path.GetFileNameWithoutExtension(file.Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(title)) title = "Track";
            _queue.Add(new Song(title, "Added Artist", "Added Album", file));
        }
        if (qs.GetValueOrDefault("playnow") == "1" && _queue.Count > 0)
        {
            _song = _queue.Count - 1;
            _state = "play";
            SetPos(0);
        }
        _pid++;
        Pulse();
    }

    string Browse(string key, string q)
    {
        if (key == LibraryRoot)
        {
            return "<browse serviceName=\"Library\" searchKey=\"LocalMusic:Search\">"
                + Item("Artists", "link", "LocalMusic:bySection:Artist/x")
                + Item("Albums", "link", "LocalMusic:bySection:Album/x")
                + "</browse>";
        }
        if (key.StartsWith("LocalMusic:bySection:Artist", StringComparison.Ordinal))
        {
            var sb = new StringBuilder("<browse serviceName=\"Library\">");
            for (int i = 0; i < Albums.Length; i++) sb.Append(Item(Albums[i].Artist, "artist", "ART:" + i));
            return sb.Append("</browse>").ToString();
        }
        if (key.StartsWith("LocalMusic:bySection:Album", StringComparison.Ordinal))
        {
            var letters = Albums.Select(a => Section(a.Name)).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
            var sb = new StringBuilder("<browse serviceName=\"Library\" type=\"sections\">");
            foreach (var letter in letters)
                sb.Append(Item(letter, "section", "SEC:" + letter, text2: Albums.Count(a => Section(a.Name) == letter).ToString()));
            return sb.Append("</browse>").ToString();
        }
        if (key.StartsWith("ART:", StringComparison.Ordinal) && int.TryParse(key.AsSpan(4), out int artist) && artist >= 0 && artist < Albums.Length)
            return "<browse serviceName=\"Library\">" + AlbumItem(artist) + "</browse>";
        if (key.StartsWith("SEC:", StringComparison.Ordinal) || key.StartsWith("NEXT:", StringComparison.Ordinal))
        {
            string letter;
            int off;
            if (key.StartsWith("SEC:", StringComparison.Ordinal)) { letter = key[4..]; off = 0; }
            else
            {
                var parts = key.Split(':');
                letter = parts.Length > 1 ? parts[1] : "";
                off = parts.Length > 2 && int.TryParse(parts[2], out int o) ? o : 0;
            }
            int[] idx = Albums.Select((a, i) => (a, i)).Where(x => Section(x.a.Name) == letter).Select(x => x.i).Skip(off).ToArray();
            string next = idx.Length > 3 ? " nextKey=\"" + Esc("NEXT:" + letter + ":" + (off + 3)) + "\"" : "";
            var sb = new StringBuilder("<browse serviceName=\"Library\"" + next + ">");
            foreach (int i in idx.Take(3)) sb.Append(AlbumItem(i));
            return sb.Append("</browse>").ToString();
        }
        if (key.StartsWith("ALB:", StringComparison.Ordinal) && int.TryParse(key.AsSpan(4), out int album) && album >= 0 && album < Albums.Length)
        {
            var a = Albums[album];
            var sb = new StringBuilder("<browse serviceName=\"" + Esc(a.Name) + "\">");
            for (int t = 0; t < a.Tracks.Length; t++)
            {
                string file = "/mnt/music/" + a.Name + "/" + a.Tracks[t] + ".flac";
                sb.Append("<item text=\"").Append(Esc(a.Tracks[t])).Append("\" type=\"track\" playURL=\"")
                    .Append(Esc("/Add?playnow=1&file=" + Uri.EscapeDataString(file))).Append("\" contextMenuKey=\"CMT:")
                    .Append(album).Append(':').Append(t).Append("\"/>");
            }
            return sb.Append("</browse>").ToString();
        }
        if (key.StartsWith("CMA:", StringComparison.Ordinal) && int.TryParse(key.AsSpan(4), out int cma))
        {
            return "<browse type=\"contextMenu\">"
                + "<item type=\"addAll-now\" text=\"Play now\" actionURL=\"" + Esc("/Add?clear=0&playnow=1&where=next&album=" + cma) + "\"/>"
                + "<item type=\"addAll-last\" text=\"Add\" actionURL=\"" + Esc("/Add?playnow=-1&where=last&album=" + cma) + "\"/>"
                + "</browse>";
        }
        if (key.StartsWith("CMT:", StringComparison.Ordinal))
        {
            var bits = key.Split(':');
            if (bits.Length == 3 && int.TryParse(bits[1], out int ai) && int.TryParse(bits[2], out int ti) && ai >= 0 && ai < Albums.Length && ti >= 0 && ti < Albums[ai].Tracks.Length)
            {
                string file = Uri.EscapeDataString("/mnt/music/" + Albums[ai].Name + "/" + Albums[ai].Tracks[ti] + ".flac");
                return "<browse type=\"contextMenu\">"
                    + "<item type=\"add-now\" text=\"Play now\" actionURL=\"" + Esc("/Add?clear=0&playnow=1&where=next&file=" + file) + "\"/>"
                    + "<item type=\"add-last\" text=\"Add\" actionURL=\"" + Esc("/Add?playnow=-1&where=last&file=" + file) + "\"/>"
                    + "</browse>";
            }
        }
        if (key == "LocalMusic:Search")
        {
            string query = q.Trim().ToLowerInvariant();
            var sb = new StringBuilder("<browse serviceName=\"Library\" type=\"menu\">");
            if (Albums.Any(a => a.Artist.Contains(query, StringComparison.OrdinalIgnoreCase)))
                sb.Append(Item("Artists", "link", "FIND:artist:" + query));
            if (Albums.Any(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
                sb.Append(Item("Albums", "link", "FIND:album:" + query));
            if (Albums.Any(a => a.Tracks.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase))))
                sb.Append(Item("Songs", "link", "FIND:song:" + query));
            return sb.Append("</browse>").ToString();
        }
        if (key.StartsWith("FIND:", StringComparison.Ordinal))
        {
            var parts = key.Split(':', 3);
            string kind = parts.Length > 1 ? parts[1] : "";
            string query = parts.Length > 2 ? parts[2] : "";
            var sb = new StringBuilder("<browse serviceName=\"Library\">");
            if (kind == "album")
            {
                for (int i = 0; i < Albums.Length; i++)
                    if (Albums[i].Name.Contains(query, StringComparison.OrdinalIgnoreCase)) sb.Append(AlbumItem(i));
            }
            else if (kind == "artist")
            {
                for (int i = 0; i < Albums.Length; i++)
                    if (Albums[i].Artist.Contains(query, StringComparison.OrdinalIgnoreCase)) sb.Append(Item(Albums[i].Artist, "artist", "ART:" + i));
            }
            else
            {
                for (int i = 0; i < Albums.Length; i++)
                    for (int t = 0; t < Albums[i].Tracks.Length; t++)
                        if (Albums[i].Tracks[t].Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            string file = "/mnt/music/" + Albums[i].Name + "/" + Albums[i].Tracks[t] + ".flac";
                            sb.Append("<item text=\"").Append(Esc(Albums[i].Tracks[t])).Append("\" type=\"track\" playURL=\"")
                                .Append(Esc("/Add?playnow=1&file=" + Uri.EscapeDataString(file))).Append("\" contextMenuKey=\"CMT:")
                                .Append(i).Append(':').Append(t).Append("\"/>");
                        }
            }
            return sb.Append("</browse>").ToString();
        }
        if (key == "BluOS:")
        {
            var sb = new StringBuilder("<browse serviceName=\"BluOS\" type=\"playlists\">");
            foreach (var name in _saved.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                string enc = Uri.EscapeDataString(name);
                sb.Append("<item type=\"playlist\" text=\"").Append(Esc(name))
                    .Append("\" browseKey=\"BluOS:PL:").Append(Esc(enc))
                    .Append("\" contextMenuKey=\"BluOS:CM:").Append(Esc(enc))
                    .Append("\" playURL=\"").Append(Esc("/Load?service=BluOS&name=" + enc)).Append("\"/>");
            }
            return sb.Append("</browse>").ToString();
        }
        if (key.StartsWith("BluOS:CM:", StringComparison.Ordinal))
        {
            string enc = key["BluOS:CM:".Length..];
            return "<browse serviceName=\"BluOS\" type=\"contextMenu\">"
                + "<item type=\"add-now\" text=\"Play now\" actionURL=\"" + Esc("/Load?service=BluOS&name=" + enc) + "\"/>"
                + "<item type=\"delete\" text=\"Delete\" actionURL=\"" + Esc("/Delete?name=" + enc + "&service=BluOS") + "\"/>"
                + "</browse>";
        }
        return "<browse serviceName=\"?\"></browse>";
    }

    string Etag()
    {
        Song? song = Current;
        string core = _state + "|" + _pid + "|" + _song + "|" + (song?.Title ?? "") + "|" + (song?.Artist ?? "") + "|" + (song?.Album ?? "")
            + "|" + _volume + "|" + _shuffle + "|" + _repeat + "|" + _mute + "|" + _sleep + "|" + (song?.File ?? "");
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(core)));
    }

    Song? Current => _song >= 0 && _song < _queue.Count ? _queue[_song] : null;

    string StatusXml()
    {
        Song? song = Current;
        return "<status etag=\"" + Etag() + "\"><state>" + _state + "</state><pid>" + _pid + "</pid><song>" + _song
            + "</song><title1>" + Esc(song?.Title ?? "") + "</title1><title2>" + Esc(song?.Artist ?? "") + "</title2><title3>" + Esc(song?.Album ?? "")
            + "</title3><volume>" + _volume + "</volume><db>" + Db + "</db><secs>" + Secs() + "</secs><totlen>200</totlen>"
            + "<streamFormat>FLAC 16/44.1</streamFormat><shuffle>" + _shuffle + "</shuffle><repeat>" + _repeat + "</repeat><mute>" + _mute
            + "</mute><sleep>" + (_sleep == 0 ? "" : _sleep.ToString()) + "</sleep><fn>" + Esc(song?.File ?? "")
            + "</fn><service>LocalMusic</service><image>/Artwork?service=LocalMusic</image></status>";
    }

    string PlaylistXml()
    {
        var sb = new StringBuilder("<playlist length=\"" + _queue.Count + "\" id=\"" + _pid + "\">");
        for (int i = 0; i < _queue.Count; i++)
        {
            var s = _queue[i];
            sb.Append("<song id=\"").Append(i).Append("\"><art>").Append(Esc(s.Artist)).Append("</art><alb>").Append(Esc(s.Album))
                .Append("</alb><title>").Append(Esc(s.Title)).Append("</title><fn>").Append(Esc(s.File)).Append("</fn></song>");
        }
        return sb.Append("</playlist>").ToString();
    }

    static string AlbumItem(int i)
    {
        var a = Albums[i];
        return "<item type=\"album\" text=\"" + Esc(a.Name) + "\" text2=\"" + Esc(a.Artist) + "\" browseKey=\"ALB:" + i
            + "\" contextMenuKey=\"CMA:" + i + "\" playURL=\"" + Esc("/Add?playnow=1&album=" + i) + "\"/>";
    }

    static string Item(string text, string type, string key, string text2 = "")
    {
        string extra = text2.Length == 0 ? "" : " text2=\"" + Esc(text2) + "\"";
        return "<item type=\"" + type + "\" text=\"" + Esc(text) + "\"" + extra + " browseKey=\"" + Esc(key) + "\"/>";
    }

    int Db => _volume - 80;

    int Secs()
    {
        double s = _base + (_state == "play" ? (DateTime.UtcNow - _t0).TotalSeconds : 0);
        if (s < 0) s = 0;
        if (s > 200) s = 200;
        return (int)s;
    }

    void SetPos(double seconds)
    {
        _base = Math.Clamp(seconds, 0, 200);
        _t0 = DateTime.UtcNow;
    }

    void Pulse() => Monitor.PulseAll(_gate);

    static string Section(string name) => name.Length > 0 && char.IsAsciiLetter(name[0]) ? char.ToUpperInvariant(name[0]).ToString() : "#";

    static string Esc(string s) => SecurityElement.Escape(s) ?? "";

    static Dictionary<string, string> Query(string? query)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query)) return d;
        if (query[0] == '?') query = query[1..];
        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = part.IndexOf('=');
            string k = Decode(i < 0 ? part : part[..i]);
            string v = i < 0 ? "" : Decode(part[(i + 1)..]);
            d[k] = v;
        }
        return d;
    }

    static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    readonly record struct Album(string Name, string Artist, string[] Tracks);
    readonly record struct Song(string Title, string Artist, string Album, string File);
}
