using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nightdeck.Core;

namespace Nightdeck;

public partial class LibraryView : UserControl
{
    readonly Stack<Level> _back = new();
    PlayerSession? _session;
    Level? _current;
    string _searchKey = "";
    int _nav;
    int _pages;
    bool _loadingMore;
    bool _scrollHooked;

    public LibraryView()
    {
        InitializeComponent();
        Items.Loaded += (_, _) => HookScroll();
    }

    public void Attach(PlayerSession session)
    {
        _session = session;
        session.EndpointChanged += (_, _) =>
        {
            _back.Clear();
            _current = null;
            _searchKey = "";
            _nav++;
            Items.ItemsSource = null;
            _ = OpenAsync(BluOs.LibraryRoot, null, "Library", push: false);
        };
        _ = OpenAsync(BluOs.LibraryRoot, null, "Library", push: false);
    }

    sealed class Level
    {
        public string Key = "";
        public string? Query;
        public string Title = "";
        public string NextKey = "";
        public ObservableCollection<BrowseItem> Items = [];
    }

    async Task OpenAsync(string key, string? query, string title, bool push)
    {
        if (_session == null || key.Length == 0) return;
        if (string.IsNullOrWhiteSpace(_session.Client.Host))
        {
            LibStatus.Text = "No player";
            return;
        }
        int gen = ++_nav;
        Items.IsEnabled = false;
        LibStatus.Text = "Loading…";
        try
        {
            var first = await _session.Client.BrowseAsync(key, query);
            if (gen != _nav) return;
            if (first.SearchKey.Length > 0) _searchKey = first.SearchKey;
            var level = new Level
            {
                Key = key,
                Query = query,
                Title = title,
                NextKey = first.NextKey,
                Items = new ObservableCollection<BrowseItem>(first.Items),
            };
            if (push && _current != null) _back.Push(_current);
            _current = level;
            _pages = 1;
            Show(level);
        }
        catch (Exception ex)
        {
            if (gen != _nav) return;
            LibStatus.Text = ex.Message;
            BackBtn.IsEnabled = _back.Count > 0;
        }
        finally
        {
            if (gen == _nav) Items.IsEnabled = true;
        }
    }

    void Show(Level level)
    {
        Heading.Text = level.Title.Length == 0 ? "Library" : level.Title;
        Items.ItemsSource = level.Items;
        BackBtn.IsEnabled = _back.Count > 0;
        LibStatus.Text = level.Items.Count == 1 ? "1 item" : level.Items.Count + " items";
        HookScroll();
    }

    void HookScroll()
    {
        if (_scrollHooked) return;
        Items.ApplyTemplate();
        if (ScrollOf(Items) is not ScrollViewer sv) return;
        sv.ScrollChanged += OnScroll;
        _scrollHooked = true;
    }

    void OnScroll(object sender, ScrollChangedEventArgs e)
    {
        if (_loadingMore || _current == null || _current.NextKey.Length == 0 || _pages >= 200) return;
        var sv = (ScrollViewer)sender;
        if (sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 2)
            _ = LoadMoreAsync();
    }

    async Task LoadMoreAsync()
    {
        if (_session == null || _current == null || _current.NextKey.Length == 0 || _loadingMore || _pages >= 200) return;
        _loadingMore = true;
        int gen = _nav;
        var level = _current;
        string next = level.NextKey;
        try
        {
            var page = await _session.Client.BrowseAsync(next);
            if (gen != _nav || level != _current) return;
            foreach (var item in page.Items) level.Items.Add(item);
            level.NextKey = page.NextKey;
            _pages++;
            LibStatus.Text = level.Items.Count == 1 ? "1 item" : level.Items.Count + " items";
        }
        catch (Exception ex)
        {
            if (gen == _nav) LibStatus.Text = ex.Message;
        }
        finally { _loadingMore = false; }
        if (gen != _nav || level != _current || level.NextKey.Length == 0 || _pages >= 200) return;
        if (ScrollOf(Items) is ScrollViewer sv && sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 2)
            _ = LoadMoreAsync();
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

    void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_back.Count == 0) return;
        _nav++;
        _current = _back.Pop();
        _pages = 1;
        Items.IsEnabled = true;
        Show(_current);
    }

    void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) _ = OpenAsync(BluOs.LibraryRoot, null, "Library", push: false);
        else _ = OpenAsync(_current.Key, _current.Query, _current.Title, push: false);
    }

    void Search_Key(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _ = SearchAsync();
        e.Handled = true;
    }

    void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

    async Task SearchAsync()
    {
        string q = SearchBox.Text.Trim();
        if (q.Length == 0) { LibStatus.Text = "Type a search"; return; }
        if (_searchKey.Length == 0) { LibStatus.Text = "Search is not available"; return; }
        await OpenAsync(_searchKey, q, "Search: " + q, push: true);
    }

    void List_Key(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _ = OpenSelectionAsync();
        e.Handled = true;
    }

    void Open_Click(object sender, MouseButtonEventArgs e) => _ = OpenSelectionAsync();

    void PlayNow_Click(object sender, RoutedEventArgs e) => _ = AddSelectedAsync(playNow: true);

    void Add_Click(object sender, RoutedEventArgs e) => _ = AddSelectedAsync(playNow: false);

    async Task OpenSelectionAsync()
    {
        if (!Items.IsEnabled) return;
        if (Items.SelectedItem is not BrowseItem item) { LibStatus.Text = "Select an item"; return; }
        if (item.IsContainer)
        {
            string title = item.Text.Length == 0 ? "Library" : item.Text;
            await OpenAsync(item.BrowseKey, null, title, push: true);
            return;
        }
        await AddSelectedAsync(item, playNow: true);
    }

    async Task AddSelectedAsync(BrowseItem? item = null, bool playNow = false)
    {
        if (_session == null) return;
        item ??= Items.SelectedItem as BrowseItem;
        if (item == null) { LibStatus.Text = "Select an item"; return; }
        try
        {
            LibStatus.Text = playNow ? "Playing…" : "Adding…";
            await _session.AddBrowseItemAsync(item, playNow);
            LibStatus.Text = _session.Message.Length == 0 ? (playNow ? "Playing" : "Added") : _session.Message;
        }
        catch (Exception ex) { LibStatus.Text = ex.Message; }
    }
}
