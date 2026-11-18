using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Nightdeck.Core;

namespace Nightdeck;

public partial class NowPlayingView : UserControl
{
    readonly DispatcherTimer _clock;
    readonly Dictionary<string, BitmapImage> _covers = new(StringComparer.Ordinal);
    readonly Queue<string> _coverOrder = new();
    PlayerSession? _session;
    string _shownKey = "";
    bool _suppressSeek;
    bool _seeking;
    bool _volDrag;
    bool _volKey;
    int? _wantVol;
    double _seekOrigin;
    Window? _host;

    public NowPlayingView()
    {
        InitializeComponent();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _clock.Tick += (_, _) => TickClock();
        Loaded += (_, _) =>
        {
            HookWindow();
            SyncClock();
        };
        IsVisibleChanged += (_, _) => SyncClock();
        Seek.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Seek_Down), handledEventsToo: true);
        Seek.PreviewKeyUp += Seek_KeyUp;
        Seek.LostKeyboardFocus += (_, _) => CommitKeyboardSeek();
        Vol.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Vol_Down), handledEventsToo: true);
        Vol.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Vol_Up), handledEventsToo: true);
        Vol.LostMouseCapture += Vol_Lost;
        Vol.PreviewMouseWheel += Vol_Wheel;
        Vol.PreviewKeyDown += Vol_KeyDown;
        Vol.PreviewKeyUp += Vol_KeyUp;
        Vol.LostKeyboardFocus += (_, _) =>
        {
            if (!_volKey) return;
            _volKey = false;
            CommitVolume();
        };
    }

    public void Attach(PlayerSession session)
    {
        _session = session;
        session.Changed += (_, _) =>
        {
            Render();
            SyncClock();
        };
        Render();
        SyncClock();
    }

    void HookWindow()
    {
        var window = Window.GetWindow(this);
        if (window == null || ReferenceEquals(window, _host)) return;
        if (_host != null) _host.StateChanged -= HostState;
        _host = window;
        _host.StateChanged += HostState;
    }

    void HostState(object? sender, EventArgs e) => SyncClock();

    void SyncClock()
    {
        HookWindow();
        bool shown = IsVisible && _host?.WindowState != WindowState.Minimized;
        if (shown && _session?.Status.IsPlaying == true)
        {
            if (!_clock.IsEnabled) _clock.Start();
        }
        else _clock.Stop();
    }

    void TickClock()
    {
        if (_session == null || _seeking) return;
        var st = _session.Status;
        Elapsed.Text = BluOs.FormatSeconds(_session.DisplaySeconds);
        if (!st.CanSeek) return;
        SetSeek(Math.Clamp(_session.DisplaySeconds, 0, Seek.Maximum));
    }

    void Render()
    {
        if (_session == null) return;
        var st = _session.Status;
        TitleText.Text = st.Title.Length == 0 ? "—" : st.Title;
        ArtistText.Text = st.Artist;
        AlbumText.Text = st.Album;
        string group = _session.GroupLabel;
        GroupText.Text = group;
        GroupText.Visibility = group.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (AutomationProperties.GetName(GroupText) != group) AutomationProperties.SetName(GroupText, group);
        FormatText.Text = st.Indexing > 0
            ? (st.Format.Length == 0 ? "Indexing" : st.Format + "   ·   indexing")
            : st.Format;
        bool playing = _session.PendingPlaying ?? st.IsPlaying;
        SetGlyph(PlayBtn, playing ? "\uE769" : "\uE768", playing ? "Pause" : "Play");
        bool shuffle = _session.PendingShuffle ?? st.Shuffle;
        SetGlyph(ShuffleBtn, "\uE8B1", shuffle ? "Shuffle on" : "Shuffle");
        SetActive(ShuffleBtn, shuffle);
        int repeat = _session.PendingRepeat ?? st.Repeat;
        SetGlyph(RepeatBtn,
            repeat == BluOs.RepeatTrack ? "\uE8ED" : repeat == BluOs.RepeatQueue ? "\uE8EE" : "\uF5E7",
            "Repeat " + _session.RepeatLabel);
        SetActive(RepeatBtn, repeat != BluOs.RepeatOff);
        bool mute = _session.PendingMute ?? st.Mute;
        SetGlyph(MuteBtn, mute ? "\uE74F" : "\uE767", mute ? "Unmute" : "Mute");
        SetActive(MuteBtn, mute);
        StopBtn.IsEnabled = _session.CanStop;
        string sleep = _session.SleepLabel;
        if (!Equals(SleepBtn.ToolTip, sleep)) SleepBtn.ToolTip = sleep;
        if (AutomationProperties.GetName(SleepBtn) != sleep) AutomationProperties.SetName(SleepBtn, sleep);
        bool sleepOn = sleep != "Sleep off";
        SetActive(SleepBtn, sleepOn);
        if (sleepOn)
        {
            string caption = sleep == "Sleep fading" ? "fading"
                : sleep.StartsWith("Sleep ", StringComparison.Ordinal) ? sleep[6..]
                : sleep;
            if (SleepCaption.Text != caption) SleepCaption.Text = caption;
            if (SleepCaption.Visibility != Visibility.Visible) SleepCaption.Visibility = Visibility.Visible;
        }
        else if (SleepCaption.Visibility != Visibility.Collapsed)
            SleepCaption.Visibility = Visibility.Collapsed;
        bool editingVol = _volDrag || _wantVol != null || Vol.IsMouseCaptureWithin || Vol.IsKeyboardFocusWithin;
        VolText.Text = editingVol
            ? ((int)Math.Round(Math.Clamp(Vol.Value, 0, 100))).ToString(CultureInfo.InvariantCulture)
            : _session.VolumeText;

        if (_wantVol is int want && (_session.PendingVolume == want || st.Volume == want))
            _wantVol = null;
        int shownVol = _wantVol ?? _session.PendingVolume ?? st.Volume;
        bool fixedVol = shownVol < 0 || st.Volume < 0 && _session.PendingVolume == null && _wantVol == null;
        bool canVol = _session.Connected && !fixedVol;
        Vol.IsEnabled = canVol;
        MuteBtn.IsEnabled = canVol;
        VolWedge.Opacity = canVol ? 1 : 0.4;
        bool editing = editingVol;
        if (!editing)
            Vol.Value = Math.Clamp(shownVol < 0 ? 0 : shownVol, 0, 100);

        Seek.IsEnabled = st.CanSeek;
        Seek.Maximum = st.CanSeek ? st.TotalSeconds : 1;
        Duration.Text = st.CanSeek ? BluOs.FormatSeconds(st.TotalSeconds) : "—";
        if (!_seeking)
        {
            SetSeek(st.CanSeek ? Math.Clamp(_session.DisplaySeconds, 0, Seek.Maximum) : 0);
            Elapsed.Text = BluOs.FormatSeconds(_session.DisplaySeconds);
        }

        string key = _session.CoverKey;
        if (key.Length == 0)
        {
            _shownKey = "";
            Cover.Source = null;
            return;
        }
        if (key != _shownKey)
        {
            _shownKey = key;
            Cover.Source = null;
        }
        if (Cover.Source == null && _session.CoverBytes is { Length: > 0 } bytes)
        {
            var bmp = CoverFor(key, bytes);
            if (bmp != null) Cover.Source = bmp;
            if (!SmtcController.IsActive) _session.ReleaseCover(bytes);
        }
    }

    void SetGlyph(Button b, string glyph, string label)
    {
        if (!Equals(b.Content, glyph)) b.Content = glyph;
        if (!Equals(b.ToolTip, label)) b.ToolTip = label;
        if (AutomationProperties.GetName(b) != label) AutomationProperties.SetName(b, label);
    }

    void SetActive(Button b, bool on)
    {
        if (b.Tag is bool cur && cur == on) return;
        b.Tag = on;
        b.SetResourceReference(ForegroundProperty, on ? "AccentBrush" : "TextBrush");
        b.SetResourceReference(BorderBrushProperty, on ? "AccentBrush" : "LineBrush");
        b.SetResourceReference(BackgroundProperty, on ? "SelectBrush" : "Surface2Brush");
    }

    void SetSeek(double value)
    {
        _suppressSeek = true;
        Seek.Value = value;
        _suppressSeek = false;
        if (!_seeking) _seekOrigin = value;
    }

    BitmapImage? CoverFor(string key, byte[] bytes)
    {
        if (key.Length == 0) key = "\0";
        if (_covers.TryGetValue(key, out var hit)) return hit;
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = 320;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            _covers[key] = bmp;
            _coverOrder.Enqueue(key);
            while (_coverOrder.Count > 8)
            {
                string old = _coverOrder.Dequeue();
                if (!_coverOrder.Contains(old)) _covers.Remove(old);
            }
            return bmp;
        }
        catch { return null; }
    }

    void Seek_Down(object sender, MouseButtonEventArgs e) => _seeking = true;

    void Seek_Up(object sender, MouseButtonEventArgs e) => EndSeek();

    void Seek_Lost(object sender, MouseEventArgs e) => EndSeek();

    void EndSeek()
    {
        if (!_seeking) return;
        _seeking = false;
        _seekOrigin = Seek.Value;
        if (_session == null || !_session.Status.CanSeek || _suppressSeek) return;
        _ = _session.SeekToAsync((int)Seek.Value);
    }

    void Seek_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            CommitKeyboardSeek();
    }

    void CommitKeyboardSeek()
    {
        if (_seeking || _session == null || !_session.Status.CanSeek) return;
        if (Math.Abs(Seek.Value - _seekOrigin) < 0.5) return;
        _seekOrigin = Seek.Value;
        _ = _session.SeekToAsync((int)Seek.Value);
    }

    void Vol_Down(object sender, MouseButtonEventArgs e) => _volDrag = true;

    void Vol_Up(object sender, MouseButtonEventArgs e) => EndVolDrag();

    void Vol_Lost(object sender, MouseEventArgs e)
    {
        if (Vol.IsMouseCaptureWithin) return;
        EndVolDrag();
    }

    void EndVolDrag()
    {
        if (!_volDrag) return;
        _volDrag = false;
        CommitVolume();
    }

    void Vol_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (_volDrag || _session == null || !Vol.IsEnabled || e.Delta == 0) return;
        e.Handled = true;
        int step = e.Delta > 0 ? 1 : -1;
        int notches = Math.Max(1, Math.Abs(e.Delta) / 120);
        Vol.Value = Math.Clamp(Math.Round(Vol.Value) + step * notches, 0, 100);
        CommitVolume();
    }

    static bool VolKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End;

    void Vol_KeyDown(object sender, KeyEventArgs e)
    {
        if (VolKey(e.Key)) _volKey = true;
    }

    void Vol_KeyUp(object sender, KeyEventArgs e)
    {
        if (!VolKey(e.Key)) return;
        _volKey = false;
        CommitVolume();
    }

    // Called only from a pointer or key gesture. Setting Value, applying the template, or laying out does not call this.
    void CommitVolume()
    {
        if (_session == null || !_session.Connected || !Vol.IsEnabled || !IsLoaded) return;
        int level = (int)Math.Round(Math.Clamp(Vol.Value, 0, 100));
        int current = _wantVol ?? _session.PendingVolume ?? _session.Status.Volume;
        if (current >= 0 && level == current) return;
        _wantVol = level;
        _ = SendVolume(level);
    }

    async Task SendVolume(int level)
    {
        if (_session == null) return;
        await _session.SetVolumeAsync(level);
        if (_wantVol != level || _session == null) return;
        if (_session.PendingVolume == null && _session.Status.Volume != level)
            _wantVol = null;
        Render();
    }

    void Play_Click(object sender, RoutedEventArgs e) => _ = _session?.PlayPauseAsync();
    void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_session?.CanStop != true) return;
        _ = _session.StopAsync();
    }

    void Prev_Click(object sender, RoutedEventArgs e) => _ = _session?.PreviousAsync();
    void Next_Click(object sender, RoutedEventArgs e) => _ = _session?.NextAsync();
    void Shuffle_Click(object sender, RoutedEventArgs e) => _ = _session?.ToggleShuffleAsync();
    void Repeat_Click(object sender, RoutedEventArgs e) => _ = _session?.CycleRepeatAsync();
    void Mute_Click(object sender, RoutedEventArgs e) => _ = _session?.ToggleMuteAsync();
    void Sleep_Click(object sender, RoutedEventArgs e) => _ = _session?.StepSleepAsync();
}
