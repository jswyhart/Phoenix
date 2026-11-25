using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Halcyon.Core;
using Halcyon.Services;

namespace Halcyon.UI;

/// <summary>Self-test: volume levelling, sleep timer and speed, the play log and stats, radio, the tag editor, library care,
/// backup, updates, artist photos, Mixview, casting and the phone remote.</summary>
public static partial class Smoke
{
    /// <summary>A stereo 16-bit file holding a steady 1 kHz tone at the given height (1 = as loud as a file can be).</summary>
    static void WriteTone(string path, double amplitude, int seconds, double hz = 1000)
    {
        const int rate = 48000;
        var n = rate * seconds;
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write("RIFF"u8); bw.Write(36 + n * 4); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)2); bw.Write(rate); bw.Write(rate * 4); bw.Write((short)4); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(n * 4);
        for (var i = 0; i < n; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * hz * i / rate) * amplitude * 32767);
            bw.Write(v); bw.Write(v);
        }
    }

    static async Task<bool> Until(Func<bool> ready, int ms = 6000)
    {
        for (var waited = 0; waited < ms; waited += 50)
        {
            if (ready()) return true;
            await Task.Delay(50);
        }
        return ready();
    }

    static async Task RunNewFeatures()
    {
        var win = App.Win;
        var lib = App.Lib;
        var P = App.Player;
        var S = App.Settings;
        var work = Path.Combine(_dir, "new-features");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        P.Stop();

        // ---------- volume levelling ----------
        string quiet = Path.Combine(work, "quiet.wav"), loud = Path.Combine(work, "loud.wav");
        WriteTone(quiet, 0.1, 5);
        WriteTone(loud, 0.4, 5);
        double lq = Levelling.Measure(quiet), ll = Levelling.Measure(loud);
        Check("loudness is measured the standard way: a 1 kHz tone at a tenth of full height reads -20 LUFS", Math.Abs(lq + 20) < 0.3 && Math.Abs(ll + 7.96) < 0.3, $"quiet {lq:0.00} LUFS, loud {ll:0.00} LUFS");
        Track Loose(string file, long id, string title) => new() { Id = id, Source = "local", SourceId = file, Location = file, Title = title, Artist = "Test Tones", AlbumArtist = "Test Tones", Album = "Levels", DurationMs = 5000, FileModified = 1 };
        Track tq = Loose(quiet, -11, "Quiet"), tl = Loose(loud, -12, "Loud");
        Check("a file without ReplayGain tags says so", Levelling.ReadTags(quiet) == null);
        S.Levelling = true; S.LevelMeasure = true; S.AudioEngine = true;
        Levelling.Forget();
        Menus.Play(new[] { tq });
        var levelled = await Until(() => P.Engine != null && P.OnEngine && Math.Abs(P.Engine.CurrentGain - Math.Pow(10, 2 / 20.0)) < 0.05);
        var quietGain = P.Engine?.CurrentGain ?? 0;
        Menus.Play(new[] { tl });
        var cut = await Until(() => P.Engine != null && Math.Abs(P.Engine.CurrentGain - Math.Pow(10, -10.04 / 20.0)) < 0.03);
        Check("volume levelling turns a quiet song up and a loud one down to the same loudness", levelled && cut && Levelling.MeasuredCount >= 2,
            $"quiet song played at ×{quietGain:0.00} (+2 dB), loud at ×{P.Engine?.CurrentGain:0.00} (-10 dB), {Levelling.MeasuredCount} measured");
        S.Levelling = false;
        P.ApplyAudioSettings();
        Check("switching levelling off puts the song back as it is", await Until(() => P.Engine != null && Math.Abs(P.Engine.CurrentGain - 1) < 0.001), $"×{P.Engine?.CurrentGain:0.00}");

        // ---------- sleep timer ----------
        var volumeBefore = P.Volume;
        P.SetSleep(TimeSpan.FromSeconds(2));
        var said = win.ModeText;
        var slept = await Until(() => !P.IsPlaying && !P.SleepOn, 5000);
        Check("the sleep timer pauses playback and leaves the volume as it was", slept && said.Contains("sleep") && Math.Abs(P.Volume - volumeBefore) < 0.001 && P.Current != null, $"shown as “{said}”");
        P.SetSleep(null, atEnd: true);
        Check("the sleep timer can wait for the end of what is playing", P.SleepAtEnd && P.SleepText.Contains("ends"));
        P.SetSleep(null);
        var more = win.MoreMenu();
        Check("the more menu offers the sleep timer, speed, casting, Mixview and stats", more.Items.OfType<MenuItem>().Count() >= 5 && ((MenuItem)more.Items[0]).Items.Count == 8, $"{more.Items.Count} entries");

        // ---------- playback speed, remembered per podcast ----------
        var episode = new Episode { Id = -5, PodcastId = 55, Title = "Episode", Url = quiet };
        P.Play(new[] { new PlayItem { Title = "Episode", Subtitle = "A Show", Url = quiet, Episode = episode } });
        await Until(() => P.CanSpeed, 2000);
        var could = P.CanSpeed;
        P.Speed = 1.5;
        P.Stop();
        P.Play(new[] { new PlayItem { Title = "Another episode", Subtitle = "A Show", Url = loud, Episode = new Episode { Id = -6, PodcastId = 55, Title = "Another", Url = loud } } });
        await Task.Delay(300);
        var remembered = P.Speed;
        Menus.Play(new[] { tq });
        await Until(() => P.OnEngine, 2000);
        Check("playback speed is kept per podcast series, and music is left at normal speed", could && Math.Abs(remembered - 1.5) < 0.01 && S.Speeds.GetValueOrDefault("podcast:55") == 1.5 && !P.CanSpeed && P.Speed == 1,
            $"series speed {remembered:0.##}×, music {P.Speed:0.##}×");
        S.Speeds.Clear();
        P.Stop();

        // ---------- the play log, stats and scrobbling ----------
        var song = lib.Tracks.OrderBy(t => t.Id).First();
        var before = lib.Db.Plays(0, long.MaxValue).Count;
        for (var i = 0; i < 3; i++) Listening.Left(new PlayItem { Title = song.Title, Track = song, DurationMs = song.DurationMs }, 0, finished: true);
        Listening.Left(new PlayItem { Title = "skipped", Track = lib.Tracks[5], DurationMs = lib.Tracks[5].DurationMs }, 900, finished: false);
        var logged = lib.Db.Plays(0, long.MaxValue);
        var year = ListenStats.FromLog("this year", new DateTimeOffset(new DateTime(DateTime.Now.Year, 1, 1)).ToUnixTimeSeconds(), Fmt.Now + 5, true);
        Check("finished songs go into the play log; a song skipped at once does not", logged.Count - before == 3 && logged.TakeLast(3).All(r => r.Counted && r.Title == song.Title),
            $"{logged.Count - before} listens logged");
        Check("stats are worked out from the log", year.Plays >= 3 && year.TopSongs.Count > 0 && year.TopSongs[0].Name == song.Title && year.Over.Count == DateTime.Now.Month && year.TopArtists[0].Name == song.Artist,
            $"{year.Plays} plays, top song “{year.TopSongs.FirstOrDefault()?.Name}”, {year.Over.Count} months");
        win.Navigate("stats");
        await Task.Delay(400);
        var stats = win.Page<StatsView>("stats");
        Check("the stats page shows the year", stats.Shown is { Plays: >= 3 } && stats.IsVisible);
        await Shot("30-stats");
        stats.Show("all");
        Check("the stats page can show all time from the play counts", stats.Shown is { Title: "all time" });
        stats.Show("year");
        var sig = Listening.Sign(new Dictionary<string, string> { ["token"] = "yyy", ["api_key"] = "xxx", ["method"] = "auth.getSession" }, "secret");
        var expected = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("api_keyxxxmethodauth.getSessiontokenyyysecret"))).ToLowerInvariant();
        var body = JsonDocument.Parse(Listening.ListenBrainzBody("single", new[] { (1700000000L, "An Artist", "A Song", "An Album", 215000L) })).RootElement;
        var listen = body.GetProperty("payload")[0];
        Check("listens are packed the way Last.fm and ListenBrainz ask", sig == expected && body.GetProperty("listen_type").GetString() == "single" && listen.GetProperty("listened_at").GetInt64() == 1700000000
            && listen.GetProperty("track_metadata").GetProperty("artist_name").GetString() == "An Artist" && listen.GetProperty("track_metadata").GetProperty("additional_info").GetProperty("duration_ms").GetInt64() == 215000);
        Check("nothing is sent to a scrobbling service unless one is switched on", !S.ScrobbleLastFm && !S.ScrobbleListenBrainz && lib.Db.PendingScrobbles("lastfm").Count == 0 && lib.Db.PendingScrobbles("listenbrainz").Count == 0);

        // ---------- internet radio ----------
        var parsed = Radio.Parse(JsonDocument.Parse("""
            [{"name":" Jazz FM ","url":"http://old.example/stream.pls","url_resolved":"https://stream.example/jazz","favicon":"","tags":"jazz,smooth,soul,extra","countrycode":"GB"},
             {"name":"","url":"https://x.example/a"},{"name":"Not a stream","url":"ftp://x.example/a"},
             {"name":"Jazz FM again","url_resolved":"https://stream.example/jazz"}]
            """).RootElement);
        Check("stations from the directory are read, without blanks or repeats", parsed.Count == 1 && parsed[0].Name == "Jazz FM" && parsed[0].Url == "https://stream.example/jazz" && parsed[0].Subtitle == "GB  ·  jazz, smooth, soul",
            $"{parsed.Count} station: {parsed.FirstOrDefault()?.Name} ({parsed.FirstOrDefault()?.Subtitle})");
        lib.SaveStation(parsed[0]);
        lib.TogglePin("radio", parsed[0].Url, parsed[0].Name);
        Check("an address typed in becomes a station, a non-address does not", Radio.FromAddress("", "https://radio.example/live.mp3") is { Name: "radio.example" } && Radio.FromAddress("x", "not an address") == null);
        win.Navigate("radio");
        await Task.Delay(300);
        var radio = win.Page<RadioView>("radio");
        var item = Player.From(lib.Stations[0]);
        Check("a saved station is listed, and plays as an endless stream outside the music engine", radio.SavedShown == 1 && lib.Stations.Count == 1 && item.Station != null && item.Track == null && item.DurationMs == 0 && CastMedia.Can(item));
        await Shot("31-radio");
        win.Navigate("quickplay");
        await Task.Delay(300);
        var pinned = ((ItemsControl)win.Page<QuickplayView>("quickplay").FindName("PinsList")).Items.Cast<QuickItem>().Any(q => q.Title == "Jazz FM" && q.Subtitle == "radio");
        Check("a station can be pinned to quickplay", pinned);
        lib.RemoveStation(lib.Stations[0]);
        Check("removing a station also takes its pin away", lib.Stations.Count == 0 && !lib.IsPinned("radio", parsed[0].Url));

        // ---------- tag editor ----------
        Check("tidy capitals", TagFixes.TitleCase("the end of the road") == "The End of the Road" && TagFixes.TitleCase("AC/DC live at the BBC") == "AC/DC Live at the BBC" && TagFixes.TitleCase("mcCartney II") == "mcCartney II",
            TagFixes.TitleCase("the end of the road"));
        Check("clean up", TagFixes.CleanUp("03 - my_song   name ", true) == "my song name" && TagFixes.CleanUp("1999", true) == "1999" && TagFixes.CleanUp("  A_B ", false) == "A B", TagFixes.CleanUp("03 - my_song   name ", true));
        var fromName = TagFixes.FromFileName("07 - The Band - Hello World", "%n - %a - %t");
        Check("fields are read out of a file name by a pattern", fromName is { Count: 3 } && fromName['n'] == "07" && fromName['a'] == "The Band" && fromName['t'] == "Hello World" && TagFixes.FromFileName("no number here", "%n - %t") == null);
        var album = lib.Albums.First(a => a.Tracks.Count >= 3);
        var editor = new TagEditorWindow(album.Tracks);
        var replaced = editor.Replace("Song", "Tune");
        editor.NumberTracks();
        var changedRows = editor.Rows.Count(r => r.Changed);
        var untouched = album.Tracks.All(t => t.Title.Contains("Song"));
        foreach (var r in editor.Rows) r.Title = r.Track.Title;
        editor.Rows[0].Genre = "Smoke Test";
        var wasGenre = editor.Rows[0].Track.Genre;
        var failedWrites = editor.SaveRows();
        var saved = album.Tracks[0].Genre == "Smoke Test";
        editor.Rows[0].Genre = wasGenre;
        editor.SaveRows();
        Check("the tag editor changes many songs at once, and nothing is saved until asked", replaced == album.Tracks.Count && changedRows == album.Tracks.Count && untouched && saved && failedWrites == 0 && album.Tracks[0].Genre == wasGenre,
            $"{replaced} titles replaced, {changedRows} rows changed, {failedWrites} files not written");
        editor.Show();
        await Task.Delay(300);
        await ShotWindow("32-tag-editor", editor);
        editor.Close();

        // ---------- duplicates and missing files ----------
        var original = lib.Tracks.First(t => t.IsLocal && t.TrackNo == 2);
        var copy = Path.Combine(Path.GetDirectoryName(original.SourceId)!, "copy of a song.wav");
        File.Copy(original.SourceId, copy, true);
        await lib.ScanAsync(_ => { });
        var groups = LibraryCare.ByName().Where(g => g.Tracks.Any(t => t.SourceId == copy)).ToList();
        var dupTrack = lib.Tracks.First(t => t.SourceId == copy);
        var foundDup = groups.Count == 1 && groups[0].Tracks.Contains(original) && groups[0].Tracks.Contains(dupTrack);
        var playsBefore = original.PlayCount;
        dupTrack.PlayCount = 4;
        var list = lib.CreatePlaylist("care test");
        lib.AddToPlaylist(list, new[] { dupTrack });
        // (the sample albums reuse song names, so the group may hold more than these two: only the copy is resolved here)
        if (foundDup) LibraryCare.Resolve(new LibraryCare.DupGroup { Tracks = { original, dupTrack } }, original, recycle: false);
        Check("the same song stored twice is found, and removing a copy passes its plays and playlist places to the one kept",
            foundDup && lib.Tracks.All(t => t.SourceId != copy) && original.PlayCount == playsBefore + 4 && lib.TracksOf(list).SequenceEqual(new[] { original }) && File.Exists(copy),
            $"{groups.Count} group, plays {playsBefore} → {original.PlayCount}");
        original.PlayCount = playsBefore;
        lib.Db.SaveStats(original);
        lib.DeletePlaylist(list);
        File.Delete(copy);
        var rnd = new Random(7);
        var printA = Enumerable.Range(0, 400).Select(_ => (uint)rnd.Next()).ToArray();
        var printB = printA.Skip(3).Select(x => rnd.Next(8) == 0 ? x ^ 1u << rnd.Next(32) : x).ToArray();
        var printC = Enumerable.Range(0, 400).Select(_ => (uint)rnd.Next()).ToArray();
        double same = LibraryCare.Similarity(printA, printB), different = LibraryCare.Similarity(printA, printC);
        var raw = LibraryCare.ParseRaw("{\"duration\": 12.5, \"fingerprint\": [1, 4294967295, 77]}");
        Check("fingerprints tell the same recording from a different one, even when one copy starts later", same > 0.95 && different < 0.6 && raw is { Length: 3 } && raw[1] == uint.MaxValue, $"same {same:P0}, different {different:P0}");
        var moved = lib.Tracks.First(t => t.IsLocal && t.TrackNo == 3);
        string oldPath = moved.SourceId, newDir = Path.Combine(Path.GetDirectoryName(oldPath)!, "moved"), newPath = Path.Combine(newDir, Path.GetFileName(oldPath));
        Directory.CreateDirectory(newDir);
        File.Move(oldPath, newPath);
        var missing = LibraryCare.Missing();
        var located = LibraryCare.Locate(missing);
        var relinked = located.TryGetValue(moved, out var where) && lib.Relink(moved, where);
        Check("a song whose file moved is found again and keeps its place in the library", missing.Count == 1 && missing[0] == moved && relinked && moved.SourceId == newPath && LibraryCare.Missing().Count == 0,
            $"{missing.Count} missing, found at {where}");
        File.Move(newPath, oldPath);
        Directory.Delete(newDir);
        lib.Relink(moved, oldPath);
        lib.NotifyChanged();
        await lib.ScanAsync(_ => { });
        Check("the library is as it was after the care tests", lib.Tracks.Count == 24 && LibraryCare.Missing().Count == 0, $"{lib.Tracks.Count} songs");
        win.Navigate("care");
        await Task.Delay(300);
        Check("the library care page opens on duplicates", win.Page<CareView>("care").IsVisible && win.Page<CareView>("care").Groups.Count == LibraryCare.ByName().Count,
            $"{win.Page<CareView>("care").Groups.Count} groups of same-named songs in the sample library");
        await Shot("33-library-care-duplicates");
        win.Page<CareView>("care").Show("missing");
        await Shot("33-library-care");

        // ---------- backup ----------
        var backup = Path.Combine(work, "test" + Backup.Extension);
        var size = await Backup.CreateAsync(backup, covers: true, _ => { });
        var manifest = Backup.Read(backup);
        Backup.Stage(backup);
        var staged = Path.Combine(AppPaths.DataDir, "restore-pending");
        var stagedOk = File.Exists(Path.Combine(staged, "library.db")) && File.Exists(Path.Combine(staged, "settings.json")) && Directory.Exists(Path.Combine(staged, "art"));
        var check = new Database(Path.Combine(staged, "library.db"));
        stagedOk &= check.LoadTracks().Count == lib.Tracks.Count && check.Plays(0, long.MaxValue).Count == lib.Db.Plays(0, long.MaxValue).Count;
        check.Close();
        Directory.Delete(staged, true);
        File.WriteAllText(Path.Combine(work, "not-a-backup" + Backup.Extension), "hello");
        Check("a backup holds the library, the play log, settings and covers, and can be unpacked for restoring",
            size > 1000 && manifest is { App: "Phoenix" } && manifest.Songs == lib.Tracks.Count && manifest.Covers && stagedOk && Backup.Read(Path.Combine(work, "not-a-backup" + Backup.Extension)) == null,
            $"{size / 1024} KB, {manifest?.Songs} songs");

        // ---------- updates ----------
        var release = Updater.Parse(JsonDocument.Parse($$"""
            {"tag_name":"v9.4.1","name":"Phoenix 9.4.1","body":"Notes","html_url":"https://github.com/{{Updater.Repo}}/releases/tag/v9.4.1","draft":false,
             "assets":[{"name":"Phoenix-9.4.1-win-arm64-setup.exe","size":5,"browser_download_url":"https://github.com/{{Updater.Repo}}/releases/download/v9.4.1/Phoenix-9.4.1-win-arm64-setup.exe"},
                       {"name":"Phoenix-9.4.1-win-x64-setup.exe","size":123,"digest":"sha256:abcd","browser_download_url":"https://github.com/{{Updater.Repo}}/releases/download/v9.4.1/Phoenix-9.4.1-win-x64-setup.exe"}]}
            """).RootElement, "x64");
        var elsewhere = Updater.Parse(JsonDocument.Parse("""
            {"tag_name":"v9.9.9","assets":[{"name":"Phoenix-9.9.9-win-x64-setup.exe","size":1,"browser_download_url":"https://downloads.example/Phoenix-9.9.9-win-x64-setup.exe"}]}
            """).RootElement, "x64");
        var current = Updater.Parse(JsonDocument.Parse($$"""{"tag_name":"v{{Updater.CurrentText}}","assets":[]}""").RootElement, "x64");
        Check("a newer release on GitHub is recognised, with the installer for this kind of PC", release is { IsNewer: true, InstallerSize: 123, Sha256: "abcd" } && release.InstallerName.Contains("x64") && release.VersionText == "9.4.1"
            && current is { IsNewer: false } && Updater.ParseVersion("v1.2") == new Version(1, 2, 0), $"{release?.VersionText}: {release?.InstallerName}");
        Check("an installer that is not on Phoenix's own GitHub page is never offered", elsewhere is { InstallerUrl: "" });

        // ---------- artist photos ----------
        var pickId = Backdrops.PickArtist(JsonDocument.Parse("""{"artists":[{"id":"wrong","score":100,"name":"The Examples"},{"id":"right","score":96,"name":"The Example Band"}]}""").RootElement, "Example Band");
        var fan = Backdrops.ParseFanart(JsonDocument.Parse("""{"artistbackground":[{"url":"https://a.example/1.jpg","likes":"1"},{"url":"https://a.example/2.jpg","likes":"9"}]}""").RootElement);
        var deezer = Backdrops.ParseDeezer(JsonDocument.Parse("""{"data":[{"name":"Example Band","picture_xl":"https://d.example/images/artist//1000x1000.jpg"},{"name":"The Example Band","picture_xl":"https://d.example/images/artist/abc/1000x1000.jpg"}]}""").RootElement, "Example Band");
        Check("artist photos: the right artist is picked, the best-liked photo comes first, and a blank silhouette is not used",
            pickId == "right" && fan.Count == 2 && fan[0].EndsWith("2.jpg") && deezer.Count == 1 && deezer[0].Contains("/abc/"), $"{pickId}, {fan.Count} photos, {deezer.Count} portrait");
        Check("artist photos are off until asked for", !S.Backdrops && !win.BackdropLayer.HasPictures);
        Menus.Play(lib.Albums[0].Tracks);
        win.ShowNow(true);
        var pictures = Directory.GetFiles(Path.Combine(_dir, "pictures"), "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".png") || f.EndsWith(".jpg")).Take(2).ToList();
        win.BackdropLayer.Show(pictures);
        win.SetVisualizer(App.Settings.Visualizer, App.Settings.VisualizerColor);
        await Until(() => win.BackdropLayer.Shown >= 1, 3000);
        await Task.Delay(2600);
        Check("photos are shown behind now playing in place of the cover wall", win.BackdropLayer.Shown >= 1 && win.BackdropLayer.IsVisible && pictures.Count > 0, $"{win.BackdropLayer.Shown} shown of {pictures.Count}");
        await Shot("34-backdrop");
        win.BackdropLayer.Show(new List<string>());
        win.ShowNow(false);
        P.Stop();

        // ---------- mixview ----------
        var centreAlbum = lib.Albums.OrderByDescending(a => lib.Albums.Count(o => o.Artist == a.Artist)).First();
        MixviewPage.Open(centreAlbum, centreAlbum.Artist);
        var mix = win.Page<MixviewPage>("mixview").Mix;
        await Task.Delay(1500);
        var around = mix.Nodes.Count - 1;
        var centreWas = mix.Centre;
        var settled = centreWas != null && Math.Abs(centreWas.X - mix.ActualWidth / 2) < 3 && mix.Nodes.All(n => n.Alpha > 0.95);
        var hit = centreWas != null && ReferenceEquals(mix.NodeAt(new Point(centreWas.X, centreWas.Y)), centreWas);
        Check("mixview puts an album in the middle with its artist, their other albums and related artists around it",
            centreWas is { Album: not null } && around >= 2 && mix.Nodes.Any(n => n.IsArtist && n.Artist == centreAlbum.Artist) && settled && hit, $"{around} around “{centreWas?.Title}”");
        if (D3DSurface.Available)
        {
            var shot = mix.Snapshot();
            var solid = 0;
            if (shot != null)
            {
                var px = new byte[shot.PixelWidth * shot.PixelHeight * 4];
                shot.CopyPixels(px, shot.PixelWidth * 4, 0);
                for (var i = 3; i < px.Length; i += 4) if (px[i] > 200) solid++;
            }
            Check("mixview is drawn by the graphics card", mix.OnGpu && mix.Frames > 20 && solid > 20000, $"{mix.Frames} frames, {solid} solid pixels");
        }
        await Shot("35-mixview");
        var other = mix.Nodes.First(n => !ReferenceEquals(n, mix.Centre) && !n.IsArtist);
        var otherWas = new Point(other.X, other.Y);
        mix.Show(other.Album!);
        var kept = ReferenceEquals(mix.Centre, other) && Math.Abs(other.X - otherWas.X) < 1;   // it starts from where it was…
        await Task.Delay(1200);
        Check("clicking a cover moves it to the middle, gliding from where it was", kept && Math.Abs(other.X - mix.ActualWidth / 2) < 3 && mix.Nodes.All(n => !n.Leaving), $"“{mix.Centre?.Title}” now in the middle, {mix.Nodes.Count - 1} around");
        var related = MixView.RelatedArtists(centreAlbum.Artist, 5);
        Check("related artists come from the collection and never include the artist itself", related.All(a => a != centreAlbum.Artist) && related.Distinct().Count() == related.Count, string.Join(", ", related));
        win.Navigate("music");

        // ---------- casting ----------
        var url = CastServer.Publish(loud, "127.0.0.1");
        using (var http = new HttpClient())
        {
            var whole = await http.GetAsync(url);
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(100, 199);
            var part = await http.SendAsync(request);
            var bytes = await part.Content.ReadAsByteArrayAsync();
            var all = await File.ReadAllBytesAsync(loud);
            var missingFile = await http.GetAsync(url.Replace("/m/", "/m/0"));
            Check("songs are handed to a cast device over the network, in parts when it asks, and nothing else is",
                whole.StatusCode == HttpStatusCode.OK && whole.Content.Headers.ContentLength == all.Length && whole.Content.Headers.ContentType?.MediaType == "audio/wav"
                && part.StatusCode == HttpStatusCode.PartialContent && bytes.SequenceEqual(all.Skip(100).Take(100)) && part.Content.Headers.ContentRange?.Length == all.Length
                && missingFile.StatusCode == HttpStatusCode.NotFound && url.StartsWith("http://127.0.0.1:"), $"{url}: {(int)whole.StatusCode}, {(int)part.StatusCode} with {bytes.Length} bytes, {(int)missingFile.StatusCode}");
        }
        var frame = ChromecastDevice.Encode("sender-0", "receiver-0", "urn:x-cast:com.google.cast.receiver", "{\"type\":\"LAUNCH\"}");
        var (source, ns, json) = ChromecastDevice.Decode(frame.AsSpan(4));
        var answer = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, 2, 0, 0, 0, 0 };
        void Record(int type, byte[] data) { answer.AddRange(new byte[] { 1, (byte)'x', 0, (byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0, 120, (byte)(data.Length >> 8), (byte)data.Length }); answer.AddRange(data); }
        var txt = new List<byte>();
        foreach (var entry in new[] { "id=abc", "fn=Living Room TV", "md=Chromecast" }) { txt.Add((byte)entry.Length); txt.AddRange(Encoding.UTF8.GetBytes(entry)); }
        Record(16, txt.ToArray());
        Record(33, new byte[] { 0, 0, 0, 0, 0x1F, 0x49, 1, (byte)'h', 0 });
        var (castName, castPort) = CastFinder.ParseCastAnswer(answer.ToArray());
        Check("Chromecast messages and discovery answers are read and written correctly",
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(frame) == frame.Length - 4 && source == "sender-0" && ns.EndsWith("receiver") && json.Contains("LAUNCH") && castName == "Living Room TV" && castPort == 8009
            && CastFinder.CastQuestion().Length == 12 + 12 + 5 + 6 + 5, $"“{castName}” on port {castPort}");
        await CastToFakeRenderer(work);

        // ---------- the phone as a remote ----------
        Menus.Play(lib.Albums[0].Tracks);
        await Until(() => P.IsPlaying && P.OnEngine, 3000);
        var state = JsonDocument.Parse(RemoteControl.StateJson()).RootElement;
        var wasPlaying = P.IsPlaying;
        var toggled = RemoteControl.Command(Encoding.UTF8.GetBytes("{\"cmd\":\"toggle\"}")) && P.IsPlaying != wasPlaying;
        RemoteControl.Command(Encoding.UTF8.GetBytes("{\"cmd\":\"volume\",\"value\":0.35}"));
        var volumeSet = Math.Abs(P.Volume - 0.35) < 0.001;
        RemoteControl.Command(Encoding.UTF8.GetBytes("{\"cmd\":\"play\",\"value\":2}"));
        var jumped = P.CurrentIndex == 2;
        RemoteControl.Command(Encoding.UTF8.GetBytes($"{{\"cmd\":\"volume\",\"value\":{volumeBefore.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"));
        Check("the remote sees what is playing and works play, volume and the queue", state.GetProperty("title").GetString() == lib.Albums[0].Tracks[0].Title && state.GetProperty("queue").GetArrayLength() == lib.Albums[0].Tracks.Count
            && toggled && volumeSet && jumped && !RemoteControl.Command(Encoding.UTF8.GetBytes("{\"cmd\":\"format-disk\"}")), $"“{state.GetProperty("title").GetString()}”, queue of {state.GetProperty("queueCount").GetInt32()}");
        S.SyncCode = "246813";
        App.Sync.Start();
        if (App.Sync.IsRunning)
        {
            using var http = new HttpClient();
            var page = await http.GetAsync($"http://127.0.0.1:{SyncServer.Port}/remote");
            var html = await page.Content.ReadAsStringAsync();
            var refused = await http.GetAsync($"http://127.0.0.1:{SyncServer.Port}/remote/state");
            var ask = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{SyncServer.Port}/remote/state");
            ask.Headers.Add("X-Halcyon-Code", "246813");
            var allowed = await http.SendAsync(ask);
            var press = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{SyncServer.Port}/remote/command") { Content = new StringContent("{\"cmd\":\"next\"}") };
            press.Headers.Add("X-Halcyon-Code", "246813");
            var indexBefore = P.CurrentIndex;
            var pressed = await http.SendAsync(press);
            var art = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{SyncServer.Port}/remote/art");
            art.Headers.Add("X-Halcyon-Code", "246813");
            var cover = await http.SendAsync(art);
            Check("the remote page is served to the phone, and everything behind it needs the pairing code",
                page.StatusCode == HttpStatusCode.OK && html.Contains("Phoenix remote") && !html.Contains("246813") && refused.StatusCode == HttpStatusCode.Unauthorized && allowed.StatusCode == HttpStatusCode.OK
                && pressed.StatusCode == HttpStatusCode.OK && P.CurrentIndex == indexBefore + 1 && cover.StatusCode == HttpStatusCode.OK && cover.Content.Headers.ContentLength > 100,
                $"page {(int)page.StatusCode}, without code {(int)refused.StatusCode}, with code {(int)allowed.StatusCode}, cover {cover.Content.Headers.ContentLength} bytes");
            // one really wrong code over the network, then the rest counted directly (each real wrong guess also waits 0.4 s)
            var wrong = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{SyncServer.Port}/remote/state");
            wrong.Headers.Add("X-Halcyon-Code", "000000");
            var wrongAnswer = (await http.SendAsync(wrong)).StatusCode;
            var notYet = !SyncServer.LockedOut("127.0.0.1");
            for (var i = 0; i < 19; i++) SyncServer.NoteCode("127.0.0.1", false);
            var right = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{SyncServer.Port}/remote/state");
            right.Headers.Add("X-Halcyon-Code", "246813");
            var shut = (await http.SendAsync(right)).StatusCode;
            Check("an address that keeps sending wrong pairing codes is shut out for a while, even with the right code",
                wrongAnswer == HttpStatusCode.Unauthorized && notYet && shut == (HttpStatusCode)429 && SyncServer.LockedOut("127.0.0.1"), $"wrong code {(int)wrongAnswer}, after twenty {(int)shut}");
            SyncServer.NoteCode("127.0.0.1", true);
            App.Sync.Stop();
        }
        else Log.AppendLine($"INFO  the remote's server was not tried over the network (port {SyncServer.Port} is in use: {App.Sync.LastError})");
        S.SyncCode = "";
        P.Stop();

        // ---------- settings ----------
        win.Navigate("settings");
        var settings = win.Page<SettingsView>("settings");
        bool Shown(string panel) => ((FrameworkElement)settings.FindName(panel)).IsVisible;
        settings.ShowCategory("software", "Listening");
        await Task.Delay(200);
        Check("settings: Listening offers scrobbling, off to begin with", Shown("PanelListening") && ((CheckBox)settings.FindName("LbOn")).IsChecked == false && ((CheckBox)settings.FindName("LfmOn")).IsChecked == false);
        await Shot("36-settings-listening");
        settings.ShowCategory("software", "Playback");
        Check("settings: Playback offers volume levelling", Shown("PanelPlayback") && ((CheckBox)settings.FindName("LevelOn")).IsChecked == false);
        settings.ShowCategory("software", "About");
        await Task.Delay(200);
        Check("settings: About shows the version and the update check", Shown("PanelAbout") && ((TextBlock)settings.FindName("AboutVersion")).Text.Contains(Updater.CurrentText) && ((CheckBox)settings.FindName("UpdatesOn")).IsChecked == true);
        await Shot("37-settings-about");
        settings.ShowCategory("software", "Storage");
        Check("settings: Storage offers backup and restore", Shown("PanelStorage") && settings.FindName("BackupCovers") is CheckBox);
        settings.ShowCategory("software");
    }

    /// <summary>
    /// Plays through a pretend DLNA speaker that lives inside this test: it answers the same calls a real one does, so
    /// the whole path is exercised (describe, load, play, the song handed over the network, the end of a song moving
    /// the queue on, and coming back to this PC).
    /// </summary>
    static async Task CastToFakeRenderer(string work)
    {
        var P = App.Player;
        var port = 0;
        HttpListener? speaker = null;
        for (var tries = 0; tries < 20 && speaker == null; tries++)
        {
            port = 42300 + Random.Shared.Next(4000);
            var l = new HttpListener();
            l.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { l.Start(); speaker = l; } catch { }
        }
        if (speaker == null) { Log.AppendLine("INFO  casting to a pretend speaker was skipped (no local port could be opened)"); return; }
        var calls = new List<string>();
        var loadedUrl = "";
        var loadedMeta = "";
        var state = "STOPPED";
        var volume = "";
        _ = Task.Run(async () =>
        {
            while (speaker.IsListening)
            {
                HttpListenerContext c;
                try { c = await speaker.GetContextAsync(); } catch { return; }
                var reply = "";
                if (c.Request.Url!.AbsolutePath == "/desc.xml")
                    reply = "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><friendlyName>Pretend Speaker</friendlyName><serviceList>" +
                            "<service><serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType><controlURL>/ctl/av</controlURL></service>" +
                            "<service><serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType><controlURL>ctl/rc</controlURL></service></serviceList></device></root>";
                else
                {
                    using var reader = new StreamReader(c.Request.InputStream);
                    var soap = XDocument.Parse(await reader.ReadToEndAsync());
                    var action = soap.Descendants().First(x => x.Name.NamespaceName.StartsWith("urn:schemas-upnp-org:service"));
                    string Arg(string name) => action.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value ?? "";
                    lock (calls) calls.Add(action.Name.LocalName);
                    var inner = "";
                    switch (action.Name.LocalName)
                    {
                        case "SetAVTransportURI": loadedUrl = Arg("CurrentURI"); loadedMeta = Arg("CurrentURIMetaData"); break;
                        case "Play": state = "PLAYING"; break;
                        case "Pause": state = "PAUSED_PLAYBACK"; break;
                        case "Stop": state = "STOPPED"; break;
                        case "SetVolume": volume = Arg("DesiredVolume"); break;
                        case "GetTransportInfo": inner = $"<CurrentTransportState>{state}</CurrentTransportState>"; break;
                        case "GetPositionInfo": inner = "<TrackDuration>0:00:04</TrackDuration><RelTime>0:00:02</RelTime>"; break;
                    }
                    reply = $"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:{action.Name.LocalName}Response xmlns:u=\"{action.Name.NamespaceName}\">{inner}</u:{action.Name.LocalName}Response></s:Body></s:Envelope>";
                }
                var bytes = Encoding.UTF8.GetBytes(reply);
                c.Response.ContentType = "text/xml";
                c.Response.ContentLength64 = bytes.Length;
                await c.Response.OutputStream.WriteAsync(bytes);
                c.Response.Close();
            }
        });
        try
        {
            var location = $"http://127.0.0.1:{port}/desc.xml";
            using var http = new HttpClient();
            var described = DlnaDevice.Describe(XDocument.Parse(await http.GetStringAsync(location)), location);
            Check("a DLNA device's description is read: its name and where its controls are",
                described is { Name: "Pretend Speaker" } && described.TransportUrl == $"http://127.0.0.1:{port}/ctl/av" && described.RenderingUrl == $"http://127.0.0.1:{port}/ctl/rc", $"{described?.Name}: {described?.TransportUrl}");
            var tracks = App.Lib.Albums[0].Tracks;
            Menus.Play(tracks);
            await Until(() => P.OnEngine && P.IsPlaying, 3000);
            P.StartCast(new CastTarget("Pretend Speaker", "DLNA", "127.0.0.1", port, location).Connect());
            var loaded = await Until(() => loadedUrl != "" && state == "PLAYING" && P.OnCast);
            var fetched = loadedUrl == "" ? Array.Empty<byte>() : await http.GetByteArrayAsync(loadedUrl);
            await Until(() => volume != "", 2000);
            Check("playing on a cast device: the song is loaded there with its name, it can fetch the file from this PC, and this PC falls silent",
                loaded && fetched.Length == new FileInfo(tracks[0].SourceId).Length && loadedMeta.Contains(tracks[0].Title) && !P.OnEngine && App.Win.ModeText.Contains("Pretend Speaker") && volume != "",
                $"{loadedUrl}, {fetched.Length} bytes, volume {volume}, shown as “{App.Win.ModeText}”");
            await Until(() => P.Cast is { Loaded: true }, 4000);
            P.Toggle();
            var paused = await Until(() => state == "PAUSED_PLAYBACK", 3000);
            P.Toggle();
            await Until(() => state == "PLAYING", 3000);
            await Task.Delay(1300);
            var firstUrl = loadedUrl;
            state = "STOPPED";   // the speaker reached the end of the song
            var movedOn = await Until(() => P.CurrentIndex == 1 && loadedUrl != firstUrl && state == "PLAYING", 8000);
            Check("the buttons work the device, and when a song ends there the next one in the queue is sent", paused && movedOn, $"song {P.CurrentIndex + 1} of {tracks.Count} now on the device");
            P.StopCast();
            var back = await Until(() => !P.OnCast && P.Cast == null && P.OnEngine && !P.IsPlaying && P.CurrentIndex == 1, 4000);
            Check("stopping the cast brings the same song back to this PC, paused", back && App.Win.ModeText == "", $"song {P.CurrentIndex + 1}, playing here: {P.OnEngine}");
        }
        finally
        {
            P.StopCast(resumeHere: false);
            P.Stop();
            try { speaker.Stop(); } catch { }
        }
    }
}
