using System.Windows;
using System.Windows.Controls;
using Nightdeck.Core;

namespace Nightdeck;

public partial class PlaylistsView : UserControl
{
    PlayerSession? _session;

    public PlaylistsView() => InitializeComponent();

    bool _loaded;
    int _gen;

    public void Attach(PlayerSession session)
    {
        _session = session;
        session.EndpointChanged += (_, _) =>
        {
            _loaded = false;
            PresetList.ItemsSource = null;
            PlaylistList.ItemsSource = null;
            EnsureLoaded();
        };
        Loaded += (_, _) => EnsureLoaded();
        IsVisibleChanged += (_, _) => EnsureLoaded();
    }

    void EnsureLoaded()
    {
        if (_loaded || _session == null || !IsVisible) return;
        if (string.IsNullOrWhiteSpace(_session.Client.Host)) return;
        _loaded = true;
        _ = ReloadAsync();
    }

    void Reload_Click(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    async Task ReloadAsync()
    {
        if (_session == null) return;
        int gen = ++_gen;
        try
        {
            var presets = await _session.PresetsAsync();
            if (gen != _gen) return;
            var lists = await _session.PlaylistsAsync();
            if (gen != _gen) return;
            PresetList.ItemsSource = presets;
            PlaylistList.ItemsSource = lists;
            Info.Text = presets.Count + (presets.Count == 1 ? " preset, " : " presets, ")
                + lists.Count + (lists.Count == 1 ? " playlist" : " playlists");
        }
        catch (Exception ex)
        {
            if (gen == _gen) Info.Text = ex.Message;
        }
    }

    void PlayPreset_Click(object sender, RoutedEventArgs e) => _ = PlayPresetAsync();

    async Task PlayPresetAsync()
    {
        if (_session == null) return;
        if (PresetList.SelectedItem is not Preset preset || preset.Id.Length == 0)
        {
            Info.Text = "Select a preset";
            return;
        }
        try
        {
            Info.Text = "Playing " + preset.Name;
            await _session.PlayPresetAsync(preset.Id);
            Info.Text = _session.Message.Length == 0 ? "Playing " + preset.Name : _session.Message;
        }
        catch (Exception ex) { Info.Text = ex.Message; }
    }

    void Save_Click(object sender, RoutedEventArgs e) => _ = SaveAsync();

    async Task SaveAsync()
    {
        if (_session == null) return;
        string name = SaveName.Text.Trim();
        if (name.Length == 0) { Info.Text = "Enter a playlist name"; return; }
        if (_session.Queue.Songs.Count == 0) { Info.Text = "Queue is empty"; return; }
        try
        {
            await _session.SavePlaylistAsync(name);
            Info.Text = _session.Message.Length == 0 ? "Saved" : _session.Message;
            await ReloadAsync();
        }
        catch (Exception ex) { Info.Text = ex.Message; }
    }

    void Load_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    async Task LoadAsync()
    {
        if (_session == null) return;
        if (PlaylistList.SelectedItem is not BrowseItem item || item.PlayUrl.Length == 0)
        {
            Info.Text = "Select a playlist";
            return;
        }
        if (_session.Queue.Songs.Count > 0 &&
            MessageBox.Show(Window.GetWindow(this), "Replace the current queue with \"" + item.Text + "\"?", "Nightdeck",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            await _session.LoadPlaylistAsync(item);
            Info.Text = "Loaded " + item.Text;
        }
        catch (Exception ex) { Info.Text = ex.Message; }
    }

    void Delete_Click(object sender, RoutedEventArgs e) => _ = DeleteAsync();

    async Task DeleteAsync()
    {
        if (_session == null) return;
        if (PlaylistList.SelectedItem is not BrowseItem item)
        {
            Info.Text = "Select a playlist";
            return;
        }
        if (MessageBox.Show(Window.GetWindow(this), "Delete playlist \"" + item.Text + "\"?", "Nightdeck",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            await _session.DeletePlaylistAsync(item);
            Info.Text = _session.Message.Length == 0 ? "Deleted" : _session.Message;
            await ReloadAsync();
        }
        catch (Exception ex) { Info.Text = ex.Message; }
    }
}
