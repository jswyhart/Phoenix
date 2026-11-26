using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Halcyon.Core;

/// <summary>A collection tab of your own, holding the streaming services you put in it.</summary>
public class CustomTab
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Page => "tab:" + Id;
}

public class PodcastSource
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

public class Settings
{
    public List<string> MusicFolders { get; set; } = new();
    public List<string> VideoFolders { get; set; } = new();
    public List<string> PictureFolders { get; set; } = new();
    public List<string> BookFolders { get; set; } = new();
    public int ReaderFontSize { get; set; } = 19;
    public string Theme { get; set; } = "light";
    public double Volume { get; set; } = 0.8;
    public bool Shuffle { get; set; }
    public int Repeat { get; set; }
    public bool WriteTagsToFiles { get; set; } = true;
    public int Visualizer { get; set; } = 1;   // index into Visualizer.Styles; 0 = off (album art wall)
    public int VisualizerColor { get; set; }   // index into Visualizer.Palettes
    public int VisualizerSensitivity { get; set; } = 1;   // 0 low, 1 normal, 2 high, 3 max
    public int RipFormat { get; set; }   // index into CdAudio.Formats
    public bool AudioEngine { get; set; } = true;   // music through the built-in engine (gapless, crossfade, equalizer)
    public double Crossfade { get; set; }           // seconds songs overlap; 0 = back to back
    public bool EqEnabled { get; set; }
    public string EqPreset { get; set; } = "flat";  // a name from AudioEngine.Presets, or "custom"
    public double[] EqGains { get; set; } = new double[10];
    public bool PodcastAutoRefresh { get; set; } = true;   // look for new episodes at start and every hour
    public int PodcastKeep { get; set; }            // download this many newest unplayed episodes per series; 0 = none
    public string PodcastFolder { get; set; } = ""; // where downloads go; blank = "podcasts" in the app's data folder
    public bool PodcastRemovePlayed { get; set; } = true;   // delete a download once its episode has been played
    public Dictionary<string, int> PodcastKeepBySeries { get; set; } = new();   // feed address -> its own "keep downloaded" choice
    public bool ShowLyrics { get; set; }            // the now playing side pane shows lyrics instead of the queue
    public List<SmartPlaylist> SmartPlaylists { get; set; } = new();
    public List<Halcyon.Services.MetaSourceSetting> MetaSources { get; set; } = new();   // which metadata sources are on, and their weights
    public Dictionary<string, string> AlbumSources { get; set; } = new();                // album key -> source id it is tied to ("none" = never)
    public bool TranslateOn { get; set; }                 // show translated names beside the originals
    public string TranslateTo { get; set; } = "en";
    public string TranslateProvider { get; set; } = "mymemory";   // mymemory | deepl | libre
    public bool TranslateLatin { get; set; }              // also send names written in Latin letters
    public string LibreTranslateUrl { get; set; } = "";
    public string TranslationFolder { get; set; } = "";   // offline translation engine and language packs; blank = "translation" in the data folder
    public bool LyricsAuto { get; set; }                  // look lyrics up online when a song has none
    public bool SpotifyEnabled { get; set; }              // older setting: carried into WebServicesOn when read
    public List<string> WebServicesOn { get; set; } = new();                       // streaming services shown as pages (ids from WebServices.Presets)
    public List<Halcyon.Services.CustomService> CustomServices { get; set; } = new();   // your own addresses
    public string ThemeLight { get; set; } = "light";     // with the theme following the system: the theme used while it is light
    public string ThemeDark { get; set; } = "dark";       // ...and while it is dark
    public bool RunInTray { get; set; }                   // closing the window leaves Phoenix playing in the notification area
    public bool QuickplayPlays { get; set; }              // clicking an album in quickplay plays it (off: opens the album)
    public string FpcalcPath { get; set; } = "";                                         // Chromaprint's fpcalc.exe, for identifying songs by sound
    public Dictionary<string, string> ApiKeysEnc { get; set; } = new();                  // other sources' keys, encrypted like the tokens below
    [JsonIgnore] public Dictionary<string, string> ApiKeys { get; set; } = new();
    public bool Levelling { get; set; }                   // play every song at about the same loudness
    public bool LevelByAlbum { get; set; }                // keep the loud and quiet songs of an album as they are relative to each other
    public bool LevelMeasure { get; set; } = true;        // measure songs that carry no ReplayGain tags
    public Dictionary<string, double> Speeds { get; set; } = new();   // "podcast:<id>" or "book:<id>" -> playback speed
    public bool ScrobbleLastFm { get; set; }
    public bool ScrobbleListenBrainz { get; set; }
    public string LastFmUser { get; set; } = "";
    public string ListenBrainzUser { get; set; } = "";
    /// <summary>The tabs of the collection that can be switched off, in the order they are shown.</summary>
    public static readonly string[] Tabs = { "music", "videos", "pictures", "podcasts", "books", "playlists", "radio" };
    public List<string> TabsOff { get; set; } = new();     // collection tabs that are hidden
    public bool TabOn(string tab) => !TabsOff.Contains(tab);
    /// <summary>Shows or hides a tab. The last tab left cannot be switched off (returns false).</summary>
    public bool SetTab(string tab, bool on)
    {
        if (on) { TabsOff.Remove(tab); return true; }
        if (TabsOff.Contains(tab)) return true;
        if (Tabs.Count(TabOn) <= 1) return false;
        TabsOff.Add(tab);
        return true;
    }
    [JsonPropertyName("RadioEnabled")] public bool? RadioEnabledOld { get; set; }   // how an earlier version stored the radio tab's switch
    [JsonIgnore] public bool RadioEnabled { get => TabOn("radio"); set => SetTab("radio", value); }
    /// <summary>How each page is sorted and filtered: page -> "sort|descending|show".</summary>
    public Dictionary<string, string> Views { get; set; } = new();
    /// <summary>Podcast lists you added yourself to browse from: a name and the address (or file) of an OPML list.</summary>
    public List<PodcastSource> PodcastSources { get; set; } = new();
    /// <summary>Where each streaming service is shown: "tab" (a collection tab of its own), a collection ("videos"), or a tab of your own ("tab:&lt;id&gt;").</summary>
    public Dictionary<string, string> ServicePlaces { get; set; } = new();
    public List<CustomTab> CustomTabs { get; set; } = new();
    public bool Backdrops { get; set; }                   // artist photos behind now playing (the artist's name is sent to find them)
    public bool CheckUpdates { get; set; } = true;        // ask GitHub once a day whether there is a newer Phoenix
    public bool AutoUpdate { get; set; }                  // download a newer Phoenix when one is found and install it when Phoenix is closed
    public bool KeepPrevious { get; set; } = true;        // keep the installer of the version being replaced, so an update can be undone
    public string PreviousVersion { get; set; } = "";     // the version that was replaced by the last update
    public string PreviousInstaller { get; set; } = "";   // where its installer is kept
    public long UpdateChecked { get; set; }
    public string UpdateSkipped { get; set; } = "";       // a version you chose not to be told about again
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public bool SyncEnabled { get; set; }      // let the phone app copy music from this PC over the local network
    public string SyncCode { get; set; } = ""; // pairing code the phone must send with every request

    public string JellyfinUrl { get; set; } = "";
    public string JellyfinUser { get; set; } = "";
    public string JellyfinUserId { get; set; } = "";
    public string PlexUrl { get; set; } = "";

    // Secrets are stored encrypted for the current Windows user (DPAPI).
    public string DiscogsTokenEnc { get; set; } = "";
    public string JellyfinTokenEnc { get; set; } = "";
    public string PlexTokenEnc { get; set; } = "";
    [JsonIgnore] public string DiscogsToken { get; set; } = "";
    [JsonIgnore] public string JellyfinToken { get; set; } = "";
    [JsonIgnore] public string PlexToken { get; set; } = "";

    public static Settings Load()
    {
        Settings s;
        try { s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsPath)) ?? new Settings(); }
        catch
        {
            s = new Settings();
            if (AppPaths.Override == null)
            {
                s.MusicFolders.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
                s.VideoFolders.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
                s.PictureFolders.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            }
        }
        s.DiscogsToken = Dec(s.DiscogsTokenEnc);
        s.JellyfinToken = Dec(s.JellyfinTokenEnc);
        s.PlexToken = Dec(s.PlexTokenEnc);
        s.ApiKeys = s.ApiKeysEnc.ToDictionary(k => k.Key, k => Dec(k.Value));
        if (s.RadioEnabledOld == false && !s.TabsOff.Contains("radio")) s.TabsOff.Add("radio");
        s.RadioEnabledOld = null;
        if (s.SpotifyEnabled) { s.SpotifyEnabled = false; if (!s.WebServicesOn.Contains("spotify")) s.WebServicesOn.Add("spotify"); }
        return s;
    }

    public void Save()
    {
        DiscogsTokenEnc = Enc(DiscogsToken);
        JellyfinTokenEnc = Enc(JellyfinToken);
        PlexTokenEnc = Enc(PlexToken);
        ApiKeysEnc = ApiKeys.Where(k => k.Value != "").ToDictionary(k => k.Key, k => Enc(k.Value));
        File.WriteAllText(AppPaths.SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    static string Enc(string s) => s == "" ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser));

    static string Dec(string s)
    {
        try { return s == "" ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}
