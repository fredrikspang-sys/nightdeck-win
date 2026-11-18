using System.Globalization;
using Nightdeck.Core;

namespace Nightdeck;

/// <summary>
/// Owns the long-poll loop, queue refresh, cover fetch and sleep fade.
/// Views call these methods for anything that changes the player.
/// Browse and other reads may use <see cref="Client"/> directly.
/// </summary>
public sealed class PlayerSession : IDisposable
{
    readonly CancellationTokenSource _life = new();
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly SynchronizationContext? _ui;
    int _pollGen;
    int _coverGen;
    int _statusGen;
    int _queueGen;
    int _followSleep;
    int _sleepLoop;
    int _owedLevel = int.MinValue;
    int _groupEpoch;
    int _groupRev;
    string _coverKey = "";
    byte[]? _coverBytes;
    bool _disposed;
    List<KnownPlayer> _known = [];
    List<GroupSlave> _slaves = [];

    public PlayerSession(IBluOsClient client, AppSettings settings)
    {
        Client = client;
        Settings = settings;
        _ui = SynchronizationContext.Current;
        Sleep.Enabled = settings.SleepFade;
        Sleep.FadeSeconds = settings.SleepFadeSeconds;
        Sleep.EndWithStop = settings.SleepEndStop;
    }

    public IBluOsClient Client { get; }
    public AppSettings Settings { get; }
    public SleepFade Sleep { get; } = new();

    public PlayerStatus Status { get; private set; } = new() { Ok = false, Song = -1 };
    public PlayQueue Queue { get; private set; } = new();
    public int QueueVersion { get; private set; }
    public string Message { get; private set; } = "";
    public bool Connected { get; private set; }
    public byte[]? CoverBytes => Volatile.Read(ref _coverBytes);
    public string CoverKey { get; private set; } = "";
    public IReadOnlyList<KnownPlayer> KnownPlayers => _known;
    public IReadOnlyList<GroupSlave> Slaves => _slaves;
    public string GroupLabel { get; private set; } = "";

    public int? PendingVolume { get; private set; }
    public bool? PendingStop { get; private set; }
    public bool? PendingMute { get; private set; }
    public bool? PendingShuffle { get; private set; }
    public int? PendingRepeat { get; private set; }
    public bool? PendingPlaying { get; private set; }

    double _secs;
    DateTime _secsAt = DateTime.UtcNow;

    public event EventHandler? Changed;
    public event EventHandler? EndpointChanged;

    public double DisplaySeconds
    {
        get
        {
            double s = _secs;
            if (Status.IsPlaying) s += (DateTime.UtcNow - _secsAt).TotalSeconds;
            if (s < 0) s = 0;
            if (Status.TotalSeconds > 0 && s > Status.TotalSeconds) s = Status.TotalSeconds;
            return s;
        }
    }

    public string RepeatLabel => BluOs.RepeatName(PendingRepeat ?? Status.Repeat);

    /// <summary>True when a stop click should be sent. Disabled while disconnected or already stopped.</summary>
    public bool CanStop => Connected && PendingStop != true && (PendingPlaying == true || Status.State != "stop");

    public string VolumeText
    {
        get
        {
            bool mute = PendingMute ?? Status.Mute;
            int volume = PendingVolume ?? Status.Volume;
            if (mute) return "Muted";
            if (volume < 0) return "Fixed";
            bool dbCurrent = PendingVolume == null || PendingVolume == Status.Volume;
            if (dbCurrent && double.TryParse(Status.Db, NumberStyles.Float, CultureInfo.InvariantCulture, out double db))
                return volume.ToString(CultureInfo.InvariantCulture) + " \u00b7 " + (db + Settings.DbOffset).ToString("0.0", CultureInfo.InvariantCulture) + " dB";
            return volume.ToString(CultureInfo.InvariantCulture);
        }
    }

    public string SleepLabel
    {
        get
        {
            if (Sleep.Active && Sleep.Phase >= 2) return "Sleep fading";
            if (Status.SleepMinutes > 0) return "Sleep " + Status.SleepMinutes + " min";
            return "Sleep off";
        }
    }

    /// <summary>Drop cover bytes once the view and the media overlay have copied them.</summary>
    public void ReleaseCover(byte[] bytes) => Interlocked.CompareExchange(ref _coverBytes, null, bytes);

    public void Start()
    {
        if (string.IsNullOrWhiteSpace(Client.Host))
        {
            Message = "No player yet. Open the player list, or set a host in Settings.";
            Raise();
        }
        RestartPoll();
    }

    public void SetKnownPlayers(IReadOnlyList<KnownPlayer> players)
    {
        _known = players.ToList();
        GroupLabel = FormatGroup(_slaves);
        Raise();
    }

    public bool InGroup(string host, int port)
    {
        foreach (var slave in _slaves)
            if (slave.Port == port && string.Equals(slave.Id, host, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public async Task RefreshGroupAsync()
    {
        int epoch = Volatile.Read(ref _groupEpoch);
        int rev = Volatile.Read(ref _groupRev);
        string host = Client.Host;
        int port = Client.Port;
        try
        {
            var sync = await Client.GetSyncStatusAsync(_life.Token).ConfigureAwait(false);
            if (epoch != Volatile.Read(ref _groupEpoch) || rev != Volatile.Read(ref _groupRev)) return;
            if (!SameEndpoint(host, port)) return;
            _slaves = sync.Slaves.ToList();
            GroupLabel = FormatGroup(_slaves);
            Raise();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // A failed read leaves membership as it was. It is not a failed group command.
        }
    }

    public async Task ToggleGroupAsync(string host, int port)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0 || port is < 1 or > 65535) return;
        string master = Client.Host;
        int masterPort = Client.Port;
        int epoch = Volatile.Read(ref _groupEpoch);
        try
        {
            await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
            try
            {
                if (epoch != Volatile.Read(ref _groupEpoch)) return;
                if (!string.Equals(Client.Host, master, StringComparison.OrdinalIgnoreCase) || Client.Port != masterPort) return;
                if (string.Equals(host, master, StringComparison.OrdinalIgnoreCase) && port == masterPort) return;
                bool on = InGroup(host, port);
                Interlocked.Increment(ref _groupRev);
                if (on) await Client.RemoveSlaveAsync(host, port, _life.Token).ConfigureAwait(false);
                else await Client.AddSlaveAsync(host, port, _life.Token).ConfigureAwait(false);
                if (epoch != Volatile.Read(ref _groupEpoch)) return;
                if (!string.Equals(Client.Host, master, StringComparison.OrdinalIgnoreCase) || Client.Port != masterPort) return;
                var sync = await Client.GetSyncStatusAsync(_life.Token).ConfigureAwait(false);
                if (epoch != Volatile.Read(ref _groupEpoch)) return;
                if (!string.Equals(Client.Host, master, StringComparison.OrdinalIgnoreCase) || Client.Port != masterPort) return;
                _slaves = sync.Slaves.ToList();
                GroupLabel = FormatGroup(_slaves);
                Interlocked.Increment(ref _groupRev);
                Message = (on ? "Ungrouped " : "Grouped ") + DisplayName(host, port);
            }
            finally { _gate.Release(); }
            Raise();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (epoch != Volatile.Read(ref _groupEpoch)) return;
            Message = "Group failed: " + ex.Message;
            Raise();
        }
    }

    bool SameEndpoint(string host, int port)
        => Client.Port == port && string.Equals(Client.Host, host, StringComparison.OrdinalIgnoreCase);

    string DisplayName(string host, int port)
    {
        foreach (var player in _known)
            if (player.Port == port && string.Equals(player.Host, host, StringComparison.OrdinalIgnoreCase) && player.Name.Length > 0)
                return player.Name;
        return port == 11000 ? host : host + ":" + port;
    }

    string FormatGroup(List<GroupSlave> slaves)
    {
        if (slaves.Count == 0) return "";
        var parts = new string[slaves.Count];
        for (int i = 0; i < slaves.Count; i++)
            parts[i] = DisplayName(slaves[i].Id, slaves[i].Port);
        return "+ " + string.Join(" + ", parts);
    }

    public void ApplySettings()
    {
        Sleep.Enabled = Settings.SleepFade;
        Sleep.FadeSeconds = Settings.SleepFadeSeconds <= 0 ? 24 : Settings.SleepFadeSeconds;
        Sleep.EndWithStop = Settings.SleepEndStop;
        string host = (Settings.Host ?? "").Trim();
        int port = Settings.Port;
        if (host.Length == 0 || port is < 1 or > 65535)
        {
            Raise();
            return;
        }
        if (!string.Equals(host, Client.Host, StringComparison.OrdinalIgnoreCase) || port != Client.Port)
        {
            var restore = Sleep.Disarm();
            Volatile.Write(ref _followSleep, 0);
            Interlocked.Increment(ref _pollGen);
            _ = SwitchEndpointAsync(host, port, restore);
            return;
        }
        Raise();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var restore = Sleep.Disarm();
        int level = restore.Op == SleepOp.Restore ? restore.Level : _owedLevel;
        try { _life.Cancel(); } catch (ObjectDisposedException) { }
        if (level != int.MinValue && Client.Host.Length > 0)
        {
            try
            {
                Task.Run(() =>
                {
                    // Wait out an in-flight fade step so its /Volume cannot land after the restore.
                    bool held = false;
                    try
                    {
                        held = _gate.Wait(TimeSpan.FromSeconds(1));
                        Client.SetVolumeAsync(level, CancellationToken.None).GetAwaiter().GetResult();
                    }
                    finally { if (held) _gate.Release(); }
                }).Wait(TimeSpan.FromSeconds(2));
            }
            catch { /* shutting down */ }
        }
        try { Task.Run(() => Client.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(2)); }
        catch { /* shutting down */ }
        _life.Dispose();
    }

    public Task PlayPauseAsync() => Run(async () =>
    {
        await ApplySleepAsync(Sleep.Abort(), _life.Token).ConfigureAwait(false);
        PendingStop = null;
        bool playing = PendingPlaying ?? Status.IsPlaying;
        PendingPlaying = !playing;
        try
        {
            if (playing) await Client.PauseToggleAsync(_life.Token).ConfigureAwait(false);
            else await Client.PlayAsync(_life.Token).ConfigureAwait(false);
        }
        catch
        {
            PendingPlaying = null;
            throw;
        }
    });

    public Task NextAsync() => Run(() => Client.SkipAsync(_life.Token));
    public Task PreviousAsync() => Run(() => Client.BackAsync(_life.Token));

    public Task StopAsync() => Run(async () =>
    {
        await ApplySleepAsync(Sleep.Abort(), _life.Token).ConfigureAwait(false);
        if (!Connected || PendingStop == true) return;
        if (Status.State == "stop" && PendingPlaying != true) return;
        PendingStop = true;
        PendingPlaying = false;
        try { await Client.StopAsync(_life.Token).ConfigureAwait(false); }
        catch
        {
            PendingStop = null;
            PendingPlaying = null;
            throw;
        }
    });

    public Task SeekToAsync(int seconds) => Run(() => Client.SeekAsync(Math.Max(0, seconds), _life.Token), after: () =>
    {
        _secs = Math.Max(0, seconds);
        _secsAt = DateTime.UtcNow;
    });

    public Task SetVolumeAsync(int level) => Run(async () =>
    {
        if (!Connected) return;
        int clamped = Math.Clamp(level, 0, 100);
        if (Status.Volume < 0 && PendingVolume == null) return;
        int current = PendingVolume ?? Status.Volume;
        if (current >= 0 && clamped == current) return;
        PendingVolume = clamped;
        try
        {
            await ApplySleepAsync(Sleep.Abort(), _life.Token).ConfigureAwait(false);
            await Client.SetVolumeAsync(clamped, _life.Token).ConfigureAwait(false);
        }
        catch
        {
            PendingVolume = null;
            throw;
        }
    });

    public Task ToggleMuteAsync() => Run(async () =>
    {
        await ApplySleepAsync(Sleep.Abort(), _life.Token).ConfigureAwait(false);
        bool mute = PendingMute ?? Status.Mute;
        PendingMute = !mute;
        try { await Client.SetMuteAsync(!mute, _life.Token).ConfigureAwait(false); }
        catch
        {
            PendingMute = null;
            throw;
        }
    });

    public Task ToggleShuffleAsync() => Run(async () =>
    {
        bool shuffle = PendingShuffle ?? Status.Shuffle;
        PendingShuffle = !shuffle;
        try { await Client.SetShuffleAsync(!shuffle, _life.Token).ConfigureAwait(false); }
        catch
        {
            PendingShuffle = null;
            throw;
        }
    });

    public Task CycleRepeatAsync() => Run(async () =>
    {
        int next = BluOs.NextRepeat(PendingRepeat ?? Status.Repeat);
        PendingRepeat = next;
        try { await Client.SetRepeatAsync(next, _life.Token).ConfigureAwait(false); }
        catch
        {
            PendingRepeat = null;
            throw;
        }
    });

    public Task StepSleepAsync() => Run(async () =>
    {
        await ApplySleepAsync(Sleep.Abort(), _life.Token).ConfigureAwait(false);
        int minutes = await Client.StepSleepAsync(_life.Token).ConfigureAwait(false);
        Sleep.NoteUserSleep(minutes);
        Volatile.Write(ref _followSleep, 1);
        Message = minutes > 0 ? "Sleep timer: " + minutes + " min" : "Sleep timer: off";
        EnsureSleepLoop();
    });

    public Task PlayQueueSongAsync(int id) => Run(async () =>
    {
        await EnsureQueueCurrent().ConfigureAwait(false);
        await Client.PlaySongAsync(id, _life.Token).ConfigureAwait(false);
    });

    public Task RemoveFromQueueAsync(int id) => Run(async () =>
    {
        await EnsureQueueCurrent().ConfigureAwait(false);
        await Client.DeleteSongAsync(id, _life.Token).ConfigureAwait(false);
    }, refreshQueue: true);

    public Task MoveInQueueAsync(int oldId, int newId) => Run(async () =>
    {
        await EnsureQueueCurrent().ConfigureAwait(false);
        await Client.MoveSongAsync(oldId, newId, _life.Token).ConfigureAwait(false);
    }, refreshQueue: true);

    public Task ClearQueueAsync() => Run(() => Client.ClearQueueAsync(_life.Token), refreshQueue: true);

    public Task AddBrowseItemAsync(BrowseItem item, bool playNow) => Run(async () =>
    {
        int n = await Client.AddItemAsync(item, playNow, _life.Token).ConfigureAwait(false);
        Message = n == 0 ? "Nothing to add" : playNow ? "Playing" : "Added to queue";
    }, refreshQueue: true);

    public Task<IReadOnlyList<Preset>> PresetsAsync() => Client.GetPresetsAsync(_life.Token);
    public Task PlayPresetAsync(string id) => Run(() => Client.PlayPresetAsync(id, _life.Token), refreshQueue: true);
    public Task SavePlaylistAsync(string name) => Run(async () =>
    {
        int n = await Client.SavePlaylistAsync(name, _life.Token).ConfigureAwait(false);
        Message = "Saved playlist '" + name + "' (" + n + " tracks)";
    });
    public Task<IReadOnlyList<BrowseItem>> PlaylistsAsync() => Client.ListPlaylistsAsync(_life.Token);
    public Task LoadPlaylistAsync(BrowseItem item) => Run(() => Client.SendPathAsync(item.PlayUrl, _life.Token), refreshQueue: true);
    public Task DeletePlaylistAsync(BrowseItem item) => Run(async () =>
    {
        string? url = await DeleteUrlAsync(item, _life.Token).ConfigureAwait(false);
        if (url == null) throw new BluOsException("The player offers no delete for '" + item.Text + "'.");
        await Client.SendPathAsync(url, _life.Token).ConfigureAwait(false);
        Message = "Deleted playlist: " + item.Text;
    });

    async Task EnsureQueueCurrent()
    {
        if (Queue.Id.Length == 0) return;
        var meta = await Client.GetQueueMetaAsync(_life.Token).ConfigureAwait(false);
        if (!string.Equals(meta.Id, Queue.Id, StringComparison.Ordinal))
            throw new BluOsException("The queue changed. Refresh and try again.");
    }

    async Task<string?> DeleteUrlAsync(BrowseItem item, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(item.ContextMenuKey)) return null;
        var menu = await Client.BrowseAllAsync(item.ContextMenuKey, null, 4, ct).ConfigureAwait(false);
        foreach (var entry in menu)
            if (string.Equals(entry.Type, "delete", StringComparison.Ordinal) && entry.ActionUrl.Length > 0)
                return entry.ActionUrl;
        return null;
    }

    async Task SwitchEndpointAsync(string host, int port, SleepAction restore)
    {
        Interlocked.Increment(ref _groupEpoch);
        try
        {
            await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
            try
            {
                int owed = _owedLevel;
                int restoreLevel = restore.Op == SleepOp.Restore ? restore.Level : owed;
                if (restoreLevel != int.MinValue && Client.Host.Length > 0)
                {
                    try { await Client.SetVolumeAsync(restoreLevel, _life.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch { /* the old player did not take the restore */ }
                }
                Client.SetEndpoint(host, port);
                ClearPlayerState();
            }
            finally { _gate.Release(); }
            Raise();
            RaiseEndpoint();
            RestartPoll();
            _ = RefreshGroupAsync();
        }
        catch (OperationCanceledException) { }
    }

    void ClearPlayerState()
    {
        Status = new PlayerStatus { Ok = false, Song = -1 };
        Queue = new PlayQueue();
        QueueVersion++;
        Interlocked.Increment(ref _statusGen);
        Interlocked.Increment(ref _queueGen);
        Interlocked.Increment(ref _coverGen);
        _coverKey = "";
        CoverKey = "";
        Interlocked.Exchange(ref _coverBytes, null);
        _owedLevel = int.MinValue;
        PendingVolume = PendingRepeat = null;
        PendingMute = PendingShuffle = PendingPlaying = PendingStop = null;
        Connected = false;
        Message = "";
        _slaves = [];
        GroupLabel = "";
        _secs = 0;
        _secsAt = DateTime.UtcNow;
    }

    void RestartPoll()
    {
        int gen = Interlocked.Increment(ref _pollGen);
        if (string.IsNullOrWhiteSpace(Client.Host)) return;
        _ = PollAsync(gen, _life.Token);
    }

    async Task PollAsync(int gen, CancellationToken ct)
    {
        string? etag = null;
        int errors = 0;
        while (!ct.IsCancellationRequested && gen == Volatile.Read(ref _pollGen))
        {
            long started = Environment.TickCount64;
            int seen = Volatile.Read(ref _statusGen);
            bool longPoll = etag != null;
            try
            {
                PlayerStatus st = longPoll
                    ? await Client.PollStatusAsync(etag!, 60, ct).ConfigureAwait(false)
                    : await Client.GetStatusAsync(ct).ConfigureAwait(false);
                if (gen != Volatile.Read(ref _pollGen) || ct.IsCancellationRequested) return;
                errors = 0;
                string? next = string.IsNullOrEmpty(st.Etag) ? null : st.Etag;
                bool sameFast = longPoll && next != null && next == etag && Environment.TickCount64 - started < 30_000;
                etag = next;
                bool restore = false;
                bool stop = false;
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (gen != Volatile.Read(ref _pollGen)) stop = true;
                    else if (seen == Volatile.Read(ref _statusGen))
                    {
                        ApplyStatus(st);
                        if (_owedLevel != int.MinValue && st.Volume == _owedLevel) _owedLevel = int.MinValue;
                        restore = _owedLevel != int.MinValue;
                    }
                }
                finally { _gate.Release(); }
                if (stop) return;
                if (restore) _ = RestoreOwedAsync();
                await GapAsync(started, etag == null || sameFast, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (gen != Volatile.Read(ref _pollGen) || ct.IsCancellationRequested) return;
                errors++;
                try
                {
                    await _gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (gen != Volatile.Read(ref _pollGen)) return;
                        var lost = Sleep.Tick(new PlayerStatus { Ok = false, Song = -1 }, MonoNow());
                        if (lost.Op == SleepOp.Restore) _owedLevel = lost.Level;
                        Connected = false;
                        Message = string.IsNullOrEmpty(lost.Message)
                            ? "Waiting for " + Client.Host + ":" + Client.Port + " — " + ex.Message
                            : lost.Message;
                    }
                    finally { _gate.Release(); }
                }
                catch (OperationCanceledException) { return; }
                Raise();
                etag = null;
                int delay = Math.Min(4000, 1000 << Math.Min(errors - 1, 2));
                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    static async Task GapAsync(long started, bool slow, CancellationToken ct)
    {
        double gap = (slow ? 5.0 : 1.0) - (Environment.TickCount64 - started) / 1000.0;
        if (gap > 0.01)
            await Task.Delay(TimeSpan.FromMilliseconds(gap * 1000.0), ct).ConfigureAwait(false);
    }

    async Task RestoreOwedAsync()
    {
        int level = _owedLevel;
        if (level == int.MinValue) return;
        try
        {
            await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
            try
            {
                if (_owedLevel != level || _life.IsCancellationRequested) return;
                await Client.SetVolumeAsync(level, _life.Token).ConfigureAwait(false);
                if (_owedLevel == level) _owedLevel = int.MinValue;
            }
            finally { _gate.Release(); }
            Raise();
        }
        catch (OperationCanceledException) { }
        catch { /* keep the owed level for the next status */ }
    }

    void ApplyStatus(PlayerStatus st)
    {
        string previousPid = Status.PlaylistId;
        Status = st;
        _secs = st.Secs;
        _secsAt = DateTime.UtcNow;
        bool opened = !Connected;
        Connected = true;
        if (opened) _ = RefreshGroupAsync();
        if (PendingPlaying is bool playing && playing == st.IsPlaying) PendingPlaying = null;
        if (PendingStop == true && st.State == "stop") PendingStop = null;
        if (PendingMute is bool mute && mute == st.Mute) PendingMute = null;
        if (PendingShuffle is bool shuffle && shuffle == st.Shuffle) PendingShuffle = null;
        if (PendingRepeat is int repeat && repeat == st.Repeat) PendingRepeat = null;
        if (PendingVolume is int volume && (st.Volume < 0 || volume == st.Volume)) PendingVolume = null;
        if (Message.StartsWith("Waiting for ", StringComparison.Ordinal)) Message = "";
        if (Volatile.Read(ref _followSleep) == 1 || Sleep.Active)
            Sleep.Observe(st.SleepMinutes);
        if (!string.Equals(previousPid, st.PlaylistId, StringComparison.Ordinal))
            _ = RefreshQueueAsync();
        else if (Queue.Songs.Count == 0 && st.Song >= 0)
            _ = RefreshQueueAsync();
        MaybeCover(st);
        EnsureSleepLoop();
        Raise();
    }

    void MaybeCover(PlayerStatus st)
    {
        string key = st.Image;
        if (key.Length == 0 && st.Service.Length > 0)
            key = "/Artwork?service=" + Uri.EscapeDataString(st.Service);
        if (key == _coverKey) return;
        _coverKey = key;
        CoverKey = key;
        Interlocked.Increment(ref _coverGen);
        Interlocked.Exchange(ref _coverBytes, null);
        if (key.Length == 0) return;
        int gen = Volatile.Read(ref _coverGen);
        _ = FetchCoverAsync(key, gen);
    }

    async Task FetchCoverAsync(string key, int gen)
    {
        try
        {
            var bytes = await Client.GetBytesAsync(key, 1_500_000, _life.Token).ConfigureAwait(false);
            if (gen != Volatile.Read(ref _coverGen) || bytes == null || bytes.Length == 0) return;
            Volatile.Write(ref _coverBytes, bytes);
            Raise();
        }
        catch (OperationCanceledException) { }
        catch { /* art is optional */ }
    }

    async Task RefreshQueueAsync()
    {
        int gen = Interlocked.Increment(ref _queueGen);
        try
        {
            var q = await Client.GetQueueAsync(_life.Token).ConfigureAwait(false);
            if (gen != Volatile.Read(ref _queueGen)) return;
            Queue = q;
            QueueVersion++;
            Raise();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Message = ex.Message;
            Raise();
        }
    }

    void EnsureSleepLoop()
    {
        if (!Sleep.Active && (Volatile.Read(ref _followSleep) == 0 || Status.SleepMinutes <= 0)) return;
        if (Interlocked.CompareExchange(ref _sleepLoop, 1, 0) != 0) return;
        _ = SleepLoopAsync();
    }

    async Task SleepLoopAsync()
    {
        try
        {
            while (!_life.IsCancellationRequested)
            {
                // A switch clears the follow flag and disarms. Do not tick the next
                // player's countdown: that would fade or pause a timer this process did not start.
                if (SleepFollowIdle()) break;
                await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
                try
                {
                    if (SleepFollowIdle()) break;
                    var action = Sleep.Tick(Status, MonoNow());
                    if (action.Op == SleepOp.NeedFresh)
                    {
                        var fresh = await Client.GetStatusAsync(_life.Token).ConfigureAwait(false);
                        Interlocked.Increment(ref _statusGen);
                        Status = fresh;
                        _secs = fresh.Secs;
                        _secsAt = DateTime.UtcNow;
                        if (Volatile.Read(ref _followSleep) == 1 || Sleep.Active)
                            Sleep.Observe(fresh.SleepMinutes);
                        var now = MonoNow();
                        action = Sleep.Phase == 3 ? Sleep.Resolve(fresh, now) : Sleep.Begin(fresh, now);
                        Raise();
                    }
                    if (SleepFollowIdle()) break;
                    await ApplySleepAsync(action, _life.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Message = "Sleep: " + ex.Message;
                    Raise();
                }
                finally { _gate.Release(); }

                if (SleepFollowIdle()) break;
                if (!Sleep.Active && Status.SleepMinutes <= 0)
                {
                    Volatile.Write(ref _followSleep, 0);
                    Sleep.Disarm();
                    break;
                }
                try { await Task.Delay(Sleep.Active ? 200 : 1000, _life.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Interlocked.Exchange(ref _sleepLoop, 0);
            if (!_life.IsCancellationRequested && (Sleep.Active || (Volatile.Read(ref _followSleep) == 1 && Status.SleepMinutes > 0)))
                EnsureSleepLoop();
        }
    }

    /// <summary>True when this process is no longer following a sleep timer and no fade is running.</summary>
    bool SleepFollowIdle()
    {
        if (Volatile.Read(ref _followSleep) != 0 || Sleep.Active) return false;
        Sleep.Disarm();
        return true;
    }

    static DateTime MonoNow() => DateTime.UnixEpoch.AddMilliseconds(Environment.TickCount64);

    async Task Run(Func<Task> work, bool refreshQueue = false, Action? after = null)
    {
        try
        {
            await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
            try
            {
                await work().ConfigureAwait(false);
                after?.Invoke();
            }
            finally { _gate.Release(); }
            if (refreshQueue) await RefreshQueueAsync().ConfigureAwait(false);
            Raise();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Message = ex.Message;
            Raise();
        }
    }

    async Task ApplySleepAsync(SleepAction action, CancellationToken ct)
    {
        if (!action.HasWork || ct.IsCancellationRequested) return;
        if (!string.IsNullOrEmpty(action.Message)) Message = action.Message;
        try
        {
            await DoSleepOp(action.Op, action.Level, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;
            await DoSleepOp(action.Then, action.Level, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var abort = Sleep.Abort();
            Message = "Sleep: " + ex.Message;
            if (abort.Op == SleepOp.Restore)
            {
                if (ct.IsCancellationRequested) _owedLevel = abort.Level;
                else
                {
                    try { await Client.SetVolumeAsync(abort.Level, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { _owedLevel = abort.Level; }
                    catch { _owedLevel = abort.Level; }
                }
            }
        }
        if (!ct.IsCancellationRequested) Raise();
    }

    Task DoSleepOp(SleepOp op, int level, CancellationToken ct) => op switch
    {
        SleepOp.SetLevel or SleepOp.Restore => Client.SetVolumeAsync(level, ct),
        SleepOp.Pause => Client.PauseAsync(ct),
        SleepOp.Stop => Client.StopAsync(ct),
        _ => Task.CompletedTask,
    };

    void Raise() => Post(Changed);
    void RaiseEndpoint() => Post(EndpointChanged);

    void Post(EventHandler? handler)
    {
        if (handler == null) return;
        if (_ui != null) _ui.Post(_ => handler(this, EventArgs.Empty), null);
        else handler(this, EventArgs.Empty);
    }
}

public sealed class KnownPlayer
{
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 11000;
    public bool Manual { get; init; }
}
