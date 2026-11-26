using System.IO;
using System.Windows;
using System.Windows.Controls;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.UI;

public class RipRow : Observable
{
    public CdTrack Track { get; init; } = null!;
    public int Number => Track.Number;
    public string Duration => Fmt.Time(Track.DurationMs);
    bool _selected = true;
    string _title = "", _status = "";
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
}

/// <summary>Rip audio CDs into the collection and burn audio CDs from it.</summary>
public partial class DiscView : UserControl
{
    List<RipRow> _rows = new();
    CancellationTokenSource? _ripCancel, _burnCancel;
    byte[]? _cover;

    public DiscView()
    {
        InitializeComponent();
        BurnList.ItemsSource = App.BurnList;
        foreach (var k in CdAudio.DiscKinds) BurnKindBox.Items.Add(k);
        BurnKindBox.SelectedIndex = Math.Clamp(App.Settings.BurnKind, 0, CdAudio.DiscKinds.Length - 1);
        App.BurnList.CollectionChanged += (_, _) => UpdateBurnCaption();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            RefreshDrives();
            UpdateRipFormat();
            BurnKindBox.SelectedIndex = Math.Clamp(App.Settings.BurnKind, 0, CdAudio.DiscKinds.Length - 1);
        };
        TrackDrag.Attach(BurnList, o => o is Track t ? new[] { t } : null, BurnList);
        UpdateBurnCaption();
        ShowPivot("burn");
    }

    /// <summary>"burn" shows the burn list, "cd" the disc in the drive.</summary>
    public void ShowPivot(string pivot)
    {
        var cd = pivot == "cd";
        PivotCd.IsChecked = cd;
        PivotBurn.IsChecked = !cd;
        CdPane.Visibility = CdBar.Visibility = cd ? Visibility.Visible : Visibility.Collapsed;
        BurnPane.Visibility = BurnBar.Visibility = cd ? Visibility.Collapsed : Visibility.Visible;
    }

    void Pivot_Click(object sender, RoutedEventArgs e) => ShowPivot((string)((FrameworkElement)sender).Tag);

    void UpdateRipFormat() => RipFormatText.Text = "as " + CdAudio.Formats[Math.Clamp(App.Settings.RipFormat, 0, CdAudio.Formats.Length - 1)];

    void RipSettings_Click(object sender, RoutedEventArgs e)
    {
        App.Win.Navigate("settings");
        App.Win.Page<SettingsView>("settings").ShowCategory("software", "Rip");
    }

    string? Drive => DriveBox.SelectedItem as string;

    void RefreshDrives()
    {
        var drives = CdAudio.Drives();
        var current = Drive;
        DriveBox.ItemsSource = drives;
        DriveBox.SelectedItem = current != null && drives.Contains(current) ? current : drives.FirstOrDefault();
        if (drives.Count == 0)
        {
            DriveStatus.Text = "No CD or DVD drive found. Connect one and open this page again.";
            RipBtn.IsEnabled = BurnBtn.IsEnabled = false;
        }
        else BurnBtn.IsEnabled = true;
    }

    void Drive_Changed(object sender, SelectionChangedEventArgs e) => ReadDisc();
    void ReadDisc_Click(object sender, RoutedEventArgs e) { RefreshDrives(); ReadDisc(); }

    async void ReadDisc()
    {
        if (Drive is not { } drive || _ripCancel != null) return;
        DriveStatus.Text = "reading disc…";
        List<CdTrack> toc;
        try { toc = await Task.Run(() => CdAudio.ReadToc(drive)); }
        catch (Exception ex) { DriveStatus.Text = ex.Message; toc = new(); }
        _cover = null;
        _rows = toc.Select(t => new RipRow { Track = t, Title = $"Track {t.Number}" }).ToList();
        RipList.ItemsSource = _rows;
        RipBtn.IsEnabled = _rows.Count > 0;
        CdEmpty.Visibility = _rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_rows.Count > 0 && App.BurnList.Count == 0) ShowPivot("cd");   // a CD went in and nothing is waiting to burn: show it
        if (_rows.Count > 0) DriveStatus.Text = $"audio CD: {_rows.Count} tracks, {Fmt.Time(toc.Sum(t => t.DurationMs))}";
        else if (!DriveStatus.Text.StartsWith("Can't")) DriveStatus.Text = "no audio CD in the drive";
    }

    // ---------- rip ----------

    async void Lookup_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) { RipStatus.Text = "Read a disc first."; return; }
        if (ArtistBox.Text.Trim() == "" && AlbumBox.Text.Trim() == "") { RipStatus.Text = "Type the artist and album, then look it up."; return; }
        LookupBtn.IsEnabled = false;
        try
        {
            RipStatus.Text = "searching Discogs…";
            var results = await App.Discogs.SearchAsync(ArtistBox.Text.Trim(), AlbumBox.Text.Trim());
            DiscogsRelease? match = null;
            // Prefer the first release with the same number of tracks as this disc.
            foreach (var r in results.Take(6))
            {
                var rel = await App.Discogs.GetReleaseAsync(r.Id);
                match ??= rel;
                if (rel.Tracks.Count == _rows.Count) { match = rel; break; }
            }
            if (match == null) { RipStatus.Text = "No matching release found on Discogs."; return; }
            if (match.Artist != "") ArtistBox.Text = match.Artist;
            if (match.Title != "") AlbumBox.Text = match.Title;
            if (match.Year > 0) YearBox.Text = match.Year.ToString();
            if (match.Genre != "") GenreBox.Text = match.Genre;
            var same = match.Tracks.Count == _rows.Count;
            if (same) for (var i = 0; i < _rows.Count; i++) _rows[i].Title = match.Tracks[i].Title;
            try { _cover = match.CoverUrl != "" ? await App.Discogs.GetBytesAsync(match.CoverUrl) : null; } catch { _cover = null; }
            RipStatus.Text = same ? $"Filled in from “{match.Title}” on Discogs."
                : $"Found “{match.Title}”, but it lists {match.Tracks.Count} tracks and this disc has {_rows.Count}, so track names were left alone.";
        }
        catch (Exception ex) { RipStatus.Text = ex.Message; }
        finally { LookupBtn.IsEnabled = true; }
    }

    static string Safe(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim().TrimEnd('.') is { Length: > 0 } s ? s : "Unknown";
    }

    async void Rip_Click(object sender, RoutedEventArgs e)
    {
        if (_ripCancel != null) { _ripCancel.Cancel(); return; }
        if (Drive is not { } drive) return;
        var rows = _rows.Where(r => r.Selected).ToList();
        if (rows.Count == 0) { RipStatus.Text = "Tick at least one track."; return; }
        var root = App.Settings.MusicFolders.FirstOrDefault(Directory.Exists);
        if (root == null) { RipStatus.Text = "Add a music folder in settings first; ripped music is saved there."; return; }

        var artist = ArtistBox.Text.Trim() is { Length: > 0 } a ? a : "Unknown Artist";
        var album = AlbumBox.Text.Trim() is { Length: > 0 } b ? b : "Unknown Album";
        var genre = GenreBox.Text.Trim();
        uint.TryParse(YearBox.Text, out var year);
        var format = Math.Clamp(App.Settings.RipFormat, 0, CdAudio.Formats.Length - 1);
        var folder = Path.Combine(root, Safe(artist), Safe(album));
        var cover = _cover;
        _ripCancel = new CancellationTokenSource();
        var cancel = _ripCancel.Token;
        RipBtn.Content = "STOP";
        var done = 0;
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var row in rows)
            {
                var title = row.Title.Trim() is { Length: > 0 } t ? t : $"Track {row.Number}";
                var target = Path.Combine(folder, $"{row.Number:00} {Safe(title)}{CdAudio.Extension(format)}");
                var temp = Path.Combine(Path.GetTempPath(), $"halcyon-rip-{Guid.NewGuid():N}.wav");
                RipStatus.Text = $"ripping track {row.Number}: {title}";
                try
                {
                    await Task.Run(() =>
                    {
                        CdAudio.RipTrack(drive, row.Track, temp, p => Dispatcher.BeginInvoke(() => row.Status = $"{p:P0}"), cancel);
                        Dispatcher.BeginInvoke(() => row.Status = "encoding…");
                        CdAudio.Encode(temp, target, format);
                        using var tf = TagLib.File.Create(target);
                        tf.Tag.Title = title;
                        tf.Tag.Performers = new[] { artist };
                        tf.Tag.AlbumArtists = new[] { artist };
                        tf.Tag.Album = album;
                        if (genre != "") tf.Tag.Genres = new[] { genre };
                        tf.Tag.Year = year;
                        tf.Tag.Track = (uint)row.Number;
                        tf.Tag.TrackCount = (uint)_rows.Count;
                        if (cover != null) tf.Tag.Pictures = new TagLib.IPicture[] { new TagLib.Picture(new TagLib.ByteVector(cover)) };
                        tf.Save();
                    }, cancel);
                    row.Status = "done";
                    row.Selected = false;
                    done++;
                }
                finally { try { File.Delete(temp); } catch { } }
            }
            RipStatus.Text = $"Ripped {done} track(s) to {folder}";
        }
        catch (OperationCanceledException) { RipStatus.Text = $"Stopped after {done} track(s)."; }
        catch (Exception ex) { RipStatus.Text = "Rip failed: " + ex.Message; }
        finally
        {
            _ripCancel = null;
            RipBtn.Content = "START RIP";
            if (done > 0) _ = App.Lib.ScanAsync(App.Win.SetStatus);
        }
    }

    // ---------- burn ----------

    void BurnKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (BurnKindBox.SelectedIndex < 0 || BurnKindBox.SelectedIndex == App.Settings.BurnKind) { UpdateBurnCaption(); return; }
        App.Settings.BurnKind = BurnKindBox.SelectedIndex;
        App.Settings.Save();
        UpdateBurnCaption();
    }

    /// <summary>About how many bytes the burn list takes on an MP3 CD or a data disc.</summary>
    public static long DiscBytes(IEnumerable<Track> tracks, bool mp3Only)
    {
        long total = 0;
        foreach (var t in tracks)
        {
            var copy = t.IsLocal && (!mp3Only || Path.GetExtension(t.Location).Equals(".mp3", StringComparison.OrdinalIgnoreCase));
            long size = 0;
            if (copy) try { size = new FileInfo(t.Location).Length; } catch { }
            total += size > 0 ? size : t.DurationMs * Math.Clamp(App.Settings.BurnMp3Kbps, 64, 320) / 8;
        }
        return total;
    }

    void UpdateBurnCaption()
    {
        var ms = App.BurnList.Sum(t => t.DurationMs);
        BurnEmpty.Visibility = App.BurnList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var kind = Math.Clamp(App.Settings.BurnKind, 0, CdAudio.DiscKinds.Length - 1);
        if (kind == 0)
        {
            BurnCap.Text = $"BURN LIST  {App.BurnList.Count}   ·   {Fmt.Time(ms)}{(ms > 80 * 60_000 ? "   ·   too long for one CD" : "")}";
            return;
        }
        var bytes = DiscBytes(App.BurnList, kind == 1);
        var mb = bytes / 1_000_000;
        var fits = bytes <= CdAudio.CdBytes ? "" : kind == 1 ? "   ·   too big for one CD" : bytes <= 4_700_000_000 ? "   ·   needs a DVD" : bytes <= 25_000_000_000 ? "   ·   needs a Blu-ray" : "   ·   too big for one disc";
        BurnCap.Text = $"BURN LIST  {App.BurnList.Count}   ·   {Fmt.Time(ms)}   ·   about {mb:N0} MB{fits}";
    }

    void BurnRemove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in BurnList.SelectedItems.Cast<Track>().ToList()) App.BurnList.Remove(t);
    }

    void BurnClear_Click(object sender, RoutedEventArgs e) => App.BurnList.Clear();
    void BurnUp_Click(object sender, RoutedEventArgs e) => MoveBurn(-1);
    void BurnDown_Click(object sender, RoutedEventArgs e) => MoveBurn(1);

    void MoveBurn(int delta)
    {
        var i = BurnList.SelectedIndex;
        if (i < 0 || i + delta < 0 || i + delta >= App.BurnList.Count) return;
        App.BurnList.Move(i, i + delta);
        BurnList.SelectedIndex = i + delta;
    }

    async void Burn_Click(object sender, RoutedEventArgs e)
    {
        if (_burnCancel != null) { _burnCancel.Cancel(); BurnStatus.Text = "stopping after the current step…"; return; }
        if (Drive is not { } drive) { BurnStatus.Text = "No disc drive found."; return; }
        var tracks = App.BurnList.ToList();
        if (tracks.Count == 0) { BurnStatus.Text = "The burn list is empty."; return; }
        var kind = Math.Clamp(App.Settings.BurnKind, 0, CdAudio.DiscKinds.Length - 1);
        if (kind > 0) { await BurnFilesAsync(drive, tracks, kind == 1); return; }
        if (tracks.Count > 99) { BurnStatus.Text = "An audio CD can hold at most 99 tracks."; return; }
        if (tracks.Sum(t => t.DurationMs) > 80 * 60_000) { BurnStatus.Text = "The burn list is longer than 80 minutes. Remove some songs."; return; }

        _burnCancel = new CancellationTokenSource();
        var cancel = _burnCancel.Token;
        BurnBtn.Content = "STOP";
        var temp = Path.Combine(Path.GetTempPath(), $"halcyon-burn-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temp);
            var files = new List<string>();
            // Convert everything first so a bad file is caught before the disc is touched.
            for (var i = 0; i < tracks.Count; i++)
            {
                BurnStatus.Text = $"preparing {i + 1} of {tracks.Count}: {tracks[i].Title}";
                var raw = Path.Combine(temp, $"{i:00}.raw");
                var source = Player.From(tracks[i]).Url;
                await Task.Run(() => CdAudio.DecodeForCd(source, raw, cancel), cancel);
                files.Add(raw);
            }
            cancel.ThrowIfCancellationRequested();

            var done = new TaskCompletionSource<Exception?>();
            var thread = new Thread(() =>
            {
                try { CdAudio.Burn(drive, files, s => Dispatcher.BeginInvoke(() => BurnStatus.Text = s)); done.SetResult(null); }
                catch (Exception ex) { done.SetResult(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);   // IMAPI2 is apartment-threaded
            thread.IsBackground = true;
            thread.Start();
            if (await done.Task is { } error) throw error;
            BurnStatus.Text = $"Burned {tracks.Count} tracks. The disc is ready.";
        }
        catch (OperationCanceledException) { BurnStatus.Text = "Stopped before burning; the disc was not written."; }
        catch (Exception ex) { BurnStatus.Text = "Burn failed: " + (ex.InnerException?.Message ?? ex.Message); }
        finally
        {
            _burnCancel = null;
            BurnBtn.Content = "START BURN";
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    /// <summary>The songs for an MP3 or data disc, in burn list order.</summary>
    public static List<CdAudio.DiscFile> DiscFiles(IEnumerable<Track> tracks) =>
        tracks.Select(t => new CdAudio.DiscFile(t.IsLocal ? t.Location : Player.From(t).Url, t.IsLocal, t.Title, t.Artist, t.Album, t.TrackNo, t.DurationMs, 0)).ToList();

    /// <summary>A label for the disc: the album when every song is from one, otherwise the date.</summary>
    public static string DiscLabel(IReadOnlyList<Track> tracks) =>
        tracks.Select(t => t.Album).Distinct().Count() == 1 && tracks[0].Album != "" ? tracks[0].Album : "Phoenix " + DateTime.Now.ToString("yyyy-MM-dd");

    async Task BurnFilesAsync(string drive, List<Track> tracks, bool mp3Only)
    {
        if (mp3Only && DiscBytes(tracks, true) > CdAudio.CdBytes) { BurnStatus.Text = "The songs won't fit on one CD (700 MB). Remove some, choose a lower MP3 quality in settings, or burn a data disc to a DVD."; return; }
        _burnCancel = new CancellationTokenSource();
        var cancel = _burnCancel.Token;
        BurnBtn.Content = "STOP";
        var temp = Path.Combine(Path.GetTempPath(), $"halcyon-burn-{Guid.NewGuid():N}");
        try
        {
            var songs = DiscFiles(tracks);
            var kbps = Math.Clamp(App.Settings.BurnMp3Kbps, 64, 320) * 1000;
            // prepare every file first so a bad one is caught before the disc is touched
            await Task.Run(() => CdAudio.StageFiles(songs, temp, mp3Only, kbps,
                i => Dispatcher.BeginInvoke(() => BurnStatus.Text = $"preparing {i + 1} of {songs.Count}: {songs[i].Title}"), cancel), cancel);
            cancel.ThrowIfCancellationRequested();
            var label = DiscLabel(tracks);
            var done = new TaskCompletionSource<Exception?>();
            var thread = new Thread(() =>
            {
                try { CdAudio.BurnData(drive, temp, label, s => Dispatcher.BeginInvoke(() => BurnStatus.Text = s)); done.SetResult(null); }
                catch (Exception ex) { done.SetResult(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);   // IMAPI2 is apartment-threaded
            thread.IsBackground = true;
            thread.Start();
            if (await done.Task is { } error) throw error;
            BurnStatus.Text = $"Burned {tracks.Count} songs{(mp3Only ? " as MP3s" : "")}. The disc is ready.";
        }
        catch (OperationCanceledException) { BurnStatus.Text = "Stopped before burning; the disc was not written."; }
        catch (Exception ex) { BurnStatus.Text = "Burn failed: " + (ex.InnerException?.Message ?? ex.Message); }
        finally
        {
            _burnCancel = null;
            BurnBtn.Content = "START BURN";
            try { Directory.Delete(temp, true); } catch { }
        }
    }
}
