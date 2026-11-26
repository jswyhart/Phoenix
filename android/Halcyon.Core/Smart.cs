namespace Halcyon.Core;

// Shared with the Windows app (Core\Smart.cs and the SmartPlaylist in Core\Models.cs).

/// <summary>A playlist made by rules (an auto playlist), kept in the settings.</summary>
public class SmartPlaylist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Genre { get; set; } = "";     // contains; several separated by commas
    public string Artist { get; set; } = "";    // contains; several separated by commas
    public int YearFrom { get; set; }
    public int YearTo { get; set; }
    public int Rating { get; set; }             // 0 any, 1 liked, 2 not disliked, 3 not rated yet
    public int NotPlayedDays { get; set; }      // only songs not played in this many days (0 = no rule)
    public int AddedDays { get; set; }          // only songs added in the last this many days (0 = no rule)
    public int MinPlays { get; set; }
    public int MaxPlays { get; set; } = -1;     // -1 = no upper limit
    public int Sort { get; set; }               // index into SmartPlaylist.Sorts
    public int Limit { get; set; } = 50;        // 0 = no limit

    public static readonly string[] Ratings = { "any rating", "liked", "not disliked", "not rated yet" };
    public static readonly string[] Sorts = { "random", "most played", "least played", "newest in collection", "longest since played", "artist and album" };
}


/// <summary>Smart DJ mixes and rule-based (auto) playlists.</summary>
public partial class Library
{
    /// <summary>
    /// Songs that go with the seed song, best matches first with some chance mixed in: the same artist, the same genre,
    /// the same era, songs you like. Disliked songs and the ones in <paramref name="exclude"/> are left out, and no
    /// single artist takes over the mix.
    /// </summary>
    public List<Track> SmartDj(Track seed, IEnumerable<Track> exclude, int count)
    {
        var skip = exclude.Select(t => t.Id).ToHashSet();
        var rnd = new Random();
        var seedGenres = Genres(seed.Genre);
        var seedArtist = seed.AlbumArtist != "" ? seed.AlbumArtist : seed.Artist;
        // artists that share an album or a genre-and-era with the seed are "near" it
        double Score(Track t)
        {
            var s = rnd.NextDouble() * 2.2;
            var artist = t.AlbumArtist != "" ? t.AlbumArtist : t.Artist;
            if (string.Equals(artist, seedArtist, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Artist, seed.Artist, StringComparison.OrdinalIgnoreCase)) s += 2.4;
            var shared = Genres(t.Genre).Count(seedGenres.Contains);
            if (shared > 0) s += 2.0 + 0.4 * Math.Min(2, shared - 1);
            if (seed.Year > 0 && t.Year > 0) s += Math.Abs(seed.Year - t.Year) switch { <= 3 => 1.0, <= 8 => 0.5, <= 15 => 0.1, _ => -0.4 };
            if (t.Rating > 0) s += 0.9;
            if (t.LastPlayed > 0 && Fmt.Now - t.LastPlayed < 3600 * 6) s -= 1.2;   // heard in the last few hours
            return s;
        }
        var ranked = Tracks.Where(t => t.Rating >= 0 && t.Id != seed.Id && !skip.Contains(t.Id))
            .Select(t => (t, score: Score(t))).Where(x => x.score > 2.0).OrderByDescending(x => x.score).ToList();
        var result = new List<Track>();
        var perArtist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cap = Math.Max(3, count / 6);
        foreach (var (t, _) in ranked)
        {
            var artist = t.AlbumArtist != "" ? t.AlbumArtist : t.Artist;
            perArtist.TryGetValue(artist, out var n);
            if (n >= cap) continue;
            perArtist[artist] = n + 1;
            result.Add(t);
            if (result.Count >= count) break;
        }
        // the best matches, but not in best-first order: a mix should wander
        return result.OrderBy(_ => rnd.Next()).ToList();
    }

    static HashSet<string> Genres(string genre) =>
        genre.Split(new[] { ',', ';', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    static bool ContainsAny(string value, string wanted)
    {
        var parts = wanted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 || parts.Any(p => value.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The songs an auto playlist holds right now.</summary>
    public List<Track> Evaluate(SmartPlaylist p)
    {
        var now = Fmt.Now;
        IEnumerable<Track> q = Tracks.Where(t =>
            ContainsAny(t.Genre, p.Genre)
            && (p.Artist.Trim() == "" || ContainsAny(t.Artist, p.Artist) || ContainsAny(t.AlbumArtist, p.Artist))
            && (p.YearFrom <= 0 || t.Year >= p.YearFrom)
            && (p.YearTo <= 0 || (t.Year > 0 && t.Year <= p.YearTo))
            && p.Rating switch { 1 => t.Rating > 0, 2 => t.Rating >= 0, 3 => t.Rating == 0, _ => true }
            && (p.NotPlayedDays <= 0 || t.LastPlayed == 0 || now - t.LastPlayed >= p.NotPlayedDays * 86400L)
            && (p.AddedDays <= 0 || now - t.DateAdded <= p.AddedDays * 86400L)
            && t.PlayCount >= p.MinPlays
            && (p.MaxPlays < 0 || t.PlayCount <= p.MaxPlays));
        // "random" keeps one order for the day, so the list does not reshuffle every time it is opened
        var rnd = new Random(HashCode.Combine(p.Id.GetHashCode(StringComparison.Ordinal), DateTime.Now.DayOfYear));
        q = p.Sort switch
        {
            1 => q.OrderByDescending(t => t.PlayCount).ThenByDescending(t => t.LastPlayed),
            2 => q.OrderBy(t => t.PlayCount).ThenBy(t => t.LastPlayed),
            3 => q.OrderByDescending(t => t.DateAdded).ThenBy(t => t.Album).ThenBy(t => t.DiscNo).ThenBy(t => t.TrackNo),
            4 => q.OrderBy(t => t.LastPlayed),
            5 => q.OrderBy(t => t.AlbumArtist, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Year).ThenBy(t => t.Album, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.DiscNo).ThenBy(t => t.TrackNo),
            _ => q.OrderBy(t => t.Id).ToList().OrderBy(_ => rnd.Next())
        };
        return (p.Limit > 0 ? q.Take(p.Limit) : q).ToList();
    }

    /// <summary>A one-line description of an auto playlist's rules.</summary>
    public static string Describe(SmartPlaylist p)
    {
        var parts = new List<string>();
        if (p.Genre.Trim() != "") parts.Add("genre " + p.Genre.Trim());
        if (p.Artist.Trim() != "") parts.Add("artist " + p.Artist.Trim());
        if (p.YearFrom > 0 && p.YearTo > 0) parts.Add($"{p.YearFrom}–{p.YearTo}");
        else if (p.YearFrom > 0) parts.Add($"from {p.YearFrom}");
        else if (p.YearTo > 0) parts.Add($"up to {p.YearTo}");
        if (p.Rating > 0) parts.Add(SmartPlaylist.Ratings[Math.Clamp(p.Rating, 0, 3)]);
        if (p.NotPlayedDays > 0) parts.Add($"not played in {p.NotPlayedDays} days");
        if (p.AddedDays > 0) parts.Add($"added in the last {p.AddedDays} days");
        if (p.MinPlays > 0) parts.Add($"played at least {p.MinPlays} times");
        if (p.MaxPlays >= 0) parts.Add(p.MaxPlays == 0 ? "never played" : $"played at most {p.MaxPlays} times");
        if (parts.Count == 0) parts.Add("all songs");
        parts.Add(SmartPlaylist.Sorts[Math.Clamp(p.Sort, 0, SmartPlaylist.Sorts.Length - 1)]);
        if (p.Limit > 0) parts.Add($"first {p.Limit}");
        return string.Join("  ·  ", parts);
    }
}
