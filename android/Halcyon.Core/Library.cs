using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Halcyon.Services;

namespace Halcyon.Core;

public partial class Library
{
    public static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".m4b", ".aac", ".wma", ".flac", ".wav", ".ogg", ".opus" };
    public static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".wmv", ".avi", ".mov", ".mkv", ".mpg", ".mpeg", ".webm" };
    public static readonly HashSet<string> PictureExts = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic" };
    static readonly string[] CoverNames = { "folder.jpg", "cover.jpg", "front.jpg", "album.jpg", "folder.png", "cover.png", "front.png" };

    public Database Db { get; }
    public List<Track> Tracks = new();
    public List<Album> Albums = new();
    public List<VideoItem> Videos = new();
    public List<Playlist> Playlists = new();
    public List<Podcast> Podcasts = new();
    public List<Pin> Pins = new();
    public Dictionary<long, Track> ById = new();
    public bool IsScanning { get; private set; }

    /// <summary>Raised on the UI thread whenever tracks, videos, playlists, podcasts or pins change.</summary>
    public event Action? Changed;

    /// <summary>Raised when songs are liked, disliked or cleared (lighter than <see cref="Changed"/>: lists keep their place).</summary>
    public event Action? RatingsChanged;

    public Library(Database db) => Db = db;

    long _lastRatedAt;

    public void Rate(IEnumerable<Track> tracks, int rating)
    {
        foreach (var t in tracks)
        {
            t.Rating = rating;
            t.RatedAt = _lastRatedAt = Math.Max(Fmt.Now, _lastRatedAt + 1);   // strictly increasing, so order is exact within a second
            Db.SaveStats(t);
            t.Raise();
        }
        RatingsChanged?.Invoke();
    }

    /// <summary>Saves ratings and play counts that were set elsewhere (a sync with the PC) and refreshes the screens.</summary>
    public void StoreStats(IEnumerable<Track> tracks)
    {
        var list = tracks.ToList();
        Db.Batch(() => { foreach (var t in list) Db.SaveStats(t); });
        foreach (var t in list) t.Raise();
        RatingsChanged?.Invoke();
    }

    /// <summary>Liked songs, most recently liked first.</summary>
    public List<Track> Liked() => Tracks.Where(t => t.Rating > 0).OrderByDescending(t => t.RatedAt).ThenByDescending(t => t.LastPlayed).ToList();

    public void Load()
    {
        Tracks = Db.LoadTracks();
        Videos = Db.LoadVideos();
        Playlists = Db.LoadPlaylists();
        Podcasts = Db.LoadPodcasts();
        Pins = Db.LoadPins();
        Books = Db.LoadBooks();
        LoadTags();
        Rebuild();
    }

    void Rebuild()
    {
        ById = Tracks.ToDictionary(t => t.Id);
        Albums = Tracks.GroupBy(t => t.AlbumKey).Select(g =>
        {
            var list = g.OrderBy(t => t.DiscNo).ThenBy(t => t.TrackNo).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
            var f = list[0];
            return new Album
            {
                Key = g.Key, Title = f.Album, Artist = f.AlbumArtist, Source = f.Source,
                Year = list.Max(t => t.Year),
                Genre = list.Select(t => t.Genre).FirstOrDefault(x => x != "") ?? "",
                ArtPath = list.Select(t => t.ArtPath).FirstOrDefault(x => x != "") ?? "",
                DateAdded = list.Max(t => t.DateAdded),
                Tracks = list
            };
        }).OrderBy(a => a.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Year).ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void NotifyChanged()
    {
        Rebuild();
        Changed?.Invoke();
    }

    static void Ui(Action a) => App.Ui(a);

    // ---------- local scanning ----------

    public async Task ScanAsync(Action<string> status)
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            var s = App.Settings;
            var musicFolders = s.MusicFolders.ToList();
            var videoFolders = s.VideoFolders.ToList();
            var existing = Tracks.Where(t => t.IsLocal).ToDictionary(t => t.SourceId, StringComparer.OrdinalIgnoreCase);
            var existingVideos = Videos.Where(v => v.Source == "local").ToDictionary(v => v.SourceId, StringComparer.OrdinalIgnoreCase);
            status("scanning collection…");

            await Task.Run(() =>
            {
                // Reading tags is mostly waiting on storage, so a few files are read at once.
                var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4) };

                // ---- music: one crawl (the directory listing carries each file's modified time), then only new or changed files are opened ----
                var found = Crawler.Crawl(musicFolders, AudioExts);
                var seen = found.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var changed = found.Where(f => !existing.TryGetValue(f.Path, out var old) || old.FileModified != f.Modified).ToList();
                var pending = new List<Track>();
                var done = 0;

                void FlushUi()
                {
                    if (pending.Count == 0) return;
                    var items = pending.ToList();
                    pending.Clear();
                    Ui(() =>
                    {
                        var ids = items.Select(t => t.Id).ToHashSet();
                        Tracks = Tracks.Where(t => !ids.Contains(t.Id)).Concat(items).ToList();
                        NotifyChanged();
                        status($"scanning collection… {done} of {changed.Count} new or changed songs");
                    });
                }

                foreach (var chunk in changed.Chunk(250))
                {
                    var items = new Track[chunk.Length];
                    Parallel.For(0, chunk.Length, parallel, i => items[i] = ReadTrack(chunk[i].Path, chunk[i].Modified));
                    Db.Batch(() => { foreach (var t in items) Db.UpsertTrack(t); });   // one transaction per chunk, not one per song
                    pending.AddRange(items);
                    done += items.Length;
                    if (pending.Count >= 500) FlushUi();                               // the screens catch up in steps, not on every chunk
                }
                FlushUi();

                // Drop tracks whose file is gone, but keep ones on a drive/folder that is merely offline.
                var gone = existing.Values.Where(t => !seen.Contains(t.SourceId) && IsReallyGone(t.SourceId, musicFolders)).ToList();
                if (gone.Count > 0)
                {
                    Db.Batch(() => { foreach (var t in gone) Db.DeleteTrack(t.Id); });
                    var ids = gone.Select(t => t.Id).ToHashSet();
                    Ui(() => { Tracks = Tracks.Where(t => !ids.Contains(t.Id)).ToList(); NotifyChanged(); });
                }

                // ---- videos ----
                var foundVideos = Crawler.Crawl(videoFolders, VideoExts);
                var seenVideos = foundVideos.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var newVideoFiles = foundVideos.Where(f => !existingVideos.TryGetValue(f.Path, out var known) || known.Kind == "").ToList();
                var newVideos = new List<VideoItem>();
                foreach (var chunk in newVideoFiles.Chunk(100))
                {
                    var items = new VideoItem[chunk.Length];
                    Parallel.For(0, chunk.Length, parallel, i =>
                    {
                        var file = chunk[i].Path;
                        var v = new VideoItem { Source = "local", SourceId = file, Location = file, ArtPath = "thumb:" + file, DateAdded = Fmt.Now };
                        Classify(v, file);
                        try { using var tf = TagLib.File.Create(file); v.DurationMs = (long)tf.Properties.Duration.TotalMilliseconds; } catch { }
                        items[i] = v;
                    });
                    Db.Batch(() => { foreach (var v in items) Db.UpsertVideo(v); });
                    newVideos.AddRange(items);
                }
                var goneVideos = existingVideos.Values.Where(v => !seenVideos.Contains(v.SourceId) && IsReallyGone(v.SourceId, videoFolders)).ToList();
                if (goneVideos.Count > 0) Db.Batch(() => { foreach (var v in goneVideos) Db.DeleteVideo(v.Id); });
                if (newVideos.Count > 0 || goneVideos.Count > 0)
                    Ui(() =>
                    {
                        var ids = goneVideos.Concat(newVideos).Select(v => v.Id).ToHashSet();
                        Videos = Videos.Where(v => !ids.Contains(v.Id)).Concat(newVideos).ToList();
                        NotifyChanged();
                    });
            });
            await ScanBooksAsync(status);
            status("");
        }
        catch (Exception ex) { status("scan failed: " + ex.Message); }
        finally { IsScanning = false; }
    }

    static readonly Regex EpisodePattern = new(@"^(.*?)[\s._-]*\bS(\d{1,2})[\s._-]*E(\d{1,3})\b[\s._-]*(.*)$", RegexOptions.IgnoreCase);

    /// <summary>Works out what a video file is from its name and folder: "Show.S01E02.Title" is an episode; anything else is grouped by its folder.</summary>
    public static void Classify(VideoItem v, string file)
    {
        static string Clean(string s) => Regex.Replace(s.Replace('.', ' ').Replace('_', ' '), @"\s+", " ").Trim(' ', '-');
        var name = Path.GetFileNameWithoutExtension(file);
        var folder = Path.GetFileName(Path.GetDirectoryName(file) ?? "") ?? "";
        var m = EpisodePattern.Match(name);
        if (m.Success)
        {
            var series = Clean(m.Groups[1].Value);
            if (series == "")
            {
                // "Show/Season 1/S01E02.mkv": the show is the folder above the season folder.
                var dir = Path.GetDirectoryName(file) ?? "";
                series = Regex.IsMatch(folder, @"^(season|series)\s*\d+$", RegexOptions.IgnoreCase) ? Path.GetFileName(Path.GetDirectoryName(dir) ?? "") ?? folder : folder;
            }
            v.Kind = "episode";
            v.Series = series;
            v.Season = int.Parse(m.Groups[2].Value);
            v.Episode = int.Parse(m.Groups[3].Value);
            v.Title = Clean(m.Groups[4].Value) is { Length: > 0 } t ? t : $"Episode {v.Episode}";
        }
        else
        {
            v.Kind = "video";
            v.Series = folder;
            v.Title = name;
        }
    }

    static IEnumerable<string> Enumerate(string folder, HashSet<string> exts)
    {
        if (!Directory.Exists(folder)) return Array.Empty<string>();
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(folder, "*", opts).Where(f => exts.Contains(Path.GetExtension(f)));
    }

    static bool IsReallyGone(string file, List<string> folders)
    {
        var parent = folders.FirstOrDefault(f => file.StartsWith(f.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        return parent == null || Directory.Exists(parent);
    }

    public static string ArtCachePath(string source, string albumArtist, string album)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{source}|{albumArtist}|{album}".ToLowerInvariant())))[..20];
        return Path.Combine(AppPaths.ArtDir, hash + ".jpg");
    }

    static Track ReadTrack(string file, long modified)
    {
        var t = new Track
        {
            Source = "local", SourceId = file, Location = file, FileModified = modified, DateAdded = Fmt.Now,
            Title = Path.GetFileNameWithoutExtension(file), Artist = "Unknown Artist", AlbumArtist = "Unknown Artist", Album = "Unknown Album"
        };
        byte[]? embedded = null;
        try
        {
            using var f = TagLib.File.Create(file);
            var tag = f.Tag;
            if (!string.IsNullOrWhiteSpace(tag.Title)) t.Title = tag.Title.Trim();
            var artist = tag.FirstPerformer ?? tag.FirstAlbumArtist;
            if (!string.IsNullOrWhiteSpace(artist)) t.Artist = artist.Trim();
            t.AlbumArtist = string.IsNullOrWhiteSpace(tag.FirstAlbumArtist) ? t.Artist : tag.FirstAlbumArtist.Trim();
            if (!string.IsNullOrWhiteSpace(tag.Album)) t.Album = tag.Album.Trim();
            t.Genre = tag.FirstGenre?.Trim() ?? "";
            t.Year = (int)tag.Year;
            t.TrackNo = (int)tag.Track;
            t.DiscNo = (int)tag.Disc;
            t.DurationMs = (long)f.Properties.Duration.TotalMilliseconds;
            if (tag.Pictures.Length > 0) embedded = tag.Pictures[0].Data.Data;
        }
        catch { /* unreadable tags: keep the file with name-derived info */ }

        var art = ArtCachePath("local", t.AlbumArtist, t.Album);
        if (File.Exists(art)) t.ArtPath = art;
        else if (embedded is { Length: > 0 })
        {
            try { File.WriteAllBytes(art, embedded); t.ArtPath = art; } catch { }
        }
        else
        {
            var dir = Path.GetDirectoryName(file)!;
            t.ArtPath = CoverNames.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists) ?? "";
        }
        return t;
    }

    // ---------- remote sources ----------

    public async Task ReplaceSourceAsync(string source, List<Track> tracks, List<VideoItem> videos)
    {
        var oldTracks = Tracks.Where(t => t.Source == source).ToList();
        var oldVideos = Videos.Where(v => v.Source == source).ToList();

        // Anything the user re-labelled or regrouped here stays that way, exactly as an edited local file would.
        var editedTracks = oldTracks.Where(t => t.Edited).ToDictionary(t => t.SourceId);
        foreach (var t in tracks)
            if (editedTracks.TryGetValue(t.SourceId, out var o))
            {
                t.Title = o.Title; t.Artist = o.Artist; t.AlbumArtist = o.AlbumArtist; t.Album = o.Album; t.Genre = o.Genre;
                t.Year = o.Year; t.TrackNo = o.TrackNo; t.DiscNo = o.DiscNo; t.Edited = true;
                if (o.ArtPath.StartsWith(AppPaths.ArtDir, StringComparison.OrdinalIgnoreCase)) t.ArtPath = o.ArtPath;
            }
        var editedVideos = oldVideos.Where(v => v.Edited).ToDictionary(v => v.SourceId);
        foreach (var v in videos)
            if (editedVideos.TryGetValue(v.SourceId, out var o))
            {
                v.Title = o.Title; v.Kind = o.Kind; v.Series = o.Series; v.Season = o.Season; v.Episode = o.Episode;
                v.Year = o.Year; v.Collection = o.Collection; v.Edited = true;
            }
        await Task.Run(() => Db.Batch(() =>
        {
            foreach (var t in tracks) Db.UpsertTrack(t);
            foreach (var v in videos) Db.UpsertVideo(v);
            var keep = tracks.Select(t => t.Id).ToHashSet();
            foreach (var t in oldTracks.Where(t => !keep.Contains(t.Id))) Db.DeleteTrack(t.Id);
            var keepV = videos.Select(v => v.Id).ToHashSet();
            foreach (var v in oldVideos.Where(v => !keepV.Contains(v.Id))) Db.DeleteVideo(v.Id);
        }));
        Tracks = Tracks.Where(t => t.Source != source).Concat(tracks).ToList();
        Videos = Videos.Where(v => v.Source != source).Concat(videos).ToList();
        NotifyChanged();
    }

    public async Task RemoveSourceAsync(string source)
    {
        await ReplaceSourceAsync(source, new(), new());
        await ReplaceBooksAsync(source, new());
    }

    bool _syncing;

    /// <summary>Refreshes every connected server, the same way a rescan refreshes folders.</summary>
    public async Task SyncServersAsync(Action<string> status)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            if (App.Jellyfin.IsConnected)
                try
                {
                    var (tracks, videos) = await App.Jellyfin.FetchAsync(status);
                    await ReplaceSourceAsync("jellyfin", tracks, videos);
                    await ReplaceBooksAsync("jellyfin", await App.Jellyfin.FetchBooksAsync(status));
                    status("");
                }
                catch (Exception ex) { status("jellyfin sync failed: " + ex.Message); }
            if (App.Plex.IsConnected)
                try
                {
                    var (tracks, videos) = await App.Plex.FetchAsync(status);
                    await ReplaceSourceAsync("plex", tracks, videos);
                    status("");
                }
                catch (Exception ex) { status("plex sync failed: " + ex.Message); }
        }
        finally { _syncing = false; }
    }

    public void SaveVideos(IEnumerable<VideoItem> videos)
    {
        var list = videos.ToList();
        Db.Batch(() => { foreach (var v in list) { v.Edited = true; Db.UpdateVideo(v); } });
        foreach (var v in list) v.Raise();
        NotifyChanged();
    }

    // ---------- editing ----------

    /// <summary>Persists edited metadata; writes file tags for local tracks when enabled. Returns files that could not be written.</summary>
    public int SaveTracks(IEnumerable<Track> tracks, byte[]? cover = null)
    {
        var failed = 0;
        var list = tracks.ToList();
        foreach (var t in list)
        {
            if (!t.IsLocal) t.Edited = true;
            else if (App.Settings.WriteTagsToFiles && !WriteTags(t, cover)) failed++;
        }
        Db.Batch(() => { foreach (var t in list) Db.UpdateTrack(t); });
        foreach (var t in list) t.Raise();
        NotifyChanged();
        return failed;
    }

    static bool WriteTags(Track t, byte[]? cover)
    {
        try
        {
            using var f = TagLib.File.Create(t.SourceId);
            f.Tag.Title = t.Title;
            f.Tag.Performers = new[] { t.Artist };
            f.Tag.AlbumArtists = new[] { t.AlbumArtist };
            f.Tag.Album = t.Album;
            f.Tag.Genres = t.Genre == "" ? Array.Empty<string>() : new[] { t.Genre };
            f.Tag.Year = (uint)Math.Max(0, t.Year);
            f.Tag.Track = (uint)Math.Max(0, t.TrackNo);
            if (cover != null) f.Tag.Pictures = new TagLib.IPicture[] { new TagLib.Picture(new TagLib.ByteVector(cover)) };
            f.Save();
            t.FileModified = File.GetLastWriteTimeUtc(t.SourceId).Ticks;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Applies a Discogs release to an album. Returns the number of files whose tags could not be written.</summary>
    public async Task<int> ApplyReleaseAsync(Album album, DiscogsRelease rel, bool albumInfo, bool titles, bool cover, byte[]? coverBytes = null)
    {
        var tracks = album.Tracks.ToList();
        var oldArtist = album.Artist;
        if (albumInfo)
        {
            foreach (var t in tracks)
            {
                if (rel.Title != "") t.Album = rel.Title;
                if (rel.Artist != "")
                {
                    if (string.Equals(t.Artist, oldArtist, StringComparison.OrdinalIgnoreCase)) t.Artist = rel.Artist;
                    t.AlbumArtist = rel.Artist;
                }
                if (rel.Year > 0) t.Year = rel.Year;
                if (rel.Genre != "") t.Genre = rel.Genre;
            }
        }
        if (titles && rel.Tracks.Count == tracks.Count)
            for (var i = 0; i < tracks.Count; i++)
            {
                tracks[i].Title = rel.Tracks[i].Title;
                tracks[i].TrackNo = i + 1;
            }

        byte[]? bytes = null;
        if (cover && rel.CoverUrl != "")
        {
            try
            {
                // any source: a web address (Discogs needs its token), or a picture in the album's folder
                bytes = coverBytes ?? await Halcyon.Services.Metadata.GetBytesAsync(rel.CoverUrl);
                var path = ArtCachePath(album.Source, tracks[0].AlbumArtist, tracks[0].Album);
                await File.WriteAllBytesAsync(path, bytes);
                App.ArtInvalidate(path);
                foreach (var t in tracks) t.ArtPath = path;
            }
            catch { bytes = null; }
        }
        else if (albumInfo && tracks[0].ArtPath.StartsWith(AppPaths.ArtDir, StringComparison.OrdinalIgnoreCase) && File.Exists(tracks[0].ArtPath))
        {
            // Album identity changed: carry the cached cover over to the new cache key.
            var path = ArtCachePath(album.Source, tracks[0].AlbumArtist, tracks[0].Album);
            try { if (!File.Exists(path)) File.Copy(tracks[0].ArtPath, path); foreach (var t in tracks) t.ArtPath = path; } catch { }
        }
        return await Task.Run(() =>
        {
            var failed = 0;
            foreach (var t in tracks)
            {
                if (!t.IsLocal) t.Edited = true;
                else if (App.Settings.WriteTagsToFiles && !WriteTags(t, bytes)) failed++;
            }
            Db.Batch(() => { foreach (var t in tracks) Db.UpdateTrack(t); });
            Ui(() => { foreach (var t in tracks) t.Raise(); NotifyChanged(); });
            return failed;
        });
    }

    // ---------- playlists & pins ----------

    public List<Track> TracksOf(Playlist p) => p.TrackIds.Where(ById.ContainsKey).Select(id => ById[id]).ToList();

    public Playlist CreatePlaylist(string name)
    {
        var p = Db.CreatePlaylist(name);
        Playlists.Add(p);
        Playlists = Playlists.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Changed?.Invoke();
        return p;
    }

    public void AddToPlaylist(Playlist p, IEnumerable<Track> tracks)
    {
        p.TrackIds.AddRange(tracks.Select(t => t.Id));
        Db.SavePlaylistTracks(p);
        Changed?.Invoke();
    }

    public void SavePlaylist(Playlist p)
    {
        Db.RenamePlaylist(p);
        Db.SavePlaylistTracks(p);
        Playlists = Playlists.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Changed?.Invoke();
    }

    public void DeletePlaylist(Playlist p)
    {
        Db.DeletePlaylist(p.Id);
        Playlists.Remove(p);
        Pins.RemoveAll(x => x.Kind == "playlist" && x.Key == p.Id.ToString());
        Db.RemovePin(new Pin("playlist", p.Id.ToString(), ""));
        Changed?.Invoke();
    }

    public bool IsPinned(string kind, string key) => Pins.Any(p => p.Kind == kind && p.Key == key);

    public void TogglePin(string kind, string key, string title)
    {
        var existing = Pins.FirstOrDefault(p => p.Kind == kind && p.Key == key);
        if (existing != null) { Pins.Remove(existing); Db.RemovePin(existing); }
        else { var pin = new Pin(kind, key, title); Pins.Add(pin); Db.AddPin(pin); }
        Changed?.Invoke();
    }

    public List<Track> AutoPlaylist(string kind) => kind switch
    {
        "liked" => Liked(),
        "most played" => Tracks.Where(t => t.PlayCount > 0).OrderByDescending(t => t.PlayCount).Take(100).ToList(),
        "recently added" => Tracks.OrderByDescending(t => t.DateAdded).ThenBy(t => t.Album).ThenBy(t => t.TrackNo).Take(100).ToList(),
        "recently played" => Tracks.Where(t => t.LastPlayed > 0).OrderByDescending(t => t.LastPlayed).Take(100).ToList(),
        _ => new()
    };

    /// <summary>A shuffled mix seeded by one artist: their songs plus others sharing their genres, skipping disliked songs.</summary>
    public List<Track> SmartMix(string artist)
    {
        var own = Tracks.Where(t => t.Rating >= 0 && (string.Equals(t.AlbumArtist, artist, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Artist, artist, StringComparison.OrdinalIgnoreCase))).ToList();
        var genres = own.Select(t => t.Genre).Where(g => g != "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var related = Tracks.Where(t => t.Rating >= 0 && genres.Contains(t.Genre)).Except(own);
        var rnd = new Random();
        return own.OrderBy(_ => rnd.Next()).Take(20).Concat(related.OrderBy(_ => rnd.Next()).Take(40)).OrderBy(_ => rnd.Next()).ToList();
    }
}
