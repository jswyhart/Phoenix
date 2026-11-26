using Halcyon.Core;
using Halcyon.Services;

namespace Phoenix.Backend;

/// <summary>
/// Streaming services shown in Phoenix's window, and where each is shown (a collection tab of its own, under a
/// collection, or in a tab of your own), as on Windows. The pages themselves are drawn by the window; this keeps the
/// list, the switches and the places, which live in the settings.
/// </summary>
public static partial class Api
{
    static object ServiceShape(WebService s) => new
    {
        id = s.Id, name = s.Name, url = s.Url, video = s.Video, profile = s.Profile, custom = s.Custom, page = s.Page,
        on = WebServices.IsOn(s), place = WebServices.PlaceOf(s), placeName = WebServices.PlaceName(WebServices.PlaceOf(s))
    };

    static object WebState() => new
    {
        services = WebServices.All.Select(ServiceShape).ToList(),
        places = WebServices.Places().Select(p => new { place = p.Place, name = p.Name }).ToList(),
        ownTabs = WebServices.OwnTabs.Select(s => s.Id).ToList(),
        custom = CustomTabsShape()
    };

    static bool _webHooked;

    static void RegisterWeb()
    {
        if (!_webHooked)
        {
            _webHooked = true;
            WebServices.Changed += () => { Rpc.Emit("web", WebState()); Rpc.Emit("tabs", TabsState()); };
        }
        Rpc.On("web.state", _ => WebState());
        Rpc.On("web.under", p => WebServices.Under(p.Str("collection")).Select(ServiceShape).ToList());
        Rpc.On("web.set", p => { if (WebServices.Find(p.Str("id")) is { } s) WebServices.Set(s, p.Bool("on")); return WebState(); });
        Rpc.On("web.place", p => { if (WebServices.Find(p.Str("id")) is { } s) WebServices.SetPlace(s, p.Str("place")); return WebState(); });
        Rpc.On("web.addCustom", p =>
        {
            var name = p.Str("name").Trim();
            var url = WebServices.Address(p.Str("url"));
            if (name == "" || url == "") throw new InvalidOperationException("Give the tab a name and an address.");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "file")) throw new InvalidOperationException("That is not a web address or a file on this computer.");
            S.CustomServices.Add(new CustomService { Name = name, Url = p.Str("url").Trim(), Video = p.Bool("video") });
            Save();
            WebServices.RaiseChanged();
            return WebState();
        });
        Rpc.On("web.removeCustom", p =>
        {
            var id = p.Str("id").Replace("custom-", "");
            S.CustomServices.RemoveAll(c => c.Id == id);
            S.ServicePlaces.Remove("custom-" + id);
            Save();
            WebServices.RaiseChanged();
            return WebState();
        });
        Rpc.On("tabs.addCustom", p =>
        {
            if (p.Str("name").Trim() == "") throw new InvalidOperationException("Give the tab a name.");
            var tab = WebServices.AddTab(p.Str("name"));
            return new { id = tab.Id, page = tab.Page, state = WebState() };
        });
        Rpc.On("tabs.renameCustom", p => { if (S.CustomTabs.FirstOrDefault(t => t.Id == p.Str("id")) is { } t) WebServices.RenameTab(t, p.Str("name")); return WebState(); });
        Rpc.On("tabs.removeCustom", p => { if (S.CustomTabs.FirstOrDefault(t => t.Id == p.Str("id")) is { } t) WebServices.RemoveTab(t); return WebState(); });
    }
}
