using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Nightdeck.Core;

namespace Nightdeck;

public partial class SettingsView : UserControl
{
    PlayerSession? _session;

    public SettingsView() => InitializeComponent();

    public void Attach(PlayerSession session)
    {
        _session = session;
        var s = session.Settings;
        HostBox.Text = s.Host;
        PortBox.Text = s.Port.ToString(CultureInfo.InvariantCulture);
        FadeBox.IsChecked = s.SleepFade;
        FadeSeconds.Text = s.SleepFadeSeconds.ToString(CultureInfo.InvariantCulture);
        EndStopBox.IsChecked = s.SleepEndStop;
        TrayBox.IsChecked = s.ShowTray;
        DbBox.Text = s.DbOffset.ToString("0.##", CultureInfo.InvariantCulture);
    }

    void Found_Click(object sender, MouseButtonEventArgs e)
    {
        if (Found.SelectedItem is not DiscoveredPlayer player) return;
        HostBox.Text = player.Host;
        PortBox.Text = player.Port.ToString(CultureInfo.InvariantCulture);
        Info.Text = player.Name + "  " + player.EndPoint;
    }

    void Discover_Click(object sender, RoutedEventArgs e) => _ = DiscoverAsync();

    async Task DiscoverAsync()
    {
        if (_session == null) return;
        DiscoverBtn.IsEnabled = false;
        Info.Text = "Searching…";
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(_session.Settings.DiscoveryTimeoutSeconds, 0.2, 30));
            var found = await PlayerDiscovery.DiscoverAsync(timeout);
            Found.ItemsSource = found;
            Info.Text = found.Count == 0 ? "No players found" : found.Count + " found. Double-click one to fill the host.";
        }
        catch (Exception ex) { Info.Text = ex.Message; }
        finally { DiscoverBtn.IsEnabled = true; }
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        string host = HostBox.Text.Trim();
        if (host.Length == 0) { Info.Text = "Host is required"; return; }
        if (!int.TryParse(PortBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
        {
            Info.Text = "Port must be a whole number from 1 to 65535";
            return;
        }
        if (!int.TryParse(FadeSeconds.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fade))
        {
            Info.Text = "Fade seconds must be a whole number";
            return;
        }
        fade = Math.Clamp(fade, 2, 300);
        if (!double.TryParse(DbBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double db))
        {
            Info.Text = "dB offset must be a number, with a dot as the decimal mark";
            return;
        }

        var s = _session.Settings;
        s.Host = host;
        s.Port = port;
        s.SleepFade = FadeBox.IsChecked == true;
        s.SleepFadeSeconds = fade;
        s.SleepEndStop = EndStopBox.IsChecked == true;
        s.ShowTray = TrayBox.IsChecked == true;
        s.DbOffset = db;
        PortBox.Text = port.ToString(CultureInfo.InvariantCulture);
        FadeSeconds.Text = fade.ToString(CultureInfo.InvariantCulture);
        DbBox.Text = db.ToString("0.##", CultureInfo.InvariantCulture);
        try
        {
            SettingsStore.Save(s);
            _session.ApplySettings();
            var window = Window.GetWindow(this);
            if (s.ShowTray && window != null)
            {
                if (!TrayIcon.Enabled) TrayIcon.Install(window, _session);
                Info.Text = TrayIcon.Enabled ? "Saved" : "Saved. The tray icon could not be created.";
            }
            else
            {
                TrayIcon.Remove();
                Info.Text = "Saved";
            }
        }
        catch (Exception ex) { Info.Text = ex.Message; }
    }
}
