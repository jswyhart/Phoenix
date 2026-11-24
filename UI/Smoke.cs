using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.UI;

/// <summary>
/// Developer self-test: `Halcyon.exe --smoke &lt;dir&gt;` (with HALCYON_DATA pointing at a throwaway folder) builds a sample
/// library, exercises scanning, playback, playlists and parsers, and writes screenshots plus smoke.log to &lt;dir&gt;.
/// </summary>
public static class Smoke
{
    static readonly StringBuilder Log = new();
    static string _dir = "";
    static int _fails;

    static int CountFiles(string folder) => Directory.Exists(folder) ? Directory.GetFiles(folder).Length : 0;

    static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _fails++;
        Log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
    }

    public static async Task RunAsync(string dir, bool server = false, bool bench = false, bool perf = false)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        try { if (perf) await RunPerf(); else if (bench) await RunBench(); else if (server) await RunServerProbe(); else await RunCore(); }
        catch (Exception ex) { _fails++; Log.AppendLine("EXCEPTION " + ex); }
        Log.AppendLine(_fails == 0 ? "ALL PASSED" : $"{_fails} FAILED");
        File.WriteAllText(Path.Combine(dir, "smoke.log"), Log.ToString());
        Application.Current.Shutdown(_fails == 0 ? 0 : 1);
    }

    /// <summary>`--perf &lt;dir&gt;`: times switching the Music tabs over whatever library HALCYON_DATA holds (no scanning).</summary>
    static string VisualTreeHelperFind(DependencyObject o)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(o); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(o, i);
            if (c is System.Windows.Controls.VirtualizingStackPanel vp) return $"{vp.GetType().Name} actual {vp.ActualHeight} viewport {vp.ViewportHeight} extent {vp.ExtentHeight}";
            var r = VisualTreeHelperFind(c);
            if (r != "") return r;
        }
        return "";
    }

    static async Task RunPerf()
    {
        var win = App.Win;
        Log.AppendLine($"INFO  library: {App.Lib.Tracks.Count} songs, {App.Lib.Albums.Count} albums");
        win.Navigate("music");
        var music = win.Page<MusicView>("music");
        async Task<long> Time(Action a)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            a();
            var d = Application.Current.Dispatcher;
            var ul = System.Diagnostics.Stopwatch.StartNew();
            win.UpdateLayout();
            Log.AppendLine($"  layout pass {ul.ElapsedMilliseconds} (after code {sw.ElapsedMilliseconds - ul.ElapsedMilliseconds})");
            await d.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            var l = sw.ElapsedMilliseconds;
            await d.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            var r = sw.ElapsedMilliseconds;
            await d.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Log.AppendLine($"  loaded {l}  render {r}");
            return sw.ElapsedMilliseconds;
        }
        Log.AppendLine($"INFO  books: {App.Lib.Books.Count}");
        Log.AppendLine($"PERF  books first visit {await Time(() => win.Navigate("books"))} ms");
        await Task.Delay(1500);
        Log.AppendLine($"PERF  quickplay {await Time(() => win.Navigate("quickplay"))} ms");
        Log.AppendLine($"PERF  books second visit {await Time(() => win.Navigate("books"))} ms");
        var booksPage = win.Page<BooksView>("books");
        Log.AppendLine($"PERF  books tab ebooks {await Time(() => booksPage.ShowTab("ebook"))} ms");
        Log.AppendLine($"PERF  books tab all {await Time(() => booksPage.ShowTab("all"))} ms");
        win.Navigate("music");
        foreach (var round in new[] { 1, 2 })
            foreach (var mode in new[] { "artists", "genres", "albums", "songs" })
                { MusicView.Trace.Clear(); Log.AppendLine($"PERF  round {round}  {mode,-8} {await Time(() => music.SetMode(mode))} ms"); Log.Append(MusicView.Trace.ToString()); }
        static int Count<T>(DependencyObject o) where T : DependencyObject
        {
            var n = o is T ? 1 : 0;
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(o); i++) n += Count<T>(System.Windows.Media.VisualTreeHelper.GetChild(o, i));
            return n;
        }
        music.SetMode("songs");
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        {
            var sl = (System.Windows.Controls.ListBox)music.FindName("SongList");
            long Lay() { var w = System.Diagnostics.Stopwatch.StartNew(); win.UpdateLayout(); return w.ElapsedMilliseconds; }
            var items = sl.ItemsSource;
            var tmpl = sl.ItemTemplate;
            sl.ItemsSource = null; Log.AppendLine($"PERF  step: unbind {Lay()} ms");
            sl.ItemTemplate = (DataTemplate)music.FindResource("SongRowCompact"); Log.AppendLine($"PERF  step: swap template (empty list) {Lay()} ms");
            sl.ItemsSource = items; Log.AppendLine($"PERF  step: bind with compact rows {Lay()} ms");
            sl.ItemsSource = null; Lay();
            sl.ItemTemplate = tmpl; Lay();
            sl.ItemsSource = items; Log.AppendLine($"PERF  step: bind with song rows {Lay()} ms");
            sl.ItemsSource = null; Lay();
            sl.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock)) };
            sl.ItemsSource = items; Log.AppendLine($"PERF  step: bind with trivial rows {Lay()} ms");
            sl.ItemsSource = null; sl.ItemTemplate = tmpl; Lay();
        }
        music.SetMode("albums");
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var grid = (DependencyObject)music.FindName("AlbumGrid");
        Log.AppendLine($"PERF  album grid realises {Count<System.Windows.Controls.ListBoxItem>(grid)} rows, {Count<System.Windows.Controls.Image>(grid)} images, grid width {((FrameworkElement)grid).ActualWidth}");
        {
            var w = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 100; i++) { var t = new System.Windows.Controls.TextBlock { Text = "Song title number " + i }; t.Measure(new Size(200, 100)); }
            Log.AppendLine($"PERF  100 plain TextBlocks measured in {w.ElapsedMilliseconds} ms");
            w.Restart();
            for (var i = 0; i < 100; i++) { var t = new System.Windows.Controls.TextBlock { Text = "Song title number " + i, TextTrimming = TextTrimming.CharacterEllipsis }; t.Measure(new Size(100, 100)); }
            Log.AppendLine($"PERF  100 trimmed TextBlocks measured in {w.ElapsedMilliseconds} ms");
            w.Restart();
            for (var i = 0; i < 100; i++) { var t = new System.Windows.Controls.TextBlock { Text = "Song title number " + i, FontFamily = new System.Windows.Media.FontFamily("Segoe UI Light") }; t.Measure(new Size(200, 100)); }
            Log.AppendLine($"PERF  100 Segoe UI Light TextBlocks measured in {w.ElapsedMilliseconds} ms");
            w.Restart();
            for (var i = 0; i < 100; i++) { var t = new System.Windows.Controls.Image(); t.Measure(new Size(200, 100)); }
            Log.AppendLine($"PERF  100 empty Images measured in {w.ElapsedMilliseconds} ms");
            var parent = (System.Windows.Controls.Panel)((FrameworkElement)music.FindName("LeftPane")).Parent;
            long Trial(string name, DataTemplate t)
            {
                var host = new System.Windows.Controls.WrapPanel { Width = 800 };
                parent.Children.Add(host);
                var sw3 = System.Diagnostics.Stopwatch.StartNew();
                foreach (var a in App.Lib.Albums.Take(25)) host.Children.Add(new System.Windows.Controls.ContentPresenter { Content = a, ContentTemplate = t });
                win.UpdateLayout();
                var ms = sw3.ElapsedMilliseconds;
                Log.AppendLine($"PERF  25 live tiles, {name}: {ms} ms");
                parent.Children.Remove(host);
                return ms;
            }
            DataTemplate Make(string xaml) => (DataTemplate)System.Windows.Markup.XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:ui='clr-namespace:Halcyon.UI;assembly=Phoenix'>" + xaml + "</DataTemplate>");
            Trial("real album tile", (DataTemplate)music.FindResource("AlbumTile"));
            Trial("real album tile (again)", (DataTemplate)music.FindResource("AlbumTile"));
            Trial("three text lines", Make("<StackPanel><TextBlock Text='{Binding Title}'/><TextBlock Text='{Binding Subtitle}'/><TextBlock Text='{Binding SourceBadge}'/></StackPanel>"));
            Trial("square + text, static brush", Make("<StackPanel><Grid Width='138' Height='138' Background='Gray'/><TextBlock Text='{Binding Title}'/></StackPanel>"));
            Trial("square + text, dynamic brush", Make("<StackPanel><Grid Width='138' Height='138' Background='{DynamicResource BgAlt}'/><TextBlock Text='{Binding Title}'/></StackPanel>"));
            Trial("square + image with art path", Make("<StackPanel><Grid Width='138' Height='138'><Image ui:Art.Path='{Binding ArtPath}' Stretch='UniformToFill'/></Grid><TextBlock Text='{Binding Title}'/></StackPanel>"));
            Trial("square + icon glyph", Make("<StackPanel><Grid Width='138' Height='138'><TextBlock Text='&#xE8D6;' FontFamily='Segoe MDL2 Assets' FontSize='34'/></Grid><TextBlock Text='{Binding Title}'/></StackPanel>"));
            Trial("text with ellipsis trimming", Make("<StackPanel><TextBlock Text='{Binding Title}' TextTrimming='CharacterEllipsis'/><TextBlock Text='{Binding Subtitle}' TextTrimming='CharacterEllipsis'/></StackPanel>"));
        }
        music.SetMode("songs");
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        System.Windows.Controls.ListBox list = (System.Windows.Controls.ListBox)music.FindName("SongList");
        Log.AppendLine($"PERF  songs tab realises {Count<System.Windows.Controls.ListBoxItem>(list)} rows of {list.Items.Count}; virtualizing={System.Windows.Controls.VirtualizingPanel.GetIsVirtualizing(list)}");
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        list.ItemsSource = null;
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Log.AppendLine($"PERF  clearing the list {sw2.ElapsedMilliseconds} ms");
        sw2.Restart();
        list.ItemsSource = App.Lib.Tracks.ToList();
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Log.AppendLine($"PERF  binding 2000 songs {sw2.ElapsedMilliseconds} ms");
        var feed = Environment.GetEnvironmentVariable("HALCYON_PODCAST_FEED");
        if (!string.IsNullOrEmpty(feed))
        {
            var (pod, eps) = await Halcyon.Services.PodcastService.FetchAsync(feed);
            Log.AppendLine($"INFO  podcast \"{pod.Title}\": {eps.Count} episodes; first url {eps[0].Url[..Math.Min(80, eps[0].Url.Length)]}");
            App.Player.Volume = 0;
            App.Player.Play(new[] { Halcyon.Core.Player.From(eps[0], pod) });
            var sw4 = System.Diagnostics.Stopwatch.StartNew();
            while (sw4.Elapsed < TimeSpan.FromSeconds(60) && App.Player.Position < TimeSpan.FromSeconds(3) && App.Player.Current != null)
                await Task.Delay(250);
            Check("podcast episode plays from its RSS feed", App.Player.Current != null && App.Player.Position >= TimeSpan.FromSeconds(3), $"position {App.Player.Position.TotalSeconds:0.0}s after {sw4.Elapsed.TotalSeconds:0.0}s, source {App.Player.Current?.Resolved?[..Math.Min(70, App.Player.Current.Resolved.Length)]}");
            App.Player.Stop();
        }
        Log.AppendLine("PERF  done");
    }

    /// <summary>`--bench &lt;dir&gt;`: times library scans over a large synthetic library (empty files; names carry the metadata).</summary>
    static async Task RunBench()
    {
        const int folders = 400, perFolder = 50;   // 20,000 songs
        var root = Path.Combine(_dir, "bench-music");
        if (!Directory.Exists(root))
            for (var f = 0; f < folders; f++)
            {
                var d = Path.Combine(root, $"Artist {f / 20}", $"Album {f}");
                Directory.CreateDirectory(d);
                for (var i = 0; i < perFolder; i++) File.WriteAllBytes(Path.Combine(d, $"{i + 1:00} Song {i + 1}.mp3"), Array.Empty<byte>());
            }
        App.Settings.MusicFolders = new() { root }; App.Settings.VideoFolders = new(); App.Settings.PictureFolders = new(); App.Settings.BookFolders = new();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await App.Lib.ScanAsync(_ => { });
        Log.AppendLine($"INFO  first scan of {App.Lib.Tracks.Count} songs: {sw.ElapsedMilliseconds} ms");
        Check("bench: all files indexed", App.Lib.Tracks.Count == folders * perFolder, $"{App.Lib.Tracks.Count}");
        var times = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            sw.Restart();
            await App.Lib.ScanAsync(_ => { });
            times.Add(sw.ElapsedMilliseconds);
        }
        Log.AppendLine($"INFO  rescan with nothing changed: {string.Join(", ", times)} ms");
        // startup cost without scanning: loading the stored library from the database
        sw.Restart();
        var fresh = new Library(new Database(AppPaths.DbPath));
        fresh.Load();
        Log.AppendLine($"INFO  loading {fresh.Tracks.Count} songs from the database: {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// `--probe &lt;dir&gt;`: exercises the connected Jellyfin server for real (sync, grouping, playback, seeking). Run it with
    /// HALCYON_DATA pointing at a scratch folder holding a copy of settings.json; playback is muted and nothing is saved.
    /// </summary>
    static async Task RunServerProbe()
    {
        var win = App.Win;
        var lib = App.Lib;
        var p = App.Player;
        App.Settings.MusicFolders = new(); App.Settings.VideoFolders = new(); App.Settings.PictureFolders = new();
        Check("server configured", App.Jellyfin.IsConnected);
        if (!App.Jellyfin.IsConnected) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await lib.SyncServersAsync(s => { if (s.Contains("failed")) Log.AppendLine("INFO  " + s); });
        var vids = lib.Videos;
        Log.AppendLine($"INFO  sync took {sw.Elapsed.TotalSeconds:0.0}s: {lib.Tracks.Count} songs, {vids.Count} videos");
        var eps = vids.Where(v => v.Kind == "episode").ToList();
        Log.AppendLine($"INFO  movies {vids.Count(v => v.Kind == "movie")}, episodes {eps.Count}, series {eps.Select(v => v.Series).Distinct().Count()}, " +
                       $"seasons {eps.Select(v => (v.Series, v.Season)).Distinct().Count()}, collections {vids.Where(v => v.Collection != "").Select(v => v.Collection).Distinct().Count()} " +
                       $"({vids.Count(v => v.Collection != "")} movies in them), other {vids.Count(v => v.Kind is "video" or "")}");
        Log.AppendLine($"INFO  direct-play {vids.Count(v => JellyfinClient.IsDirect(v.Location))}, transcode {vids.Count(v => !JellyfinClient.IsDirect(v.Location))}, " +
                       $"with art {vids.Count(v => v.ArtPath != "")}, series art {eps.Count(v => v.GroupArt != "")}, episodes without series {eps.Count(v => v.Series == "")}, without number {eps.Count(v => v.Episode == 0)}");
        Check("videos synced", vids.Count > 0);
        Check("every video classified", vids.All(v => v.Kind != ""));

        win.Navigate("videos");
        var view = win.Page<VideosView>("videos");
        foreach (var tab in new[] { "movies", "shows", "collections", "all" })
        {
            view.ShowTab(tab);
            await Task.Delay(2500);
            await Shot("probe-" + tab);
        }

        p.Volume = 0;
        async Task<double> WaitFor(Func<bool> done, int seconds)
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            while (!done() && t.Elapsed.TotalSeconds < seconds) await Task.Delay(250);
            return t.Elapsed.TotalSeconds;
        }

        foreach (var (label, pick) in new (string, VideoItem?)[]
                 {
                     ("movie", vids.FirstOrDefault(v => v.Kind == "movie" && v.DurationMs > 20 * 60_000)),
                     ("episode", vids.LastOrDefault(v => v.Kind == "episode" && v.DurationMs > 20 * 60_000))
                 })
        {
            if (pick == null) { Log.AppendLine($"INFO  no {label} video to test"); continue; }
            p.Play(new[] { Player.From(pick) });
            var t0 = await WaitFor(() => p.Position.TotalSeconds > 2, 40);
            await Task.Delay(1500);
            await Shot($"probe-{label}-at-start");
            Check($"{label}: playback starts", p.Position.TotalSeconds > 2, $"after {t0:0.0}s; reported length {p.Duration.TotalMinutes:0.0} min vs library {pick.DurationMs / 60000.0:0.0} min");
            foreach (var target in new[] { 600.0, 120.0, pick.DurationMs / 1000.0 * 0.8 })
            {
                p.Seek(TimeSpan.FromSeconds(target));
                await Task.Delay(700);
                var t1 = await WaitFor(() => p.Position.TotalSeconds > target + 1.5 && p.Position.TotalSeconds < target + 60, 40);
                var pos = p.Position.TotalSeconds;
                Check($"{label}: seek to {target:0}s", pos > target + 1.5 && pos < target + 60, $"position {pos:0.0}s after {t1:0.0}s");
                await Task.Delay(1500);
                await Shot($"probe-{label}-at-{target:0}s");
            }
            p.Toggle();
            var held = p.Position;
            await Task.Delay(1500);
            Check($"{label}: pause holds", Math.Abs((p.Position - held).TotalSeconds) < 0.3);
            p.Toggle();
            await Task.Delay(1500);
            await Shot("probe-playing-" + label);
            Check($"{label}: seek bar length", Math.Abs(p.Duration.TotalMilliseconds - pick.DurationMs) < 5000, $"{p.Duration.TotalMinutes:0.0} min");
        }
        p.Stop();
    }

    static async Task RunCore()
    {
        var win = App.Win;
        var lib = App.Lib;
        var music = Path.Combine(_dir, "music");
        var pics = Path.Combine(_dir, "pictures");
        if (Directory.Exists(music)) Directory.Delete(music, true);
        MakeSamples(music, pics);
        App.Settings.MusicFolders = new() { music };
        App.Settings.PictureFolders = new() { pics };
        var videoDir = Path.Combine(_dir, "videos");
        Directory.CreateDirectory(Path.Combine(videoDir, "Some Show", "Season 2"));
        Directory.CreateDirectory(Path.Combine(videoDir, "Home movies"));
        foreach (var f in new[] { @"Some.Show.S01E02.The.Second.One.mp4", @"Some Show\Season 2\S02E05.mkv", @"Another Show - s03e11 - Finale.mp4", @"Home movies\Beach 2019.mp4" })
            File.WriteAllBytes(Path.Combine(videoDir, f), new byte[16]);
        App.Settings.VideoFolders = new() { videoDir };

        await Shot("01-empty");

        // --- scan ---
        await lib.ScanAsync(_ => { });
        Check("scan finds songs", lib.Tracks.Count == 24, $"{lib.Tracks.Count} tracks");
        Check("albums grouped", lib.Albums.Count == 6, $"{lib.Albums.Count} albums");
        Check("tags read", lib.Tracks.Any(t => t.Title == "Song 3" && t.Artist == "The Example Band" && t.TrackNo == 3 && t.Year == 2001 && t.Genre == "Rock"));
        Check("cover art found", lib.Albums.All(a => a.ArtPath != "" && File.Exists(a.ArtPath)));
        Check("duration read", lib.Tracks.All(t => t.DurationMs is > 3000 and < 5000), $"{lib.Tracks[0].DurationMs} ms");
        await lib.ScanAsync(_ => { });
        Check("rescan is idempotent", lib.Tracks.Count == 24 && lib.Videos.Count == 4);
        Check("rescan opens no unchanged files", lib.LastScan is { Read: 0, Seen: >= 28 }, $"{lib.LastScan.Seen} files seen, {lib.LastScan.Read} opened, {lib.LastScan.Millis} ms");
        var touched = lib.Tracks[0].SourceId;
        File.SetLastWriteTimeUtc(touched, DateTime.UtcNow.AddHours(-3));
        await lib.ScanAsync(_ => { });
        Check("a changed file is the only one reopened", lib.LastScan.Read == 1 && lib.Tracks.Count == 24, $"{lib.LastScan.Read} opened");
        var extra = Path.Combine(Path.GetDirectoryName(touched)!, "99 Brand New.wav");
        WriteWav(extra, 2);
        await lib.ScanAsync(_ => { });
        Check("a new file is picked up", lib.LastScan.Read == 1 && lib.Tracks.Count == 25, $"{lib.LastScan.Read} opened, {lib.Tracks.Count} songs");
        File.Delete(extra);
        await lib.ScanAsync(_ => { });
        Check("a deleted file is dropped", lib.LastScan.Read == 0 && lib.Tracks.Count == 24 && !lib.Tracks.Any(t => t.SourceId == extra));

        // --- local videos are classified the same way server videos are ---
        VideoItem V(string title) => lib.Videos.First(v => v.SourceId.Contains(title));
        Check("video: dotted episode name", V("Second.One") is { Kind: "episode", Series: "Some Show", Season: 1, Episode: 2, Title: "The Second One" });
        Check("video: series from folder", V("S02E05") is { Kind: "episode", Series: "Some Show", Season: 2, Episode: 5 });
        Check("video: dashed episode name", V("Finale") is { Kind: "episode", Series: "Another Show", Season: 3, Episode: 11, Title: "Finale" });
        Check("video: other grouped by folder", V("Beach") is { Kind: "video", Series: "Home movies", Title: "Beach 2019" });
        var beach = V("Beach");
        beach.Kind = "movie"; beach.Year = 2019; beach.Collection = "Family";
        lib.SaveVideos(new[] { beach });
        await lib.ScanAsync(_ => { });
        Check("video regroup survives rescan", new Database(AppPaths.DbPath).LoadVideos().Single(v => v.Id == beach.Id) is { Kind: "movie", Collection: "Family", Edited: true });
        win.Navigate("videos");
        win.Page<VideosView>("videos").ShowTab("shows");
        await Shot("18-videos-shows");

        await Shot("02-quickplay");
        win.Navigate("music");
        await Shot("03-music-artists");
        var mv = win.Page<MusicView>("music");
        mv.SetMode("albums");
        await Shot("04-music-albums");
        mv.SetMode("songs");
        await Shot("05-music-songs");
        mv.SetMode("artists");

        // --- playback (file name contains '#' and '%') ---
        var album = lib.Albums[0];
        Menus.Play(album.Tracks);
        await Task.Delay(2500);
        var pos = App.Player.Position.TotalMilliseconds;
        Check("audio playback advances", App.Player.IsPlaying && pos > 800, $"position {pos:0} ms, file {Path.GetFileName(album.Tracks[0].SourceId)}");
        Check("duration known", App.Player.Duration.TotalSeconds > 3, $"{App.Player.Duration.TotalSeconds:0.0}s");
        Check("the player bar says what format and bitrate is playing", win.BarInfoShown.StartsWith("WAV") && win.BarInfoShown.Contains("kHz") && win.BarInfoShown.Contains("-bit") && win.BarInfoShown.Contains("kbps"), win.BarInfoShown);
        Check("format names", MediaInfo.FormatName("a.m4a", "MPEG-4 Audio (mp4a)") == "AAC" && MediaInfo.FormatName("a.m4a", "Apple Lossless (ALAC)") == "ALAC" && MediaInfo.FormatName("a.ogg", "Opus") == "Opus" && MediaInfo.FormatName("a.flac", "") == "FLAC");
        App.Player.Seek(TimeSpan.FromSeconds(3.2));
        await Task.Delay(2200);
        Check("auto-advance to next track", App.Player.CurrentIndex == 1, $"index {App.Player.CurrentIndex}");
        Check("play count recorded", album.Tracks[0].PlayCount == 1 && album.Tracks[0].LastPlayed > 0);
        Check("music plays through the audio engine and runs into the next song with no gap", App.Player.OnEngine && App.Player.Engine is { Handovers: >= 1 },
            $"on engine {App.Player.OnEngine}, {App.Player.Engine?.Handovers} handovers");
        App.Player.Toggle();
        var p1 = App.Player.Position;
        await Task.Delay(600);
        Check("pause holds position", !App.Player.IsPlaying && Math.Abs((App.Player.Position - p1).TotalMilliseconds) < 100);
        App.Player.Toggle();
        App.Player.Next();
        Check("next", App.Player.CurrentIndex == 2);

        // crossfade: the next song takes over while the last second of this one is still fading out
        App.Settings.Crossfade = 1;
        App.Player.ApplyAudioSettings();
        var handoversBefore = App.Player.Engine?.Handovers ?? 0;
        App.Player.PlayAt(0);
        var fade = System.Diagnostics.Stopwatch.StartNew();
        while (fade.ElapsedMilliseconds < 6000 && App.Player.CurrentIndex == 0) await Task.Delay(40);
        var early = App.Player.Position.TotalMilliseconds;
        Check("crossfade blends into the next song before this one has ended", App.Player.CurrentIndex == 1 && fade.ElapsedMilliseconds is > 2300 and < 3700 && early < 600
            && (App.Player.Engine?.Handovers ?? 0) == handoversBefore + 1, $"moved on after {fade.ElapsedMilliseconds} ms of a 4 s song, {early:0} ms into the next");
        App.Settings.Crossfade = 0;
        App.Player.ApplyAudioSettings();
        App.Player.PlayAt(2);
        Check("taskbar thumbnail has previous, play and next buttons", win.TaskbarButtonCount == 3);
        App.Player.Shuffle = true;
        App.Player.Shuffle = false;

        win.ShowNow(true);
        await Shot("06-now-playing");

        // lyrics: a .lrc file beside the song is shown in now playing and follows the music
        var timed = Halcyon.Core.Lyrics.Parse("[ar:Someone]\n[00:01.50]first\n[00:03.00][00:05.00]again\n[00:04.00]middle", "test");
        Check("timed lyrics are read in order", timed is { Synced: true, Lines.Count: 4 } && timed.LineAt(3500) == 1 && timed.LineAt(100) == -1 && timed.Lines[3].Text == "again");
        var plainWords = Halcyon.Core.Lyrics.Parse("one\n\n\ntwo\n", "test");
        Check("plain lyrics keep their verses", plainWords is { Synced: false, Lines.Count: 3 });
        File.WriteAllText(Path.ChangeExtension(album.Tracks[0].Location, ".lrc"), "[00:00.20]The first line\n[00:01.20]The second line\n[00:02.60]The third line\n");
        win.ShowQueueTab(true);
        App.Player.PlayAt(0);
        await Task.Delay(1900);
        Check("lyrics beside the song show in now playing and follow along", win.ShownLyrics is { Synced: true, Lines.Count: 3 } && win.ShownLyricLine == 1,
            $"{win.ShownLyrics?.Lines.Count} lines, on line {win.ShownLyricLine}");
        await Shot("06-now-playing-lyrics");
        App.Player.PlayAt(1);
        await Task.Delay(300);
        Check("a song without lyrics says so", win.ShownLyrics == null);
        win.ShowQueueTab(false);
        App.Player.PlayAt(2);
        win.ShowNow(false);

        // --- ratings, playlists, pins ---
        Cmd.Rate.Execute(album.Tracks[1]);
        Check("like", album.Tracks[1].Rating == 1 && lib.AutoPlaylist("liked").Count == 1);
        Cmd.Rate.Execute(lib.Albums[3].Tracks[2]);
        Cmd.Rate.Execute(lib.Albums[1].Tracks[0]);
        Check("liked: newest first", lib.Liked().Select(t => t.Id).SequenceEqual(new[] { lib.Albums[1].Tracks[0].Id, lib.Albums[3].Tracks[2].Id, album.Tracks[1].Id }));
        Cmd.Rate.Execute(lib.Albums[3].Tracks[2]);   // like -> dislike drops it from the list
        Cmd.Rate.Execute(lib.Albums[3].Tracks[2]);   // -> cleared
        Check("liked: order persisted", new Database(AppPaths.DbPath).LoadTracks().Where(t => t.Rating > 0).OrderByDescending(t => t.RatedAt).Select(t => t.Id).SequenceEqual(new[] { lib.Albums[1].Tracks[0].Id, album.Tracks[1].Id }));
        Cmd.Rate.Execute(lib.Albums[1].Tracks[0]);
        Cmd.Rate.Execute(lib.Albums[1].Tracks[0]);   // back to one liked song for the checks below

        // --- visualizer: a 1 kHz vizTone must light the bar that covers 1 kHz ---
        var viz = win.VisualizerControl;
        var vizTone = new float[4096];
        for (var i = 0; i < vizTone.Length; i++) vizTone[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * i / 48000.0) + 0.2 * Math.Sin(2 * Math.PI * 6000 * i / 48000.0));
        viz.Feed(vizTone, 48000);
        viz.Update();
        var top = Array.IndexOf(viz.Bars, viz.Bars.Max());
        var expected = (int)(Math.Log(1000 / 40.0) / Math.Log(16000 / 40.0) * viz.Bars.Length);
        Check("visualizer spectrum", Math.Abs(top - expected) <= 1 && viz.Bars[top] > 0.7 && viz.Bars[2] < 0.25, $"peak bar {top} (expected {expected}), level {viz.Bars[top]:0.00}");
        win.ShowNow(true);
        await Task.Delay(700);
        Check("visualizer runs in now playing", viz.IsVisible);
        var vizFrames = viz.Frames;
        await Task.Delay(1000);
        Log.AppendLine($"INFO  visualizer drew {viz.Frames - vizFrames} frames in one second");
        Check("visualizer keeps up with the screen (more than 40 frames a second)", viz.Frames - vizFrames > 40, $"{viz.Frames - vizFrames} fps");
        viz.Feed(vizTone, 48000);
        await Shot("20-visualizer");
        App.Settings.Visualizer = 2;
        win.ShowNow(true);
        viz.Feed(vizTone, 48000);
        await Shot("21-visualizer-scope");
        viz.Hold = true;
        var vizMusic = new float[4096];
        var rnd = new Random(7);
        for (var style = 1; style < Visualizer.Styles.Length; style++)
        {
            win.SetVisualizer(style, (style - 1) % Visualizer.Palettes.Length);
            for (var frame = 0; frame < 50; frame++)   // a changing chord so the waterfall and peaks have history
            {
                double f1 = 110 * (1 + frame % 5), f2 = 900 + 150 * (frame % 7), f3 = 5000 + 400 * (frame % 3);
                for (var i = 0; i < vizMusic.Length; i++)
                    vizMusic[i] = (float)(0.45 * Math.Sin(2 * Math.PI * f1 * i / 48000) + 0.25 * Math.Sin(2 * Math.PI * f2 * i / 48000) + 0.12 * Math.Sin(2 * Math.PI * f3 * i / 48000) + 0.03 * (rnd.NextDouble() - 0.5));
                viz.Feed(vizMusic, 48000);
                viz.Update();
            }
            viz.InvalidateVisual();
            await Shot($"22-viz-{style}-{Visualizer.Styles[style].Replace(' ', '-')}");
            Check($"visualizer style: {Visualizer.Styles[style]}", viz.IsVisible && viz.Mode == style);
        }
        var sizeBefore = (win.ActualWidth, win.ActualHeight);
        win.ToggleFullscreenForTest();
        await Task.Delay(800);
        viz.InvalidateVisual();
        await Shot("23-fullscreen-visualizer");
        // The window may be on any monitor, so compare against its own windowed size rather than the primary screen.
        Check("full screen fills the display", win.IsFullscreen && win.WindowState == WindowState.Maximized && win.ActualWidth > sizeBefore.Item1 && win.ActualHeight > sizeBefore.Item2,
            $"{win.ActualWidth:0}x{win.ActualHeight:0}");
        win.ToggleFullscreenForTest();
        await Task.Delay(500);
        Check("leaving full screen restores the window", !win.IsFullscreen && Math.Abs(win.ActualWidth - sizeBefore.Item1) < 2 && Math.Abs(win.ActualHeight - sizeBefore.Item2) < 2);
        viz.Hold = false;
        win.SetVisualizer(1, 0);
        win.ShowNow(false);
        var pl = lib.CreatePlaylist("Road trip");
        lib.AddToPlaylist(pl, lib.Albums[2].Tracks.Take(3));
        lib.TogglePin("album", album.Key, album.Title);
        lib.TogglePin("playlist", pl.Id.ToString(), pl.Name);
        lib.TogglePin("artist", lib.Albums[4].Artist, lib.Albums[4].Artist);
        var reloaded = new Database(AppPaths.DbPath);
        Check("playlist persisted", reloaded.LoadPlaylists().Single().TrackIds.Count == 3);
        Check("pins persisted", reloaded.LoadPins().Count == 3);
        Check("rating persisted", reloaded.LoadTracks().Count(t => t.Rating == 1) == 1);
        Check("smart mix", lib.SmartMix(album.Artist).Count > 4);

        // smart dj
        var seed = album.Tracks[0];
        var dj = lib.SmartDj(seed, new[] { seed }, 10);
        Check("smart dj picks other songs like the seed, never disliked ones", dj.Count > 0 && !dj.Contains(seed) && dj.All(t => t.Rating >= 0) && dj.Distinct().Count() == dj.Count, $"{dj.Count} songs");
        App.Player.PlaySmartDj(seed);
        Check("smart dj starts a mix from the seed song", App.Player.DjSeed == seed && App.Player.Current?.Track == seed && App.Player.Queue.Count > 1, $"{App.Player.Queue.Count} in the queue");
        Menus.Play(album.Tracks);
        App.Player.PlayAt(2);
        Check("playing something else ends the smart dj mix", App.Player.DjSeed == null);

        // auto playlists
        var likedOnly = new SmartPlaylist { Name = "liked only", Rating = 1, Sort = 5, Limit = 0 };
        Check("auto playlist follows its rules", lib.Evaluate(likedOnly).Count == lib.Tracks.Count(t => t.Rating > 0) && lib.Evaluate(likedOnly).Count > 0
            && lib.Evaluate(new SmartPlaylist { Artist = album.Artist, Limit = 2 }).Count == 2
            && lib.Evaluate(new SmartPlaylist { MaxPlays = 0, Limit = 0 }).All(t => t.PlayCount == 0)
            && lib.Evaluate(new SmartPlaylist { MinPlays = 1, Limit = 0 }).All(t => t.PlayCount >= 1),
            $"{lib.Evaluate(likedOnly).Count} liked");
        Check("auto playlist rules read as a sentence", Library.Describe(likedOnly).Contains("liked") && Library.Describe(likedOnly).Contains("artist and album"), Library.Describe(likedOnly));
        App.Settings.SmartPlaylists.Add(likedOnly);

        // podcasts: only what arrives after subscribing counts as new
        var cast = new Podcast { FeedUrl = "https://example.invalid/feed.xml", Title = "Test cast" };
        List<Episode> Eps(int n) => Enumerable.Range(1, n).Select(i => new Episode { Guid = "g" + i, Title = "Episode " + i, Url = $"https://example.invalid/{i}.mp3", Published = 1000 + i }).ToList();
        var firstSave = lib.Db.SavePodcast(cast, Eps(3));
        var secondSave = lib.Db.SavePodcast(cast, Eps(5));
        var savedCast = lib.Db.LoadPodcasts().First(x => x.FeedUrl == cast.FeedUrl);
        var castEps = lib.Db.LoadEpisodes(savedCast.Id);
        Check("podcast refresh marks only newly arrived episodes as new", firstSave == 0 && secondSave == 2 && savedCast.NewCount == 3 && castEps.Count == 5 && castEps[0].Fresh && !castEps[4].Fresh,
            $"first {firstSave}, second {secondSave}, new {savedCast.NewCount}");
        // which downloads to have and which to remove
        {
            Episode Ep(int id, bool played = false, string file = "", bool manual = false) => new() { Id = id, Title = "E" + id, Played = played, File = file, Manual = manual };
            var list = new List<Episode> { Ep(5), Ep(4, file: "f4"), Ep(3, played: true, file: "f3"), Ep(2, file: "f2"), Ep(1, file: "f1", manual: true) };
            var (want, remove) = PodcastSync.Plan(list, 2, removePlayed: true);
            Check("podcast downloads: newest unplayed are wanted; played and pushed-out ones go; hand-picked ones stay",
                want.Select(e => e.Id).SequenceEqual(new long[] { 5, 4 }) && remove.Select(e => e.Id).SequenceEqual(new long[] { 3, 2 }), $"want {string.Join(",", want.Select(e => e.Id))} remove {string.Join(",", remove.Select(e => e.Id))}");
            var (_, keepPlayed) = PodcastSync.Plan(list, 2, removePlayed: false);
            Check("podcast downloads: played episodes are kept when asked", keepPlayed.Select(e => e.Id).SequenceEqual(new long[] { 2 }));
            var (allWant, allRemove) = PodcastSync.Plan(list, PodcastSync.All, removePlayed: true, playingId: 3);
            Check("podcast downloads: 'every unplayed episode', and never the one that is playing", allWant.Count == 4 && allRemove.Count == 0);
            var named = PodcastSync.FileFor(new Podcast { Title = "Tech: Weekly?" }, new Episode { Title = "Ep 12 / What's <new>", Published = 1767225600 }, "https://x.invalid/a/b/file.m4a?token=1");
            Check("podcast downloads are named by series, date and title", named.StartsWith(Path.Combine(AppPaths.DataDir, "podcasts", "Tech Weekly")) && named.EndsWith(".m4a") && Path.GetFileName(named).Contains("Ep 12 What's new")
                && Path.GetFileName(named).IndexOfAny(Path.GetInvalidFileNameChars()) < 0, named);
            App.Settings.PodcastFolder = Path.Combine(AppPaths.DataDir, "elsewhere");
            Check("podcast downloads go to the chosen folder", PodcastSync.FileFor(cast, castEps[0], "https://x.invalid/1.mp3").StartsWith(Path.Combine(AppPaths.DataDir, "elsewhere", "Test cast")));
            App.Settings.PodcastFolder = "";
            PodcastSync.SetKeep(cast, 3);
            Check("a series can have its own download choice", PodcastSync.KeepFor(cast) == 3 && PodcastSync.KeepFor(new Podcast { FeedUrl = "other" }) == App.Settings.PodcastKeep);
            PodcastSync.SetKeep(cast, null);
        }
        lib.Db.DeletePodcast(savedCast.Id);
        win.Navigate("playlists");
        var playlistsPage = win.Page<PlaylistsView>("playlists");
        var autoRow = playlistsPage.Rows.FirstOrDefault(r => r.Smart == likedOnly);
        if (autoRow != null) playlistsPage.Select(autoRow);
        Check("auto playlist is listed with the playlists and shows its songs", autoRow != null && playlistsPage.ShownSongs.Count == lib.Tracks.Count(t => t.Rating > 0));
        await Shot("07-playlists-auto");
        playlistsPage.Select(playlistsPage.Rows[0]);
        await Shot("07-playlists");
        win.Navigate("quickplay");
        await Shot("08-quickplay-pins");

        // --- editing writes tags back to the file ---
        var edit = lib.Albums[5].Tracks[0];
        edit.Title = "Renamed Song";
        edit.Genre = "Jazz";
        var failed = lib.SaveTracks(new[] { edit });
        using (var tf = TagLib.File.Create(edit.SourceId))
            Check("tag write-back", failed == 0 && tf.Tag.Title == "Renamed Song" && tf.Tag.FirstGenre == "Jazz");

        // --- search ---
        win.Navigate("search");
        win.Page<SearchView>("search").Search("second example");
        await Shot("09-search");

        // --- pictures ---
        win.Navigate("pictures");
        await Task.Delay(1200);
        await Shot("10-pictures");

        // --- podcasts (parser, offline) ---
        var (pod, eps) = PodcastService.Parse(SampleFeed, "https://example.invalid/feed.xml");
        Check("podcast parse", pod.Title == "Sample Cast" && pod.Author == "Someone" && eps.Count == 2 && eps[0].DurationMs == 3_723_000 && eps[1].DurationMs == 90_000 && eps[0].Published > 0);
        lib.Db.SavePodcast(pod, eps);
        lib.Podcasts = lib.Db.LoadPodcasts();
        Check("podcast persisted", lib.Db.LoadEpisodes(pod.Id).Count == 2);
        win.Navigate("podcasts");
        await Shot("11-podcasts");

        // --- discogs release parser (offline sample of the documented response shape) ---
        var rel = DiscogsClient.ParseRelease(J.Parse(SampleRelease));
        Check("discogs parse", rel.Title == "Sample Album" && rel.Artist == "The Example Band" && rel.Year == 1999 && rel.Genre == "Electronic"
                               && rel.Tracks.Count == 4 && rel.CoverUrl == "https://example.invalid/primary.jpg");
        var target = lib.Albums[3];
        var before = target.Tracks.Select(t => t.Id).ToList();
        await lib.ApplyReleaseAsync(target, rel with { CoverUrl = "" }, albumInfo: true, titles: true, cover: false);
        var applied = lib.Tracks.Where(t => before.Contains(t.Id)).OrderBy(t => t.TrackNo).ToList();
        Check("discogs apply", applied.All(t => t.Album == "Sample Album" && t.Year == 1999 && t.Genre == "Electronic") && applied[0].Title == "Intro" && applied[3].Title == "Outro"
                               && applied.All(t => t.ArtPath != "" && File.Exists(t.ArtPath)));

        // --- remote source plumbing (no server: checks merge/replace and URL building) ---
        App.Settings.JellyfinUrl = "http://jf.invalid:8096"; App.Settings.JellyfinToken = "tok"; App.Settings.JellyfinUserId = "uid";
        App.Settings.PlexUrl = "http://plex.invalid:32400"; App.Settings.PlexToken = "ptok";
        var remote = Enumerable.Range(1, 5).Select(i => new Track { Source = "jellyfin", SourceId = "id" + i, Location = "id" + i, Title = "Remote " + i, Artist = "Remote Artist", AlbumArtist = "Remote Artist", Album = "Cloud Album", TrackNo = i, DateAdded = Fmt.Now }).ToList();
        await lib.ReplaceSourceAsync("jellyfin", remote, new());
        Check("remote merge", lib.Tracks.Count == 29 && lib.Albums.Any(a => a.Source == "jellyfin" && a.Tracks.Count == 5));
        Check("jellyfin stream url", Player.From(remote[0]).Url.StartsWith("http://jf.invalid:8096/Audio/id1/stream.mp3?") && Player.From(remote[0]).Url.Contains("api_key=tok"));
        Check("plex stream url", App.Plex.PartUrl("/library/parts/9/file.mp3") == "http://plex.invalid:32400/library/parts/9/file.mp3?X-Plex-Token=ptok");
        var rv = new VideoItem { Source = "jellyfin", SourceId = "v1", Location = "v1|transcode", Title = "Pilot", Kind = "episode", Series = "Remote Show", Season = 1, Episode = 1, DurationMs = 1_800_000 };
        await lib.ReplaceSourceAsync("jellyfin", remote, new() { rv });
        var item = Player.From(rv);
        Check("jellyfin video seek url", item.SeekUrl != null && item.SeekUrl(600_000).Contains("StartTimeTicks=6000000000") && !item.Url.Contains("StartTimeTicks"));
        rv.Series = "Renamed Show";
        lib.SaveVideos(new[] { rv });
        remote[0].Album = "My Own Grouping"; remote[0].GroupId = "album1";
        lib.SaveTracks(new[] { remote[0] });
        var again = new VideoItem { Source = "jellyfin", SourceId = "v1", Location = "v1|transcode", Title = "Pilot", Kind = "episode", Series = "Remote Show", Season = 1, Episode = 1 };
        var fresh = remote.Take(2).Select(t => new Track { Source = "jellyfin", SourceId = t.SourceId, Location = t.Location, Title = t.Title, Artist = t.Artist, AlbumArtist = t.AlbumArtist, Album = "Cloud Album", GroupId = "album1", TrackNo = t.TrackNo }).ToList();
        await lib.ReplaceSourceAsync("jellyfin", fresh, new() { again });
        Check("remote resync removes missing", lib.Tracks.Count == 26);
        Check("edits to server items survive a sync", lib.Videos.Single(v => v.Source == "jellyfin").Series == "Renamed Show" && lib.Tracks.Single(t => t.SourceId == "id1").Album == "My Own Grouping");
        Check("server albums group by server id unless regrouped", lib.Albums.Count(a => a.Source == "jellyfin") == 2);
        await lib.RemoveSourceAsync("jellyfin");
        Check("remote disconnect", lib.Tracks.Count == 24);

        // --- dialogs and mini player open without XAML errors ---
        await ShotWindow("14-edit-song", new EditInfoDialog(new List<Track> { lib.Albums[0].Tracks[0] }, false));
        await ShotWindow("15-edit-album", new EditInfoDialog(lib.Albums[0].Tracks, true));
        await ShotWindow("16-discogs-dialog", new DiscogsDialog(lib.Albums[0]));
        await ShotWindow("17-mini", new MiniWindow());
        foreach (var menu in new[] { Menus.ForTracks(lib.Albums[0].Tracks.Take(1).ToList()), Menus.ForAlbum(lib.Albums[0]), Menus.ForArtist(lib.Albums[0].Artist) })
            Check("context menu builds", menu.Items.Count >= 5);

        // --- live Discogs probe (public release endpoint, no token): informational only ---
        try
        {
            var live = await App.Discogs.GetReleaseAsync(249504);
            Log.AppendLine($"INFO  discogs live: \"{live.Artist} – {live.Title}\" ({live.Year}), {live.Tracks.Count} tracks, genre {live.Genre}, cover {(live.CoverUrl != "" ? "yes" : "no")}");
        }
        catch (Exception ex) { Log.AppendLine("INFO  discogs live probe skipped: " + ex.Message); }

        // --- covers recorded under the old data folder follow the library to the new one ---
        {
            var moved = lib.Tracks[0];
            var real = moved.ArtPath;
            var oldRoot = @"C:\Somewhere\Halcyon\";
            moved.ArtPath = oldRoot + @"art\ABC123.jpg";
            lib.Db.UpdateTrack(moved);
            var changed = lib.Db.MoveDataFolder(oldRoot.ToUpperInvariant(), AppPaths.DataDir.TrimEnd('\\') + "\\");
            var now = new Database(AppPaths.DbPath).LoadTracks().First(t => t.Id == moved.Id).ArtPath;
            Check("covers recorded under the old data folder are pointed at the new one", changed == 1 && now == Path.Combine(AppPaths.DataDir, "art", "ABC123.jpg"), $"{changed} changed, now {now}");
            moved.ArtPath = real;
            lib.Db.UpdateTrack(moved);
        }

        // --- picture thumbnails: Windows' cache is used when it has them ---
        {
            var wall = Path.Combine(AppPaths.DataDir, "thumb-test");
            Directory.CreateDirectory(wall);
            var files = Enumerable.Range(0, 40).Select(i => Path.Combine(wall, $"wall {i:00}.png")).ToList();
            foreach (var f in files) SavePng(f, f.GetHashCode() % 2 == 0 ? "#3A7BD5" : "#D5573A", Path.GetFileNameWithoutExtension(f));
            var countsBefore = Art.ThumbnailCounts;
            var cold = System.Diagnostics.Stopwatch.StartNew();
            var first = await Task.WhenAll(files.Select(f => Art.LoadAsync("shell:" + f, 256)));
            cold.Stop();
            var mid = Art.ThumbnailCounts;
            Art.ForgetAll();   // forget our own copies, so the second pass has to ask Windows again
            var warm = System.Diagnostics.Stopwatch.StartNew();
            var second = await Task.WhenAll(files.Select(f => Art.LoadAsync("shell:" + f, 256)));
            warm.Stop();
            var after = Art.ThumbnailCounts;
            Check("picture thumbnails load for a wall of pictures", first.All(x => x != null) && second.All(x => x != null), $"{first.Count(x => x != null)} and {second.Count(x => x != null)} of 40");
            Log.AppendLine($"INFO  40 picture thumbnails: first time {cold.ElapsedMilliseconds} ms ({mid.Made - countsBefore.Made} made, {mid.FromWindows - countsBefore.FromWindows} from Windows' cache); " +
                           $"again {warm.ElapsedMilliseconds} ms ({after.Made - mid.Made} made, {after.FromWindows - mid.FromWindows} from Windows' cache)");
        }

        // --- translated names ---
        {
            Check("translation answers are read", Translator.ParseMyMemory(J.Parse("""{"responseData":{"translatedText":"Racing into the Night"},"responseStatus":200}""")) == "Racing into the Night"
                && Translator.ParseDeepL(J.Parse("""{"translations":[{"detected_source_language":"JA","text":"Racing into the Night"}]}""")) == "Racing into the Night");
            Check("a translation that only repeats the name is not shown", Translator.Useful("Hello", "hello!") == "" && Translator.Useful("夜に駆ける", " Racing into the Night ") == "Racing into the Night");
            Check("translating is off until switched on", !Translator.Needs("夜に駆ける"));
            App.Settings.TranslateOn = true;
            App.Settings.TranslateTo = "en";
            Translator.Reset();
            Check("only names in another script are sent, unless asked otherwise", Translator.Needs("夜に駆ける") && Translator.Needs("Кино") && !Translator.Needs("Hello World") && !Translator.Needs("Unknown Artist") && !Translator.Needs("1984"));
            App.Settings.TranslateLatin = true;
            Check("names in Latin letters are sent when that is switched on", Translator.Needs("Hola Mundo"));
            App.Settings.TranslateLatin = false;
            lib.Db.SaveTranslation("en", "夜に駆ける", "Racing into the Night");
            Translator.Reset();
            var foreign = new TextBlock();
            Tr.SetText(foreign, "夜に駆ける");
            var plain = new TextBlock();
            Tr.SetText(plain, "Hello World");
            Check("a translated name is shown beside the original", Tr.Shown(foreign) == "夜に駆ける   Racing into the Night" && foreign.Inlines.Count == 2 && Tr.Shown(plain) == "Hello World", Tr.Shown(foreign));
            Check("translations are remembered in the library", new Database(AppPaths.DbPath).LoadTranslations("en").Any(x => x.Source == "夜に駆ける" && x.Result == "Racing into the Night"));
            App.Settings.TranslateOn = false;
            Translator.Reset();
            var off = new TextBlock();
            Tr.SetText(off, "夜に駆ける");
            Check("with translating off the name is shown alone", Tr.Shown(off) == "夜に駆ける");
        }

        // --- book covers say where you are; a long list of books does not build what is off screen ---
        {
            Check("a book cover says where you are", BookTile.CounterFor(new Book { Format = "comic", Part = 11, PositionMs = 180, Progress = 12 / 180.0 }) == "12 / 180"
                && BookTile.CounterFor(new Book { Format = "epub", Progress = 0.345 }) == "34%" && BookTile.CounterFor(new Book { Format = "comic", Finished = true, Progress = 1 }) == "read"
                && BookTile.CounterFor(new Book { Format = "comic" }) == "" && BookTile.CounterFor(new Book { Kind = "audio", Format = "audio", Progress = 0.5 }) == "50%");
            var booksBefore = lib.Books;
            var many = Enumerable.Range(0, 6000).Select(i => new Book { Id = 900000 + i, Kind = "ebook", Format = "comic", Title = $"Issue {i:0000}", Author = "Many Books", SourceId = $"x{i}" }).ToList();
            lib.Books = booksBefore.Concat(many).ToList();
            win.Navigate("quickplay");
            var visit = System.Diagnostics.Stopwatch.StartNew();
            win.Navigate("books");
            win.UpdateLayout();
            await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var booksFirstMs = visit.ElapsedMilliseconds;
            var rowsShown = win.Page<BooksView>("books").BuiltRows;

            // the wheel glides: one click moves the list over many frames, and ends exactly one step on
            var area = win.Page<BooksView>("books").TileArea;
            var startAt = area.VerticalOffset;
            var framesBefore = SmoothScroll.Frames;
            SmoothScroll.Wheel(area, -120);
            await Task.Delay(40);
            var midway = area.VerticalOffset;
            await Task.Delay(900);
            Check("a wheel click glides over many frames to exactly one step on", Math.Abs(area.VerticalOffset - startAt - SmoothScroll.Step) < 0.6 && midway > startAt && midway < startAt + SmoothScroll.Step && SmoothScroll.Frames - framesBefore > 8,
                $"moved {area.VerticalOffset - startAt:0.#} px in {SmoothScroll.Frames - framesBefore} frames, {midway - startAt:0.#} px after 40 ms");
            SmoothScroll.Wheel(area, -120); SmoothScroll.Wheel(area, -120); SmoothScroll.Wheel(area, -120);
            await Task.Delay(1000);
            Check("quick clicks add up", Math.Abs(area.VerticalOffset - startAt - 4 * SmoothScroll.Step) < 0.6, $"{area.VerticalOffset - startAt:0.#} px");
            SmoothScroll.Wheel(area, 480);
            await Task.Delay(1000);
            win.Navigate("quickplay");
            lib.NotifyChanged();     // the list changes while the page is hidden
            visit.Restart();
            win.Navigate("books");
            win.UpdateLayout();
            await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var againMs = visit.ElapsedMilliseconds;
            Log.AppendLine($"INFO  books page with {lib.Books.Count} books: first visit {booksFirstMs} ms, coming back {againMs} ms, {win.Page<BooksView>("books").BuiltRows} rows built");
            Check("a library of thousands of books opens quickly and builds only the rows on screen", rowsShown is > 0 and < 12 && win.Page<BooksView>("books").BuiltRows is > 0 and < 12 && againMs < 1500, $"{rowsShown} then {win.Page<BooksView>("books").BuiltRows} rows, {booksFirstMs} ms and {againMs} ms");
            lib.Books = booksBefore;
            lib.NotifyChanged();
            win.Navigate("quickplay");
        }

        // --- translating on this PC ---
        {
            Check("a name's language is told from its script", LocalTranslator.Detect("夜に駆ける") == "ja" && LocalTranslator.Detect("봄날") == "ko" && LocalTranslator.Detect("海阔天空") == "zh" && LocalTranslator.Detect("Кино") == "ru"
                && LocalTranslator.Detect("Океан Ельзи їде") == "uk" && LocalTranslator.Detect("فيروز") == "ar" && LocalTranslator.Detect("Hello") == "" && LocalTranslator.Detect("Björk") == "");
            var sample = LocalTranslator.ParseCatalogue(J.Parse("""
                {"data":[
                {"fromLang":"ru","toLang":"en","version":"2.0","fileType":"model","attachment":{"filename":"model.ruen.bin","size":30,"hash":"aa","location":"x/old-model"}},
                {"fromLang":"ru","toLang":"en","version":"2.1","fileType":"model","attachment":{"filename":"model.ruen.bin","size":31,"hash":"ab","location":"x/model"}},
                {"fromLang":"ru","toLang":"en","version":"2.1","fileType":"lex","attachment":{"filename":"lex.ruen.bin","size":4,"hash":"ac","location":"x/lex"}},
                {"fromLang":"ru","toLang":"en","version":"2.1","fileType":"vocab","attachment":{"filename":"vocab.ruen.spm","size":1,"hash":"ad","location":"x/vocab"}},
                {"fromLang":"en","toLang":"de","version":"1.0","fileType":"model","attachment":{"filename":"model.ende.bin","size":30,"hash":"ba","location":"y/model"}},
                {"fromLang":"en","toLang":"de","version":"1.0","fileType":"lex","attachment":{"filename":"lex.ende.bin","size":4,"hash":"bb","location":"y/lex"}},
                {"fromLang":"en","toLang":"de","version":"1.0","fileType":"srcvocab","attachment":{"filename":"srcvocab.ende.spm","size":1,"hash":"bc","location":"y/src"}},
                {"fromLang":"en","toLang":"de","version":"1.0","fileType":"trgvocab","attachment":{"filename":"trgvocab.ende.spm","size":1,"hash":"bd","location":"y/trg"}},
                {"fromLang":"ko","toLang":"en","version":"1.0","fileType":"model","attachment":{"filename":"model.koen.bin","size":30,"hash":"ca","location":"z/model"}}
                ]}
                """));
            Check("language packs: the newest complete version of each, incomplete ones left out", sample.Count == 2 && sample.First(x => x.Key == "ruen") is { Version: "2.1", Size: 36 } && sample.First(x => x.Key == "ende").Files.Count == 4,
                string.Join(", ", sample.Select(x => $"{x.Key} {x.Version} {x.Files.Count} files")));
            Check("a language with no direct pack goes through English", LocalTranslator.Route(sample, "ru", "de").Select(x => x.Key).SequenceEqual(new[] { "ruen", "ende" }) && LocalTranslator.Route(sample, "ru", "en").Count == 1 && LocalTranslator.Route(sample, "ko", "en").Count == 0);
            Check("language packs are kept in the data folder unless another is chosen", LocalTranslator.Folder == Path.Combine(AppPaths.DataDir, "translation"));

            // the real thing (informational: needs the network and a 37 MB download): Russian to English, entirely on this PC
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var catalogue = await LocalTranslator.CatalogueAsync();
                var route = LocalTranslator.Route(catalogue, "ru", "en");
                foreach (var pack in route) await LocalTranslator.DownloadAsync(pack, null);
                var downloadMs = clock.ElapsedMilliseconds;
                clock.Restart();
                var first = await LocalTranslator.TranslateAsync("Группа крови", "ru", "en");
                var firstMs = clock.ElapsedMilliseconds;
                clock.Restart();
                var second = await LocalTranslator.TranslateAsync("Звезда по имени Солнце", "ru", "en");
                Log.AppendLine($"INFO  offline translation: {catalogue.Count} packs published; ru→en {route.Sum(x => x.Size) >> 20} MB downloaded in {downloadMs} ms; \"Группа крови\" → \"{first}\" ({firstMs} ms, includes loading the pack); \"Звезда по имени Солнце\" → \"{second}\" ({clock.ElapsedMilliseconds} ms)");
                Check("the offline translator translates with a downloaded language pack", first.Trim().Length > 0 && second.Contains("Sun", StringComparison.OrdinalIgnoreCase) && LocalTranslator.CanTranslate("ru", "en") && !LocalTranslator.CanTranslate("ja", "en"), $"{first} / {second}");

                // through the same path the song lists use
                App.Settings.TranslateOn = true; App.Settings.TranslateTo = "en"; App.Settings.TranslateProvider = "local";
                Translator.Reset();
                Check("with the local translator only names whose language is known are sent", Translator.Needs("Кино") && !Translator.Needs("Hello World"));
                var viaList = await Translator.GetAsync("Кино");
                Check("song names are translated on this PC and remembered", viaList.Length > 0 && Translator.Lookup("Кино") == viaList, viaList);
                var noPack = await Translator.GetAsync("千本桜のうた");
                Check("a language without its pack is left alone, and not remembered as untranslatable", noPack == "" && !new Database(AppPaths.DbPath).LoadTranslations("en").Any(x => x.Source == "千本桜のうた"));
                var movedPacks = Path.Combine(Path.GetDirectoryName(AppPaths.DataDir.TrimEnd('\\'))!, "packs-moved");
                if (Directory.Exists(movedPacks)) Directory.Delete(movedPacks, true);
                await LocalTranslator.MoveToAsync(movedPacks);
                var afterMove = await LocalTranslator.TranslateAsync("Перемен", "ru", "en");
                Check("language packs can be moved to another folder and still work", LocalTranslator.Folder == movedPacks && LocalTranslator.Installed("ru", "en") && afterMove.Length > 0 && !Directory.Exists(Path.Combine(AppPaths.DataDir, "translation")), afterMove);
                await LocalTranslator.RemoveAsync("ru", "en");
                Check("removing a language pack", !LocalTranslator.CanTranslate("ru", "en"));
                await LocalTranslator.MoveToAsync("");
            }
            catch (Exception ex) { Log.AppendLine($"INFO  offline translation could not be tried: {ex.GetType().Name}: {ex.Message}"); }
            App.Settings.TranslateOn = false; App.Settings.TranslateProvider = "mymemory"; App.Settings.TranslationFolder = "";
            Translator.Reset();
        }

        // --- imported lyrics ---
        {
            var song = lib.Albums[0].Tracks[2];
            Lyrics.Remove(song);
            Lyrics.Save(song, "[00:01.00]An imported line\n[00:02.50]And another");
            var got = Lyrics.For(song);
            Check("imported lyrics are kept in the data folder and found for the song", got is { Synced: true, Source: "imported", Lines.Count: 2 } && Lyrics.StorePath(song).StartsWith(Path.Combine(AppPaths.DataDir, "lyrics")), Lyrics.StorePath(song));
            var serverSong = new Track { Source = "plex", SourceId = "99", Location = "x", Title = "Server song" };
            Lyrics.Save(serverSong, "Just words\nSecond line");
            Check("imported lyrics work for a song on a server", Lyrics.For(serverSong) is { Synced: false, Lines.Count: 2 });
            Lyrics.Remove(serverSong);
            Lyrics.Remove(song);
            Check("removing imported lyrics", !Lyrics.HasStored(song) && Lyrics.For(song) == null);
            var picked = LyricsFinder.PickFrom(J.Parse("""[{"duration":200,"plainLyrics":"wrong length"},{"duration":321,"plainLyrics":"plain"},{"duration":319,"syncedLyrics":"[00:01.00]timed","plainLyrics":"timed"}]"""), 320);
            Check("online lyrics: the same length, timed ones first", picked == "[00:01.00]timed", picked ?? "none");
            try
            {
                var live = await LyricsFinder.FindAsync(new Track { Title = "One More Time", Artist = "Daft Punk", Album = "Discovery", DurationMs = 320000 });
                var liveLyrics = live == null ? null : Lyrics.Parse(live, "lrclib");
                Log.AppendLine($"INFO  lrclib live: {(liveLyrics == null ? "nothing found" : $"{liveLyrics.Lines.Count} lines, timed {liveLyrics.Synced}")}");
            }
            catch (Exception ex) { Log.AppendLine($"INFO  lrclib live probe unavailable: {ex.Message}"); }
        }

        // --- moving the data folder ---
        {
            var movedTo = Path.Combine(Path.GetDirectoryName(AppPaths.DataDir.TrimEnd('\\'))!, "data-moved");
            if (Directory.Exists(movedTo)) Directory.Delete(movedTo, true);
            File.WriteAllText(Path.Combine(AppPaths.ArtDir, "move-test.jpg"), "a cover");
            var failedCopies = await Storage.MoveToAsync(movedTo, _ => { });
            File.Delete(Path.Combine(AppPaths.ArtDir, "move-test.jpg"));
            var copied = new Database(Path.Combine(movedTo, "library.db"));
            var note = File.Exists(AppPaths.PointerFile) ? File.ReadAllText(AppPaths.PointerFile) : "";
            Check("moving the data folder copies the library, settings and covers", failedCopies == 0 && copied.LoadTracks().Count == lib.Tracks.Count && File.Exists(Path.Combine(movedTo, "settings.json"))
                && File.Exists(Path.Combine(movedTo, "art", "move-test.jpg")),
                $"{copied.LoadTracks().Count} songs, {CountFiles(Path.Combine(movedTo, "art"))} covers, {failedCopies} failed");
            Check("the new place is noted for the next start, and the old one for clearing afterwards", note.Contains("folder=" + movedTo) && note.Contains("cleanup=" + AppPaths.DataDir.TrimEnd('\\')) && note.Contains("was=" + AppPaths.DataDir.TrimEnd('\\')), note.Replace("\r\n", " | "));
            File.Delete(AppPaths.PointerFile);
            var refused = false;
            try { await Storage.MoveToAsync(Path.Combine(AppPaths.DataDir, "inside"), _ => { }); } catch { refused = true; }
            Check("a folder inside the data folder is refused", refused && !File.Exists(AppPaths.PointerFile));
        }

        // --- metadata sources ---
        {
            var mb = MusicBrainzSource.ParseSearch(J.Parse("""{"releases":[{"id":"abc","title":"Blue Train","date":"1957-09-15","country":"US","artist-credit":[{"name":"John Coltrane"}],"label-info":[{"label":{"name":"Blue Note"}}],"media":[{"format":"CD"}]}]}"""));
            Check("musicbrainz search results are read", mb.Count == 1 && mb[0] is { Key: "abc", Title: "Blue Train", Artist: "John Coltrane", Year: "1957", Label: "Blue Note", Format: "CD" } && mb[0].Thumb.EndsWith("/release/abc/front-250"));
            var mbRel = MusicBrainzSource.ParseRelease(J.Parse("""{"id":"abc","title":"Blue Train","date":"1997-01-01","artist-credit":[{"name":"John Coltrane"}],"release-group":{"first-release-date":"1957-09","genres":[{"name":"hard bop","count":9}]},"genres":[{"name":"jazz","count":20}],"media":[{"tracks":[{"title":"Blue Train","length":643000},{"title":"Moment's Notice","length":550000}]}]}"""));
            Check("musicbrainz release: first release year, top genre, tracks, cover address", mbRel is { Title: "Blue Train", Artist: "John Coltrane", Year: 1957, Genre: "Jazz", Style: "Hard bop" } && mbRel.Tracks.Count == 2 && mbRel.Tracks[1].Title == "Moment's Notice" && mbRel.CoverUrl.EndsWith("/release/abc/front"),
                $"{mbRel.Year} {mbRel.Genre} {mbRel.Tracks.Count} tracks");
            var it = ITunesSource.ParseRelease(J.Parse("""{"results":[{"wrapperType":"collection","collectionName":"Kind of Blue","artistName":"Miles Davis","releaseDate":"1959-08-17T07:00:00Z","primaryGenreName":"Jazz","artworkUrl100":"https://x.invalid/a/100x100bb.jpg"},{"wrapperType":"track","trackName":"Freddie Freeloader","discNumber":1,"trackNumber":2,"trackTimeMillis":586000},{"wrapperType":"track","trackName":"So What","discNumber":1,"trackNumber":1,"trackTimeMillis":562000}]}"""));
            Check("itunes release: big artwork and tracks in order", it is { Title: "Kind of Blue", Artist: "Miles Davis", Year: 1959, Genre: "Jazz" } && it.CoverUrl.EndsWith("/1200x1200bb.jpg") && it.Tracks[0].Title == "So What", it.CoverUrl);
            var dz = DeezerSource.ParseRelease(J.Parse("""{"title":"Discovery","artist":{"name":"Daft Punk"},"release_date":"2001-03-07","genres":{"data":[{"name":"Dance"}]},"cover_xl":"https://x.invalid/xl.jpg","tracks":{"data":[{"title":"One More Time","duration":320}]}}"""));
            Check("deezer release is read", dz is { Title: "Discovery", Artist: "Daft Punk", Year: 2001, Genre: "Dance", CoverUrl: "https://x.invalid/xl.jpg" } && dz.Tracks[0].Duration == "5:20");
            var lf = LastFmSource.ParseRelease(J.Parse("""{"album":{"name":"OK Computer","artist":"Radiohead","image":[{"#text":"s.png","size":"small"},{"#text":"xl.png","size":"extralarge"}],"tags":{"tag":[{"name":"1997"},{"name":"alternative rock"},{"name":"albums I own"},{"name":"rock"}]},"tracks":{"track":[{"name":"Airbag","duration":284}]}}}"""));
            Check("last.fm release: genre from tags, skipping years and personal tags", lf is { Title: "OK Computer", Genre: "Alternative rock", Style: "Rock", CoverUrl: "xl.png" } && lf.Tracks.Count == 1, lf.Genre);
            var adb = AudioDbSource.ParseSearch(J.Parse("""{"album":[{"idAlbum":"77","strAlbum":"Thriller","strArtist":"Michael Jackson","intYearReleased":"1982","strLabel":"Epic","strAlbumThumb":"https://x.invalid/t.jpg"}]}"""));
            Check("theaudiodb search results are read", adb.Count == 1 && adb[0] is { Key: "77", Title: "Thriller", Year: "1982" });
            var fa = FanartSource.ParseCovers(J.Parse("""{"albums":{"g1":{"albumcover":[{"url":"https://x.invalid/c1.jpg","likes":"3"},{"url":"https://x.invalid/c2.jpg","likes":"0"}]}}}"""), "g1", "Thriller", "Michael Jackson", "1982");
            Check("fanart.tv covers are read", fa.Count == 2 && fa[0].Key == "https://x.invalid/c1.jpg" && fa[0].Format == "3 likes");
            var ac = Metadata.ParseAcoustId(J.Parse("""{"status":"ok","results":[{"score":0.4,"recordings":[{"title":"Wrong"}]},{"score":0.97,"recordings":[{"title":"Hey Jude","artists":[{"name":"The Beatles"}],"releasegroups":[{"type":"Single","title":"Hey Jude"},{"type":"Album","title":"1"}]}]}]}"""));
            Check("acoustid answer: best score, album before single", ac is { Title: "Hey Jude", Artist: "The Beatles", Album: "1" } && ac.Score > 0.9);

            MetaHit Hit(string title, string artist) => new("x", "1", title, artist, "", "", "", "", "");
            Check("album matching ignores case, punctuation, 'the' and edition notes", Metadata.Fit(Hit("The Dark Side Of The Moon (Remastered 2011)", "Pink Floyd"), "pink floyd", "Dark Side of the Moon") == 1
                && Metadata.Fit(Hit("Pink Floyd - The Wall", ""), "Pink Floyd", "The Wall") == 1 && Metadata.Fit(Hit("The Wall", "Someone Else"), "Pink Floyd", "The Wall") == 0
                && Metadata.Fit(Hit("Animals", "Pink Floyd"), "Pink Floyd", "The Wall") == 0);

            // weights decide the order; a source without its key is left out
            foreach (var src in Metadata.Sources) { Metadata.Setting(src.Id).Enabled = true; }
            Check("sources that need a key wait for it", !Metadata.Ranked().Any(x => x.Id is "lastfm" or "theaudiodb" or "fanart" or "discogs") && Metadata.Ranked().Count == 4, string.Join(", ", Metadata.Ranked().Select(x => x.Id)));
            Metadata.Setting("deezer").Weight = 10; Metadata.Setting("folder").Weight = 9; Metadata.Setting("musicbrainz").Weight = 2; Metadata.Setting("itunes").Weight = 6;
            Check("sources are asked in order of weight", string.Join(",", Metadata.Ranked().Select(x => x.Id)) == "deezer,folder,itunes,musicbrainz", string.Join(",", Metadata.Ranked().Select(x => x.Id)));
            Metadata.Setting("itunes").Enabled = false;
            Check("a source switched off is not asked", !Metadata.Ranked().Any(x => x.Id == "itunes"));
            Metadata.Setting("itunes").Enabled = true;
            Metadata.Setting("deezer").Weight = 5; Metadata.Setting("folder").Weight = 10; Metadata.Setting("musicbrainz").Weight = 8;

            // an album tied to one source, and filling a missing cover from the folder (no network)
            var artDir = Path.Combine(AppPaths.DataDir, "meta-test");
            Directory.CreateDirectory(artDir);
            var coverFile = Path.Combine(artDir, "cover.jpg");
            File.WriteAllBytes(coverFile, Enumerable.Range(0, 2000).Select(i => (byte)i).ToArray());
            File.WriteAllBytes(Path.Combine(artDir, "booklet.png"), new byte[900]);
            var bare = new Album { Key = "meta\u001ftest", Title = "Tied Album", Artist = "Nobody", Year = 1999, Genre = "Rock", Tracks = new() { new Track { Location = Path.Combine(artDir, "01.mp3"), Title = "One" } } };
            Check("an album starts on automatic", Metadata.SourceOf(bare) == Metadata.Auto && Metadata.For(bare).Count == Metadata.Ranked().Count);
            Metadata.SetSource(bare, "folder");
            Check("an album can be tied to one source", Metadata.SourceOf(bare) == "folder" && Metadata.For(bare).Select(x => x.Id).SequenceEqual(new[] { "folder" }));
            var folderHits = await Metadata.Find("folder")!.SearchAsync(bare, bare.Artist, bare.Title);
            Check("folder pictures are offered, cover.jpg first", folderHits.Count == 2 && folderHits[0].Key == coverFile, string.Join(", ", folderHits.Select(h => Path.GetFileName(h.Key))));
            var filled = await Metadata.FillAsync(bare);
            Check("a missing cover is filled from the album's source, and what it already has is kept", filled is { Year: 0, Genre: "" } && filled.Cover is { Length: 2000 } && filled.From.SequenceEqual(new[] { "Pictures in the album's folder" }),
                $"cover {filled.Cover?.Length} bytes from {string.Join(", ", filled.From)}");
            Metadata.SetSource(bare, Metadata.Never);
            Check("an album set to never is left alone", Metadata.For(bare).Count == 0 && !(await Metadata.FillAsync(bare)).Any);
            Metadata.SetSource(bare, Metadata.Auto);
            Check("back to automatic", !App.Settings.AlbumSources.ContainsKey(bare.Key));

            // keys are kept encrypted in the settings file
            var probe = new Settings { ApiKeys = { ["lastfm"] = "secret-key" } };
            var savedJson = System.Text.Json.JsonSerializer.Serialize(probe);
            Check("api keys are not written to settings in the clear", !savedJson.Contains("secret-key"));

            var infoMenu = Menus.InfoSource(lib.Albums[0]);
            Check("album menu offers every source, automatic and never", infoMenu.Items.OfType<MenuItem>().Count() == Metadata.Sources.Length + 2 && infoMenu.Items.OfType<MenuItem>().First().IsChecked);
            var dialog = new DiscogsDialog(lib.Albums[0]);
            Check("the album info dialog offers every ready source and opens on the most trusted online one", dialog.OfferedSources.Count == 4 && dialog.ChosenSource?.Id == "musicbrainz", $"{dialog.OfferedSources.Count} sources, opens on {dialog.ChosenSource?.Id}");
            dialog.Close();

            // live probes (informational): the three sources that need no key
            foreach (var id in new[] { "musicbrainz", "itunes", "deezer" })
            {
                try
                {
                    var src = Metadata.Find(id)!;
                    var hits = await src.SearchAsync(null, "Daft Punk", "Discovery");
                    var best = hits.OrderByDescending(h => Metadata.Fit(h, "Daft Punk", "Discovery")).First();
                    var liveRel = await src.GetAsync(best);
                    var bytes = 0;
                    try { bytes = (await Metadata.GetBytesAsync(liveRel.CoverUrl)).Length; } catch { }
                    Log.AppendLine($"INFO  {id} live: {hits.Count} hits, best \"{best.Title}\" by {best.Artist} (fit {Metadata.Fit(best, "Daft Punk", "Discovery"):0.0}), {liveRel.Year}, genre {liveRel.Genre}, {liveRel.Tracks.Count} tracks, cover {bytes} bytes");
                }
                catch (Exception ex) { Log.AppendLine($"INFO  {id} live probe unavailable: {ex.Message}"); }
            }
        }

        // --- tags ---
        var tagged = lib.Albums[1].Tracks.Take(3).ToList();
        var untagged = lib.Tracks.Except(tagged).ToList();
        lib.ApplyTags(Library.SongTag, tagged.Select(t => t.Id), new[] { "Road Trip", "#Workout", " rainy  day " }, Array.Empty<string>());
        Check("tags are normalised", lib.TagsOf(Library.SongTag, tagged[0].Id).Order().SequenceEqual(new[] { "rainy day", "road trip", "workout" }), lib.TagText(Library.SongTag, tagged[0].Id));
        Check("tags persist in the database", new Database(AppPaths.DbPath).LoadTags().Count(x => x.Kind == "song") == 9);
        Check("#tag matches by prefix", lib.Search("#road").Songs.Count == 3 && lib.Search("#road").Songs.All(t => tagged.Contains(t)));
        Check("#\"two words\" tag", lib.Search("#\"road trip\"").Songs.Count == 3 && lib.Search("#\"rainy da\"").Songs.Count == 3 && lib.Search("#\"day rainy\"").Songs.Count == 0);
        Check("plain words match tags too", lib.Search("workout").Songs.Count == 3 && lib.Search("workout").Albums.Any(a => a.Key == lib.Albums[1].Key));
        Check("tags combine with other words", lib.Search("#workout song").Songs.Count == 3 && lib.Search("#workout zzzz").Songs.Count == 0 && lib.Search("#nothing").Songs.Count == 0);
        Check("untagged songs are not matched by #tag", !lib.Search("#workout").Songs.Any(untagged.Contains));
        var tagList = lib.Search("#").Tags;
        Check("# lists your tags with counts", tagList.Count == 3 && tagList.All(t => t.Count == 3), string.Join(", ", tagList.Select(t => $"{t.Tag} {t.Count}")));
        Check("search offers matching tags", lib.Search("road").Tags.Any(t => t.Tag == "road trip"));
        lib.ApplyTags(Library.SongTag, tagged.Take(1).Select(t => t.Id), Array.Empty<string>(), new[] { "workout" });
        Check("removing a tag from one song", lib.Search("#workout").Songs.Count == 2 && lib.CommonTags(Library.SongTag, tagged.Select(t => t.Id)).SequenceEqual(new[] { "rainy day", "road trip" }));
        Check("tag shows in the song list", tagged[1].TagText == "rainy day, road trip, workout");
        Check("TagText.Query quotes spaces", TagText.Query("road trip") == "#\"road trip\"" && TagText.Query("jazz") == "#jazz");

        // --- sorting ---
        var sort = new SongSort();
        var byTitle = lib.Tracks.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ThenBy(t => lib.Tracks.IndexOf(t)).Select(t => t.Id).ToList();
        sort.Click("title");
        Check("sort ascending", sort.Apply(lib.Tracks).Select(t => t.Id).SequenceEqual(byTitle));
        sort.Click("title");
        Check("click again reverses", sort.Descending && sort.Apply(lib.Tracks).First().Title == lib.Tracks.Max(t => t.Title));
        sort.Click("title");
        Check("third click restores the original order", !sort.Active && sort.Apply(lib.Tracks).SequenceEqual(lib.Tracks));
        sort.Click("artist");
        var artistSorted = sort.Apply(lib.Tracks);
        Check("artist sort keeps album and track order inside an artist", artistSorted.Zip(artistSorted.Skip(1)).All(p => string.Compare(p.First.Artist, p.Second.Artist, StringComparison.OrdinalIgnoreCase) <= 0
            && (p.First.Artist != p.Second.Artist || string.Compare(p.First.Album, p.Second.Album, StringComparison.OrdinalIgnoreCase) < 0 || (p.First.Album == p.Second.Album && p.First.TrackNo <= p.Second.TrackNo))));
        sort.Click("tags");
        var byTags = sort.Apply(lib.Tracks);
        Check("tag sort puts tagged songs first", byTags.Take(3).All(t => t.TagText != "") && byTags.Skip(3).All(t => t.TagText == ""));
        sort.Set("time", true);
        Check("length sort descending", sort.Apply(lib.Tracks).First().DurationMs >= sort.Apply(lib.Tracks).Last().DurationMs);
        sort.Set("plays", true);
        Check("play count sort uses the stored counts", sort.Apply(lib.Tracks).First().PlayCount == lib.Tracks.Max(t => t.PlayCount));
        sort.Set("rating", true);
        Check("rating sort puts liked songs first", sort.Apply(lib.Tracks).First().Rating == 1);
        win.Navigate("music");
        var musicView = win.Page<MusicView>("music");
        musicView.SetMode("songs");
        musicView.Header.SortBy("artist", true);
        await Shot("26-songs-sorted");
        musicView.Header.Reset();
        musicView.SetMode("artists");
        win.Navigate("search");
        win.Page<SearchView>("search").Search("#road");
        await Shot("27-search-by-tag");

        // --- books: audiobooks and ebooks ---
        var bookDir = Path.Combine(_dir, "books");
        if (Directory.Exists(bookDir)) Directory.Delete(bookDir, true);
        var audioBookDir = Path.Combine(bookDir, "Jane Writer", "The Long Road");
        Directory.CreateDirectory(audioBookDir);
        foreach (var n in new[] { 10, 1, 2 })   // written out of order: the scan must sort 1, 2, 10
        {
            var f = Path.Combine(audioBookDir, $"Chapter {n}.wav");
            WriteWav(f, 4);
            using var tf = TagLib.File.Create(f);
            tf.Tag.Title = $"Part {n}"; tf.Tag.Album = "The Long Road"; tf.Tag.AlbumArtists = new[] { "Jane Writer" };
            tf.Save();
        }
        var epubFile = Path.Combine(bookDir, "quiet.epub");
        MakeEpub(epubFile);
        File.WriteAllText(Path.Combine(bookDir, "Manual.pdf"), "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 300]>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF");
        var cbzFile = Path.Combine(bookDir, "Hero Tales 3.cbz");
        var cbrFile = Path.Combine(bookDir, "Space Gang.cbr");
        var comicPages = MakeComics(cbzFile, cbrFile);
        App.Settings.BookFolders = new() { bookDir };
        await lib.ScanAsync(_ => { });
        var audioBook = lib.Books.FirstOrDefault(b => b.IsAudio);
        var ebook = lib.Books.FirstOrDefault(b => b.Format == "epub");
        Check("books scanned", lib.Books.Count == 5 && lib.Books.Any(b => b.Format == "pdf" && b.Title == "Manual"), $"{lib.Books.Count} books");
        Check("audiobook grouped and ordered", audioBook is { Title: "The Long Road", Author: "Jane Writer" } && audioBook.Parts.Select(x => x.Title).SequenceEqual(new[] { "Part 1", "Part 2", "Part 10" })
                                                && Math.Abs(audioBook.DurationMs - 12000) < 600, audioBook == null ? "missing" : string.Join(", ", audioBook.Parts.Select(x => x.Title)));
        Check("epub metadata and cover", ebook is { Title: "A Quiet Book", Author: "Sam Author" } && File.Exists(ebook.ArtPath));
        var parsed = Epub.Open(epubFile);
        Check("epub chapters and titles", parsed.Chapters.Select(c => c.Title).SequenceEqual(new[] { "Opening", "The Middle", "The End" }) && parsed.Chapters[1].Href == "OEBPS/text/ch2.xhtml",
            string.Join(" | ", parsed.Chapters.Select(c => c.Title + "=" + c.Href)));
        await lib.ScanAsync(_ => { });
        Check("book rescan is idempotent", lib.Books.Count == 5);

        // --- comics (.cbz zip, .cbr rar) ---
        var cbz = lib.Books.FirstOrDefault(b => b.Format == "comic" && b.SourceId == cbzFile);
        var cbr = lib.Books.FirstOrDefault(b => b.Format == "comic" && b.SourceId == cbrFile);
        Check("cbz metadata from ComicInfo.xml and cover", cbz is { Title: "Hero Tales #3", Author: "Pat Pencil", Series: "Hero Tales" } && File.Exists(cbz.ArtPath), cbz == null ? "missing" : $"{cbz.Title} / {cbz.Author} / {cbz.Series}");
        Check("cbr is picked up", cbr is { Title: "Space Gang" } && File.Exists(cbr.ArtPath), cbr == null ? "missing" : cbr.Title);
        var cbzDir = Path.Combine(_dir, "comic-test-zip");
        var cbrDir = Path.Combine(_dir, "comic-test-rar");
        var zipPages = Comic.Pages(cbzFile, cbzDir);
        var rarPages = Comic.Pages(cbrFile, cbrDir);
        Check("cbz pages in reading order (page2 before page10)", zipPages.Count == 3 && zipPages.Select(File.ReadAllBytes).Zip(comicPages.zip).All(t => t.First.SequenceEqual(t.Second)), $"{zipPages.Count} pages");
        Check("cbr pages in reading order", rarPages.Count == 3 && rarPages.Select(File.ReadAllBytes).Zip(comicPages.rar).All(t => t.First.SequenceEqual(t.Second)), $"{rarPages.Count} pages");
        if (cbz != null)
        {
            var comicWin = ComicWindow.Open(cbz);
            await Task.WhenAny(comicWin.Opening ?? Task.Delay(1), Task.Delay(15000));
            var openedAt = comicWin.Pages;
            comicWin.Show(1);
            Check("comic reader opens and turns pages", openedAt == 3 && comicWin.Page == 1 && cbz.Part == 1 && Math.Abs(cbz.Progress - 2.0 / 3) < 0.01, $"{openedAt} pages, now on {comicWin.Page + 1}");
            comicWin.Show(99);
            Check("comic reader stops at the last page and marks it finished", comicWin.Page == 2 && cbz.Finished);
            comicWin.Close();
            var savedComic = new Database(AppPaths.DbPath).LoadBooks().Single(b => b.Id == cbz.Id);
            Check("comic place is saved", savedComic.Part == 2 && savedComic.Finished);
            await Task.Delay(100);
        }

        if (audioBook != null)
        {
            App.Player.Shuffle = true;   // a book must still play in order
            App.Player.PlayBook(audioBook);
            await Task.Delay(1500);
            Check("audiobook starts at part 1", App.Player.CurrentIndex == 0 && App.Player.Position.TotalMilliseconds > 500);
            App.Player.Next();
            await Task.Delay(1800);
            App.Player.Stop();
            var saved = new Database(AppPaths.DbPath).LoadBooks().Single(b => b.Id == audioBook.Id);
            Check("audiobook position saved", saved.Part == 1 && saved.PositionMs > 900 && saved.Progress is > 0.34 and < 0.6, $"part {saved.Part}, {saved.PositionMs} ms, {saved.Progress:P0}");
            App.Player.PlayBook(audioBook);
            await Task.Delay(1500);
            Check("audiobook resumes where it stopped", App.Player.CurrentIndex == 1 && App.Player.Position.TotalMilliseconds > saved.PositionMs - 200,
                $"index {App.Player.CurrentIndex}, {App.Player.Position.TotalMilliseconds:0} ms");
            App.Player.PlayAt(2);
            await Task.Delay(900);
            App.Player.Seek(TimeSpan.FromSeconds(3.3));
            await Task.Delay(2200);
            Check("audiobook finishes", audioBook.Finished && audioBook.Progress == 1 && App.Player.Current == null, $"finished {audioBook.Finished}");
            App.Player.Shuffle = false;
        }

        if (ebook != null)
        {
            var reader = ReaderWindow.Open(ebook);
            var opened = await Task.WhenAny(reader.Ready, Task.Delay(30000)) == reader.Ready && reader.Ready.IsCompletedSuccessfully;
            Check("reader opens the book", opened && reader.Chapter == 0, opened ? $"{reader.Pages} page(s) in chapter 1" : reader.Ready.Exception?.InnerException?.Message ?? "timed out");
            if (opened)
            {
                for (var i = 0; i < 12 && reader.Chapter == 0; i++) { await reader.NextAsync(); await Task.Delay(500); }
                Check("reader turns into the next chapter", reader.Chapter == 1 && reader.Page == 0);
                Check("long chapter is split into pages", reader.Pages >= 3, $"{reader.Pages} pages");
                await reader.NextAsync(); await Task.Delay(400);
                await reader.NextAsync(); await Task.Delay(600);
                var at = (reader.Chapter, reader.Page);
                using (var png = File.Create(Path.Combine(_dir, "25-reader.png")))
                    await reader.Core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, png);
                await reader.PrevAsync(); await Task.Delay(400);
                await reader.PrevAsync(); await Task.Delay(400);
                await reader.PrevAsync(); await Task.Delay(900);
                Check("turning back lands on the previous chapter's last page", reader.Chapter == 0 && reader.Page == reader.Pages - 1, $"chapter {reader.Chapter}, page {reader.Page + 1} of {reader.Pages}");
                for (var i = 0; i < 12 && reader.Chapter == 0; i++) { await reader.NextAsync(); await Task.Delay(500); }
                await reader.NextAsync(); await Task.Delay(400);
                await reader.NextAsync(); await Task.Delay(600);
                reader.Close();
                await Task.Delay(300);
                var savedBook = new Database(AppPaths.DbPath).LoadBooks().Single(b => b.Id == ebook.Id);
                Check("reading place saved", at == (1, 2) && savedBook.Part == 1 && savedBook.Fraction > 0 && savedBook.Progress is > 0.33 and < 0.67, $"chapter {savedBook.Part}, {savedBook.Progress:P0}");
                var reader2 = ReaderWindow.Open(ebook);
                var reopened = await Task.WhenAny(reader2.Ready, Task.Delay(30000)) == reader2.Ready;
                await Task.Delay(700);
                Check("reader reopens at the saved page", reopened && (reader2.Chapter, reader2.Page) == at, $"chapter {reader2.Chapter}, page {reader2.Page + 1}");
                reader2.Close();
            }
        }
        win.Navigate("books");
        await Shot("24-books");
        if (ebook != null && audioBook != null)
        {
            lib.ApplyTags(Library.BookTag, new[] { ebook.Id, audioBook.Id }, new[] { "road trip" }, Array.Empty<string>());
            lib.ApplyTags(Library.VideoTag, new[] { lib.Videos[0].Id }, new[] { "road trip", "family" }, Array.Empty<string>());
            var found = lib.Search("#\"road trip\"");
            Check("search finds tagged videos and books", found.Videos.Count == 1 && found.Books.Count == 2 && found.Songs.Count == 3, $"{found.Songs.Count} songs, {found.Videos.Count} videos, {found.Books.Count} books");
            Check("book and video tags persist", new Database(AppPaths.DbPath).LoadTags().Count(x => x.Kind is "book" or "video") == 4);
            await lib.ScanAsync(_ => { });
            Check("tags survive a rescan", lib.Search("#family").Videos.Count == 1 && lib.Search("#road").Songs.Count == 3);
            var reopened = new Library(new Database(AppPaths.DbPath));
            reopened.Load();
            Check("tags load back with the library", reopened.TagsOf(Library.SongTag, tagged[1].Id).Count == 3 && reopened.TagsOf(Library.BookTag, ebook.Id).Count == 1);
            win.Navigate("search");
            win.Page<SearchView>("search").Search("road trip");
            await Shot("28-search-books-videos");
        }

        // --- bookmarks ---
        if (cbz != null && audioBook != null)
        {
            var m1 = lib.AddBookmark(cbz, 2, 0);
            var m2 = lib.AddBookmark(cbz, 1, 0, "  The fight  ");
            Check("bookmarks listed in reading order with a default or custom label", lib.BookmarksOf(cbz).Select(m => m.Label).SequenceEqual(new[] { "The fight", "Page 3" }), string.Join(" | ", lib.BookmarksOf(cbz).Select(m => m.Label)));
            lib.RenameBookmark(m2, "Big reveal");
            Check("bookmarks persist and reload with the library", new Database(AppPaths.DbPath).LoadBookmarks().Count(m => m.BookId == cbz.Id && m.Label == "Big reveal") == 1);
            var win2 = ComicWindow.Open(cbz);
            await Task.WhenAny(win2.Opening ?? Task.Delay(1), Task.Delay(15000));
            win2.Show(0);
            BooksView.OpenBookmark(cbz, m1);
            Check("jumping to a comic bookmark turns the open reader to that page", win2.Page == 2 && cbz.Part == 2, $"on page {win2.Page + 1}");
            win2.Close();

            var am = lib.AddBookmark(audioBook, 1, 1500);
            Check("audiobook bookmark has a readable default label", am.Label.Contains("Part 2") && am.Label.Contains("1"), am.Label);
            App.Player.PlayBook(audioBook, restart: true);
            await Task.Delay(1200);
            BooksView.OpenBookmark(audioBook, am);
            await Task.Delay(1400);
            Check("jumping to an audiobook bookmark plays from that file and place while the book is playing", App.Player.CurrentIndex == 1 && App.Player.Position.TotalMilliseconds is > 1200 and < 3800, $"file {App.Player.CurrentIndex + 1}, {App.Player.Position.TotalMilliseconds:0} ms");
            App.Player.Stop();
            lib.DeleteBookmark(m1);
            Check("deleting a bookmark removes it from the list and the database", lib.BookmarksOf(cbz).Count == 1 && new Database(AppPaths.DbPath).LoadBookmarks().All(m => m.Id != m1.Id));
            Check("a book's bookmarks are counted", lib.BookmarkCount(cbz) == 1 && lib.BookmarkCount(audioBook) == 1);
            var newSession = new Library(new Database(AppPaths.DbPath));
            newSession.Load();
            var inDb = new Database(AppPaths.DbPath).LoadBookmarks().Count(m => m.BookId == cbz.Id);
            var first = newSession.AddBookmark(cbz, 0, 0, "first in a new session");
            Check("the first bookmark added in a session is listed once", newSession.BookmarkCount(cbz) == inDb + 1, $"{newSession.BookmarkCount(cbz)} listed, {inDb} were saved");
            newSession.DeleteBookmark(first);
            if (ebook != null)
            {
                var em = lib.AddBookmark(ebook, 1, 0.5);
                var reader = ReaderWindow.Open(ebook);
                await Task.WhenAny(reader.Ready, Task.Delay(30000));
                BooksView.OpenBookmark(ebook, em);
                await Task.Delay(2500);
                Check("jumping to an ebook bookmark turns the open reader to that chapter", reader.Chapter == 1, $"chapter {reader.Chapter + 1}");
                reader.Close();
                await Task.Delay(300);
            }
        }

        // --- disc: the encoders used for ripping and the decode step used for burning (no drive needed) ---
        var wav = Path.Combine(_dir, "enc-test.wav");
        using (var w = new NAudio.Wave.WaveFileWriter(wav, CdAudio.CdFormat))
        {
            var tone = new byte[CdAudio.SectorBytes * 75 * 3];   // 3 seconds
            for (var i = 0; i < tone.Length / 4; i++)
            {
                var sample = (short)(Math.Sin(i * 2 * Math.PI * 440 / 44100) * 8000);
                tone[i * 4] = tone[i * 4 + 2] = (byte)sample; tone[i * 4 + 1] = tone[i * 4 + 3] = (byte)(sample >> 8);
            }
            w.Write(tone, 0, tone.Length);
        }
        for (var f = 0; f < CdAudio.Formats.Length; f++)
        {
            var outFile = Path.Combine(_dir, "enc-out" + CdAudio.Extension(f));
            try
            {
                await Task.Run(() => CdAudio.Encode(wav, outFile, f));
                using var tf = TagLib.File.Create(outFile);
                tf.Tag.Title = "Encoded";
                tf.Save();
                Check($"rip encoder {CdAudio.Formats[f]}", new FileInfo(outFile).Length > 2000 && Math.Abs(tf.Properties.Duration.TotalSeconds - 3) < 0.3, $"{new FileInfo(outFile).Length} bytes, {tf.Properties.Duration.TotalSeconds:0.00}s");
            }
            catch (Exception ex) { Check($"rip encoder {CdAudio.Formats[f]}", false, ex.Message); }
        }
        var raw = Path.Combine(_dir, "burn-test.raw");
        await Task.Run(() => CdAudio.DecodeForCd(Path.Combine(_dir, "enc-out.mp3"), raw, CancellationToken.None));
        var rawLen = new FileInfo(raw).Length;
        Check("burn: decode to CD audio", rawLen % CdAudio.SectorBytes == 0 && Math.Abs(rawLen / 176400.0 - 3) < 0.3, $"{rawLen / 176400.0:0.00}s, {rawLen / CdAudio.SectorBytes} sectors");
        await Task.Run(() => CdAudio.DecodeForCd(lib.Tracks[0].SourceId, raw, CancellationToken.None));   // 8 kHz mono source is resampled
        Check("burn: resample to CD audio", new FileInfo(raw).Length % CdAudio.SectorBytes == 0 && Math.Abs(new FileInfo(raw).Length / 176400.0 - 4) < 0.3);
        Check("disc drives enumerate", CdAudio.Drives() != null, $"{CdAudio.Drives().Count} optical drive(s) on this PC");
        // --- the Zune-style phone / disc / playlist icons, disc view and settings ---
        App.BurnList.Clear();
        win.DropOn("disc", lib.Albums[0].Tracks.ToList());
        Check("dropping an album on the disc icon fills the burn list", App.BurnList.Count == lib.Albums[0].Tracks.Count && App.BurnList.SequenceEqual(lib.Albums[0].Tracks));
        win.Navigate("disc");
        var disc = win.Page<DiscView>("disc");
        await Task.Delay(300);
        Check("disc view opens on the burn list", ((FrameworkElement)disc.FindName("BurnPane")).IsVisible && !((FrameworkElement)disc.FindName("CdPane")).IsVisible);
        await Shot("19-disc");
        disc.ShowPivot("cd");
        Check("disc view switches to the CD", ((FrameworkElement)disc.FindName("CdPane")).IsVisible && !((FrameworkElement)disc.FindName("BurnPane")).IsVisible);
        await Shot("19-disc-cd");
        disc.ShowPivot("burn");

        var localSongs = lib.Tracks.Where(t => t.Source == "local").Take(3).ToList();
        var remoteSong = new Track { Id = -99, Source = "jellyfin", Title = "Remote only" };
        win.DropOn("phone", localSongs.Append(remoteSong).ToList());
        var phoneList = PhoneView.List;
        Check("dropping songs on the phone icon builds the phone sync list (local songs only)", phoneList != null && lib.TracksOf(phoneList).Select(t => t.Id).SequenceEqual(localSongs.Select(t => t.Id)), phoneList == null ? "no list" : $"{phoneList.TrackIds.Count} songs");
        win.DropOn("phone", localSongs.Take(2).ToList());
        Check("songs already on the phone sync list are not added twice", PhoneView.List!.TrackIds.Count == 3);
        Check("the phone sync list is saved as a playlist", new Database(AppPaths.DbPath).LoadPlaylists().Any(pl => pl.Name == PhoneView.ListName && pl.TrackIds.Count == 3));
        win.Navigate("phone");
        await Shot("19-phone");

        win.Navigate("settings");
        var settings = win.Page<SettingsView>("settings");
        bool Shown(string panel) => ((FrameworkElement)settings.FindName(panel)).IsVisible;
        settings.ShowCategory("software");
        await Task.Delay(300);
        Check("settings open on Software, Collection", Shown("PanelCollection") && !Shown("PanelRip") && !Shown("PanelJellyfin"));
        await Shot("12-settings");
        settings.ShowCategory("software", "Storage");
        Check("settings: Storage shows where the data is kept", Shown("PanelStorage") && settings.StorageShown == AppPaths.DataDir);
        await Shot("12-settings-storage");
        settings.ShowCategory("software", "Translation");
        Check("settings: Translation offers languages and services", Shown("PanelTranslation") && settings.TranslateLanguageCount == Translator.Languages.Length);
        await Shot("12-settings-translation");
        settings.ShowCategory("servers", "Streaming");
        Check("settings: Servers lists the streaming services, all off to begin with", Shown("PanelWeb") && !Shown("PanelPlex") && settings.WebChoices == WebServices.Presets.Length
            && WebServices.Presets.All(x => !WebServices.IsOn(x)) && win.WebTabs.Count == 0, $"{settings.WebChoices} services");
        await Shot("12-settings-streaming");
        Check("an older 'show spotify' setting carries over", System.Text.Json.JsonSerializer.Deserialize<Settings>("{\"SpotifyEnabled\":true}") is { } old && !old.WebServicesOn.Contains("spotify"));
        Check("addresses: a site, a full address, a file on this PC", WebServices.Address("example.com/app") == "https://example.com/app" && WebServices.Address("http://localhost:8080") == "http://localhost:8080"
            && WebServices.Address(@"C:\apps\player.html") == "file:///C:/apps/player.html");

        // music services get a tab after playlists; video services a link on the videos page
        var spotifyService = WebServices.Find("spotify")!;
        WebServices.Set(spotifyService, true);
        WebServices.Set(WebServices.Find("ytmusic")!, true);
        WebServices.Set(WebServices.Find("netflix")!, true);
        var localApp = Path.Combine(AppPaths.DataDir, "my-app.html");
        File.WriteAllText(localApp, "<html><head><title>My local app</title></head><body><h1>My local app</h1></body></html>");
        Check("your own page can be added by file path", settings.AddCustomService("My app", localApp, video: false) && !settings.AddCustomService("", "", false) && !settings.AddCustomService("Missing", @"C:\no\such\file.html", false));
        win.ShowWebTabs();
        Check("music services switched on get tabs after playlists", win.WebTabs.Count == 3 && win.WebTabs[0] == "web:spotify" && win.WebTabs[1] == "web:ytmusic" && win.WebTabs[2].StartsWith("web:custom-"), string.Join(", ", win.WebTabs));
        win.Navigate("videos");
        await Task.Delay(300);
        Check("video services switched on get a link on the videos page", win.Page<VideosView>("videos").ServiceLinks.SequenceEqual(new[] { "web:netflix" }), string.Join(", ", win.Page<VideosView>("videos").ServiceLinks));
        await Shot("13-videos-with-services");

        win.Navigate(win.WebTabs[2]);
        var mine = win.Page<WebServiceView>(win.WebTabs[2]);
        var openingMine = System.Diagnostics.Stopwatch.StartNew();
        while (!mine.Ready && openingMine.ElapsedMilliseconds < 20000) await Task.Delay(200);
        await Task.Delay(500);
        Check("a local HTML page opens in its tab", mine.Ready && mine.PageTitle == "My local app" && mine.Address.StartsWith("file:///"), $"{mine.PageTitle} at {mine.Address}");

        win.Navigate("web:spotify");
        var spotify = win.Page<WebServiceView>("web:spotify");
        var opening = System.Diagnostics.Stopwatch.StartNew();
        while (!spotify.Ready && opening.ElapsedMilliseconds < 25000) await Task.Delay(200);
        await Task.Delay(1500);
        Check("the spotify tab opens the web player page", win.CurrentPage == "web:spotify");
        Log.AppendLine($"INFO  spotify page: ready {spotify.Ready} after {opening.ElapsedMilliseconds} ms, address {spotify.Address}, title \"{spotify.PageTitle}\"");
        await Shot("13-spotify");

        // Phoenix's controls work a web player. A stand-in page with Spotify's control names counts what is pressed;
        // a second stand-in has only a plain audio element and the browser's media-session hooks, like most other sites.
        await spotify.LoadTestPageAsync("""
            <html><body><footer>
            <button data-testid="control-button-playpause" onclick="window.playing=!window.playing;document.title='pp'+(++window.pp)">pp</button>
            <button data-testid="control-button-skip-forward" onclick="window.track++;set()">next</button>
            <button data-testid="control-button-skip-back" onclick="window.track--;set()">prev</button>
            <button aria-label="Enable shuffle" aria-checked="false" role="checkbox" onclick="this.setAttribute('aria-checked', this.getAttribute('aria-checked')=='true'?'false':'true')">s</button>
            <button data-testid="control-button-repeat" aria-checked="false" onclick="var o=['false','true','mixed'];this.setAttribute('aria-checked', o[(o.indexOf(this.getAttribute('aria-checked'))+1)%3])">r</button>
            <div data-testid="now-playing-widget"><button data-testid="add-button" aria-checked="false" onclick="this.setAttribute('aria-checked', this.getAttribute('aria-checked')=='true'?'false':'true')">like</button></div>
            <div data-testid="playback-progressbar"><input type="range" min="0" max="210000" value="65000" onchange="window.sought=this.value"></div>
            <div data-testid="volume-bar"><input type="range" min="0" max="1" step="0.01" value="1" onchange="window.vol=this.value"></div>
            </footer><script>
              window.pp = 0; window.track = 1; window.playing = false;
              function set() { navigator.mediaSession.metadata = new MediaMetadata({ title: 'Stand-in song ' + window.track, artist: 'Stand-in artist', album: 'Stand-in album', artwork: [{ src: 'https://x.invalid/art.png', sizes: '64x64', type: 'image/png' }] }); }
              set();
            </script></body></html>
            """);
        await spotify.AskAsync();
        Check("the web player's song, clock and state are read", spotify.State is { Title: "Stand-in song 1", Artist: "Stand-in artist", Position: 65, Duration: 210, Shuffle: 0, Repeat: 0, Liked: false, CanSeek: true, CanNext: true }, spotify.State.ToString());
        Check("a web player does not take the controls until it plays", !spotify.Active && !win.ShowingWebInBar);
        spotify.TakeControls();
        await Task.Delay(200);
        Check("while it is playing the player bar shows its song and every control it supports", win.ShowingWebInBar && win.BarTitleShown == "Stand-in song 1" && win.ShuffleShown && win.RepeatShown && win.HeartShown && win.SeekShown, win.BarTitleShown);
        App.Player.Next();
        await Task.Delay(600);
        Check("next goes to the web player", spotify.State.Title == "Stand-in song 2" && win.BarTitleShown == "Stand-in song 2", spotify.State.Title);
        App.Player.Previous();
        await Task.Delay(600);
        Check("previous goes to the web player", spotify.State.Title == "Stand-in song 1", spotify.State.Title);
        App.Player.Toggle();
        await Task.Delay(600);
        Check("play/pause goes to the web player", spotify.PageTitle == "pp1", spotify.PageTitle);
        spotify.ToggleShuffle();
        await Task.Delay(500);
        spotify.CycleRepeat();
        await Task.Delay(500);
        spotify.CycleRepeat();
        await Task.Delay(500);
        spotify.ToggleLike();
        await Task.Delay(500);
        Check("shuffle (found by its label), repeat and like go to the web player and are read back", spotify.State is { Shuffle: 1, Repeat: 2, Liked: true }, $"shuffle {spotify.State.Shuffle}, repeat {spotify.State.Repeat}, liked {spotify.State.Liked}");
        spotify.SeekTo(120);
        await Task.Delay(600);
        Check("seeking moves the web player's own progress slider", Math.Abs(spotify.State.Position - 120) < 0.01, $"{spotify.State.Position} s");
        Check("a web player's answer is read even when it is empty or broken", WebServiceView.Parse("null", false) == WebState.None && WebServiceView.Parse("garbage", false) == WebState.None);

        // Spotify as it really is (from the control names it reported): shuffle has no name and three settings, shown by
        // colour and picture; repeat is worded by what a press does next; the like button reads "Add to playlist" once liked
        await spotify.LoadTestPageAsync("""
            <html><body><footer data-testid="now-playing-bar">
            <button aria-checked="true" aria-label="Add to playlist" onclick="this.setAttribute('aria-checked','false');this.setAttribute('aria-label','Add to Liked Songs')">l</button>
            <button id="sh" aria-label="Shuffle" style="color: rgb(179,179,179)" onclick="step()"><svg><path d="M1 2 3 4"/></svg></button>
            <button data-testid="control-button-skip-back">p</button><button data-testid="control-button-playpause">pp</button><button data-testid="control-button-skip-forward">n</button>
            <button id="rp" data-testid="control-button-repeat" role="checkbox" aria-checked="false" aria-label="Enable repeat" onclick="rstep()">r</button>
            <input type="range" min="0" max="179167" value="1000"><input type="range" min="0" max="1" step="0.1" value="1">
            </footer><script>
              var n = 0, r = 0;
              function step() { n = (n + 1) % 3; var b = document.getElementById('sh');
                b.style.color = n ? 'rgb(30,215,96)' : 'rgb(179,179,179)';
                b.innerHTML = n == 2 ? '<svg><path d="M1 2 3 4"/><path d="M9 9 8 8 7 7"/></svg>' : '<svg><path d="M1 2 3 4"/></svg>'; }
              function rstep() { r = (r + 1) % 3; var b = document.getElementById('rp');
                b.setAttribute('aria-label', ['Enable repeat', 'Enable repeat one', 'Disable repeat'][r]); b.style.color = r ? 'rgb(30,215,96)' : 'rgb(179,179,179)'; }
              navigator.mediaSession.metadata = new MediaMetadata({ title: 'Real layout' });
            </script></body></html>
            """);
        await spotify.AskAsync();
        Check("spotify's real layout: shuffle off, repeat off, a liked song", spotify.State is { Shuffle: 0, Repeat: 0, Liked: true, Duration: > 179 and < 180 }, spotify.State.ToString());
        spotify.ToggleShuffle();
        await Task.Delay(500);
        var firstPress = spotify.State.Shuffle;
        var tipOn = win.ShuffleTip;
        spotify.ToggleShuffle();
        await Task.Delay(500);
        var secondPress = spotify.State.Shuffle;
        var tipSmart = win.ShuffleTip;
        spotify.ToggleShuffle();
        await Task.Delay(500);
        Check("shuffle has three settings: off, shuffle, smart shuffle", firstPress == 1 && secondPress == 2 && spotify.State.Shuffle == 0 && tipOn.StartsWith("Shuffle (") && tipSmart.StartsWith("Smart shuffle"),
            $"{firstPress}, {secondPress}, {spotify.State.Shuffle}; tips: {tipOn} / {tipSmart}");
        spotify.CycleRepeat();
        await Task.Delay(500);
        var repeatAll = spotify.State.Repeat;
        spotify.CycleRepeat();
        await Task.Delay(500);
        Check("repeat is read from spotify's wording: all, then one", repeatAll == 1 && spotify.State.Repeat == 2, $"{repeatAll}, {spotify.State.Repeat}");
        spotify.ToggleLike();
        await Task.Delay(500);
        Check("the like button is found whichever way it is worded", spotify.State.Liked == false);
        Check("bitrate from a fetched file's size and the song's length", WebServiceView.Bitrate(new long[] { 5_760_000 }, 180) == 256 && WebServiceView.Bitrate(new long[] { 7_200_000 }, 180) == 320
            && WebServiceView.Bitrate(new long[] { 2_880_000, 90_000_000 }, 180) == 128 && WebServiceView.Bitrate(new long[] { 5_760_000 }, 5) == 0 && WebServiceView.Bitrate(Array.Empty<long>(), 180) == 0);

        // a player whose audio element is never put on the page (as Spotify's is): Phoenix can still stop it
        await spotify.LoadTestPageAsync("""
            <html><head><title>hidden audio</title></head><body><script>
              var a = document.createElement('audio');      // made in code, never added to the page
              var on = false;
              Object.defineProperty(a, 'paused', { get: function () { return !on; } });
              a.pause = function () { on = false; document.title = 'stopped'; };
              HTMLMediaElement.prototype.play.call = undefined;
              a.play = function () { on = true; return Promise.resolve(); };
              HTMLMediaElement.prototype.play.apply(a, []).catch(function () { });   // goes through the hook Phoenix placed
              on = true;
            </script></body></html>
            """);
        await Task.Delay(300);
        await spotify.AskAsync();
        var seenPlaying = spotify.State.Playing;
        spotify.Pause();
        await Task.Delay(600);
        Log.AppendLine($"INFO  hidden audio element: seen playing {seenPlaying}, after pause the page says \"{spotify.PageTitle}\"");

        // a plain site: no named buttons, just media and the media-session hooks
        await spotify.LoadTestPageAsync("""
            <html><head><title>Plain player</title></head><body>
            <script>
              window.calls = [];
              var a = document.createElement('audio'); document.body.appendChild(a);
              Object.defineProperty(a, 'duration', { get: function () { return 200; } });
              Object.defineProperty(a, 'paused', { get: function () { return !window.on; } });
              a.play = function () { window.on = true; return Promise.resolve(); };
              a.pause = function () { window.on = false; };
              navigator.mediaSession.setActionHandler('nexttrack', function () { window.calls.push('next'); document.title = window.calls.join(','); });
              navigator.mediaSession.setActionHandler('previoustrack', function () { window.calls.push('prev'); document.title = window.calls.join(','); });
              navigator.mediaSession.setActionHandler('play', function () { window.on = true; window.calls.push('play'); document.title = window.calls.join(','); });
              navigator.mediaSession.setActionHandler('pause', function () { window.on = false; window.calls.push('pause'); document.title = window.calls.join(','); });
              navigator.mediaSession.setActionHandler('seekto', function (d) { window.calls.push('seek' + d.seekTime); document.title = window.calls.join(','); });
            </script></body></html>
            """);
        await spotify.AskAsync();
        await Task.Delay(200);
        Check("a site without shuffle, repeat or like has those hidden in the player bar", spotify.State is { Shuffle: null, Repeat: null, Liked: null, CanSeek: true, CanNext: true, CanPrevious: true }
            && !win.ShuffleShown && !win.RepeatShown && !win.HeartShown && win.SeekShown, $"shuffle {win.ShuffleShown}, repeat {win.RepeatShown}, like {win.HeartShown}, seek {win.SeekShown}");
        App.Player.Toggle();
        await Task.Delay(500);
        App.Player.Next();
        await Task.Delay(500);
        App.Player.Previous();
        await Task.Delay(500);
        spotify.SeekTo(42);
        await Task.Delay(500);
        App.Player.Toggle();
        await Task.Delay(500);
        Check("play, next, previous, seek and pause reach a plain site through its media session", spotify.PageTitle == "play,next,prev,seek42,pause", spotify.PageTitle);

        Menus.Play(lib.Albums[0].Tracks);
        await Task.Delay(500);
        Check("starting Phoenix's own music takes the controls back and shows them all again", !spotify.Active && !win.ShowingWebInBar && win.BarTitleShown == lib.Albums[0].Tracks[0].Title && win.ShuffleShown && win.RepeatShown && win.HeartShown && win.SeekShown, win.BarTitleShown);
        App.Player.Next();
        Check("and next moves through the queue again", App.Player.CurrentIndex == 1);
        App.Player.Stop();

        win.ShowNow(true);
        Check("the web player steps aside for now playing", spotify.Covered && mine.Covered);
        win.ShowNow(false);
        Check("and comes back afterwards", !spotify.Covered);
        WebServices.Set(spotifyService, false);
        win.ShowWebTabs();
        Check("switching a service off removes its tab and leaves its page", !win.WebTabs.Contains("web:spotify") && win.CurrentPage == "music");
        WebServices.Set(WebServices.Find("ytmusic")!, false);
        WebServices.Set(WebServices.Find("netflix")!, false);
        App.Settings.CustomServices.Clear();
        win.ShowWebTabs();

        // quickplay: a click on an album opens it; the setting makes it play instead
        win.Navigate("quickplay");
        await Task.Delay(300);
        var quick = win.Page<QuickplayView>("quickplay").NewItems;
        App.Player.Stop();
        QuickplayView.Activate(quick[0]);
        await Task.Delay(300);
        Check("clicking an album in quickplay opens it in the collection", win.CurrentPage == "music" && win.Page<MusicView>("music").ShownAlbum?.Title == quick[0].Title && App.Player.Current == null, win.Page<MusicView>("music").ShownAlbum?.Title ?? "none");
        App.Settings.QuickplayPlays = true;
        QuickplayView.Activate(quick[0]);
        await Task.Delay(300);
        Check("with the setting on it plays instead", App.Player.Current?.Album == quick[0].Title);
        App.Settings.QuickplayPlays = false;
        App.Player.Stop();
        win.Navigate("settings");
        await Task.Delay(400);
        settings.ShowCategory("software", "Metadata");
        await Task.Delay(200);
        Check("settings: Metadata lists every source with its weight, and the order they are asked in", Shown("PanelMetadata") && settings.SourceRowCount == Metadata.Sources.Length
            && settings.SourceOrderText.Contains("Pictures in the album's folder  →  MusicBrainz"), settings.SourceOrderText);
        await Shot("12-settings-metadata");
        settings.ShowCategory("software", "Rip");
        Check("settings: Rip shows the audio formats", Shown("PanelRip") && !Shown("PanelCollection") && ((Panel)settings.FindName("RipFormats")).Children.Count == CdAudio.Formats.Length);
        await Shot("12-settings-rip");
        settings.ShowCategory("software", "Display");
        Check("settings: Display lists every visualizer style", Shown("PanelDisplay") && ((ComboBox)settings.FindName("VizStyle")).Items.Count == Visualizer.Styles.Length);
        await Shot("12-settings-display");
        settings.ShowCategory("software", "Playback");
        ((ComboBox)settings.FindName("EqPreset")).SelectedItem = "rock";
        Check("settings: Playback has a ten band equalizer with presets", Shown("PanelPlayback") && settings.EqSliders.Count == 10 && Math.Abs(settings.EqSliders[0].Value - 5) < 0.01
            && App.Settings.EqPreset == "rock" && Math.Abs(App.Settings.EqGains[9] - 4.5) < 0.01, $"{settings.EqSliders.Count} bands, preset {App.Settings.EqPreset}");
        settings.EqSliders[3].Value = -6;
        Check("moving a band by hand makes the preset custom", App.Settings.EqPreset == "custom" && Math.Abs(App.Settings.EqGains[3] + 6) < 0.01);
        App.Settings.EqEnabled = true;
        App.Player.ApplyAudioSettings();
        Check("the equalizer reaches the audio engine", App.Player.Engine?.EqualizerOn == true);
        await Shot("12-settings-playback");
        App.Settings.EqEnabled = false;
        App.Player.ApplyAudioSettings();
        Check("turning the equalizer off reaches the engine too", App.Player.Engine?.EqualizerOn == false);
        settings.ShowCategory("software", "Podcasts");
        Check("settings: Podcasts offers to keep episodes downloaded", Shown("PanelPodcasts") && !Shown("PanelPlayback") && settings.PodcastKeepChoices == 6
            && settings.PodcastFolderShown == Path.Combine(AppPaths.DataDir, "podcasts"), settings.PodcastFolderShown);
        await Shot("12-settings-podcasts");
        settings.ShowCategory("phone");
        Check("settings: Phone shows sync options", Shown("PanelPhone") && !Shown("PanelDisplay"));
        await Shot("12-settings-phone");
        settings.ShowCategory("servers", "Plex");
        Check("settings: Servers shows Plex", Shown("PanelPlex") && !Shown("PanelJellyfin") && !Shown("PanelPhone"));
        settings.ShowCategory("servers");
        Check("settings: Servers opens on Jellyfin", Shown("PanelJellyfin") && !Shown("PanelPlex"));
        await Shot("12-settings-servers");
        settings.ShowCategory("software");
        App.ApplyTheme("dark");
        win.Navigate("music");
        await Shot("13-music-dark");
        App.Player.Stop();
        await DarkAudit();
    }

    // ---------- dark mode: every kind of menu, dropdown, dialog and page ----------

    static void Snap(FrameworkElement? el, string name)
    {
        if (el == null || el.ActualWidth < 5 || el.ActualHeight < 5) { Log.AppendLine($"INFO  nothing to capture for {name}"); return; }
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(el.ActualWidth), (int)Math.Ceiling(el.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(_dir, name + ".png"));
        enc.Save(fs);
    }

    /// <summary>Opens a modal dialog, captures it half a second later, then closes it.</summary>
    static void Modal(string name, Action open)
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            var top = Application.Current.Windows.OfType<Window>().LastOrDefault(w => w != App.Win && w.IsVisible);
            Snap(top, name);
            top?.Close();
        };
        t.Start();
        open();
    }

    static async Task DarkAudit()
    {
        var win = App.Win;
        var lib = App.Lib;
        Check("dark theme is active", App.Settings.Theme == "dark" || ((SolidColorBrush)Application.Current.Resources["Bg"]).Color.R < 60);

        win.Page<SettingsView>("settings").ShowCategory("software");
        foreach (var page in new[] { "quickplay", "music", "videos", "pictures", "podcasts", "books", "playlists", "disc", "phone", "settings", "search" })
        {
            win.Navigate(page);
            if (page == "search") win.Page<SearchView>("search").Search("song");
            await Shot("40-dark-" + page);
        }
        win.Navigate("music");
        win.Page<MusicView>("music").SetMode("songs");
        await Task.Delay(300);

        // context menus, with a submenu open
        async Task MenuShot(string name, ContextMenu m, string? openSub = null)
        {
            Menus.Open(m, win);
            await Task.Delay(500);
            Snap(m, name);
            if (openSub != null && m.Items.OfType<MenuItem>().FirstOrDefault(i => (string?)i.Header == openSub) is { } sub)
            {
                sub.IsSubmenuOpen = true;
                await Task.Delay(400);
                Snap((sub.Template.FindName("Sub", sub) as System.Windows.Controls.Primitives.Popup)?.Child as FrameworkElement, name + "-submenu");
            }
            m.IsOpen = false;
            await Task.Delay(200);
        }
        await MenuShot("41-dark-menu-song", Menus.ForTracks(lib.Tracks.Take(2).ToList(), null), "Add to playlist");
        await MenuShot("42-dark-menu-album", Menus.ForAlbum(lib.Albums[0]));

        // dropdown
        win.Navigate("settings");
        win.Page<SettingsView>("settings").ShowCategory("software", "Display");
        await Task.Delay(300);
        if (win.Page<SettingsView>("settings").FindName("VizStyle") is ComboBox cb)
        {
            cb.IsDropDownOpen = true;
            await Task.Delay(500);
            Snap((cb.Template.FindName("PART_Popup", cb) as System.Windows.Controls.Primitives.Popup)?.Child as FrameworkElement, "44-dark-dropdown");
            cb.IsDropDownOpen = false;
        }

        // dialogs
        var tracks = lib.Tracks.Take(2).ToList();
        Modal("45-dark-dialog-edit-song", () => new EditInfoDialog(new List<Track> { tracks[0] }, false) { Owner = win }.ShowDialog());
        Modal("46-dark-dialog-edit-album", () => new EditInfoDialog(lib.Albums[0].Tracks, true) { Owner = win }.ShowDialog());
        Modal("47-dark-dialog-tags", () => new EditTagsDialog(Library.SongTag, tracks.Select(t => t.Id), "2 songs").ShowDialog());
        if (lib.Videos.Count > 0) Modal("48-dark-dialog-edit-video", () => new EditVideoDialog(new List<VideoItem> { lib.Videos[0] }).ShowDialog());
        Modal("49-dark-dialog-input", () => InputDialog.Ask("New playlist", "Playlist name", "My songs"));
        Modal("50-dark-dialog-message", () => MessageBox.Show(win, "Delete the playlist “Road trip”? The songs stay in your collection.", "Phoenix", MessageBoxButton.OKCancel, MessageBoxImage.Question));
        Modal("51-dark-dialog-discogs", () => new DiscogsDialog(lib.Albums[0]) { Owner = win }.ShowDialog());
        if (lib.Books.FirstOrDefault(b => lib.BookmarkCount(b) > 0) is { } marked)
            Modal("52-dark-dialog-bookmarks", () => new BookmarksDialog(marked, _ => { }).ShowDialog());
        win.Navigate("music");
    }

    static async Task ShotWindow(string name, Window w)
    {
        w.Show();
        await Shot(name, w);
        w.Close();
    }

    static async Task Shot(string name, Window? window = null)
    {
        await Task.Delay(900);   // let async artwork arrive
        var root = (FrameworkElement)(window ?? App.Win).Content;
        var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(_dir, name + ".png"));
        enc.Save(fs);
    }

    // ---------- sample data ----------

    static void MakeSamples(string music, string pics)
    {
        var artists = new[] { ("The Example Band", "Rock", 2001), ("Second Example", "Electronic", 2010), ("Quiet Trio", "Jazz", 1988) };
        var colors = new[] { "#D9455F", "#3D7EDB", "#3BAA6E", "#E08A2B", "#8A55D6", "#2FA7A0" };
        var n = 0;
        foreach (var (artist, genre, year) in artists)
            for (var a = 1; a <= 2; a++)
            {
                var album = $"{(a == 1 ? "First" : "Later")} Record";
                var dir = Path.Combine(music, artist, album);
                Directory.CreateDirectory(dir);
                SavePng(Path.Combine(dir, "cover.png"), colors[n++], album);
                for (var t = 1; t <= 4; t++)
                {
                    var file = Path.Combine(dir, $"{t:00} #{t} 100% song.wav");
                    WriteWav(file, 4);
                    using var tf = TagLib.File.Create(file);
                    tf.Tag.Title = "Song " + t;
                    tf.Tag.Performers = new[] { artist };
                    tf.Tag.AlbumArtists = new[] { artist };
                    tf.Tag.Album = album;
                    tf.Tag.Genres = new[] { genre };
                    tf.Tag.Year = (uint)(year + a - 1);
                    tf.Tag.Track = (uint)t;
                    tf.Save();
                }
            }
        Directory.CreateDirectory(Path.Combine(pics, "Holiday"));
        for (var i = 0; i < 7; i++) SavePng(Path.Combine(pics, i < 4 ? "" : "Holiday", $"photo {i + 1}.png"), colors[i % colors.Length], "photo " + (i + 1));
    }

    static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>A RAR 4 archive with stored (uncompressed) files: enough to prove .cbr reading without a RAR writer.</summary>
    static void WriteRar4(string path, (string name, byte[] data)[] files)
    {
        using var o = new BinaryWriter(File.Create(path));
        o.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 });
        byte[] Block(Action<BinaryWriter> body)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            body(w);
            w.Flush();
            var raw = ms.ToArray();                      // type, flags, size, fields…
            var crc = (ushort)(Crc32(raw) & 0xFFFF);
            return BitConverter.GetBytes(crc).Concat(raw).ToArray();
        }
        o.Write(Block(w => { w.Write((byte)0x73); w.Write((ushort)0); w.Write((ushort)13); w.Write((ushort)0); w.Write(0u); }));
        foreach (var (name, data) in files)
        {
            var nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
            o.Write(Block(w =>
            {
                w.Write((byte)0x74); w.Write((ushort)0x8000); w.Write((ushort)(32 + nameBytes.Length));
                w.Write((uint)data.Length); w.Write((uint)data.Length);
                w.Write((byte)2); w.Write(Crc32(data)); w.Write(0x5A000000u); w.Write((byte)20); w.Write((byte)0x30);
                w.Write((ushort)nameBytes.Length); w.Write(0x20u); w.Write(nameBytes);
            }));
            o.Write(data);
        }
        o.Write(new byte[] { 0xC4, 0x3D, 0x7B, 0x00, 0x40, 0x07, 0x00 });
    }

    /// <summary>Writes a .cbz (pages named so that plain sorting would put page10 before page2) and a .cbr; returns each one's pages in reading order.</summary>
    static (List<byte[]> zip, List<byte[]> rar) MakeComics(string cbz, string cbr)
    {
        byte[] Page(string color, string text)
        {
            var f = Path.Combine(_dir, "comic-page.png");
            SavePng(f, color, text);
            return File.ReadAllBytes(f);
        }
        var zipPages = new List<byte[]> { Page("#D94A4A", "page 1"), Page("#4A7FD9", "page 2"), Page("#4AD98A", "page 10") };
        using (var zip = System.IO.Compression.ZipFile.Open(cbz, System.IO.Compression.ZipArchiveMode.Create))
        {
            void Add(string name, byte[] bytes) { using var s = zip.CreateEntry(name).Open(); s.Write(bytes); }
            Add("page10.png", zipPages[2]);
            Add("page1.png", zipPages[0]);
            Add("page2.png", zipPages[1]);
            Add("ComicInfo.xml", System.Text.Encoding.UTF8.GetBytes("<ComicInfo><Series>Hero Tales</Series><Number>3</Number><Writer>Pat Pencil</Writer></ComicInfo>"));
        }
        var rarPages = new List<byte[]> { Page("#E0A030", "p1"), Page("#9A4AD9", "p2"), Page("#30B0C0", "p3") };
        WriteRar4(cbr, new[] { ("p3.png", rarPages[2]), ("p1.png", rarPages[0]), ("p2.png", rarPages[1]) });
        return (zipPages, rarPages);
    }

    static void MakeEpub(string path)
    {
        var cover = Path.Combine(_dir, "epub-cover.png");
        SavePng(cover, "#3D7EDB", "A Quiet Book");
        var filler = string.Concat(Enumerable.Range(1, 60).Select(i => $"<p>Paragraph {i}. The road went on past the last houses and into the hills, and nobody who walked it that year could say afterwards exactly where the town had ended and the country begun.</p>"));
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        void Add(string name, string text) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(text); }
        Add("mimetype", "application/epub+zip");
        Add("META-INF/container.xml", """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""");
        Add("OEBPS/content.opf", """
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>A Quiet Book</dc:title><dc:creator>Sam Author</dc:creator><dc:identifier id="id">test-1</dc:identifier><dc:language>en</dc:language>
                <meta name="cover" content="cover-img"/>
              </metadata>
              <manifest>
                <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
                <item id="cover-img" href="images/front.png" media-type="image/png"/>
                <item id="c1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/>
                <item id="c2" href="text/ch2.xhtml" media-type="application/xhtml+xml"/>
                <item id="c3" href="text/ch3.xhtml" media-type="application/xhtml+xml"/>
              </manifest>
              <spine toc="ncx"><itemref idref="c1"/><itemref idref="c2"/><itemref idref="c3"/></spine>
            </package>
            """.Trim());
        Add("OEBPS/toc.ncx", """
            <?xml version="1.0" encoding="UTF-8"?>
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><navMap>
              <navPoint id="n1" playOrder="1"><navLabel><text>Opening</text></navLabel><content src="text/ch1.xhtml"/></navPoint>
              <navPoint id="n2" playOrder="2"><navLabel><text>The Middle</text></navLabel><content src="text/ch2.xhtml#start"/></navPoint>
              <navPoint id="n3" playOrder="3"><navLabel><text>The End</text></navLabel><content src="text/ch3.xhtml"/></navPoint>
            </navMap></ncx>
            """.Trim());
        string Chapter(string title, string body) => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>{title}</title></head><body><h1 id=\"start\">{title}</h1>{body}</body></html>";
        Add("OEBPS/text/ch1.xhtml", Chapter("Opening", "<p>It began quietly.</p><p><img src=\"../images/front.png\" alt=\"cover\"/></p>"));
        Add("OEBPS/text/ch2.xhtml", Chapter("The Middle", filler));
        Add("OEBPS/text/ch3.xhtml", Chapter("The End", "<p>And that was all.</p>"));
        System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, cover, "OEBPS/images/front.png");
    }

    static void WriteWav(string path, int seconds)
    {
        const int rate = 8000;
        var n = rate * seconds;
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write("RIFF"u8); bw.Write(36 + n); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1); bw.Write(rate); bw.Write(rate); bw.Write((short)1); bw.Write((short)8);
        bw.Write("data"u8); bw.Write(n);
        bw.Write(Enumerable.Repeat((byte)128, n).ToArray());   // silence
    }

    static void SavePng(string path, string color, string text)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var c = (Color)ColorConverter.ConvertFromString(color);
            dc.DrawRectangle(new LinearGradientBrush(c, Color.FromRgb((byte)(c.R / 2), (byte)(c.G / 2), (byte)(c.B / 2)), 45), null, new Rect(0, 0, 400, 400));
            var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Light"), 44, Brushes.White, 1.0);
            dc.DrawText(ft, new Point(24, 300));
        }
        var bmp = new RenderTargetBitmap(400, 400, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    const string SampleFeed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
          <channel>
            <title>Sample Cast</title>
            <itunes:author>Someone</itunes:author>
            <description><![CDATA[<p>A show about <b>samples</b>.</p>]]></description>
            <item><title>Episode two</title><guid>ep-2</guid><pubDate>Tue, 03 Jun 2025 10:00:00 GMT</pubDate>
              <enclosure url="https://example.invalid/2.mp3" type="audio/mpeg" length="1"/><itunes:duration>1:02:03</itunes:duration></item>
            <item><title>Episode one</title><guid>ep-1</guid><pubDate>Tue, 27 May 2025 10:00:00 GMT</pubDate>
              <enclosure url="https://example.invalid/1.mp3" type="audio/mpeg" length="1"/><itunes:duration>90</itunes:duration></item>
            <item><title>No audio</title><guid>ep-0</guid></item>
          </channel>
        </rss>
        """;

    const string SampleRelease = """
        {"id": 1, "title": "Sample Album", "year": 1999, "artists": [{"name": "The Example Band (2)"}],
         "genres": ["Electronic"], "styles": ["Ambient"],
         "images": [{"type": "secondary", "uri": "https://example.invalid/secondary.jpg"}, {"type": "primary", "uri": "https://example.invalid/primary.jpg"}],
         "tracklist": [{"position": "", "type_": "heading", "title": "Side A"}, {"position": "A1", "type_": "track", "title": "Intro", "duration": "1:00"},
                       {"position": "A2", "type_": "track", "title": "Middle", "duration": "2:00"}, {"position": "B1", "type_": "track", "title": "Later", "duration": "3:00"},
                       {"position": "B2", "type_": "track", "title": "Outro", "duration": "4:00"}]}
        """;
}
