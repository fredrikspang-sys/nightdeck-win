namespace Nightdeck.Core;

/// <summary>
/// BluOS HTTP API used by bluplay 0.3.1. Implementations must be safe to call
/// while a long-poll is in flight. Query values are encoded like bluplay's urlq
/// (space as '+', unreserved left as-is). Action paths returned by the player
/// (playURL, actionURL) are sent as-is, without a second encoding pass.
/// </summary>
public interface IBluOsClient : IAsyncDisposable
{
    string Host { get; }
    int Port { get; }
    string BaseUrl { get; }

    void SetEndpoint(string host, int port);

    Task<PlayerStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// GET /Status?timeout=SECONDS&amp;etag=ETAG. Completes when the etag changes,
    /// the player closes the poll, or the timeout elapses. Must honour <paramref name="ct"/>
    /// immediately (HttpClient.Timeout must not be the only deadline).
    /// </summary>
    Task<PlayerStatus> PollStatusAsync(string etag, int timeoutSeconds, CancellationToken ct = default);

    Task<PlayQueue> GetQueueAsync(CancellationToken ct = default);
    Task<(int Length, string Id)> GetQueueMetaAsync(CancellationToken ct = default);

    Task<BrowseResult> BrowseAsync(string key, string? query = null, CancellationToken ct = default);

    /// <summary>Follows nextKey. Stops on a repeated key or after <paramref name="maxPages"/>.</summary>
    Task<IReadOnlyList<BrowseItem>> BrowseAllAsync(string key, string? query = null, int maxPages = 200, CancellationToken ct = default);

    /// <summary>GET a player-supplied path such as /Add?... or /Load?... or an absolute URL.</summary>
    Task SendPathAsync(string pathOrAbsoluteUrl, CancellationToken ct = default);

    Task PlayAsync(CancellationToken ct = default);
    Task PauseToggleAsync(CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task SkipAsync(CancellationToken ct = default);
    Task BackAsync(CancellationToken ct = default);
    Task SeekAsync(int seconds, CancellationToken ct = default);
    Task PlaySongAsync(int id, CancellationToken ct = default);

    Task SetVolumeAsync(int level, CancellationToken ct = default);
    Task NudgeVolumeDbAsync(int deltaDb, CancellationToken ct = default);
    Task SetMuteAsync(bool mute, CancellationToken ct = default);

    Task SetShuffleAsync(bool on, CancellationToken ct = default);
    Task SetRepeatAsync(int state, CancellationToken ct = default);

    Task DeleteSongAsync(int id, CancellationToken ct = default);
    Task ClearQueueAsync(CancellationToken ct = default);
    Task MoveSongAsync(int oldIndex, int newIndex, CancellationToken ct = default);

    /// <summary>
    /// bluplay add(): context menu action, else recurse containers (depth &lt; 3, max 1500 nodes),
    /// else playURL. playNow inserts after the current track (where=next, clear=0). Otherwise appends.
    /// Returns the number of successful player calls.
    /// </summary>
    Task<int> AddItemAsync(BrowseItem item, bool playNow, CancellationToken ct = default);

    /// <summary>GET /Sleep (cycles 15, 30, 45, 60, 90, off). Returns the new minutes, 0 = off.</summary>
    Task<int> StepSleepAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Preset>> GetPresetsAsync(CancellationToken ct = default);
    Task PlayPresetAsync(string id, CancellationToken ct = default);

    /// <summary>Returns the entries count from the save response when present.</summary>
    Task<int> SavePlaylistAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<BrowseItem>> ListPlaylistsAsync(CancellationToken ct = default);

    /// <summary>Relative path or absolute URL. Null when the player returns 404. Caps the body at maxBytes.</summary>
    Task<byte[]?> GetBytesAsync(string pathOrAbsoluteUrl, int maxBytes, CancellationToken ct = default);

    /// <summary>GET /SyncStatus. Slave membership is &lt;slave id=".." port=".."/&gt;. An &lt;error&gt; root throws BluOsException.</summary>
    Task<SyncStatus> GetSyncStatusAsync(CancellationToken ct = default);

    /// <summary>GET /AddSlave?slave=IP&amp;port=PORT. &lt;error&gt; throws BluOsException.</summary>
    Task AddSlaveAsync(string host, int port, CancellationToken ct = default);

    /// <summary>GET /RemoveSlave?slave=IP&amp;port=PORT. &lt;error&gt; throws BluOsException.</summary>
    Task RemoveSlaveAsync(string host, int port, CancellationToken ct = default);
}
