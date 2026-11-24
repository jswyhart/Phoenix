using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.Desktop;

// The screens are composed in code from the shared styles and templates in Theme.axaml.

public class QuickplayView : UserControl
{
    readonly TileGrid _pins = new("SmallTile", 124), _new = new("SmallTile", 124), _history = new("SmallTile", 124), _liked = new("SmallTile", 124);
    readonly TextBlock _pinsHint = Ui.Note("Right-click an artist, album or playlist and choose “Pin to quickplay” to keep it here."),
        _historyHint = Ui.Note("What you play shows up here."), _likedHint = Ui.Note("Songs you like (the heart next to a song) collect here, newest first.");
    readonly Grid _sections;
    readonly StackPanel _empty;
    readonly StackPanel _likedButtons;

    public QuickplayView()
    {
        foreach (var grid in new[] { _pins, _new, _history, _liked }) grid.ItemClicked += o => ((QuickItem)o).Play();
        _pins.ItemContext += (o, el) =>
        {
            if (((QuickItem)o).Pin is { } pin)
                Ui.Menu(Ui.MenuItem("Play", ((QuickItem)o).Play), Ui.MenuItem("Unpin from quickplay", () => App.Lib.TogglePin(pin.Kind, pin.Key, pin.Title))).Show(el);
        };
        _likedButtons = Ui.Row(Ui.TextBtn("play all", () => App.Player.PlayTracks(App.Lib.Liked()), new Thickness(22, 26, 0, 0)),
            Ui.TextBtn("shuffle", () => App.Player.PlayShuffled(App.Lib.Liked()), new Thickness(14, 26, 0, 0)));
        Control Section(string title, TextBlock? hint, TileGrid grid, Control? extra = null)
        {
            var head = Ui.Row(Ui.Text(title, "zunehead"));
            if (extra != null) head.Children.Add(extra);
            var top = Ui.Column(head);
            if (hint != null) { hint.Margin = new Thickness(6, 16, 16, 0); top.Children.Add(hint); }
            return Ui.Under(top, grid, new Thickness(0, 0, 16, 0));
        }
        _sections = Ui.Cells("*,*,*", "380,*",
            Section("pins", _pinsHint, _pins), Section("new", null, _new).At(1), Section("history", _historyHint, _history).At(2),
            Section("liked", _likedHint, _liked, _likedButtons).At(0, 1, 3));
        _sections.Margin = new Thickness(22, 14, 0, 0);
        _empty = Ui.Column(
            Ui.H2("welcome").With(t => { t.FontSize = 34; t.HorizontalAlignment = HorizontalAlignment.Center; }),
            Ui.Note("Add your music, video and picture folders, or connect a Jellyfin or Plex server, to get started.").With(t => t.TextAlignment = TextAlignment.Center),
            Ui.Pill("open settings", () => HalcyonApp.Win.Navigate("settings")).With(b => b.HorizontalAlignment = HorizontalAlignment.Center));
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        Content = new Grid { Children = { _sections, _empty } };

        App.Lib.Changed += Reload;
        App.Lib.RatingsChanged += () => { if (IsVisible) Reload(); };
        App.Player.CurrentChanged += () => { if (IsVisible) Reload(); };
        AttachedToVisualTree += (_, _) => Reload();
        Reload();
    }

    void Reload()
    {
        var empty = App.Lib.Tracks.Count == 0 && App.Lib.Videos.Count == 0;
        _empty.IsVisible = empty;
        _sections.IsVisible = !empty;
        var pins = Catalog.Pins();
        var history = Catalog.History();
        var liked = Catalog.Liked();
        _pins.SetItems(pins.Select(Tile.Of));
        _new.SetItems(Catalog.NewAlbums().Select(Tile.Of));
        _history.SetItems(history.Select(Tile.Of));
        _liked.SetItems(liked.Select(Tile.Of));
        _pinsHint.IsVisible = pins.Count == 0;
        _historyHint.IsVisible = history.Count == 0;
        _likedHint.IsVisible = liked.Count == 0;
        _likedButtons.IsVisible = liked.Count > 0;
    }
}

public class MusicView : UserControl
{
    readonly ListBox _left = Ui.List("NameRow"), _songsList = Ui.List("SongRowCompact", multi: true).With(Row.Enable);
    readonly TileGrid _albums = new("SquareTile", 150);
    readonly TextBlock _leftCap = Ui.Cap("ARTISTS"), _midCap = Ui.Cap("ALBUMS"), _rightCap = Ui.Cap("SONGS"), _count = Ui.Text("");
    readonly SongHeader _header = new() { IsVisible = false };
    readonly Grid _grid;
    readonly Control _leftPane, _midPane;
    readonly StackPanel _empty;
    readonly Action<string> _selectTab;
    string _mode = "artists";
    string? _leftSel;
    Album? _album;
    List<Track> _songs = new();
    bool _loading;

    public MusicView()
    {
        var (tabs, select, _) = Ui.Tabs("musicTabs", new[] { ("artists", "artists"), ("genres", "genres"), ("albums", "albums"), ("songs", "songs") }, SetMode);
        _selectTab = select;
        _count.Classes.Add("cap");
        _count.Margin = new Thickness(18, 2, 0, 0);
        _count.VerticalAlignment = VerticalAlignment.Center;
        tabs.Children.Add(Ui.TextBtn("shuffle all", () => App.Player.PlayShuffled(_songs.Count > 0 ? _songs : App.Lib.Tracks), new Thickness(18, 2, 0, 0)));
        tabs.Children.Add(_count);
        tabs.Margin = new Thickness(8, 0, 0, 12);

        _leftPane = Ui.Under(_leftCap, _left);
        _midPane = Ui.Under(_midCap, _albums, new Thickness(10, 0, 0, 0));
        _empty = Ui.Column(
            Ui.H2("your collection is empty").With(t => t.HorizontalAlignment = HorizontalAlignment.Center),
            Ui.Note("Add music folders, or connect a Jellyfin or Plex server, in settings.").With(t => t.TextAlignment = TextAlignment.Center),
            Ui.Pill("open settings", () => HalcyonApp.Win.Navigate("settings")).With(b => b.HorizontalAlignment = HorizontalAlignment.Center));
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _grid = Ui.Cells("230,1.25*,*", "*", _leftPane, _midPane.At(1), Ui.Under(Ui.Column(_rightCap, _header), _songsList, new Thickness(10, 0, 0, 0)).At(2), _empty.At(0, 0, 3));
        _header.Changed += LoadSongs;
        Content = Ui.Under(tabs, _grid, new Thickness(20, 8, 0, 0));

        _albums.ItemClicked += o => { _album = ReferenceEquals(_album, o) ? null : (Album)o; _albums.SelectedItem = _album; LoadSongs(); };
        _albums.ItemActivated += o => App.Player.PlayTracks(((Album)o).Tracks);
        _albums.ItemContext += (o, el) => Menus.ForAlbum((Album)o).Show(el);
        _left.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _leftSel = _left.SelectedIndex <= 0 ? null : (string?)_left.SelectedItem;
            _album = null;
            LoadAlbums();
        };
        _left.DoubleTapped += (_, _) => { if (_songs.Count > 0) App.Player.PlayTracks(_songs); };
        _left.ContextRequested += (_, e) => { if (_mode == "artists" && _leftSel != null) { Menus.ForArtist(_leftSel).Show(_left); e.Handled = true; } };
        _songsList.DoubleTapped += (_, e) => { if (Ui.Item<Track>(e) is { } t) App.Player.PlayTracks(_songs, t); };
        _songsList.ContextRequested += (_, e) =>
        {
            var sel = _songsList.Selected<Track>();
            if (sel.Count > 0) { Menus.ForTracks(sel, sel.Count == 1 ? _songs : null).Show(_songsList); e.Handled = true; }
        };
        App.Lib.Changed += Reload;
        _selectTab(_mode);
        Reload();
    }

    public void SetMode(string mode)
    {
        _mode = mode;
        _leftSel = null;
        _album = null;
        _selectTab(mode);
        Reload();
    }

    void Reload()
    {
        var lib = App.Lib;
        _empty.IsVisible = lib.Tracks.Count == 0;
        var showLeft = _mode is "artists" or "genres";
        var showMid = _mode != "songs";
        _grid.ColumnDefinitions[0].Width = new GridLength(showLeft ? 230 : 0);
        _grid.ColumnDefinitions[1].Width = showMid ? new GridLength(_mode == "albums" ? 1.6 : 1.25, GridUnitType.Star) : new GridLength(0);
        _leftPane.IsVisible = showLeft;
        _midPane.IsVisible = showMid;
        _songsList.ItemTemplate = Ui.Template(_mode == "songs" ? "SongRow" : "SongRowCompact");
        _header.IsVisible = _mode == "songs";
        _count.Text = Fmt.Join(Fmt.Plural(Catalog.Artists().Count, "artist"), Fmt.Plural(lib.Albums.Count, "album"), Fmt.Plural(lib.Tracks.Count, "song"));

        if (showLeft)
        {
            var items = _mode == "artists" ? Catalog.Artists() : Catalog.Genres();
            _leftCap.Text = (_mode == "artists" ? "ARTISTS" : "GENRES") + $"  {items.Count}";
            items.Insert(0, _mode == "artists" ? "all artists" : "all genres");
            _loading = true;
            _left.ItemsSource = items;
            var idx = _leftSel == null ? 0 : items.FindIndex(n => string.Equals(n, _leftSel, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) { idx = 0; _leftSel = null; }
            _left.SelectedIndex = idx;
            _loading = false;
        }
        LoadAlbums();
    }

    List<Album> FilteredAlbums()
    {
        if (_leftSel != null && _mode == "artists") return Catalog.AlbumsOf(_leftSel);
        if (_leftSel != null && _mode == "genres") return Catalog.AlbumsInGenre(_leftSel);
        return _mode == "albums" ? App.Lib.Albums.OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase).ToList() : App.Lib.Albums.ToList();
    }

    void LoadAlbums()
    {
        var albums = FilteredAlbums();
        if (_album != null) _album = albums.FirstOrDefault(a => a.Key == _album.Key);
        _midCap.Text = $"ALBUMS  {albums.Count}";
        _albums.SetItems(albums.Select(Tile.Of));
        _albums.SelectedItem = _album;
        LoadSongs();
    }

    void LoadSongs()
    {
        if (_mode == "songs") _songs = _header.Sort.Apply(Catalog.Songs());
        else if (_album != null) _songs = _album.Tracks.ToList();
        else
        {
            var tracks = FilteredAlbums().SelectMany(a => a.Tracks);
            if (_leftSel != null && _mode == "genres") tracks = tracks.Where(t => string.Equals(t.Genre, _leftSel, StringComparison.OrdinalIgnoreCase));
            _songs = tracks.ToList();
        }
        _rightCap.Text = $"SONGS  {_songs.Count}";
        _songsList.ItemsSource = _songs;
    }
}

public record SeasonRow(string Name, int? Season);

public class VideosView : UserControl
{
    readonly ListBox _groups = Ui.List("GroupRow"), _seasons = Ui.List("NamedRow"), _rowList = Ui.List("VideoRow", multi: true);
    readonly TileGrid _tiles = new("PosterTile", 162);
    readonly TextBlock _leftCap = Ui.Cap("SERIES"), _rightCap = Ui.Cap("VIDEOS"), _count = Ui.Text("", "cap"),
        _emptyText = Ui.Note("Nothing here yet. Add video folders or connect a server in settings.");
    readonly Grid _grid;
    readonly Control _leftPane, _seasonPane;
    readonly DockPanel _rightPane;
    readonly Action<string> _selectTab;
    readonly Action<IEnumerable<string>> _visibleTabs;
    string _tab = "";
    string? _group;
    int? _season;
    List<VideoItem> _rows = new();
    bool _loading;

    public VideosView()
    {
        var (tabs, select, visible) = Ui.Tabs("videoTabs", new[] { ("movies", "movies"), ("shows", "tv shows"), ("collections", "collections"), ("folders", "other videos"), ("all", "all") }, ShowTab);
        _selectTab = select;
        _visibleTabs = visible;
        _count.Margin = new Thickness(18, 2, 0, 0);
        _count.VerticalAlignment = VerticalAlignment.Center;
        tabs.Children.Add(_count);
        tabs.Margin = new Thickness(8, 0, 0, 12);
        _leftPane = Ui.Under(_leftCap, _groups);
        _seasonPane = Ui.Under(Ui.Cap("SEASONS"), _seasons, new Thickness(10, 0, 0, 0));
        _emptyText.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyText.VerticalAlignment = VerticalAlignment.Center;
        _grid = Ui.Cells("300,150,*", "*", _leftPane, _seasonPane.At(1),
            (_rightPane = Ui.Under(_rightCap, new Grid { Children = { _tiles, _rowList } }, new Thickness(10, 0, 0, 0))).At(2), _emptyText.At(0, 0, 3));
        Content = Ui.Under(tabs, _grid, new Thickness(20, 8, 0, 0));

        _tiles.ItemClicked += o => _tiles.SelectedItem = o;
        _tiles.ItemActivated += o => App.Player.Play(new[] { Menus.Item((VideoItem)o) });
        _tiles.ItemContext += (o, el) => MenuFor(new List<VideoItem> { (VideoItem)o }).Show(el);
        _groups.SelectionChanged += (_, _) =>
        {
            if (_loading || _groups.SelectedItem is not VideoGroup g) return;
            _group = g.Key;
            _season = null;
            LoadSeasons();
        };
        _groups.DoubleTapped += (_, _) => { if (_rows.Count > 0) PlayFrom(_rows[0]); };
        _groups.ContextRequested += (_, e) => { if (_groups.SelectedItem is VideoGroup g) { GroupMenu(g).Show(_groups); e.Handled = true; } };
        _seasons.SelectionChanged += (_, _) =>
        {
            if (_loading || _seasons.SelectedItem is not SeasonRow s) return;
            _season = s.Season;
            LoadContent();
        };
        _rowList.DoubleTapped += (_, e) => { if (Ui.Item<VideoItem>(e) is { } v) PlayFrom(v); };
        _rowList.ContextRequested += (_, e) => { var sel = _rowList.Selected<VideoItem>(); if (sel.Count > 0) { MenuFor(sel).Show(_rowList); e.Handled = true; } };
        App.Lib.Changed += Reload;
        Reload();
    }

    public void ShowTab(string tab)
    {
        _tab = tab;
        _group = null;
        _season = null;
        Reload();
    }

    void Reload()
    {
        var tabs = Catalog.VideoTabs();
        _visibleTabs(tabs);
        if (!tabs.Contains(_tab)) _tab = tabs[0];
        _selectTab(_tab);
        _count.Text = Catalog.VideoCounts();
        _emptyText.IsVisible = App.Lib.Videos.Count == 0;

        var grouped = _tab is "shows" or "collections" or "folders";
        _grid.ColumnDefinitions[0].Width = new GridLength(grouped ? 300 : 0);
        _grid.ColumnDefinitions[1].Width = new GridLength(_tab == "shows" ? 150 : 0);
        _leftPane.IsVisible = grouped;
        _seasonPane.IsVisible = _tab == "shows";
        // with no list beside it, the grid lines up with the other pages' left edge
        _rightPane.Margin = new Thickness(grouped ? 10 : 0, 0, 0, 0);
        if (grouped)
        {
            var items = Catalog.VideoGroups(_tab);
            if (_tab != "shows") items = items.Select(g => g with { Art = "" }).ToList();   // posters only make sense for series
            _leftCap.Text = (_tab == "shows" ? "SERIES" : _tab == "collections" ? "COLLECTIONS" : "FOLDERS") + $"  {items.Count}";
            _loading = true;
            _groups.ItemsSource = items;
            var sel = items.FirstOrDefault(i => string.Equals(i.Key, _group, StringComparison.OrdinalIgnoreCase)) ?? items.FirstOrDefault();
            _group = sel?.Key;
            _groups.SelectedItem = sel;
            _loading = false;
        }
        LoadSeasons();
    }

    void LoadSeasons()
    {
        if (_tab == "shows" && _group != null)
        {
            var rows = Catalog.Seasons(_group).Select(s => new SeasonRow(Catalog.SeasonName(s), s)).ToList();
            if (rows.Count > 1) rows.Insert(0, new SeasonRow("all seasons", null));
            _loading = true;
            _seasons.ItemsSource = rows;
            var sel = rows.FirstOrDefault(r => r.Season == _season) ?? rows.FirstOrDefault();
            _season = sel?.Season;
            _seasons.SelectedItem = sel;
            _loading = false;
        }
        LoadContent();
    }

    void LoadContent()
    {
        var rows = _tab is "shows" or "folders";
        _tiles.IsVisible = !rows;
        _rowList.IsVisible = rows;
        _rows = new();
        switch (_tab)
        {
            case "shows":
            case "folders":
                _rows = _group == null ? new() : Catalog.GroupMembers(_tab, _group).Where(v => _tab != "shows" || _season == null || v.Season == _season).ToList();
                _rightCap.Text = (_tab == "shows" ? "EPISODES" : "VIDEOS") + $"  {_rows.Count}";
                _rowList.ItemsSource = _rows;
                break;
            case "movies":
            case "collections":
                var movies = _tab == "movies" ? Catalog.Movies() : _group == null ? new() : Catalog.GroupMembers("collections", _group);
                _rightCap.Text = $"MOVIES  {movies.Count}";
                _tiles.TileWidth = 162;
                _tiles.TileTemplate = Ui.Template("PosterTile");
                _tiles.SetItems(movies.Select(Tile.Movie));
                break;
            default:
                var all = Catalog.AllVideos();
                _rightCap.Text = $"VIDEOS  {all.Count}";
                _tiles.TileWidth = 220;
                _tiles.TileTemplate = Ui.Template("WideTile");
                _tiles.SetItems(all.Select(Tile.Video));
                break;
        }
    }

    /// <summary>Plays one row and queues the rest of the list after it, so a season plays through.</summary>
    void PlayFrom(VideoItem v) => App.Player.Play(_rows.Select(Menus.Item), Math.Max(0, _rows.IndexOf(v)));

    ContextMenu MenuFor(List<VideoItem> videos) => Ui.Menu(
        Ui.MenuItem("Play", () => { if (videos.Count == 1 && _rows.Contains(videos[0])) PlayFrom(videos[0]); else App.Player.Play(videos.Select(Menus.Item)); }),
        Ui.MenuItem("Add to now playing", () => App.Player.Enqueue(videos.Select(Menus.Item))),
        new Separator(),
        Ui.MenuItem(videos.Count == 1 ? "Edit video info…" : $"Edit info for {videos.Count} videos…", () => new EditVideoDialog(videos).ShowDialog(HalcyonApp.Win)),
        videos.Count == 1 && videos[0].Source == "local" ? Ui.MenuItem(Platform.IsMac ? "Show in Finder" : "Show in file manager", () => Platform.RevealFile(videos[0].SourceId)) : null);

    ContextMenu GroupMenu(VideoGroup g)
    {
        var tab = _tab;
        var list = Catalog.GroupMembers(tab, g.Key);
        return Ui.Menu(
            Ui.MenuItem("Play all", () => App.Player.Play(list.Select(Menus.Item))),
            Ui.MenuItem("Rename…", async () =>
            {
                var name = await Dialog.Ask("Rename", "New name (use an existing name to merge the two)", g.Name);
                if (string.IsNullOrWhiteSpace(name)) return;
                _group = name.Trim();
                Catalog.RenameGroup(tab, g.Key, name);
            }),
            Ui.MenuItem($"Edit info for all {list.Count}…", () => new EditVideoDialog(list).ShowDialog(HalcyonApp.Win)));
    }
}

public class PicturesView : UserControl
{
    readonly ListBox _folders = Ui.List("CountRow");
    readonly TileGrid _grid = new("PictureTile", 160);
    readonly TextBlock _folderCap = Ui.Cap("FOLDERS"), _picCap = Ui.Cap("PICTURES"), _empty = Ui.Note("No pictures found. Add picture folders in settings."),
        _caption = new() { Foreground = Brushes.Silver, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 12) };
    readonly Image _image = new() { Stretch = Stretch.Uniform, Margin = new Thickness(60, 40) };
    readonly Grid _viewer;
    readonly DispatcherTimer _slideTimer;
    List<PictureItem> _pics = new();
    int _index;
    bool _loaded;

    public PicturesView()
    {
        Focusable = true;
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.IsVisible = false;
        var browse = Ui.Cells("260,*", "*",
            Ui.Under(_folderCap, _folders),
            Ui.Under(Ui.Split(_picCap, Ui.TextBtn("play slideshow", () => OpenViewer(0, true), new Thickness(0, -4, 28, 6))), _grid, new Thickness(10, 0, 0, 0)).At(1),
            _empty.At(0, 0, 2));
        browse.Margin = new Thickness(20, 8, 0, 0);

        Button Nav(string text, HorizontalAlignment side, Action click) =>
            Ui.TextBtn(text, click, new Thickness(12)).With(b => { b.Classes.Add("big"); b.FontSize = 34; b.Foreground = Brushes.White; b.HorizontalAlignment = side; b.VerticalAlignment = VerticalAlignment.Center; });
        var close = Ui.TextBtn("close", CloseViewer, new Thickness(0, 12, 16, 0)).With(b => { b.Classes.Add("big"); b.Foreground = Brushes.White; b.HorizontalAlignment = HorizontalAlignment.Right; b.VerticalAlignment = VerticalAlignment.Top; });
        _viewer = new Grid { Background = new SolidColorBrush(Color.FromArgb(0xFF, 0, 0, 0)), IsVisible = false, Children = { _image, _caption, Nav("‹", HorizontalAlignment.Left, () => Manual(-1)), Nav("›", HorizontalAlignment.Right, () => Manual(1)), close } };
        // double-clicking the picture closes the viewer, but quick clicks on the arrows or close button must not
        _viewer.DoubleTapped += (_, e) =>
        {
            for (var v = e.Source as Avalonia.Visual; v != null && v != _viewer; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
                if (v is Button) return;
            CloseViewer();
        };
        Content = new Grid { Children = { browse, _viewer } };

        _slideTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) => Step(1));
        _slideTimer.Stop();
        // one click opens a picture, as in the Windows app
        _grid.ItemClicked += o => OpenViewer(_pics.IndexOf((PictureItem)o), false);
        _folders.SelectionChanged += (_, _) =>
        {
            if (_folders.SelectedItem is not PicFolder f) return;
            _pics = Catalog.PicturesIn(f);
            _picCap.Text = $"PICTURES  {_pics.Count}";
            _grid.SetItems(_pics.Select(Tile.Of));
        };
        KeyDown += (_, e) =>
        {
            if (!_viewer.IsVisible) return;
            if (e.Key == Key.Escape) { CloseViewer(); e.Handled = true; }
            else if (e.Key == Key.Left) { Manual(-1); e.Handled = true; }
            else if (e.Key == Key.Right) { Manual(1); e.Handled = true; }
        };
        AttachedToVisualTree += (_, _) => { if (!_loaded) _ = ReloadAsync(); };
    }

    public async Task ReloadAsync()
    {
        _loaded = true;
        var roots = App.Settings.PictureFolders.ToList();
        var folders = await Task.Run(() => Catalog.PictureFolders(roots));
        // Photo libraries on connected servers sit alongside the local folders.
        foreach (var fetch in new Func<Task<List<RemotePhoto>>>[] { App.Jellyfin.FetchPhotosAsync, App.Plex.FetchPhotosAsync })
            try
            {
                foreach (var g in (await fetch()).GroupBy(p => p.Folder).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                    folders.Add(new PicFolder(g.Key, "", g.Count(), g.Select(p => new PictureItem(p.Name, p.FullUrl, p.ThumbUrl)).ToList()));
            }
            catch { /* server offline: show what we have */ }
        _folderCap.Text = $"FOLDERS  {folders.Count}";
        _folders.ItemsSource = folders;
        _empty.IsVisible = folders.Count == 0;
        if (folders.Count > 0) _folders.SelectedIndex = 0; else { _pics = new(); _grid.SetItems(Array.Empty<Tile>()); }
    }

    void OpenViewer(int index, bool slideshow)
    {
        if (_pics.Count == 0) return;
        _index = Math.Max(0, index);
        _viewer.IsVisible = true;
        Focus();
        ShowCurrent();
        if (slideshow) _slideTimer.Start();
    }

    // the picture on screen and its neighbours, decoded at screen size, so the arrows answer at once
    readonly Dictionary<string, Task<Avalonia.Media.Imaging.Bitmap?>> _near = new();

    Task<Avalonia.Media.Imaging.Bitmap?> Decode(PictureItem pic)
    {
        if (!_near.TryGetValue(pic.File, out var t)) _near[pic.File] = t = Art.LoadAsync(pic.File, 2400, cache: false);
        return t;
    }

    async void ShowCurrent()
    {
        var pic = _pics[_index];
        var label = $"{pic.Name}   ·   {_index + 1} of {_pics.Count}";
        var load = Decode(pic);
        if (!load.IsCompleted)
        {
            _image.Source = null;   // don't leave the previous picture up while this one loads
            _caption.Text = label + "   ·   loading…";
        }
        var bmp = await load;
        if (_index >= _pics.Count || _pics[_index] != pic) return;   // moved on meanwhile
        _image.Source = bmp;
        _caption.Text = bmp == null ? label + "   ·   this picture can't be shown" : label;
        // get the next and previous pictures ready, and let go of the rest
        var keep = new[] { _index, (_index + 1) % _pics.Count, (_index - 1 + _pics.Count) % _pics.Count }.Select(i => _pics[i].File).ToHashSet();
        foreach (var k in _near.Keys.Where(k => !keep.Contains(k)).ToList()) _near.Remove(k);
        foreach (var f in keep) Decode(_pics.First(p => p.File == f));
    }

    void Manual(int delta) { _slideTimer.Stop(); Step(delta); }

    // for the self-test
    public void OpenAt(int index) => OpenViewer(index, false);
    public void Next() => Manual(1);
    public void Close() => CloseViewer();
    public string ViewerCaption => _caption.Text ?? "";
    public bool ShowingPicture => _image.Source != null;
    public bool ViewerOpen => _viewer.IsVisible;
    public Button? NextButton => _viewer.Children.OfType<Button>().FirstOrDefault(b => b.HorizontalAlignment == HorizontalAlignment.Right && b.VerticalAlignment == VerticalAlignment.Center);

    void Step(int delta)
    {
        if (_pics.Count == 0) return;
        _index = (_index + delta + _pics.Count) % _pics.Count;
        ShowCurrent();
    }

    void CloseViewer()
    {
        _slideTimer.Stop();
        _viewer.IsVisible = false;
        _image.Source = null;
        _near.Clear();
    }
}

public class PodcastsView : UserControl
{
    readonly ListBox _series = Ui.List("PodcastRow"), _episodeList = Ui.List("EpisodeRow");
    readonly TextBlock _seriesCap = Ui.Cap("SERIES"), _epCap = Ui.Cap("EPISODES"), _title = Ui.H2(""), _desc = Ui.Note(""),
        _empty = Ui.Note("No podcasts yet. Choose “add” and paste a podcast's RSS feed address.");
    readonly Button _refresh;
    List<Episode> _episodes = new();
    Podcast? _podcast;

    public PodcastsView()
    {
        _refresh = Ui.TextBtn("refresh", Refresh, new Thickness(0));
        _title.Margin = new Thickness(8, 0, 28, 4);
        _desc.Margin = new Thickness(8, 0, 28, 12);
        _desc.MaxHeight = 54;
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        var left = Ui.Under(Ui.Split(_seriesCap, Ui.Row(Ui.TextBtn("add", Add, new Thickness(0, 0, 12, 0)), _refresh).With(r => r.Margin = new Thickness(0, -4, 4, 6))), _series);
        var right = Ui.Under(Ui.Column(_title, _desc, _epCap), _episodeList, new Thickness(18, 0, 0, 0));
        Content = Ui.Cells("320,*", "*", left, right.At(1), _empty.At(0, 0, 2)).With(g => g.Margin = new Thickness(20, 8, 0, 0));

        _series.SelectionChanged += (_, _) => ShowPodcast(_series.SelectedItem as Podcast);
        _series.ContextRequested += (_, e) =>
        {
            if (_series.SelectedItem is not Podcast p) return;
            Ui.Menu(Ui.MenuItem("Play newest episode", () => { if (_episodes.Count > 0) Play(_episodes[0]); }),
                Ui.MenuItem("Unsubscribe", () => { App.Lib.Db.DeletePodcast(p.Id); App.Lib.Podcasts = App.Lib.Db.LoadPodcasts(); Reload(null); })).Show(_series);
            e.Handled = true;
        };
        _episodeList.DoubleTapped += (_, e) => { if (Ui.Item<Episode>(e) is { } ep) Play(ep); };
        _episodeList.ContextRequested += (_, e) =>
        {
            if (_episodeList.SelectedItem is not Episode ep) return;
            Ui.Menu(Ui.MenuItem("Play", () => Play(ep)), Ui.MenuItem(ep.Played ? "Mark as unplayed" : "Mark as played", () =>
            {
                ep.Played = !ep.Played;
                ep.PositionMs = 0;
                App.Lib.Db.SaveEpisodeState(ep);
                ep.Raise();
            })).Show(_episodeList);
            e.Handled = true;
        };
        Reload(null);
    }

    void Reload(long? selectId)
    {
        var pods = App.Lib.Podcasts;
        _seriesCap.Text = $"SERIES  {pods.Count}";
        _series.ItemsSource = pods.ToList();
        _empty.IsVisible = pods.Count == 0;
        var sel = pods.FirstOrDefault(p => p.Id == selectId) ?? pods.FirstOrDefault();
        _series.SelectedItem = sel;
        if (sel == null) ShowPodcast(null);
    }

    void ShowPodcast(Podcast? p)
    {
        _podcast = p;
        _title.Text = p?.Title ?? "";
        _desc.Text = p?.Description ?? "";
        _episodes = p == null ? new() : App.Lib.Db.LoadEpisodes(p.Id);
        _epCap.Text = p == null ? "" : $"EPISODES  {_episodes.Count}";
        _episodeList.ItemsSource = _episodes;
    }

    void Play(Episode ep)
    {
        if (_podcast != null) App.Player.Play(new[] { Player.From(ep, _podcast) });
    }

    async void Add()
    {
        var url = await Dialog.Ask("Add a podcast", "Podcast RSS feed address", "https://");
        if (string.IsNullOrWhiteSpace(url) || url.Trim() == "https://") return;
        try
        {
            App.SetStatus("fetching podcast…");
            var (podcast, episodes) = await PodcastService.FetchAsync(url);
            App.Lib.Db.SavePodcast(podcast, episodes);
            App.Lib.Podcasts = App.Lib.Db.LoadPodcasts();
            App.SetStatus("");
            Reload(podcast.Id);
        }
        catch (Exception ex)
        {
            App.SetStatus("");
            await Dialog.Message("Couldn't add that podcast.\n\n" + ex.Message);
        }
    }

    async void Refresh()
    {
        _refresh.IsEnabled = false;
        var failed = 0;
        foreach (var p in App.Lib.Podcasts.ToList())
        {
            App.SetStatus($"refreshing {p.Title}…");
            try
            {
                var (podcast, episodes) = await PodcastService.FetchAsync(p.FeedUrl);
                App.Lib.Db.SavePodcast(podcast, episodes);
            }
            catch { failed++; }
        }
        App.Lib.Podcasts = App.Lib.Db.LoadPodcasts();
        App.SetStatus(failed > 0 ? $"{failed} podcast feed(s) could not be refreshed" : "");
        _refresh.IsEnabled = true;
        Reload(_podcast?.Id);
    }
}

public record PlaylistRow(string Name, Playlist? Playlist, string? Auto);

public class PlaylistsView : UserControl
{
    readonly ListBox _lists = Ui.List("NamedRow"), _songList = Ui.List("SongRow", multi: true).With(Row.Enable);
    readonly TextBlock _songCap = Ui.Cap("SONGS");
    readonly SongHeader _songHeader = new();
    List<Track> _songs = new();
    PlaylistRow? _row;
    bool _loading;

    public PlaylistsView()
    {
        var left = Ui.Under(Ui.Split(Ui.Cap("PLAYLISTS"), Ui.TextBtn("new playlist", New, new Thickness(0, -4, 4, 6))), _lists);
        var actions = Ui.Row(Ui.TextBtn("play", () => App.Player.PlayTracks(_songs)), Ui.TextBtn("shuffle", () => App.Player.PlayShuffled(_songs), new Thickness(0))).With(r => r.Margin = new Thickness(0, -4, 28, 6));
        var right = Ui.Under(Ui.Column(Ui.Split(_songCap, actions), _songHeader), _songList, new Thickness(18, 0, 0, 0));
        _songHeader.Changed += () => { if (_row != null) Show(_row); };
        Content = Ui.Cells("280,*", "*", left, right.At(1)).With(g => g.Margin = new Thickness(20, 8, 0, 0));

        _lists.SelectionChanged += (_, _) => { if (!_loading && _lists.SelectedItem is PlaylistRow row) Show(row); };
        _lists.DoubleTapped += (_, _) => App.Player.PlayTracks(_songs);
        _lists.ContextRequested += (_, e) => { if (_lists.SelectedItem is PlaylistRow row) { ListMenu(row).Show(_lists); e.Handled = true; } };
        _songList.DoubleTapped += (_, e) => { if (Ui.Item<Track>(e) is { } t) App.Player.PlayTracks(_songs, t); };
        _songList.ContextRequested += (_, e) => { SongMenu()?.Show(_songList); e.Handled = true; };
        App.Lib.Changed += Reload;
        App.Lib.RatingsChanged += () => { if (IsVisible && _row?.Auto == "liked") Reload(); };
        AttachedToVisualTree += (_, _) => Reload();
        Reload();
    }

    void Reload()
    {
        var rows = Catalog.AutoPlaylists.Select(k => new PlaylistRow(k, null, k)).Concat(App.Lib.Playlists.Select(p => new PlaylistRow(p.Name, p, null))).ToList();
        _loading = true;
        _lists.ItemsSource = rows;
        var sel = _row == null ? rows[0] : rows.FirstOrDefault(r => r.Auto == _row.Auto && r.Playlist?.Id == _row.Playlist?.Id) ?? rows[0];
        _lists.SelectedItem = sel;
        _loading = false;
        Show(sel);
    }

    void Show(PlaylistRow row)
    {
        _row = row;
        _songs = row.Playlist != null ? App.Lib.TracksOf(row.Playlist) : App.Lib.AutoPlaylist(row.Auto!);
        _songs = _songHeader.Sort.Apply(_songs);
        _songCap.Text = $"SONGS  {_songs.Count}   ·   {Fmt.Time(_songs.Sum(t => t.DurationMs))}";
        _songList.ItemsSource = _songs;
    }

    async void New()
    {
        var name = await Dialog.Ask("New playlist", "Playlist name", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        var p = App.Lib.CreatePlaylist(name.Trim());
        _row = new PlaylistRow(p.Name, p, null);
        Reload();
    }

    ContextMenu ListMenu(PlaylistRow row)
    {
        var items = new List<object?> { Ui.MenuItem("Play", () => App.Player.PlayTracks(_songs)), Ui.MenuItem("Add to now playing", () => App.Player.EnqueueTracks(_songs)) };
        if (row.Playlist is { } p)
        {
            var key = p.Id.ToString();
            items.Add(Ui.MenuItem(App.Lib.IsPinned("playlist", key) ? "Unpin from quickplay" : "Pin to quickplay", () => App.Lib.TogglePin("playlist", key, p.Name)));
            items.Add(new Separator());
            items.Add(Ui.MenuItem("Rename…", async () =>
            {
                var name = await Dialog.Ask("Rename playlist", "Playlist name", p.Name);
                if (string.IsNullOrWhiteSpace(name)) return;
                p.Name = name.Trim();
                App.Lib.SavePlaylist(p);
            }));
            items.Add(Ui.MenuItem("Delete playlist", async () =>
            {
                if (!await Dialog.Confirm($"Delete the playlist “{p.Name}”? The songs stay in your collection.", "delete")) return;
                _row = null;
                App.Lib.DeletePlaylist(p);
            }));
        }
        return Ui.Menu(items.ToArray());
    }

    ContextMenu? SongMenu()
    {
        var sel = _songList.Selected<Track>();
        if (sel.Count == 0) return null;
        var m = Menus.ForTracks(sel, sel.Count == 1 ? _songs : null);
        if (_row?.Playlist is not { } p) return m;
        var indexes = sel.Select(t => _songs.IndexOf(t)).OrderBy(i => i).ToList();
        void Move(int delta)
        {
            var ids = _songs.Select(t => t.Id).ToList();
            foreach (var i in delta < 0 ? indexes : indexes.AsEnumerable().Reverse()) (ids[i], ids[i + delta]) = (ids[i + delta], ids[i]);
            p.TrackIds = ids;
            App.Lib.SavePlaylist(p);
        }
        m.Items.Add(new Separator());
        m.Items.Add(Ui.MenuItem("Move up", () => Move(-1), indexes[0] > 0));
        m.Items.Add(Ui.MenuItem("Move down", () => Move(1), indexes[^1] < _songs.Count - 1));
        m.Items.Add(Ui.MenuItem("Remove from playlist", () =>
        {
            var ids = _songs.Select(t => t.Id).ToList();
            foreach (var i in indexes.OrderByDescending(i => i)) ids.RemoveAt(i);
            p.TrackIds = ids;
            App.Lib.SavePlaylist(p);
        }));
        return m;
    }
}

public class SearchView : UserControl
{
    readonly TileGrid _albums = new("SquareTile", 150);
    readonly ListBox _songList = Ui.List("SongRow", multi: true);
    readonly TextBlock _header = Ui.H2(""), _albumCap = Ui.Cap("ALBUMS"), _songCap = Ui.Cap("SONGS");
    List<Track> _songs = new();
    string _query = "";

    public SearchView()
    {
        _header.Margin = new Thickness(8, 0, 0, 12);
        Content = Ui.Under(_header, Ui.Cells("1.1*,*", "*", Ui.Under(_albumCap, _albums), Ui.Under(_songCap, _songList, new Thickness(10, 0, 0, 0)).At(1)), new Thickness(20, 8, 0, 0));
        _albums.ItemClicked += o => { _albums.SelectedItem = o; _songList.ItemsSource = _songs = ((Album)o).Tracks.ToList(); _songCap.Text = $"SONGS  {_songs.Count}"; };
        _albums.ItemActivated += o => App.Player.PlayTracks(((Album)o).Tracks);
        _albums.ItemContext += (o, el) => Menus.ForAlbum((Album)o).Show(el);
        _songList.DoubleTapped += (_, e) => { if (Ui.Item<Track>(e) is { } t) App.Player.PlayTracks(_songs, t); };
        _songList.ContextRequested += (_, e) =>
        {
            var sel = _songList.Selected<Track>();
            if (sel.Count > 0) { Menus.ForTracks(sel, sel.Count == 1 ? _songs : null).Show(_songList); e.Handled = true; }
        };
        App.Lib.Changed += () => { if (_query != "") Search(_query); };
    }

    public void Search(string query)
    {
        _query = query;
        var (albums, songs, _) = Catalog.Search(query);
        _songs = songs;
        _header.Text = $"results for “{query}”";
        _albumCap.Text = $"ALBUMS  {albums.Count}";
        _songCap.Text = $"SONGS  {songs.Count}";
        _albums.SelectedItem = null;
        _albums.SetItems(albums.Select(Tile.Of));
        _songList.ItemsSource = songs;
    }
}
