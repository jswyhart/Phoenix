using System.IO;

namespace Halcyon.Core;

/// <summary>Radio stations, and removing or re-pointing songs (used by the duplicate and missing-file finder).</summary>
public partial class Library
{
    List<Station>? _stations;
    public List<Station> Stations => _stations ??= Db.LoadStations();

    public void SaveStation(Station s)
    {
        Db.SaveStation(s);
        _stations = null;
        Changed?.Invoke();
    }

    public void RemoveStation(Station s)
    {
        Db.DeleteStation(s.Id);
        _stations = null;
        var pin = Pins.FirstOrDefault(p => p.Kind == "radio" && p.Key == s.Url);
        if (pin != null) { Pins.Remove(pin); Db.RemovePin(pin); }
        Changed?.Invoke();
    }

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
