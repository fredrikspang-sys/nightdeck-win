using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Nightdeck.Core;

namespace Nightdeck;

public partial class SettingsView : UserControl
{
    PlayerSession? _session;
    SharesPage? _shares;
    bool _sharesLoaded, _sharesBusy;
    int _sharesGen;

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
        session.Changed += (_, _) => UpdateShareControls();
        session.EndpointChanged += (_, _) =>
        {
            _sharesLoaded = false;
            _shares = null;
            SharesList.ItemsSource = null;
            HideAddPanel();
            EnsureSharesLoaded();
        };
        IsVisibleChanged += (_, _) => EnsureSharesLoaded();
        UpdateShareControls();
    }

    // ---- music library: network shares (Settings?id=networkShares on the player) and reindex

    sealed class ShareRow(NetworkShare share) : INotifyPropertyChanged
    {
        bool _selected;
        public string Path => share.Path;
        public string Label => share.Label;
        public bool Selected
        {
            get => _selected;
            set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    bool HasPlayer => _session != null && !string.IsNullOrWhiteSpace(_session.Client.Host);

    void EnsureSharesLoaded()
    {
        if (_sharesLoaded || !IsVisible || !HasPlayer) return;
        _sharesLoaded = true;
        _ = ReloadSharesAsync();
    }

    void UpdateShareControls()
    {
        bool ok = HasPlayer && _session!.Connected && !_sharesBusy;
        RefreshSharesBtn.IsEnabled = HasPlayer && !_sharesBusy;
        RemoveSharesBtn.IsEnabled = ok && _shares is { Shares.Count: > 0 };
        AddShareBtn.IsEnabled = ok;
        AddConfirmBtn.IsEnabled = ok;
        bool indexing = _session is { Connected: true, Status.Indexing: > 0 };
        ReindexBtn.IsEnabled = ok && !indexing;
        IndexingPanel.Visibility = indexing ? Visibility.Visible : Visibility.Collapsed;
        SharesEmpty.Visibility = _shares is { Shares.Count: 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task ReloadSharesAsync()
    {
        if (_session == null || !HasPlayer) return;
        int gen = ++_sharesGen;
        _sharesBusy = true;
        UpdateShareControls();
        SharesInfo.Text = "Loading shares…";
        try
        {
            var page = await _session.Client.GetNetworkSharesAsync();
            if (gen != _sharesGen) return;
            _shares = page;
            SharesList.ItemsSource = page.Shares.Select(sh => new ShareRow(sh)).ToList();
            SharesInfo.Text = "";
        }
        catch (Exception ex)
        {
            if (gen != _sharesGen) return;
            SharesInfo.Text = "Could not read the shares: " + ex.Message;
            _sharesLoaded = false;
        }
        finally
        {
            if (gen == _sharesGen) { _sharesBusy = false; UpdateShareControls(); }
        }
    }

    void RefreshShares_Click(object sender, RoutedEventArgs e) { _sharesLoaded = true; _ = ReloadSharesAsync(); }

    async void RemoveShares_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null || _shares == null) return;
        var rows = (SharesList.ItemsSource as IEnumerable<ShareRow>)?.Where(r => r.Selected).ToList() ?? [];
        if (rows.Count == 0 && SharesList.SelectedItem is ShareRow current) rows.Add(current);
        if (rows.Count == 0) { SharesInfo.Text = "Tick the shares to remove first."; return; }
        string list = string.Join("\n", rows.Select(r => r.Path));
        string q = rows.Count == 1
            ? "Remove this share from the player?\n\n" + list + "\n\nThe player stops indexing music from it."
            : "Remove these " + rows.Count + " shares from the player?\n\n" + list + "\n\nThe player stops indexing music from them.";
        if (MessageBox.Show(Window.GetWindow(this), q, "Nightdeck", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _sharesBusy = true;
        UpdateShareControls();
        try
        {
            await _session.Client.RemoveNetworkSharesAsync(_shares, rows.Select(r => r.Path));
            _sharesBusy = false;
            await ReloadSharesAsync();
            SharesInfo.Text = rows.Count == 1 ? "Share removed. Reindex to update the library." : rows.Count + " shares removed. Reindex to update the library.";
        }
        catch (Exception ex) { SharesInfo.Text = "Could not remove: " + ex.Message; }
        finally { _sharesBusy = false; UpdateShareControls(); }
    }

    void AddShare_Click(object sender, RoutedEventArgs e)
    {
        AddPanel.Visibility = Visibility.Visible;
        AddError.Visibility = Visibility.Collapsed;
        SharePathBox.Focus();
    }

    void AddCancel_Click(object sender, RoutedEventArgs e) => HideAddPanel();

    void HideAddPanel()
    {
        AddPanel.Visibility = Visibility.Collapsed;
        SharePathBox.Text = "";
        ShareUserBox.Text = "";
        SharePassBox.Clear();
        AddError.Visibility = Visibility.Collapsed;
    }

    async void AddConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        _sharesBusy = true;
        UpdateShareControls();
        AddError.Visibility = Visibility.Collapsed;
        try
        {
            var form = await _session.Client.GetAddShareFormAsync();
            string? path = form.Normalize(SharePathBox.Text);
            if (path == null) { ShowAddError(form.PatternError); return; }
            await _session.Client.AddNetworkShareAsync(form, path, ShareUserBox.Text.Trim(), SharePassBox.Password);
            HideAddPanel();
            _sharesBusy = false;
            await ReloadSharesAsync();
            SharesInfo.Text = "Share added: " + path + ". Reindex to scan it.";
        }
        catch (Exception ex) { ShowAddError("Could not add the share: " + ex.Message); }
        finally { _sharesBusy = false; UpdateShareControls(); }
    }

    void ShowAddError(string text)
    {
        AddError.Text = text;
        AddError.Visibility = Visibility.Visible;
    }

    async void Reindex_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        if (MessageBox.Show(Window.GetWindow(this), "Reindex the player's music library?\n\nThe player rescans all shares and attached storage. This can take a while on a large library.",
                "Nightdeck", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await _session.Client.ReindexAsync();
            SharesInfo.Text = "Reindex started.";
            IndexingPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { SharesInfo.Text = "Could not start the reindex: " + ex.Message; }
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
