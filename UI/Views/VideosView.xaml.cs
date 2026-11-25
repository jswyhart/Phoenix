using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Halcyon.Core;

namespace Halcyon.UI;

public record VideoGroup(string Name, string Art, int Count, string? Key)
{
    public bool HasArt => Art != "";
    public double RowHeight => HasArt ? 70 : 32;
}
public record SeasonRow(string Name, int? Season);
public record VideoTile(VideoItem Video, string Title, string Caption)
{
    public string ArtPath => Video.ArtPath;
}

/// <summary>
/// Videos from every source in one place: movies, TV shows (series → seasons → episodes), collections, and
/// everything else by folder. Local files and server libraries are grouped the same way.
/// </summary>
public partial class VideosView : UserControl
{
    string _tab = "";
    string? _group;      // selected series / collection / folder
    int? _season;
    List<VideoItem> _rows = new();
    bool _loading;

    static TileLook VideoLook(object o) => o is VideoTile v ? new TileLook(v.ArtPath, v.Title, v.Caption, "\uE714") : default;

    public VideosView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible) ShowServices(); };
        ShowServices();
        Tiles.ItemActivated += o => Play(((VideoTile)o).Video);
        Tiles.ItemClicked += o => Tiles.SelectedItem = o;
        Tiles.ItemContext += (o, el) => Menus.Open(MenuFor(new List<VideoItem> { ((VideoTile)o).Video }), el);
        App.Lib.Changed += Reload;
        Reload();
    }

    static bool IsShow(VideoItem v) => v.Kind == "episode";
    static bool IsMovie(VideoItem v) => v.Kind == "movie";
    static bool IsOther(VideoItem v) => !IsShow(v) && !IsMovie(v);
    static string GroupOf(VideoItem v) => v.Series != "" ? v.Series : v.Source == "local" ? "Videos" : v.Source;

    void Tab_Click(object sender, RoutedEventArgs e) => ShowTab((string)((FrameworkElement)sender).Tag);

    /// <summary>Adds a link for each video streaming service switched on in settings, after the tabs.</summary>
    public void ShowServices()
    {
        foreach (var old in TabStrip.Children.OfType<Button>().Where(b => b.Tag is string t && t.StartsWith("web:")).ToList()) TabStrip.Children.Remove(old);
        var at = TabStrip.Children.IndexOf(TabAll) + 1;
        foreach (var service in Halcyon.Services.WebServices.Enabled(video: true))
        {
            var link = new Button { Content = service.Name.ToLowerInvariant(), Tag = service.Page, Style = (Style)FindResource("TextBtn"), FontSize = 15, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = $"Open {service.Name} here" };
            link.Click += (_, _) => App.Win.Navigate(service.Page);
            TabStrip.Children.Insert(at++, link);
        }
    }

    public List<string> ServiceLinks => TabStrip.Children.OfType<Button>().Where(b => b.Tag is string t && t.StartsWith("web:")).Select(b => (string)b.Tag).ToList();

    public void ShowTab(string tab)
    {
        _tab = tab;
        _group = null;
        _season = null;
        Reload();
    }

    void Reload()
    {
        var all = App.Lib.Videos;
        // Only offer the tabs that have something in them.
        TabMovies.Visibility = all.Any(IsMovie) ? Visibility.Visible : Visibility.Collapsed;
        TabShows.Visibility = all.Any(IsShow) ? Visibility.Visible : Visibility.Collapsed;
        TabCollections.Visibility = all.Any(v => v.Collection != "") ? Visibility.Visible : Visibility.Collapsed;
        TabFolders.Visibility = all.Any(IsOther) ? Visibility.Visible : Visibility.Collapsed;
        var tabs = new[] { TabMovies, TabShows, TabCollections, TabFolders, TabAll };
        if (_tab == "" || tabs.First(t => (string)t.Tag == _tab).Visibility != Visibility.Visible)
            _tab = (string)tabs.First(t => t.Visibility == Visibility.Visible).Tag;
        foreach (var t in tabs) t.IsChecked = (string)t.Tag == _tab;

        CountText.Text = string.Join("  ·  ", new[]
        {
            Plural(all.Count(IsMovie), "movie"), Plural(all.Where(IsShow).Select(v => v.Series).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "show"),
            Plural(all.Count(IsShow), "episode"), Plural(all.Count(IsOther), "other video")
        }.Where(s => s != ""));
        EmptyText.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var grouped = _tab is "shows" or "collections" or "folders";
        ColLeft.Width = grouped ? new GridLength(300) : new GridLength(0);
        ColSeason.Width = _tab == "shows" ? new GridLength(150) : new GridLength(0);
        LeftPane.Visibility = grouped ? Visibility.Visible : Visibility.Collapsed;
        SeasonPane.Visibility = _tab == "shows" ? Visibility.Visible : Visibility.Collapsed;

        if (grouped)
        {
            IEnumerable<IGrouping<string, VideoItem>> groups = _tab switch
            {
                "shows" => all.Where(IsShow).GroupBy(v => v.Series, StringComparer.OrdinalIgnoreCase),
                "collections" => all.Where(v => v.Collection != "").GroupBy(v => v.Collection, StringComparer.OrdinalIgnoreCase),
                _ => all.Where(IsOther).GroupBy(GroupOf, StringComparer.OrdinalIgnoreCase)
            };
            var items = groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new VideoGroup(g.Key == "" ? "Unknown" : g.Key, _tab == "shows" ? g.Select(v => v.GroupArt).FirstOrDefault(a => a != "") ?? "" : "", g.Count(), g.Key)).ToList();
            LeftCap.Text = (_tab == "shows" ? "SERIES" : _tab == "collections" ? "COLLECTIONS" : "FOLDERS") + $"  {items.Count}";
            _loading = true;
            GroupList.ItemsSource = items;
            var sel = items.FirstOrDefault(i => string.Equals(i.Key, _group, StringComparison.OrdinalIgnoreCase)) ?? items.FirstOrDefault();
            _group = sel?.Key;
            GroupList.SelectedItem = sel;
            _loading = false;
        }
        LoadSeasons();
    }

    static string Plural(int n, string noun) => n == 0 ? "" : $"{n} {noun}{(n == 1 ? "" : "s")}";

    void LoadSeasons()
    {
        if (_tab == "shows")
        {
            var seasons = App.Lib.Videos.Where(v => IsShow(v) && string.Equals(v.Series, _group, StringComparison.OrdinalIgnoreCase))
                .Select(v => v.Season).Distinct().OrderBy(s => s == 0 ? int.MaxValue : s).ToList();
            var rows = seasons.Select(s => new SeasonRow(s == 0 ? "specials" : $"season {s}", s)).ToList();
            if (rows.Count > 1) rows.Insert(0, new SeasonRow("all seasons", null));
            _loading = true;
            SeasonList.ItemsSource = rows;
            var sel = rows.FirstOrDefault(r => r.Season == _season) ?? rows.FirstOrDefault();
            _season = sel?.Season;
            SeasonList.SelectedItem = sel;
            _loading = false;
        }
        LoadContent();
    }

    void LoadContent()
    {
        var all = App.Lib.Videos;
        var byTitle = StringComparer.OrdinalIgnoreCase;
        var rows = _tab is "shows" or "folders";
        if (!rows) _rows = new();
        Tiles.Visibility = rows ? Visibility.Collapsed : Visibility.Visible;
        RowList.Visibility = rows ? Visibility.Visible : Visibility.Collapsed;

        switch (_tab)
        {
            case "shows":
                _rows = all.Where(v => IsShow(v) && string.Equals(v.Series, _group, StringComparison.OrdinalIgnoreCase) && (_season == null || v.Season == _season))
                    .OrderBy(v => v.Season == 0 ? int.MaxValue : v.Season).ThenBy(v => v.Episode).ThenBy(v => v.Title, byTitle).ToList();
                RightCap.Text = $"EPISODES  {_rows.Count}";
                RowList.ItemsSource = _rows;
                break;
            case "folders":
                _rows = all.Where(v => IsOther(v) && string.Equals(GroupOf(v), _group, StringComparison.OrdinalIgnoreCase)).OrderBy(v => v.Title, byTitle).ToList();
                RightCap.Text = $"VIDEOS  {_rows.Count}";
                RowList.ItemsSource = _rows;
                break;
            case "movies":
            case "collections":
                var movies = (_tab == "movies" ? all.Where(IsMovie) : all.Where(v => string.Equals(v.Collection, _group, StringComparison.OrdinalIgnoreCase) && v.Collection != ""))
                    .OrderBy(v => v.Title, byTitle).ToList();
                RightCap.Text = $"MOVIES  {movies.Count}";
                Tiles.TileWidth = 162;
                Tiles.TileTemplate = (DataTemplate)FindResource("PosterTile");
                Tiles.DrawWith(TileStyle.Poster, VideoLook);
                Tiles.SetItems(movies.Select(v => new VideoTile(v, v.Title, string.Join("  ·  ", new[] { v.Year > 0 ? v.Year.ToString() : "", v.DurationText }.Where(s => s != "")))));
                break;
            default:
                var everything = all.OrderBy(v => v.DisplayTitle, byTitle).ToList();
                RightCap.Text = $"VIDEOS  {everything.Count}";
                Tiles.TileWidth = 220;
                Tiles.TileTemplate = (DataTemplate)FindResource("WideTile");
                Tiles.DrawWith(TileStyle.Wide, VideoLook);
                Tiles.SetItems(everything.Select(v => new VideoTile(v, v.DisplayTitle, v.DurationText)));
                break;
        }
    }

    void Group_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || GroupList.SelectedItem is not VideoGroup g) return;
        _group = g.Key;
        _season = null;
        LoadSeasons();
    }

    void Season_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SeasonList.SelectedItem is not SeasonRow s) return;
        _season = s.Season;
        LoadContent();
    }

    // ---------- playing ----------

    static void Play(VideoItem v) => App.Player.Play(new[] { Player.From(v) });

    /// <summary>Plays one row and queues the rest of the list after it, so a season plays through.</summary>
    void PlayFrom(VideoItem v) => App.Player.Play(_rows.Select(Player.From), Math.Max(0, _rows.IndexOf(v)));

    void Group_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_rows.Count > 0 && _tab is "shows" or "folders") PlayFrom(_rows[0]);
    }

    void Row_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is VideoItem v) PlayFrom(v);
    }

    // ---------- menus & regrouping ----------

    ContextMenu MenuFor(List<VideoItem> videos)
    {
        var m = new ContextMenu();
        m.Items.Add(Menus.Item("Play", () => { if (videos.Count == 1 && _rows.Contains(videos[0])) PlayFrom(videos[0]); else App.Player.Play(videos.Select(Player.From)); }));
        m.Items.Add(Menus.Item("Add to now playing", () => App.Player.Enqueue(videos.Select(Player.From))));
        m.Items.Add(new Separator());
        m.Items.Add(Menus.Item(videos.Count == 1 ? "Edit video info…" : $"Edit info for {videos.Count} videos…", () => new EditVideoDialog(videos).ShowDialog()));
        m.Items.Add(Menus.Item(videos.Count == 1 ? "Edit tags…" : $"Edit tags for {videos.Count} videos…",
            () => new EditTagsDialog(Library.VideoTag, videos.Select(v => v.Id), videos.Count == 1 ? $"“{videos[0].Title}”" : $"{videos.Count} videos").ShowDialog()));
        if (videos.Count == 1 && videos[0].Source == "local")
            m.Items.Add(Menus.Item("Show in File Explorer", () => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{videos[0].SourceId}\"")));
        return m;
    }

    void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        var sel = RowList.SelectedItems.Cast<VideoItem>().ToList();
        if (sel.Count > 0) Menus.Open(MenuFor(sel), RowList);
    }

    void Group_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (GroupList.SelectedItem is not VideoGroup g) return;
        var members = _tab switch
        {
            "shows" => App.Lib.Videos.Where(v => IsShow(v) && string.Equals(v.Series, g.Key, StringComparison.OrdinalIgnoreCase)),
            "collections" => App.Lib.Videos.Where(v => v.Collection != "" && string.Equals(v.Collection, g.Key, StringComparison.OrdinalIgnoreCase)),
            _ => App.Lib.Videos.Where(v => IsOther(v) && string.Equals(GroupOf(v), g.Key, StringComparison.OrdinalIgnoreCase))
        };
        var list = members.OrderBy(v => v.Season).ThenBy(v => v.Episode).ThenBy(v => v.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var m = new ContextMenu();
        m.Items.Add(Menus.Item("Play all", () => App.Player.Play(list.Select(Player.From))));
        m.Items.Add(Menus.Item("Rename…", () =>
        {
            var name = InputDialog.Ask("Rename", "New name (use an existing name to merge the two)", g.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            foreach (var v in list) { if (_tab == "collections") v.Collection = name.Trim(); else v.Series = name.Trim(); }
            _group = name.Trim();
            App.Lib.SaveVideos(list);
        }));
        m.Items.Add(Menus.Item($"Edit info for all {list.Count}…", () => new EditVideoDialog(list).ShowDialog()));
        Menus.Open(m, GroupList);
    }
}
