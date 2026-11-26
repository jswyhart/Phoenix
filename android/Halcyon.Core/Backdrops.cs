using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Halcyon.Core;

namespace Halcyon.Services;

// Shared with the Windows app (Services\Backdrops.cs).

/// <summary>
/// Artist photos for the now playing screen. They come from fanart.tv (wide backgrounds; needs your key, set under
/// settings → metadata), TheAudioDB (also with a key) and Deezer (a portrait; no key). The artist's name is what is
/// sent. Pictures are kept in the data folder so each artist is only asked about once a month.
/// </summary>
public static class Backdrops
{
    static readonly HttpClient Http = Create();
    static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PhoenixMediaPlayer/1.0 (desktop music player)");
        return c;
    }

    static readonly ConcurrentDictionary<string, Task<List<string>>> Running = new(StringComparer.OrdinalIgnoreCase);
    public static string Folder => Path.Combine(AppPaths.DataDir, "backdrops");

    static string DirFor(string artist) =>
        Path.Combine(Folder, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(artist.Trim().ToLowerInvariant())))[..16]);

    static List<string> Stored(string dir) => Directory.Exists(dir)
        ? Directory.GetFiles(dir, "*.jpg").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() : new();

    /// <summary>The pictures kept for an artist, fetching them the first time. Empty if there are none.</summary>
    public static Task<List<string>> ForAsync(string artist)
    {
        artist = artist.Trim();
        if (artist == "" || artist.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase) || artist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new List<string>());
        var dir = DirFor(artist);
        var mark = Path.Combine(dir, "asked.txt");
        if (File.Exists(mark) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(mark)).TotalDays < 30) return Task.FromResult(Stored(dir));
        return Running.GetOrAdd(artist, a => Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(dir);
                var urls = await FindAsync(a);
                var n = Stored(dir).Count;
                foreach (var url in urls.Take(6))
                {
                    try
                    {
                        var bytes = await Http.GetByteArrayAsync(url);
                        if (bytes.Length < 8000) continue;   // a placeholder, not a photo
                        await File.WriteAllBytesAsync(Path.Combine(dir, $"{++n:00}.jpg"), bytes);
                    }
                    catch { }
                }
                await File.WriteAllTextAsync(mark, a);
            }
            catch { }
            finally { Running.TryRemove(a, out _); }
            return Stored(dir);
        }));
    }

    /// <summary>Addresses of pictures of an artist, best (widest) first.</summary>
    static async Task<List<string>> FindAsync(string artist)
    {
        var urls = new List<string>();
        var fanart = Metadata.Key("fanart");
        if (fanart != "")
            try
            {
                var found = await MusicBrainzSource.AskAsync($"https://musicbrainz.org/ws/2/artist/?query=artist:{Uri.EscapeDataString("\"" + artist.Replace("\"", "") + "\"")}&fmt=json&limit=3");
                var id = PickArtist(found, artist);
                if (id != "") urls.AddRange(ParseFanart(J.Parse(await Http.GetStringAsync($"https://webservice.fanart.tv/v3/music/{id}?api_key={Uri.EscapeDataString(fanart)}"))));
            }
            catch { }
        var audioDb = Metadata.Key("theaudiodb");
        if (urls.Count < 3 && audioDb != "")
            try { urls.AddRange(ParseAudioDb(J.Parse(await Http.GetStringAsync($"https://www.theaudiodb.com/api/v1/json/{Uri.EscapeDataString(audioDb)}/search.php?s={Uri.EscapeDataString(artist)}")))); }
            catch { }
        if (urls.Count == 0)
            try { urls.AddRange(ParseDeezer(J.Parse(await Http.GetStringAsync($"https://api.deezer.com/search/artist?limit=5&q={Uri.EscapeDataString(artist)}")), artist)); }
            catch { }
        return urls.Distinct().ToList();
    }

    /// <summary>The MusicBrainz id of the artist a search most surely means, or "".</summary>
    public static string PickArtist(JsonElement r, string artist) =>
        r.A("artists").Where(a => a.L("score") >= 90 && Metadata.Norm(a.S("name")) == Metadata.Norm(artist)).Select(a => a.S("id")).FirstOrDefault() ?? "";

    public static List<string> ParseFanart(JsonElement r) =>
        r.A("artistbackground").OrderByDescending(b => b.L("likes")).Select(b => b.S("url")).Where(u => u.StartsWith("http")).ToList();

    public static List<string> ParseAudioDb(JsonElement r) =>
        r.A("artists").Take(1).SelectMany(a => new[] { a.S("strArtistFanart"), a.S("strArtistFanart2"), a.S("strArtistFanart3"), a.S("strArtistFanart4") }).Where(u => u.StartsWith("http")).ToList();

    public static List<string> ParseDeezer(JsonElement r, string artist) =>
        r.A("data").Where(a => Metadata.Norm(a.S("name")) == Metadata.Norm(artist)).Select(a => a.S("picture_xl"))
            .Where(u => u.StartsWith("http") && !u.Contains("/artist//")).Take(1).ToList();   // "/artist//" is Deezer's blank silhouette

    public static (int Artists, long Bytes) Usage()
    {
        if (!Directory.Exists(Folder)) return (0, 0);
        return (Directory.GetDirectories(Folder).Count(d => Directory.GetFiles(d, "*.jpg").Length > 0), new DirectoryInfo(Folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length));
    }

    public static void Clear()
    {
        try { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); } catch { }
    }
}
