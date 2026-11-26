using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
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
        Check("a saved station is listed, and is an endless stream with no length", radio.SavedShown == 1 && lib.Stations.Count == 1 && item.Station != null && item.Track == null && item.DurationMs == 0 && CastMedia.Can(item));
        await Shot("31-radio");
        // the sources tab: browse the directory and add from it
        var genres = Radio.ParseGenres(JsonDocument.Parse("""[{"name":"pop","stationcount":3000},{"name":"x","stationcount":900},{"name":"rare thing","stationcount":3},{"name":" jazz ","stationcount":800}]""").RootElement);
        var countries = Radio.ParseCountries(JsonDocument.Parse("""[{"name":"Norway","iso_3166_1":"NO","stationcount":210},{"name":"Austria","iso_3166_1":"AT","stationcount":400},{"name":"Nowhere","iso_3166_1":"","stationcount":5}]""").RootElement);
        Check("the directory's genres and countries are read for browsing", genres.Select(g => g.Name).SequenceEqual(new[] { "pop", "jazz" }) && countries.Select(c => c.Key).SequenceEqual(new[] { "AT", "NO" }) && genres[0].CountText == "3,000",
            $"{string.Join(", ", genres.Select(g => g.Name))}; {string.Join(", ", countries.Select(c => c.Name))}");
        radio.ShowTab("sources");
        var fromDirectory = new List<Station> { new() { Name = "Blues Hour", Url = "https://stream.example/blues", Tags = "blues", Country = "US" }, lib.Stations[0] };
        radio.Show(fromDirectory, "BLUES");
        await Task.Delay(200);
        var savedBefore = lib.Stations.Count;
        radio.Save(fromDirectory[0]);
        Check("the radio page has a sources tab where stations are browsed and added to my stations",
            radio.Tab == "sources" && radio.SourcesVisible && radio.Results.Count == 2 && savedBefore == 1 && lib.Stations.Count == 2 && lib.Stations.Any(x => x.Name == "Blues Hour") && radio.SavedShown == 2,
            $"{radio.Results.Count} shown, {lib.Stations.Count} saved");
        await Shot("31-radio-sources");
        lib.RemoveStation(lib.Stations.First(x => x.Name == "Blues Hour"));
        radio.ShowTab("mine");
        win.Navigate("quickplay");
        await Task.Delay(300);
        bool Pinned() => ((ItemsControl)win.Page<QuickplayView>("quickplay").FindName("PinsList")).Items.Cast<QuickItem>().Any(q => q.Title == "Jazz FM" && q.Subtitle == "radio");
        var pinned = Pinned();
        Check("a station can be pinned to quickplay", pinned);
        // radio switched off in settings
        win.Navigate("radio");
        S.RadioEnabled = false;
        win.ShowRadioTab();
        lib.NotifyChanged();
        var leftPage = win.CurrentPage != "radio";
        win.Navigate("radio");
        var hidden = !win.RadioTabShown && leftPage && win.CurrentPage != "radio";
        win.Navigate("quickplay");
        await Task.Delay(200);
        var pinHidden = !Pinned();
        S.RadioEnabled = true;
        win.ShowRadioTab();
        lib.NotifyChanged();
        await Task.Delay(200);
        Check("radio can be switched off in settings: the tab and pinned stations go, and come back with the stations kept", hidden && pinHidden && win.RadioTabShown && Pinned() && lib.Stations.Count == 1,
            $"hidden {hidden}, pin hidden {pinHidden}, back {win.RadioTabShown}");
        // a real station, if the network is there: https streams are what failed in Windows' own player
        try
        {
            var live = (await Radio.SearchAsync("BBC World Service")).Where(x => x.Url.StartsWith("https://")).Take(4).ToList();
            var tuned = "";
            foreach (var st in live)
            {
                Radio.Play(st);
                if (await Until(() => P.OnEngine && P.Engine != null && P.Engine.Position.TotalSeconds > 1.5, 15000)) { tuned = st.Name + "  " + st.Url; break; }
            }
            Log.AppendLine(live.Count == 0 ? "INFO  live radio: the directory returned no https station to try" : tuned != "" ? $"INFO  live radio: an https station plays through the audio engine: {tuned}" : $"INFO  live radio: none of {live.Count} https stations started within 15 s");
            P.Stop();
        }
        catch (Exception ex) { Log.AppendLine("INFO  live radio was not tried (no network): " + ex.Message); }
        Check("radio plays through the audio engine even when the engine is switched off for music", PlaysOnEngine());
        bool PlaysOnEngine()
        {
            var was = S.AudioEngine;
            S.AudioEngine = false;
            Radio.Play(new Station { Name = "Local tone", Url = quiet });
            var on = P.OnEngine;
            P.Stop();
            S.AudioEngine = was;
            return on;
        }
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
        string Rel(string v, bool pre = false) => "{\"tag_name\":\"v" + v + "\",\"prerelease\":" + (pre ? "true" : "false") + ",\"assets\":[{\"name\":\"Phoenix-" + v + "-win-x64-setup.exe\",\"size\":1,\"browser_download_url\":\"https://github.com/"
            + Updater.Repo + "/releases/download/v" + v + "/Phoenix-" + v + "-win-x64-setup.exe\"}]}";
        var published = Updater.ParseAll(JsonDocument.Parse($"[{Rel("2.1.0")},{Rel("2.3.0")},{Rel("2.2.5", pre: true)},{Rel("2.2.0")},{Rel("1.9.0")}]").RootElement, "x64");
        var back = Updater.Before(published, new Version(2, 3, 0));
        Check("going back one version picks the release just before the one you have, never a pre-release", published.Count == 4 && published[0].VersionText == "2.3.0" && back is { VersionText: "2.2.0" }
            && Updater.Before(published, new Version(1, 9, 0)) == null && Updater.Before(published, new Version(2, 2, 0)) is { VersionText: "2.1.0" }, $"from 2.3.0 back to {back?.VersionText}");
        Check("updating by itself is off until asked for, and nothing waits to be installed", !S.AutoUpdate && S.KeepPrevious && Updater.Waiting == null && Updater.Kept == null);

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

        await RunTabsSortingPodcasts(work, loud);

        // ---------- settings ----------
        win.Navigate("settings");
        var settings = win.Page<SettingsView>("settings");
        bool Shown(string panel) => ((FrameworkElement)settings.FindName(panel)).IsVisible;
        settings.ShowCategory("software", "Listening");
        await Task.Delay(200);
        Check("settings: Listening has the switch for radio, on to begin with", Shown("PanelListening") && ((CheckBox)settings.FindName("RadioOn")).IsChecked == true);
        Check("settings: Listening offers scrobbling, off to begin with", Shown("PanelListening") && ((CheckBox)settings.FindName("LbOn")).IsChecked == false && ((CheckBox)settings.FindName("LfmOn")).IsChecked == false);
        await Shot("36-settings-listening");
        settings.ShowCategory("software", "Playback");
        Check("settings: Playback offers volume levelling", Shown("PanelPlayback") && ((CheckBox)settings.FindName("LevelOn")).IsChecked == false);
        settings.ShowCategory("software", "Updates");
        await Task.Delay(200);
        Check("settings: Updates shows the version, the daily check, updating by itself (off) and going back", Shown("PanelAbout") && ((TextBlock)settings.FindName("AboutVersion")).Text.Contains(Updater.CurrentText)
            && ((CheckBox)settings.FindName("UpdatesOn")).IsChecked == true && ((CheckBox)settings.FindName("AutoUpdateOn")).IsChecked == false && ((CheckBox)settings.FindName("KeepPreviousOn")).IsChecked == true
            && settings.FindName("RevertBtn") is Button);
        await Shot("37-settings-updates");
        settings.ShowCategory("software", "Storage");
        Check("settings: Storage offers backup and restore", Shown("PanelStorage") && settings.FindName("BackupCovers") is CheckBox);
        settings.ShowCategory("software");
    }

    /// <summary>Switching collection tabs on and off, the sorting and filtering bar on each page, and the podcast sources.</summary>
    static async Task RunTabsSortingPodcasts(string work, string tone)
    {
        var win = App.Win;
        var lib = App.Lib;
        var S = App.Settings;

        // ---------- every collection tab can be switched off ----------
        win.Navigate("settings");
        var settings = win.Page<SettingsView>("settings");
        settings.ShowCategory("software", "Display");
        await Task.Delay(200);
        var allTabs = win.TabsShown;
        win.Navigate("books");
        settings.SetTab("books", false);
        settings.SetTab("pictures", false);
        var hidden = win.TabsShown;
        var leftBooks = win.CurrentPage != "books";
        win.Navigate("books");
        var stayedOut = win.CurrentPage != "books";
        foreach (var tab in Settings.Tabs.Where(t => t != "videos")) settings.SetTab(tab, false);
        var lastOne = win.TabsShown;
        var refused = !settings.SetTab("videos", false);
        foreach (var tab in Settings.Tabs) settings.SetTab(tab, true);
        Check("every collection tab can be switched off in settings (all but the last one), and comes back",
            allTabs.SequenceEqual(Settings.Tabs) && settings.TabBoxes.Count == Settings.Tabs.Length && hidden.SequenceEqual(Settings.Tabs.Where(t => t is not ("books" or "pictures"))) && leftBooks && stayedOut
            && lastOne.SequenceEqual(new[] { "videos" }) && refused && win.TabsShown.SequenceEqual(Settings.Tabs) && S.TabsOff.Count == 0,
            $"shown: {string.Join(", ", allTabs)}; with two off: {string.Join(", ", hidden)}; last one left: {string.Join(", ", lastOne)}");
        win.Navigate("settings");
        settings.ShowCategory("software", "Display");
        await Task.Delay(200);
        await Shot("38-settings-tabs");

        // ---------- streaming services anywhere, and tabs of your own ----------
        var youtube = WebServices.Find("youtube")!;
        var spotify = WebServices.Find("spotify")!;
        var webBefore = (S.WebServicesOn.ToList(), new Dictionary<string, string>(S.ServicePlaces));
        WebServices.Set(youtube, true);
        WebServices.Set(spotify, true);
        await Task.Delay(200);
        var defaults = WebServices.PlaceOf(youtube) == "videos" && WebServices.PlaceOf(spotify) == WebServices.OwnTab && win.WebTabs.Contains("web:spotify");
        var mine = settings.AddTab("Anime");
        WebServices.SetPlace(youtube, mine!.Page);
        WebServices.SetPlace(spotify, "music");
        await Task.Delay(300);
        win.Navigate(mine.Page);
        await Task.Delay(300);
        var tabView = win.Page<CustomTabView>(mine.Page);
        var inTab = tabView.Services.SequenceEqual(new[] { "web:youtube" }) && win.CustomTabsShown.SequenceEqual(new[] { mine.Page }) && win.LitTab == mine.Page;
        await Shot("42-custom-tab");
        win.Navigate("music");
        await Task.Delay(300);
        var musicView = win.Page<MusicView>("music");
        var musicRow = (Panel)musicView.FindName("TopBar");
        var underMusic = ServiceTabs.In(musicRow).SequenceEqual(new[] { "web:spotify" }) && !win.WebTabs.Contains("web:spotify");
        // same style and line as the page's own tabs
        var own = musicRow.Children.OfType<RadioButton>().First(rb => rb.Uid != "service-tab");
        var svc = musicRow.Children.OfType<RadioButton>().First(rb => rb.Uid == "service-tab");
        var aligned = svc.Style == own.Style && Math.Abs(svc.TranslatePoint(new Point(0, 0), musicRow).Y - own.TranslatePoint(new Point(0, 0), musicRow).Y) < 1 && svc.FontSize == own.FontSize;
        var shuffle = (Button)musicView.FindName("ShuffleAllBtn");
        var shuffleAligned = shuffle.FontSize == own.FontSize && Math.Abs(shuffle.TranslatePoint(new Point(0, 0), musicRow).Y - own.TranslatePoint(new Point(0, 0), musicRow).Y) < 1;
        var counts = ((TextBlock)musicView.FindName("CountText")).Text;
        await Shot("43-musicView-tabs");
        win.Navigate("web:spotify");
        await Task.Delay(300);
        var litMusic = win.LitTab == "music";
        WebServices.RemoveTab(mine);
        await Task.Delay(200);
        var back = WebServices.PlaceOf(youtube) == "videos" && win.CustomTabsShown.Count == 0 && ServiceTabs.In((Panel)win.Page<VideosView>("videos").FindName("TabStrip")).Contains("web:youtube");
        Check("a streaming service can go in a tab of its own, under any collection, or in a tab you make; removing a tab puts its services back",
            defaults && inTab && underMusic && litMusic && back, $"defaults {defaults}, in your tab {inTab}, under music {underMusic}, music lit on its page {litMusic}, back after removing {back}");
        Check("services under a collection are tabs of the same style and on the same line as its own, and shuffle all is too", aligned && shuffleAligned, $"service tab aligned {aligned}, shuffle all aligned {shuffleAligned}");
        Check("the music page counts its total playing time", counts.Contains("songs  ·  ") && (counts.Contains("minute") || counts.Contains("hour") || counts.Contains("day")), counts);
        Check("long stretches of time read in words", Fmt.Long(90_000) == "1 minute" && Fmt.Long(3 * 3600_000L + 5 * 60_000) == "3 hours 5 minutes" && Fmt.Long(26 * 3600_000L) == "1 day 2 hours" && Fmt.Long(48 * 3600_000L) == "2 days");
        S.WebServicesOn = webBefore.Item1;
        S.ServicePlaces = webBefore.Item2;
        WebServices.RaiseChanged();
        win.Navigate("music");

        // ---------- sorting and filtering ----------
        win.Navigate("music");
        var music = win.Page<MusicView>("music");
        music.SetMode("albums");
        var everyAlbum = music.ShownAlbums.Count;
        music.Options.Set(text: "later");
        var filtered = music.ShownAlbums.ToList();
        music.Options.Set(text: "", sort: "title", descending: true);
        var backwards = music.ShownAlbums.Select(a => a.Title).ToList();
        var remembered = S.Views.GetValueOrDefault("music");
        music.Options.Set(sort: "year", descending: false);
        var years = music.ShownAlbums.Select(a => a.Year).ToList();
        music.SetMode("songs");
        music.Options.Set(sort: "", text: "song 3");
        var songs = music.ShownSongs.ToList();
        await Shot("39-music-filtered");
        music.Options.Clear();
        music.SetMode("artists");
        Check("music can be filtered by name and sorted by title, year and more, either way round",
            everyAlbum == lib.Albums.Count && filtered.Count > 0 && filtered.Count < everyAlbum && filtered.All(a => (a.Title + " " + a.Artist).Contains("later", StringComparison.OrdinalIgnoreCase) || a.Tracks.Any(t => t.Title.Contains("later", StringComparison.OrdinalIgnoreCase)))
            && backwards.SequenceEqual(backwards.OrderByDescending(t => t, StringComparer.OrdinalIgnoreCase)) && remembered == "title|1|" && years.SequenceEqual(years.OrderBy(y => y))
            && songs.Count > 0 && songs.Count < lib.Tracks.Count && songs.All(t => music.Options.Matches(t.Title, t.Artist, t.Album) || true) && songs.All(t => (t.Title + t.Artist + t.Album).Contains('3')) && !music.Options.Active,
            $"{filtered.Count} of {everyAlbum} albums match “later”; by title backwards: {string.Join(", ", backwards.Take(3))}…; {songs.Count} songs match “song 3”");

        win.Navigate("videos");
        var videos = win.Page<VideosView>("videos");
        videos.ShowTab("all");
        var everyVideo = videos.ShownCount;
        videos.Options.Set(text: "beach");
        var beach = videos.ShownCount;
        videos.Options.Set(text: "", show: "server");
        var onServers = videos.ShownCount;
        videos.Options.Clear();
        win.Navigate("books");
        var books = win.Page<BooksView>("books");
        books.ShowTab("all");
        var everyBook = books.ShownBooks.Count;
        books.Options.Set(sort: "title");
        var titles = books.ShownBooks.Select(b => b.Title).ToList();
        books.Options.Set(show: "done");
        var done = books.ShownBooks.ToList();
        books.Options.Clear();
        Check("videos and books have the same bar: filter by name, show a kind, sort",
            everyVideo == lib.Videos.Count && beach == 1 && onServers == lib.Videos.Count(v => v.Source != "local") && everyBook == lib.Books.Count
            && titles.SequenceEqual(titles.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)) && done.All(b => b.Finished) && done.Count == lib.Books.Count(b => b.Finished) && books.ShownBooks.Count == everyBook,
            $"{beach} of {everyVideo} videos match “beach”; {everyBook} books, {done.Count} finished");
        win.Navigate("pictures");
        var pictures = win.Page<PicturesView>("pictures");
        await Until(() => pictures.Shown.Count > 0, 4000);
        var everyPicture = pictures.Shown.Count;
        var firstName = everyPicture > 0 ? pictures.Shown[0].Name : "";
        pictures.Options.Set(descending: true);
        var lastFirst = everyPicture > 1 && pictures.Shown[^1].Name == firstName;
        pictures.Options.Set(descending: false, text: System.IO.Path.GetFileNameWithoutExtension(firstName));
        var onlyThat = pictures.Shown.Count >= 1 && pictures.Shown.Count < Math.Max(2, everyPicture) && pictures.Shown.All(x => x.Name.Contains(System.IO.Path.GetFileNameWithoutExtension(firstName), StringComparison.OrdinalIgnoreCase));
        pictures.Options.Clear();
        Check("pictures can be turned round and filtered by name", everyPicture > 1 && lastFirst && onlyThat && pictures.Shown.Count == everyPicture, $"{everyPicture} pictures in the folder");

        // ---------- podcast sources ----------
        var top = PodcastDirectory.ParseAppleTop(JsonDocument.Parse("""
            {"feed":{"entry":[{"im:name":{"label":"Show One"},"im:artist":{"label":"Maker"},"im:image":[{"label":"https://a.example/55.png"},{"label":"https://a.example/170.png"}],
              "category":{"attributes":{"label":"Technology"}},"id":{"attributes":{"im:id":"123456"}}},{"im:name":{"label":""},"id":{"attributes":{"im:id":"9"}}}]}}
            """).RootElement);
        var found = PodcastDirectory.ParseAppleSearch(JsonDocument.Parse("""{"results":[{"collectionName":"Radio Hour","artistName":"Studio","feedUrl":"https://feeds.example/hour","artworkUrl600":"https://a.example/600.jpg","primaryGenreName":"Science","trackCount":310,"collectionId":77}]}""").RootElement);
        var fyyd = PodcastDirectory.ParseFyyd(JsonDocument.Parse("""{"data":[{"title":"Open Show","author":"Someone","xmlURL":"https://feeds.example/open","imgURL":"https://i.example/a.jpg","language":"en","episode_count":12},{"title":"No feed","xmlURL":""}]}""").RootElement);
        var opml = PodcastDirectory.ParseOpml("""<opml version="2.0"><body><outline text="Group"><outline type="rss" text="First" xmlUrl="https://feeds.example/first"/><outline title="Second" xmlUrl="https://feeds.example/second"/></outline><outline text="No feed here"/></body></opml>""");
        Check("podcast directories are read: Apple's charts and search, fyyd, and an OPML list",
            top.Count == 1 && top[0] is { Title: "Show One", Author: "Maker", AppleId: "123456", FeedUrl: "", Detail: "Technology" } && top[0].Art.EndsWith("170.png")
            && found.Count == 1 && found[0].FeedUrl == "https://feeds.example/hour" && found[0].Detail == "Science  ·  310 episodes"
            && fyyd.Count == 1 && fyyd[0].Subtitle == "Someone  ·  EN  ·  12 episodes" && opml.Select(o => o.Title).SequenceEqual(new[] { "First", "Second" }),
            $"{top.Count} from the chart, {found.Count} from search, {fyyd.Count} from fyyd, {opml.Count} from the list");
        Check("an episode counts as video when its feed says so or its file is a video", PodcastService.LooksLikeVideo("video/mp4", "https://x.example/a") && PodcastService.LooksLikeVideo("", "https://x.example/ep.m4v?x=1")
            && !PodcastService.LooksLikeVideo("audio/mpeg", "https://x.example/ep.mp4") && !PodcastService.LooksLikeVideo("", "https://x.example/ep.mp3"));

        win.Navigate("podcasts");
        var pod = win.Page<PodcastsView>("podcasts");
        var seriesBefore = lib.Podcasts.Count;
        pod.ShowTab("sources");
        await Task.Delay(200);
        var sources = pod.Sources;
        var builtIn = sources.SourcesShown;
        var genres = sources.GenresShown;
        sources.Show(found.Concat(fyyd).ToList(), "TEST");
        var (feedPodcast, feedEpisodes) = PodcastService.Parse("""
            <rss version="2.0"><channel><title>Open Show</title><description>A show</description>
              <item><title>Watch this</title><guid>v1</guid><pubDate>Mon, 02 Nov 2026 10:00:00 GMT</pubDate><enclosure url="https://media.example/one.mp4" type="video/mp4"/></item>
              <item><title>Hear this</title><guid>a1</guid><pubDate>Mon, 26 Oct 2026 10:00:00 GMT</pubDate><enclosure url="https://media.example/two.mp3" type="audio/mpeg"/></item>
            </channel></rss>
            """, "https://feeds.example/open");
        sources.Add(feedPodcast, feedEpisodes);
        await Task.Delay(200);
        await Shot("40-podcast-sources");
        var subscribed = lib.Podcasts.Count == seriesBefore + 1 && lib.Podcasts.Any(x => x.FeedUrl == "https://feeds.example/open");
        var list = System.IO.Path.Combine(work, "my-list.opml");
        File.WriteAllText(list, PodcastDirectory.ToOpml(lib.Podcasts));
        sources.AddSource("My list", list);
        var ownShown = await Until(() => sources.Hits.Count == lib.Podcasts.Count && sources.Source.StartsWith("own:"), 4000);
        var withOwn = sources.SourcesShown;
        S.PodcastSources.Clear();
        sources.Choose("apple", browse: false);
        Check("the podcasts page has a sources tab: Apple Podcasts with its genres, fyyd, and lists of your own, from which podcasts are subscribed to",
            pod.Tab == "sources" && builtIn == 2 && genres == PodcastDirectory.AppleGenres.Length + 1 && subscribed && ownShown && withOwn == 3 && sources.SourcesShown == 2,
            $"{builtIn} built-in sources, {genres} genre choices, {withOwn} with your own; your list held {sources.Hits.Count} podcasts");

        pod.ShowTab("mine");
        var added = lib.Podcasts.First(x => x.FeedUrl == "https://feeds.example/open");
        ((ListBox)pod.FindName("SeriesList")).SelectedItem = added;
        await Task.Delay(150);
        var both = pod.ShownEpisodes.Count;
        pod.Options.Set(show: "video");
        var onlyVideo = pod.ShownEpisodes.ToList();
        pod.Options.Set(show: "", text: "hear");
        var byName = pod.ShownEpisodes.ToList();
        var item = Player.From(onlyVideo.FirstOrDefault() ?? new Episode(), added);
        await Shot("41-podcasts-video");
        pod.Options.Clear();
        Check("video podcasts: video episodes are marked, can be shown by themselves, and play as video",
            both == 2 && onlyVideo.Count == 1 && onlyVideo[0] is { IsVideo: true, Title: "Watch this" } && onlyVideo[0].KindGlyph != "" && byName.Count == 1 && byName[0].Title == "Hear this" && item.IsVideo
            && PodcastSync.FileFor(added, onlyVideo[0], onlyVideo[0].Url).EndsWith(".mp4"), $"{both} episodes, {onlyVideo.Count} video; a download would be saved as {System.IO.Path.GetFileName(PodcastSync.FileFor(added, onlyVideo[0], onlyVideo[0].Url))}");
        lib.Db.DeletePodcast(added.Id);
        lib.Podcasts = lib.Db.LoadPodcasts();
        pod.ShowTab("mine");

        // an https episode is handed to Windows' player from this PC: checked here against a file served locally
        var far = CastServer.Publish(tone, "127.0.0.1");
        var near = LocalProxy.Url(far);
        using (var http = new HttpClient())
        {
            var ask = new HttpRequestMessage(HttpMethod.Get, near);
            ask.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(200, 399);
            var part = await http.SendAsync(ask);
            var bytes = await part.Content.ReadAsByteArrayAsync();
            var whole = await File.ReadAllBytesAsync(tone);
            var full = await http.GetAsync(near);
            Check("a stream is passed through this PC part by part, so an episode can start at once and be skipped around in",
                near.StartsWith("http://127.0.0.1:") && near != far && part.StatusCode == HttpStatusCode.PartialContent && bytes.SequenceEqual(whole.Skip(200).Take(200))
                && part.Content.Headers.ContentRange?.Length == whole.Length && full.Content.Headers.ContentLength == whole.Length && LocalProxy.Served >= 2,
                $"{(int)part.StatusCode} with {bytes.Length} bytes of {whole.Length}");
        }
        var anAlbum = lib.Albums[0];
        var offered = Menus.InfoSources(anAlbum).Count;
        var deezer = Metadata.Setting("deezer");
        var wasOn = deezer.Enabled;
        deezer.Enabled = false;
        var without = Menus.InfoSources(anAlbum);
        Metadata.SetSource(anAlbum, "deezer");
        var tiedStillShown = Menus.InfoSources(anAlbum).Any(x => x.Id == "deezer");
        Metadata.SetSource(anAlbum, Metadata.Auto);
        deezer.Enabled = wasOn;
        Check("an info source switched off in settings is not offered in an album's right-click menu (unless the album is tied to it)",
            without.All(x => x.Id != "deezer") && without.Count == offered - (wasOn ? 1 : 0) && tiedStillShown, $"{offered} offered, {without.Count} with Deezer off");
        // MusicBrainz saying "busy" twice, then answering
        var asked = 0;
        var realAsk = MusicBrainzSource.Ask;
        var realDelay = MusicBrainzSource.RetryDelay;
        MusicBrainzSource.RetryDelay = 20;
        MusicBrainzSource.Ask = _ => ++asked <= 2 ? throw new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable) : Task.FromResult(JsonDocument.Parse("{\"releases\":[{\"id\":\"x\",\"title\":\"Found\"}]}").RootElement);
        var hits = await new MusicBrainzSource().SearchAsync(null, "Artist", "Found");
        var afterBusy = asked;
        MusicBrainzSource.Ask = _ => throw new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable);
        var gaveUp = "";
        try { await new MusicBrainzSource().SearchAsync(null, "Artist", "Found"); } catch (Exception ex) { gaveUp = ex.Message; }
        MusicBrainzSource.Ask = realAsk;
        MusicBrainzSource.RetryDelay = realDelay;
        Check("when MusicBrainz says it is busy it is asked again, and after several tries the reason is said plainly", hits.Count == 1 && hits[0].Title == "Found" && afterBusy == 3 && gaveUp.StartsWith("MusicBrainz is busy"),
            $"answered on try {afterBusy}; when it never answers: “{gaveUp}”");
        var station = Player.From(new Station { Name = "News Radio", Url = "https://stream.example/live", Tags = "news" });
        var load = JsonDocument.Parse(JsonSerializer.Serialize(ChromecastDevice.LoadMessage(station, "https://stream.example/live", "", "audio/aac", 12, 5))).RootElement;
        var song = JsonDocument.Parse(JsonSerializer.Serialize(ChromecastDevice.LoadMessage(Player.From(lib.Tracks[0]), "http://pc/a.mp3", "http://pc/a.jpg", "audio/mpeg", 12, 6))).RootElement;
        Check("a radio station is sent to a Chromecast as a live stream of the right kind, a song as a file with its place",
            load.GetProperty("media").GetProperty("streamType").GetString() == "LIVE" && load.GetProperty("media").GetProperty("contentType").GetString() == "audio/aac" && !load.TryGetProperty("currentTime", out _)
            && load.GetProperty("media").GetProperty("metadata").GetProperty("title").GetString() == "News Radio"
            && song.GetProperty("media").GetProperty("streamType").GetString() == "BUFFERED" && song.GetProperty("currentTime").GetDouble() == 12 && song.GetProperty("media").GetProperty("metadata").GetProperty("images")[0].GetProperty("url").GetString() == "http://pc/a.jpg"
            && CastMedia.StreamType("audio/aacp", "https://x.example/stream") == "audio/aac" && CastMedia.StreamType("application/vnd.apple.mpegurl; charset=utf-8", "https://x.example/live") == "application/x-mpegURL"
            && CastMedia.StreamType("", "https://x.example/live.m3u8?token=1") == "application/x-mpegURL" && CastMedia.StreamType("audio/mpeg", "https://x.example/") == "audio/mpeg"
            && DlnaDevice.Didl("u", "audio/aac", "t", "a", "b", "", live: true).Contains("audioBroadcast"));
        var themedBefore = WindowTheme.Applied;
        var wasTheme = S.Theme;
        S.Theme = "dark";
        App.ApplyTheme("dark");
        var dialog = Make.Window("A dialog", 300, 200);
        dialog.Show();
        await Task.Delay(200);
        var darkBar = WindowTheme.LastCaption;
        dialog.Close();
        S.Theme = wasTheme;
        App.ApplyTheme(wasTheme);
        Check("a dialog's title bar follows the dark setting instead of staying white", WindowTheme.Applied > themedBefore + 1 && darkBar.R < 40 && darkBar.G < 40 && darkBar.B < 40 && WindowTheme.LastCaption.R > 200 == (wasTheme != "dark"),
            $"title bar colour in dark mode #{darkBar.R:X2}{darkBar.G:X2}{darkBar.B:X2}");
        Check("the tag editor names its file-name layouts in words", TagFixes.InWords("%n - %a - %t") == "named:  number - artist - title");
        await ThemesAsync();
    }

    /// <summary>Your own themes: saved, found, shown everywhere (now playing too), checked for contrast, imported, exported, removed.</summary>
    static async Task ThemesAsync()
    {
        static Color Brush(string key) => ((System.Windows.Media.SolidColorBrush)Application.Current.Resources[key]).Color;
        var S = App.Settings;
        var wasTheme = S.Theme;
        var mine = new Theme { Name = "smoke sunset", Base = "dark" };
        mine.Set("Accent", Color.FromRgb(0x22, 0xAA, 0x55));
        mine.Set("NowFg", Color.FromRgb(0xFF, 0xEE, 0xCC));
        mine.Set("Fg", Themes.Default("Fg", dark: true));   // the same as the base: not kept
        mine.TextFont = "Consolas";
        var id = Themes.Save(mine);
        var found = Themes.Find(id);
        Check("a theme of yours is saved as a file and read back, keeping only the colours it changes",
            File.Exists(Path.Combine(Themes.Folder, id + ".json")) && found.Name == "smoke sunset" && found.IsDark && found.Colours.Count == 2 && found.TextFont == "Consolas"
            && Themes.All().Any(t => t.Id == id) && found.Get("Bg") == Themes.Default("Bg", dark: true));

        App.ApplyTheme(id);
        await Shot("42-theme-custom");
        var grad = (System.Windows.Media.LinearGradientBrush)Application.Current.Resources["AccentGrad"];
        var nowFg = ((System.Windows.Media.SolidColorBrush)App.Win.NowLayer.Resources["Fg"]).Color;
        Check("applying a theme changes the accent, the bars, the fonts and the now playing colours",
            Brush("Accent") == Color.FromRgb(0x22, 0xAA, 0x55) && Brush("Bg") == Themes.Default("Bg", dark: true) && grad.GradientStops[0].Color == Themes.Default("AccentStart", false)
            && ((System.Windows.Media.FontFamily)Application.Current.Resources["TextFont"]).Source.StartsWith("Consolas,") && nowFg == Color.FromRgb(0xFF, 0xEE, 0xCC)
            && Themes.Current.IsDark && Themes.TextFontName == "Consolas", $"accent #{Brush("Accent")}, now fg {nowFg}");

        // the designer shows its copy live; closing it without saving brings the theme back
        string? designed = "unset";
        ThemeDesigner.Open(Themes.Current, r => designed = r);
        var designer = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.Title == "Design a theme");
        if (designer != null) await Shot("43-theme-designer", designer);
        var whileOpen = Themes.Designing && !ReferenceEquals(Themes.Current, found);
        designer?.Close();
        await Task.Delay(50);
        Check("the designer opens on a copy and closing it without saving puts the theme back",
            designer != null && whileOpen && designed == null && !Themes.Designing && Themes.Current.Id == id && Brush("Accent") == Color.FromRgb(0x22, 0xAA, 0x55));

        // an edit made by hand to the theme's file shows by itself
        var edited = Themes.Find(id);
        edited.Set("Accent", Color.FromRgb(0x10, 0x20, 0xF0));
        File.WriteAllText(Path.Combine(Themes.Folder, id + ".json"), JsonSerializer.Serialize(edited, new JsonSerializerOptions { WriteIndented = true }));
        var reloaded = await Until(() => Brush("Accent") == Color.FromRgb(0x10, 0x20, 0xF0), 4000);
        Check("saving the theme file in another program shows the change straight away", reloaded, $"accent now {Brush("Accent")}");

        Check("contrast is worked out the usual way and unreadable colours are pointed out",
            Math.Abs(Themes.Contrast(Colors.Black, Colors.White) - 21) < 0.01 && Math.Abs(Themes.Contrast(Colors.Red, Colors.Red) - 1) < 0.01
            && Themes.Problems(Themes.Light).Count == 0 && Themes.Problems(Themes.Dark).Count == 0
            && Themes.Problems(new Theme { Colours = { ["Fg"] = "#EEEEEE" } }).Any(p => p.What == "text on the page"));

        var export = Path.Combine(Path.GetTempPath(), "phoenix-smoke-theme.json");
        Themes.Export(found, export);
        var exported = Themes.Read(export);
        var notTheme = Path.Combine(Path.GetTempPath(), "phoenix-smoke-not-theme.json");
        File.WriteAllText(notTheme, "{\"hello\": 1}");
        var imported = Themes.Import(export);
        Check("an exported theme spells out every colour, imports as a theme of its own, and other JSON is turned away",
            exported != null && exported.Colours.Count == Themes.Keys.Length && imported != null && imported.Id != id && imported.Get("Accent") == Color.FromRgb(0x22, 0xAA, 0x55)
            && Themes.Import(notTheme) == null);

        Themes.Delete(id);
        if (imported != null) Themes.Delete(imported.Id);
        App.ApplyTheme(id);   // gone: falls back to light
        Check("a theme whose file is gone falls back to light", Themes.Current.Id == "light" && Brush("Bg") == Colors.White && !Themes.All().Any(t => t.Id == id));
        Themes.Apply(Themes.Find("system"));
        Check("a system theme is built in, and is light or dark as Windows is set for apps",
            string.Join(",", Themes.All().Take(3).Select(t => t.Id)) == "light,dark,system" && Themes.Current.Id == "system" && Themes.Current.IsDark == Themes.SystemIsDark && Themes.Current.BuiltIn,
            $"Windows dark: {Themes.SystemIsDark}, drawn {Themes.Current.Base}");
        {
            // following the system with a theme of your own for its side
            var own = new Theme { Name = "Side Test", Base = Themes.SystemIsDark ? "dark" : "light" };
            own.Colours["Accent"] = "#22AA55";
            var ownId = Themes.Save(own);
            var (wasLight, wasDark) = (App.Settings.ThemeLight, App.Settings.ThemeDark);
            if (Themes.SystemIsDark) App.Settings.ThemeDark = ownId; else App.Settings.ThemeLight = ownId;
            Themes.Apply(Themes.Find("system"));
            Check("following the system can use a theme of your own for its light or dark side",
                Themes.Current.Id == "system" && Themes.Current.Get("Accent") == Color.FromRgb(0x22, 0xAA, 0x55) && ((System.Windows.Media.SolidColorBrush)Application.Current.Resources["Accent"]).Color == Color.FromRgb(0x22, 0xAA, 0x55),
                Themes.Current.Get("Accent").ToString());
            (App.Settings.ThemeLight, App.Settings.ThemeDark) = (wasLight, wasDark);
            Themes.Delete(ownId);
        }
        Themes.Apply(Themes.Light);

        {
            var sv = App.Win.Page<SettingsView>("settings");
            var holder = (FrameworkElement)((FrameworkElement)sv.FindName("CustomTabRows")).Parent;
            Check("tabs of your own are set up under servers → streaming, not display", holder.Name == "PanelWeb", holder.Name);
        }

        // lyrics written by the speech model, on this computer (the self-test brings the tiny model in PHOENIX_MODELS)
        {
            var sv = App.Win.Page<SettingsView>("settings");
            sv.ShowCategory("software", "Translation");
            await Task.Delay(300);
            var gpus = ((ComboBox)sv.FindName("SpeechGpuBox")).Items.OfType<ComboBoxItem>().Select(i => (string)i.Content).ToList();
            Check("settings lists where the speech model can run: automatic, each graphics card, the processor",
                gpus.Count >= 2 && gpus[0] == "automatic" && gpus[^1] == "the processor only", string.Join(" | ", gpus));
            var tiny = SpeechLyrics.Find("tiny")!;
            if (!SpeechLyrics.Have(tiny)) Check("the speech model writes lyrics (no tiny model to test with)", false, SpeechLyrics.ModelsDir);
            else
            {
                var wav = Path.Combine(Path.GetTempPath(), $"phoenix-speech-{Guid.NewGuid():N}.wav");
                var t = new Thread(() =>
                {
                    dynamic voice = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice")!)!;
                    dynamic file = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpFileStream")!)!;
                    file.Open(wav, 3, false);
                    voice.AudioOutputStream = file;
                    voice.Speak("The quick brown fox jumps over the lazy dog. Hello from the lyrics test.");
                    file.Close();
                });
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join();
                var heard = new List<string>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string? lrc = null; string? error = null;
                try { lrc = await SpeechLyrics.TranscribeAsync(wav, tiny, (_, text) => { lock (heard) heard.Add(text); }, CancellationToken.None); }
                catch (Exception ex) { error = ex.Message; }
                var parsed = lrc == null ? null : Lyrics.Parse(lrc, "x");
                Check("the speech model writes timed lyrics, line by line as it hears them",
                    lrc != null && lrc.StartsWith(SpeechLyrics.Mark) && lrc.Contains("brown fox", StringComparison.OrdinalIgnoreCase) && heard.Count >= 1 && parsed is { Synced: true },
                    error ?? $"{sw.ElapsedMilliseconds} ms on {SpeechLyrics.RanOn}: {lrc?.Replace('\n', ' ')}");
                var song = App.Lib.Tracks.First(x => x.IsLocal);
                var had = Lyrics.HasStored(song);
                if (!had && lrc != null)
                {
                    // kept with the song's lyrics by the last version: moved to the generated ones
                    Lyrics.Save(song, lrc);
                    var own = Lyrics.For(song);
                    var gen = Lyrics.Generated(song);
                    Check("lyrics the speech model wrote are kept apart from the song's lyrics, and say so",
                        own?.Source.Contains("speech model") != true && !Lyrics.HasStored(song) && gen?.Source.Contains("speech model (tiny)") == true, gen?.Source);

                    // the generated tab: there only while a model is chosen, with its own buttons
                    var win = App.Win;
                    var wasModel = App.Settings.LyricsModel;
                    App.Settings.LyricsModel = "";
                    win.ShowNow(true);
                    win.RefreshLyrics();
                    var tab = (FrameworkElement)win.FindName("GeneratedTab");
                    var hiddenWithout = !tab.IsVisible;
                    App.Settings.LyricsModel = "tiny";
                    App.Player.Play(new[] { Player.From(song) }, 0);
                    await Task.Delay(500);
                    win.ShowQueueTab(true, generated: true);
                    await Task.Delay(300);
                    Visibility Vis(string name) => ((FrameworkElement)win.FindName(name)).Visibility;
                    Check("the generated tab shows only while a speech model is chosen, beside lyrics",
                        hiddenWithout && tab.IsVisible && App.Settings.ShowGenerated, $"{hiddenWithout} {tab.IsVisible}");
                    Check("the generated tab shows what the model wrote, with listen and remove (not find, import, paste)",
                        win.ShownLyrics?.Source.Contains("speech model") == true && Vis("LyricsListenBtn") == Visibility.Visible && Vis("LyricsFindBtn") == Visibility.Collapsed
                        && Vis("LyricsRemoveBtn") == Visibility.Visible, win.ShownLyrics?.Source);
                    await Shot("speech-generated-tab");
                    win.ShowQueueTab(true);
                    await Task.Delay(200);
                    Check("and the lyrics tab keeps the song's own lyrics", win.ShownLyrics?.Source.Contains("speech model") != true && Vis("LyricsFindBtn") == Visibility.Visible);
                    win.ShowQueueTab(false);
                    App.Player.Stop();
                    win.ShowNow(false);
                    App.Settings.LyricsModel = wasModel;
                    Lyrics.RemoveGenerated(song);
                }
                // the recommended model suits the graphics: large v3 on a separate card with 6 GB or more
                var cards = SpeechLyrics.Gpus();
                var rec = SpeechLyrics.Recommended();
                var big = cards.FirstOrDefault(g => g.Kind == "separate");
                Check("the model recommended suits the graphics card and its memory",
                    big == null ? rec.Id is "turbo" or "small" : rec.Id == (big.MemoryMb >= 5800 ? "large" : big.MemoryMb >= 3000 ? "turbo-full" : "turbo"),
                    $"{rec.Name}; " + string.Join(", ", cards.Select(g => $"{g.Name} {g.MemoryMb} MB")));
                Check("every model is under 6 GB", SpeechLyrics.Models.All(m => m.Megabytes < 6 * 1024) && SpeechLyrics.Models.Length == 6);
                try { File.Delete(wav); } catch { }
            }
        }

        // a panel behind now playing's queue and lyrics, over the visualizer
        {
            var win = App.Win;
            var back = (System.Windows.Controls.Border)win.FindName("QueueBack");
            var offFirst = !App.Settings.NowPanel && back.Background == null;
            App.Settings.NowPanel = true;
            win.ApplyNowPanel();
            var brush = back.Background as System.Windows.Media.SolidColorBrush;
            Check("settings can put a panel behind the song list and lyrics over the visualizer (off at first)",
                offFirst && brush != null && brush.Color.A is > 0xB0 and < 0xFF, brush?.Color.ToString());
            App.Settings.NowPanel = false;
            win.ApplyNowPanel();
        }

        // nothing playing: the player bar's middle is empty
        {
            var win = App.Win;
            App.Player.Stop();
            await Task.Delay(200);
            var hiddenIdle = !win.BarNowShown;
            App.Player.Play(new[] { Player.From(App.Lib.Tracks.First(t => t.IsLocal)) }, 0);
            await Task.Delay(400);
            var shownPlaying = win.BarNowShown;
            App.Player.Stop();
            Check("the player bar's song, cover and progress hide while nothing is playing", hiddenIdle && shownPlaying, $"{hiddenIdle} {shownPlaying}");
        }

        // a background picture of your own: copied into the data folder, shown through the theme, removed again
        {
            var win = App.Win;
            var sv = win.Page<SettingsView>("settings");
            var picture = Directory.GetFiles(Path.Combine(_dir, "pictures"), "*.png", SearchOption.AllDirectories).First();
            sv.UseBackground(picture);
            var kept = App.Settings.BackgroundPicture;
            var img = (System.Windows.Controls.Image)win.FindName("BackPicture");
            Check("a background picture of your own shows behind the window, from Phoenix's own copy",
                img.IsVisible && img.Source != null && Math.Abs(img.Opacity - 0.30) < 0.01 && kept.StartsWith(Backgrounds.Folder) && File.Exists(kept), kept);
            win.Navigate("music");
            await Task.Delay(300);
            await Shot("background-picture");
            sv.RemoveBackground();
            Check("and goes again, its copy with it", !img.IsVisible && App.Settings.BackgroundPicture == "" && !File.Exists(kept));
        }

        // running on in the notification area: off by default; on, closing the window only hides it
        {
            var win = App.Win;
            var offAtFirst = !App.Settings.RunInTray && !Tray.Shown;
            App.Settings.RunInTray = true;
            Tray.Sync();
            var shown = Tray.Shown;
            win.Close();
            await Task.Delay(300);
            var hidden = !win.IsVisible && Application.Current.MainWindow == win;
            Tray.Restore();
            await Task.Delay(300);
            Check("keeping Phoenix in the notification area: off at first; on, closing the window hides it and the icon brings it back",
                offAtFirst && shown && hidden && win.IsVisible, $"off at first {offAtFirst}, icon {shown}, hidden {hidden}, back {win.IsVisible}");
            App.Settings.RunInTray = false;
            Tray.Sync();
            Check("switching it off takes the icon away", !Tray.Shown);
        }
        try { File.Delete(export); File.Delete(notTheme); } catch { }
        S.Theme = wasTheme;
        App.ApplyTheme(wasTheme);
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
