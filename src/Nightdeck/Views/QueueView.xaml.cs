using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nightdeck.Core;

namespace Nightdeck;

public partial class QueueView : UserControl
{
    readonly List<Row> _rows = [];
    PlayerSession? _session;
    int _ver = -1;
    string? _followFile;
    int _followIndex = -1;
    string _followPid = "";

    public QueueView() => InitializeComponent();

    public void Attach(PlayerSession session)
    {
        _session = session;
        session.Changed += (_, _) => Render();
        session.EndpointChanged += (_, _) =>
        {
            _followFile = null;
            _followIndex = -1;
            Render();
        };
        Render();
    }

    sealed class Row : INotifyPropertyChanged
    {
        public int Id { get; init; }
        public string File { get; init; } = "";
        public string Body { get; init; } = "";
        bool _current;
        public bool Current
        {
            get => _current;
            set
            {
                if (_current == value) return;
                _current = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Line)));
            }
        }
        public string Line => (_current ? "▶  " : "    ") + Body;
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    void Render()
    {
        if (_session == null) return;
        var songs = _session.Queue.Songs;
        int current = _session.Status.Song;
        if (_session.QueueVersion != _ver)
        {
            _ver = _session.QueueVersion;
            int? selected = (Songs.SelectedItem as Row)?.Id;
            string? followFile = null;
            int followIndex = -1;
            if (_followFile != null && _session.Queue.Id != _followPid)
            {
                followFile = _followFile;
                followIndex = _followIndex;
                _followFile = null;
                _followIndex = -1;
            }
            double offset = ScrollOf(Songs)?.VerticalOffset ?? 0;
            _rows.Clear();
            foreach (var song in songs)
                _rows.Add(new Row { Id = song.Id, File = song.File, Body = song.Line, Current = song.Id == current });
            Songs.ItemsSource = null;
            Songs.ItemsSource = _rows;
            if (followFile != null)
            {
                Row? match = null;
                if (!string.IsNullOrEmpty(followFile))
                {
                    foreach (var row in _rows)
                        if (row.File == followFile) { match = row; break; }
                }
                if (match == null && followIndex >= 0 && followIndex < _rows.Count)
                    match = _rows[followIndex];
                if (match != null) Songs.SelectedItem = match;
            }
            else if (selected is int id)
            {
                foreach (var row in _rows)
                    if (row.Id == id) { Songs.SelectedItem = row; break; }
            }
            Dispatcher.BeginInvoke(() => ScrollOf(Songs)?.ScrollToVerticalOffset(offset));
        }
        else
        {
            foreach (var row in _rows) row.Current = row.Id == current;
        }
        Empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static ScrollViewer? ScrollOf(DependencyObject root)
    {
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(root); i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var nested = ScrollOf(child);
            if (nested != null) return nested;
        }
        return null;
    }

    Row? Selected() => Songs.SelectedItem as Row;

    void List_Key(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Play_Click(sender, e);
        e.Handled = true;
    }

    void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        if (Selected() is not Row row) { QStatus.Text = "Select a track"; return; }
        QStatus.Text = "";
        _ = _session.PlayQueueSongAsync(row.Id);
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        if (Selected() is not Row row) { QStatus.Text = "Select a track"; return; }
        QStatus.Text = "";
        _ = _session.RemoveFromQueueAsync(row.Id);
    }

    void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    void Down_Click(object sender, RoutedEventArgs e) => Move(1);

    void Move(int delta)
    {
        if (_session == null) return;
        if (Selected() is not Row row) { QStatus.Text = "Select a track"; return; }
        int index = _rows.IndexOf(row);
        int dest = index + delta;
        if (index < 0 || dest < 0 || dest >= _rows.Count) return;
        QStatus.Text = "";
        _followFile = row.File;
        _followIndex = dest;
        _followPid = _session.Queue.Id;
        _ = _session.MoveInQueueAsync(row.Id, _rows[dest].Id);
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null || _rows.Count == 0) return;
        if (MessageBox.Show(Window.GetWindow(this), "Clear the queue?", "Nightdeck", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        QStatus.Text = "";
        _ = _session.ClearQueueAsync();
    }
}
