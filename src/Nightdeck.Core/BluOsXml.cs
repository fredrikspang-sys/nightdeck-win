using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Nightdeck.Core;

public static class BluOsXml
{
    public static PlayerStatus ParseStatus(string xml)
    {
        var root = Require(xml, "status");
        string title1 = Child(root, "title1"), name = Child(root, "name");
        string title2 = Child(root, "title2"), artist = Child(root, "artist");
        string title3 = Child(root, "title3"), album = Child(root, "album");
        string format = Child(root, "streamFormat");
        if (format.Length == 0) format = Child(root, "quality");
        string repeatText = Child(root, "repeat");
        int repeat = repeatText.Length == 0 ? -1 : Int(repeatText, -1);
        if (repeat > 2) repeat = -1;
        int indexing = Int(Child(root, "indexing"), 0);
        if (indexing < 0) indexing = 0;
        int sleep = Int(Child(root, "sleep"), 0);
        if (sleep < 0) sleep = 0;
        return new PlayerStatus
        {
            Ok = true,
            Etag = Attr(root, "etag"),
            State = Child(root, "state"),
            Title = title1.Length > 0 ? title1 : name,
            Artist = title2.Length > 0 ? title2 : artist,
            Album = title3.Length > 0 ? title3 : album,
            Image = Child(root, "image"),
            Volume = Int(Child(root, "volume"), 0),
            Db = Child(root, "db"),
            Secs = Int(Child(root, "secs"), 0),
            TotalSeconds = Int(Child(root, "totlen"), 0),
            Song = Int(Child(root, "song"), -1),
            PlaylistId = Child(root, "pid"),
            Format = format,
            File = Child(root, "fn"),
            StreamUrl = Child(root, "streamUrl"),
            Service = Child(root, "service"),
            Shuffle = Child(root, "shuffle") == "1",
            Repeat = repeat,
            Mute = Child(root, "mute") == "1",
            SleepMinutes = sleep,
            Indexing = indexing,
        };
    }

    public static SyncStatus ParseSyncStatus(string xml)
    {
        var root = Require(xml, "SyncStatus");
        var slaves = new List<GroupSlave>();
        foreach (var el in root.Elements())
        {
            if (el.Name.LocalName != "slave") continue;
            string id = Attr(el, "id");
            if (id.Length == 0) continue;
            slaves.Add(new GroupSlave { Id = id, Port = Int(Attr(el, "port"), 11000) });
        }
        return new SyncStatus { Name = Attr(root, "name"), Slaves = slaves };
    }

    public static PlayQueue ParseQueue(string xml)
    {
        var root = Require(xml, "playlist");
        var songs = new List<QueueSong>();
        foreach (var el in root.Elements())
        {
            if (el.Name.LocalName != "song") continue;
            string title = Child(el, "title");
            songs.Add(new QueueSong
            {
                Id = Int(Attr(el, "id"), songs.Count),
                Title = title.Length == 0 ? "?" : title,
                Artist = Child(el, "art"),
                Album = Child(el, "alb"),
                File = Child(el, "fn"),
            });
        }
        string id = Attr(root, "id");
        if (id.Length == 0) id = Child(root, "id");
        string lengthText = Attr(root, "length");
        if (lengthText.Length == 0) lengthText = Child(root, "length");
        int length = lengthText.Length == 0 ? songs.Count : Int(lengthText, songs.Count);
        return new PlayQueue { Id = id, Length = length, Songs = songs };
    }

    public static (int Length, string Id) ParseQueueMeta(string xml)
    {
        var root = Require(xml, "playlist");
        string length = Child(root, "length"), id = Child(root, "id");
        if (length.Length == 0 || id.Length == 0) throw new BluOsException("bad /Playlist?length=1 reply");
        return (Int(length, 0), id);
    }

    public static BrowseResult ParseBrowse(string xml)
    {
        var root = Require(xml, "browse");
        var items = new List<BrowseItem>();
        foreach (var el in root.Elements())
        {
            if (el.Name.LocalName != "item") continue;
            items.Add(new BrowseItem
            {
                Type = Attr(el, "type"),
                Text = Attr(el, "text"),
                Text2 = Attr(el, "text2"),
                BrowseKey = Attr(el, "browseKey"),
                ContextMenuKey = Attr(el, "contextMenuKey"),
                PlayUrl = Attr(el, "playURL"),
                ActionUrl = Attr(el, "actionURL"),
            });
        }
        return new BrowseResult
        {
            ServiceName = Attr(root, "serviceName"),
            SearchKey = Attr(root, "searchKey"),
            NextKey = Attr(root, "nextKey"),
            Type = Attr(root, "type"),
            Items = items,
        };
    }

    public static IReadOnlyList<Preset> ParsePresets(string xml)
    {
        var root = Load(xml);
        var list = new List<Preset>();
        foreach (var el in root.Elements())
        {
            if (el.Name.LocalName != "preset") continue;
            list.Add(new Preset { Id = Attr(el, "id"), Name = Attr(el, "name") });
        }
        return list;
    }

    public static int ParseSleepMinutes(string xml)
    {
        var root = Load(xml);
        string text = root.Name.LocalName == "sleep" ? root.Value : Child(root, "sleep");
        int n = Int(text, 0);
        return n < 0 ? 0 : n;
    }

    public static string? ErrorDetail(string xml)
    {
        XElement root;
        try { root = Load(xml); }
        catch (BluOsException) { return null; }
        if (root.Name.LocalName != "error") return null;
        return Child(root, "detail");
    }

    public static int ParseEntries(string xml)
    {
        var root = Load(xml);
        return Int(Child(root, "entries"), 0);
    }

    static XElement Require(string xml, string name)
    {
        var root = Load(xml);
        if (root.Name.LocalName == "error")
        {
            string detail = Child(root, "detail");
            throw new BluOsException(detail.Length == 0 ? "error" : detail);
        }
        if (root.Name.LocalName != name)
            throw new BluOsException(name + ": " + root.Name.LocalName);
        return root;
    }

    /// <summary>
    /// BluOS often declares ISO-8859-1, or no encoding, while the body octets are UTF-8.
    /// Decode as UTF-8 before parsing so a name such as Skuggsjá is not read as Latin-1.
    /// </summary>
    public static string Decode(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return "";
        int start = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) start = 3;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    /// <summary>A command reply must be XML and must not be an &lt;error&gt;.</summary>
    public static void EnsureOk(string xml)
    {
        var root = Load(xml);
        if (root.Name.LocalName != "error") return;
        string detail = Child(root, "detail");
        throw new BluOsException(detail.Length == 0 ? "error" : detail);
    }

    static XElement Load(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) throw new BluOsException("empty response");
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            return XDocument.Load(reader).Root ?? throw new BluOsException("empty response");
        }
        catch (BluOsException) { throw; }
        catch (Exception) { throw new BluOsException("invalid response"); }
    }

    static string Child(XElement parent, string name)
        => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";

    static string Attr(XElement el, string name)
        => el.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? "";

    static int Int(string text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
}
