namespace Halcyon.Core;

public record VideoGroup(string Name, string Art, int Count, string Key);

public class QuickItem
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string ArtPath { get; init; } = "";
    public Action Play { get; init; } = () => { };
    public Pin? Pin { get; init; }
}

public record PicFolder(string Name, string Path, int Count, List<PictureItem>? Items = null);
/// <summary>File is a local path or a full-size image URL; ArtPath is what the thumbnail loader reads.</summary>
public record PictureItem(string Name, string File, string ArtPath);

/// <summary>
/// The groupings every screen shows (the same ones the Windows views build), kept out of the UI so local files and
/// server libraries are presented alike and the logic can be tested without a device.
/// </summary>
public static class Catalog
{
    static readonly StringComparer NoCase = StringComparer.OrdinalIgnoreCase;
    static Library Lib => App.Lib;

    // ---------- music ----------

    public static List<string> Artists() => Lib.Albums.Select(a => a.Artist).Distinct(NoCase).OrderBy(a => a, NoCase).ToList();
    public static List<Album> AlbumsOf(string artist) => Lib.Albums.Where(a => string.Equals(a.Artist, artist, StringComparison.OrdinalIgnoreCase)).ToList();
    public static List<string> Genres() => Lib.Tracks.Select(t => t.Genre).Where(g => g != "").Distinct(NoCase).OrderBy(g => g, NoCase).ToList();
    public static List<Album> AlbumsInGenre(string genre) => Lib.Albums.Where(a => a.Tracks.Any(t => string.Equals(t.Genre, genre, StringComparison.OrdinalIgnoreCase))).ToList();
    public static List<Track> Songs() => Lib.Tracks.OrderBy(t => t.Title, NoCase).ThenBy(t => t.Artist, NoCase).ToList();
    public static Album? AlbumOf(Track t) => Lib.Albums.FirstOrDefault(a => a.Key == t.AlbumKey);

    public static readonly string[] AutoPlaylists = { "liked", "most played", "recently added", "recently played" };

    // ---------- videos ----------

    public static bool IsShow(VideoItem v) => v.Kind == "episode";
    public static bool IsMovie(VideoItem v) => v.Kind == "movie";
    public static bool IsOther(VideoItem v) => !IsShow(v) && !IsMovie(v);
    public static string FolderOf(VideoItem v) => v.Series != "" ? v.Series : v.Source == "local" ? "Videos" : v.Source;

    /// <summary>Only the tabs that have something in them, in display order.</summary>
    public static List<string> VideoTabs()
    {
        var all = Lib.Videos;
        var tabs = new List<string>();
        if (all.Any(IsMovie)) tabs.Add("movies");
        if (all.Any(IsShow)) tabs.Add("shows");
        if (all.Any(v => v.Collection != "")) tabs.Add("collections");
        if (all.Any(IsOther)) tabs.Add("folders");
        tabs.Add("all");
        return tabs;
    }

    public static string VideoCounts()
    {
        var all = Lib.Videos;
        return Fmt.Join(Fmt.Plural(all.Count(IsMovie), "movie"), Fmt.Plural(all.Where(IsShow).Select(v => v.Series).Distinct(NoCase).Count(), "show"),
            Fmt.Plural(all.Count(IsShow), "episode"), Fmt.Plural(all.Count(IsOther), "other video"));
    }

    public static List<VideoGroup> VideoGroups(string tab)
    {
        var all = Lib.Videos;
        var groups = tab switch
        {
            "shows" => all.Where(IsShow).GroupBy(v => v.Series, NoCase),
            "collections" => all.Where(v => v.Collection != "").GroupBy(v => v.Collection, NoCase),
            _ => all.Where(IsOther).GroupBy(FolderOf, NoCase)
        };
        return groups.OrderBy(g => g.Key, NoCase).Select(g => new VideoGroup(g.Key == "" ? "Unknown" : g.Key,
            (tab == "shows" ? g.Select(v => v.GroupArt).FirstOrDefault(a => a != "") : null) ?? g.Select(v => v.ArtPath).FirstOrDefault(a => a != "") ?? "",
            g.Count(), g.Key)).ToList();
    }

    public static List<VideoItem> GroupMembers(string tab, string key)
    {
        var all = Lib.Videos;
        var members = tab switch
        {
            "shows" => all.Where(v => IsShow(v) && string.Equals(v.Series, key, StringComparison.OrdinalIgnoreCase)),
            "collections" => all.Where(v => v.Collection != "" && string.Equals(v.Collection, key, StringComparison.OrdinalIgnoreCase)),
            _ => all.Where(v => IsOther(v) && string.Equals(FolderOf(v), key, StringComparison.OrdinalIgnoreCase))
        };
        return tab == "shows"
            ? members.OrderBy(v => v.Season == 0 ? int.MaxValue : v.Season).ThenBy(v => v.Episode).ThenBy(v => v.Title, NoCase).ToList()
            : members.OrderBy(v => v.Title, NoCase).ToList();
    }

    /// <summary>Season numbers of a series in display order (specials, season 0, last).</summary>
    public static List<int> Seasons(string series) => GroupMembers("shows", series).Select(v => v.Season).Distinct().OrderBy(s => s == 0 ? int.MaxValue : s).ToList();
    public static string SeasonName(int season) => season == 0 ? "specials" : $"season {season}";

    public static List<VideoItem> Movies() => Lib.Videos.Where(IsMovie).OrderBy(v => v.Title, NoCase).ToList();
    public static List<VideoItem> AllVideos() => Lib.Videos.OrderBy(v => v.DisplayTitle, NoCase).ToList();

    /// <summary>Renames a series, collection or folder; using an existing name merges the two.</summary>
    public static void RenameGroup(string tab, string key, string name)
    {
        var list = GroupMembers(tab, key);
        foreach (var v in list) { if (tab == "collections") v.Collection = name.Trim(); else v.Series = name.Trim(); }
        Lib.SaveVideos(list);
    }

    // ---------- quickplay ----------

    static QuickItem FromAlbum(Album a, Pin? pin = null) =>
        new() { Title = a.Title, Subtitle = a.Artist, ArtPath = a.ArtPath, Play = () => App.Player.PlayTracks(a.Tracks), Pin = pin };

    public static List<QuickItem> Pins()
    {
        var pins = new List<QuickItem>();
        foreach (var pin in Lib.Pins)
            switch (pin.Kind)
            {
                case "album" when Lib.Albums.FirstOrDefault(a => a.Key == pin.Key) is { } album:
                    pins.Add(FromAlbum(album, pin));
                    break;
                case "artist":
                    var albums = AlbumsOf(pin.Key);
                    if (albums.Count > 0)
                        pins.Add(new QuickItem
                        {
                            Title = pin.Title, Subtitle = "artist", Pin = pin,
                            ArtPath = albums.Select(a => a.ArtPath).FirstOrDefault(p => p != "") ?? "",
                            Play = () => App.Player.PlayTracks(albums.SelectMany(a => a.Tracks))
                        });
                    break;
                case "playlist" when Lib.Playlists.FirstOrDefault(p => p.Id.ToString() == pin.Key) is { } pl:
                    pins.Add(new QuickItem
                    {
                        Title = pl.Name, Subtitle = "playlist", Pin = pin,
                        ArtPath = Lib.TracksOf(pl).Select(t => t.ArtPath).FirstOrDefault(p => p != "") ?? "",
                        Play = () => App.Player.PlayTracks(Lib.TracksOf(pl))
                    });
                    break;
            }
        return pins;
    }

    public static List<QuickItem> NewAlbums() => Lib.Albums.OrderByDescending(a => a.DateAdded).Take(12).Select(a => FromAlbum(a)).ToList();

    public static List<QuickItem> History() => Lib.Tracks.Where(t => t.LastPlayed > 0).OrderByDescending(t => t.LastPlayed)
        .Select(t => t.AlbumKey).Distinct().Take(12)
        .Select(k => Lib.Albums.FirstOrDefault(a => a.Key == k)).Where(a => a != null).Select(a => FromAlbum(a!)).ToList();

    /// <summary>Liked songs, most recently liked first; a tile plays that song and carries on down the list.</summary>
    public static List<QuickItem> Liked() => Lib.Liked().Take(30).Select(t => new QuickItem
    {
        Title = t.Title, Subtitle = t.Artist, ArtPath = t.ArtPath, Play = () => App.Player.PlayTracks(Lib.Liked(), t)
    }).ToList();

    // ---------- search ----------

    public static (List<Album> albums, List<Track> songs, List<VideoItem> videos) Search(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return (new(), new(), new());
        bool Match(params string[] fields) => words.All(w => fields.Any(f => f.Contains(w, StringComparison.OrdinalIgnoreCase)));
        var albums = Lib.Albums.Where(a => Match(a.Title, a.Artist)).Take(200).ToList();
        var songs = Lib.Tracks.Where(t => Match(t.Title, t.Artist, t.Album, t.Genre))
            .OrderBy(t => t.Artist, NoCase).ThenBy(t => t.Album).ThenBy(t => t.TrackNo).Take(1000).ToList();
        var videos = Lib.Videos.Where(v => Match(v.Title, v.Series, v.Collection)).OrderBy(v => v.DisplayTitle, NoCase).Take(300).ToList();
        return (albums, songs, videos);
    }

    // ---------- pictures ----------

    public static List<PicFolder> PictureFolders(IEnumerable<string> roots)
    {
        var list = new List<PicFolder>();
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var dir in new[] { root }.Concat(Directory.EnumerateDirectories(root, "*", opts)))
            {
                if (Path.GetFileName(dir).StartsWith('.')) continue;   // .thumbnails and friends
                // app libraries (the Mac Photos library, Lightroom previews) hold copies and caches, not folders of pictures
                if (dir.Contains(".photoslibrary", StringComparison.OrdinalIgnoreCase) || dir.Contains(".lrdata", StringComparison.OrdinalIgnoreCase)) continue;
                int n;
                try { n = Directory.EnumerateFiles(dir).Count(f => Library.PictureExts.Contains(Path.GetExtension(f))); }
                catch { continue; }
                if (n > 0) list.Add(new PicFolder(dir == root ? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) : Path.GetRelativePath(root, dir), dir, n));
            }
        return list;
    }

    public static List<PictureItem> PicturesIn(PicFolder f)
    {
        try
        {
            return f.Items ?? Directory.EnumerateFiles(f.Path).Where(p => Library.PictureExts.Contains(Path.GetExtension(p)))
                .OrderBy(p => p, NoCase).Select(p => new PictureItem(Path.GetFileName(p), p, p)).ToList();
        }
        catch { return new(); }
    }
}
