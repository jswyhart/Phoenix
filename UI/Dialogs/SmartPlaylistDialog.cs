using System.Windows;
using System.Windows.Controls;
using Halcyon.Core;

namespace Halcyon.UI;

/// <summary>Edits the rules of an auto playlist, showing how many songs match as the rules change.</summary>
public class SmartPlaylistDialog : Window
{
    readonly SmartPlaylist _p;
    readonly TextBox _name = new(), _genre = new(), _artist = new(), _from = Small(), _to = Small(), _notPlayed = Small(), _added = Small(), _min = Small(), _max = Small(), _limit = Small();
    readonly ComboBox _rating = new() { Width = 200, HorizontalAlignment = HorizontalAlignment.Left }, _sort = new() { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock _count = new() { Margin = new Thickness(0, 14, 0, 0) };

    static TextBox Small() => new() { Width = 70, HorizontalAlignment = HorizontalAlignment.Left };

    SmartPlaylistDialog(SmartPlaylist p, bool isNew)
    {
        _p = p;
        Title = isNew ? "New auto playlist" : "Auto playlist rules";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");
        _count.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");

        var grid = new Grid { Margin = new Thickness(20) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Row(string label, FrameworkElement field, string? hint = null)
        {
            var r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 5, 10, 5) };
            Grid.SetRow(l, r);
            grid.Children.Add(l);
            FrameworkElement content = field;
            if (hint != null)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(field);
                var h = new TextBlock { Text = hint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontSize = 12 };
                h.SetResourceReference(TextBlock.ForegroundProperty, "FgFaint");
                line.Children.Add(h);
                content = line;
            }
            content.Margin = new Thickness(0, 5, 0, 5);
            Grid.SetRow(content, r);
            Grid.SetColumn(content, 1);
            grid.Children.Add(content);
        }
        var years = new StackPanel { Orientation = Orientation.Horizontal };
        years.Children.Add(_from);
        years.Children.Add(new TextBlock { Text = "to", Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        years.Children.Add(_to);

        Row("Name", _name);
        Row("Genre contains", _genre, null);
        Row("Artist contains", _artist, null);
        Row("Years", years);
        Row("Rating", _rating);
        Row("Not played in", _notPlayed, "days (blank: no rule)");
        Row("Added in the last", _added, "days (blank: no rule)");
        Row("Played at least", _min, "times");
        Row("Played at most", _max, "times (blank: no limit, 0: never played)");
        Row("Order", _sort);
        Row("Keep the first", _limit, "songs (blank: all of them)");

        _rating.ItemsSource = SmartPlaylist.Ratings;
        _sort.ItemsSource = SmartPlaylist.Sorts;
        _name.Text = p.Name;
        _genre.Text = p.Genre;
        _artist.Text = p.Artist;
        _from.Text = Num(p.YearFrom);
        _to.Text = Num(p.YearTo);
        _rating.SelectedIndex = Math.Clamp(p.Rating, 0, SmartPlaylist.Ratings.Length - 1);
        _notPlayed.Text = Num(p.NotPlayedDays);
        _added.Text = Num(p.AddedDays);
        _min.Text = Num(p.MinPlays);
        _max.Text = p.MaxPlays < 0 ? "" : p.MaxPlays.ToString();
        _sort.SelectedIndex = Math.Clamp(p.Sort, 0, SmartPlaylist.Sorts.Length - 1);
        _limit.Text = Num(p.Limit);

        var ok = new Button { Content = "ok", IsDefault = true, Style = (Style)FindResource("PillBtn"), MinWidth = 80 };
        var cancel = new Button { Content = "cancel", IsCancel = true, Style = (Style)FindResource("PillBtn"), MinWidth = 80, Margin = new Thickness(0) };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text)) { _name.Focus(); return; }
            Read(_p);
            DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var hintText = new TextBlock { Text = "Several genres or artists can be separated with commas. An auto playlist fills itself: it changes as your collection, ratings and listening do.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) };
        hintText.SetResourceReference(TextBlock.ForegroundProperty, "FgFaint");
        var panel = new StackPanel();
        panel.Children.Add(grid);
        var foot = new StackPanel { Margin = new Thickness(20, 0, 20, 20) };
        foot.Children.Add(hintText);
        foot.Children.Add(_count);
        foot.Children.Add(buttons);
        panel.Children.Add(foot);
        Content = panel;

        foreach (var box in new[] { _genre, _artist, _from, _to, _notPlayed, _added, _min, _max, _limit }) box.TextChanged += (_, _) => Count();
        _rating.SelectionChanged += (_, _) => Count();
        _sort.SelectionChanged += (_, _) => Count();
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); Count(); };
    }

    static string Num(int n) => n > 0 ? n.ToString() : "";
    static int Int(TextBox box, int blank = 0) => int.TryParse(box.Text.Trim(), out var n) && n >= 0 ? n : blank;

    void Read(SmartPlaylist p)
    {
        p.Name = _name.Text.Trim();
        p.Genre = _genre.Text.Trim();
        p.Artist = _artist.Text.Trim();
        p.YearFrom = Int(_from);
        p.YearTo = Int(_to);
        p.Rating = Math.Max(0, _rating.SelectedIndex);
        p.NotPlayedDays = Int(_notPlayed);
        p.AddedDays = Int(_added);
        p.MinPlays = Int(_min);
        p.MaxPlays = Int(_max, -1);
        p.Sort = Math.Max(0, _sort.SelectedIndex);
        p.Limit = Int(_limit);
    }

    void Count()
    {
        var probe = new SmartPlaylist { Id = _p.Id };
        Read(probe);
        var shown = App.Lib.Evaluate(probe).Count;
        probe.Limit = 0;
        var all = App.Lib.Evaluate(probe).Count;
        _count.Text = all == shown ? (all == 1 ? "1 song matches" : $"{all} songs match") : $"{all} songs match; the playlist keeps {shown}";
    }

    /// <summary>Shows the dialog; true if the rules were accepted (the playlist object is then updated).</summary>
    public static bool Edit(SmartPlaylist p, bool isNew) => new SmartPlaylistDialog(p, isNew) { Owner = App.Win }.ShowDialog() == true;
}
