using System.Net.Http;
using System.Text.Json;
using Halcyon.Core;

namespace Halcyon.Services;

/// <summary>Looks lyrics up on LRCLIB (lrclib.net): a free, open lyrics database with timed lyrics. No account.</summary>
public static class LyricsFinder
{
    static readonly HttpClient Http = Create();
    static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PhoenixMediaPlayer/1.0 (desktop music player)");
        return c;
    }

    /// <summary>The lyrics text (timed if LRCLIB has them timed), or null when it has none for this song.</summary>
    public static async Task<string?> FindAsync(Track t)
    {
        static string Q(string s) => Uri.EscapeDataString(s);
        var seconds = (int)Math.Round(t.DurationMs / 1000.0);
        // exact match first: same names and the same length
        if (seconds > 0)
        {
            using var res = await Http.GetAsync($"https://lrclib.net/api/get?track_name={Q(t.Title)}&artist_name={Q(t.Artist)}&album_name={Q(t.Album)}&duration={seconds}");
            if (res.IsSuccessStatusCode && Pick(J.Parse(await res.Content.ReadAsStringAsync())) is { } exact) return exact;
        }
        // then a search, keeping to recordings of about the same length so the timing fits
        var found = J.Parse(await Http.GetStringAsync($"https://lrclib.net/api/search?track_name={Q(t.Title)}&artist_name={Q(t.Artist)}"));
        return PickFrom(found, seconds);
    }

    static string? Pick(JsonElement r) => r.S("syncedLyrics") is { Length: > 0 } timed ? timed : r.S("plainLyrics") is { Length: > 0 } plain ? plain : null;

    public static string? PickFrom(JsonElement results, int seconds)
    {
        if (results.ValueKind != JsonValueKind.Array) return null;
        var fits = results.EnumerateArray().Where(r => seconds <= 0 || Math.Abs(r.L("duration") - seconds) <= 3).ToList();
        var best = fits.FirstOrDefault(r => r.S("syncedLyrics") != "");
        if (best.ValueKind != JsonValueKind.Object) best = fits.FirstOrDefault(r => r.S("plainLyrics") != "");
        return best.ValueKind == JsonValueKind.Object ? Pick(best) : null;
    }
}
