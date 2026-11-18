using Nightdeck.Core;

namespace Nightdeck.Tests;

static class SleepTests
{
    static PlayerStatus St(string state, int sleep, int volume = 40, bool mute = false, bool ok = true) => new()
    {
        Ok = ok,
        State = state,
        SleepMinutes = sleep,
        Volume = volume,
        Mute = mute,
        Song = 0,
    };

    public static void Run()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var sf = new SleepFade { Enabled = true, FadeSeconds = 16 };

        sf.Tick(St("play", 0), t);
        TestHarness.Check(sf.Phase == 0, "idle when sleep stays off");

        sf.Observe(15);
        sf.Tick(St("play", 15), t);
        sf.Tick(St("play", 0), t);
        TestHarness.Check(sf.Phase == 0, "turning sleep off from 15 does not fade");

        sf = new SleepFade { Enabled = true, FadeSeconds = 16 };
        sf.Observe(2);
        sf.Tick(St("play", 0), t);
        TestHarness.Equal(sf.Phase, 1, "grace starts when sleep drops from 2 to 0");

        sf.Tick(St("pause", 0), t.AddSeconds(1));
        TestHarness.Equal(sf.Phase, 0, "player paused itself during grace");

        sf = new SleepFade { Enabled = true, FadeSeconds = 16 };
        sf.Observe(1);
        sf.Tick(St("play", 0), t);
        var fresh = sf.Tick(St("play", 0), t.AddSeconds(3));
        TestHarness.Equal(fresh.Op, SleepOp.NeedFresh, "grace asks for a fresh status");
        var began = sf.Begin(St("play", 0, volume: -1), t.AddSeconds(3));
        TestHarness.Equal(began.Op, SleepOp.Pause, "fixed volume pauses without a fade");
        TestHarness.Equal(sf.Phase, 3, "fixed volume waits for the pause");

        sf = new SleepFade { Enabled = true, FadeSeconds = 16, EndWithStop = true };
        sf.Observe(1);
        sf.Tick(St("play", 0), t);
        sf.Tick(St("play", 0), t.AddSeconds(3));
        var stop = sf.Begin(St("play", 0, mute: true), t.AddSeconds(3));
        TestHarness.Equal(stop.Op, SleepOp.Stop, "muted sleep ends with stop when configured");

        sf = new SleepFade { Enabled = false, FadeSeconds = 16 };
        sf.Observe(1);
        sf.Tick(St("play", 0), t);
        sf.Tick(St("play", 0), t.AddSeconds(3));
        var noFade = sf.Begin(St("play", 0, volume: 50), t.AddSeconds(3));
        TestHarness.Equal(noFade.Op, SleepOp.Pause, "fade disabled only pauses");

        sf = new SleepFade { Enabled = true, FadeSeconds = 16 };
        sf.Observe(1);
        sf.Tick(St("play", 0), t);
        sf.Tick(St("play", 0), t.AddSeconds(3));
        var msg = sf.Begin(St("play", 0, volume: 80), t.AddSeconds(3));
        TestHarness.Check(msg.Message != null && msg.Message.Contains("fading", StringComparison.Ordinal), "fade announces itself");
        TestHarness.Equal(sf.Phase, 2, "fade phase");

        var playing = St("play", 0, volume: 80);
        var first = sf.Tick(playing, t.AddSeconds(3));
        TestHarness.Equal(first.Op, SleepOp.SetLevel, "first fade step sets a level");
        TestHarness.Equal(first.Level, 75, "80 * 15/16");

        SleepAction last = default;
        for (int i = 1; i <= 20 && sf.Phase == 2; i++)
            last = sf.Tick(playing, t.AddSeconds(3 + i));
        TestHarness.Equal(sf.Phase, 3, "fade reaches the pause wait");
        TestHarness.Equal(last.Then, SleepOp.Pause, "last step pauses");
        TestHarness.Equal(last.Level, 0, "last step is silence");

        var done = sf.Tick(St("pause", 0, volume: 0), t.AddSeconds(30));
        TestHarness.Equal(done.Op, SleepOp.Restore, "volume restored after pause");
        TestHarness.Equal(done.Level, 80, "restored to the original level");
        TestHarness.Equal(sf.Phase, 0, "idle after restore");

        sf = new SleepFade { Enabled = true, FadeSeconds = 16 };
        sf.Observe(1);
        sf.Tick(St("play", 0), t);
        sf.Tick(St("play", 0), t.AddSeconds(3));
        sf.Begin(St("play", 0, volume: 32), t.AddSeconds(3));
        sf.Tick(St("play", 0, volume: 32), t.AddSeconds(3));
        var cancel = sf.Abort();
        TestHarness.Equal(cancel.Op, SleepOp.Restore, "abort restores a lowered volume");
        TestHarness.Equal(cancel.Level, 32, "abort restores the original");
        TestHarness.Equal(sf.Phase, 0, "abort clears the phase");
        TestHarness.Equal(sf.Abort().Op, SleepOp.None, "second abort is a no-op");

        sf = new SleepFade { Enabled = true, FadeSeconds = 16 };
        sf.Observe(2);
        sf.Tick(St("play", 0), t);
        var lost = sf.Tick(St("play", 0, ok: false), t.AddSeconds(1));
        TestHarness.Equal(lost.Op, SleepOp.Lost, "lost connection during grace does not restore");
        TestHarness.Equal(sf.Phase, 0, "lost connection clears the phase");

        sf.NoteUserSleep(30);
        sf.Tick(St("play", 0), t);
        TestHarness.Equal(sf.Phase, 0, "explicit sleep step to a value is not a natural ending");

        sf = new SleepFade { Enabled = true, FadeSeconds = 16 };
        sf.Observe(1);
        sf.Tick(St("play", 0), t);
        sf.Tick(St("play", 0), t.AddSeconds(3));
        sf.Begin(St("play", 0, volume: 80), t.AddSeconds(3));
        for (int i = 0; i < 20 && sf.Phase == 2; i++)
            sf.Tick(St("play", 0, volume: 80), t.AddSeconds(3 + i));
        var ask = sf.Tick(St("play", 0, volume: 0), t.AddSeconds(40));
        TestHarness.Equal(ask.Op, SleepOp.NeedFresh, "a still-playing deadline asks for a fresh status");
        var back = sf.Resolve(St("pause", 0, volume: 0), t.AddSeconds(40));
        TestHarness.Equal(back.Op, SleepOp.Restore, "fresh pause restores the faded volume");
        TestHarness.Equal(back.Level, 80, "fresh pause restores the original level");
        TestHarness.Equal(sf.Phase, 0, "resolve clears the phase");
    }
}
