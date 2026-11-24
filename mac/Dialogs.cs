using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.Desktop;

/// <summary>Base for the small modal windows.</summary>
public class Dialog : Window
{
    protected readonly StackPanel Body = new() { Margin = new Thickness(22) };

    protected Dialog(string title, double width)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = Body;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    protected StackPanel Buttons(string ok, Action onOk, string cancel = "cancel")
    {
        var okBtn = Ui.Pill(ok, onOk);
        okBtn.IsDefault = true;
        okBtn.MinWidth = 80;
        var cancelBtn = Ui.Pill(cancel, Close);
        cancelBtn.MinWidth = 80;
        cancelBtn.Margin = default;
        return new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0), Children = { okBtn, cancelBtn } };
    }

    protected Grid Field(string label, Control input)
    {
        input.VerticalAlignment = VerticalAlignment.Center;
        var text = Ui.Text(label);
        text.VerticalAlignment = VerticalAlignment.Center;
        text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("FgDim"));
        return Ui.Cells("150,*", "40", text, input.At(1));
    }

    // ---------- one-liners ----------

    public static async Task<string?> Ask(string title, string prompt, string initial)
    {
        var d = new Dialog(title, 460);
        var box = new TextBox { Text = initial };
        string? result = null;
        d.Body.Children.Add(Ui.Text(prompt, margin: new Thickness(0, 0, 0, 6)));
        d.Body.Children.Add(box);
        d.Body.Children.Add(d.Buttons("ok", () => { result = box.Text ?? ""; d.Close(); }));
        d.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await d.ShowDialog(HalcyonApp.Win);
        return result;
    }

    public static async Task<bool> Confirm(string text, string ok = "ok")
    {
        var d = new Dialog("Halcyon", 460);
        var yes = false;
        d.Body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        d.Body.Children.Add(d.Buttons(ok, () => { yes = true; d.Close(); }));
        await d.ShowDialog(HalcyonApp.Win);
        return yes;
    }

    public static async Task Message(string text)
    {
        var d = new Dialog("Halcyon", 460);
        d.Body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        var close = Ui.Pill("ok", d.Close);
        close.IsDefault = true;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 18, 0, 0);
        d.Body.Children.Add(close);
        await d.ShowDialog(HalcyonApp.Win);
    }
}

/// <summary>Edits one song, or the shared fields of several songs / a whole album. Blank shared fields are left unchanged.</summary>
public class EditInfoDialog : Dialog
{
    public EditInfoDialog(List<Track> tracks, bool album) : base("Edit info", 500)
    {
        var single = tracks.Count == 1 && !album;
        var f = tracks[0];
        string Shared(Func<Track, string> get) => tracks.All(t => get(t) == get(f)) ? get(f) : "";

        TextBox title = new() { Text = single ? f.Title : "", IsEnabled = single }, trackNo = new() { Text = single && f.TrackNo > 0 ? f.TrackNo.ToString() : "", IsEnabled = single, Width = 90, HorizontalAlignment = HorizontalAlignment.Left },
            artist = new() { Text = Shared(t => t.Artist) }, albumBox = new() { Text = Shared(t => t.Album) }, albumArtist = new() { Text = Shared(t => t.AlbumArtist) },
            genre = new() { Text = Shared(t => t.Genre) }, year = new() { Text = Shared(t => t.Year > 0 ? t.Year.ToString() : ""), Width = 90, HorizontalAlignment = HorizontalAlignment.Left };

        Body.Children.Add(Ui.H2(single ? "edit song info" : album ? "edit album info" : $"edit {tracks.Count} songs"));
        var hint = single ? "" : "Changes apply to every selected song. Fields left blank keep their current values. ";
        if (f.Source != "local") hint += $"This music comes from {f.Source}: your changes are kept in Halcyon and survive the next sync; the server itself is not changed.";
        if (hint != "") Body.Children.Add(Ui.Note(hint));
        Body.Children.Add(Field("Song title", title));
        Body.Children.Add(Field("Song artist", artist));
        Body.Children.Add(Field("Album", albumBox));
        Body.Children.Add(Field("Album artist", albumArtist));
        Body.Children.Add(Field("Genre", genre));
        Body.Children.Add(Field("Year", year));
        Body.Children.Add(Field("Track number", trackNo));
        Body.Children.Add(Buttons("save", async () =>
        {
            foreach (var t in tracks)
            {
                void Apply(TextBox box, Action<string> set) { var v = (box.Text ?? "").Trim(); if (v != "" || single) set(v); }
                if (single)
                {
                    if ((title.Text ?? "").Trim() != "") t.Title = title.Text!.Trim();
                    t.TrackNo = int.TryParse(trackNo.Text, out var n) ? n : 0;
                }
                Apply(artist, v => t.Artist = v == "" ? "Unknown Artist" : v);
                Apply(albumBox, v => t.Album = v == "" ? "Unknown Album" : v);
                Apply(albumArtist, v => t.AlbumArtist = v == "" ? t.Artist : v);
                Apply(genre, v => t.Genre = v);
                Apply(year, v => t.Year = int.TryParse(v, out var y) ? y : 0);
            }
            var failed = App.Lib.SaveTracks(tracks);
            Close();
            if (failed > 0) await Message($"Saved in Halcyon, but {failed} file(s) couldn't be updated (in use or read-only).");
        }));
    }
}

/// <summary>Edits what a video is and where it is grouped, for one video or many.</summary>
public class EditVideoDialog : Dialog
{
    static readonly (string key, string label)[] Kinds = { ("movie", "Movie"), ("episode", "TV episode"), ("video", "Other video") };

    public EditVideoDialog(List<VideoItem> videos) : base("Edit video info", 500)
    {
        var single = videos.Count == 1;
        var f = videos[0];
        string Shared(Func<VideoItem, string> get) => videos.All(v => get(v) == get(f)) ? get(f) : "";
        static string Num(int n) => n > 0 ? n.ToString() : "";
        TextBox Small(string text, bool enabled = true) => new() { Text = text, Width = 90, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = enabled };

        var kind = new ComboBox { Width = 200, ItemsSource = Kinds.Select(k => k.label).ToList(), SelectedIndex = Array.FindIndex(Kinds, k => k.key == Shared(v => v.Kind == "" ? "video" : v.Kind)) };
        TextBox title = new() { Text = single ? f.Title : "", IsEnabled = single }, series = new() { Text = Shared(v => v.Series) }, collection = new() { Text = Shared(v => v.Collection) };
        TextBox season = Small(Shared(v => Num(v.Season))), episode = Small(single ? Num(f.Episode) : "", single), year = Small(Shared(v => Num(v.Year)));

        Body.Children.Add(Ui.H2(single ? "edit video info" : $"edit {videos.Count} videos"));
        var hint = single ? "" : "Changes apply to every selected video. Fields left blank keep their current values. ";
        if (f.Source != "local") hint += $"These come from {f.Source}: your changes are kept in Halcyon and survive the next sync; the server itself is not changed.";
        if (hint != "") Body.Children.Add(Ui.Note(hint));
        Body.Children.Add(Field("Title", title));
        Body.Children.Add(Field("Type", kind));
        Body.Children.Add(Field("Series / folder group", series));
        Body.Children.Add(Field("Season", season));
        Body.Children.Add(Field("Episode number", episode));
        Body.Children.Add(Field("Year", year));
        Body.Children.Add(Field("Collection", collection));
        Body.Children.Add(Buttons("save", () =>
        {
            foreach (var v in videos)
            {
                if (kind.SelectedIndex >= 0) v.Kind = Kinds[kind.SelectedIndex].key;
                if (single)
                {
                    if ((title.Text ?? "").Trim() != "") v.Title = title.Text!.Trim();
                    v.Episode = int.TryParse(episode.Text, out var e) ? e : 0;
                }
                void Apply(TextBox box, Action<string> set) { var t = (box.Text ?? "").Trim(); if (single || t != "") set(t); }
                Apply(series, s => v.Series = s);
                Apply(season, s => v.Season = int.TryParse(s, out var n) ? n : 0);
                Apply(year, s => v.Year = int.TryParse(s, out var n) ? n : 0);
                Apply(collection, s => v.Collection = s);
            }
            App.Lib.SaveVideos(videos);
            Close();
        }));
    }
}

public record TrackComparison(string No, string Mine, string Theirs);

/// <summary>Finds an album on Discogs and applies its details, track names and cover.</summary>
public class DiscogsDialog : Window
{
    readonly Album _album;
    readonly TextBox _artist = new(), _albumBox = new();
    readonly ListBox _results = Ui.List("DiscogsRow"), _compare = Ui.List("CompareRow");
    readonly Image _cover = new() { Stretch = Stretch.UniformToFill };
    readonly TextBlock _relTitle = new() { FontSize = 20, FontWeight = FontWeight.Light, TextWrapping = TextWrapping.Wrap, MaxHeight = 56 }, _relArtist = new() { FontSize = 14 },
        _relMeta = Ui.Text("", "note"), _status = Ui.Status(), _resultCap = Ui.Cap("RELEASES");
    readonly CheckBox _chkAlbum = new() { Content = "album title, artist, year, genre", IsChecked = true, Margin = new Thickness(0, 0, 18, 0) },
        _chkTitles = new() { Content = "song titles", IsChecked = true, Margin = new Thickness(0, 0, 18, 0) }, _chkCover = new() { Content = "cover art", IsChecked = true };
    readonly Button _apply, _search;
    DiscogsRelease? _release;

    public DiscogsDialog(Album album)
    {
        _album = album;
        Title = "Find album info on Discogs";
        Width = 980; Height = 660; MinWidth = 820; MinHeight = 520;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _artist.Text = album.Artist == "Unknown Artist" ? "" : album.Artist;
        _albumBox.Text = album.Title == "Unknown Album" ? "" : album.Title;
        Art.SetSize(_cover, 300);
        _search = Ui.Pill("search", Search);
        _search.VerticalAlignment = VerticalAlignment.Bottom;
        _search.Margin = default;
        _apply = Ui.Pill("apply to album", Apply);
        _apply.IsEnabled = false;
        var close = Ui.Pill("close", Close);
        close.Margin = default;
        _artist.KeyDown += (_, e) => { if (e.Key == Key.Enter) Search(); };
        _albumBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Search(); };
        _results.SelectionChanged += (_, _) => LoadRelease();
        _status.Margin = new Thickness(0, 6, 0, 0);

        var query = Ui.Cells("*,10,*,10,Auto", "Auto",
            Ui.Column(Ui.Label("ARTIST"), _artist), Ui.Column(Ui.Label("ALBUM"), _albumBox).At(2), _search.At(4));
        query.Margin = new Thickness(0, 0, 0, 14);
        var coverBox = new Border { Width = 120, Height = 120, Margin = new Thickness(0, 0, 14, 0), ClipToBounds = true, Child = _cover };
        coverBox.Bind(Border.BackgroundProperty, this.GetResourceObservable("BgAlt"));
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(coverBox, Dock.Left);
        head.Children.Add(coverBox);
        head.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _relTitle, _relArtist, _relMeta } });
        var right = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        right.Children.Add(head);
        right.Children.Add(Ui.Under(Ui.Cap("YOUR SONGS  →  DISCOGS TRACK NAMES"), _compare));
        var middle = Ui.Cells("*,18,1.15*", "*", Ui.Under(_resultCap, _results), right.At(2));
        var bottom = Ui.Split(Ui.Column(Ui.Row(_chkAlbum, _chkTitles, _chkCover), _status),
            new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Children = { _apply, close } });
        bottom.Margin = new Thickness(0, 14, 0, 0);
        Content = Ui.Cells("*", "Auto,*,Auto", query, middle.At(0, 1), bottom.At(0, 2)).With(g => g.Margin = new Thickness(22));

        ShowTracks(null);
        if (album.Source != "local") _status.Text = $"This album comes from {album.Source}: changes are kept in Halcyon and survive the next sync; the server itself is not changed.";
        Opened += (_, _) =>
        {
            if (!App.Discogs.HasToken) _status.Text = "Add your Discogs personal access token in settings first.";
            else Search();
        };
    }

    async void Search()
    {
        _search.IsEnabled = false;
        _status.Text = "searching Discogs…";
        try
        {
            var results = await App.Discogs.SearchAsync((_artist.Text ?? "").Trim(), (_albumBox.Text ?? "").Trim());
            _results.ItemsSource = results;
            _resultCap.Text = $"RELEASES  {results.Count}";
            _status.Text = results.Count == 0 ? "No releases found. Try a shorter album or artist name." : "Pick the release that matches your copy.";
            if (results.Count > 0) _results.SelectedIndex = 0;
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _search.IsEnabled = true; }
    }

    async void LoadRelease()
    {
        if (_results.SelectedItem is not DiscogsResult r) return;
        _apply.IsEnabled = false;
        try
        {
            var rel = await App.Discogs.GetReleaseAsync(r.Id);
            if (!ReferenceEquals(_results.SelectedItem, r)) return;   // selection moved on while loading
            _release = rel;
            _relTitle.Text = rel.Title;
            _relArtist.Text = rel.Artist;
            _relMeta.Text = Fmt.Join(rel.Year > 0 ? rel.Year.ToString() : "", rel.Genre, rel.Style);
            Art.SetPath(_cover, rel.CoverUrl == "" ? null : rel.CoverUrl);
            ShowTracks(rel);
            _apply.IsEnabled = true;
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    void ShowTracks(DiscogsRelease? rel)
    {
        var mine = _album.Tracks;
        var theirs = rel?.Tracks ?? new List<DiscogsTrack>();
        _compare.ItemsSource = Enumerable.Range(0, Math.Max(mine.Count, theirs.Count))
            .Select(i => new TrackComparison((i + 1).ToString(), i < mine.Count ? mine[i].Title : "—", i < theirs.Count ? theirs[i].Title : "—")).ToList();
        if (rel == null) return;
        var same = mine.Count == theirs.Count;
        _chkTitles.IsEnabled = same;
        if (!same) _chkTitles.IsChecked = false;
        _chkCover.IsEnabled = rel.CoverUrl != "";
        if (rel.CoverUrl == "") _chkCover.IsChecked = false;
        _status.Text = same ? "Pick the release that matches your copy."
            : $"This release has {theirs.Count} tracks but your album has {mine.Count}, so song titles won't be changed.";
    }

    async void Apply()
    {
        if (_release == null) return;
        _apply.IsEnabled = false;
        _status.Text = "applying…";
        try
        {
            var failed = await App.Lib.ApplyReleaseAsync(_album, _release, _chkAlbum.IsChecked == true, _chkTitles.IsChecked == true, _chkCover.IsChecked == true);
            Close();
            if (failed > 0) await Dialog.Message($"Saved in Halcyon, but {failed} file(s) couldn't be updated (in use or read-only).");
        }
        catch (Exception ex) { _status.Text = ex.Message; _apply.IsEnabled = true; }
    }
}
