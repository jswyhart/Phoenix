using System.Text.Json;
using Halcyon.Core;

namespace Phoenix.Backend;

/// <summary>
/// What the window can ask for. Each screen asks for the lists it shows (built by the shared core's Catalog, as the
/// other apps do) and names things back by id: a song or video by its database id, an album by its key.
/// </summary>
public static partial class Api
{
    static Library Lib => App.Lib;
    static Player P => App.Player;
    static Settings S => App.Settings;
    static readonly StringComparer NoCase = StringComparer.OrdinalIgnoreCase;
    static bool _started;
    static Timer? _tick;
    static int _ticks;

    public static void Register()
    {
        // ---- events ----
        P.CurrentChanged += () => Rpc.Emit("player", PlayerState());
        P.StateChanged += () => Rpc.Emit("player", PlayerState());
        P.QueueChanged += () => Rpc.Emit("queue", QueueState());
        P.Error += s => Rpc.Emit("status", s);
        Lib.Changed += () => Rpc.Emit("lib", Counts());
        Lib.RatingsChanged += () => { Rpc.Emit("ratings"); Rpc.Emit("player", PlayerState()); };
        App.Status += s => Rpc.Emit("status", s);
        App.ArtInvalidate = path => Rpc.Emit("art", path);
        _tick = MainLoop.Every(250, () =>
        {
            // Every ten seconds, note where a playing podcast or audiobook is, so a crash loses little.
            if (++_ticks % 40 == 0 && P.IsPlaying) P.SaveEpisodePosition();
            if (P.Current != null) Rpc.Emit("tick", new { pos = P.PositionMs, dur = P.DurationMs });
        });

        // ---- start-up ----
        Rpc.On("init", p =>
        {
            if (!_started && !Program.SmokeMode) App.RefreshCollectionAsync().ContinueWith(t => { if (t.Exception != null) Program.LogError("scan", t.Exception); });
            _started = true;
            return new
            {
                platform = Program.IsMac ? "mac" : Program.IsLinux ? "linux" : "win",
                smoke = Program.SmokeMode,
                bench = Environment.GetEnvironmentVariable("PHOENIX_BENCH") == "1",
                settings = SettingsState(),
                counts = Counts(),
                player = PlayerState(),
                queue = QueueState(),
                vizStyles = VisualizerOptions.Styles,
                vizPalettes = VisualizerOptions.Palettes,
                vizSensitivities = VisualizerOptions.Sensitivities,
                sortKeys = SongSort.Keys.Select(k => new { key = k.Key, label = k.Label }),
                autoPlaylists = Catalog.AutoPlaylists
            };
        });
        Rpc.On("scan", async _ => { await App.RefreshCollectionAsync(); return Counts(); });
        // where the page connects for video frames; asked for by the main process, which hands the address to the page
        Rpc.On("video.socket", _ => $"ws://127.0.0.1:{Program.Frames.Port}/video?token={Program.Frames.Token}");
        Rpc.On("art.resolve", async p => new { file = await ArtFiles.ResolveAsync(p.Str("path"), p.Int("size", 300)) });

        RegisterPlayer();
        RegisterMusic();
        RegisterSettings();
        RegisterPages();
        RegisterPhone();
        RegisterSmoke();
    }

    // ---------- shapes sent to the window ----------

    static object Counts() => new
    {
        tracks = Lib.Tracks.Count, albums = Lib.Albums.Count, videos = Lib.Videos.Count, books = Lib.Books.Count,
        podcasts = Lib.Podcasts.Count, playlists = Lib.Playlists.Count, scanning = Lib.IsScanning
    };

    static object Song(Track t) => new
    {
        id = t.Id, title = t.Title, artist = t.Artist, album = t.Album, no = t.TrackNo, dur = t.DurationMs,
        rating = t.Rating, tags = t.TagText, local = t.IsLocal
    };

    static object Tile(Album a) => new
    {
        key = a.Key, title = a.Title, caption = a.Source == "local" ? a.Artist : $"{a.Artist}  ·  {a.Source}", art = a.ArtPath
    };

    static List<Track> Tracks(JsonElement p, string name = "ids") => p.Longs(name).Where(Lib.ById.ContainsKey).Select(id => Lib.ById[id]).ToList();
    static Album? AlbumByKey(string? key) => key == null ? null : Lib.Albums.FirstOrDefault(a => a.Key == key);

    static object PlayerState()
    {
        var cur = P.Current;
        return new
        {
            current = cur == null ? null : new
            {
                title = cur.Title, subtitle = cur.Subtitle, album = cur.Album, art = cur.ArtPath, isVideo = cur.IsVideo,
                trackId = cur.Track?.Id, rating = cur.Track?.Rating ?? 0, isBook = cur.Book != null, bookId = cur.Book?.Id
            },
            index = P.CurrentIndex,
            playing = P.IsPlaying,
            shuffle = P.Shuffle,
            repeat = P.Repeat,
            volume = P.Volume,
            muted = Program.Engine.Muted,
            pos = P.PositionMs,
            dur = P.DurationMs
        };
    }

    static object QueueState() => P.Queue.Select(i => new { title = i.Title, subtitle = i.Subtitle, dur = i.DurationMs }).ToList();

    // ---------- transport ----------

    static void RegisterPlayer()
    {
        Rpc.On("player.state", _ => PlayerState());
        Rpc.On("player.toggle", _ => P.Toggle());
        Rpc.On("player.next", _ => P.Next());
        Rpc.On("player.prev", _ => P.Previous());
        Rpc.On("player.stop", _ => P.Stop());
        Rpc.On("player.seek", p => P.Seek(p.Long("ms")));
        Rpc.On("player.playAt", p => P.PlayAt(p.Int("index")));
        Rpc.On("player.removeAt", p => P.RemoveAt(p.Int("index")));
        Rpc.On("player.volume", p => { P.Volume = Math.Clamp(p.Num("value"), 0, 1); });
        Rpc.On("player.mute", p => { Program.Engine.Muted = p.Has("value") ? p.Bool("value") : !Program.Engine.Muted; Rpc.Emit("player", PlayerState()); });
        Rpc.On("player.shuffle", _ => { P.Shuffle = !P.Shuffle; Rpc.Emit("player", PlayerState()); });
        Rpc.On("player.repeat", _ => { P.Repeat = (P.Repeat + 1) % 3; Rpc.Emit("player", PlayerState()); });

        // songs: play a list (optionally starting at one of them, or shuffled), or add to what is playing
        Rpc.On("play.tracks", p =>
        {
            var list = Tracks(p);
            if (p.Bool("shuffle")) P.PlayShuffled(list);
            else P.PlayTracks(list, p.LongOrNull("start") is { } id && Lib.ById.TryGetValue(id, out var t) ? t : null);
        });
        Rpc.On("queue.tracks", p => P.EnqueueTracks(Tracks(p)));
        Rpc.On("play.album", p => { if (AlbumByKey(p.StrOrNull("key")) is { } a) P.PlayTracks(a.Tracks); });
        Rpc.On("queue.album", p => { if (AlbumByKey(p.StrOrNull("key")) is { } a) P.EnqueueTracks(a.Tracks); });

        Rpc.On("rate", p => Lib.Rate(Tracks(p), Math.Clamp(p.Int("rating"), -1, 1)));
        // cycles a song's rating: unrated → liked → disliked → unrated
        Rpc.On("rate.cycle", p =>
        {
            if (Lib.ById.TryGetValue(p.Long("id"), out var t)) Lib.Rate(new[] { t }, t.Rating == 0 ? 1 : t.Rating == 1 ? -1 : 0);
        });
    }

    // ---------- music ----------

    static void RegisterMusic()
    {
        // The whole music screen for one state of it: mode (artists | genres | albums | songs), the chosen artist or
        // genre, the chosen album, and the song sort.
        Rpc.On("music.view", p =>
        {
            var mode = p.Str("mode", "artists");
            var sel = p.StrOrNull("sel");
            var albumKey = p.StrOrNull("album");
            List<string>? left = mode == "artists" ? Catalog.Artists() : mode == "genres" ? Catalog.Genres() : null;
            if (sel != null && left != null && !left.Contains(sel, NoCase)) sel = null;

            List<Album> albums =
                sel != null && mode == "artists" ? Catalog.AlbumsOf(sel)
                : sel != null && mode == "genres" ? Catalog.AlbumsInGenre(sel)
                : mode == "albums" ? Lib.Albums.OrderBy(a => a.Title, NoCase).ToList()
                : Lib.Albums.ToList();
            var album = albums.FirstOrDefault(a => a.Key == albumKey);

            List<Track> songs;
            if (mode == "songs")
            {
                var sort = new SongSort();
                if (p.Str("sortKey") != "") sort.Set(p.Str("sortKey"), p.Bool("sortDesc"));
                songs = sort.Apply(Catalog.Songs());
            }
            else if (album != null) songs = album.Tracks.ToList();
            else
            {
                var tracks = albums.SelectMany(a => a.Tracks);
                if (sel != null && mode == "genres") tracks = tracks.Where(t => string.Equals(t.Genre, sel, StringComparison.OrdinalIgnoreCase));
                songs = tracks.ToList();
            }
            return new
            {
                counts = Fmt.Join(Fmt.Plural(Catalog.Artists().Count, "artist"), Fmt.Plural(Lib.Albums.Count, "album"), Fmt.Plural(Lib.Tracks.Count, "song")),
                empty = Lib.Tracks.Count == 0,
                left,
                sel,
                albums = mode == "songs" ? null : albums.Select(Tile),
                album = album?.Key,
                songs = songs.Select(Song)
            };
        });

        Rpc.On("artist.tracks", p => Catalog.AlbumsOf(p.Str("artist")).SelectMany(a => a.Tracks).Select(t => t.Id));
        Rpc.On("artist.smartMix", p => P.PlayTracks(Lib.SmartMix(p.Str("artist"))));
        Rpc.On("album.info", p => AlbumByKey(p.StrOrNull("key")) is { } a
            ? new { key = a.Key, title = a.Title, artist = a.Artist, source = a.Source, pinned = Lib.IsPinned("album", a.Key), ids = a.Tracks.Select(t => t.Id) }
            : null);
        Rpc.On("track.albumKey", p => Lib.ById.TryGetValue(p.Long("id"), out var t) ? Catalog.AlbumOf(t)?.Key : null);
        Rpc.On("track.path", p => Lib.ById.TryGetValue(p.Long("id"), out var t) && t.IsLocal ? t.SourceId : null);
        // forty covers in random order for the wall behind "now playing"
        Rpc.On("now.mosaic", _ =>
        {
            var rnd = new Random();
            return Lib.Albums.Where(a => a.ArtPath != "").Select(a => a.ArtPath).Distinct().OrderBy(_ => rnd.Next()).Take(40).ToList();
        });
        Rpc.On("pin.toggle", p => Lib.TogglePin(p.Str("kind"), p.Str("key"), p.Str("title")));
        Rpc.On("pin.is", p => Lib.IsPinned(p.Str("kind"), p.Str("key")));
    }

    // ---------- settings ----------

    static object SettingsState() => new
    {
        theme = S.Theme == "dark" ? "dark" : "light",
        musicFolders = S.MusicFolders, videoFolders = S.VideoFolders, pictureFolders = S.PictureFolders, bookFolders = S.BookFolders,
        writeTags = S.WriteTagsToFiles,
        visualizer = S.Visualizer, visualizerColor = S.VisualizerColor, visualizerSensitivity = S.VisualizerSensitivity,
        readerFontSize = S.ReaderFontSize, comicRightToLeft = S.ComicRightToLeft,
        jellyfinUrl = S.JellyfinUrl, jellyfinUser = S.JellyfinUser, jellyfinConnected = App.Jellyfin.IsConnected,
        plexUrl = S.PlexUrl, plexConnected = App.Plex.IsConnected, hasPlexToken = S.PlexToken != "",
        hasDiscogsToken = S.DiscogsToken != ""
    };

    static List<string> FolderList(string kind) => kind switch { "music" => S.MusicFolders, "video" => S.VideoFolders, "book" => S.BookFolders, _ => S.PictureFolders };

    static void Save() { if (!Program.SmokeMode) S.Save(); }

    static void RegisterSettings()
    {
        Rpc.On("settings.get", _ => SettingsState());
        Rpc.On("settings.set", p =>
        {
            if (p.Has("theme")) S.Theme = p.Str("theme") == "dark" ? "dark" : "light";
            if (p.Has("writeTags")) S.WriteTagsToFiles = p.Bool("writeTags");
            if (p.Has("visualizer")) S.Visualizer = Math.Clamp(p.Int("visualizer"), 0, VisualizerOptions.Styles.Length - 1);
            if (p.Has("visualizerColor")) S.VisualizerColor = Math.Clamp(p.Int("visualizerColor"), 0, VisualizerOptions.Palettes.Length - 1);
            if (p.Has("visualizerSensitivity")) S.VisualizerSensitivity = Math.Clamp(p.Int("visualizerSensitivity"), 0, VisualizerOptions.Sensitivities.Length - 1);
            if (p.Has("readerFontSize")) S.ReaderFontSize = Math.Clamp(p.Int("readerFontSize"), 12, 40);
            if (p.Has("comicRightToLeft")) S.ComicRightToLeft = p.Bool("comicRightToLeft");
            Save();
            return SettingsState();
        });
        Rpc.On("settings.addFolders", p =>
        {
            var list = FolderList(p.Str("kind"));
            foreach (var f in p.Strs("paths"))
                if (f != "" && !list.Contains(f, NoCase)) list.Add(f);
            Save();
            return SettingsState();
        });
        Rpc.On("settings.removeFolder", p =>
        {
            FolderList(p.Str("kind")).Remove(p.Str("path"));
            Save();
            return SettingsState();
        });
    }
}
