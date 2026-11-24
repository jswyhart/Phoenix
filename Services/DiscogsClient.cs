using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Halcyon.Services;

public record DiscogsResult(int Id, string Title, string Year, string Country, string Label, string Format, string Thumb)
{
    public string Details => string.Join("  ·  ", new[] { Year, Country, Format, Label }.Where(s => s != ""));
}
public record DiscogsTrack(string Position, string Title, string Duration);
public record DiscogsRelease(int Id, string Title, string Artist, int Year, string Genre, string Style, string CoverUrl, List<DiscogsTrack> Tracks);

static class J
{
    public static string S(this JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : ""
            : "";

    public static long L(this JsonElement e, string n)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(n, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt64(out var l) ? l : (long)v.GetDouble();
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var p) ? p : 0;
    }

    public static JsonElement[] A(this JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToArray() : Array.Empty<JsonElement>();

    public static JsonElement O(this JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) ? v : default;

    public static string First(this JsonElement e, string n)
    {
        var a = e.A(n);
        return a.Length > 0 && a[0].ValueKind == JsonValueKind.String ? a[0].GetString() ?? "" : "";
    }

    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

/// <summary>Discogs database API (https://www.discogs.com/developers). Uses a personal access token.</summary>
public class DiscogsClient
{
    const string Api = "https://api.discogs.com";
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    readonly SemaphoreSlim _gate = new(1, 1);
    DateTime _last = DateTime.MinValue;

    public DiscogsClient() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoenixMediaPlayer/1.0");

    public bool HasToken => App.Settings.DiscogsToken != "";

    async Task<HttpResponseMessage> SendAsync(string url)
    {
        // Discogs allows 60 authenticated requests a minute; stay just under it.
        await _gate.WaitAsync();
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var wait = _last.AddMilliseconds(1100) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (HasToken && url.StartsWith(Api)) req.Headers.TryAddWithoutValidation("Authorization", "Discogs token=" + App.Settings.DiscogsToken);
                var res = await _http.SendAsync(req);
                _last = DateTime.UtcNow;
                if ((int)res.StatusCode == 429) { res.Dispose(); await Task.Delay(6000); continue; }
                if ((int)res.StatusCode == 401) throw new Exception("Discogs rejected the access token. Check it in settings.");
                res.EnsureSuccessStatusCode();
                return res;
            }
            throw new Exception("Discogs rate limit reached. Try again in a minute.");
        }
        finally { _gate.Release(); }
    }

    async Task<JsonElement> GetAsync(string url)
    {
        using var res = await SendAsync(url);
        return J.Parse(await res.Content.ReadAsStringAsync());
    }

    public async Task<byte[]> GetBytesAsync(string url)
    {
        using var res = await SendAsync(url);
        return await res.Content.ReadAsByteArrayAsync();
    }

    public async Task<List<DiscogsResult>> SearchAsync(string artist, string album)
    {
        if (!HasToken) throw new Exception("Add your Discogs personal access token in settings first.");
        var url = $"{Api}/database/search?type=release&per_page=20&artist={Uri.EscapeDataString(artist)}&release_title={Uri.EscapeDataString(album)}";
        var results = (await GetAsync(url)).A("results");
        if (results.Length == 0)
            results = (await GetAsync($"{Api}/database/search?type=release&per_page=20&q={Uri.EscapeDataString(artist + " " + album)}")).A("results");
        return results.Select(r => new DiscogsResult((int)r.L("id"), r.S("title"), r.S("year"), r.S("country"), r.First("label"), r.First("format"), r.S("thumb"))).ToList();
    }

    public async Task<DiscogsRelease> GetReleaseAsync(int id) => ParseRelease(await GetAsync($"{Api}/releases/{id}"));

    public static DiscogsRelease ParseRelease(JsonElement r)
    {
        var artists = r.A("artists").Select(a => CleanName(a.S("name"))).Where(n => n != "").ToList();
        var images = r.A("images");
        var cover = images.FirstOrDefault(i => i.S("type") == "primary");
        if (cover.ValueKind == JsonValueKind.Undefined && images.Length > 0) cover = images[0];
        var tracks = r.A("tracklist").Where(t => t.S("type_") is "track" or "")
            .Select(t => new DiscogsTrack(t.S("position"), t.S("title"), t.S("duration"))).ToList();
        return new DiscogsRelease((int)r.L("id"), r.S("title"), string.Join(" & ", artists), (int)r.L("year"),
            r.First("genres"), r.First("styles"), cover.S("uri"), tracks);
    }

    // Discogs disambiguates duplicate artist names as "Name (2)".
    static string CleanName(string n) => Regex.Replace(n, @"\s\(\d+\)$", "").Trim();
}
