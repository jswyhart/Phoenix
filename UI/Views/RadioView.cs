using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.UI;

/// <summary>
/// Internet radio, in two tabs: "my stations" (the ones you saved) and "sources" (the station directory, browsed by
/// what is popular, by genre, by country, or searched by name), where stations are added from.
/// </summary>
public class RadioView : UserControl
{
    readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 10) };
    readonly Grid _minePane = new(), _sourcesPane = new();
    readonly ListBox _mine = new(), _found = new(), _groups = new();
    readonly StackPanel _ways = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
    readonly TextBox _search = new() { MinWidth = 260, VerticalContentAlignment = VerticalAlignment.Center };
    readonly TextBlock _note = Make.Text("", 12, "FgDim", new Thickness(8, 6, 28, 6), wrap: true);
    readonly TextBlock _groupNote = Make.Text("", 12, "FgFaint", new Thickness(8, 6, 8, 6), wrap: true);
    readonly TextBlock _foundCap = Make.Styled("STATIONS", "Cap");
    readonly TextBlock _mineHint = Make.Text("Stations you save appear here. Find some under “sources”, or add a stream's address yourself.", 13, "FgFaint", new Thickness(8, 10, 12, 0), wrap: true);
    string _tab = "mine", _way = "popular";
    List<Station> _results = new();
    List<Radio.Group>? _genres, _countries;
    int _asked;

    public List<Station> Results => _results;
    public int SavedShown => _mine.Items.Count;
    public string Tab => _tab;
    public string Way => _way;
    public int GroupsShown => _groups.Items.Count;
    public bool SourcesVisible => _sourcesPane.Visibility == Visibility.Visible;

    public RadioView()
    {
        // ----- my stations -----
        var mineHead = new DockPanel();
        var add = Make.Button("  add by address", "TextBtn", AddByAddress, "Add a station by its stream address", new Thickness(0, -4, 28, 6));
        DockPanel.SetDock(add, Dock.Right);
        mineHead.Children.Add(add);
        mineHead.Children.Add(Make.Styled("MY STATIONS", "Cap"));
        var mine = new DockPanel();
        DockPanel.SetDock(mineHead, Dock.Top);
        mine.Children.Add(mineHead);
        DockPanel.SetDock(_mineHint, Dock.Top);
        mine.Children.Add(_mineHint);
        mine.Children.Add(_mine);
        _minePane.Children.Add(mine);

        // ----- sources: ways of browsing on the left, the stations found on the right -----
        _sourcesPane.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        _sourcesPane.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new DockPanel();
        var leftCap = Make.Styled("BROWSE", "Cap");
        DockPanel.SetDock(leftCap, Dock.Top);
        left.Children.Add(leftCap);
        DockPanel.SetDock(_ways, Dock.Top);
        _ways.Margin = new Thickness(8, 0, 0, 6);
        left.Children.Add(_ways);
        DockPanel.SetDock(_groupNote, Dock.Top);
        left.Children.Add(_groupNote);
        left.Children.Add(_groups);
        _sourcesPane.Children.Add(left);

        var right = new DockPanel { Margin = new Thickness(18, 0, 0, 0) };
        var bar = new DockPanel { Margin = new Thickness(0, -4, 28, 4) };
        var go = Make.Button("  search", "TextBtn", () => _ = FindAsync(_search.Text), null, new Thickness(12, 0, 0, 0));
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
        Grid.SetColumn(right, 1);
        _sourcesPane.Children.Add(right);

        var root = new DockPanel { Margin = new Thickness(20, 0, 0, 0) };
        DockPanel.SetDock(_tabs, Dock.Top);
        root.Children.Add(_tabs);
        var panes = new Grid();
        panes.Children.Add(_minePane);
        panes.Children.Add(_sourcesPane);
        root.Children.Add(panes);
        Content = root;

        _search.ToolTip = "A station's name (what you type is sent to radio-browser.info)";
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) _ = FindAsync(_search.Text); };
        _mine.MouseDoubleClick += (_, e) => { if (StationAt(e) is { } s) Radio.Play(s); };
        _found.MouseDoubleClick += (_, e) => { if (StationAt(e) is { } s) Radio.Play(s); };
        _mine.MouseRightButtonUp += (_, e) => { if (StationAt(e) is { } s) Menus.Open(MenuFor(s, saved: true), _mine); };
        _found.MouseRightButtonUp += (_, e) => { if (StationAt(e) is { } s) Menus.Open(MenuFor(s, saved: false), _found); };
        _groups.SelectionChanged += (_, _) => { if ((_groups.SelectedItem as ListBoxItem)?.Tag is Radio.Group g) _ = OpenGroupAsync(g); };
        App.Lib.Changed += () => { if (IsVisible) ShowMine(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) ShowTab(_tab); };
        ShowTab("mine");
    }

    // ---------- the two tabs ----------

    public void ShowTab(string tab)
    {
        _tab = tab == "sources" ? "sources" : "mine";
        _tabs.Children.Clear();
        foreach (var (id, label) in new[] { ("mine", "my stations"), ("sources", "sources") })
        {
            var b = Make.Button(label, "TextBtn", () => ShowTab(id), id == "sources" ? "Browse the station directory and add stations" : null, new Thickness(0, 0, 22, 0));
            b.Opacity = id == _tab ? 1 : 0.5;
            b.FontSize = 15;
            _tabs.Children.Add(b);
        }
        _minePane.Visibility = _tab == "mine" ? Visibility.Visible : Visibility.Collapsed;
        _sourcesPane.Visibility = _tab == "sources" ? Visibility.Visible : Visibility.Collapsed;
        ShowMine();
        if (_tab == "sources") { ShowWays(); if (_results.Count == 0 && _asked == 0 && IsVisible && !App.SmokeMode) _ = BrowseAsync("popular"); }
    }

    void ShowWays()
    {
        _ways.Children.Clear();
        foreach (var (id, label) in new[] { ("popular", "popular"), ("genres", "genres"), ("countries", "countries") })
        {
            var b = Make.Button(label, "TextBtn", () => _ = BrowseAsync(id), null, new Thickness(0, 0, 18, 0));
            b.Opacity = id == _way ? 1 : 0.5;
            _ways.Children.Add(b);
        }
    }

    /// <summary>Chooses a way of browsing: the most listened-to stations, or the list of genres or countries to pick from.</summary>
    public async Task BrowseAsync(string way)
    {
        _way = way;
        ShowWays();
        _groups.Items.Clear();
        if (way == "popular")
        {
            _groupNote.Text = "The stations people tune in to most, from the Radio Browser directory (radio-browser.info). Choose genres or countries above to browse, or search by name on the right.";
            await ShowStationsAsync("POPULAR", Radio.PopularAsync);
            return;
        }
        _groupNote.Text = "asking radio-browser.info…";
        try
        {
            var groups = way == "genres" ? _genres ??= await Radio.GenresAsync() : _countries ??= await Radio.CountriesAsync();
            if (_way != way) return;
            foreach (var g in groups)
            {
                var row = new DockPanel { Height = 30 };
                var count = Make.Text(g.CountText, 11.5, "FgFaint");
                count.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(count, Dock.Right);
                row.Children.Add(count);
                var name = Make.Text(g.Name, 14);
                name.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(name);
                _groups.Items.Add(new ListBoxItem { Content = row, Tag = g, Padding = new Thickness(8, 0, 10, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
            _groupNote.Text = groups.Count == 0 ? "The directory sent nothing back." : way == "genres" ? "Choose a genre to see its stations." : "Choose a country to see its stations.";
        }
        catch (Exception ex) { _groupNote.Text = "The station directory can't be reached: " + ex.Message; }
    }

    Task OpenGroupAsync(Radio.Group g) =>
        ShowStationsAsync(g.Name.ToUpperInvariant(), () => _way == "countries" ? Radio.ByCountryAsync(g.Key) : Radio.ByGenreAsync(g.Key));

    async Task ShowStationsAsync(string title, Func<Task<List<Station>>> ask)
    {
        var mine = ++_asked;
        _foundCap.Text = title;
        _note.Text = "asking radio-browser.info…";
        try
        {
            var stations = await ask();
            if (mine != _asked) return;   // something else was chosen meanwhile
            Show(stations, title);
        }
        catch (Exception ex) { if (mine == _asked) _note.Text = "The station directory can't be reached: " + ex.Message; }
    }

    /// <summary>Puts stations in the list on the right (also used by the self-test, which has no network to ask).</summary>
    public void Show(List<Station> stations, string title)
    {
        _results = stations;
        _foundCap.Text = title;
        Fill(_found, _results, saved: false);
        _note.Text = _results.Count == 0 ? "No stations found." : $"{_results.Count} stations. Double-click one to listen; the + adds it to your stations.";
    }

    /// <summary>Searches the directory by name (null = the most listened-to stations).</summary>
    public async Task FindAsync(string? text)
    {
        if (text != null && text.Trim() == "") return;
        if (_tab != "sources") ShowTab("sources");
        if (text == null) { await BrowseAsync("popular"); return; }
        await ShowStationsAsync($"“{text.Trim().ToUpperInvariant()}”", () => Radio.SearchAsync(text));
    }

    // ---------- rows, saving and menus ----------

    static Station? StationAt(MouseEventArgs e) => (e.OriginalSource as FrameworkElement)?.DataContext as Station;

    FrameworkElement RowFor(Station s, bool saved)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        text.Children.Add(Make.Text(s.Name, 14));
        if (s.Subtitle != "") text.Children.Add(Make.Text(s.Subtitle, 11.5, "FgFaint"));
        var row = new DockPanel { Height = 46, Background = System.Windows.Media.Brushes.Transparent, DataContext = s };
        var cover = Make.Cover(s.Art, 36, "");
        DockPanel.SetDock(cover, Dock.Left);
        row.Children.Add(cover);
        if (!saved)
        {
            var known = App.Lib.Stations.Any(x => x.Url == s.Url);
            var save = Make.Button(known ? "" : "", "IconBtn", () => Save(s), known ? "In my stations" : "Add to my stations");
            save.Width = save.Height = 30; save.IsEnabled = !known;
            save.Margin = new Thickness(0, 0, 22, 0);
            DockPanel.SetDock(save, Dock.Right);
            row.Children.Add(save);
        }
        row.Children.Add(text);
        return row;
    }

    /// <summary>Adds a station from the directory to "my stations".</summary>
    public void Save(Station s)
    {
        App.Lib.SaveStation(s);
        App.Win.SetStatus($"{s.Name} added to my stations");
        ShowMine();
    }

    void Fill(ListBox list, IEnumerable<Station> stations, bool saved)
    {
        list.Items.Clear();
        foreach (var s in stations) list.Items.Add(new ListBoxItem { Content = RowFor(s, saved), DataContext = s, Padding = new Thickness(6, 0, 6, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch });
    }

    void ShowMine()
    {
        Fill(_mine, App.Lib.Stations, saved: true);
        _mineHint.Visibility = App.Lib.Stations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_results.Count > 0) Fill(_found, _results, saved: false);   // the + marks follow what is saved
    }

    void AddByAddress()
    {
        var url = InputDialog.Ask("Add a station", "Stream address (http://… or https://…)", "");
        if (string.IsNullOrWhiteSpace(url)) return;
        var name = InputDialog.Ask("Add a station", "Name", "") ?? "";
        if (Radio.FromAddress(name, url) is not { } station) { App.Win.SetStatus("that is not a web address"); return; }
        App.Lib.SaveStation(station);
        ShowMine();
    }

    ContextMenu MenuFor(Station s, bool saved)
    {
        var m = new ContextMenu();
        m.Items.Add(Menus.Item("Listen", () => Radio.Play(s)));
        if (!saved) m.Items.Add(Menus.Item("Add to my stations", () => Save(s), App.Lib.Stations.All(x => x.Url != s.Url)));
        else
        {
            m.Items.Add(Menus.Item(App.Lib.IsPinned("radio", s.Url) ? "Unpin from quickplay" : "Pin to quickplay", () => App.Lib.TogglePin("radio", s.Url, s.Name)));
            m.Items.Add(Menus.Item("Rename…", () =>
            {
                var name = InputDialog.Ask("Rename station", "Name", s.Name);
                if (string.IsNullOrWhiteSpace(name)) return;
                s.Name = name.Trim();
                App.Lib.SaveStation(s);
            }));
            m.Items.Add(new Separator());
            m.Items.Add(Menus.Item("Remove", () => App.Lib.RemoveStation(s)));
        }
        m.Items.Add(Menus.Item("Copy stream address", () => { try { Clipboard.SetText(s.Url); } catch { } }));
        return m;
    }
}
