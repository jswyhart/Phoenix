using System.Net;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Halcyon.Core;

namespace Halcyon.Services;

public static class PodcastService
{
    static readonly HttpClient Http = Create();
    static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PhoenixMediaPlayer/1.0");
        return c;
    }

    // Episode audio: follows the tracking/redirect hops podcast hosts put in front of the file.
    static readonly HttpClient Audio = new(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 20 }) { Timeout = TimeSpan.FromMinutes(30) };

    static HttpRequestMessage AudioRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("PhoenixMediaPlayer/1.0");
        return req;
    }

    /// <summary>The address the episode file really lives at once every redirect has been followed.</summary>
    public static async Task<string> ResolveAsync(string url)
    {
        using var resp = await Audio.SendAsync(AudioRequest(url), HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        return resp.RequestMessage?.RequestUri?.AbsoluteUri ?? url;
    }

    /// <summary>Saves the episode to the cache (for servers the media engine cannot stream from) and returns the file.</summary>
    public static async Task<string> DownloadAsync(string url, string? key = null)
    {
        var dir = Path.Combine(AppPaths.DataDir, "podcast-cache");
        Directory.CreateDirectory(dir);
        var name = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key ?? url)));
        var ext = Path.GetExtension(new Uri(url).AbsolutePath);
        var file = Path.Combine(dir, name + (ext.Length is > 1 and <= 5 ? ext : ".mp3"));
        if (File.Exists(file)) return file;
        using var resp = await Audio.SendAsync(AudioRequest(url), HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var part = file + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync())
        await using (var dst = File.Create(part))
            await src.CopyToAsync(dst);
        File.Move(part, file, true);
        return file;
    }

    /// <summary>Saves the episode at the given address as the given file (through a .part file, so a broken download leaves nothing behind).</summary>
    public static async Task DownloadToAsync(string url, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (File.Exists(file) && new FileInfo(file).Length > 0) return;
        using var resp = await Audio.SendAsync(AudioRequest(url), HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var part = file + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync())
        await using (var dst = File.Create(part))
            await src.CopyToAsync(dst);
        File.Move(part, file, true);
    }

    public static async Task<(Podcast podcast, List<Episode> episodes)> FetchAsync(string url)
    {
        url = url.Trim();
        if (!url.Contains("://")) url = "https://" + url;
        return Parse(await Http.GetStringAsync(url), url);
    }

    public static (Podcast podcast, List<Episode> episodes) Parse(string xml, string url)
    {
        XNamespace it = "http://www.itunes.com/dtds/podcast-1.0.dtd";
        var ch = XDocument.Parse(xml).Root?.Element("channel") ?? throw new Exception("That address is not a podcast RSS feed.");
        var podcast = new Podcast
        {
            FeedUrl = url,
            Title = ((string?)ch.Element("title"))?.Trim() ?? url,
            Author = ((string?)ch.Element(it + "author"))?.Trim() ?? "",
            Description = Strip((string?)ch.Element("description") ?? ""),
            ArtUrl = (string?)ch.Element(it + "image")?.Attribute("href") ?? (string?)ch.Element("image")?.Element("url") ?? ""
        };
        var episodes = new List<Episode>();
        foreach (var i in ch.Elements("item"))
        {
            var enc = i.Element("enclosure");
            var media = (string?)enc?.Attribute("url");
            if (string.IsNullOrEmpty(media)) continue;
            episodes.Add(new Episode
            {
                Guid = (string?)i.Element("guid") ?? media,
                Title = ((string?)i.Element("title"))?.Trim() ?? "Untitled",
                Url = media,
                Published = DateTimeOffset.TryParse((string?)i.Element("pubDate"), out var d) ? d.ToUnixTimeSeconds() : 0,
                DurationMs = ParseDuration((string?)i.Element(it + "duration")),
                Description = Strip((string?)i.Element("description") ?? (string?)i.Element(it + "summary") ?? ""),
                IsVideo = ((string?)enc?.Attribute("type") ?? "").StartsWith("video")
            });
        }
        return (podcast, episodes);
    }

    static long ParseDuration(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        long seconds = 0;
        foreach (var part in s.Trim().Split(':'))
        {
            if (!double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)) return 0;
            seconds = seconds * 60 + (long)n;
        }
        return seconds * 1000;
    }

    static string Strip(string html)
    {
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 600 ? text[..600] + "…" : text;
    }
}
