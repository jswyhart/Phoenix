using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.UI;

/// <summary>Internet radio: your saved stations on the left, the station directory on the right.</summary>
public class RadioView : UserControl
{
    readonly ListBox _mine = new(), _found = new();
    readonly TextBox _search = new() { MinWidth = 260, VerticalContentAlignment = VerticalAlignment.Center };
    readonly TextBlock _note = Make.Text("", 12, "FgDim", new Thickness(8, 6, 0, 6), wrap: true);
    readonly TextBlock _mineHint = Make.Text("Stations you save appear here. Find one on the right, or add a stream's address yourself.", 13, "FgFaint", new Thickness(8, 10, 12, 0), wrap: true);
    List<Station> _results = new();

    public List<Station> Results => _results;
    public int SavedShown => _mine.Items.Count;

    public RadioView()
    {
        var root = new Grid { Margin = new Thickness(20, 8, 0, 0) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new DockPanel();
        var leftHead = new DockPanel();
        var add = Make.Button("  add by address", "TextBtn", AddByAddress, "Add a station by its stream address", new Thickness(0, -4, 4, 6));
        DockPanel.SetDock(add, Dock.Right);
        leftHead.Children.Add(add);
        leftHead.Children.Add(Make.Styled("MY STATIONS", "Cap"));
        DockPanel.SetDock(leftHead, Dock.Top);
        left.Children.Add(leftHead);
        DockPanel.SetDock(_mineHint, Dock.Top);
        left.Children.Add(_mineHint);
        left.Children.Add(_mine);
        root.Children.Add(left);

        var right = new DockPanel { Margin = new Thickness(18, 0, 0, 0) };
        var bar = new DockPanel { Margin = new Thickness(0, -4, 28, 4) };
        var popular = Make.Button("popular", "TextBtn", () => _ = FindAsync(null), "The stations people tune in to most", new Thickness(14, 0, 0, 0));
        var go = Make.Button("  search", "TextBtn", () => _ = FindAsync(_search.Text), null, new Thickness(12, 0, 0, 0));
        DockPanel.SetDock(popular, Dock.Right);
        DockPanel.SetDock(go, Dock.Right);
        var cap = Make.Styled("FIND STATIONS", "Cap");
        cap.Margin = new Thickness(cap.Margin.Left, cap.Margin.Top, 16, cap.Margin.Bottom);
        DockPanel.SetDock(cap, Dock.Left);
        bar.Children.Add(cap);
        bar.Children.Add(popular);
        bar.Children.Add(go);
        bar.Children.Add(_search);
        DockPanel.SetDock(bar, Dock.Top);
        right.Children.Add(bar);
        DockPanel.SetDock(_note, Dock.Top);
        right.Children.Add(_note);
        right.Children.Add(_found);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        Content = root;

        _search.ToolTip = "A station's name (the search text is sent to radio-browser.info)";
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) _ = FindAsync(_search.Text); };
        _mine.MouseDoubleClick += (_, e) => { if (StationAt(e) is { } s) Radio.Play(s); };
        _found.MouseDoubleClick += (_, e) => { if (StationAt(e) is { } s) Radio.Play(s); };
        _mine.MouseRightButtonUp += (_, e) => { if (StationAt(e) is { } s) Menus.Open(MenuFor(s, saved: true), _mine); };
        _found.MouseRightButtonUp += (_, e) => { if (StationAt(e) is { } s) Menus.Open(MenuFor(s, saved: false), _found); };
        App.Lib.Changed += () => { if (IsVisible) ShowMine(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) ShowMine(); };
        _note.Text = "Type a station's name and press Enter, or choose “popular”. Double-click a station to listen; right-click to save it.";
    }

    static Station? StationAt(MouseEventArgs e) => (e.OriginalSource as FrameworkElement)?.DataContext as Station;

    static FrameworkElement RowFor(Station s, bool saved)
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
            var save = Make.Button(known ? "" : "", "IconBtn", () => { App.Lib.SaveStation(s); App.Win.SetStatus($"saved {s.Name}"); }, known ? "In my stations" : "Save to my stations");
            save.Width = save.Height = 30; save.IsEnabled = !known;
            DockPanel.SetDock(save, Dock.Right);
            row.Children.Add(save);
        }
        row.Children.Add(text);
        return row;
    }

    void Fill(ListBox list, IEnumerable<Station> stations, bool saved)
    {
        list.Items.Clear();
        foreach (var s in stations) list.Items.Add(new ListBoxItem { Content = RowFor(s, saved), DataContext = s, Padding = new Thickness(6, 0, 6, 0) });
    }

    void ShowMine()
    {
        Fill(_mine, App.Lib.Stations, saved: true);
        _mineHint.Visibility = App.Lib.Stations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_results.Count > 0) Fill(_found, _results, saved: false);
    }

    /// <summary>Searches the directory (null = the most listened-to stations).</summary>
    public async Task FindAsync(string? text)
    {
        if (text != null && text.Trim() == "") return;
        _note.Text = "asking radio-browser.info…";
        try
        {
            _results = text == null ? await Radio.PopularAsync() : await Radio.SearchAsync(text);
            Fill(_found, _results, saved: false);
            _note.Text = _results.Count == 0 ? "No stations found by that name." : $"{_results.Count} stations. Double-click to listen; the + saves one to your stations.";
        }
        catch (Exception ex) { _note.Text = "The station directory can't be reached: " + ex.Message; }
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
        if (!saved) m.Items.Add(Menus.Item("Save to my stations", () => App.Lib.SaveStation(s), App.Lib.Stations.All(x => x.Url != s.Url)));
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
