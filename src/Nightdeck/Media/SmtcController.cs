using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Nightdeck;

/// <summary>
/// Media overlay through ISystemMediaTransportControlsInterop. The full WinRT projection
/// is not referenced. A failure leaves <see cref="IsActive"/> false and the window keys work.
/// </summary>
public sealed class SmtcController : IDisposable
{
    readonly PlayerSession _session;
    nint _smtc;
    nint _smtc2;
    nint _updater;
    long _buttonToken;
    long _seekToken;
    SmtcHandler? _buttonHandler;
    SmtcHandler? _seekHandler;
    int _song = int.MinValue;
    string _pid = "";
    string _coverKey = "";
    long _timelineAt;
    bool _accept;
    bool _stopOn;
    bool _started;
    bool _hooked;
    bool _disposed;

    public static bool IsActive { get; private set; }

    public SmtcController(PlayerSession session) => _session = session;

    public void Start(Window window)
    {
        if (_started || _disposed) return;
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            if (hwnd == IntPtr.Zero) return;
            Bind(hwnd);
            _session.Changed += OnChanged;
            _hooked = true;
            _started = true;
            IsActive = true;
            Push(forceTimeline: true);
        }
        catch
        {
            Cleanup();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
    }

    void Cleanup()
    {
        IsActive = false;
        _started = false;
        _accept = false;
        _stopOn = false;
        if (_hooked)
        {
            _session.Changed -= OnChanged;
            _hooked = false;
        }
        if (_smtc != 0 && _buttonToken != 0)
        {
            SmtcCom.Remove(_smtc, 33, _buttonToken);
            _buttonToken = 0;
        }
        if (_smtc2 != 0 && _seekToken != 0)
        {
            SmtcCom.Remove(_smtc2, 14, _seekToken);
            _seekToken = 0;
        }
        if (_smtc != 0) SmtcCom.PutByte(_smtc, 11, 0);
        _buttonHandler?.Release();
        _seekHandler?.Release();
        _buttonHandler = null;
        _seekHandler = null;
        SmtcCom.Free(ref _updater);
        SmtcCom.Free(ref _smtc2);
        SmtcCom.Free(ref _smtc);
    }

    void Bind(nint hwnd)
    {
        nint factory = SmtcCom.Factory("Windows.Media.SystemMediaTransportControls", SmtcCom.IidInterop);
        try
        {
            _smtc = SmtcCom.GetForWindow(factory, hwnd, SmtcCom.IidSmtc);
        }
        finally { SmtcCom.Free(ref factory); }

        if (SmtcCom.Query(_smtc, SmtcCom.IidSmtc2, out nint second) >= 0) _smtc2 = second;
        SmtcCom.Ok(SmtcCom.PutByte(_smtc, 11, 1));
        SmtcCom.Ok(SmtcCom.PutByte(_smtc, 13, 1));
        SmtcCom.Ok(SmtcCom.PutByte(_smtc, 15, 0)); // stop stays off until a connected status
        SmtcCom.Ok(SmtcCom.PutByte(_smtc, 17, 1));
        SmtcCom.Ok(SmtcCom.PutByte(_smtc, 25, 1));
        SmtcCom.Ok(SmtcCom.PutByte(_smtc, 27, 1));

        _buttonHandler = SmtcHandler.Create(this, button: true, SmtcCom.IidButton);
        SmtcCom.Ok(SmtcCom.Add(_smtc, 32, _buttonHandler.Ptr, out _buttonToken));
        if (_smtc2 != 0)
        {
            _seekHandler = SmtcHandler.Create(this, button: false, SmtcCom.IidSeek);
            int hr = SmtcCom.Add(_smtc2, 13, _seekHandler.Ptr, out _seekToken);
            if (hr < 0)
            {
                _seekHandler.Release();
                _seekHandler = null;
            }
        }

        SmtcCom.Ok(SmtcCom.GetPtr(_smtc, 8, out _updater));
        SmtcCom.Ok(SmtcCom.PutInt(_updater, 7, 1));
    }

    void OnChanged(object? sender, EventArgs e) => Push(forceTimeline: false);

    internal void OnNativeEvent(bool button, nint args)
    {
        if (_disposed || !_accept || args == 0) return;
        try
        {
            if (button)
            {
                if (SmtcCom.GetInt(args, 6, out int which) < 0) return;
                switch (which)
                {
                    case 0:
                    case 1:
                        _ = _session.PlayPauseAsync();
                        break;
                    case 2:
                        _ = _session.StopAsync();
                        break;
                    case 6:
                        _ = _session.NextAsync();
                        break;
                    case 7:
                        _ = _session.PreviousAsync();
                        break;
                }
                return;
            }
            if (Environment.TickCount64 - _timelineAt < 300) return;
            if (!_session.Status.CanSeek) return;
            if (SmtcCom.GetI64(args, 6, out long ticks) < 0) return;
            if (ticks < 0) ticks = 0;
            _ = _session.SeekToAsync((int)(ticks / 10_000_000));
        }
        catch { /* the key is optional */ }
    }

    void Push(bool forceTimeline)
    {
        if (_smtc == 0 || _updater == 0) return;
        try
        {
            var st = _session.Status;
            bool stopOn = _session.Connected && st.Ok;
            if (stopOn != _stopOn)
            {
                _stopOn = stopOn;
                SmtcCom.PutByte(_smtc, 15, (byte)(stopOn ? 1 : 0));
            }
            int status = !st.Ok ? 0 : st.IsPlaying ? 3 : st.State == "stop" ? 2 : 4;
            SmtcCom.PutInt(_smtc, 7, status);

            if (SmtcCom.GetPtr(_updater, 12, out nint music) >= 0 && music != 0)
            {
                try
                {
                    SmtcCom.PutString(music, 7, st.Title.Length == 0 ? "Nightdeck" : st.Title);
                    SmtcCom.PutString(music, 11, st.Artist);
                    if (SmtcCom.Query(music, SmtcCom.IidMusic2, out nint music2) >= 0 && music2 != 0)
                    {
                        try { SmtcCom.PutString(music2, 7, st.Album); }
                        finally { SmtcCom.Free(ref music2); }
                    }
                }
                finally { SmtcCom.Free(ref music); }
            }

            PublishCover();

            bool songChanged = st.Song != _song || st.PlaylistId != _pid;
            _song = st.Song;
            _pid = st.PlaylistId;
            bool due = forceTimeline || songChanged || !st.IsPlaying || Environment.TickCount64 - _timelineAt >= 5000;
            if (due && st.TotalSeconds > 0 && _smtc2 != 0)
            {
                try { PublishTimeline(st.TotalSeconds); }
                catch { /* timeline is optional */ }
            }
            SmtcCom.Call(_updater, 17);
            if (st.Ok) _accept = true;
        }
        catch { /* the overlay is optional */ }
    }

    void PublishCover()
    {
        string key = _session.CoverKey;
        var bytes = _session.CoverBytes;
        if (key.Length == 0)
        {
            if (_coverKey.Length != 0)
            {
                SmtcCom.PutPtr(_updater, 11, 0);
                _coverKey = "";
            }
            return;
        }
        if (bytes == null || bytes.Length == 0 || key == _coverKey) return;
        if (TryThumbnail(bytes)) _coverKey = key;
        _session.ReleaseCover(bytes);
    }

    bool TryThumbnail(byte[] bytes)
    {
        nint mem = SmtcCom.MemStream(bytes);
        if (mem == 0) return false;
        nint ras = 0;
        nint factory = 0;
        nint reference = 0;
        try
        {
            if (SmtcCom.StreamOver(mem, SmtcCom.IidRandomAccess, out ras) < 0 || ras == 0) return false;
            factory = SmtcCom.Factory("Windows.Storage.Streams.RandomAccessStreamReference", SmtcCom.IidStreamStatics);
            if (SmtcCom.CreateFromStream(factory, ras, out reference) < 0 || reference == 0) return false;
            return SmtcCom.PutPtr(_updater, 11, reference) >= 0;
        }
        catch { return false; }
        finally
        {
            SmtcCom.Free(ref reference);
            SmtcCom.Free(ref factory);
            SmtcCom.Free(ref ras);
            SmtcCom.Free(ref mem);
        }
    }

    void PublishTimeline(int totalSeconds)
    {
        nint props = SmtcCom.Activate("Windows.Media.SystemMediaTransportControlsTimelineProperties");
        try
        {
            long end = TimeSpan.FromSeconds(totalSeconds).Ticks;
            double pos = Math.Clamp(_session.DisplaySeconds, 0, totalSeconds);
            long at = TimeSpan.FromSeconds(pos).Ticks;
            SmtcCom.PutI64(props, 7, 0);
            SmtcCom.PutI64(props, 9, end);
            SmtcCom.PutI64(props, 11, 0);
            SmtcCom.PutI64(props, 13, end);
            SmtcCom.PutI64(props, 15, at);
            _timelineAt = Environment.TickCount64;
            SmtcCom.PutPtr(_smtc2, 12, props);
        }
        finally { SmtcCom.Free(ref props); }
    }
}

static unsafe class SmtcCom
{
    public static readonly Guid IidInterop = new("DDB0472D-C911-4A1F-86D9-DC3D71A95F5A");
    public static readonly Guid IidSmtc = new("99FA3FF4-1742-42A6-902E-087D41F965EC");
    public static readonly Guid IidSmtc2 = new("EA98D2F6-7F3C-4AF2-A586-72889808EFB1");
    public static readonly Guid IidMusic2 = new("00368462-97D3-44B9-B00F-008AFCEFAF18");
    public static readonly Guid IidButton = new("0557E996-7B23-5BAE-AA81-EA0D671143A4");
    public static readonly Guid IidSeek = new("44E34F15-BDC0-50A7-ACE4-39E91FB753F1");
    public static readonly Guid IidRandomAccess = new("905A0FE1-BC53-11DF-8C49-001E4FC686DA");
    public static readonly Guid IidStreamStatics = new("857309DC-3FBF-4E7D-986F-EF3B1A07A964");

    public static void Ok(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    public static void Free(ref nint p)
    {
        nint q = p;
        p = 0;
        if (q != 0) Release(q);
    }

    public static nint Factory(string className, Guid iid)
    {
        Ok(WindowsCreateString(className, className.Length, out nint name));
        try
        {
            Ok(RoGetActivationFactory(name, in iid, out nint factory));
            return factory;
        }
        finally { WindowsDeleteString(name); }
    }

    public static nint Activate(string className)
    {
        Ok(WindowsCreateString(className, className.Length, out nint name));
        try
        {
            Ok(RoActivateInstance(name, out nint instance));
            return instance;
        }
        finally { WindowsDeleteString(name); }
    }

    public static nint GetForWindow(nint factory, nint hwnd, Guid iid)
    {
        nint result;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Vtbl(factory, 6))(factory, hwnd, &iid, &result);
        Ok(hr);
        return result;
    }

    public static int Query(nint p, Guid iid, out nint result)
    {
        nint found = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtbl(p, 0))(p, &iid, &found);
        result = hr >= 0 ? found : 0;
        return hr;
    }

    public static uint Release(nint p) => ((delegate* unmanaged[Stdcall]<nint, uint>)Vtbl(p, 2))(p);

    public static int Call(nint p, int slot) => ((delegate* unmanaged[Stdcall]<nint, int>)Vtbl(p, slot))(p);

    public static int PutInt(nint p, int slot, int value) => ((delegate* unmanaged[Stdcall]<nint, int, int>)Vtbl(p, slot))(p, value);

    public static int PutByte(nint p, int slot, byte value) => ((delegate* unmanaged[Stdcall]<nint, byte, int>)Vtbl(p, slot))(p, value);

    public static int PutI64(nint p, int slot, long value) => ((delegate* unmanaged[Stdcall]<nint, long, int>)Vtbl(p, slot))(p, value);

    public static int PutPtr(nint p, int slot, nint value) => ((delegate* unmanaged[Stdcall]<nint, nint, int>)Vtbl(p, slot))(p, value);

    public static int GetInt(nint p, int slot, out int value)
    {
        int v = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, int*, int>)Vtbl(p, slot))(p, &v);
        value = v;
        return hr;
    }

    public static int GetI64(nint p, int slot, out long value)
    {
        long v = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, long*, int>)Vtbl(p, slot))(p, &v);
        value = v;
        return hr;
    }

    public static int GetPtr(nint p, int slot, out nint value)
    {
        nint v = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtbl(p, slot))(p, &v);
        value = hr >= 0 ? v : 0;
        return hr;
    }

    public static int PutString(nint p, int slot, string? text)
    {
        nint hs = 0;
        if (!string.IsNullOrEmpty(text))
            Ok(WindowsCreateString(text, text.Length, out hs));
        try { return PutPtr(p, slot, hs); }
        finally { if (hs != 0) WindowsDeleteString(hs); }
    }

    public static int Add(nint p, int slot, nint handler, out long token)
    {
        long tok = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, long*, int>)Vtbl(p, slot))(p, handler, &tok);
        token = tok;
        return hr;
    }

    public static int Remove(nint p, int slot, long token) => ((delegate* unmanaged[Stdcall]<nint, long, int>)Vtbl(p, slot))(p, token);

    public static int CreateFromStream(nint statics, nint stream, out nint reference)
    {
        nint result = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Vtbl(statics, 8))(statics, stream, &result);
        reference = hr >= 0 ? result : 0;
        return hr;
    }

    public static int StreamOver(nint stream, Guid iid, out nint result)
    {
        nint found = 0;
        int hr = CreateRandomAccessStreamOverStream(stream, 0, in iid, out found);
        result = hr >= 0 ? found : 0;
        return hr;
    }

    public static nint MemStream(byte[] bytes) => SHCreateMemStream(bytes, (uint)bytes.Length);

    static nint Vtbl(nint p, int slot) => (*((nint**)p))[slot];

    [DllImport("combase.dll")] static extern int RoGetActivationFactory(nint classId, in Guid iid, out nint factory);
    [DllImport("combase.dll")] static extern int RoActivateInstance(nint classId, out nint instance);
    [DllImport("combase.dll")] static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out nint hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(nint hstring);
    [DllImport("shlwapi.dll")] static extern nint SHCreateMemStream(byte[] init, uint size);
    [DllImport("shcore.dll")] static extern int CreateRandomAccessStreamOverStream(nint stream, uint options, in Guid iid, out nint result);
}

sealed unsafe class SmtcHandler
{
    static readonly nint Vtbl = CreateVtbl();
    int _refs = 1;
    readonly SmtcController _owner;
    readonly bool _button;
    readonly Guid _iid;
    GCHandle _handle;

    SmtcHandler(SmtcController owner, bool button, Guid iid)
    {
        _owner = owner;
        _button = button;
        _iid = iid;
    }

    public nint Ptr { get; private set; }

    public static SmtcHandler Create(SmtcController owner, bool button, Guid iid)
    {
        var handler = new SmtcHandler(owner, button, iid);
        nint* mem = (nint*)NativeMemory.Alloc(2, (nuint)sizeof(nint));
        handler._handle = GCHandle.Alloc(handler);
        mem[0] = Vtbl;
        mem[1] = GCHandle.ToIntPtr(handler._handle);
        handler.Ptr = (nint)mem;
        return handler;
    }

    public void Release()
    {
        nint p = Ptr;
        if (p == 0) return;
        var fn = (delegate* unmanaged[Stdcall]<nint, uint>)(*((nint**)p))[2];
        fn(p);
    }

    static nint CreateVtbl()
    {
        nint* v = (nint*)NativeMemory.Alloc(7, (nuint)sizeof(nint));
        v[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
        v[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        v[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        v[3] = (nint)(delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)&GetIids;
        v[4] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetClass;
        v[5] = (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&GetTrust;
        v[6] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&Invoke;
        return (nint)v;
    }

    static SmtcHandler Self(nint self) => (SmtcHandler)GCHandle.FromIntPtr(((nint*)self)[1]).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int QueryInterface(nint self, Guid* iid, nint* ppv)
    {
        try
        {
            if (ppv == null) return unchecked((int)0x80004003);
            *ppv = 0;
            if (iid == null) return unchecked((int)0x80004003);
            Guid want = *iid;
            var handler = Self(self);
            if (want == IidUnknown || want == IidInspectable || want == IidAgile || want == handler._iid)
            {
                *ppv = self;
                Interlocked.Increment(ref handler._refs);
                return 0;
            }
            return unchecked((int)0x80004002);
        }
        catch { return unchecked((int)0x80004005); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static uint AddRef(nint self)
    {
        try { return (uint)Interlocked.Increment(ref Self(self)._refs); }
        catch { return 1; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static uint Release(nint self)
    {
        try
        {
            var handler = Self(self);
            uint refs = (uint)Interlocked.Decrement(ref handler._refs);
            if (refs == 0)
            {
                handler.Ptr = 0;
                if (handler._handle.IsAllocated) handler._handle.Free();
                NativeMemory.Free((void*)self);
            }
            return refs;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int GetIids(nint self, uint* count, nint* iids)
    {
        if (count != null) *count = 0;
        if (iids != null) *iids = 0;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int GetClass(nint self, nint* name)
    {
        if (name != null) *name = 0;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int GetTrust(nint self, int* level)
    {
        if (level != null) *level = 0;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int Invoke(nint self, nint sender, nint args)
    {
        try
        {
            var handler = Self(self);
            Interlocked.Increment(ref handler._refs);
            try
            {
                handler._owner.OnNativeEvent(handler._button, args);
                return 0;
            }
            finally
            {
                var release = (delegate* unmanaged[Stdcall]<nint, uint>)(*((nint**)self))[2];
                release(self);
            }
        }
        catch { return unchecked((int)0x80004005); }
    }

    static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    static readonly Guid IidInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
    static readonly Guid IidAgile = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
}
