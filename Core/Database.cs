using Microsoft.Data.Sqlite;

namespace Halcyon.Core;

public partial class Database
{
    readonly SqliteConnection _c;
    readonly object _lock = new();
    SqliteTransaction? _tx;

    public Database(string path)
    {
        _c = new SqliteConnection($"Data Source={path}");
        _c.Open();
        Exec("PRAGMA journal_mode=WAL");
        Exec("""
            CREATE TABLE IF NOT EXISTS tracks(Id INTEGER PRIMARY KEY, Source TEXT NOT NULL, SourceId TEXT NOT NULL, Location TEXT,
                Title TEXT, Artist TEXT, AlbumArtist TEXT, Album TEXT, Genre TEXT, Year INT, TrackNo INT, DiscNo INT, DurationMs INT,
                ArtPath TEXT, FileModified INT, DateAdded INT, Rating INT DEFAULT 0, PlayCount INT DEFAULT 0, LastPlayed INT DEFAULT 0,
                UNIQUE(Source,SourceId));
            CREATE TABLE IF NOT EXISTS videos(Id INTEGER PRIMARY KEY, Source TEXT NOT NULL, SourceId TEXT NOT NULL, Location TEXT,
                Title TEXT, DurationMs INT, ArtPath TEXT, DateAdded INT, UNIQUE(Source,SourceId));
            CREATE TABLE IF NOT EXISTS playlists(Id INTEGER PRIMARY KEY, Name TEXT);
            CREATE TABLE IF NOT EXISTS playlist_tracks(PlaylistId INT, Position INT, TrackId INT);
            CREATE TABLE IF NOT EXISTS podcasts(Id INTEGER PRIMARY KEY, FeedUrl TEXT UNIQUE, Title TEXT, Author TEXT, Description TEXT, ArtUrl TEXT);
            CREATE TABLE IF NOT EXISTS episodes(Id INTEGER PRIMARY KEY, PodcastId INT, Guid TEXT, Title TEXT, Published INT, Url TEXT,
                DurationMs INT, Played INT DEFAULT 0, PositionMs INT DEFAULT 0, Description TEXT, IsVideo INT DEFAULT 0, UNIQUE(PodcastId,Guid));
            CREATE TABLE IF NOT EXISTS pins(Kind TEXT, Key TEXT, Title TEXT, UNIQUE(Kind,Key));
            """);
        CreateBookTables();
        CreateTagTable();
        AddColumn("tracks", "GroupId", "TEXT");
        AddColumn("tracks", "Edited", "INT DEFAULT 0");
        AddColumn("tracks", "RatedAt", "INT DEFAULT 0");
        AddColumn("episodes", "Fresh", "INT DEFAULT 0");
        AddColumn("episodes", "File", "TEXT DEFAULT ''");
        AddColumn("episodes", "Manual", "INT DEFAULT 0");
        foreach (var c in new[] { "Kind TEXT", "Series TEXT", "Season INT", "Episode INT", "Year INT", "Collection TEXT", "GroupArt TEXT", "Edited INT DEFAULT 0" })
            AddColumn("videos", c.Split(' ')[0], c[(c.IndexOf(' ') + 1)..]);
    }

    /// <summary>
    /// The library remembers where covers and downloads are kept by their full path. When the data folder has moved
    /// (the app was renamed from Halcyon to Phoenix), those paths are pointed at the new place. Returns rows changed.
    /// </summary>
    public int MoveDataFolder(string oldRoot, string newRoot)
    {
        var changed = 0;
        foreach (var (table, column) in new[] { ("tracks", "ArtPath"), ("videos", "ArtPath"), ("videos", "GroupArt"), ("books", "ArtPath"), ("episodes", "File") })
        {
            try
            {
                changed += Query($"UPDATE {table} SET {column} = $p1 || substr({column}, length($p0) + 1) WHERE lower(substr({column}, 1, length($p0))) = lower($p0) RETURNING 1",
                    r => 1, oldRoot, newRoot).Count;
            }
            catch { /* a table without that column */ }
        }
        return changed;
    }

    /// <summary>Writes a clean, complete copy of the library to another file (works while the library is open).</summary>
    public void CopyTo(string file) => Exec($"VACUUM INTO '{file.Replace("'", "''")}'");

    // ---- translated names ----
    public List<(string Source, string Result)> LoadTranslations(string lang)
    {
        Exec("CREATE TABLE IF NOT EXISTS translations(Lang TEXT NOT NULL, Source TEXT NOT NULL, Result TEXT, PRIMARY KEY(Lang,Source))");
        return Query("SELECT Source,Result FROM translations WHERE Lang=$p0", r => (S(r, 0), S(r, 1)), lang);
    }

    public void SaveTranslation(string lang, string source, string result)
    {
        Exec("CREATE TABLE IF NOT EXISTS translations(Lang TEXT NOT NULL, Source TEXT NOT NULL, Result TEXT, PRIMARY KEY(Lang,Source))");
        Exec("INSERT INTO translations(Lang,Source,Result) VALUES($p0,$p1,$p2) ON CONFLICT(Lang,Source) DO UPDATE SET Result=excluded.Result", lang, source, result);
    }

    void AddColumn(string table, string column, string decl)
    {
        if (!Query($"PRAGMA table_info({table})", r => r.GetString(1)).Contains(column))
            Exec($"ALTER TABLE {table} ADD COLUMN {column} {decl}");
    }

    SqliteCommand Command(string sql, object?[] args)
    {
        var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _tx;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
        return cmd;
    }

    public int Exec(string sql, params object?[] args)
    {
        lock (_lock) { using var cmd = Command(sql, args); return cmd.ExecuteNonQuery(); }
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params object?[] args)
    {
        lock (_lock)
        {
            using var cmd = Command(sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    public void Batch(Action action)
    {
        lock (_lock)
        {
            if (_tx != null) { action(); return; }
            _tx = _c.BeginTransaction();
            try { action(); _tx.Commit(); }
            catch { _tx.Rollback(); throw; }
            finally { _tx.Dispose(); _tx = null; }
        }
    }

    static string S(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    static long L(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);

    // ---- tracks ----
    static readonly string[] TrackCols = { "Source", "SourceId", "Location", "Title", "Artist", "AlbumArtist", "Album", "Genre", "Year", "TrackNo", "DiscNo", "DurationMs", "ArtPath", "FileModified", "DateAdded", "GroupId" };
    static readonly string TrackUpsert =
        $"INSERT INTO tracks({string.Join(",", TrackCols)}) VALUES({string.Join(",", TrackCols.Select((_, i) => "$p" + i))}) " +
        $"ON CONFLICT(Source,SourceId) DO UPDATE SET {string.Join(",", TrackCols.Where(c => c is not ("Source" or "SourceId" or "DateAdded")).Select(c => $"{c}=excluded.{c}"))} " +
        "RETURNING Id, Rating, PlayCount, LastPlayed, DateAdded";

    public List<Track> LoadTracks() => Query(
        $"SELECT Id,{string.Join(",", TrackCols)},Rating,PlayCount,LastPlayed,Edited,RatedAt FROM tracks", r => new Track
        {
            Id = L(r, 0), Source = S(r, 1), SourceId = S(r, 2), Location = S(r, 3), Title = S(r, 4), Artist = S(r, 5),
            AlbumArtist = S(r, 6), Album = S(r, 7), Genre = S(r, 8), Year = (int)L(r, 9), TrackNo = (int)L(r, 10), DiscNo = (int)L(r, 11),
            DurationMs = L(r, 12), ArtPath = S(r, 13), FileModified = L(r, 14), DateAdded = L(r, 15),
            GroupId = S(r, 16), Rating = (int)L(r, 17), PlayCount = (int)L(r, 18), LastPlayed = L(r, 19), Edited = L(r, 20) != 0, RatedAt = L(r, 21)
        });

    /// <summary>Inserts or refreshes metadata; ratings, play counts and date added survive.</summary>
    public void UpsertTrack(Track t)
    {
        lock (_lock)
        {
            using var cmd = Command(TrackUpsert, new object?[] { t.Source, t.SourceId, t.Location, t.Title, t.Artist, t.AlbumArtist, t.Album, t.Genre, t.Year, t.TrackNo, t.DiscNo, t.DurationMs, t.ArtPath, t.FileModified, t.DateAdded, t.GroupId });
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return;
            t.Id = L(r, 0); t.Rating = (int)L(r, 1); t.PlayCount = (int)L(r, 2); t.LastPlayed = L(r, 3); t.DateAdded = L(r, 4);
        }
    }

    public void UpdateTrack(Track t) => Exec(
        "UPDATE tracks SET Title=$p0,Artist=$p1,AlbumArtist=$p2,Album=$p3,Genre=$p4,Year=$p5,TrackNo=$p6,DiscNo=$p7,ArtPath=$p8,FileModified=$p9,Edited=$p10 WHERE Id=$p11",
        t.Title, t.Artist, t.AlbumArtist, t.Album, t.Genre, t.Year, t.TrackNo, t.DiscNo, t.ArtPath, t.FileModified, t.Edited ? 1 : 0, t.Id);

    public void SaveStats(Track t) => Exec("UPDATE tracks SET Rating=$p0,PlayCount=$p1,LastPlayed=$p2,RatedAt=$p3 WHERE Id=$p4", t.Rating, t.PlayCount, t.LastPlayed, t.RatedAt, t.Id);

    public void DeleteTrack(long id)
    {
        Exec("DELETE FROM tracks WHERE Id=$p0", id);
        Exec("DELETE FROM playlist_tracks WHERE TrackId=$p0", id);
    }

    // ---- videos ----
    const string VideoCols = "Source,SourceId,Location,Title,DurationMs,ArtPath,DateAdded,Kind,Series,Season,Episode,Year,Collection,GroupArt,Edited";

    public List<VideoItem> LoadVideos() => Query($"SELECT Id,{VideoCols} FROM videos", r => new VideoItem
    {
        Id = L(r, 0), Source = S(r, 1), SourceId = S(r, 2), Location = S(r, 3), Title = S(r, 4), DurationMs = L(r, 5), ArtPath = S(r, 6), DateAdded = L(r, 7),
        Kind = S(r, 8), Series = S(r, 9), Season = (int)L(r, 10), Episode = (int)L(r, 11), Year = (int)L(r, 12), Collection = S(r, 13), GroupArt = S(r, 14), Edited = L(r, 15) != 0
    });

    /// <summary>Inserts or fully refreshes a video; date added survives.</summary>
    public void UpsertVideo(VideoItem v)
    {
        var cols = VideoCols.Split(',');
        var ids = Query(
            $"INSERT INTO videos({VideoCols}) VALUES({string.Join(",", cols.Select((_, i) => "$p" + i))}) " +
            $"ON CONFLICT(Source,SourceId) DO UPDATE SET {string.Join(",", cols.Where(c => c is not ("Source" or "SourceId" or "DateAdded")).Select(c => $"{c}=excluded.{c}"))} RETURNING Id",
            r => L(r, 0), v.Source, v.SourceId, v.Location, v.Title, v.DurationMs, v.ArtPath, v.DateAdded,
            v.Kind, v.Series, v.Season, v.Episode, v.Year, v.Collection, v.GroupArt, v.Edited ? 1 : 0);
        if (ids.Count > 0) v.Id = ids[0];
    }

    public void UpdateVideo(VideoItem v) => Exec(
        "UPDATE videos SET Title=$p0,Kind=$p1,Series=$p2,Season=$p3,Episode=$p4,Year=$p5,Collection=$p6,Edited=$p7 WHERE Id=$p8",
        v.Title, v.Kind, v.Series, v.Season, v.Episode, v.Year, v.Collection, v.Edited ? 1 : 0, v.Id);

    public void DeleteVideo(long id) => Exec("DELETE FROM videos WHERE Id=$p0", id);

    // ---- playlists ----
    public List<Playlist> LoadPlaylists()
    {
        var lists = Query("SELECT Id,Name FROM playlists ORDER BY Name COLLATE NOCASE", r => new Playlist { Id = L(r, 0), Name = S(r, 1) });
        foreach (var p in lists)
            p.TrackIds = Query("SELECT TrackId FROM playlist_tracks WHERE PlaylistId=$p0 ORDER BY Position", r => L(r, 0), p.Id);
        return lists;
    }

    public Playlist CreatePlaylist(string name)
    {
        var id = Query("INSERT INTO playlists(Name) VALUES($p0) RETURNING Id", r => L(r, 0), name)[0];
        return new Playlist { Id = id, Name = name };
    }

    public void RenamePlaylist(Playlist p) => Exec("UPDATE playlists SET Name=$p0 WHERE Id=$p1", p.Name, p.Id);

    public void DeletePlaylist(long id)
    {
        Exec("DELETE FROM playlists WHERE Id=$p0", id);
        Exec("DELETE FROM playlist_tracks WHERE PlaylistId=$p0", id);
    }

    public void SavePlaylistTracks(Playlist p) => Batch(() =>
    {
        Exec("DELETE FROM playlist_tracks WHERE PlaylistId=$p0", p.Id);
        for (var i = 0; i < p.TrackIds.Count; i++)
            Exec("INSERT INTO playlist_tracks(PlaylistId,Position,TrackId) VALUES($p0,$p1,$p2)", p.Id, i, p.TrackIds[i]);
    });

    // ---- podcasts ----
    public List<Podcast> LoadPodcasts() => Query(
        "SELECT Id,FeedUrl,Title,Author,Description,ArtUrl,(SELECT COUNT(*) FROM episodes e WHERE e.PodcastId=podcasts.Id AND e.Fresh=1 AND e.Played=0) " +
        "FROM podcasts ORDER BY Title COLLATE NOCASE", r => new Podcast
    {
        Id = L(r, 0), FeedUrl = S(r, 1), Title = S(r, 2), Author = S(r, 3), Description = S(r, 4), ArtUrl = S(r, 5), NewCount = (int)L(r, 6)
    });

    public List<Episode> LoadEpisodes(long podcastId) => Query(
        "SELECT Id,PodcastId,Guid,Title,Published,Url,DurationMs,Played,PositionMs,Description,IsVideo,Fresh,File,Manual FROM episodes WHERE PodcastId=$p0 ORDER BY Published DESC, Id", r => new Episode
        {
            Id = L(r, 0), PodcastId = L(r, 1), Guid = S(r, 2), Title = S(r, 3), Published = L(r, 4), Url = S(r, 5), DurationMs = L(r, 6),
            Played = L(r, 7) != 0, PositionMs = L(r, 8), Description = S(r, 9), IsVideo = L(r, 10) != 0, Fresh = L(r, 11) != 0, File = S(r, 12), Manual = L(r, 13) != 0
        }, podcastId);

    /// <summary>
    /// Saves a feed and its episodes. Returns how many episodes are new since the last save; those are marked fresh.
    /// On a first subscription only the newest episode counts as new, not the whole back catalogue.
    /// </summary>
    public int SavePodcast(Podcast p, List<Episode> episodes)
    {
        var added = 0;
        Batch(() =>
        {
            var known = Query("SELECT Id FROM podcasts WHERE FeedUrl=$p0", r => L(r, 0), p.FeedUrl).Count > 0;
            p.Id = Query(
                "INSERT INTO podcasts(FeedUrl,Title,Author,Description,ArtUrl) VALUES($p0,$p1,$p2,$p3,$p4) " +
                "ON CONFLICT(FeedUrl) DO UPDATE SET Title=excluded.Title,Author=excluded.Author,Description=excluded.Description,ArtUrl=excluded.ArtUrl RETURNING Id",
                r => L(r, 0), p.FeedUrl, p.Title, p.Author, p.Description, p.ArtUrl)[0];
            var have = Query("SELECT Guid FROM episodes WHERE PodcastId=$p0", r => S(r, 0), p.Id).ToHashSet();
            var newest = episodes.OrderByDescending(e => e.Published).FirstOrDefault();
            foreach (var e in episodes)
            {
                var isNew = have.Add(e.Guid);
                var fresh = isNew && (known || ReferenceEquals(e, newest));
                if (isNew && known) added++;
                Exec("INSERT INTO episodes(PodcastId,Guid,Title,Published,Url,DurationMs,Description,IsVideo,Fresh) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8) " +
                     "ON CONFLICT(PodcastId,Guid) DO UPDATE SET Title=excluded.Title,Url=excluded.Url,Published=excluded.Published,Description=excluded.Description",
                    p.Id, e.Guid, e.Title, e.Published, e.Url, e.DurationMs, e.Description, e.IsVideo ? 1 : 0, fresh ? 1 : 0);
            }
        });
        return added;
    }

    public void SaveEpisodeFresh(Episode e) => Exec("UPDATE episodes SET Fresh=$p0 WHERE Id=$p1", e.Fresh ? 1 : 0, e.Id);

    public void SaveEpisodeFile(Episode e) => Exec("UPDATE episodes SET File=$p0,Manual=$p1 WHERE Id=$p2", e.File, e.Manual ? 1 : 0, e.Id);

    /// <summary>Clears the "new" marks of a series (it has been looked at).</summary>
    public void ClearFresh(long podcastId) => Exec("UPDATE episodes SET Fresh=0 WHERE PodcastId=$p0", podcastId);

    public List<string> EpisodeFiles(long podcastId) => Query("SELECT File FROM episodes WHERE PodcastId=$p0 AND File<>''", r => S(r, 0), podcastId);

    public void SaveEpisodeState(Episode e) => Exec("UPDATE episodes SET Played=$p0,PositionMs=$p1 WHERE Id=$p2", e.Played ? 1 : 0, e.PositionMs, e.Id);

    public void DeletePodcast(long id)
    {
        Exec("DELETE FROM podcasts WHERE Id=$p0", id);
        Exec("DELETE FROM episodes WHERE PodcastId=$p0", id);
    }

    // ---- pins ----
    public List<Pin> LoadPins() => Query("SELECT Kind,Key,Title FROM pins ORDER BY rowid", r => new Pin(S(r, 0), S(r, 1), S(r, 2)));
    public void AddPin(Pin p) => Exec("INSERT OR IGNORE INTO pins(Kind,Key,Title) VALUES($p0,$p1,$p2)", p.Kind, p.Key, p.Title);
    public void RemovePin(Pin p) => Exec("DELETE FROM pins WHERE Kind=$p0 AND Key=$p1", p.Kind, p.Key);
}
