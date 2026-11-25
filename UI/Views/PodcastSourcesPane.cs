using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.UI;

/// <summary>
/// The "sources" tab of the podcasts page: the directories to browse on the left (Apple Podcasts with its genres,
/// fyyd, and any lists of your own), the podcasts found on the right, each with a + to subscribe.
/// </summary>
public class PodcastSourcesPane : Grid
{
    readonly ListBox _sourceList = new(), _genres = new(), _found = new();
    readonly TextBlock _genreCap = Make.Styled("GENRES", "Cap");
    readonly TextBlock _foundCap = Make.Styled("PODCASTS", "Cap");
    readonly TextBlock _note = Make.Text("", 12, "FgDim", new Thickness(8, 6, 28, 6), wrap: true);
    readonly TextBox _search = new() { MinWidth = 260, VerticalContentAlignment = VerticalAlignment.Center };
    string _source = "apple";
    int _genre;
    int _asked;
    bool _filling;
    List<PodcastHit> _hits = new();

    public List<PodcastHit> Hits => _hits;
    public string Source => _source;
    public int SourcesShown => _sourceList.Items.Count;
    public int GenresShown => _genres.Items.Count;
    /// <summary>A podcast was subscribed to (its id in the library).</summary>
    public event Action<long>? Subscribed;

    public PodcastSourcesPane()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new DockPanel();
        var head = new DockPanel();
        var add = Make.Button("  add your own", "TextBtn", AddOwn, "Add a list of podcasts of your own: an OPML file or its address", new Thickness(0, -4, 4, 6));
        DockPanel.SetDock(add, Dock.Right);
        head.Children.Add(add);
        head.Children.Add(Make.Styled("SOURCES", "Cap"));
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        _sourceList.MaxHeight = 260;
        DockPanel.SetDock(_sourceList, Dock.Top);
        left.Children.Add(_sourceList);
        var export = Make.Button("  save my podcasts as a list…", "TextBtn", Export, "Writes your subscriptions to an OPML file, for another app or PC", new Thickness(8, 8, 0, 6));
        export.HorizontalAlignment = HorizontalAlignment.Left;
        DockPanel.SetDock(export, Dock.Bottom);
        left.Children.Add(export);
        _genreCap.Margin = new Thickness(_genreCap.Margin.Left, 14, 0, _genreCap.Margin.Bottom);
        DockPanel.SetDock(_genreCap, Dock.Top);
        left.Children.Add(_genreCap);
        left.Children.Add(_genres);
        Children.Add(left);

        var right = new DockPanel { Margin = new Thickness(18, 0, 0, 0) };
        var bar = new DockPanel { Margin = new Thickness(0, -4, 28, 4) };
        var go = Make.Button("  search", "TextBtn", () => _ = SearchAsync(_search.Text), null, new Thickness(12, 0, 0, 0));
        DockPanel.SetDock(go, Dock.Right);
        _foundCap.Margin = new Thickness(_foundCap.Margin.Left, _foundCap.Margin.Top, 16, _foundCap.Margin.Bottom);
        DockPanel.SetDock(_foundCap, Dock.Left);
        bar.Children.Add(_foundCap);
        bar.Children.Add(go);
        bar.Children.Add(_search);
        DockPanel.SetDock(bar, Dock.Top);
        right.Children.Add(bar);
        DockPanel.SetDock(_note, Dock.Top);
        right.Children.Add(_note);
        right.Children.Add(_found);
        SetColumn(right, 1);
        Children.Add(right);

        _search.ToolTip = "A podcast's name (what you type is sent to the source chosen on the left)";
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) _ = SearchAsync(_search.Text); };
        _sourceList.SelectionChanged += (_, _) => { if (!_filling && (_sourceList.SelectedItem as ListBoxItem)?.Tag is PodcastDirectory.Source s) Choose(s.Id); };
        _sourceList.MouseRightButtonUp += (_, e) => { if ((e.OriginalSource as FrameworkElement)?.DataContext is PodcastDirectory.Source { Yours: true } s) Menus.Open(OwnMenu(s), _sourceList); };
        _genres.SelectionChanged += (_, _) => { if (!_filling && (_genres.SelectedItem as ListBoxItem)?.Tag is int id) { _genre = id; _ = BrowseAsync(); } };
        _found.MouseDoubleClick += (_, e) => { if ((e.OriginalSource as FrameworkElement)?.DataContext is PodcastHit h) _ = SubscribeAsync(h); };
        ShowSources();
    }

    /// <summary>The tab came into view: the first time, open the chart of the chosen source.</summary>
    public void Shown()
    {
        ShowSources();
        if (_hits.Count == 0 && _asked == 0 && !App.SmokeMode) _ = BrowseAsync();
    }

    void ShowSources()
    {
        _filling = true;
        var sources = PodcastDirectory.Sources();
        if (sources.All(s => s.Id != _source)) _source = "apple";
        _sourceList.Items.Clear();
        foreach (var s in sources)
        {
            var row = Make.Column(Make.Text(s.Name, 14), Make.Text(s.Yours ? "your list  ·  " + s.Note : s.Note, 11.5, "FgFaint"));
            row.DataContext = s;
            _sourceList.Items.Add(new ListBoxItem { Content = row, Tag = s, DataContext = s, Padding = new Thickness(8, 6, 8, 6), IsSelected = s.Id == _source });
        }
        var current = sources.First(s => s.Id == _source);
        _genreCap.Visibility = _genres.Visibility = current.HasGenres ? Visibility.Visible : Visibility.Collapsed;
        _genres.Items.Clear();
        if (current.HasGenres)
        {
            _genres.Items.Add(new ListBoxItem { Content = Make.Text("top podcasts", 14), Tag = 0, Padding = new Thickness(8, 5, 8, 5), IsSelected = _genre == 0 });
            foreach (var (name, id) in PodcastDirectory.AppleGenres)
                _genres.Items.Add(new ListBoxItem { Content = Make.Text(name, 14), Tag = id, Padding = new Thickness(8, 5, 8, 5), IsSelected = _genre == id });
        }
        _filling = false;
    }

    /// <summary>Chooses a source and opens what it shows first.</summary>
    public void Choose(string source, bool browse = true)
    {
        _source = source;
        _genre = 0;
        ShowSources();
        if (browse && !App.SmokeMode) _ = BrowseAsync();
    }

    string SourceName => PodcastDirectory.Sources().FirstOrDefault(s => s.Id == _source)?.Name ?? "";

    Task BrowseAsync()
    {
        var genre = PodcastDirectory.AppleGenres.FirstOrDefault(g => g.Id == _genre).Name;
        var source = _source;
        var id = _genre;
        return AskAsync(genre != null ? genre.ToUpperInvariant() : _source == "apple" ? "TOP PODCASTS" : SourceName.ToUpperInvariant(), () => PodcastDirectory.TopAsync(source, id));
    }

    public Task SearchAsync(string text)
    {
        if (text.Trim() == "") return Task.CompletedTask;
        var source = _source;
        return AskAsync($"“{text.Trim().ToUpperInvariant()}” IN {SourceName.ToUpperInvariant()}", () => PodcastDirectory.SearchAsync(source, text));
    }

    async Task AskAsync(string title, Func<Task<List<PodcastHit>>> ask)
    {
        var mine = ++_asked;
        _foundCap.Text = title;
        _note.Text = $"asking {SourceName}…";
        try
        {
            var hits = await ask();
            if (mine == _asked) Show(hits, title);
        }
        catch (Exception ex) { if (mine == _asked) _note.Text = $"{SourceName} can't be read: {ex.Message}"; }
    }

    /// <summary>Lists podcasts on the right (also used by the self-test, which has no network to ask).</summary>
    public void Show(List<PodcastHit> hits, string title)
    {
        _hits = hits;
        _foundCap.Text = title;
        Fill();
        _note.Text = hits.Count == 0 ? "No podcasts found." : $"{hits.Count} podcasts. The + subscribes to one (or double-click it).";
    }

    static bool Have(PodcastHit h) => h.FeedUrl != "" && App.Lib.Podcasts.Any(p => string.Equals(p.FeedUrl, h.FeedUrl, StringComparison.OrdinalIgnoreCase))
                                      || App.Lib.Podcasts.Any(p => string.Equals(p.Title, h.Title, StringComparison.OrdinalIgnoreCase) && (h.Author == "" || string.Equals(p.Author, h.Author, StringComparison.OrdinalIgnoreCase)));

    void Fill()
    {
        _found.Items.Clear();
        foreach (var h in _hits)
        {
            var hit = h;
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            text.Children.Add(Make.Text(h.Title, 14));
            if (h.Subtitle != "") text.Children.Add(Make.Text(h.Subtitle, 11.5, "FgFaint"));
            var row = new DockPanel { Height = 60, Background = System.Windows.Media.Brushes.Transparent, DataContext = h };
            var cover = Make.Cover(h.Art, 50, "");
            DockPanel.SetDock(cover, Dock.Left);
            row.Children.Add(cover);
            var known = Have(h);
            var add = Make.Button(known ? "" : "", "IconBtn", () => _ = SubscribeAsync(hit), known ? "In my podcasts" : "Subscribe");
            add.Width = add.Height = 30; add.IsEnabled = !known; add.Margin = new Thickness(0, 0, 22, 0);
            DockPanel.SetDock(add, Dock.Right);
            row.Children.Add(add);
            row.Children.Add(text);
            _found.Items.Add(new ListBoxItem { Content = row, DataContext = h, Padding = new Thickness(6, 0, 6, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
    }

    /// <summary>Subscribes to a podcast from a source: fetches its feed and adds it to "my podcasts".</summary>
    public async Task SubscribeAsync(PodcastHit hit)
    {
        if (Have(hit)) return;
        App.Win.SetStatus($"subscribing to {hit.Title}…");
        try
        {
            var (podcast, episodes) = await PodcastService.FetchAsync(await PodcastDirectory.FeedAsync(hit));
            Add(podcast, episodes);
            App.Win.SetStatus($"subscribed to {podcast.Title}" + (episodes.Count > 0 && episodes.All(e => e.IsVideo) ? " (a video podcast)" : ""));
        }
        catch (Exception ex) { App.Win.SetStatus($"couldn't subscribe to {hit.Title}: {ex.Message}"); }
    }

    /// <summary>Puts a fetched podcast in the library and marks it as subscribed in the list.</summary>
    public void Add(Podcast podcast, List<Episode> episodes)
    {
        App.Lib.Db.SavePodcast(podcast, episodes);
        App.Lib.Podcasts = App.Lib.Db.LoadPodcasts();
        Fill();
        Subscribed?.Invoke(podcast.Id);
    }

    // ---------- your own sources ----------

    void AddOwn()
    {
        var where = InputDialog.Ask("Add a source of your own", "Address of an OPML list of podcasts (leave empty to choose a file on this PC)", "");
        if (where == null) return;
        where = where.Trim();
        if (where == "")
        {
            var pick = new Microsoft.Win32.OpenFileDialog { Title = "Choose an OPML list of podcasts", Filter = "OPML lists (*.opml;*.xml)|*.opml;*.xml|All files|*.*" };
            if (pick.ShowDialog(App.Win) != true) return;
            where = pick.FileName;
        }
        var name = InputDialog.Ask("Add a source of your own", "A name for it", Path.GetFileNameWithoutExtension(where.Split('?')[0].TrimEnd('/')));
        if (string.IsNullOrWhiteSpace(name)) return;
        AddSource(name, where);
    }

    /// <summary>Adds a list of your own and opens it.</summary>
    public void AddSource(string name, string where)
    {
        App.Settings.PodcastSources.Add(new PodcastSource { Name = name.Trim(), Url = where.Trim() });
        if (!App.SmokeMode) App.Settings.Save();
        _source = "own:" + (App.Settings.PodcastSources.Count - 1);
        _genre = 0;
        ShowSources();
        _ = BrowseAsync();
    }

    ContextMenu OwnMenu(PodcastDirectory.Source s)
    {
        var m = new ContextMenu();
        m.Items.Add(Menus.Item("Remove this source", () =>
        {
            if (int.TryParse(s.Id[4..], out var i) && i < App.Settings.PodcastSources.Count) App.Settings.PodcastSources.RemoveAt(i);
            if (!App.SmokeMode) App.Settings.Save();
            Choose("apple", browse: false);
        }));
        return m;
    }

    void Export()
    {
        if (App.Lib.Podcasts.Count == 0) { App.Win.SetStatus("you have no podcasts to save yet"); return; }
        var pick = new Microsoft.Win32.SaveFileDialog { Title = "Save my podcasts as a list", FileName = "Phoenix podcasts.opml", Filter = "OPML list (*.opml)|*.opml", DefaultExt = ".opml" };
        if (pick.ShowDialog(App.Win) != true) return;
        try { File.WriteAllText(pick.FileName, PodcastDirectory.ToOpml(App.Lib.Podcasts)); App.Win.SetStatus($"{App.Lib.Podcasts.Count} podcasts saved to {pick.FileName}"); }
        catch (Exception ex) { App.Win.SetStatus("couldn't save the list: " + ex.Message); }
    }
}
