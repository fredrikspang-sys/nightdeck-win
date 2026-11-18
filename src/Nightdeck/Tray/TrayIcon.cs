using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nightdeck;

/// <summary>Shell_NotifyIcon tray icon. Enabled stays false until the icon is actually added.</summary>
public static class TrayIcon
{
    const int NIM_ADD = 0x00;
    const int NIM_MODIFY = 0x01;
    const int NIM_DELETE = 0x02;
    const int NIF_MESSAGE = 0x01;
    const int NIF_ICON = 0x02;
    const int NIF_TIP = 0x04;
    const int WM_TRAY = 0x8001;
    const int WM_LBUTTONUP = 0x0202;
    const int WM_LBUTTONDBLCLK = 0x0203;
    const int WM_RBUTTONUP = 0x0205;
    const int WM_CONTEXTMENU = 0x007B;

    static NOTIFYICONDATA _nid;
    static IntPtr _icon;
    static HwndSource? _source;
    static Window? _window;
    static PlayerSession? _session;
    static bool _added;
    static long _menuAt;

    public static bool Enabled { get; private set; }
    public static bool QuitRequested { get; private set; }

    public static void Install(Window window, PlayerSession session)
    {
        if (_session != null) _session.Changed -= OnSession;
        _window = window;
        _session = session;
        session.Changed += OnSession;
        if (Enabled) return;
        var helper = new WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero)
            window.SourceInitialized += (_, _) => AddIcon();
        helper.EnsureHandle();
        AddIcon();
    }

    public static void Remove()
    {
        if (_session != null) _session.Changed -= OnSession;
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
        if (_added)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            _added = false;
        }
        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
        Enabled = false;
    }

    public static void RequestQuit() => QuitRequested = true;

    static void AddIcon()
    {
        if (Enabled || _added || _window == null) return;
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var source = HwndSource.FromHwnd(hwnd);
        if (source == null) return;
        IntPtr icon = CreateAppIcon();
        if (icon == IntPtr.Zero) return;
        _icon = icon;
        _nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = icon,
            szTip = "Nightdeck",
        };
        if (!Shell_NotifyIcon(NIM_ADD, ref _nid))
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
            return;
        }
        _added = true;
        _source = source;
        source.AddHook(WndProc);
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            _window.Icon = image;
        }
        catch { /* the window icon is optional */ }
        Enabled = true;
        OnSession(null, EventArgs.Empty);
    }

    static void OnSession(object? sender, EventArgs e)
    {
        if (!_added || _session == null) return;
        var st = _session.Status;
        string tip = st.Title.Length == 0 ? "Nightdeck" : st.Artist.Length == 0 ? st.Title : st.Title + " — " + st.Artist;
        if (tip.Length > 127) tip = tip[..127];
        _nid.szTip = tip;
        _nid.uFlags = NIF_TIP;
        Shell_NotifyIcon(NIM_MODIFY, ref _nid);
    }

    static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_TRAY) return IntPtr.Zero;
        int ev = lParam.ToInt32() & 0xFFFF;
        if (ev is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
        {
            ShowWindow();
            handled = true;
        }
        else if (ev is WM_RBUTTONUP or WM_CONTEXTMENU)
        {
            ShowMenu();
            handled = true;
        }
        return IntPtr.Zero;
    }

    static void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    static void ShowMenu()
    {
        if (_window == null || _session == null) return;
        long now = Environment.TickCount64;
        if (now - _menuAt < 300) return;
        _menuAt = now;
        SetForegroundWindow(new WindowInteropHelper(_window).Handle);
        var menu = new ContextMenu();
        menu.Items.Add(Item("Show", (_, _) => ShowWindow()));
        bool playing = _session.PendingPlaying ?? _session.Status.IsPlaying;
        menu.Items.Add(Item(playing ? "Pause" : "Play", (_, _) => _ = _session.PlayPauseAsync()));
        var stop = Item("Stop", (_, _) => _ = _session.StopAsync());
        stop.IsEnabled = _session.CanStop;
        menu.Items.Add(stop);
        menu.Items.Add(Item("Previous", (_, _) => _ = _session.PreviousAsync()));
        menu.Items.Add(Item("Next", (_, _) => _ = _session.NextAsync()));
        var players = _session.KnownPlayers;
        if (players.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var player in players)
            {
                string host = player.Host;
                int port = player.Port;
                bool active = port == _session.Client.Port && string.Equals(host, _session.Client.Host, StringComparison.OrdinalIgnoreCase);
                string name = player.Name.Length > 0 ? player.Name : host + ":" + port;
                string text = name;
                if (!active && _session.InGroup(host, port)) text += "  ·  in group";
                if (active) text += "  ·  active";
                var item = Item(text, (_, _) => PlayerList.SwitchTo(_session, host, port));
                item.IsChecked = active;
                menu.Items.Add(item);
            }
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", (_, _) =>
        {
            RequestQuit();
            _window.Close();
        }));
        GetCursorPos(out POINT pt);
        var dip = new Point(pt.X, pt.Y);
        if (PresentationSource.FromVisual(_window)?.CompositionTarget is { } target)
            dip = target.TransformFromDevice.Transform(dip);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
        menu.PlacementTarget = _window;
        menu.HorizontalOffset = dip.X;
        menu.VerticalOffset = dip.Y;
        menu.IsOpen = true;
    }

    static MenuItem Item(string header, RoutedEventHandler click)
    {
        var item = new MenuItem { Header = header };
        item.Click += click;
        return item;
    }

    static IntPtr CreateAppIcon()
    {
        const int size = 32;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int dx = x - 15;
                int dy = y - 15;
                int d2 = dx * dx + dy * dy;
                bool on = d2 <= 13 * 13;
                byte a = on ? (byte)255 : (byte)0;
                byte r = 0xD7, g = 0xB0, b = 0x7A;
                int i = (y * size + x) * 4;
                pixels[i] = (byte)(b * a / 255);
                pixels[i + 1] = (byte)(g * a / 255);
                pixels[i + 2] = (byte)(r * a / 255);
                pixels[i + 3] = a;
            }
        }
        var bmi = new BITMAPINFO
        {
            biSize = 40,
            biWidth = size,
            biHeight = -size,
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr bits;
        IntPtr color = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        IntPtr mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        var info = new ICONINFO { fIcon = 1, hbmColor = color, hbmMask = mask };
        IntPtr icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        if (mask != IntPtr.Zero) DeleteObject(mask);
        return icon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr CreateIconIndirect(ref ICONINFO icon);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr handle);
}
