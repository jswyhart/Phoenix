using System.Text.Json;
using Halcyon.Core;

namespace Phoenix.Backend;

/// <summary>
/// The Windows app's editing tools: the tag editor (many songs at once), your own tags, Smart DJ, and auto playlists
/// made by rules.
/// </summary>
public static partial class Api
{
    static object TagRowShape(Track t) => new
    {
        id = t.Id, title = t.Title, artist = t.Artist, albumArtist = t.AlbumArtist, album = t.Album, genre = t.Genre,
        year = t.Year, trackNo = t.TrackNo, discNo = t.DiscNo, local = t.IsLocal, file = t.IsLocal ? Path.GetFileName(t.SourceId) : t.Source,
        name = t.IsLocal ? Path.GetFileNameWithoutExtension(t.SourceId) : ""
    };

    static SmartPlaylist SmartFrom(JsonElement p) => new()
    {
        Id = p.Str("id") is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N"),
        Name = p.Str("name").Trim(), Genre = p.Str("genre").Trim(), Artist = p.Str("artist").Trim(),
        YearFrom = Math.Max(0, p.Int("yearFrom")), YearTo = Math.Max(0, p.Int("yearTo")),
        Rating = Math.Clamp(p.Int("rating"), 0, SmartPlaylist.Ratings.Length - 1),
        NotPlayedDays = Math.Max(0, p.Int("notPlayedDays")), AddedDays = Math.Max(0, p.Int("addedDays")),
        MinPlays = Math.Max(0, p.Int("minPlays")), MaxPlays = p.Has("maxPlays") ? Math.Max(-1, p.Int("maxPlays", -1)) : -1,
        Sort = Math.Clamp(p.Int("sort"), 0, SmartPlaylist.Sorts.Length - 1), Limit = Math.Max(0, p.Int("limit"))
    };

    static object SmartShape(SmartPlaylist p) => new
    {
        id = p.Id, name = p.Name, genre = p.Genre, artist = p.Artist, yearFrom = p.YearFrom, yearTo = p.YearTo, rating = p.Rating,
        notPlayedDays = p.NotPlayedDays, addedDays = p.AddedDays, minPlays = p.MinPlays, maxPlays = p.MaxPlays, sort = p.Sort, limit = p.Limit,
        rules = Library.Describe(p)
    };

    static void RegisterTools()
    {
        // ---- the tag editor ----
        Rpc.On("tags.rows", p => Tracks(p).Select(TagRowShape).ToList());
        Rpc.On("tags.saveRows", p =>
        {
            var changed = new List<Track>();
            foreach (var r in p.Obj("rows").EnumerateArray())
            {
                if (!Lib.ById.TryGetValue(r.Long("id"), out var t)) continue;
                var title = r.Str("title").Trim();
                t.Title = title == "" ? t.Title : title;
                t.Artist = r.Str("artist").Trim() is { Length: > 0 } a ? a : "Unknown Artist";
                t.AlbumArtist = r.Str("albumArtist").Trim() is { Length: > 0 } aa ? aa : t.Artist;
                t.Album = r.Str("album").Trim() is { Length: > 0 } al ? al : "Unknown Album";
                t.Genre = r.Str("genre").Trim();
                t.Year = Math.Max(0, r.Int("year")); t.TrackNo = Math.Max(0, r.Int("trackNo")); t.DiscNo = Math.Max(0, r.Int("discNo"));
                changed.Add(t);
            }
            return changed.Count == 0 ? 0 : Lib.SaveTracks(changed);
        });

        // ---- your own tags ----
        Rpc.On("owntags.get", p => new
        {
            common = Lib.CommonTags(p.Str("kind", Library.SongTag), p.Longs("ids")),
            all = Lib.AllTags().Take(60).Select(x => new { tag = x.Tag, count = x.Count })
        });
        Rpc.On("owntags.apply", p =>
        {
            var now = Library.ParseTags(p.Str("text"));
            var before = p.Strs("before");
            Lib.ApplyTags(p.Str("kind", Library.SongTag), p.Longs("ids"), now, before.Where(t => !now.Contains(t, StringComparer.OrdinalIgnoreCase)));
        });

        // ---- Smart DJ: an endless mix like a song (a liked one of those given, if there is one) ----
        Rpc.On("dj.start", p =>
        {
            var list = Tracks(p);
            var rnd = new Random();
            var liked = list.Where(t => t.Rating > 0).ToList();
            var ok = liked.Count > 0 ? liked : list.Where(t => t.Rating >= 0).ToList();
            if (ok.Count == 0) return "";
            var seed = ok[rnd.Next(ok.Count)];
            P.PlaySmartDj(seed);
            return P.Queue.Count > 1 ? $"smart dj: music like “{seed.Title}” by {seed.Artist}" : "smart dj found nothing else like that in your collection";
        });

        // ---- auto playlists made by rules ----
        Rpc.On("smart.choices", _ => new { ratings = SmartPlaylist.Ratings, sorts = SmartPlaylist.Sorts });
        Rpc.On("smart.get", p => S.SmartPlaylists.FirstOrDefault(x => x.Id == p.Str("id")) is { } sp ? SmartShape(sp) : null);
        Rpc.On("smart.count", p =>
        {
            var probe = SmartFrom(p);
            var shown = Lib.Evaluate(probe).Count;
            probe.Limit = 0;
            var all = Lib.Evaluate(probe).Count;
            return all == shown ? (all == 1 ? "1 song matches" : $"{all} songs match") : $"{all} songs match; the playlist keeps {shown}";
        });
        Rpc.On("smart.save", p =>
        {
            var rule = SmartFrom(p);
            if (rule.Name == "") throw new InvalidOperationException("Give the playlist a name.");
            var i = S.SmartPlaylists.FindIndex(x => x.Id == rule.Id);
            if (i >= 0) S.SmartPlaylists[i] = rule; else S.SmartPlaylists.Add(rule);
            Save();
            Lib.NotifyChanged();
            return rule.Id;
        });
        Rpc.On("smart.delete", p =>
        {
            S.SmartPlaylists.RemoveAll(x => x.Id == p.Str("id"));
            Save();
            Lib.NotifyChanged();
        });
    }
}
