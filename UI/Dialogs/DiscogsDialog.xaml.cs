using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.UI;

public record TrackComparison(string No, string Mine, string Theirs);

public partial class DiscogsDialog : Window
{
    readonly Album _album;
    readonly List<IMetaSource> _sources;
    IMetaSource? _source;
    DiscogsRelease? _release;

    /// <summary>For the self-test: the sources on offer and the one chosen.</summary>
    public IReadOnlyList<IMetaSource> OfferedSources => _sources;
    public IMetaSource? ChosenSource => _source;

    public DiscogsDialog(Album album)
    {
        InitializeComponent();
        Owner = App.Win;
        _album = album;
        ArtistBox.Text = album.Artist == "Unknown Artist" ? "" : album.Artist;
        AlbumBox.Text = album.Title == "Unknown Album" ? "" : album.Title;
        ShowTracks(null);
        if (album.Source != "local") Status.Text = $"This album comes from {album.Source}: changes are kept in Phoenix and survive the next sync; the server itself is not changed.";
        // every source that can be used now, most trusted first; an album tied to a source opens on it
        _sources = Metadata.Sources.Where(x => x.Ready).OrderByDescending(x => Metadata.Setting(x.Id).Enabled).ThenByDescending(x => Metadata.Setting(x.Id).Weight).ToList();
        SourceBox.ItemsSource = _sources.Select(x => x.Name).ToList();
        var tied = Metadata.SourceOf(album);
        _source = _sources.FirstOrDefault(x => x.Id == tied) ?? _sources.FirstOrDefault(x => x.Id != "folder") ?? _sources.FirstOrDefault();
        ChkTie.IsChecked = _source != null && _source.Id == tied;
        _opening = true;
        SourceBox.SelectedIndex = _source == null ? -1 : _sources.IndexOf(_source);
        _opening = false;
        Loaded += (_, _) =>
        {
            if (_source == null) Status.Text = "No source is ready. Switch one on in settings → metadata.";
            else if (!App.SmokeMode) Search_Click(this, new RoutedEventArgs());
        };
    }

    bool _opening;

    void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_opening || SourceBox.SelectedIndex < 0) return;
        _source = _sources[SourceBox.SelectedIndex];
        ChkTie.IsChecked = _source.Id == Metadata.SourceOf(_album);
        Results.ItemsSource = null;
        _release = null;
        ApplyBtn.IsEnabled = false;
        ShowTracks(null);
        Search_Click(sender, e);
    }

    void Query_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Search_Click(sender, e); e.Handled = true; }
    }

    async void Search_Click(object sender, RoutedEventArgs e)
    {
        if (_source is not { } source) return;
        SearchBtn.IsEnabled = false;
        Status.Text = $"searching {source.Name}…";
        TrackCap.Text = $"YOUR SONGS  →  {source.Name.ToUpperInvariant()} TRACK NAMES";
        try
        {
            var results = await source.SearchAsync(_album, ArtistBox.Text.Trim(), AlbumBox.Text.Trim());
            if (!ReferenceEquals(source, _source)) return;   // another source was chosen meanwhile
            Results.ItemsSource = results;
            ResultCap.Text = $"RELEASES  {results.Count}";
            Status.Text = results.Count == 0 ? (source.Id == "folder" ? "No pictures beside this album's songs." : "No releases found. Try a shorter album or artist name.") : "Pick the release that matches your copy.";
            if (results.Count > 0) Results.SelectedIndex = 0;
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { SearchBtn.IsEnabled = true; }
    }

    async void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Results.SelectedItem is not MetaHit r || Metadata.Find(r.Source) is not { } from) return;
        ApplyBtn.IsEnabled = false;
        try
        {
            var rel = await from.GetAsync(r);
            if (!ReferenceEquals(Results.SelectedItem, r)) return;   // selection moved on while loading
            _release = rel;
            RelTitle.Text = rel.Title != "" ? rel.Title : r.Title;
            RelArtist.Text = rel.Artist != "" ? rel.Artist : r.Artist;
            RelMeta.Text = string.Join("  ·  ", new[] { rel.Year > 0 ? rel.Year.ToString() : "", rel.Genre, rel.Style }.Where(s => s != ""));
            Art.SetPath(Cover, rel.CoverUrl == "" ? null : rel.CoverUrl);
            ShowTracks(rel);
            ApplyBtn.IsEnabled = true;
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    void ShowTracks(DiscogsRelease? rel)
    {
        var mine = _album.Tracks;
        var theirs = rel?.Tracks ?? new List<DiscogsTrack>();
        var rows = new List<TrackComparison>();
        for (var i = 0; i < Math.Max(mine.Count, theirs.Count); i++)
            rows.Add(new TrackComparison((i + 1).ToString(), i < mine.Count ? mine[i].Title : "—", i < theirs.Count ? theirs[i].Title : "—"));
        TrackCompare.ItemsSource = rows;
        if (rel == null) return;
        var same = mine.Count == theirs.Count;
        ChkTitles.IsEnabled = same;
        if (!same) ChkTitles.IsChecked = false;
        ChkCover.IsEnabled = rel.CoverUrl != "";
        if (rel.CoverUrl == "") ChkCover.IsChecked = false;
        // a source that only has pictures leaves the album's names alone
        var coverOnly = rel.Title == "" && rel.Year == 0 && rel.Genre == "";
        ChkAlbum.IsEnabled = !coverOnly;
        if (coverOnly) ChkAlbum.IsChecked = false;
        Status.Text = coverOnly ? "This source has cover art only."
            : same ? "Pick the release that matches your copy."
            : $"This release has {theirs.Count} tracks but your album has {mine.Count}, so song titles won't be changed.";
    }

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_release == null) return;
        ApplyBtn.IsEnabled = false;
        Status.Text = "applying…";
        try
        {
            var key = _album.Key;
            var tie = ChkTie.IsChecked == true && _source != null;
            if (!tie && _source != null && Metadata.SourceOf(_album) == _source.Id) Metadata.SetSource(_album, Metadata.Auto);
            var failed = await App.Lib.ApplyReleaseAsync(_album, _release, ChkAlbum.IsChecked == true, ChkTitles.IsChecked == true, ChkCover.IsChecked == true);
            if (tie)
            {
                // the album may have a new name now: tie the source to the album as it is after the change
                var now = _album.Tracks.Count > 0 ? App.Lib.Albums.FirstOrDefault(a => a.Key == _album.Tracks[0].AlbumKey) ?? _album : _album;
                if (now.Key != key) App.Settings.AlbumSources.Remove(key);
                Metadata.SetSource(now, _source!.Id);
            }
            if (failed > 0)
                MessageBox.Show(this, $"Saved in Phoenix, but {failed} file(s) couldn't be updated (in use or read-only).", "Phoenix", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex) { Status.Text = ex.Message; ApplyBtn.IsEnabled = true; }
    }
}
