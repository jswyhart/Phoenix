using System.Text.RegularExpressions;

namespace Halcyon.Core;

public partial class Database
{
    void CreateTagTable()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS tags(Kind TEXT NOT NULL, ItemId INT NOT NULL, Tag TEXT NOT NULL, UNIQUE(Kind,ItemId,Tag));
            CREATE INDEX IF NOT EXISTS tags_tag ON tags(Tag);
            """);
        // tags of items that no longer exist (a removed server, a deleted file)
        Exec("""
            DELETE FROM tags WHERE (Kind='song' AND ItemId NOT IN (SELECT Id FROM tracks))
                OR (Kind='video' AND ItemId NOT IN (SELECT Id FROM videos))
                OR (Kind='book' AND ItemId NOT IN (SELECT Id FROM books))
            """);
    }

    public List<(string Kind, long Id, string Tag)> LoadTags() => Query("SELECT Kind,ItemId,Tag FROM tags", r => (S(r, 0), L(r, 1), S(r, 2)));

    public void AddTags(string kind, IEnumerable<long> ids, IEnumerable<string> tags)
    {
        var list = tags.ToList();
        Batch(() => { foreach (var id in ids) foreach (var tag in list) Exec("INSERT OR IGNORE INTO tags(Kind,ItemId,Tag) VALUES($p0,$p1,$p2)", kind, id, tag); });
    }

    public void RemoveTags(string kind, IEnumerable<long> ids, IEnumerable<string> tags)
    {
        var list = tags.ToList();
        Batch(() => { foreach (var id in ids) foreach (var tag in list) Exec("DELETE FROM tags WHERE Kind=$p0 AND ItemId=$p1 AND Tag=$p2", kind, id, tag); });
    }
}

public record SearchResults(List<Album> Albums, List<Track> Songs, List<VideoItem> Videos, List<Book> Books, List<(string Tag, int Count)> Tags);

/// <summary>
/// Your own tags on songs, videos and books ("road trip", "workout", "to read"). They live in the library database, so
/// they survive rescans and server syncs, and search finds them: plain words match tags like any other text, and
/// #tag (or #"two words") matches tags specifically.
/// </summary>
public partial class Library
{
    public const string SongTag = "song", VideoTag = "video", BookTag = "book";
    Dictionary<string, HashSet<string>> _tags = new();

    /// <summary>Raised when tags are added or removed.</summary>
    public event Action? TagsChanged;

    static string TagKey(string kind, long id) => kind + ":" + id;

    void LoadTags()
    {
        _tags = new();
        foreach (var (kind, id, tag) in Db.LoadTags())
        {
            var key = TagKey(kind, id);
            if (!_tags.TryGetValue(key, out var set)) _tags[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(tag);
        }
    }

    public IReadOnlyCollection<string> TagsOf(string kind, long id) => _tags.TryGetValue(TagKey(kind, id), out var s) ? s : Array.Empty<string>();

    public string TagText(string kind, long id) =>
        _tags.TryGetValue(TagKey(kind, id), out var s) ? string.Join(", ", s.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)) : "";

    /// <summary>Lower-case, one space between words, no leading #, no commas or quotes, at most 40 characters.</summary>
    public static string NormalizeTag(string raw)
    {
        var t = Regex.Replace(raw.Replace(",", " ").Replace("\"", " ").Trim().TrimStart('#'), @"\s+", " ").Trim().ToLowerInvariant();
        return t.Length > 40 ? t[..40].Trim() : t;
    }

    public static List<string> ParseTags(string text) =>
        text.Split(new[] { ',', ';', '\n' }).Select(NormalizeTag).Where(t => t != "").Distinct().ToList();

    /// <summary>Tags carried by every one of the items.</summary>
    public List<string> CommonTags(string kind, IEnumerable<long> ids)
    {
        HashSet<string>? common = null;
        foreach (var id in ids)
        {
            var tags = TagsOf(kind, id);
            if (common == null) common = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase); else common.IntersectWith(tags);
        }
        return (common ?? new()).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Adds and removes tags on a set of items in one step.</summary>
    public void ApplyTags(string kind, IEnumerable<long> ids, IEnumerable<string> add, IEnumerable<string> remove)
    {
        var idList = ids.Distinct().ToList();
        var addList = add.Select(NormalizeTag).Where(t => t != "").Distinct().ToList();
        var removeList = remove.Select(NormalizeTag).Where(t => t != "").Distinct().ToList();
        if (idList.Count == 0 || addList.Count + removeList.Count == 0) return;
        Db.RemoveTags(kind, idList, removeList);
        Db.AddTags(kind, idList, addList);
        foreach (var id in idList)
        {
            var key = TagKey(kind, id);
            if (!_tags.TryGetValue(key, out var set)) _tags[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in removeList) set.Remove(t);
            foreach (var t in addList) set.Add(t);
            if (set.Count == 0) _tags.Remove(key);
        }
        var wanted = idList.ToHashSet();
        if (kind == SongTag) foreach (var id in idList) { if (ById.TryGetValue(id, out var t)) t.Raise(); }
        else if (kind == VideoTag) foreach (var v in Videos.Where(v => wanted.Contains(v.Id))) v.Raise();
        else foreach (var b in Books.Where(b => wanted.Contains(b.Id))) b.Raise();
        TagsChanged?.Invoke();
    }

    /// <summary>Every tag in use with how many items carry it, most used first.</summary>
    public List<(string Tag, int Count)> AllTags() =>
        _tags.Values.SelectMany(s => s).GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Tag: g.Key.ToLowerInvariant(), Count: g.Count())).OrderByDescending(t => t.Count).ThenBy(t => t.Tag, StringComparer.OrdinalIgnoreCase).ToList();

    // ---------- search ----------

    static readonly Regex TermPattern = new("#\"([^\"]+)\"|#(\\S+)|\"([^\"]+)\"|(\\S+)");

    public record Term(string Text, bool IsTag);

    /// <summary>Splits a search into terms: words, "quoted phrases", #tags and #"quoted tags".</summary>
    public static List<Term> ParseQuery(string query)
    {
        var terms = new List<Term>();
        foreach (Match m in TermPattern.Matches(query))
        {
            if (m.Groups[1].Success) terms.Add(new Term(NormalizeTag(m.Groups[1].Value), true));
            else if (m.Groups[2].Success) terms.Add(new Term(NormalizeTag(m.Groups[2].Value), true));
            else if (m.Groups[3].Success) terms.Add(new Term(m.Groups[3].Value, false));
            else terms.Add(new Term(m.Groups[4].Value, false));
        }
        return terms.Where(t => t.Text != "").ToList();
    }

    public SearchResults Search(string query)
    {
        var empty = new SearchResults(new(), new(), new(), new(), new());
        if (query.Trim() == "#") return empty with { Tags = AllTags() };   // "#" on its own lists your tags
        var terms = ParseQuery(query);
        if (terms.Count == 0) return empty;

        bool Match(string[] fields, IEnumerable<string> tags)
        {
            var tagList = tags as IReadOnlyCollection<string> ?? tags.ToList();
            return terms.All(term => term.IsTag
                ? tagList.Any(t => t.StartsWith(term.Text, StringComparison.OrdinalIgnoreCase))
                : fields.Any(f => f.Contains(term.Text, StringComparison.OrdinalIgnoreCase)) || tagList.Any(t => t.Contains(term.Text, StringComparison.OrdinalIgnoreCase)));
        }

        var anyTags = _tags.Count > 0;
        var songs = Tracks.Where(t => Match(new[] { t.Title, t.Artist, t.Album, t.Genre }, anyTags ? TagsOf(SongTag, t.Id) : Array.Empty<string>()))
            .OrderBy(t => t.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Album, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.TrackNo).Take(1000).ToList();
        // an album matches on its own text, or on tags carried by any of its songs
        var albums = Albums.Where(a => Match(new[] { a.Title, a.Artist }, anyTags ? a.Tracks.SelectMany(t => TagsOf(SongTag, t.Id)).Distinct() : Array.Empty<string>())).Take(200).ToList();
        var videos = Videos.Where(v => Match(new[] { v.Title, v.Series, v.Collection }, anyTags ? TagsOf(VideoTag, v.Id) : Array.Empty<string>()))
            .OrderBy(v => v.DisplayTitle, StringComparer.OrdinalIgnoreCase).Take(300).ToList();
        var books = Books.Where(b => Match(new[] { b.Title, b.Author, b.Series }, anyTags ? TagsOf(BookTag, b.Id) : Array.Empty<string>()))
            .OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase).Take(200).ToList();

        // tags worth offering for the next click: those the search words touch, or that start like the #tag terms
        var words = terms.Where(t => !t.IsTag).Select(t => t.Text).ToList();
        var prefixes = terms.Where(t => t.IsTag).Select(t => t.Text).ToList();
        var tags = AllTags().Where(t => words.Any(w => t.Tag.Contains(w, StringComparison.OrdinalIgnoreCase)) || prefixes.Any(p => t.Tag.StartsWith(p, StringComparison.OrdinalIgnoreCase))).Take(12).ToList();
        return new SearchResults(albums, songs, videos, books, tags);
    }
}

/// <summary>The #tag form of a tag for the search box: quoted when it has spaces.</summary>
public static class TagText
{
    public static string Query(string tag) => tag.Contains(' ') ? $"#\"{tag}\"" : "#" + tag;
}

/// <summary>Sorting for song lists: click a column to sort by it, again to reverse, a third time to go back to the original order.</summary>
public class SongSort
{
    public static readonly (string Key, string Label)[] Keys =
    {
        ("no", "track number"), ("title", "title"), ("artist", "artist"), ("album", "album"), ("tags", "tags"), ("time", "length"), ("rating", "rating"),
        ("genre", "genre"), ("year", "year"), ("added", "date added"), ("plays", "play count"), ("played", "last played")
    };

    public string Key { get; private set; } = "";
    public bool Descending { get; private set; }
    public bool Active => Key != "";

    public void Click(string key)
    {
        if (Key != key) { Key = key; Descending = false; }
        else if (!Descending) Descending = true;
        else Clear();
    }

    public void Set(string key, bool descending) { Key = key; Descending = descending; }
    public void Clear() { Key = ""; Descending = false; }

    static readonly StringComparer Text = StringComparer.OrdinalIgnoreCase;

    static int By(Track a, Track b, string key) => key switch
    {
        "no" => Cmp(a.DiscNo, b.DiscNo, a.TrackNo, b.TrackNo),
        "title" => Text.Compare(a.Title, b.Title),
        "artist" => Chain(Text.Compare(a.Artist, b.Artist), Text.Compare(a.Album, b.Album), Cmp(a.DiscNo, b.DiscNo, a.TrackNo, b.TrackNo)),
        "album" => Chain(Text.Compare(a.Album, b.Album), Cmp(a.DiscNo, b.DiscNo, a.TrackNo, b.TrackNo)),
        "tags" => TagCmp(a, b),
        "time" => a.DurationMs.CompareTo(b.DurationMs),
        "rating" => a.Rating.CompareTo(b.Rating),
        "genre" => Text.Compare(a.Genre, b.Genre),
        "year" => a.Year.CompareTo(b.Year),
        "added" => a.DateAdded.CompareTo(b.DateAdded),
        "plays" => a.PlayCount.CompareTo(b.PlayCount),
        "played" => a.LastPlayed.CompareTo(b.LastPlayed),
        _ => 0
    };

    /// <summary>By tag text; songs with no tags come after the tagged ones.</summary>
    static int TagCmp(Track a, Track b)
    {
        string x = a.TagText, y = b.TagText;
        if ((x == "") != (y == "")) return x == "" ? 1 : -1;
        return Text.Compare(x, y);
    }

    static int Cmp(int a1, int b1, int a2, int b2) => a1 != b1 ? a1.CompareTo(b1) : a2.CompareTo(b2);
    static int Chain(params int[] results) => results.FirstOrDefault(r => r != 0);

    public List<Track> Apply(IEnumerable<Track> songs)
    {
        var list = songs.ToList();
        if (!Active) return list;
        var key = Key;
        var sign = Descending ? -1 : 1;
        // LINQ's ordering is stable, so songs that compare equal keep the order they came in
        return list.OrderBy(t => t, Comparer<Track>.Create((a, b) => sign * By(a, b, key))).ToList();
    }
}
