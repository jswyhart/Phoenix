using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Halcyon.Core;

namespace Halcyon.UI;

public partial class MusicView : UserControl
{
    const string All = "all";
    string _mode = "artists";
    string? _left;        // selected artist or genre; null = all
    Album? _album;
    List<Track> _songs = new();
    bool _loading;
    List<Album>? _gridAlbums;
    bool _gridStale = true;

    /// <summary>Sorting and filtering for the albums and songs on show.</summary>
    public ViewOptions Options { get; } = new("music",
        new[] { ("", "artist"), ("title", "album title"), ("year", "year"), ("added", "date added"), ("plays", "most played"), ("recent", "last played") },
        new[] { ("", "everything"), ("liked", "liked"), ("unplayed", "never played"), ("local", "on this PC"), ("server", "on servers") });

    public List<Album> ShownAlbums => _gridAlbums ?? new();
    public List<Track> ShownSongs => _songs;

    bool Keep(Track t) => (Options.Show switch { "liked" => t.Rating > 0, "unplayed" => t.PlayCount == 0, "local" => t.IsLocal, "server" => !t.IsLocal, _ => true })
                          && Options.Matches(t.Title, t.Artist, t.Album);

    public MusicView()
    {
        InitializeComponent();
        TopBar.Children.Add(Options);
        Options.Changed += () => { _gridStale = true; LoadAlbums(); };
        AlbumGrid.ItemClicked += o => { _album = ReferenceEquals(_album, o) ? null : (Album)o; AlbumGrid.SelectedItem = _album; LoadSongs(); };
        AlbumGrid.ItemActivated += o => Menus.Play(((Album)o).Tracks);
        AlbumGrid.ItemContext += (o, el) => Menus.Open(Menus.ForAlbum((Album)o), el);
        AlbumGrid.DrawWith(TileStyle.Album, Menus.AlbumLook);
        App.Lib.Changed += Reload;
        Header.Changed += LoadSongs;
        // drag songs, albums, artists or genres to the phone, disc and playlist icons in the lower left
        TrackDrag.Attach(SongList, o => o is Track t ? new[] { t } : null, SongList);
        TrackDrag.Attach(AlbumGrid, o => o is TileCell { Item: Album a } ? a.Tracks : null);
        TrackDrag.Attach(LeftList, o => o is string name && LeftList.Items.IndexOf(name) > 0
            ? _mode == "artists"
                ? App.Lib.Albums.Where(a => string.Equals(a.Artist, name, StringComparison.OrdinalIgnoreCase)).SelectMany(a => a.Tracks)
                : App.Lib.Tracks.Where(t => string.Equals(t.Genre, name, StringComparison.OrdinalIgnoreCase))
            : null);
        Reload();
    }

    void Tab_Click(object sender, RoutedEventArgs e) => SetMode((string)((FrameworkElement)sender).Tag);

    /// <summary>Shows an album in the collection: its artist's albums with this one chosen and its songs listed.</summary>
    public void OpenAlbum(Album album)
    {
        _mode = "artists";
        _left = album.Artist;
        _album = album;
        foreach (var tab in FindTabs(this)) tab.IsChecked = (string)tab.Tag == "artists";
        Reload();
    }

    public Album? ShownAlbum => _album;

    static IEnumerable<RadioButton> FindTabs(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is RadioButton { GroupName: "musicTabs" } rb) yield return rb;
            foreach (var deeper in FindTabs(child)) yield return deeper;
        }
    }

    public void SetMode(string mode)
    {
        _mode = mode;
        _left = null;
        _album = null;
        Reload();
    }

    public static System.Text.StringBuilder Trace = new();
    public static bool Tracing;
    void Lay(string what)
    {
        if (!Tracing) return;
        var w = System.Diagnostics.Stopwatch.StartNew();
        App.Win.UpdateLayout();
        Trace.AppendLine($"    layout after {what}: {w.ElapsedMilliseconds} ms");
    }
    void Reload()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lib = App.Lib;
        EmptyPanel.Visibility = lib.Tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var showLeft = _mode is "artists" or "genres";
        var showMid = _mode != "songs";
        ColLeft.Width = showLeft ? new GridLength(230) : new GridLength(0);
        ColMid.Width = showMid ? new GridLength(_mode == "albums" ? 1.6 : 1.25, GridUnitType.Star) : new GridLength(0);
        Lay("columns");
        LeftPane.Visibility = showLeft ? Visibility.Visible : Visibility.Collapsed;
        MidPane.Visibility = showMid ? Visibility.Visible : Visibility.Collapsed;
        Lay("panes");
        var rowTemplate = (DataTemplate)FindResource(_mode == "songs" ? "SongRow" : "SongRowCompact");
        if (!ReferenceEquals(SongList.ItemTemplate, rowTemplate)) SongList.ItemTemplate = rowTemplate;
        Lay("template");
        Header.Visibility = _mode == "songs" ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = $"{lib.Albums.Select(a => a.Artist).Distinct(StringComparer.OrdinalIgnoreCase).Count()} artists  ·  {lib.Albums.Count} albums  ·  {lib.Tracks.Count} songs";

        Lay("chrome"); sw.Restart();
        if (showLeft)
        {
            var names = _mode == "artists"
                ? lib.Albums.Select(a => a.Artist)
                : lib.Tracks.Select(t => t.Genre).Where(g => g != "");
            var items = names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            LeftCap.Text = _mode == "artists" ? $"ARTISTS  {items.Count}" : $"GENRES  {items.Count}";
            items.Insert(0, _mode == "artists" ? "all artists" : "all genres");
            _loading = true;
            if (!(LeftList.ItemsSource is List<string> shown && shown.SequenceEqual(items))) LeftList.ItemsSource = items;
            var idx = _left == null ? 0 : items.FindIndex(n => string.Equals(n, _left, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) { idx = 0; _left = null; }
            LeftList.SelectedIndex = idx;
            _loading = false;
        }
        Lay("left"); sw.Restart();
        LoadAlbums();
        Trace.AppendLine($"  albums+songs {sw.ElapsedMilliseconds}");
    }

    List<Album> FilteredAlbums()
    {
        IEnumerable<Album> albums = App.Lib.Albums;
        if (_left != null && _mode == "artists") albums = albums.Where(a => string.Equals(a.Artist, _left, StringComparison.OrdinalIgnoreCase));
        if (_left != null && _mode == "genres") albums = albums.Where(a => a.Tracks.Any(t => string.Equals(t.Genre, _left, StringComparison.OrdinalIgnoreCase)));
        if (_mode == "albums") albums = albums.OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase);
        if (!Options.Active) return albums.ToList();
        // an album is shown when it fits by name, or one of its songs does
        albums = Options.Show switch
        {
            "liked" => albums.Where(a => a.Tracks.Any(t => t.Rating > 0)),
            "unplayed" => albums.Where(a => a.Tracks.All(t => t.PlayCount == 0)),
            "local" => albums.Where(a => a.Source == "local"),
            "server" => albums.Where(a => a.Source != "local"),
            _ => albums
        };
        if (Options.Text != "") albums = albums.Where(a => Options.Matches(a.Title, a.Artist) || a.Tracks.Any(t => Options.Matches(t.Title)));
        var byName = StringComparer.OrdinalIgnoreCase;
        return Options.Order(albums, sort => sort switch
        {
            "title" => list => list.OrderBy(a => a.Title, byName),
            "year" => list => list.OrderBy(a => a.Year == 0 ? int.MaxValue : a.Year).ThenBy(a => a.Title, byName),
            "added" => list => list.OrderByDescending(a => a.DateAdded),
            "plays" => list => list.OrderByDescending(a => a.Tracks.Sum(t => t.PlayCount)).ThenBy(a => a.Title, byName),
            "recent" => list => list.OrderByDescending(a => a.Tracks.Max(t => t.LastPlayed)),
            _ => null
        });
    }

    void LoadAlbums()
    {
        var albums = FilteredAlbums();
        if (_album != null) _album = albums.FirstOrDefault(a => a.Key == _album.Key);
        MidCap.Text = $"ALBUMS  {albums.Count}";
        var t = System.Diagnostics.Stopwatch.StartNew();
        if (_mode == "songs") _gridStale = true;   // hidden: build it when it is next shown
        else
        {
            if (_gridStale || _gridAlbums == null || !_gridAlbums.SequenceEqual(albums)) { AlbumGrid.SetItems(albums); _gridAlbums = albums; }
            _gridStale = false;
        }
        AlbumGrid.SelectedItem = _album;
        Lay("grid"); t.Restart();
        LoadSongs();
        Lay("songs");
    }

    void LoadSongs()
    {
        var songFilter = Options.Show != "" || Options.Text != "";
        if (_mode == "songs")
        {
            // the song list has its own column headings to sort by; the bar's "show" and filter apply to it too
            var all = App.Lib.Tracks.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase);
            _songs = Header.Sort.Apply(songFilter ? all.Where(Keep) : all);
        }
        else if (_album != null) _songs = _album.Tracks.ToList();
        else
        {
            var tracks = FilteredAlbums().SelectMany(a => a.Tracks);
            if (_left != null && _mode == "genres") tracks = tracks.Where(t => string.Equals(t.Genre, _left, StringComparison.OrdinalIgnoreCase));
            if (Options.Show is "liked") tracks = tracks.Where(t => t.Rating > 0);
            _songs = tracks.ToList();
        }
        RightCap.Text = $"SONGS  {_songs.Count}";
        if (!(SongList.ItemsSource is List<Track> shownSongs && shownSongs.SequenceEqual(_songs))) SongList.ItemsSource = _songs;
    }

    void Left_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _left = LeftList.SelectedIndex <= 0 ? null : (string)LeftList.SelectedItem;
        _album = null;
        LoadAlbums();
    }

    void Left_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_songs.Count > 0) Menus.Play(_songs);
    }

    void Left_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (_mode == "artists" && _left != null) Menus.Open(Menus.ForArtist(_left), LeftList);
    }

    void Song_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Track t) Menus.Play(_songs, t);
    }

    void Song_RightClick(object sender, MouseButtonEventArgs e)
    {
        var sel = SongList.SelectedItems.Cast<Track>().ToList();
        if (sel.Count > 0) Menus.Open(Menus.ForTracks(sel, sel.Count == 1 ? _songs : null), SongList);
    }

    void ShuffleAll_Click(object sender, RoutedEventArgs e) => Menus.PlayShuffled(_songs.Count > 0 ? _songs : App.Lib.Tracks);
    void OpenSettings_Click(object sender, RoutedEventArgs e) => App.Win.Navigate("settings");
}
