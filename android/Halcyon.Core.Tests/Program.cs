using System.Text;
using Halcyon.Core;
using Halcyon.Services;

// Self-test of the logic the Android app shares with the Windows app. Exit code 0 = all checks passed.

if (args.Length == 2 && args[0] == "makemedia") { MediaMaker.Make(args[1]); return 0; }

var failures = 0;
var checks = 0;
void Check(bool ok, string what)
{
    checks++;
    if (ok) return;
    failures++;
    Console.WriteLine("FAIL  " + what);
}
void Section(string name) => Console.WriteLine("-- " + name);

var root = Path.Combine(Path.GetTempPath(), "halcyon-core-test-" + Guid.NewGuid().ToString("N")[..8]);
var music = Path.Combine(root, "media", "Music");
var videoDir = Path.Combine(root, "media", "Movies");
Directory.CreateDirectory(Path.Combine(music, "Artist A", "First Album"));
Directory.CreateDirectory(Path.Combine(videoDir, "Some Show", "Season 1"));

static void WriteWav(string path, double seconds)
{
    const int rate = 8000;
    var n = (int)(rate * seconds);
    using var w = new BinaryWriter(File.Create(path));
    w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
    w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
    w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
    for (var i = 0; i < n; i++) w.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 8000));
}

WriteWav(Path.Combine(music, "Artist A", "First Album", "01 One.wav"), 2);
WriteWav(Path.Combine(music, "Artist A", "First Album", "02 Two.wav"), 3);
WriteWav(Path.Combine(music, "loose.wav"), 1);
File.WriteAllBytes(Path.Combine(videoDir, "Some Show", "Season 1", "S01E02.mkv"), new byte[64]);
File.WriteAllBytes(Path.Combine(videoDir, "Other.Show.S02E05.The.Title.mkv"), new byte[64]);
File.WriteAllBytes(Path.Combine(videoDir, "holiday clip.mp4"), new byte[64]);

var engine = new FakeEngine();
App.Secrets = new XorSecrets();
App.Init(Path.Combine(root, "data"), engine, () =>
{
    var s = new Settings();
    s.MusicFolders.Add(music);
    s.VideoFolders.Add(videoDir);
    return s;
});

// ------------------------------------------------------------------
Section("settings: secrets are stored encrypted");
App.Settings.DiscogsToken = "discogs-secret";
App.Settings.JellyfinToken = "jf-secret";
App.Settings.Save();
var raw = File.ReadAllText(AppPaths.SettingsPath);
Check(!raw.Contains("discogs-secret") && !raw.Contains("jf-secret"), "tokens must not appear in settings.json in clear text");
var reloaded = Settings.Load();
Check(reloaded.DiscogsToken == "discogs-secret" && reloaded.JellyfinToken == "jf-secret", "tokens decrypt on load");
Check(reloaded.MusicFolders.SequenceEqual(App.Settings.MusicFolders), "folders round-trip");
App.Settings.JellyfinToken = "";

// ------------------------------------------------------------------
Section("scan: local files");
var changed = 0;
App.Lib.Changed += () => changed++;
await App.Lib.ScanAsync(_ => { });
Check(App.Lib.Tracks.Count == 3, $"3 songs scanned (got {App.Lib.Tracks.Count})");
Check(changed > 0, "Changed raised by scan");
var one = App.Lib.Tracks.First(t => t.SourceId.EndsWith("01 One.wav"));
Check(one.DurationMs is > 1900 and < 2100, $"wav duration read ({one.DurationMs} ms)");
Check(App.Lib.Videos.Count == 3, $"3 videos scanned (got {App.Lib.Videos.Count})");
var ep = App.Lib.Videos.First(v => v.SourceId.EndsWith("S01E02.mkv"));
Check(ep is { Kind: "episode", Series: "Some Show", Season: 1, Episode: 2 }, $"folder-named episode classified ({ep.Kind}/{ep.Series}/{ep.Season}/{ep.Episode})");
var ep2 = App.Lib.Videos.First(v => v.SourceId.Contains("Other.Show"));
Check(ep2 is { Kind: "episode", Series: "Other Show", Season: 2, Episode: 5, Title: "The Title" }, "dotted episode name classified");
Check(App.Lib.Videos.First(v => v.SourceId.EndsWith(".mp4")) is { Kind: "video", Series: "Movies" }, "plain video grouped by folder");

// Ratings and edits survive a rescan; a deleted file disappears.
App.Lib.Rate(new[] { one }, 1);
one.Title = "Renamed"; one.Album = "My Album"; one.AlbumArtist = one.Artist = "Me";
App.Lib.SaveTracks(new[] { one });
File.Delete(Path.Combine(music, "loose.wav"));
await App.Lib.ScanAsync(_ => { });
App.Lib.Load();
one = App.Lib.Tracks.First(t => t.SourceId.EndsWith("01 One.wav"));
Check(App.Lib.Tracks.Count == 2, "deleted file dropped on rescan");
Check(one is { Title: "Renamed", Rating: 1, Album: "My Album" }, "edit + rating persist across rescan and reload");
Check(App.Lib.Albums.Any(a => a.Title == "My Album" && a.Artist == "Me"), "edited song regrouped into its new album");

// ------------------------------------------------------------------
Section("liked row ordering");
var two = App.Lib.Tracks.First(t => t.SourceId.EndsWith("02 Two.wav"));
App.Lib.Rate(new[] { two }, 1);
Check(App.Lib.Liked().Select(t => t.Id).SequenceEqual(new[] { two.Id, one.Id }), "most recently liked first");
App.Lib.Rate(new[] { one }, 1);
Check(App.Lib.Liked()[0].Id == one.Id, "re-liking moves a song to the front");
App.Lib.Rate(new[] { two }, -1);
Check(App.Lib.Liked().Count == 1 && App.Lib.AutoPlaylist("liked").Count == 1, "disliked song leaves liked");
Check(Catalog.Liked().Count == 1, "quickplay liked row");

// ------------------------------------------------------------------
Section("server sync: grouping and edits persist");
List<Track> ServerTracks() => new()
{
    new Track { Source = "jellyfin", SourceId = "t1", Location = "t1|direct", GroupId = "albumX", Title = "S One", Artist = "Srv", AlbumArtist = "Srv", Album = "Srv Album", TrackNo = 1, Genre = "Rock" },
    new Track { Source = "jellyfin", SourceId = "t2", Location = "t2|transcode", GroupId = "albumX", Title = "S Two", Artist = "Guest", AlbumArtist = "Srv", Album = "Srv Album (disc 2 title)", TrackNo = 2, Genre = "Rock", DurationMs = 200000 },
};
List<VideoItem> ServerVideos() => new()
{
    new VideoItem { Source = "jellyfin", SourceId = "m1", Location = "m1", Title = "Movie One", Kind = "movie", Year = 1999, Collection = "Box" },
    new VideoItem { Source = "jellyfin", SourceId = "m2", Location = "m2", Title = "Movie Two", Kind = "movie", Year = 2001 },
    new VideoItem { Source = "jellyfin", SourceId = "e1", Location = "e1", Title = "Pilot", Kind = "episode", Series = "Srv Show", Season = 1, Episode = 1, GroupArt = "http://x/poster" },
    new VideoItem { Source = "jellyfin", SourceId = "e2", Location = "e2", Title = "Special", Kind = "episode", Series = "Srv Show", Season = 0, Episode = 1 },
    new VideoItem { Source = "jellyfin", SourceId = "e3", Location = "e3", Title = "Second", Kind = "episode", Series = "srv show", Season = 2, Episode = 1 },
};
await App.Lib.ReplaceSourceAsync("jellyfin", ServerTracks(), ServerVideos());
Check(App.Lib.Albums.Count(a => a.Source == "jellyfin") == 1, "server tracks group by the server's album id even when album names differ");
var st2 = App.Lib.Tracks.First(t => t.SourceId == "t2");
App.Lib.Rate(new[] { st2 }, 1);
st2.Album = "Regrouped"; st2.AlbumArtist = "Guest";
App.Lib.SaveTracks(new[] { st2 });
Check(st2.Edited, "editing a server track marks it edited");
var m2 = App.Lib.Videos.First(v => v.SourceId == "m2");
m2.Collection = "Box"; m2.Title = "Movie II";
App.Lib.SaveVideos(new[] { m2 });
await App.Lib.ReplaceSourceAsync("jellyfin", ServerTracks(), ServerVideos().Where(v => v.SourceId != "e2").ToList());
st2 = App.Lib.Tracks.First(t => t.SourceId == "t2");
Check(st2 is { Album: "Regrouped", AlbumArtist: "Guest", Rating: 1, Edited: true }, "track edit + rating survive a sync");
Check(App.Lib.Albums.Count(a => a.Source == "jellyfin") == 2, "regrouped track stays in its own album after sync");
m2 = App.Lib.Videos.First(v => v.SourceId == "m2");
Check(m2 is { Collection: "Box", Title: "Movie II", Edited: true }, "video edit survives a sync");
Check(App.Lib.Videos.All(v => v.SourceId != "e2"), "item removed on the server is removed here");

Section("catalog: videos");
Check(Catalog.VideoTabs().SequenceEqual(new[] { "movies", "shows", "collections", "folders", "all" }), "video tabs: " + string.Join(",", Catalog.VideoTabs()));
var shows = Catalog.VideoGroups("shows");
Check(shows.Any(g => g.Name.Equals("Srv Show", StringComparison.OrdinalIgnoreCase) && g.Count == 2 && g.Art == "http://x/poster"), "series merge case-insensitively and carry the poster");
Check(Catalog.Seasons("Srv Show").SequenceEqual(new[] { 1, 2 }), "seasons listed");
Check(Catalog.GroupMembers("collections", "Box").Count == 2, "collection holds server + user-added movie");
Catalog.RenameGroup("shows", "Other Show", "Some Show");
Check(Catalog.GroupMembers("shows", "Some Show").Count == 2, "renaming a series onto an existing name merges them");
Check(Catalog.Movies().Count == 2 && Catalog.AllVideos().Count == App.Lib.Videos.Count, "movies / all");
var (sa, ss, sv) = Catalog.Search("srv");
Check(sa.Count >= 1 && ss.Count == 1 && sv.Count == 2, $"search finds albums/songs/videos ({sa.Count}/{ss.Count}/{sv.Count})");
Check(Catalog.Artists().Contains("Me") && Catalog.Genres().Contains("Rock") && Catalog.AlbumsInGenre("rock").Count >= 1, "artists / genres");

// ------------------------------------------------------------------
Section("playlists, pins, smart mix");
var pl = App.Lib.CreatePlaylist("road trip");
App.Lib.AddToPlaylist(pl, new[] { one, st2 });
App.Lib.TogglePin("playlist", pl.Id.ToString(), pl.Name);
App.Lib.TogglePin("artist", "Srv", "Srv");
App.Lib.Load();
Check(App.Lib.TracksOf(App.Lib.Playlists.Single()).Count == 2, "playlist tracks persist");
Check(Catalog.Pins().Count == 2, "pins resolve to quickplay tiles");
Check(Catalog.NewAlbums().Count > 0, "new row");
var mix = App.Lib.SmartMix("Srv");
Check(mix.Count == 2 && mix.All(t => t.Rating >= 0), "smart mix: artist's songs plus genre relatives, no disliked");
App.Lib.DeletePlaylist(App.Lib.Playlists.Single());
Check(Catalog.Pins().Count == 1, "deleting a playlist removes its pin");

// ------------------------------------------------------------------
Section("player: queue, repeat, shuffle");
var p = App.Player;
string? error = null;
p.Error += m => error = m;
var locals = App.Lib.Tracks.Where(t => t.IsLocal).OrderBy(t => t.SourceId).ToList();
p.PlayTracks(locals);
Check(p.IsPlaying && p.CurrentIndex == 0 && engine.Url == locals[0].Location, "plays first item");
engine.FireOpened();
var before = locals[0].PlayCount;
engine.FireEnded();
Check(locals[0].PlayCount == before + 1 && p.CurrentIndex == 1, "ended: play count saved, advances");
engine.FireEnded();
Check(!p.IsPlaying && p.Current == null, "end of queue stops");
Check(Catalog.History().Count > 0, "history row filled from plays");
p.Repeat = 1;
p.PlayTracks(locals, locals[1]);
engine.FireEnded();
Check(p.CurrentIndex == 0 && p.IsPlaying, "repeat all wraps");
p.Repeat = 2;
engine.FireEnded();
Check(p.CurrentIndex == 0 && engine.Seeks.Last() == 0, "repeat one restarts the same item");
p.Repeat = 0;
engine.Position = 5000;
p.Previous();
Check(p.CurrentIndex == 0 && engine.Seeks.Last() == 0, "previous after 3s restarts");
p.Toggle();
Check(!p.IsPlaying && engine.Paused, "toggle pauses");
p.Shuffle = true;
var many = Enumerable.Range(0, 30).Select(i => new PlayItem { Title = "item " + i, Url = "u" + i }).ToList();
p.Play(many, 7);
var seen = new List<int> { p.CurrentIndex };
for (var i = 0; i < 29; i++) { p.Next(); seen.Add(p.CurrentIndex); }
Check(seen[0] == 7 && seen.Distinct().Count() == 30, "shuffle starts on the chosen item and visits every item once");
Check(!seen.SequenceEqual(Enumerable.Range(7, 30).Select(i => i % 30)), "shuffle order is not sequential");
p.Shuffle = false;
p.Play(many.Take(3));
p.Enqueue(new[] { new PlayItem { Title = "late", Url = "late" } });
p.Next(); p.Next(); p.Next();
Check(engine.Url == "late", "enqueued item plays after the queue");
p.RemoveAt(0);
Check(p.Queue.Count == 3 && p.CurrentIndex == 2, "removing an earlier item keeps the current one");

Section("player: server streams");
// Transcoded audio: seeking reloads the stream at an offset and reports position from there.
var transcoded = Player.From(App.Lib.Tracks.First(t => t.SourceId == "t2"));
App.Settings.JellyfinUrl = "http://server:8096";
App.Settings.JellyfinToken = "tok";
p.Play(new[] { transcoded });
await Task.Yield();
var firstUrl = engine.Url;
Check(firstUrl.Contains("/Audio/t2/stream.mp3") && firstUrl.Contains("PlaySessionId="), "transcode URL has a play session");
engine.FireOpened();
p.Seek(60000);
Check(engine.Url.Contains("StartTimeTicks=600000000"), "seek asks the server for a stream starting at the target");
Check(Session(engine.Url) != Session(firstUrl), "each seek uses a fresh PlaySessionId");
engine.Position = 1500;
Check(p.PositionMs == 61500 && p.DurationMs == 200000, "position = offset + stream position; duration from the library");
var direct = Player.From(App.Lib.Tracks.First(t => t.SourceId == "t1"));
Check(direct.SeekUrl == null && direct.Url.Contains("static=true"), "direct audio plays the original file");

// Video: resolved when it starts; falls back to HLS once if the original can't be played.
var resolved = 0;
var vid = new PlayItem
{
    Title = "vid", IsVideo = true,
    Resolve = async () => { resolved++; await Task.Delay(20); return "direct-url"; },
    Fallback = () => "hls-url"
};
p.Play(new[] { vid, new PlayItem { Title = "after", Url = "after" } });
await Task.Delay(120);
Check(resolved == 1 && engine.Url == "direct-url", "video URL resolved at play time");
engine.Position = 42000;
error = null;
engine.FireFailed("decoder");
Check(engine.Url == "hls-url" && error == null && p.CurrentIndex == 0, "first failure switches to the fallback stream silently");
engine.Position = 0;
engine.FireOpened();
Check(engine.Seeks.Last() == 42000, "fallback resumes where the original stopped");
engine.FireFailed("still broken");
Check(error != null && p.CurrentIndex == 1, "second failure reports and moves on");
// A quick skip must not let a slow lookup start the wrong item.
p.Play(new[] { vid, new PlayItem { Title = "after", Url = "after" } });
p.Next();
await Task.Delay(120);
Check(engine.Url == "after", "late URL lookup for a skipped item is dropped");

Check(JellyfinClient.PlanVideo("mkv", "hevc", "aac") == "direct", "plan: mkv/hevc/aac plays directly");
Check(JellyfinClient.PlanVideo("mkv", "hevc", "eac3") == "hevc", "plan: unsupported audio -> HLS keeping HEVC picture");
Check(JellyfinClient.PlanVideo("mkv", "h264", "dts") == "h264", "plan: unsupported audio -> HLS keeping H.264 picture");
Check(JellyfinClient.PlanVideo("avi", "mpeg2video", "mp3") == "h264", "plan: unsupported picture -> convert to H.264");
Check(JellyfinClient.PlanVideo("mov,mp4,m4a,3gp,3g2,mj2", "h264", "aac") == "direct", "plan: multi-name container");
var hls = App.Jellyfin.HlsUrl("abc", "hevc");
Check(hls.Contains("/Videos/abc/master.m3u8") && hls.Contains("SegmentContainer=mp4") && hls.Contains("api_key=tok"), "HLS URL shape");
Check(Session(App.Jellyfin.HlsUrl("abc", "h264")) != Session(hls), "HLS URLs get fresh sessions");
App.Settings.JellyfinToken = "";

// ------------------------------------------------------------------
Section("podcasts");
var rss = """
    <?xml version="1.0"?>
    <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd"><channel>
      <title> Test Cast </title><itunes:author>Someone</itunes:author><description>&lt;p&gt;About &amp;amp; things&lt;/p&gt;</description>
      <itunes:image href="http://img/cover.jpg"/>
      <item><title>Ep 2</title><guid>g2</guid><pubDate>Tue, 02 Jan 2024 10:00:00 GMT</pubDate><itunes:duration>1:02:03</itunes:duration>
        <enclosure url="http://media/2.mp3" type="audio/mpeg"/></item>
      <item><title>Ep 1</title><guid>g1</guid><pubDate>Mon, 01 Jan 2024 10:00:00 GMT</pubDate><itunes:duration>90</itunes:duration>
        <enclosure url="http://media/1.mp4" type="video/mp4"/></item>
      <item><title>No media</title></item>
    </channel></rss>
    """.Trim();
var (pod, eps) = PodcastService.Parse(rss, "http://feed");
Check(pod is { Title: "Test Cast", Author: "Someone", ArtUrl: "http://img/cover.jpg" } && pod.Description == "About & things", "feed header parsed");
Check(eps.Count == 2 && eps[0].DurationMs == 3723000 && eps[1].DurationMs == 90000 && eps[1].IsVideo, "episodes parsed");
App.Lib.Db.SavePodcast(pod, eps);
var stored = App.Lib.Db.LoadEpisodes(pod.Id);
Check(stored.Count == 2 && stored[0].Title == "Ep 2", "episodes stored newest first");
p.Play(new[] { Player.From(stored[0], pod) });
engine.FireOpened();
engine.Position = 30000;
p.Stop();
Check(App.Lib.Db.LoadEpisodes(pod.Id)[0].PositionMs == 30000, "episode position saved on stop");
stored = App.Lib.Db.LoadEpisodes(pod.Id);
p.Play(new[] { Player.From(stored[0], pod) });
engine.Position = 0;
engine.FireOpened();
Check(engine.Seeks.Last() == 30000, "episode resumes where it was left");
engine.FireEnded();
Check(App.Lib.Db.LoadEpisodes(pod.Id)[0] is { Played: true, PositionMs: 0 }, "finished episode marked played");

// ------------------------------------------------------------------
Section("discogs");
var rel = DiscogsClient.ParseRelease(J.Parse("""
    {"id":5,"title":"Real Album","year":1987,"artists":[{"name":"Band (2)"}],"genres":["Rock"],"styles":["Indie"],
     "images":[{"type":"secondary","uri":"http://i/2"},{"type":"primary","uri":"http://i/1"}],
     "tracklist":[{"type_":"heading","title":"Side A"},{"type_":"track","position":"A1","title":"Alpha"},{"type_":"track","position":"A2","title":"Beta"}]}
    """));
Check(rel is { Title: "Real Album", Artist: "Band", Year: 1987, Genre: "Rock", CoverUrl: "http://i/1" } && rel.Tracks.Count == 2, "release parsed");
var album = App.Lib.Albums.First(a => a.Source == "jellyfin" && a.Tracks.Count == 1 && a.Title == "Srv Album");
var twoTrack = new Album { Key = album.Key, Source = "jellyfin", Artist = "Srv", Tracks = App.Lib.Tracks.Where(t => t.Source == "jellyfin").OrderBy(t => t.TrackNo).ToList() };
await App.Lib.ApplyReleaseAsync(twoTrack, rel, albumInfo: true, titles: true, cover: false);
App.Lib.Load();
var applied = App.Lib.Tracks.Where(t => t.Source == "jellyfin").OrderBy(t => t.TrackNo).ToList();
Check(applied.Select(t => t.Title).SequenceEqual(new[] { "Alpha", "Beta" }) && applied.All(t => t is { Album: "Real Album", AlbumArtist: "Band", Year: 1987, Edited: true }), "release applied to album and kept as an edit");

// ------------------------------------------------------------------
Section("pc sync: selection");
var pcLib = SyncClient.ParseLibrary("""
    {"name":"PC","tracks":[
      {"id":1,"title":"A1","artist":"Ann","albumArtist":"Ann","album":"First","size":100,"modified":5,"path":"Ann/First/a1.mp3","hasArt":true,"rating":1,"ratedAt":50,"playCount":2,"lastPlayed":9},
      {"id":2,"title":"A2","artist":"Ann","albumArtist":"Ann","album":"Second","size":200,"modified":5,"path":"Ann/Second/a2.mp3","hasArt":false,"rating":0,"ratedAt":0,"playCount":0,"lastPlayed":0},
      {"id":3,"title":"B1","artist":"Bob","albumArtist":"Bob","album":"Only","size":300,"modified":5,"path":"../../etc/b1.mp3","hasArt":false,"rating":0,"ratedAt":0,"playCount":0,"lastPlayed":0}],
     "playlists":[{"name":"Mix","trackIds":[2,3]}]}
    """);
Check(pcLib is { Name: "PC" } && pcLib.Tracks.Count == 3 && pcLib.Tracks[0] is { HasArt: true, Rating: 1, RatedAt: 50, Size: 100 } && pcLib.Playlists[0].TrackIds.Count == 2, "PC collection parsed");
var sync = App.Sync;
Check(sync.Wanted(pcLib).Count == 0, "nothing chosen, nothing wanted");
SyncClient.Toggle(sync.State.Albums, SyncClient.AlbumKey("Ann", "First"));
Check(sync.Wanted(pcLib).Select(t => t.Id).SequenceEqual(new long[] { 1 }), "an album selects its songs");
SyncClient.Toggle(sync.State.Playlists, "mix");
Check(sync.Wanted(pcLib).Count == 3, "a playlist adds its songs");
SyncClient.Toggle(sync.State.Artists, "ANN");
SyncClient.Toggle(sync.State.Playlists, "Mix");
Check(sync.Wanted(pcLib).Select(t => t.Id).SequenceEqual(new long[] { 1, 2 }), "an artist selects every album; unticking a playlist drops its extras");
var withPhoneList = pcLib with { Playlists = pcLib.Playlists.Append(new PcPlaylist("phone SYNC", new List<long> { 3 })).ToList() };
Check(sync.IsChosen("Phone sync") && !sync.IsChosen("Mix") && !sync.State.Playlists.Contains("Phone sync", StringComparer.OrdinalIgnoreCase), "the PC's Phone sync playlist counts as chosen without being ticked");
Check(sync.Wanted(withPhoneList).Select(t => t.Id).SequenceEqual(new long[] { 1, 2, 3 }), "songs in the PC's Phone sync playlist are always wanted");
Check(sync.Wanted(pcLib).Count == 2, "without that playlist on the PC nothing extra is wanted");

// ------------------------------------------------------------------
Section("visualizer analysis");
var spec = new Spectrum();
var wave = new float[Spectrum.FftSize];
for (var i = 0; i < wave.Length; i++) wave[i] = (float)(0.8 * Math.Sin(2 * Math.PI * 1000 * i / 44100.0));
spec.Feed(wave, 44100);
spec.Update();
var top = Array.IndexOf(spec.Bars, spec.Bars.Max());
var expected = (int)(Math.Log(1000 / 40.0) / Math.Log(16000 / 40.0) * Spectrum.BarCount);
Check(Math.Abs(top - expected) <= 1 && spec.Bars[top] > 0.8, $"1 kHz tone lights bar {expected} (got {top}, level {spec.Bars[top]:0.00})");
Check(spec.Bars[2] < 0.35 && spec.Bars[Spectrum.BarCount - 2] < 0.35, "other bands stay low");
spec.FeedU8(Enumerable.Repeat((byte)128, Spectrum.FftSize).ToArray(), 44100);
for (var i = 0; i < 40; i++) spec.Update();
Check(spec.Bars.Max() < 0.01 && spec.Scope.All(s => s == 0), "silence lets the bars fall to zero");

// ------------------------------------------------------------------
Section("visualizer: beats, sensitivity, animation state");
{
    // loud bass bursts every 400 ms of fake time, silence between: each burst is one beat, never two within 170 ms
    var vs = new Spectrum();
    long clock = 0;
    vs.NowMs = () => clock;
    var bass = new float[Spectrum.FftSize];
    for (var i = 0; i < bass.Length; i++) bass[i] = (float)(0.9 * Math.Sin(2 * Math.PI * 80 * i / 44100.0));
    var quiet = new float[Spectrum.FftSize];
    var beatTimes = new List<long>();
    void Step() { var before = vs.Beats; vs.Update(); if (vs.Beats > before) beatTimes.Add(clock); }
    for (var round = 0; round < 6; round++)
    {
        vs.Feed(bass, 44100);
        clock += 400;
        Step();
        Step();   // a second step on the same loud frame must not count as another beat
        vs.Feed(quiet, 44100);
        for (var k = 0; k < 14; k++) { clock += 30; Step(); }
    }
    var spaced = beatTimes.Zip(beatTimes.Skip(1), (x, y) => y - x).All(d => d > 170);
    Check(vs.Beats >= 6 && vs.Beats <= 14 && spaced, $"every bass burst is a beat, never two within 170 ms ({vs.Beats} beats at {string.Join(", ", beatTimes)})");
    Check(vs.Ripples.Count > 0 && vs.Ripples.Count <= 8 && vs.Sparks.Count > 0, $"beats spawn ripples ({vs.Ripples.Count}) and sparks ({vs.Sparks.Count})");
    Check(vs.History.Count == Spectrum.HistoryRows && vs.History.All(r => r.Length == Spectrum.BarCount), "history keeps the recent bar rows for the waterfall");
    Check(vs.Needle[0] >= 0 && vs.Needle[0] <= 1 && vs.Needle[1] >= 0 && vs.Needle[1] <= 1, "meter needles stay within 0..1");
    var phaseBefore = vs.Phase;
    vs.Update();
    Check(vs.Phase > phaseBefore, "the animation clock advances");

    // sensitivity: the same quiet tone reads higher on a higher setting
    double LevelAt(int sensitivity)
    {
        var sp = new Spectrum { Sensitivity = sensitivity };
        var tone = new float[Spectrum.FftSize];
        for (var i = 0; i < tone.Length; i++) tone[i] = (float)(0.004 * Math.Sin(2 * Math.PI * 1000 * i / 44100.0));
        sp.Feed(tone, 44100);
        sp.Update();
        return sp.Bars.Max();
    }
    var levels = new[] { LevelAt(0), LevelAt(1), LevelAt(2), LevelAt(3) };
    Check(levels[0] < levels[1] && levels[1] < levels[2] && levels[2] < levels[3] && Math.Abs((levels[1] - levels[0]) - 7 / 60.0) < 0.001, $"sensitivity raises the level by 7 dB per step ({string.Join(", ", levels.Select(l => l.ToString("0.00")))})");
    Check(VisualizerOptions.Styles.Length == 15 && VisualizerOptions.Palettes.Length == 11 && VisualizerOptions.Sensitivities.Length == 4, "style, colour and sensitivity lists match the Windows app");
    vs.Clear();
    Check(vs.History.Count == 0 && vs.Sparks.Count == 0 && vs.Beat == 0, "clearing resets the animation state");
}

Section("books: audiobooks and ebooks");
App.Player.Stop();
var bookDir = Path.Combine(root, "media", "Books");
MediaMaker.MakeBooks(bookDir, 4);
App.Settings.BookFolders = new() { bookDir };
await App.Lib.ScanAsync(_ => { });
var audioBook = App.Lib.Books.FirstOrDefault(b => b.IsAudio);
var ebook = App.Lib.Books.FirstOrDefault(b => b.Format == "epub");
Check(App.Lib.Books.Count == 5 && App.Lib.Books.Any(b => b.Format == "pdf" && b.Title == "Manual" && b.ArtPath.StartsWith("pdf:")), $"5 books scanned (got {App.Lib.Books.Count})");
Check(audioBook is { Title: "The Long Road", Author: "Jane Writer" } && audioBook.Parts.Select(x => x.Title).SequenceEqual(new[] { "Part 1", "Part 2", "Part 10" })
      && Math.Abs(audioBook.DurationMs - 12000) < 600, "audiobook grouped per folder in natural order: " + (audioBook == null ? "missing" : string.Join(", ", audioBook.Parts.Select(x => x.Title))));
Check(ebook is { Title: "A Quiet Book", Author: "Sam Author" } && File.Exists(ebook.ArtPath), "epub title, author and cover");
var parsed = Epub.Open(Path.Combine(bookDir, "quiet.epub"));
Check(parsed.Chapters.Select(c => c.Title).SequenceEqual(new[] { "Opening", "The Middle", "The End" }) && parsed.Chapters[1].Href == "OEBPS/text/ch2.xhtml",
    "epub chapters and titles: " + string.Join(" | ", parsed.Chapters.Select(c => c.Title + "=" + c.Href)));
var unpacked = Path.Combine(root, "unpacked");
Epub.Extract(Path.Combine(bookDir, "quiet.epub"), unpacked);
Check(File.Exists(Path.Combine(unpacked, "OEBPS", "text", "ch2.xhtml")) && File.Exists(Path.Combine(unpacked, "OEBPS", "images", "front.png")), "epub extracts for the reader");
Check(ReaderScript.Build(ReaderScript.PostAndroid).Contains("HalcyonHost.post") && !ReaderScript.Build(ReaderScript.PostAndroid).Contains("__POST__"), "reader script takes the app's message function");
await App.Lib.ScanAsync(_ => { });
Check(App.Lib.Books.Count == 5 && ReferenceEquals(audioBook, App.Lib.Books.FirstOrDefault(b => b.IsAudio)), "book rescan is idempotent");
Check(App.Lib.Tracks.All(t => !t.SourceId.StartsWith(bookDir)), "book audio is not listed as songs");

var cbzFile = Path.Combine(bookDir, "Hero Tales 3.cbz");
var cbrFile = Path.Combine(bookDir, "Space Gang.cbr");
var cbz = App.Lib.Books.FirstOrDefault(b => b.Format == "comic" && b.SourceId == cbzFile);
var cbr = App.Lib.Books.FirstOrDefault(b => b.Format == "comic" && b.SourceId == cbrFile);
Check(cbz is { Title: "Hero Tales #3", Author: "Pat Pencil", Series: "Hero Tales", Kind: "ebook" } && File.Exists(cbz.ArtPath), "cbz metadata from ComicInfo.xml and cover: " + (cbz == null ? "missing" : $"{cbz.Title} / {cbz.Author} / {cbz.Series}"));
Check(cbr is { Title: "Space Gang" } && File.Exists(cbr.ArtPath), "cbr is picked up with a cover: " + (cbr == null ? "missing" : cbr.Title));
var (zipPageData, rarPageData) = MediaMaker.MakeComics(Path.Combine(root, "x.cbz"), Path.Combine(root, "x.cbr"));
var zipPages = Comic.Pages(cbzFile, Path.Combine(root, "comic-zip"));
var rarPages = Comic.Pages(cbrFile, Path.Combine(root, "comic-rar"));
Check(zipPages.Count == 3 && zipPages.Select(File.ReadAllBytes).Zip(File.ReadAllBytes(Path.Combine(root, "x.cbz")).Length > 0 ? ReadZipPages(cbzFile) : zipPageData).All(t => t.First.SequenceEqual(t.Second)), $"cbz pages in reading order (page2 before page10): {zipPages.Count} pages");
Check(rarPages.Count == 3 && rarPages.Select(File.ReadAllBytes).Zip(ReadRarOrder(cbrFile)).All(t => t.First.SequenceEqual(t.Second)), $"cbr pages in reading order: {rarPages.Count} pages");
Check(Comic.Pages(cbzFile, Path.Combine(root, "comic-zip")).SequenceEqual(zipPages), "extracted comic pages are reused");

if (audioBook != null && ebook != null)
{
    App.Player.Shuffle = true;   // a book must still play in order
    App.Player.PlayBook(audioBook);
    engine.FireOpened();
    Check(App.Player.CurrentIndex == 0 && App.Player.Queue.Count == 3 && engine.Url.EndsWith("Chapter 1.wav"), "audiobook starts at part 1");
    App.Player.Next();
    engine.FireOpened();
    Check(App.Player.CurrentIndex == 1 && engine.Url.EndsWith("Chapter 2.wav"), "next goes to part 2 even with shuffle on");
    engine.Position = 1500;
    App.Player.Stop();
    var saved = new Database(AppPaths.DbPath).LoadBooks().Single(b => b.Id == audioBook.Id);
    Check(saved.Part == 1 && saved.PositionMs == 1500 && saved.Progress is > 0.4 and < 0.5, $"audiobook position saved (part {saved.Part}, {saved.PositionMs} ms, {saved.Progress:P0})");
    engine.Seeks.Clear();
    App.Player.PlayBook(audioBook);
    Check(App.Player.CurrentIndex == 1, "audiobook reopens on the part it stopped in");
    App.Player.SaveEpisodePosition();   // e.g. the app is put away before the file has opened
    Check(audioBook.PositionMs == 1500, "the place is not lost while a part is still opening");
    engine.FireOpened();
    Check(engine.Seeks.LastOrDefault() == 1500, "audiobook resumes where it stopped");
    App.Player.PlayAt(2);
    engine.FireOpened();
    engine.FireEnded();
    Check(audioBook.Finished && audioBook.Progress == 1 && App.Player.Current == null, "audiobook finishes after its last part");
    App.Player.PlayBook(audioBook);
    Check(!audioBook.Finished && App.Player.CurrentIndex == 0 && audioBook.Progress == 0, "a finished book starts over");
    App.Player.Stop();
    App.Player.Shuffle = false;

    ebook.Part = 1; ebook.Fraction = 0.5; ebook.Progress = 0.5;
    App.Lib.Db.SaveBookState(ebook);
    ebook.Title = "A Quieter Book"; ebook.Series = "Quiet";
    App.Lib.SaveBookInfo(ebook);
    File.SetLastWriteTimeUtc(Path.Combine(bookDir, "quiet.epub"), DateTime.UtcNow.AddMinutes(1));
    await App.Lib.ScanAsync(_ => { });
    var again = App.Lib.Books.Single(b => b.Format == "epub");
    Check(again is { Title: "A Quieter Book", Series: "Quiet", Part: 1, Edited: true } && again.Fraction == 0.5 && again.Id == ebook.Id, "edits and reading place survive a rescan of a changed file");
    App.Lib.SetBookFinished(again, true);
    Check(new Database(AppPaths.DbPath).LoadBooks().Single(b => b.Id == again.Id) is { Finished: true, Progress: 1, Part: 0 }, "mark as finished is stored");

    // bookmarks
    var bmEvents = 0;
    App.Lib.BookmarksChanged += () => bmEvents++;
    var bm1 = App.Lib.AddBookmark(audioBook, 1, 4500);
    var bm0 = App.Lib.AddBookmark(audioBook, 0, 2000, "  The start  ");
    var bm2 = App.Lib.AddBookmark(again, 1, 0.5);
    var marks = App.Lib.BookmarksOf(audioBook);
    Check(marks.Select(m => m.Id).SequenceEqual(new[] { bm0.Id, bm1.Id }) && App.Lib.BookmarkCount(audioBook) == 2 && App.Lib.BookmarkCount(again) == 1, $"bookmarks are listed per book in reading order ({string.Join(",", marks.Select(m => m.Id + "@" + m.BookId))} audio {audioBook.Id} ebook {again.Id} counts {App.Lib.BookmarkCount(audioBook)}/{App.Lib.BookmarkCount(again)})");
    Check(bm0.Label == "The start" && bm1.Label.Contains("Part 2") && bm1.Label.Contains("0:04") && bm2.Label.StartsWith("Chapter 2") && bm2.Label.Contains("50%"), $"default labels describe the place: '{bm1.Label}' / '{bm2.Label}'");
    App.Lib.RenameBookmark(bm1, "  Good bit ");
    App.Lib.RenameBookmark(bm1, "   ");
    Check(bm1.Label == "Good bit" && bmEvents == 4, "rename trims and ignores blank names");
    Check(new Database(AppPaths.DbPath).LoadBookmarks().Count == 3 && new Database(AppPaths.DbPath).LoadBookmarks().Single(m => m.Id == bm1.Id).Label == "Good bit", "bookmarks persist");
    App.Lib.SetBookFinished(audioBook, true);
    Check(App.Lib.MoveToBookmark(bm1) && audioBook is { Finished: false, Part: 1, PositionMs: 4500 } && audioBook.Progress is > 0.65 and < 0.75, $"MoveToBookmark sets the audiobook place ({audioBook.Part}, {audioBook.PositionMs} ms, {audioBook.Progress:P0})");
    Check(App.Lib.MoveToBookmark(bm2) && again is { Part: 1, Finished: false } && again.Fraction == 0.5, "MoveToBookmark sets the ebook place");
    App.Player.PlayBook(audioBook);
    engine.FireOpened();
    engine.Position = 3000;
    engine.Seeks.Clear();
    App.Player.PlayBookAt(audioBook, 2, 1234);   // playing the same book: the old place must not overwrite the new one
    engine.FireOpened();
    Check(App.Player.CurrentIndex == 2 && engine.Seeks.LastOrDefault() == 1234 && audioBook.Part == 2, $"PlayBookAt starts at the bookmark (index {App.Player.CurrentIndex}, seek {engine.Seeks.LastOrDefault()})");
    App.Player.Stop();
    App.Lib.DeleteBookmark(bm0);
    Check(App.Lib.BookmarksOf(audioBook).Count == 1 && new Database(AppPaths.DbPath).LoadBookmarks().All(m => m.Id != bm0.Id), "delete removes a bookmark");
    App.Lib.Db.DeleteBook(again.Id);
    Check(new Database(AppPaths.DbPath).LoadBookmarks().All(m => m.BookId != again.Id), "deleting a book deletes its bookmarks");
    await App.Lib.ScanAsync(_ => { });   // the epub is back (its file is still there)

    // a server's books: positions and edits survive a sync, and go when the server is removed
    Book Remote() => new() { Source = "jellyfin", SourceId = "b1", Kind = "ebook", Format = "epub", Title = "Server Book", Author = "Remote Writer" };
    await App.Lib.ReplaceBooksAsync("jellyfin", new() { Remote() });
    var rb = App.Lib.Books.Single(b => b.Source == "jellyfin");
    rb.Part = 2; rb.Progress = 0.7; App.Lib.Db.SaveBookState(rb);
    rb.Author = "Fixed Name"; App.Lib.SaveBookInfo(rb);
    await App.Lib.ReplaceBooksAsync("jellyfin", new() { Remote() });
    rb = App.Lib.Books.Single(b => b.Source == "jellyfin");
    Check(rb is { Part: 2, Author: "Fixed Name", Edited: true } && rb.Progress == 0.7, "server books keep position and edits across syncs");
    await App.Lib.RemoveSourceAsync("jellyfin");
    Check(App.Lib.Books.All(b => b.Source == "local") && App.Lib.Books.Count == 5, "removing a server removes its books only");
}

// ------------------------------------------------------------------
Section("podcast redirects, tags, sorting, crawler");
{
    // an episode address that redirects (like a tracking hop in front of the real file)
    var wavBytes = File.ReadAllBytes(Directory.EnumerateFiles(music, "*.wav", SearchOption.AllDirectories).First());
    var listener = new System.Net.HttpListener();
    var port = 18000 + Random.Shared.Next(1000);
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    _ = Task.Run(async () =>
    {
        while (listener.IsListening)
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                if (ctx.Request.Url!.AbsolutePath == "/hop1") { ctx.Response.StatusCode = 302; ctx.Response.RedirectLocation = "/hop2"; }
                else if (ctx.Request.Url.AbsolutePath == "/hop2") { ctx.Response.StatusCode = 302; ctx.Response.RedirectLocation = "/real/episode.mp3"; }
                else { ctx.Response.ContentType = "audio/mpeg"; ctx.Response.OutputStream.Write(wavBytes); }
                ctx.Response.Close();
            }
            catch { }
        }
    });
    var finalUrl = await PodcastService.ResolveAsync($"http://127.0.0.1:{port}/hop1");
    Check(finalUrl == $"http://127.0.0.1:{port}/real/episode.mp3", "podcast redirects are followed to the real file: " + finalUrl);
    var cached = await PodcastService.DownloadAsync(finalUrl);
    Check(File.Exists(cached) && new FileInfo(cached).Length == wavBytes.Length && cached.EndsWith(".mp3") && !File.Exists(cached + ".part"), "episode download lands in the cache");
    var redirEp = new Episode { Title = "Redirected", Url = $"http://127.0.0.1:{port}/hop1", Guid = "g1" };
    var item = Player.From(redirEp, new Podcast { Title = "Show" });
    Check(await item.Resolve!() == finalUrl && item.FallbackAsync != null, "podcast item resolves redirects when it starts");
    App.Player.Play(new[] { item });
    await Task.Delay(500);
    Check(engine.Url == finalUrl, "player hands the finalUrl address to the engine: " + engine.Url);
    engine.FireFailed("source error");
    await Task.Delay(500);
    Check(engine.Url == cached, "if streaming fails the episode is downloaded and the file is played: " + engine.Url);
    App.Player.Stop();
    listener.Stop();

    // tags
    var songs = App.Lib.Tracks.Where(t => t.SourceId.StartsWith(music)).OrderBy(t => t.Title).ToList();
    App.Lib.ApplyTags(Library.SongTag, new[] { songs[0].Id, songs[1].Id }, new[] { "Road Trip", "#workout", " ,, " }, Array.Empty<string>());
    App.Lib.ApplyTags(Library.SongTag, new[] { songs[0].Id }, new[] { "chill" }, Array.Empty<string>());
    Check(App.Lib.TagText(Library.SongTag, songs[0].Id) == "chill, road trip, workout" && songs[0].TagText == "chill, road trip, workout", "tags are normalised and listed: " + songs[0].TagText);
    Check(App.Lib.CommonTags(Library.SongTag, new[] { songs[0].Id, songs[1].Id }).SequenceEqual(new[] { "road trip", "workout" }), "tags shared by several songs");
    Check(Library.ParseQuery("rock \"road trip\" #chill #\"two words\"").Select(t => (t.Text, t.IsTag)).SequenceEqual(new[] { ("rock", false), ("road trip", false), ("chill", true), ("two words", true) }), "search terms: words, quoted phrases, #tags");
    Check(App.Lib.Search("#road").Songs.Count == 2 && App.Lib.Search("#\"road trip\" #chill").Songs.Select(t => t.Id).SequenceEqual(new[] { songs[0].Id }), "#tag search finds tagged songs");
    Check(App.Lib.Search("workout").Songs.Count == 2, "plain words match tags too");
    Check(App.Lib.Search("#").Tags.Select(t => t.Tag).SequenceEqual(new[] { "road trip", "workout", "chill" }) || App.Lib.Search("#").Tags.Count == 3, "# on its own lists the tags");
    var reloaded2 = new Library(new Database(AppPaths.DbPath)); reloaded2.Load();
    Check(reloaded2.TagText(Library.SongTag, songs[0].Id) == "chill, road trip, workout", "tags persist");
    var ebookItem = App.Lib.Books.First(b => b.Format == "epub");
    App.Lib.ApplyTags(Library.BookTag, new[] { ebookItem.Id }, new[] { "to read" }, Array.Empty<string>());
    Check(App.Lib.Search("#\"to read\"").Books.Count == 1, "books can be tagged and found");
    App.Lib.ApplyTags(Library.SongTag, new[] { songs[1].Id }, Array.Empty<string>(), new[] { "workout" });
    Check(App.Lib.Search("#workout").Songs.Count == 1, "tags can be removed");

    // sorting
    var sort = new SongSort();
    sort.Click("title");
    var asc = sort.Apply(songs).Select(t => t.Title).ToList();
    sort.Click("title");
    var desc = sort.Apply(songs).Select(t => t.Title).ToList();
    sort.Click("title");
    Check(asc.SequenceEqual(asc.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) && desc.SequenceEqual(asc.AsEnumerable().Reverse()) && !sort.Active && sort.Apply(songs).SequenceEqual(songs), "sort: ascending, descending, back to original order");
    App.Lib.ApplyTags(Library.SongTag, new[] { songs[1].Id }, Array.Empty<string>(), new[] { "road trip" });   // leaves one song untagged
    sort.Set("tags", false);
    Check(sort.Apply(App.Lib.Tracks).First().Id == songs[0].Id && sort.Apply(App.Lib.Tracks).Last().TagText == "", "sort by tags puts untagged songs last: " + string.Join(" | ", sort.Apply(App.Lib.Tracks).Select(t => t.Title + "=" + t.TagText)));

    // crawler: listing carries modified time and size; hidden files are skipped
    File.WriteAllBytes(Path.Combine(music, ".hidden.wav"), wavBytes);
    var crawled = Crawler.Crawl(new[] { music, music }, Library.AudioExts);
    var one2 = crawled.First(f => f.Path.EndsWith("01 One.wav"));
    Check(crawled.Count == 3 && one2.Modified == File.GetLastWriteTimeUtc(one2.Path).Ticks && one2.Size == new FileInfo(one2.Path).Length, $"crawler lists files once with time and size ({crawled.Count} files)");
    File.Delete(Path.Combine(music, ".hidden.wav"));
}

Section("radio, podcast sources, play log, sleep timer, speed, sections, backup, updates");
{
    Listening.Offline = true;
    var S = App.Settings;
    var lib = App.Lib;
    var P = App.Player;
    P.Stop();

    // sections can be switched off, all but the last
    var off = S.SetTab("books", false) && !S.TabOn("books");
    foreach (var tab in Settings.Tabs.Where(t => t != "videos")) S.SetTab(tab, false);
    var lastStays = !S.SetTab("videos", false) && S.TabOn("videos");
    foreach (var tab in Settings.Tabs) S.SetTab(tab, true);
    Check(off && lastStays && S.TabsOff.Count == 0, "every collection section can be switched off except the last one left");

    // sorting and filtering choices
    var view = new ViewChoice("test");
    view.SortBy("title");
    view.SortBy("title");
    view.ShowOnly("liked");
    view.Text = "blue moon";
    var again = new ViewChoice("test");
    Check(view.Descending && again.Sort == "title" && again.Descending && again.Show == "liked" && view.Matches("Blue", "Moon river") && !view.Matches("Blue", "sun")
          && view.Order(new[] { 2, 3, 1 }, key => key == "title" ? l => l.OrderBy(x => x) : null).SequenceEqual(new[] { 3, 2, 1 }), "a page's sort, direction and show choices are remembered, and a filter needs every word");
    view.Clear();
    Check(!view.Active, "sort and filter can be cleared");

    // radio
    var stations = Radio.Parse(System.Text.Json.JsonDocument.Parse("""
        [{"name":" Jazz FM ","url":"http://old.example/s.pls","url_resolved":"https://stream.example/jazz","tags":"jazz,smooth,soul,extra","countrycode":"GB"},
         {"name":"","url":"https://x.example/a"},{"name":"Again","url_resolved":"https://stream.example/jazz"}]
        """).RootElement);
    Check(stations.Count == 1 && stations[0].Name == "Jazz FM" && stations[0].Url == "https://stream.example/jazz" && stations[0].Subtitle == "GB  ·  jazz, smooth, soul", "stations from the directory are read without blanks or repeats");
    var genres = Radio.ParseGenres(System.Text.Json.JsonDocument.Parse("""[{"name":"pop","stationcount":3000},{"name":"x","stationcount":900},{"name":"rare","stationcount":3}]""").RootElement);
    var countries = Radio.ParseCountries(System.Text.Json.JsonDocument.Parse("""[{"name":"Norway","iso_3166_1":"NO","stationcount":210},{"name":"Austria","iso_3166_1":"AT","stationcount":400}]""").RootElement);
    Check(genres.Count == 1 && genres[0].Name == "pop" && countries.Select(c => c.Key).SequenceEqual(new[] { "AT", "NO" }), "genres and countries are read for browsing");
    lib.SaveStation(stations[0]);
    lib.TogglePin("radio", stations[0].Url, stations[0].Name);
    var pinned = Catalog.Pins().Any(q => q.Title == "Jazz FM" && q.Subtitle == "radio");
    S.SetTab("radio", false);
    var hiddenPin = !Catalog.Pins().Any(q => q.Subtitle == "radio");
    S.SetTab("radio", true);
    P.PlayStation(lib.Stations[0]);
    Check(lib.Stations.Count == 1 && pinned && hiddenPin && P.Current is { Station: not null, Track: null } && engine.Url == "https://stream.example/jazz" && !P.CanSpeed,
        "a station is saved, pinned (hidden while radio is off) and played as an endless stream");
    P.Stop();
    lib.RemoveStation(lib.Stations[0]);
    Check(lib.Stations.Count == 0 && !lib.IsPinned("radio", stations[0].Url) && Radio.FromAddress("", "https://radio.example/live") is { Name: "radio.example" } && Radio.FromAddress("x", "nope") == null,
        "removing a station takes its pin away; an address typed in becomes a station");

    // podcast sources
    var chart = PodcastDirectory.ParseAppleTop(System.Text.Json.JsonDocument.Parse("""
        {"feed":{"entry":[{"im:name":{"label":"Show One"},"im:artist":{"label":"Maker"},"im:image":[{"label":"https://a.example/55.png"},{"label":"https://a.example/170.png"}],
          "category":{"attributes":{"label":"Technology"}},"id":{"attributes":{"im:id":"123456"}}}]}}
        """).RootElement);
    var found = PodcastDirectory.ParseAppleSearch(System.Text.Json.JsonDocument.Parse("""{"results":[{"collectionName":"Radio Hour","artistName":"Studio","feedUrl":"https://feeds.example/hour","primaryGenreName":"Science","trackCount":310,"collectionId":77}]}""").RootElement);
    var fyyd = PodcastDirectory.ParseFyyd(System.Text.Json.JsonDocument.Parse("""{"data":[{"title":"Open Show","author":"Someone","xmlURL":"https://feeds.example/open","language":"en","episode_count":12},{"title":"No feed","xmlURL":""}]}""").RootElement);
    var opml = PodcastDirectory.ParseOpml(PodcastDirectory.ToOpml(new[] { new Podcast { Title = "First", FeedUrl = "https://feeds.example/first" }, new Podcast { Title = "Second", FeedUrl = "https://feeds.example/second" } }));
    Check(chart.Count == 1 && chart[0] is { Title: "Show One", AppleId: "123456", FeedUrl: "" } && found[0].FeedUrl == "https://feeds.example/hour" && found[0].Subtitle == "Studio  ·  Science  ·  310 episodes"
          && fyyd.Count == 1 && opml.Select(o => o.Title).SequenceEqual(new[] { "First", "Second" }), "podcast directories are read: Apple's charts and search, fyyd, and an OPML list");
    S.PodcastSources.Add(new PodcastSource { Name = "Mine", Url = "https://lists.example/mine.opml" });
    Check(PodcastDirectory.Sources().Select(x => x.Id).SequenceEqual(new[] { "apple", "fyyd", "own:0" }) && PodcastDirectory.Sources()[2].Yours && PodcastDirectory.AppleGenres.Length == 19, "the sources are Apple Podcasts, fyyd and your own lists");
    S.PodcastSources.Clear();
    Check(PodcastService.LooksLikeVideo("video/mp4", "https://x.example/a") && PodcastService.LooksLikeVideo("", "https://x.example/ep.m4v?x=1") && !PodcastService.LooksLikeVideo("audio/mpeg", "https://x.example/ep.mp4"),
        "an episode counts as video when its feed says so or its file is a video");

    // the play log and stats
    var song = lib.Tracks.OrderByDescending(t => t.DurationMs).First();
    var playsBefore = lib.Db.Plays(0, long.MaxValue).Count;
    P.PlayTracks(new[] { song });
    engine.FireOpened();
    engine.FireEnded();
    Listening.Left(new PlayItem { Title = "skipped", Track = song, DurationMs = 200_000 }, 900, finished: false);
    var logged = lib.Db.Plays(0, long.MaxValue);
    var year = ListenStats.FromLog("this year", new DateTimeOffset(new DateTime(DateTime.Now.Year, 1, 1)).ToUnixTimeSeconds(), Fmt.Now + 5);
    Check(logged.Count - playsBefore == 1 && logged[^1].Counted && logged[^1].Title == song.Title && year.Plays >= 1 && year.TopSongs[0].Name == song.Title && ListenStats.AllTime().Plays >= 1,
        "a song played to the end goes into the play log and the stats; one skipped at once does not");
    var sig = Listening.Sign(new Dictionary<string, string> { ["token"] = "yyy", ["api_key"] = "xxx", ["method"] = "auth.getSession" }, "secret");
    var wantSig = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("api_keyxxxmethodauth.getSessiontokenyyysecret"))).ToLowerInvariant();
    Check(sig == wantSig && Listening.ListenBrainzBody("single", new[] { (1700000000L, "A", "T", "", 0L) }).Contains("\"listened_at\":1700000000") && !S.ScrobbleLastFm && lib.Db.PendingScrobbles("lastfm").Count == 0,
        "listens are packed the way Last.fm and ListenBrainz ask, and nothing is queued for a service that is off");
    S.Keys["listenbrainz"] = "token-123";
    S.Save();
    var settingsText = File.ReadAllText(AppPaths.SettingsPath);
    Check(!settingsText.Contains("token-123") && Settings.Load().Keys["listenbrainz"] == "token-123", "scrobbling keys are stored encrypted");
    S.Keys.Clear();

    // sleep timer and speed
    P.PlayTracks(lib.Tracks.Take(3));
    engine.FireOpened();
    var volume = P.Volume;
    P.SetSleep(TimeSpan.FromMilliseconds(300));
    Thread.Sleep(400);
    P.SleepTick();
    Check(!P.IsPlaying && !P.SleepOn && engine.Paused && Math.Abs(P.Volume - volume) < 0.001 && Math.Abs(engine.Volume - (float)volume) < 0.001, "the sleep timer pauses playback and leaves the volume as it was");
    P.Toggle();
    P.SetSleep(null, atEnd: true);
    var index = P.CurrentIndex;
    engine.FireEnded();
    Check(P.CurrentIndex == index + 1 && !P.IsPlaying && !P.SleepAtEnd && P.SleepText == "", "“when this one ends” stops at the start of the next item");
    var fast = new SpeedEngine();
    var p2 = new Player(fast);
    var episode = new Episode { Id = 900, PodcastId = 55, Title = "Ep", Url = "https://media.example/ep.mp3" };
    p2.Play(new[] { new PlayItem { Title = "Ep", Url = episode.Url, Episode = episode } });
    p2.Speed = 1.5;
    p2.Play(new[] { new PlayItem { Title = "Other", Url = "https://media.example/2.mp3", Episode = new Episode { Id = 901, PodcastId = 55, Title = "Other", Url = "https://media.example/2.mp3" } } });
    var remembered = p2.Speed;
    p2.Play(new[] { Player.From(lib.Tracks[0]) });
    Check(Math.Abs(remembered - 1.5) < 0.01 && S.Speeds.GetValueOrDefault("podcast:55") == 1.5 && p2.Speed == 1 && fast.Speed == 1 && !P.CanSpeed,
        "playback speed is kept per podcast series; music starts at normal speed; an engine that cannot change speed is left alone");
    p2.Stop();
    S.Speeds.Clear();
    P.Stop();

    // backup
    using var packed = new MemoryStream();
    Backup.Write(packed);
    packed.Position = 0;
    var manifest = Backup.Stage(packed);
    var pending = Path.Combine(AppPaths.DataDir, "restore-pending");
    var staged = File.Exists(Path.Combine(pending, "library.db")) && File.Exists(Path.Combine(pending, "settings.json"));
    Backup.Discard();
    var notOne = false;
    try { Backup.Stage(new MemoryStream(Encoding.UTF8.GetBytes("hello"))); } catch { notOne = true; }
    Check(manifest.App == "Phoenix" && manifest.Songs == lib.Tracks.Count && staged && !Directory.Exists(pending) && notOne && packed.Length > 1000, "a backup holds the library and settings, can be unpacked for restoring, and anything else is refused");

    // updates
    var release = Updates.Parse(System.Text.Json.JsonDocument.Parse($$"""
        {"tag_name":"v9.4.1","name":"Phoenix 9.4.1","html_url":"https://github.com/{{Updates.Repo}}/releases/tag/v9.4.1",
         "assets":[{"name":"Phoenix-9.4.1-win-x64-setup.exe","size":5,"browser_download_url":"https://github.com/{{Updates.Repo}}/releases/download/v9.4.1/Phoenix-9.4.1-win-x64-setup.exe"},
                   {"name":"Phoenix-9.4.1.apk","size":123,"browser_download_url":"https://github.com/{{Updates.Repo}}/releases/download/v9.4.1/Phoenix-9.4.1.apk"}]}
        """).RootElement, ".apk");
    var elsewhere = Updates.Parse(System.Text.Json.JsonDocument.Parse("""{"tag_name":"v9.9.9","assets":[{"name":"Phoenix.apk","size":1,"browser_download_url":"https://downloads.example/Phoenix.apk"}]}""").RootElement, ".apk");
    Check(release is { VersionText: "9.4.1", FileName: "Phoenix-9.4.1.apk", FileSize: 123 } && release.Version > new Version(1, 0, 0) && elsewhere is { FileUrl: "" } && Updates.ParseVersion("v1.2") == new Version(1, 2, 0),
        "a newer release is recognised with the file for this platform, and a file from anywhere else is never offered");
}

Section("the auto equalizer");
{
    // made-up songs: noise shaped to be dull (little treble), boomy (lots of bass) or balanced
    float[] Shaped(double bassDb, double trebleDb, int seconds = 12, int rate = 44100)
    {
        var rnd = new Random(7);
        var white = new float[rate * seconds];
        for (var i = 0; i < white.Length; i++) white[i] = (float)(rnd.NextDouble() * 2 - 1);
        // pink-ish: Paul Kellet's filter, then a low and a high one-pole shelf
        double b0 = 0, b1 = 0, b2 = 0, lp = 0, hp = 0, prev = 0;
        var o = new float[white.Length];
        double gl = Math.Pow(10, bassDb / 20) - 1, gh = Math.Pow(10, trebleDb / 20) - 1;
        double al = Math.Exp(-2 * Math.PI * 150 / rate), ah = Math.Exp(-2 * Math.PI * 3000 / rate);
        for (var i = 0; i < white.Length; i++)
        {
            var w = white[i];
            b0 = 0.99765 * b0 + w * 0.0990460; b1 = 0.96300 * b1 + w * 0.2965164; b2 = 0.57000 * b2 + w * 1.0526913;
            var pink = (b0 + b1 + b2 + w * 0.1848) * 0.05;
            lp = (1 - al) * pink + al * lp;
            hp = ah * (hp + pink - prev); prev = pink;
            o[i] = (float)(pink + gl * lp + gh * hp);
        }
        return o;
    }
    var dull = AutoEq.Measure(Shaped(0, -14), 44100)!;
    var boomy = AutoEq.Measure(Shaped(12, 0), 44100)!;
    var even = AutoEq.Measure(Shaped(0, 0), 44100)!;
    Check(dull != null && boomy != null && even != null && AutoEq.Measure(new float[44100 * 5], 44100) == null, "songs are measured band by band; silence isn't");
    Check(Math.Abs(even.Max() - even.Min()) < 2.5 && dull[9] < even[9] - 5 && dull[1] > even[1] - 1 && boomy[1] > even[1] + 8 && boomy[8] > even[8] - 1,
        $"the measurement follows the sound: flat stays flat, less treble and more bass show where they are (dull {string.Join(",", dull.Select(x => x.ToString("0")))})");
    // the sounds, from band levels set against a well-balanced mix
    double[] balanced = { 34, 40, 40, 39, 38, 37, 35.5, 34, 32, 26 };
    double[] Off(params (int band, double db)[] change) { var v = balanced.ToArray(); foreach (var (b, d) in change) v[b] += d; return v; }
    var dullAcc = AutoEq.Gains(Off((7, -6), (8, -9), (9, -10)), "accuracy");
    var boomyAcc = AutoEq.Gains(Off((0, 9), (1, 10), (2, 8)), "accuracy");
    var balancedAcc = AutoEq.Gains(balanced, "accuracy");
    Check(dullAcc[8] > 1.5 && boomyAcc[1] < -1.5 && balancedAcc.All(g => Math.Abs(g) < 0.6),
        $"sound accuracy lifts a dull song's treble, tames a boomy song's bass, and leaves a balanced one alone: dull {string.Join(",", dullAcc)}  boomy {string.Join(",", boomyAcc)}");
    var evenBass = AutoEq.Gains(balanced, "bass");
    var boomyBass = AutoEq.Gains(Off((0, 9), (1, 10), (2, 8)), "bass");
    var thinBass = AutoEq.Gains(Off((0, -8), (1, -8), (2, -6)), "bass");
    Check(evenBass[1] > 2 && thinBass[1] > evenBass[1] + 1 && boomyBass[1] < evenBass[1] - 1, $"bass lifts the low end, more on a thin song, less on one that has plenty: thin {thinBass[1]}  even {evenBass[1]}  boomy {boomyBass[1]}");
    var evenClarity = AutoEq.Gains(balanced, "clarity");
    var muddyClarity = AutoEq.Gains(Off((3, 6), (4, 6)), "clarity");
    Check(evenClarity[6] > 0.5 && evenClarity[7] > 1 && evenClarity[3] < 0 && muddyClarity[3] < evenClarity[3] - 1, $"clarity brings the presence forward and trims the low mids, more on a muddy song: {string.Join(",", evenClarity)}");
    Check(new[] { dullAcc, boomyAcc, evenBass, boomyBass, thinBass, evenClarity, muddyClarity }.All(g => g.All(x => x >= -8 && x <= 8)), "no band goes past 8 dB either way");
    Check(AutoEq.Parse(AutoEq.Format(dull))!.Zip(dull).All(z => Math.Abs(z.First - z.Second) < 0.06), "a measurement round-trips through its text form");

    // keeping it: Phoenix's library, the song's tags, the album folder's file
    var dir = Path.Combine(Path.GetTempPath(), "phoenix-autoeq-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var mp3 = Path.Combine(dir, "song.mp3");
    WriteWav(Path.ChangeExtension(mp3, ".wav"), 1);
    File.Copy(Path.ChangeExtension(mp3, ".wav"), Path.Combine(dir, "other.wav"));
    var song = new Track { Source = "local", SourceId = mp3 + "-x", Location = Path.Combine(dir, "other.wav"), Title = "x" };
    AutoEq.Remember(song, even, "folder");
    var inFolder = File.Exists(Path.Combine(dir, AutoEq.FolderFile)) && File.ReadAllText(Path.Combine(dir, AutoEq.FolderFile)).Contains("other.wav");
    AutoEq.Remember(new Track { Source = "jellyfin", SourceId = "srv1", Location = "srv1|direct" }, boomy, "tags");
    Check(inFolder && AutoEq.LevelsFor(new Track { Source = "local", SourceId = "fresh", Location = song.Location }) is { } f && Math.Abs(f[1] - even[1]) < 0.06,
        "a measurement kept in the album folder's file is found for the song");
    Check(AutoEq.LevelsFor(new Track { Source = "jellyfin", SourceId = "srv1", Location = "srv1|direct" }) is { } srv && Math.Abs(srv[1] - boomy[1]) < 0.06,
        "a song on a server (no tags to write) is kept in Phoenix's library instead");
    App.Settings.AutoEq = true; App.Settings.AutoEqProfile = "bass"; App.Settings.EqEnabled = true; App.Settings.EqGains = new double[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };
    var combined = AutoEq.Combine(App.Settings, new Track { Source = "local", SourceId = "fresh2", Location = song.Location })!;
    Check(Math.Abs(combined[1] - (1 + AutoEq.Gains(even, "bass")[1])) < 0.01, "the auto equalizer goes on top of your own equalizer");
    App.Settings.AutoEq = false; App.Settings.EqEnabled = false;
    try { Directory.Delete(dir, true); } catch { }

    // the AI part: styles shift the target
    var electronic = AutoEq.Gains(balanced, "accuracy", new Dictionary<string, double> { ["electronic"] = 0.8 });
    var classical = AutoEq.Gains(balanced, "accuracy", new Dictionary<string, double> { ["classical"] = 0.8 });
    Check(electronic[1] > balancedAcc[1] + 0.5 && classical[1] < balancedAcc[1] - 0.3, $"a song recognised as electronic gets more low end than one recognised as classical: {electronic[1]} vs {classical[1]}");
    var withStyles = AutoEq.Format(even, new Dictionary<string, double> { ["rock"] = 0.42, ["vocal"] = 0.2 });
    Check(AutoEq.Parse(withStyles) is { Length: 10 } && AutoEq.ParseStyles(withStyles) is { } st && Math.Abs(st["rock"] - 0.42) < 0.001 && AutoEq.ParseStyles(AutoEq.Format(even)) == null,
        "the styles the AI heard are kept with the measurement");

    // headphones: AutoEq's ten-band profiles
    var (phones, preamp) = HeadphoneEq.Parse("Preamp: -4.1 dB\nFilter 1: ON PK Fc 31 Hz Gain 3.9 dB Q 1.41\nFilter 2: ON PK Fc 62 Hz Gain 1.5 dB Q 1.41\nFilter 3: ON PK Fc 125 Hz Gain -2.7 dB Q 1.41\n" +
        "Filter 4: ON PK Fc 250 Hz Gain -1.6 dB Q 1.41\nFilter 5: ON PK Fc 500 Hz Gain -0.4 dB Q 1.41\nFilter 6: ON PK Fc 1000 Hz Gain 1.0 dB Q 1.41\nFilter 7: ON PK Fc 2000 Hz Gain 0.1 dB Q 1.41\n" +
        "Filter 8: ON PK Fc 4000 Hz Gain 1.5 dB Q 1.41\nFilter 9: ON PK Fc 8000 Hz Gain 2.1 dB Q 1.41\nFilter 10: ON PK Fc 16000 Hz Gain -9.3 dB Q 1.41");
    Check(preamp == -4.1 && phones[0] == 3.9 && phones[9] == -9.3, "a headphone profile's ten bands are read");
    App.Settings.HeadphoneOn = true; App.Settings.HeadphoneGains = phones;
    var phonesOnly = AutoEq.Combine(App.Settings, null)!;
    Check(phonesOnly[9] == -9.3, "the headphone correction goes on the equalizer by itself too");
    App.Settings.HeadphoneOn = false; App.Settings.HeadphoneGains = Array.Empty<double>();

    if (Environment.GetEnvironmentVariable("PHOENIX_NET_TESTS") == "1")
    {
        var found = await HeadphoneEq.SearchAsync("sony wf-1000xm3");
        var profile = found.Count > 0 ? await HeadphoneEq.FetchAsync(found[0]) : default;
        Check(found.Count > 0 && found[0].Name.StartsWith("Sony WF-1000XM3") && profile.Gains?.Length == 10, $"AutoEq's list is searched and a profile fetched: {found.FirstOrDefault()?.Label} {string.Join(",", profile.Gains ?? Array.Empty<double>())}");
        if (!AutoEqAi.Have) await AutoEqAi.DownloadAsync(_ => { }, CancellationToken.None);
        var sample = Environment.GetEnvironmentVariable("PHOENIX_SPEECH_SAMPLE") ?? "";
        if (File.Exists(sample))
        {
            using var reader = new System.IO.BinaryReader(File.OpenRead(sample));
            var bytes = File.ReadAllBytes(sample);
            // a 16-bit WAV: find "data", read the samples
            var at = 12; var rate = BitConverter.ToInt32(bytes, 24); var channels = BitConverter.ToInt16(bytes, 22);
            while (at + 8 < bytes.Length && System.Text.Encoding.ASCII.GetString(bytes, at, 4) != "data") at += 8 + BitConverter.ToInt32(bytes, at + 4);
            var n = (bytes.Length - at - 8) / 2 / channels;
            var mono = new float[n];
            for (var i = 0; i < n; i++) mono[i] = BitConverter.ToInt16(bytes, at + 8 + i * 2 * channels) / 32768f;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var heard = AutoEqAi.Recognise(mono, rate);
            Check(heard != null && heard.GetValueOrDefault("speech") > 0.3, $"YAMNet recognises speech in a spoken clip, on {AutoEqAi.RanOn} in {watch.ElapsedMilliseconds} ms: {AutoEqAi.Describe(heard)} ({(heard == null ? "" : AutoEqAi.Format(heard))})");
        }
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? $"ALL {checks} CHECKS PASSED" : $"{failures} of {checks} CHECKS FAILED");
try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); } catch { }
return failures == 0 ? 0 : 1;

static List<byte[]> ReadZipPages(string file)
{
    using var z = System.IO.Compression.ZipFile.OpenRead(file);
    return new[] { "page1.png", "page2.png", "page10.png" }.Select(n => { using var s = z.GetEntry(n)!.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }).ToList();
}
static List<byte[]> ReadRarOrder(string file)
{
    using var a = SharpCompress.Archives.ArchiveFactory.OpenArchive(file);
    return a.Entries.Where(e => !e.IsDirectory).OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => { using var s = e.OpenEntryStream(); using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }).ToList();
}

static string Session(string url) => url[(url.IndexOf("PlaySessionId=") + 14)..].Split('&')[0];

class FakeEngine : IMediaEngine
{
    public string Url = "";
    public bool Paused;
    public long Position;
    public List<long> Seeks = new() { -1 };
    public void Load(string url, bool isVideo) { Url = url; Paused = false; Position = 0; }
    public void Play() => Paused = false;
    public void Pause() => Paused = true;
    public void Stop() { }
    public long PositionMs => Position;
    public long DurationMs => 0;
    public void SeekTo(long ms) { Seeks.Add(ms); Position = ms; }
    public float Volume { get; set; }
    public event Action? Opened;
    public event Action? Ended;
    public event Action<string>? Failed;
    public void FireOpened() => Opened?.Invoke();
    public void FireEnded() => Ended?.Invoke();
    public void FireFailed(string m) => Failed?.Invoke(m);
}

/// <summary>An engine that can also play faster or slower.</summary>
class SpeedEngine : FakeEngine, ISpeedEngine
{
    public float Speed { get; set; } = 1;
}

class XorSecrets : ISecretProtector
{
    public string Protect(string plain) => Convert.ToBase64String(Encoding.UTF8.GetBytes(plain).Select(b => (byte)(b ^ 0x5A)).ToArray());
    public string Unprotect(string stored) => Encoding.UTF8.GetString(Convert.FromBase64String(stored).Select(b => (byte)(b ^ 0x5A)).ToArray());
}
