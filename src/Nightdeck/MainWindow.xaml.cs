using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Nightdeck.Core;

namespace Nightdeck;

public partial class MainWindow : Window
{
    readonly PlayerSession _session;
    readonly List<DiscoveredPlayer> _found = [];
    int _pickerGen;
    long _popupClosed;
    string _rowSig = "";

    public MainWindow(PlayerSession session)
    {
        _session = session;
        InitializeComponent();
        FitToWorkArea();
        PlayerPopup.Closed += (_, _) => _popupClosed = Environment.TickCount64;
        Now.Attach(session);
        Library.Attach(session);
        Queue.Attach(session);
        Playlists.Attach(session);
        Settings.Attach(session);
        session.Changed += (_, _) => OnSession();
        session.EndpointChanged += (_, _) => SeedPlayers();
        SeedPlayers();
        RefreshChrome();
    }

    void OnSession()
    {
        RefreshChrome();
        if (!PlayerPopup.IsOpen) return;
        FillRows(_session.KnownPlayers);
        if (_session.Message.StartsWith("Group failed:", StringComparison.Ordinal))
            PickerInfo.Text = _session.Message;
    }

    void SeedPlayers()
    {
        var list = PlayerList.Merge(_session.Settings, _found);
        _session.SetKnownPlayers(list);
        if (PlayerPopup.IsOpen) FillRows(list);
    }

    void RefreshChrome()
    {
        var st = _session.Status;
        Title = st.Title.Length == 0 ? "Nightdeck" : "Nightdeck — " + st.Title;
        string endpoint = _session.Client.Host + ":" + _session.Client.Port;
        if (!_session.Connected) Conn.Text = "Not connected";
        else
        {
            string name = "";
            foreach (var player in _session.KnownPlayers)
                if (player.Port == _session.Client.Port && string.Equals(player.Host, _session.Client.Host, StringComparison.OrdinalIgnoreCase) && player.Name.Length > 0)
                    name = player.Name;
            Conn.Text = name.Length > 0 && !string.Equals(name, _session.Client.Host, StringComparison.OrdinalIgnoreCase)
                ? name + "   " + endpoint
                : endpoint;
        }
        StatusLine.Text = _session.Message;
        string playerName = _session.Connected ? "Players, " + Conn.Text : "Players, not connected";
        if (AutomationProperties.GetName(PlayerButton) != playerName)
            AutomationProperties.SetName(PlayerButton, playerName);
    }

    /// <summary>Keep the default 1120×740 inside the work area, including at 150% on a 1080p screen.</summary>
    void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        const double margin = 16;
        double maxW = area.Width - margin;
        double maxH = area.Height - margin;
        if (maxW < 320) maxW = area.Width;
        if (maxH < 240) maxH = area.Height;
        if (maxW > 0 && MinWidth > maxW) MinWidth = maxW;
        if (maxH > 0 && MinHeight > maxH) MinHeight = maxH;
        if (maxW > 0 && Width > maxW) Width = maxW;
        if (maxH > 0 && Height > maxH) Height = maxH;
    }

    void PlayerButton_Click(object sender, RoutedEventArgs e)
    {
        if (PlayerPopup.IsOpen) return;
        if (Environment.TickCount64 - _popupClosed < 250) return;
        FillRows(PlayerList.Merge(_session.Settings, _found));
        PlayerPopup.HorizontalOffset = PlayerButton.ActualWidth - PickerCard.Width;
        PlayerPopup.IsOpen = true;
        _ = LoadPickerAsync(++_pickerGen);
    }

    async Task LoadPickerAsync(int gen)
    {
        PickerInfo.Text = _session.Settings.Discovery ? "Searching…" : "";
        try
        {
            if (_session.Settings.Discovery)
            {
                var timeout = TimeSpan.FromSeconds(Math.Clamp(_session.Settings.DiscoveryTimeoutSeconds, 0.2, 30));
                var found = await PlayerDiscovery.DiscoverAsync(timeout);
                if (gen != _pickerGen) return;
                _found.Clear();
                _found.AddRange(found);
            }
            var list = PlayerList.Merge(_session.Settings, _found);
            _session.SetKnownPlayers(list);
            await _session.RefreshGroupAsync();
            if (gen != _pickerGen) return;
            FillRows(_session.KnownPlayers);
            if (_session.Message.StartsWith("Group failed:", StringComparison.Ordinal))
                PickerInfo.Text = _session.Message;
            else if (_session.Settings.Discovery)
                PickerInfo.Text = _found.Count == 0 ? "No players found on the network" : _found.Count + " found";
        }
        catch (Exception ex)
        {
            if (gen != _pickerGen) return;
            PickerInfo.Text = ex.Message;
        }
    }

    void FillRows(IReadOnlyList<KnownPlayer> players)
    {
        var sig = new StringBuilder();
        foreach (var player in players)
            sig.Append(player.Host).Append(':').Append(player.Port).Append('|').Append(player.Name).Append('|')
               .Append(player.Manual ? '1' : '0').Append('|')
               .Append(_session.InGroup(player.Host, player.Port) ? '1' : '0').Append(';');
        sig.Append(_session.Client.Host).Append(':').Append(_session.Client.Port);
        string text = sig.ToString();
        if (text == _rowSig && PlayerRows.Children.Count > 0) return;
        _rowSig = text;
        PlayerRows.Children.Clear();
        var style = (Style)PickerCard.FindResource("RowButton");
        foreach (var player in players)
        {
            string host = player.Host;
            int port = player.Port;
            bool active = port == _session.Client.Port && string.Equals(host, _session.Client.Host, StringComparison.OrdinalIgnoreCase);
            bool inGroup = !active && _session.InGroup(host, port);
            string endpoint = host + ":" + port;
            string label = player.Name.Length > 0 ? player.Name : endpoint;
            string caption = player.Name.Length > 0 && !string.Equals(player.Name, host, StringComparison.OrdinalIgnoreCase)
                ? player.Name + "   " + endpoint
                : endpoint;
            if (active) caption += "   master";
            else if (inGroup) caption += "   in group";

            var row = new DockPanel { LastChildFill = true };
            if (!active && player.Manual)
            {
                var remove = RowButton("Remove", "Remove " + label, style);
                DockPanel.SetDock(remove, Dock.Right);
                remove.Click += (_, _) => RemoveManual(host, port);
                row.Children.Add(remove);
            }
            if (!active)
            {
                string verb = inGroup ? "Ungroup" : "Group";
                var group = RowButton(verb, verb + " " + label, style);
                DockPanel.SetDock(group, Dock.Right);
                group.Click += (_, _) => _ = _session.ToggleGroupAsync(host, port);
                row.Children.Add(group);
            }
            var select = RowButton(caption, "Select " + label, style);
            select.Content = new TextBlock { Text = caption, TextTrimming = TextTrimming.CharacterEllipsis };
            select.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            select.IsEnabled = !active;
            select.HorizontalAlignment = HorizontalAlignment.Stretch;
            select.Click += (_, _) =>
            {
                PlayerList.SwitchTo(_session, host, port);
                PlayerPopup.IsOpen = false;
            };
            row.Children.Add(select);
            PlayerRows.Children.Add(row);
        }
    }

    static Button RowButton(string caption, string name, Style style)
    {
        var button = new Button { Content = caption, Style = style };
        AutomationProperties.SetName(button, name);
        return button;
    }

    void AddManual_Click(object sender, RoutedEventArgs e)
    {
        if (!PlayerList.TryParse(ManualBox.Text, out string host, out int port))
        {
            PickerInfo.Text = "Use host or host:port";
            return;
        }
        PlayerList.Remember(_session.Settings, host, port);
        SettingsStore.Save(_session.Settings);
        ManualBox.Text = "";
        PickerInfo.Text = "Saved " + PlayerList.Format(host, port);
        var list = PlayerList.Merge(_session.Settings, _found);
        _session.SetKnownPlayers(list);
        _rowSig = "";
        FillRows(list);
    }

    void RemoveManual(string host, int port)
    {
        _session.Settings.ManualPlayers.RemoveAll(raw =>
            PlayerList.TryParse(raw, out string known, out int knownPort)
            && knownPort == port
            && string.Equals(known, host, StringComparison.OrdinalIgnoreCase));
        SettingsStore.Save(_session.Settings);
        var list = PlayerList.Merge(_session.Settings, _found);
        _session.SetKnownPlayers(list);
        _rowSig = "";
        FillRows(list);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || e.IsRepeat || e.OriginalSource is TextBox or ButtonBase) return;
        switch (e.Key)
        {
            case Key.Space:
                _ = _session.PlayPauseAsync();
                e.Handled = true;
                break;
            case Key.MediaPlayPause:
            case Key.MediaNextTrack:
            case Key.MediaPreviousTrack:
            case Key.MediaStop:
                if (SmtcController.IsActive)
                {
                    e.Handled = true;
                    break;
                }
                if (e.Key == Key.MediaPlayPause) _ = _session.PlayPauseAsync();
                else if (e.Key == Key.MediaNextTrack) _ = _session.NextAsync();
                else if (e.Key == Key.MediaPreviousTrack) _ = _session.PreviousAsync();
                else _ = _session.StopAsync();
                e.Handled = true;
                break;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (TrayIcon.Enabled && !TrayIcon.QuitRequested)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}
