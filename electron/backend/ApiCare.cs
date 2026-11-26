using Halcyon.Core;
using Halcyon.Services;

namespace Phoenix.Backend;

/// <summary>
/// Library care and metadata, as on Windows: the duplicate and missing-file finder, the metadata sources with their
/// weights and keys, "find album info" from any source, filling in albums that lack a cover, year or genre, and
/// identifying a song by its sound (AcoustID).
/// </summary>
public static partial class Api
{
    static List<LibraryCare.DupGroup> _dups = new();
    static List<Track> _missing = new();
    static Dictionary<Track, string> _located = new();
    static CancellationTokenSource? _careStop, _fillStop;

    static object DupShape(LibraryCare.DupGroup g, int i) => new
    {
        index = i, why = g.Why, best = g.Best.Id,
        tracks = g.Tracks.Select(t => new { id = t.Id, title = t.Title, artist = t.Artist, album = t.Album, path = t.SourceId, describe = LibraryCare.Describe(t) })
    };

    static object DupsState() => new { groups = _dups.Select(DupShape).ToList(), canListen = LibraryCare.CanFingerprint };

    static object MissingState() => new
    {
        tracks = _missing.Select(t => new { id = t.Id, title = t.Title, artist = t.Artist, album = t.Album, path = t.SourceId, found = _located.GetValueOrDefault(t, "") }).ToList(),
        found = _located.Count
    };

    static void RegisterCare()
    {
        Rpc.On("care.byName", _ => { _dups = LibraryCare.ByName(); return DupsState(); });
        Rpc.On("care.state", _ => DupsState());
        // listens to each group to tell real copies from different versions
        Rpc.On("care.confirm", async _ =>
        {
            if (!LibraryCare.CanFingerprint) throw new InvalidOperationException("This needs Chromaprint's fpcalc (settings → metadata).");
            var n = 0;
            foreach (var g in _dups) { Rpc.Emit("care.progress", $"listening… {++n} of {_dups.Count}"); await LibraryCare.ConfirmAsync(g); }
            return DupsState();
        });
        Rpc.On("care.bySound", async _ =>
        {
            if (!LibraryCare.CanFingerprint) throw new InvalidOperationException("This needs Chromaprint's fpcalc (settings → metadata).");
            _careStop = new CancellationTokenSource();
            try { _dups = await LibraryCare.BySoundAsync((done, all) => Rpc.Emit("care.progress", $"listening… {done} of {all}"), _careStop.Token); }
            catch (OperationCanceledException) { }
            finally { _careStop = null; }
            return DupsState();
        });
        Rpc.On("care.stop", _ => { _careStop?.Cancel(); });
        // keeps one copy and takes the others out of the library; their files are handed back (the window may move them to the bin)
        Rpc.On("care.resolve", p =>
        {
            var i = p.Int("group");
            if (i < 0 || i >= _dups.Count) throw new InvalidOperationException("That group is gone.");
            var g = _dups[i];
            var keep = g.Tracks.FirstOrDefault(t => t.Id == p.Long("keep")) ?? g.Best;
            var files = LibraryCare.Resolve(g, keep);
            _dups.RemoveAt(i);
            return new { files, state = DupsState() };
        });
        Rpc.On("care.resolveAll", _ =>
        {
            var files = new List<string>();
            foreach (var g in _dups) files.AddRange(LibraryCare.Resolve(g, g.Best));
            var n = _dups.Count;
            _dups = new();
            return new { files, groups = n, state = DupsState() };
        });
        Rpc.On("care.dismiss", p => { var i = p.Int("group"); if (i >= 0 && i < _dups.Count) _dups.RemoveAt(i); return DupsState(); });

        Rpc.On("care.missing", _ => { _missing = LibraryCare.Missing(); _located = new(); return MissingState(); });
        Rpc.On("care.locate", async _ => { _located = await Task.Run(() => LibraryCare.Locate(_missing)); return MissingState(); });
        Rpc.On("care.relinkFound", _ =>
        {
            var done = _located.Count(kv => Lib.Relink(kv.Key, kv.Value));
            Lib.NotifyChanged();
            _missing = LibraryCare.Missing();
            _located = new();
            return new { done, state = MissingState() };
        });
        Rpc.On("care.relink", p =>
        {
            if (_missing.FirstOrDefault(t => t.Id == p.Long("id")) is not { } t) return MissingState();
            if (!Lib.Relink(t, p.Str("path"))) throw new InvalidOperationException("That file is already in the library, or isn't there.");
            Lib.NotifyChanged();
            _missing.Remove(t);
            _located.Remove(t);
            return MissingState();
        });
        Rpc.On("care.removeMissing", p =>
        {
            var ids = p.Longs("ids");
            var gone = _missing.Where(t => ids.Count == 0 || ids.Contains(t.Id)).ToList();
            Lib.RemoveTracks(gone);
            _missing = _missing.Except(gone).ToList();
            foreach (var t in gone) _located.Remove(t);
            return MissingState();
        });
    }

    // ---------- metadata sources ----------

    static object SourceShape(IMetaSource s) => new
    {
        id = s.Id, name = s.Name, gives = s.Gives, keyHelp = s.KeyHelp, ready = s.Ready,
        enabled = Metadata.Setting(s.Id).Enabled, weight = Metadata.Setting(s.Id).Weight,
        hasKey = s.KeyHelp == null || (s.Id == "discogs" ? S.DiscogsToken != "" : Metadata.Key(s.Id) != "")
    };

    static object MetaState() => new
    {
        sources = Metadata.Sources.Select(SourceShape).ToList(),
        order = Metadata.Ranked().Select(s => s.Name).ToList(),
        writeTags = S.WriteTagsToFiles,
        hasAcoustKey = Metadata.Key("acoustid") != "", fpcalc = S.FpcalcPath, fpcalcFound = Metadata.FpcalcPath(), canIdentify = Metadata.CanIdentify
    };

    static readonly Dictionary<string, List<MetaHit>> Hits = new();
    static readonly Dictionary<string, DiscogsRelease> Found = new();

    static void RegisterMetadata()
    {
        Rpc.On("meta.state", _ => MetaState());
        Rpc.On("meta.setSource", p =>
        {
            var id = p.Str("id");
            if (Metadata.Find(id) == null) throw new InvalidOperationException("there is no such source");
            var set = Metadata.Setting(id);
            if (p.Has("enabled")) set.Enabled = p.Bool("enabled");
            if (p.Has("weight")) set.Weight = Math.Clamp(p.Int("weight"), 0, 10);
            if (p.Has("key"))
            {
                var key = p.Str("key").Trim();
                if (id == "discogs") S.DiscogsToken = key; else S.Keys[id] = key;
            }
            Save();
            return MetaState();
        });
        Rpc.On("meta.acoustid", p =>
        {
            if (p.Has("key")) S.Keys["acoustid"] = p.Str("key").Trim();
            if (p.Has("fpcalc")) S.FpcalcPath = p.Str("fpcalc").Trim().Trim('"');
            Save();
            return MetaState();
        });

        // ---- find album info: search a source, look at a release, apply it ----
        Rpc.On("meta.album", p => AlbumByKey(p.StrOrNull("key")) is { } a ? new
        {
            key = a.Key, artist = a.Artist == "Unknown Artist" ? "" : a.Artist, title = a.Title == "Unknown Album" ? "" : a.Title, source = a.Source,
            tracks = a.Tracks.Select(t => t.Title), tied = Metadata.SourceOf(a), art = a.ArtPath,
            sources = Metadata.Sources.Select(SourceShape).ToList()
        } : null);
        Rpc.On("meta.search", async p =>
        {
            var source = Metadata.Find(p.Str("source")) ?? throw new InvalidOperationException("Choose a source.");
            if (!source.Ready) throw new InvalidOperationException($"{source.Name} needs its key first (settings → metadata).");
            var album = AlbumByKey(p.StrOrNull("key"));
            var hits = await source.SearchAsync(album, p.Str("artist").Trim(), p.Str("title").Trim());
            Hits[source.Id] = hits;
            return hits.Select((h, i) => new { index = i, title = h.Title, details = h.Details, thumb = h.Thumb, fit = Metadata.Fit(h, p.Str("artist"), p.Str("title")) }).ToList();
        });
        Rpc.On("meta.release", async p =>
        {
            var source = Metadata.Find(p.Str("source")) ?? throw new InvalidOperationException("Choose a source.");
            if (!Hits.TryGetValue(source.Id, out var hits) || p.Int("index") < 0 || p.Int("index") >= hits.Count) throw new InvalidOperationException("Search again.");
            var rel = await source.GetAsync(hits[p.Int("index")]);
            var id = Guid.NewGuid().ToString("N")[..8];
            Found[id] = rel;
            return new
            {
                id, title = rel.Title, artist = rel.Artist, cover = rel.CoverUrl,
                meta = Fmt.Join(rel.Year > 0 ? rel.Year.ToString() : "", rel.Genre, rel.Style), tracks = rel.Tracks.Select(t => t.Title)
            };
        });
        Rpc.On("meta.apply", async p =>
        {
            if (AlbumByKey(p.StrOrNull("key")) is not { } album || !Found.TryGetValue(p.Str("id"), out var rel)) throw new InvalidOperationException("Pick a release first.");
            var source = p.Str("source");
            if (!p.Bool("tie") && Metadata.SourceOf(album) == source) Metadata.SetSource(album, Metadata.Auto);
            var failed = await Lib.ApplyReleaseAsync(album, rel, p.Bool("albumInfo"), p.Bool("titles"), p.Bool("cover"));
            // the album's key may have changed with its name: tie the album as it is now
            if (p.Bool("tie") && Lib.Albums.FirstOrDefault(a => a.Tracks.Any(t => album.Tracks.Contains(t))) is { } now) Metadata.SetSource(now, source);
            return failed;
        });
        Rpc.On("meta.tie", p => { if (AlbumByKey(p.StrOrNull("key")) is { } a) Metadata.SetSource(a, p.Str("source")); });

        // ---- fill in what albums lack, asking the sources in order of weight ----
        Rpc.On("meta.fillCount", _ => Lib.Albums.Count(a => a.Title != "Unknown Album" && (a.ArtPath == "" || a.Year == 0 || a.Genre == "") && Metadata.For(a).Count > 0));
        Rpc.On("meta.fill", async _ =>
        {
            if (Metadata.Ranked().Count == 0) throw new InvalidOperationException("Switch on at least one source first (a source with a key needs its key).");
            var albums = Lib.Albums.Where(a => a.Title != "Unknown Album" && (a.ArtPath == "" || a.Year == 0 || a.Genre == "") && Metadata.For(a).Count > 0).ToList();
            _fillStop = new CancellationTokenSource();
            int done = 0, matched = 0;
            try
            {
                foreach (var album in albums)
                {
                    if (_fillStop.IsCancellationRequested) break;
                    Rpc.Emit("meta.progress", $"matching {++done} of {albums.Count}: {album.Artist} – {album.Title}");
                    try
                    {
                        var found = await Metadata.FillAsync(album, _fillStop.Token);
                        if (!found.Any) continue;
                        // only the gaps are filled: the album keeps its own title and artist
                        var fill = new DiscogsRelease(0, "", "", found.Year, found.Genre, "", found.CoverUrl, new());
                        await Lib.ApplyReleaseAsync(album, fill, albumInfo: found.Year > 0 || found.Genre != "", titles: false, cover: found.Cover != null, coverBytes: found.Cover);
                        matched++;
                    }
                    catch { /* skip this album */ }
                }
                return $"filled in {matched} of {albums.Count} albums" + (_fillStop.IsCancellationRequested ? " (stopped)" : "");
            }
            finally { _fillStop = null; }
        });
        Rpc.On("meta.stop", _ => { _fillStop?.Cancel(); });

        // ---- identify a song by its sound ----
        Rpc.On("meta.identify", async p =>
        {
            if (!Lib.ById.TryGetValue(p.Long("id"), out var t) || !t.IsLocal) throw new InvalidOperationException("Only songs on this computer can be listened to.");
            if (!Metadata.CanIdentify) throw new InvalidOperationException("This needs an AcoustID key and Chromaprint's fpcalc (settings → metadata).");
            var found = await Metadata.IdentifyAsync(t.SourceId);
            return found == null ? null : new { title = found.Title, artist = found.Artist, album = found.Album, score = found.Score };
        });
    }
}
