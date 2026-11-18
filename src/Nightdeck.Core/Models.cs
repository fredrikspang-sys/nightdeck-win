namespace Nightdeck.Core;

public static class BluOs
{
    public const string LibraryRoot = "LocalMusic:";
    public const string PlaylistRoot = "BluOS:";
    public const int RepeatQueue = 0;
    public const int RepeatTrack = 1;
    public const int RepeatOff = 2;

    public static string RepeatName(int state) => state switch
    {
        RepeatQueue => "Queue",
        RepeatTrack => "Track",
        _ => "Off",
    };

    /// <summary>off(2) -> queue(0) -> track(1) -> off, matching bluplay.</summary>
    public static int NextRepeat(int state) => state == RepeatOff || state < 0 ? RepeatQueue : state == RepeatQueue ? RepeatTrack : RepeatOff;

    public static string FormatSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        int t = (int)seconds;
        int h = t / 3600;
        int m = (t % 3600) / 60;
        int s = t % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }
}

public sealed class BluOsException(string message) : Exception(message);

public sealed class PlayerStatus
{
    public bool Ok { get; init; } = true;
    public string Etag { get; init; } = "";
    public string State { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string Image { get; init; } = "";
    public int Volume { get; init; }
    public string Db { get; init; } = "";
    public int Secs { get; init; }
    public int TotalSeconds { get; init; }
    public int Song { get; init; } = -1;
    public string PlaylistId { get; init; } = "";
    public string Format { get; init; } = "";
    public string File { get; init; } = "";
    public string StreamUrl { get; init; } = "";
    public string Service { get; init; } = "";
    public bool Shuffle { get; init; }
    public int Repeat { get; init; } = -1;
    public bool Mute { get; init; }
    public int SleepMinutes { get; init; }
    public int Indexing { get; init; }

    public bool IsPlaying => State is "play" or "stream";
    public bool CanSeek => TotalSeconds > 0;
}

public sealed class BrowseItem
{
    public string Type { get; init; } = "";
    public string Text { get; init; } = "";
    public string Text2 { get; init; } = "";
    public string BrowseKey { get; init; } = "";
    public string ContextMenuKey { get; init; } = "";
    public string PlayUrl { get; init; } = "";
    public string ActionUrl { get; init; } = "";

    public bool IsTrack => string.Equals(Type, "track", StringComparison.OrdinalIgnoreCase);
    public bool IsContainer => BrowseKey.Length > 0 && !IsTrack;

    public string Line => string.IsNullOrEmpty(Text2) ? Text : Text + "  —  " + Text2;
}

public sealed class BrowseResult
{
    public string ServiceName { get; init; } = "";
    public string SearchKey { get; init; } = "";
    public string NextKey { get; init; } = "";
    public string Type { get; init; } = "";
    public List<BrowseItem> Items { get; init; } = [];
}

public sealed class QueueSong
{
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string File { get; init; } = "";

    public string Line
    {
        get
        {
            var who = string.IsNullOrEmpty(Artist) ? Album : string.IsNullOrEmpty(Album) ? Artist : Artist + " — " + Album;
            return string.IsNullOrEmpty(who) ? Title : Title + "  —  " + who;
        }
    }
}

public sealed class PlayQueue
{
    public string Id { get; init; } = "";
    public int Length { get; init; }
    public List<QueueSong> Songs { get; init; } = [];
}

public sealed class Preset
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed class DiscoveredPlayer
{
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 11000;
    public string Service { get; init; } = "";

    public string EndPoint => Host + ":" + Port;
}

public sealed class GroupSlave
{
    public string Id { get; init; } = "";
    public int Port { get; init; } = 11000;
}

public sealed class SyncStatus
{
    public string Name { get; init; } = "";
    public List<GroupSlave> Slaves { get; init; } = [];
}

public sealed class AppSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 11000;
    public bool Discovery { get; set; } = true;
    public double DiscoveryTimeoutSeconds { get; set; } = 2;
    public bool SleepFade { get; set; } = true;
    public int SleepFadeSeconds { get; set; } = 24;
    public bool SleepEndStop { get; set; }
    public bool ShowTray { get; set; } = true;
    public double DbOffset { get; set; }

    /// <summary>Extra players typed by the user, each "host" or "host:port". The active player stays Host/Port.</summary>
    public List<string> ManualPlayers { get; set; } = [];
}
