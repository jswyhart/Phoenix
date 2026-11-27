using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Halcyon.Core;

namespace Halcyon.UI;

/// <summary>One song in the tag editor: a working copy, so nothing changes until Save.</summary>
public class TagRow : Observable
{
    public Track Track { get; }
    string _title, _artist, _albumArtist, _album, _genre;
    int _year, _trackNo, _discNo;

    public TagRow(Track t)
    {
        Track = t;
        _title = t.Title; _artist = t.Artist; _albumArtist = t.AlbumArtist; _album = t.Album; _genre = t.Genre;
        _year = t.Year; _trackNo = t.TrackNo; _discNo = t.DiscNo;
    }

    public string Title { get => _title; set => Set(ref _title, value ?? ""); }
    public string Artist { get => _artist; set => Set(ref _artist, value ?? ""); }
    public string AlbumArtist { get => _albumArtist; set => Set(ref _albumArtist, value ?? ""); }
    public string Album { get => _album; set => Set(ref _album, value ?? ""); }
    public string Genre { get => _genre; set => Set(ref _genre, value ?? ""); }
    public int Year { get => _year; set => Set(ref _year, Math.Max(0, value)); }
    public int TrackNo { get => _trackNo; set => Set(ref _trackNo, Math.Max(0, value)); }
    public int DiscNo { get => _discNo; set => Set(ref _discNo, Math.Max(0, value)); }
    public string File => Track.IsLocal ? Path.GetFileName(Track.SourceId) : Track.Source;

    public bool Changed => Title != Track.Title || Artist != Track.Artist || AlbumArtist != Track.AlbumArtist || Album != Track.Album || Genre != Track.Genre
                           || Year != Track.Year || TrackNo != Track.TrackNo || DiscNo != Track.DiscNo;

    public void Apply()
    {
        Track.Title = Title.Trim() == "" ? Track.Title : Title.Trim();
        Track.Artist = Artist.Trim() == "" ? "Unknown Artist" : Artist.Trim();
        Track.AlbumArtist = AlbumArtist.Trim() == "" ? Track.Artist : AlbumArtist.Trim();
        Track.Album = Album.Trim() == "" ? "Unknown Album" : Album.Trim();
        Track.Genre = Genre.Trim();
        Track.Year = Year; Track.TrackNo = TrackNo; Track.DiscNo = DiscNo;
    }
}

/// <summary>The fixes the tag editor can apply to many songs at once.</summary>
public static class TagFixes
{
    static readonly HashSet<string> Small = new(StringComparer.OrdinalIgnoreCase) { "a", "an", "and", "as", "at", "but", "by", "for", "in", "nor", "of", "on", "or", "the", "to", "vs", "via", "with" };

    /// <summary>"the end of the road" becomes "The End of the Road"; words already written with capitals inside (McCartney, AC/DC) are left alone.</summary>
    public static string TitleCase(string s)
    {
        var words = s.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length == 0 || w.Skip(1).Any(char.IsUpper)) continue;
            var first = i == 0 || i == words.Length - 1 || words[i - 1].EndsWith(':') || words[i - 1].EndsWith('-');
            var core = w.Trim('(', ')', '[', ']', '"', '\'', ',', '.', '!', '?');
            if (!first && Small.Contains(core)) { words[i] = w.ToLowerInvariant(); continue; }
            var at = w.TakeWhile(c => !char.IsLetterOrDigit(c)).Count();
            if (at < w.Length) words[i] = w[..at] + char.ToUpper(w[at], CultureInfo.CurrentCulture) + w[(at + 1)..];
        }
        return string.Join(' ', words);
    }

    /// <summary>Underscores to spaces, runs of spaces to one, no spaces at the ends, and a leading track number ("03 - ") removed.</summary>
    public static string CleanUp(string s, bool stripNumber)
    {
        s = Regex.Replace(s.Replace('_', ' '), @"\s+", " ").Trim();
        if (stripNumber) s = Regex.Replace(s, @"^\d{1,3}\s*[-.)]\s+|^\d{2,3}\s+(?=\D)", "");
        return s.Trim();
    }

    public static readonly string[] Patterns = { "%n - %t", "%n. %t", "%n %t", "%a - %t", "%n - %a - %t", "%a - %b - %n - %t", "%t" };

    /// <summary>A pattern as it is shown to you: "%n - %t" reads "number - title".</summary>
    public static string InWords(string pattern) => "named:  " + pattern.Replace("%n", "number").Replace("%t", "title").Replace("%a", "artist").Replace("%b", "album");

    /// <summary>Reads fields out of a file name by a pattern: %n track number, %t title, %a artist, %b album. Null if the name does not fit.</summary>
    public static Dictionary<char, string>? FromFileName(string name, string pattern)
    {
        var keys = new List<char>();
        var regex = "^" + Regex.Replace(Regex.Escape(pattern), @"%([ntab])", m =>
        {
            keys.Add(m.Groups[1].Value[0]);
            return m.Groups[1].Value == "n" ? @"(\d{1,3})" : "(.+?)";
        }).Replace(@"\ ", @"\s*") + "$";
        var match = Regex.Match(name.Replace('_', ' ').Trim(), regex);
        if (!match.Success) return null;
        var found = new Dictionary<char, string>();
        for (var i = 0; i < keys.Count; i++) found[keys[i]] = match.Groups[i + 1].Value.Trim();
        return found;
    }
}

/// <summary>
/// The tag editor: every chosen song as a row you can type into, with fixes that work on many rows at once
/// (number the tracks, tidy the capitals, take names from the file names, find and replace, copy a value down).
/// </summary>
public class TagEditorWindow : Window
{
    readonly ObservableCollection<TagRow> _rows;
    readonly DataGrid _grid = new();
    readonly TextBlock _status = Make.Text("", 12, "FgDim");
    readonly ComboBox _pattern = new() { Width = 220, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    static readonly (string Header, string Path, double Width, bool Number)[] Columns =
    {
        ("#", "TrackNo", 44, true), ("Title", "Title", 250, false), ("Artist", "Artist", 170, false), ("Album artist", "AlbumArtist", 150, false),
        ("Album", "Album", 190, false), ("Genre", "Genre", 110, false), ("Year", "Year", 58, true), ("Disc", "DiscNo", 46, true)
    };

    public IReadOnlyList<TagRow> Rows => _rows;

    public TagEditorWindow(IEnumerable<Track> tracks)
    {
        _rows = new ObservableCollection<TagRow>(tracks.Select(t => new TagRow(t)));
        Title = $"File tags  ·  {_rows.Count} song{(_rows.Count == 1 ? "" : "s")}  ·  saved into the music files";
        Width = 1240; Height = 700; MinWidth = 900; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(FontFamilyProperty, "TextFont"); FontSize = 13; UseLayoutRounding = true;
        if (App.Win is { IsLoaded: true } owner) Owner = owner;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");

        _grid.AutoGenerateColumns = false;
        _grid.CanUserAddRows = _grid.CanUserDeleteRows = false;
        _grid.SelectionMode = DataGridSelectionMode.Extended;
        _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _grid.BorderThickness = new Thickness(0);
        _grid.RowHeight = 28;
        _grid.ItemsSource = _rows;
        _grid.SetResourceReference(BackgroundProperty, "Bg");
        _grid.SetResourceReference(ForegroundProperty, "Fg");
        _grid.SetResourceReference(DataGrid.RowBackgroundProperty, "Bg");
        _grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "Line");
        var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("BgAlt")));
        headerStyle.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("FgDim")));
        headerStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(6, 5, 6, 5)));
        headerStyle.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
        _grid.ColumnHeaderStyle = headerStyle;
        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
        cellStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(4, 0, 4, 0)));
        cellStyle.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("Fg")));
        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("Selected")));
        cellStyle.Triggers.Add(selected);
        _grid.CellStyle = cellStyle;
        var textStyle = new Style(typeof(TextBlock));
        textStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        textStyle.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(4, 0, 4, 0)));
        textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        foreach (var (header, path, width, number) in Columns)
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = header, Width = width, ElementStyle = textStyle,
                Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, TargetNullValue = "", Converter = number ? new BlankZero() : null }
            });
        _grid.Columns.Add(new DataGridTextColumn { Header = "File", Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true, ElementStyle = textStyle, Binding = new Binding("File") { Mode = BindingMode.OneWay } });
        _grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(ShowStatus);

        // shown in words ("number - title") rather than as the pattern itself ("%n - %t")
        _pattern.ItemsSource = TagFixes.Patterns.Select(TagFixes.InWords).ToList();
        _pattern.SelectedIndex = 0;
        _pattern.ToolTip = "How your files are named. Choose the layout that matches, then press FROM FILE NAMES to fill in the songs' details from their file names.";

        var fixes = new WrapPanel { Margin = new Thickness(16, 8, 16, 8) };
        void Fix(string label, Action run, string tip) => fixes.Children.Add(Make.Button(label, "PillBtn", () => { _grid.CommitEdit(DataGridEditingUnit.Row, true); run(); ShowStatus(); }, tip, new Thickness(0, 0, 8, 6)));
        Fix("NUMBER TRACKS", NumberTracks, "Numbers the songs 1, 2, 3… in the order shown");
        Fix("TIDY CAPITALS", () => Each(r => { r.Title = TagFixes.TitleCase(r.Title); r.Album = TagFixes.TitleCase(r.Album); }), "Title Case for titles and albums");
        Fix("CLEAN UP", () => Each(r =>
        {
            r.Title = TagFixes.CleanUp(r.Title, true); r.Artist = TagFixes.CleanUp(r.Artist, false); r.AlbumArtist = TagFixes.CleanUp(r.AlbumArtist, false);
            r.Album = TagFixes.CleanUp(r.Album, false); r.Genre = TagFixes.CleanUp(r.Genre, false);
        }), "Removes underscores, extra spaces and a track number at the start of a title");
        Fix("FIND & REPLACE…", FindReplace, "Replaces text in titles, artists, albums and genres");
        Fix("COPY DOWN", CopyDown, "Gives every chosen song the first chosen song's artist, album artist, album, genre, year and disc");
        Fix("ALBUM ARTIST = ARTIST", () => Each(r => r.AlbumArtist = r.Artist), "Sets each song's album artist to its artist");
        fixes.Children.Add(_pattern);
        Fix("FROM FILE NAMES", FromFileNames, "Reads the fields out of each file's name by the pattern on the left");
        Fix("MATCH ALBUM ONLINE…", MatchOnline, "Looks the album up (MusicBrainz and your other sources) and applies its names and cover");

        var save = Make.Button("SAVE", "PillBtn", Save, null, new Thickness(0, 0, 8, 0));
        save.MinWidth = 96; save.IsDefault = false;
        var cancel = Make.Button("CANCEL", "PillBtn", Close, null, new Thickness(0));
        cancel.MinWidth = 96; cancel.IsCancel = true;
        var bottom = new DockPanel { Margin = new Thickness(16, 10, 16, 14) };
        var buttons = Make.Row(save, cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        _status.VerticalAlignment = VerticalAlignment.Center;
        bottom.Children.Add(_status);

        var dock = new DockPanel();
        DockPanel.SetDock(fixes, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        var about = new TextBlock { Text = "File tags are saved into the music files themselves, so they go wherever the files go. (Search tags, Phoenix's own #labels, are kept in its library: right-click → Edit search tags.)",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 12, 16, 0) };
        about.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
        DockPanel.SetDock(about, Dock.Top);
        dock.Children.Add(about);
        dock.Children.Add(fixes);
        dock.Children.Add(bottom);
        dock.Children.Add(_grid);
        Content = dock;
        ShowStatus();
    }

    sealed class BlankZero : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => value is int n && n > 0 ? n.ToString() : "";
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => int.TryParse(value as string, out var n) ? n : 0;
    }

    /// <summary>The rows a fix applies to: the ones selected, or all of them when fewer than two are.</summary>
    List<TagRow> Chosen() => _grid.SelectedItems.Count > 1 ? _rows.Where(r => _grid.SelectedItems.Contains(r)).ToList() : _rows.ToList();

    void Each(Action<TagRow> fix) { foreach (var r in Chosen()) fix(r); }

    void ShowStatus()
    {
        var changed = _rows.Count(r => r.Changed);
        var servers = _rows.Count(r => !r.Track.IsLocal);
        _status.Text = (changed == 0 ? "No changes yet." : $"{changed} song{(changed == 1 ? "" : "s")} changed.")
            + "  Fixes apply to the selected rows, or to every row when fewer than two are selected."
            + (App.Settings.WriteTagsToFiles ? "" : "  Changes are kept in Phoenix only (writing to files is off in settings → metadata).")
            + (servers > 0 ? $"  {servers} come from a server: those changes stay in Phoenix." : "");
    }

    public void NumberTracks()
    {
        var n = 0;
        foreach (var r in Chosen()) r.TrackNo = ++n;
    }

    void FindReplace()
    {
        var find = InputDialog.Ask("Find and replace", "Find (in titles, artists, albums and genres)", "");
        if (string.IsNullOrEmpty(find)) return;
        var with = InputDialog.Ask("Find and replace", $"Replace “{find}” with", "");
        if (with == null) return;
        Replace(find, with);
    }

    public int Replace(string find, string with)
    {
        var n = 0;
        string Swap(string s) { var r = s.Replace(find, with, StringComparison.OrdinalIgnoreCase); if (r != s) n++; return r; }
        Each(r => { r.Title = Swap(r.Title); r.Artist = Swap(r.Artist); r.AlbumArtist = Swap(r.AlbumArtist); r.Album = Swap(r.Album); r.Genre = Swap(r.Genre); });
        return n;
    }

    void CopyDown()
    {
        var rows = Chosen();
        if (rows.Count < 2) return;
        var first = rows[0];
        foreach (var r in rows.Skip(1)) { r.Artist = first.Artist; r.AlbumArtist = first.AlbumArtist; r.Album = first.Album; r.Genre = first.Genre; r.Year = first.Year; r.DiscNo = first.DiscNo; }
    }

    void FromFileNames()
    {
        var (done, skipped) = FromFileNames(TagFixes.Patterns[Math.Max(0, _pattern.SelectedIndex)]);
        if (skipped > 0) App.Win.SetStatus($"{done} read from their file names; {skipped} did not fit the pattern");
    }

    public (int Done, int Skipped) FromFileNames(string pattern)
    {
        int done = 0, skipped = 0;
        foreach (var r in Chosen())
        {
            if (!r.Track.IsLocal) { skipped++; continue; }
            var found = TagFixes.FromFileName(Path.GetFileNameWithoutExtension(r.Track.SourceId), pattern);
            if (found == null) { skipped++; continue; }
            if (found.TryGetValue('n', out var n) && int.TryParse(n, out var number)) r.TrackNo = number;
            if (found.TryGetValue('t', out var title) && title != "") r.Title = title;
            if (found.TryGetValue('a', out var artist) && artist != "") r.Artist = artist;
            if (found.TryGetValue('b', out var album) && album != "") r.Album = album;
            done++;
        }
        return (done, skipped);
    }

    void MatchOnline()
    {
        var key = Chosen()[0].Track.AlbumKey;
        var album = App.Lib.Albums.FirstOrDefault(a => a.Key == key);
        if (album == null) return;
        if (_rows.Any(r => r.Changed) && MessageBox.Show(this, "Looking the album up applies its details straight away. Save your changes first?", "Phoenix", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        SaveRows();
        new DiscogsDialog(album) { Owner = this }.ShowDialog();
        // show what the lookup left behind
        var tracks = _rows.Select(r => r.Track).ToList();
        _rows.Clear();
        foreach (var t in tracks) _rows.Add(new TagRow(t));
    }

    /// <summary>Writes the changed rows to the library (and the files, if that is on). Returns files that could not be written.</summary>
    public int SaveRows()
    {
        var changed = _rows.Where(r => r.Changed).ToList();
        if (changed.Count == 0) return 0;
        foreach (var r in changed) r.Apply();
        return App.Lib.SaveTracks(changed.Select(r => r.Track));
    }

    void Save()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var failed = SaveRows();
        if (failed > 0) MessageBox.Show(this, $"Saved in Phoenix, but {failed} file(s) couldn't be updated (in use or read-only).", "Phoenix", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }
}
