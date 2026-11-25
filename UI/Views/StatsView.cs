using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.UI;

/// <summary>"Your listening": hours, plays and top artists, albums and songs for a year, a month, or all time.</summary>
public class StatsView : UserControl
{
    readonly StackPanel _periods = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 0, 0) };
    readonly StackPanel _body = new() { Margin = new Thickness(6, 14, 28, 30) };
    string _period = "year";

    public ListenStats? Shown { get; private set; }

    public StatsView()
    {
        var head = new StackPanel { Margin = new Thickness(22, 8, 0, 0) };
        head.Children.Add(Make.Styled("listening", "ZuneHead"));
        head.Children.Add(_periods);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Content = _body, Margin = new Thickness(22, 0, 0, 0) };
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(scroll);
        Content = dock;
        IsVisibleChanged += (_, _) => { if (IsVisible) Reload(); };
    }

    static (long From, long To, string Title, bool ByMonth) Range(string period)
    {
        var now = DateTime.Now;
        long Unix(DateTime d) => new DateTimeOffset(d).ToUnixTimeSeconds();
        return period switch
        {
            "month" => (Unix(now.Date.AddDays(-29)), Fmt.Now + 1, "the last 30 days", false),
            "lastyear" => (Unix(new DateTime(now.Year - 1, 1, 1)), Unix(new DateTime(now.Year, 1, 1)), (now.Year - 1).ToString(), true),
            _ => (Unix(new DateTime(now.Year, 1, 1)), Fmt.Now + 1, now.Year.ToString(), true)
        };
    }

    public void Show(string period) { _period = period; Reload(); }

    void Reload()
    {
        _periods.Children.Clear();
        foreach (var (id, label) in new[] { ("year", DateTime.Now.Year.ToString()), ("lastyear", (DateTime.Now.Year - 1).ToString()), ("month", "last 30 days"), ("all", "all time") })
        {
            var b = Make.Button(label, "TextBtn", () => Show(id), margin: new Thickness(0, 0, 22, 0));
            b.Opacity = id == _period ? 1 : 0.5;
            b.FontSize = 15;
            _periods.Children.Add(b);
        }
        ListenStats s;
        if (_period == "all") s = ListenStats.AllTime();
        else { var r = Range(_period); s = ListenStats.FromLog(r.Title, r.From, r.To, r.ByMonth); }
        Shown = s;
        _body.Children.Clear();

        if (s.Plays == 0 && s.Ms == 0)
        {
            _body.Children.Add(Make.Text($"Nothing listened to in {s.Title} yet.", 20, "FgDim", new Thickness(0, 10, 0, 6)));
            if (s.Note != "") _body.Children.Add(Make.Text(s.Note, 13, "FgFaint", wrap: true));
            return;
        }

        // the headline numbers
        var numbers = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        void Number(string value, string label)
        {
            var big = new TextBlock { Text = value, FontSize = 46 };
            big.SetResourceReference(TextBlock.FontFamilyProperty, "HeadFont");
            numbers.Children.Add(Make.Column(big, Make.Text(label, 12, "FgDim")));
            ((FrameworkElement)numbers.Children[^1]).Margin = new Thickness(0, 0, 44, 0);
        }
        var time = TimeSpan.FromMilliseconds(s.Ms);
        Number(time.TotalHours >= 1 ? $"{time.TotalHours:0.#}" : $"{(int)time.TotalMinutes}", time.TotalHours >= 1 ? "hours listened" : "minutes listened");
        Number(s.Plays.ToString("N0"), s.Plays == 1 ? "play" : "plays");
        Number(s.Artists.ToString("N0"), s.Artists == 1 ? "artist" : "artists");
        Number(s.Albums.ToString("N0"), s.Albums == 1 ? "album" : "albums");
        Number(s.Songs.ToString("N0"), s.Songs == 1 ? "song" : "songs");
        _body.Children.Add(numbers);

        // listening over the period, as bars
        if (s.Over.Count > 1 && s.Over.Any(o => o.Ms > 0))
        {
            var max = Math.Max(1, s.Over.Max(o => o.Ms));
            var chart = new StackPanel { Orientation = Orientation.Horizontal, Height = 130, Margin = new Thickness(0, 0, 0, 22) };
            var width = s.Over.Count > 14 ? 16 : 40;
            foreach (var (label, ms) in s.Over)
            {
                var bar = new Border { Width = width - 4, Height = Math.Max(ms > 0 ? 2 : 0, 96.0 * ms / max), VerticalAlignment = VerticalAlignment.Bottom, ToolTip = ms > 0 ? ListenStats.Hours(ms) : null };
                bar.SetResourceReference(Border.BackgroundProperty, "Accent");
                var column = new DockPanel { Width = width, LastChildFill = true };
                var text = Make.Text(label, 10, "FgFaint");
                text.HorizontalAlignment = HorizontalAlignment.Center;
                DockPanel.SetDock(text, Dock.Bottom);
                column.Children.Add(text);
                column.Children.Add(bar);
                chart.Children.Add(column);
            }
            _body.Children.Add(chart);
        }

        var columns = new Grid();
        for (var i = 0; i < 3; i++) columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void List(int column, string title, List<ListenStats.Top> rows, string glyph, bool byPlays)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 26, 0) };
            panel.Children.Add(Make.Styled(title, "Cap", new Thickness(0, 0, 0, 8)));
            double most = Math.Max(1, rows.Count == 0 ? 1 : byPlays ? rows.Max(r => r.Plays) : rows.Max(r => r.Ms));
            var rank = 0;
            foreach (var row in rows)
            {
                rank++;
                var item = row;
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                var name = new TextBlock { FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
                Tr.SetText(name, row.Name);
                text.Children.Add(name);
                var detail = string.Join("  ·  ", new[] { row.Detail, ListenStats.Hours(row.Ms), row.Plays > 0 ? $"{row.Plays} play{(row.Plays == 1 ? "" : "s")}" : "" }.Where(x => x != ""));
                text.Children.Add(Make.Text(detail, 11.5, "FgDim"));
                text.Children.Add(new Border { Height = 2, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(3, 180 * (byPlays ? row.Plays : row.Ms) / most), Background = (Brush)FindResource("AccentGrad") });
                var line = new DockPanel { Height = 54, Background = Brushes.Transparent };
                var number = Make.Text(rank.ToString(), 13, "FgFaint");
                number.Width = 22; number.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(number, Dock.Left);
                line.Children.Add(number);
                var cover = Make.Cover(row.Art, 44, glyph);
                DockPanel.SetDock(cover, Dock.Left);
                line.Children.Add(cover);
                line.Children.Add(text);
                var button = new Button { Style = Make.Style("BareBtn"), Content = line, HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = "Play" };
                button.Click += (_, _) => Activate(item);
                button.MouseRightButtonUp += (_, _) => Menu(item, button);
                panel.Children.Add(button);
            }
            Grid.SetColumn(panel, column);
            columns.Children.Add(panel);
        }
        List(0, "TOP ARTISTS", s.TopArtists, "", false);
        List(1, "TOP ALBUMS", s.TopAlbums, "", false);
        List(2, "TOP SONGS", s.TopSongs, "", true);
        _body.Children.Add(columns);
        if (s.Note != "") _body.Children.Add(Make.Text(s.Note, 12, "FgFaint", new Thickness(0, 22, 0, 0), wrap: true));
    }

    static List<Track> ByArtist(string artist) => App.Lib.Tracks.Where(t => string.Equals(t.Artist, artist, StringComparison.OrdinalIgnoreCase) || string.Equals(t.AlbumArtist, artist, StringComparison.OrdinalIgnoreCase))
        .OrderBy(t => t.Album).ThenBy(t => t.DiscNo).ThenBy(t => t.TrackNo).ToList();

    static void Activate(ListenStats.Top row)
    {
        switch (row.Item)
        {
            case Track t: Menus.Play(new[] { t }); break;
            case Album a: App.Win.Navigate("music"); App.Win.Page<MusicView>("music").OpenAlbum(a); break;
            case string artist when ByArtist(artist) is { Count: > 0 } tracks: Menus.PlayShuffled(tracks); break;
        }
    }

    static void Menu(ListenStats.Top row, UIElement target)
    {
        var menu = row.Item switch
        {
            Track t => Menus.ForTracks(new List<Track> { t }),
            Album a => Menus.ForAlbum(a),
            string artist when ByArtist(artist).Count > 0 => Menus.ForArtist(artist),
            _ => null
        };
        if (menu != null) Menus.Open(menu, target);
    }
}
