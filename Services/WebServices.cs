using Halcyon.Core;

namespace Halcyon.Services;

/// <summary>A website shown inside Phoenix: a music or video streaming service, or an address of your own.</summary>
public record WebService(string Id, string Name, string Url, bool Video, string Profile, bool Custom = false)
{
    /// <summary>The name of its page in the app's navigation.</summary>
    public string Page => "web:" + Id;
}

/// <summary>An address of your own (a web page, or an HTML file on this PC).</summary>
public class CustomService
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Video { get; set; }
}

/// <summary>
/// The streaming services Phoenix can show in its own window, each off until switched on in settings. They are the
/// services' own web players, signed in to on their own pages; nothing of theirs is added to your library.
/// </summary>
public static class WebServices
{
    public static readonly WebService[] Presets =
    {
        new("spotify", "Spotify", "https://open.spotify.com/", false, "webview-spotify"),
        new("applemusic", "Apple Music", "https://music.apple.com/", false, "webview-applemusic"),
        new("tidal", "TIDAL", "https://listen.tidal.com/", false, "webview-tidal"),
        new("ytmusic", "YouTube Music", "https://music.youtube.com/", false, "webview-google"),
        new("youtube", "YouTube", "https://www.youtube.com/", true, "webview-google"),
        new("netflix", "Netflix", "https://www.netflix.com/", true, "webview-netflix"),
        new("primevideo", "Prime Video", "https://www.primevideo.com/", true, "webview-primevideo"),
        new("hbomax", "HBO Max", "https://www.hbomax.com/", true, "webview-hbomax"),
        new("hulu", "Hulu", "https://www.hulu.com/", true, "webview-hulu"),
        new("peacock", "Peacock", "https://www.peacocktv.com/", true, "webview-peacock"),
        new("crunchyroll", "Crunchyroll", "https://www.crunchyroll.com/", true, "webview-crunchyroll"),
    };

    /// <summary>Turns what was typed into an address: a web address, or a file on this PC.</summary>
    public static string Address(string typed)
    {
        typed = typed.Trim().Trim('"');
        if (typed == "") return "";
        if (typed.Contains("://")) return typed;
        if (System.IO.Path.IsPathRooted(typed)) return new Uri(typed).AbsoluteUri;   // C:\apps\player.html
        return "https://" + typed;
    }

    public static IEnumerable<WebService> Custom =>
        App.Settings.CustomServices.Where(c => c.Name.Trim() != "" && Address(c.Url) != "")
            .Select(c => new WebService("custom-" + c.Id, c.Name.Trim(), Address(c.Url), c.Video, "webview-custom", true));

    public static IEnumerable<WebService> All => Presets.Concat(Custom);

    public static bool IsOn(WebService s) => s.Custom || App.Settings.WebServicesOn.Contains(s.Id);

    public static void Set(WebService s, bool on)
    {
        App.Settings.WebServicesOn.Remove(s.Id);
        if (on) App.Settings.WebServicesOn.Add(s.Id);
        if (!App.SmokeMode) App.Settings.Save();
    }

    /// <summary>The services switched on, in the order they are listed.</summary>
    public static List<WebService> Enabled(bool video) => All.Where(s => s.Video == video && IsOn(s)).ToList();

    public static WebService? Find(string id) => All.FirstOrDefault(s => s.Id == id);
}
