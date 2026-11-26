using Microsoft.Data.Sqlite;

// From the Windows app's Core\Database.More.cs and Library.More.cs: measured loudness, sound fingerprints, and
// removing or re-pointing songs (the duplicate and missing-file finder).

namespace Halcyon.Core
{
    public partial class Database
    {
        bool _careReady;

        void Care()
        {
            if (_careReady) return;
            _careReady = true;
            Exec("""
                CREATE TABLE IF NOT EXISTS loudness(TrackId INTEGER PRIMARY KEY, Modified INT, TrackGain REAL, AlbumGain REAL, Measured INT DEFAULT 0);
                CREATE TABLE IF NOT EXISTS fingerprints(TrackId INTEGER PRIMARY KEY, Modified INT, Seconds INT, Print BLOB);
                """);
        }

        /// <summary>(track gain, album gain) in decibels as stored for a song; NaN where not known.</summary>
        public (double Track, double Album, bool Measured)? Loudness(long trackId, long modified)
        {
            Care();
            var rows = Query("SELECT TrackGain,AlbumGain,Measured FROM loudness WHERE TrackId=$p0 AND Modified=$p1",
                r => (r.IsDBNull(0) ? double.NaN : r.GetDouble(0), r.IsDBNull(1) ? double.NaN : r.GetDouble(1), !r.IsDBNull(2) && r.GetInt64(2) != 0), trackId, modified);
            return rows.Count > 0 ? rows[0] : null;
        }

        public void SaveLoudness(long trackId, long modified, double track, double album, bool measured)
        {
            Care();
            Exec("INSERT INTO loudness(TrackId,Modified,TrackGain,AlbumGain,Measured) VALUES($p0,$p1,$p2,$p3,$p4) " +
                 "ON CONFLICT(TrackId) DO UPDATE SET Modified=excluded.Modified,TrackGain=excluded.TrackGain,AlbumGain=excluded.AlbumGain,Measured=excluded.Measured",
                trackId, modified, double.IsNaN(track) ? null : track, double.IsNaN(album) ? null : album, measured ? 1 : 0);
        }

        public (int Seconds, byte[] Print)? Fingerprint(long trackId, long modified)
        {
            Care();
            var rows = Query("SELECT Seconds,Print FROM fingerprints WHERE TrackId=$p0 AND Modified=$p1", r => ((int)r.GetInt64(0), (byte[])r["Print"]), trackId, modified);
            return rows.Count > 0 ? rows[0] : null;
        }

        public void SaveFingerprint(long trackId, long modified, int seconds, byte[] print)
        {
            Care();
            Exec("INSERT INTO fingerprints(TrackId,Modified,Seconds,Print) VALUES($p0,$p1,$p2,$p3) " +
                 "ON CONFLICT(TrackId) DO UPDATE SET Modified=excluded.Modified,Seconds=excluded.Seconds,Print=excluded.Print", trackId, modified, seconds, print);
        }

        // ---- names translated once and remembered ----
        public List<(string Source, string Result)> LoadTranslations(string lang)
        {
            Exec("CREATE TABLE IF NOT EXISTS translations(Lang TEXT NOT NULL, Source TEXT NOT NULL, Result TEXT, PRIMARY KEY(Lang,Source))");
            return Query("SELECT Source,Result FROM translations WHERE Lang=$p0", r => (r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)), lang);
        }

        public void SaveTranslation(string lang, string source, string result)
        {
            Exec("CREATE TABLE IF NOT EXISTS translations(Lang TEXT NOT NULL, Source TEXT NOT NULL, Result TEXT, PRIMARY KEY(Lang,Source))");
            Exec("INSERT INTO translations(Lang,Source,Result) VALUES($p0,$p1,$p2) ON CONFLICT(Lang,Source) DO UPDATE SET Result=excluded.Result", lang, source, result);
        }

        /// <summary>Moves a song's place in every playlist to another song (used when a duplicate is removed).</summary>
        public void RepointPlaylists(long from, long to) => Exec("UPDATE playlist_tracks SET TrackId=$p1 WHERE TrackId=$p0", from, to);

        /// <summary>Points a song at a file that moved, keeping its ratings, counts and playlists.</summary>
        public void Relink(long id, string path, long modified) =>
            Exec("UPDATE tracks SET SourceId=$p0,Location=$p0,FileModified=$p1 WHERE Id=$p2", path, modified, id);
    }

    public partial class Library
    {
        /// <summary>Takes songs out of the library (their files are not touched here).</summary>
        public void RemoveTracks(IEnumerable<Track> tracks)
        {
            var ids = tracks.Select(t => t.Id).ToHashSet();
            if (ids.Count == 0) return;
            Db.Batch(() => { foreach (var id in ids) Db.DeleteTrack(id); });
            Tracks = Tracks.Where(t => !ids.Contains(t.Id)).ToList();
            foreach (var p in Playlists) p.TrackIds.RemoveAll(ids.Contains);
            NotifyChanged();
        }

        /// <summary>
        /// One song takes over from another that is being removed: the better rating, the plays added together, and the
        /// other's places in playlists.
        /// </summary>
        public void Absorb(Track keep, Track gone)
        {
            keep.PlayCount += gone.PlayCount;
            keep.LastPlayed = Math.Max(keep.LastPlayed, gone.LastPlayed);
            keep.DateAdded = keep.DateAdded == 0 ? gone.DateAdded : gone.DateAdded == 0 ? keep.DateAdded : Math.Min(keep.DateAdded, gone.DateAdded);
            if (keep.Rating == 0 && gone.Rating != 0) { keep.Rating = gone.Rating; keep.RatedAt = gone.RatedAt; }
            Db.SaveStats(keep);
            Db.RepointPlaylists(gone.Id, keep.Id);
            foreach (var p in Playlists)
                for (var i = 0; i < p.TrackIds.Count; i++)
                    if (p.TrackIds[i] == gone.Id) p.TrackIds[i] = keep.Id;
        }

        /// <summary>Points a song at the place its file moved to, keeping everything known about it.</summary>
        public bool Relink(Track t, string path)
        {
            if (!File.Exists(path) || Tracks.Any(o => o.IsLocal && !ReferenceEquals(o, t) && string.Equals(o.SourceId, path, StringComparison.OrdinalIgnoreCase))) return false;
            t.SourceId = t.Location = path;
            t.FileModified = File.GetLastWriteTimeUtc(path).Ticks;
            Db.Relink(t.Id, path, t.FileModified);
            t.Raise();
            return true;
        }
    }
}

namespace Halcyon.Services
{
    /// <summary>How much a metadata source is trusted, and whether it is used at all.</summary>
    public class MetaSourceSetting
    {
        public string Id { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public int Weight { get; set; } = 5;   // 0-10; sources are tried from the highest weight down
    }
}
