using System.Windows;
using System.Windows.Controls;
using Halcyon.Core;

namespace Halcyon.UI;

/// <summary>
/// Edits what a video is and where it is grouped (movie, episode of a series, or a plain video in a folder group),
/// for one video or many. With several selected, fields left blank keep their current values.
/// </summary>
public class EditVideoDialog : Window
{
    readonly List<VideoItem> _videos;
    readonly bool _single;
    readonly TextBox _title = new(), _series = new(), _season = new() { Width = 90, HorizontalAlignment = HorizontalAlignment.Left },
        _episode = new() { Width = 90, HorizontalAlignment = HorizontalAlignment.Left }, _year = new() { Width = 90, HorizontalAlignment = HorizontalAlignment.Left }, _collection = new();
    readonly ComboBox _kind = new() { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
    static readonly (string key, string label)[] Kinds = { ("movie", "Movie"), ("episode", "TV episode"), ("video", "Other video") };

    public EditVideoDialog(List<VideoItem> videos)
    {
        _videos = videos;
        _single = videos.Count == 1;
        var f = videos[0];
        Owner = App.Win;
        Title = "Edit video info";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");

        string Shared(Func<VideoItem, string> get) => videos.All(v => get(v) == get(f)) ? get(f) : "";
        static string Num(int n) => n > 0 ? n.ToString() : "";

        foreach (var k in Kinds) _kind.Items.Add(k.label);
        var kind = Shared(v => v.Kind == "" ? "video" : v.Kind);
        _kind.SelectedIndex = Array.FindIndex(Kinds, k => k.key == kind);
        _title.Text = _single ? f.Title : "";
        _title.IsEnabled = _episode.IsEnabled = _single;
        _series.Text = Shared(v => v.Series);
        _season.Text = Shared(v => Num(v.Season));
        _episode.Text = _single ? Num(f.Episode) : "";
        _year.Text = Shared(v => Num(v.Year));
        _collection.Text = Shared(v => v.Collection);

        var panel = new StackPanel { Margin = new Thickness(22) };
        var heading = new TextBlock { Text = _single ? "edit video info" : $"edit {videos.Count} videos", Style = (Style)FindResource("H2") };
        panel.Children.Add(heading);
        var hint = _single ? "" : "Changes apply to every selected video. Fields left blank keep their current values. ";
        if (f.Source != "local") hint += $"These come from {f.Source}: your changes are kept in Phoenix and survive the next sync; the server itself is not changed.";
        if (hint != "") panel.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Foreground = (System.Windows.Media.Brush)FindResource("FgDim") });

        void Row(string label, FrameworkElement input)
        {
            var row = new Grid { Height = 36 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = (System.Windows.Media.Brush)FindResource("FgDim") };
            input.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(input, 1);
            row.Children.Add(text);
            row.Children.Add(input);
            panel.Children.Add(row);
        }
        Row("Title", _title);
        Row("Type", _kind);
        Row("Series / folder group", _series);
        Row("Season", _season);
        Row("Episode number", _episode);
        Row("Year", _year);
        Row("Collection", _collection);

        var save = new Button { Content = "save", IsDefault = true, Style = (Style)FindResource("PillBtn"), MinWidth = 80 };
        var cancel = new Button { Content = "cancel", IsCancel = true, Style = (Style)FindResource("PillBtn"), MinWidth = 80, Margin = new Thickness(0) };
        save.Click += (_, _) => Save();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }

    void Save()
    {
        foreach (var v in _videos)
        {
            if (_kind.SelectedIndex >= 0) v.Kind = Kinds[_kind.SelectedIndex].key;
            if (_single)
            {
                if (_title.Text.Trim() != "") v.Title = _title.Text.Trim();
                v.Episode = int.TryParse(_episode.Text, out var e) ? e : 0;
            }
            void Text(TextBox box, Action<string> set) { if (_single || box.Text.Trim() != "") set(box.Text.Trim()); }
            Text(_series, s => v.Series = s);
            Text(_season, s => v.Season = int.TryParse(s, out var n) ? n : 0);
            Text(_year, s => v.Year = int.TryParse(s, out var n) ? n : 0);
            Text(_collection, s => v.Collection = s);
        }
        App.Lib.SaveVideos(_videos);
        DialogResult = true;
    }
}
