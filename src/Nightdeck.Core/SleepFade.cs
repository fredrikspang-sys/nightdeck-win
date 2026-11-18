namespace Nightdeck.Core;

public enum SleepOp { None, SetLevel, Pause, Stop, Restore, Lost, NeedFresh }

/// <summary>
/// What the session should do after one sleep tick. At most one volume/transport op,
/// plus an optional follow-up (the last fade step also pauses or stops).
/// </summary>
public readonly struct SleepAction
{
    public SleepOp Op { get; init; }
    public SleepOp Then { get; init; }
    public int Level { get; init; }
    public string? Message { get; init; }

    public bool HasWork => Op != SleepOp.None || Then != SleepOp.None || !string.IsNullOrEmpty(Message);

    public static SleepAction SetLevel(int level) => new() { Op = SleepOp.SetLevel, Level = level };
    public static SleepAction Pause(string? message = null) => new() { Op = SleepOp.Pause, Message = message };
    public static SleepAction Stop(string? message = null) => new() { Op = SleepOp.Stop, Message = message };
    public static SleepAction Restore(int level, string? message = null) => new() { Op = SleepOp.Restore, Level = level, Message = message };
    public static SleepAction Note(string message) => new() { Message = message };
    public static SleepAction LostConnection(string message) => new() { Op = SleepOp.Lost, Message = message };
    public static SleepAction Fresh() => new() { Op = SleepOp.NeedFresh };

    public SleepAction WithThen(SleepOp then) => new() { Op = Op, Then = then, Level = Level, Message = Message };
    public SleepAction WithMessage(string message) => new() { Op = Op, Then = Then, Level = Level, Message = message };
}

/// <summary>
/// bluplay 0.3.1 sleep-at-zero. Some firmware counts /Sleep down to 0 and keeps playing.
/// When the last seen value was 1 or 2 minutes and the timer hits 0 while still playing,
/// wait 3 s for the player to pause itself. If it does not, fade the volume to 0 in 16
/// steps and then pause (or stop when <see cref="EndWithStop"/>). Restore the volume after.
/// </summary>
public sealed class SleepFade
{
    public const double GraceSeconds = 3;
    public const int Steps = 16;
    public const double PauseWaitSeconds = 10;

    readonly Lock _lock = new();
    int _phase;
    int _prevSleep;
    int _orig = -1;
    int _last = -1;
    int _step;
    DateTime _next;
    DateTime _deadline;

    public bool Enabled { get; set; } = true;
    public double FadeSeconds { get; set; } = 24;
    public bool EndWithStop { get; set; }

    public int Phase { get { lock (_lock) return _phase; } }
    public bool Active { get { lock (_lock) return _phase != 0; } }
    public int OriginalVolume { get { lock (_lock) return _orig; } }

    public bool VolumeLowered
    {
        get { lock (_lock) return LoweredUnlocked; }
    }

    bool LoweredUnlocked => _phase >= 2 && _orig > 0 && _last >= 0 && _last < _orig;

    double StepSpan => Math.Clamp(FadeSeconds, 2, 300) / Steps;

    static bool Playing(PlayerStatus st) => st.Ok && (st.State == "play" || st.State == "stream");

    /// <summary>Remember a positive sleep value while idle so a later drop to 0 is visible.</summary>
    public void Observe(int sleepMinutes)
    {
        lock (_lock)
        {
            if (_phase == 0 && sleepMinutes > 0) _prevSleep = sleepMinutes;
        }
    }

    public void NoteUserSleep(int minutes)
    {
        lock (_lock) _prevSleep = Math.Max(0, minutes);
    }

    public SleepAction Abort()
    {
        lock (_lock)
        {
            if (_phase == 0) return default;
            bool low = LoweredUnlocked;
            int orig = _orig;
            _phase = 0;
            _last = -1;
            return low
                ? SleepAction.Restore(orig, "Sleep: fade cancelled, volume restored")
                : SleepAction.Note("Sleep: fade cancelled");
        }
    }

    public SleepAction Tick(PlayerStatus status, DateTime utcNow)
    {
        lock (_lock)
        {
            if (!status.Ok)
            {
                _prevSleep = 0;
                if (_phase == 0) return default;
                bool low = LoweredUnlocked;
                int orig = _orig;
                _phase = 0;
                _last = -1;
                return low
                    ? SleepAction.Restore(orig, "Sleep: connection lost, fade stopped")
                    : SleepAction.LostConnection("Sleep: connection lost, fade stopped");
            }

            if (_phase == 0)
            {
                if (status.SleepMinutes == 0 && _prevSleep is > 0 and <= 2 && Playing(status))
                {
                    _phase = 1;
                    _deadline = utcNow.AddSeconds(GraceSeconds);
                }
                _prevSleep = status.SleepMinutes;
                return default;
            }

            _prevSleep = status.SleepMinutes;
            if (_phase == 1)
            {
                if (!Playing(status)) { _phase = 0; return default; }
                if (utcNow >= _deadline) return SleepAction.Fresh();
                return default;
            }

            if (_phase == 2)
            {
                if (!Playing(status))
                {
                    bool low = LoweredUnlocked;
                    int orig = _orig;
                    _phase = 0;
                    _last = -1;
                    return low ? SleepAction.Restore(orig, "Sleep: playback stopped, volume restored") : default;
                }
                if (utcNow < _next) return default;
                _step++;
                int level = (int)((long)_orig * (Steps - _step) / Steps);
                if (level < 0) level = 0;
                SleepAction action = default;
                if (level < _last)
                {
                    _last = level;
                    action = SleepAction.SetLevel(level);
                }
                if (_step >= Steps)
                {
                    _phase = 3;
                    _deadline = utcNow.AddSeconds(PauseWaitSeconds);
                    var end = EndWithStop ? SleepOp.Stop : SleepOp.Pause;
                    return action.Op == SleepOp.SetLevel ? action.WithThen(end) : (EndWithStop ? SleepAction.Stop() : SleepAction.Pause());
                }
                _next = utcNow.AddSeconds(StepSpan);
                return action;
            }

            if (!Playing(status))
            {
                bool low = LoweredUnlocked;
                int orig = _orig;
                _phase = 0;
                _last = -1;
                string done = EndWithStop
                    ? (low ? "Sleep: stopped, volume restored" : "Sleep: stopped")
                    : (low ? "Sleep: paused, volume restored" : "Sleep: paused");
                return low ? SleepAction.Restore(orig, done) : SleepAction.Note(done);
            }
            // A cached "play" can be a stalled poll. The session fetches /Status, then Resolve.
            if (utcNow >= _deadline) return SleepAction.Fresh();
            return default;
        }
    }

    /// <summary>
    /// Phase 3's pause/stop deadline, with a status read just now. Still playing means the
    /// pause or stop did not take. Already paused or stopped means the timer finished.
    /// </summary>
    public SleepAction Resolve(PlayerStatus fresh, DateTime utcNow)
    {
        lock (_lock)
        {
            if (_phase != 3) return default;
            if (Playing(fresh) && utcNow < _deadline) return default;
            bool playing = Playing(fresh);
            bool low = LoweredUnlocked;
            int orig = _orig;
            _phase = 0;
            _last = -1;
            if (playing)
            {
                string msg = EndWithStop
                    ? (low ? "Sleep: the stop did not take effect; volume restored" : "Sleep: the stop did not take effect")
                    : (low ? "Sleep: the pause did not take effect; volume restored" : "Sleep: the pause did not take effect");
                return low ? SleepAction.Restore(orig, msg) : SleepAction.Note(msg);
            }
            string done = EndWithStop
                ? (low ? "Sleep: stopped, volume restored" : "Sleep: stopped")
                : (low ? "Sleep: paused, volume restored" : "Sleep: paused");
            return low ? SleepAction.Restore(orig, done) : SleepAction.Note(done);
        }
    }

    /// <summary>Leave the fade idle and forget the countdown. Restores volume when this process lowered it.</summary>
    public SleepAction Disarm()
    {
        var action = Abort();
        lock (_lock) _prevSleep = 0;
        return action;
    }

    /// <summary>Called with a fresh /Status once <see cref="SleepAction.Fresh"/> was returned.</summary>
    public SleepAction Begin(PlayerStatus fresh, DateTime utcNow)
    {
        lock (_lock)
        {
            if (_phase != 1) return default;
            if (!Playing(fresh)) { _phase = 0; return default; }
            _last = -1;
            _orig = Enabled && !fresh.Mute && fresh.Volume > 0 ? fresh.Volume : -1;
            if (_orig <= 0)
            {
                _orig = -1;
                _phase = 3;
                _deadline = utcNow.AddSeconds(PauseWaitSeconds);
                return EndWithStop
                    ? SleepAction.Stop("Sleep: timer ended, stopping")
                    : SleepAction.Pause("Sleep: timer ended, pausing");
            }
            _last = _orig;
            _step = 0;
            _phase = 2;
            _next = utcNow;
            return SleepAction.Note("Sleep: fading out…");
        }
    }
}
