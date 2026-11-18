using System.Windows;

namespace Nightdeck;

public partial class App : Application
{
    PlayerSession? _session;
    SmtcController? _smtc;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            _session?.Dispose();
            MessageBox.Show(args.Exception.Message, "Nightdeck", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };

        var settings = SettingsStore.Load();
        var client = new Nightdeck.Core.BluOsClient(settings.Host, settings.Port);
        _session = new PlayerSession(client, settings);
        var window = new MainWindow(_session);
        MainWindow = window;
        _smtc = new SmtcController(_session);
        window.SourceInitialized += (_, _) => _smtc.Start(window);
        if (settings.ShowTray) TrayIcon.Install(window, _session);
        if (new System.Windows.Interop.WindowInteropHelper(window).Handle != IntPtr.Zero)
            _smtc.Start(window);
        _session.Start();
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _smtc?.Dispose();
        TrayIcon.Remove();
        _session?.Dispose();
        base.OnExit(e);
    }
}
