// Shared with the Windows app (Services\WebServices.cs): the streaming services Phoenix can show, and where each is shown.
using Halcyon.Core;

namespace Halcyon.Services;

/// <summary>A website shown inside Phoenix: a music or video streaming service, or an address of your own.</summary>
public record WebService(string Id, string Name, string Url, bool Video, string Profile, bool Custom = false)
{
    /// <summary>The name of its page in the app's navigation.</summary>
    public string Page => "web:" + Id;
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

    /// <summary>Turns what was typed into an address: a web address, or a file on this computer.</summary>
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
        if (!App.Testing) App.Settings.Save();
        Changed?.Invoke();
    }

    /// <summary>The services switched on, in the order they are listed.</summary>
    public static List<WebService> Enabled(bool video) => All.Where(s => s.Video == video && IsOn(s)).ToList();

    public static WebService? Find(string id) => All.FirstOrDefault(s => s.Id == id);

    // ---------- where each service is shown ----------

    /// <summary>Raised when services are switched on or off, moved, or tabs of your own change.</summary>
    public static event Action? Changed;
    public static void RaiseChanged() => Changed?.Invoke();

    public const string OwnTab = "tab";

    /// <summary>A place that can be chosen right now: its own tab, a collection tab that is on, or a tab of your own.</summary>
    static bool Usable(string place) =>
        place == OwnTab
        || (Settings.Tabs.Contains(place) && App.Settings.TabOn(place))
        || (place.StartsWith("tab:") && App.Settings.CustomTabs.Any(t => t.Page == place));

    /// <summary>
    /// Where a service is shown. As before unless you chose otherwise: music services get a tab of their own, video
    /// services sit with your videos. A place that is gone (a tab switched off or removed) falls back to its own tab.
    /// </summary>
    public static string PlaceOf(WebService s)
    {
        if (App.Settings.ServicePlaces.TryGetValue(s.Id, out var chosen) && Usable(chosen)) return chosen;
        var usual = s.Video ? "videos" : OwnTab;
        return Usable(usual) ? usual : OwnTab;
    }

    public static void SetPlace(WebService s, string place)
    {
        App.Settings.ServicePlaces[s.Id] = place;
        if (!App.Testing) App.Settings.Save();
        Changed?.Invoke();
    }

    /// <summary>The services switched on that are shown under a collection (or a tab of your own).</summary>
    public static List<WebService> Under(string collection) => All.Where(s => IsOn(s) && PlaceOf(s) == collection).ToList();

    /// <summary>The services switched on that have a collection tab of their own.</summary>
    public static List<WebService> OwnTabs => All.Where(s => IsOn(s) && PlaceOf(s) == OwnTab).ToList();

    /// <summary>Every place a service can be shown, as (place, name), for settings.</summary>
    public static List<(string Place, string Name)> Places() =>
        new[] { (OwnTab, "its own tab") }
            .Concat(Settings.Tabs.Where(App.Settings.TabOn).Select(t => (t, "under " + t)))
            .Concat(App.Settings.CustomTabs.Select(t => (t.Page, "under " + t.Name.ToLowerInvariant())))
            .ToList();

    /// <summary>The name a place is shown with ("videos", or a tab of your own's name).</summary>
    public static string PlaceName(string place) =>
        place.StartsWith("tab:") ? App.Settings.CustomTabs.FirstOrDefault(t => t.Page == place)?.Name.ToLowerInvariant() ?? "" : place;

    // ---------- tabs of your own ----------

    public static CustomTab AddTab(string name)
    {
        var tab = new CustomTab { Name = name.Trim() };
        App.Settings.CustomTabs.Add(tab);
        if (!App.Testing) App.Settings.Save();
        Changed?.Invoke();
        return tab;
    }

    public static void RenameTab(CustomTab tab, string name)
    {
        if (name.Trim() == "") return;
        tab.Name = name.Trim();
        if (!App.Testing) App.Settings.Save();
        Changed?.Invoke();
    }

    /// <summary>Removes a tab of your own; the services in it go back to where they were before.</summary>
    public static void RemoveTab(CustomTab tab)
    {
        App.Settings.CustomTabs.Remove(tab);
        foreach (var key in App.Settings.ServicePlaces.Where(kv => kv.Value == tab.Page).Select(kv => kv.Key).ToList()) App.Settings.ServicePlaces.Remove(key);
        if (!App.Testing) App.Settings.Save();
        Changed?.Invoke();
    }
}
