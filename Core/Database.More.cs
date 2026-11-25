using Microsoft.Data.Sqlite;

namespace Halcyon.Core;

/// <summary>One listen, as kept in the play log.</summary>
public record PlayRow(long At, long TrackId, string Title, string Artist, string Album, long Ms, bool Counted);

/// <summary>A radio station you saved.</summary>
public class Station
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Art { get; set; } = "";
    public string Tags { get; set; } = "";
    public string Country { get; set; } = "";
    public string Subtitle => string.Join("  ·  ", new[] { Country, Tags }.Where(s => s != ""));
}

/// <summary>The play log, saved radio stations, measured loudness, sound fingerprints and listens waiting to be sent.</summary>
public partial class Database
{
    bool _moreReady;

    /// <summary>Lets go of the file (for a library that was only opened to be looked at).</summary>
    public void Close() { _c.Close(); _c.Dispose(); SqliteConnection.ClearAllPools(); }

    void More()
    {
        if (_moreReady) return;
        _moreReady = true;
        Exec("""
            CREATE TABLE IF NOT EXISTS plays(At INT, TrackId INT, Title TEXT, Artist TEXT, Album TEXT, Ms INT, Counted INT DEFAULT 1);
            CREATE INDEX IF NOT EXISTS plays_at ON plays(At);
            CREATE TABLE IF NOT EXISTS stations(Id INTEGER PRIMARY KEY, Name TEXT, Url TEXT UNIQUE, Art TEXT, Tags TEXT, Country TEXT);
            CREATE TABLE IF NOT EXISTS loudness(TrackId INTEGER PRIMARY KEY, Modified INT, TrackGain REAL, AlbumGain REAL, Measured INT DEFAULT 0);
            CREATE TABLE IF NOT EXISTS fingerprints(TrackId INTEGER PRIMARY KEY, Modified INT, Seconds INT, Print BLOB);
            CREATE TABLE IF NOT EXISTS scrobbles(Id INTEGER PRIMARY KEY, Service TEXT, At INT, Artist TEXT, Title TEXT, Album TEXT, Ms INT);
            """);
    }

    // ---- play log ----
    public void LogPlay(PlayRow p)
    {
        More();
        Exec("INSERT INTO plays(At,TrackId,Title,Artist,Album,Ms,Counted) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)", p.At, p.TrackId, p.Title, p.Artist, p.Album, p.Ms, p.Counted ? 1 : 0);
    }

    public List<PlayRow> Plays(long from, long to)
    {
        More();
        return Query("SELECT At,TrackId,Title,Artist,Album,Ms,Counted FROM plays WHERE At>=$p0 AND At<$p1 ORDER BY At",
            r => new PlayRow(L(r, 0), L(r, 1), S(r, 2), S(r, 3), S(r, 4), L(r, 5), L(r, 6) != 0), from, to);
    }

    public long FirstPlayAt()
    {
        More();
        return Query("SELECT MIN(At) FROM plays", r => L(r, 0)).FirstOrDefault();
    }

    // ---- radio ----
    public List<Station> LoadStations()
    {
        More();
        return Query("SELECT Id,Name,Url,Art,Tags,Country FROM stations ORDER BY Name COLLATE NOCASE",
            r => new Station { Id = L(r, 0), Name = S(r, 1), Url = S(r, 2), Art = S(r, 3), Tags = S(r, 4), Country = S(r, 5) });
    }

    public void SaveStation(Station s)
    {
        More();
        s.Id = Query("INSERT INTO stations(Name,Url,Art,Tags,Country) VALUES($p0,$p1,$p2,$p3,$p4) " +
                     "ON CONFLICT(Url) DO UPDATE SET Name=excluded.Name,Art=excluded.Art,Tags=excluded.Tags,Country=excluded.Country RETURNING Id",
            r => L(r, 0), s.Name, s.Url, s.Art, s.Tags, s.Country)[0];
    }

    public void DeleteStation(long id) { More(); Exec("DELETE FROM stations WHERE Id=$p0", id); }

    // ---- loudness ----
    /// <summary>(track gain, album gain) in decibels as stored for a song; NaN where not known.</summary>
    public (double Track, double Album, bool Measured)? Loudness(long trackId, long modified)
    {
        More();
        var rows = Query("SELECT TrackGain,AlbumGain,Measured FROM loudness WHERE TrackId=$p0 AND Modified=$p1",
            r => (r.IsDBNull(0) ? double.NaN : r.GetDouble(0), r.IsDBNull(1) ? double.NaN : r.GetDouble(1), L(r, 2) != 0), trackId, modified);
        return rows.Count > 0 ? rows[0] : null;
    }

    public void SaveLoudness(long trackId, long modified, double track, double album, bool measured)
    {
        More();
        Exec("INSERT INTO loudness(TrackId,Modified,TrackGain,AlbumGain,Measured) VALUES($p0,$p1,$p2,$p3,$p4) " +
             "ON CONFLICT(TrackId) DO UPDATE SET Modified=excluded.Modified,TrackGain=excluded.TrackGain,AlbumGain=excluded.AlbumGain,Measured=excluded.Measured",
            trackId, modified, double.IsNaN(track) ? null : track, double.IsNaN(album) ? null : album, measured ? 1 : 0);
    }

    // ---- fingerprints ----
    public (int Seconds, byte[] Print)? Fingerprint(long trackId, long modified)
    {
        More();
        var rows = Query("SELECT Seconds,Print FROM fingerprints WHERE TrackId=$p0 AND Modified=$p1", r => ((int)L(r, 0), (byte[])r["Print"]), trackId, modified);
        return rows.Count > 0 ? rows[0] : null;
    }

    public void SaveFingerprint(long trackId, long modified, int seconds, byte[] print)
    {
        More();
        Exec("INSERT INTO fingerprints(TrackId,Modified,Seconds,Print) VALUES($p0,$p1,$p2,$p3) " +
             "ON CONFLICT(TrackId) DO UPDATE SET Modified=excluded.Modified,Seconds=excluded.Seconds,Print=excluded.Print", trackId, modified, seconds, print);
    }

    // ---- listens waiting to be sent to a scrobbling service ----
    public record Pending(long Id, string Service, long At, string Artist, string Title, string Album, long Ms);

    public void QueueScrobble(string service, long at, string artist, string title, string album, long ms)
    {
        More();
        Exec("INSERT INTO scrobbles(Service,At,Artist,Title,Album,Ms) VALUES($p0,$p1,$p2,$p3,$p4,$p5)", service, at, artist, title, album, ms);
    }

    public List<Pending> PendingScrobbles(string service)
    {
        More();
        return Query("SELECT Id,Service,At,Artist,Title,Album,Ms FROM scrobbles WHERE Service=$p0 ORDER BY At LIMIT 50",
            r => new Pending(L(r, 0), S(r, 1), L(r, 2), S(r, 3), S(r, 4), S(r, 5), L(r, 6)), service);
    }

    public void DropScrobbles(IEnumerable<long> ids)
    {
        More();
        Batch(() => { foreach (var id in ids) Exec("DELETE FROM scrobbles WHERE Id=$p0", id); });
    }

    public void DropScrobbles(string service) { More(); Exec("DELETE FROM scrobbles WHERE Service=$p0", service); }

    /// <summary>Moves a song's place in every playlist to another song (used when a duplicate is removed).</summary>
    public void RepointPlaylists(long from, long to) => Exec("UPDATE playlist_tracks SET TrackId=$p1 WHERE TrackId=$p0", from, to);

    /// <summary>Points a song at a file that moved, keeping its ratings, counts and playlists.</summary>
    public void Relink(long id, string path, long modified) =>
        Exec("UPDATE tracks SET SourceId=$p0,Location=$p0,FileModified=$p1 WHERE Id=$p2", path, modified, id);
}
