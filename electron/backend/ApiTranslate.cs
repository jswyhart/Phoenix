using Halcyon.Core;
using Halcyon.Services;

namespace Phoenix.Backend;

/// <summary>
/// Translated names, as on Windows: MyMemory, DeepL or a LibreTranslate server. (The Windows app can also translate
/// offline with Firefox's engine in a hidden browser view; that is not in this build.)
/// </summary>
public static partial class Api
{
    static object TranslateState() => new
    {
        on = S.TranslateOn, to = S.TranslateTo, provider = S.TranslateProvider == "local" ? "mymemory" : S.TranslateProvider, latin = S.TranslateLatin,
        libreUrl = S.LibreTranslateUrl, hasDeepLKey = Metadata.Key("deepl") != "", hasLibreKey = Metadata.Key("libre") != "", trouble = Translator.Trouble,
        languages = Translator.Languages.Select(l => new { code = l.Code, name = l.Name }),
        providers = Translator.Providers.Where(p => p.Id != "local").Select(p => new { id = p.Id, name = p.Name, keyHelp = p.KeyHelp })
    };

    static void RegisterTranslate()
    {
        Translator.Attach(Lib.Db);
        if (S.TranslateProvider == "local") S.TranslateProvider = "mymemory";
        Translator.TroubleChanged += () => MainLoop.Post(() => { if (Translator.Trouble != "") App.SetStatus(Translator.Trouble); });

        Rpc.On("translate.state", _ => TranslateState());
        Rpc.On("translate.set", p =>
        {
            if (p.Has("on")) S.TranslateOn = p.Bool("on");
            if (p.Has("to") && Translator.Languages.Any(l => l.Code == p.Str("to"))) S.TranslateTo = p.Str("to");
            if (p.Has("provider") && Translator.Providers.Any(x => x.Id == p.Str("provider") && x.Id != "local")) S.TranslateProvider = p.Str("provider");
            if (p.Has("latin")) S.TranslateLatin = p.Bool("latin");
            if (p.Has("libreUrl")) S.LibreTranslateUrl = p.Str("libreUrl").Trim();
            if (p.Has("deeplKey") && p.Str("deeplKey").Trim() != "") S.Keys["deepl"] = p.Str("deeplKey").Trim();
            if (p.Has("libreKey") && p.Str("libreKey").Trim() != "") S.Keys["libre"] = p.Str("libreKey").Trim();
            Save();
            Translator.Reset();
            Rpc.Emit("translate");
            return TranslateState();
        });
        // the translations of the names on screen: known ones at once, others as the service answers (a few seconds at most)
        Rpc.On("translate.names", async p =>
        {
            var texts = p.Strs("texts").Distinct().Take(200).ToList();
            var asks = texts.Where(Translator.Needs).ToDictionary(t => t, Translator.GetAsync);
            await Task.WhenAny(Task.WhenAll(asks.Values), Task.Delay(8000));
            // names that need none come back as "" (nothing to show); ones still being asked are left out, to be asked again
            return texts.Where(t => !asks.TryGetValue(t, out var a) || a.IsCompletedSuccessfully)
                .ToDictionary(t => t, t => asks.TryGetValue(t, out var a) ? a.Result : "");
        });
    }
}
