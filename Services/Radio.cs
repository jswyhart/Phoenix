using System.Net.Http;
using System.Text.Json;
using Halcyon.Core;

namespace Halcyon.Services;

/// <summary>
/// Internet radio: the stations you saved, and looking stations up in Radio Browser (radio-browser.info), a free,
/// community-kept directory. Only what you type in the search box is sent to it.
/// </summary>
public static class Radio
{
    static readonly string[] Hosts = { "de1.api.radio-browser.info", "nl1.api.radio-browser.info", "at1.api.radio-browser.info" };
    static readonly HttpClient Http = Create();
    static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PhoenixMediaPlayer/1.0 (desktop music player)");
        return c;
    }

    static async Task<JsonElement> AskAsync(string path)
    {
        Exception? last = null;
        foreach (var host in Hosts)
        {
            try { return J.Parse(await Http.GetStringAsync($"https://{host}/json/{path}")); }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new Exception("the station directory can't be reached");
    }

    public static async Task<List<Station>> SearchAsync(string text) =>
        Parse(await AskAsync($"stations/search?name={Uri.EscapeDataString(text.Trim())}&limit=60&hidebroken=true&order=clickcount&reverse=true"));

    /// <summary>The stations people tune in to most.</summary>
    public static async Task<List<Station>> PopularAsync() => Parse(await AskAsync("stations/topclick/40?hidebroken=true"));

    public static List<Station> Parse(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Array) return new();
        return r.EnumerateArray().Select(x => new Station
        {
            Name = x.S("name").Trim(),
            Url = x.S("url_resolved") is { Length: > 0 } resolved ? resolved : x.S("url"),
            Art = x.S("favicon"),
            Tags = string.Join(", ", x.S("tags").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3)),
            Country = x.S("countrycode")
        }).Where(s => s.Name != "" && s.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)).GroupBy(s => s.Url).Select(g => g.First()).ToList();
    }

    /// <summary>A way of browsing the directory: a genre or a country, with how many stations it has.</summary>
    public record Group(string Name, string Key, int Count)
    {
        public string CountText => Count.ToString("N0");
    }

    /// <summary>The genres with the most stations.</summary>
    public static async Task<List<Group>> GenresAsync() => ParseGenres(await AskAsync("tags?order=stationcount&reverse=true&hidebroken=true&limit=150"));

    /// <summary>Countries, by name.</summary>
    public static async Task<List<Group>> CountriesAsync() => ParseCountries(await AskAsync("countries?order=name&hidebroken=true"));

    public static async Task<List<Station>> ByGenreAsync(string genre) =>
        Parse(await AskAsync($"stations/bytagexact/{Uri.EscapeDataString(genre)}?limit=100&hidebroken=true&order=clickcount&reverse=true"));

    public static async Task<List<Station>> ByCountryAsync(string code) =>
        Parse(await AskAsync($"stations/bycountrycodeexact/{Uri.EscapeDataString(code)}?limit=100&hidebroken=true&order=clickcount&reverse=true"));

    public static List<Group> ParseGenres(JsonElement r) => r.ValueKind != JsonValueKind.Array ? new()
        : r.EnumerateArray().Select(x => new Group(x.S("name").Trim(), x.S("name").Trim(), (int)x.L("stationcount")))
            .Where(g => g.Name.Length is > 1 and < 30 && g.Count >= 20).ToList();

    public static List<Group> ParseCountries(JsonElement r) => r.ValueKind != JsonValueKind.Array ? new()
        : r.EnumerateArray().Select(x => new Group(x.S("name").Trim(), x.S("iso_3166_1").Trim(), (int)x.L("stationcount")))
            .Where(g => g.Name != "" && g.Key.Length == 2 && g.Count > 0).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Makes a station from an address you typed (a stream, not a web page).</summary>
    public static Station? FromAddress(string name, string url)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        return new Station { Name = name.Trim() == "" ? uri.Host : name.Trim(), Url = url };
    }

    public static void Play(Station s)
    {
        App.Player.Play(new[] { Player.From(s) });
        App.Win.SetStatus($"tuning in to {s.Name}…");
    }
}
