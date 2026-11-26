using Halcyon.Core;
using Halcyon.Services;

namespace Phoenix.Backend;

/// <summary>
/// What the Windows app shows around the music: lyrics in now playing (the song's own, LRCLIB, a file or pasted),
/// artist photos behind now playing, and Mixview (an album or artist with what goes with it in your collection).
/// </summary>
public static partial class Api
{
    static object? LyricsShape(Lyrics? l) => l == null ? null : new
    {
        synced = l.Synced, source = l.Source, lines = l.Lines.Select(x => new { ms = x.Ms, text = x.Text })
    };

    static Track? TrackOf(long id) => Lib.ById.GetValueOrDefault(id);

    static object DisplayState() => new
    {
        quickplayPlays = S.QuickplayPlays, backdrops = S.Backdrops, runInTray = S.RunInTray, showGenerated = S.ShowGenerated, nowPanel = S.NowPanel, lyricsAuto = S.LyricsAuto, showLyrics = S.ShowLyrics,
        backdropUsage = Backdrops.Usage() is var (artists, bytes) && artists > 0 ? $"photos of {artists} artist{(artists == 1 ? "" : "s")} kept  ·  {bytes / 1048576.0:0.#} MB" : "no photos kept"
    };

    static void RegisterNow()
    {
        Rpc.On("display.state", _ => DisplayState());
        Rpc.On("display.set", p =>
        {
            if (p.Has("quickplayPlays")) S.QuickplayPlays = p.Bool("quickplayPlays");
            if (p.Has("runInTray")) S.RunInTray = p.Bool("runInTray");
            if (p.Has("backdrops")) S.Backdrops = p.Bool("backdrops");
            if (p.Has("lyricsAuto")) S.LyricsAuto = p.Bool("lyricsAuto");
            if (p.Has("showLyrics")) S.ShowLyrics = p.Bool("showLyrics");
            if (p.Has("showGenerated")) S.ShowGenerated = p.Bool("showGenerated");
            if (p.Has("nowPanel")) S.NowPanel = p.Bool("nowPanel");
            Save();
            return DisplayState();
        });

        // ---- lyrics ----
        Rpc.On("lyrics.get", async p =>
        {
            if (TrackOf(p.Long("id")) is not { } t) return null;
            var found = await Task.Run(() => Lyrics.For(t));
            // none of its own: look it up by itself if that is switched on
            if (found == null && S.LyricsAuto && p.Bool("auto") && !Program.SmokeMode)
                try
                {
                    if (await LyricsFinder.FindAsync(t) is { } text) { Lyrics.Save(t, text); found = Lyrics.For(t); }
                }
                catch { /* offline: nothing to show */ }
            return LyricsShape(found);
        });
        Rpc.On("lyrics.find", async p =>
        {
            if (TrackOf(p.Long("id")) is not { } t) return null;
            var text = await LyricsFinder.FindAsync(t) ?? throw new InvalidOperationException("LRCLIB has no lyrics for this song.");
            Lyrics.Save(t, text);
            return LyricsShape(Lyrics.For(t));
        });
        Rpc.On("lyrics.import", p =>
        {
            if (TrackOf(p.Long("id")) is not { } t) return null;
            var text = p.Has("file") ? File.ReadAllText(p.Str("file")) : p.Str("text");
            if (Lyrics.Parse(text, "imported") == null) throw new InvalidOperationException("There are no lyrics in that.");
            Lyrics.Save(t, text);
            return LyricsShape(Lyrics.For(t));
        });
        Rpc.On("lyrics.remove", p =>
        {
            if (TrackOf(p.Long("id")) is not { } t) return null;
            Lyrics.Remove(t);
            return LyricsShape(Lyrics.For(t));
        });

        // ---- artist photos behind now playing ----
        Rpc.On("backdrops.for", async p => S.Backdrops ? await Backdrops.ForAsync(p.Str("artist")) : new List<string>());
        Rpc.On("backdrops.clear", _ => { Backdrops.Clear(); return DisplayState(); });

        // ---- Mixview ----
        Rpc.On("mix.build", p =>
        {
            var album = AlbumByKey(p.StrOrNull("key"));
            var artist = album?.Artist ?? p.Str("artist");
            var (centre, around) = Mix.Build(album, artist);
            return new { centre, around };
        });
        Rpc.On("mix.play", p =>
        {
            if (AlbumByKey(p.StrOrNull("key")) is { } a) { P.PlayTracks(a.Tracks); return; }
            var tracks = Mix.AlbumsOf(p.Str("artist")).SelectMany(x => x.Tracks).Where(t => t.Rating >= 0).ToList();
            P.PlayShuffled(tracks);
        });
    }

    /// <summary>Mixview's surroundings, as on Windows: the artist's other albums, and artists in your collection that go with them.</summary>
    static class Mix
    {
        public record Node(string Key, string Title, string Sub, string Art, string Artist, string? AlbumKey);

        static bool Real(string artist) => artist.Trim() != "" && !artist.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase) && !artist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase);
        public static List<Album> AlbumsOf(string artist) => Lib.Albums.Where(a => string.Equals(a.Artist, artist, StringComparison.OrdinalIgnoreCase)).ToList();
        static IEnumerable<string> GenresOf(IEnumerable<Album> albums) =>
            albums.SelectMany(a => a.Genre.Split(new[] { ',', ';', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>Artists in the collection that go with one: the same genres first, then the same years, with liked music counting for more.</summary>
        public static List<string> RelatedArtists(string artist, int count)
        {
            var own = AlbumsOf(artist);
            var genres = GenresOf(own).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var years = own.Where(a => a.Year > 0).Select(a => a.Year).ToList();
            var year = years.Count > 0 ? years.Average() : 0;
            var rnd = new Random(artist.ToLowerInvariant().GetHashCode(StringComparison.Ordinal));
            return Lib.Albums.Where(a => Real(a.Artist) && !string.Equals(a.Artist, artist, StringComparison.OrdinalIgnoreCase))
                .GroupBy(a => a.Artist, StringComparer.OrdinalIgnoreCase).Select(g =>
                {
                    var shared = GenresOf(g).Count(genres.Contains);
                    var theirs = g.Where(a => a.Year > 0).Select(a => a.Year).ToList();
                    var gap = year > 0 && theirs.Count > 0 ? Math.Abs(theirs.Average() - year) : 99;
                    var score = (shared > 0 ? 2.0 + 0.5 * Math.Min(2, shared - 1) : 0) + (gap <= 5 ? 1.0 : gap <= 12 ? 0.4 : 0)
                                + (g.Any(a => a.Tracks.Any(t => t.Rating > 0)) ? 0.5 : 0) + Math.Min(0.4, g.Sum(a => a.Tracks.Count) / 100.0) + rnd.NextDouble() * 0.7;
                    return (artist: g.Key, score, any: shared > 0 || gap <= 5);
                }).Where(x => x.any).OrderByDescending(x => x.score).Take(count).Select(x => x.artist).ToList();
        }

        static Node ForAlbum(Album a) => new("album:" + a.Key, a.Title, a.Year > 0 ? $"{a.Artist}  ·  {a.Year}" : a.Artist, a.ArtPath, a.Artist, a.Key);

        static Node ForArtist(string artist)
        {
            var albums = AlbumsOf(artist);
            return new Node("artist:" + artist.ToLowerInvariant(), artist, albums.Count == 1 ? "artist  ·  1 album" : $"artist  ·  {albums.Count} albums",
                albums.OrderByDescending(a => a.Tracks.Sum(t => t.PlayCount)).Select(a => a.ArtPath).FirstOrDefault(p => p != "") ?? "", artist, null);
        }

        public static (Node Centre, List<Node> Around) Build(Album? album, string artist)
        {
            var around = new List<Node>();
            Node centre;
            if (album != null)
            {
                centre = ForAlbum(album);
                if (Real(artist)) around.Add(ForArtist(artist));
                around.AddRange(AlbumsOf(artist).Where(a => a.Key != album.Key).OrderByDescending(a => a.Year).Take(5).Select(ForAlbum));
            }
            else
            {
                centre = ForArtist(artist);
                around.AddRange(AlbumsOf(artist).OrderByDescending(a => a.Year).Take(7).Select(ForAlbum));
            }
            if (Real(artist)) around.AddRange(RelatedArtists(artist, 14 - around.Count > 7 ? 7 : Math.Max(3, 13 - around.Count)).Select(ForArtist));
            return (centre, around);
        }
    }
}
