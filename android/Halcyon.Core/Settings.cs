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

/// <summary>An address of your own shown as a streaming service (a web page, or an HTML file on this computer).</summary>
public class CustomService
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Video { get; set; }
}

public class Settings
{
    public List<string> MusicFolders { get; set; } = new();
    public List<string> VideoFolders { get; set; } = new();
    public List<string> PictureFolders { get; set; } = new();
    public List<string> BookFolders { get; set; } = new();
    public int ReaderFontSize { get; set; } = 19;
    public bool ComicRightToLeft { get; set; }   // manga: the next comic page is to the left
    public string Theme { get; set; } = "light";
    public string ThemeLight { get; set; } = "light";     // with the theme following the system: the theme used while it is light (desktop)
    public string ThemeDark { get; set; } = "dark";       // ...and while it is dark
    public int UiSize { get; set; }            // 0 fit the screen automatically, 1 small, 2 normal, 3 large, 4 larger
    public double Volume { get; set; } = 1.0;
    public bool Shuffle { get; set; }
    public int Repeat { get; set; }
    // Android only lets an app rewrite media files it owns, so edits live in the library database by default.
    public bool WriteTagsToFiles { get; set; }
    public int Visualizer { get; set; } = 1;   // 0 off (album art), 1 spectrum bars, 2 oscilloscope
    public int VisualizerColor { get; set; }   // colour scheme of the visualizer (desktop)
    public int VisualizerSensitivity { get; set; } = 1;   // 0 low, 1 normal, 2 high, 3 max
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The sections of the collection that can be switched off, in the order they are shown.</summary>
    public static readonly string[] Tabs = { "music", "videos", "pictures", "podcasts", "books", "playlists", "radio" };
    public List<string> TabsOff { get; set; } = new();
    public bool TabOn(string tab) => !TabsOff.Contains(tab);
    /// <summary>Shows or hides a section. The last one left cannot be switched off (returns false).</summary>
    public bool SetTab(string tab, bool on)
    {
        if (on) { TabsOff.Remove(tab); return true; }
        if (TabsOff.Contains(tab)) return true;
        if (Tabs.Count(TabOn) <= 1) return false;
        TabsOff.Add(tab);
        return true;
    }
    /// <summary>How each page is sorted and filtered: page -> "sort|descending|show".</summary>
    public Dictionary<string, string> Views { get; set; } = new();
    /// <summary>Podcast lists you added yourself to browse from.</summary>
    public List<PodcastSource> PodcastSources { get; set; } = new();
    public bool PodcastAutoRefresh { get; set; } = true;  // look for new episodes at start and every hour (desktop)
    public int PodcastKeep { get; set; }                  // newest unplayed episodes kept downloaded per series (0 none)
    public Dictionary<string, int> PodcastKeepBySeries { get; set; } = new();   // feed address -> its own choice
    public bool PodcastRemovePlayed { get; set; } = true; // remove a download once its episode has been played
    public string PodcastFolder { get; set; } = "";       // where downloads go; blank = "podcasts" in the data folder
    /// <summary>Streaming services switched on (desktop).</summary>
    public List<string> WebServicesOn { get; set; } = new();
    public List<CustomService> CustomServices { get; set; } = new();
    /// <summary>Where each streaming service is shown: "tab" (a collection tab of its own), a collection ("videos"), or a tab of your own ("tab:&lt;id&gt;").</summary>
    public Dictionary<string, string> ServicePlaces { get; set; } = new();
    public List<CustomTab> CustomTabs { get; set; } = new();
    /// <summary>Playlists made by rules (desktop).</summary>
    public List<SmartPlaylist> SmartPlaylists { get; set; } = new();
    /// <summary>Which metadata sources are on, and their weights (desktop).</summary>
    public List<Halcyon.Services.MetaSourceSetting> MetaSources { get; set; } = new();
    /// <summary>Album key -> the source it is tied to ("none" = never).</summary>
    public Dictionary<string, string> AlbumSources { get; set; } = new();
    public string FpcalcPath { get; set; } = "";          // Chromaprint's fpcalc, for identifying songs by sound
    public bool TranslateOn { get; set; }                 // show translated names beside the originals
    public bool TranslateLyrics { get; set; }             // show a translation under each line the speech model writes
    public string TranslateTo { get; set; } = "en";
    public string TranslateProvider { get; set; } = "mymemory";   // local | mymemory | deepl | libre
    public bool TranslateLatin { get; set; }              // also send names written in Latin letters
    public string LibreTranslateUrl { get; set; } = "";
    public bool LyricsAuto { get; set; }                  // look lyrics up online when a song has none
    public bool ShowLyrics { get; set; }                  // now playing's side shows lyrics instead of the queue
    public string LyricsModel { get; set; } = "";          // the speech model that writes lyrics (desktop); "" none
    public int LyricsGpu { get; set; } = -2;              // -2 automatic, -1 the processor only, or a Vulkan device index
    public bool LyricsListen { get; set; }                // write lyrics with the speech model for songs that have none, as they play
    public bool ShowGenerated { get; set; }               // now playing's side pane shows the speech model's lyrics (desktop)
    public bool NowPanel { get; set; }                    // a panel behind the queue and lyrics over the visualizer (desktop)
    public string BackgroundPicture { get; set; } = "";   // a picture of your own behind the window (desktop); "" none
    public int BackgroundStrength { get; set; } = 30;     // how much of it shows through the theme's background, 5..90 (%)
    public bool RunInTray { get; set; }                   // closing the window leaves Phoenix playing in the tray (desktop)
    public bool QuickplayPlays { get; set; }              // clicking an album in quickplay plays it (off: opens the album)
    public bool Backdrops { get; set; }                   // artist photos behind now playing (the artist's name is sent to find them)
    public bool Levelling { get; set; }                   // play every song at about the same loudness
    public bool LevelByAlbum { get; set; }                // keep an album's loud and quiet songs as they are to each other
    public bool LevelMeasure { get; set; } = true;        // songs without ReplayGain tags are measured (desktop: evened out as they play)
    public bool EqEnabled { get; set; }
    public string EqPreset { get; set; } = "flat";
    public double[] EqGains { get; set; } = new double[10];
    public bool AutoEq { get; set; }                       // the auto equalizer: each song's own adjustment, from a scan
    public string AutoEqProfile { get; set; } = "accuracy"; // accuracy | clarity | bass
    public string AutoEqStore { get; set; } = "phoenix";    // where measurements are kept: phoenix | tags | folder
    public bool AutoEqAi { get; set; }                      // the AI part: recognise each song's style (YAMNet) for a target that suits it
    public bool HeadphoneOn { get; set; }                   // correct for your headphones or speakers (AutoEq profile)
    public string HeadphoneName { get; set; } = "";
    public string HeadphoneSource { get; set; } = "";
    public double[] HeadphoneGains { get; set; } = Array.Empty<double>();
    public double Crossfade { get; set; }                 // seconds songs overlap; 0 = back to back
    /// <summary>"podcast:&lt;id&gt;" or "book:&lt;id&gt;" -> playback speed.</summary>
    public Dictionary<string, double> Speeds { get; set; } = new();
    public bool ScrobbleLastFm { get; set; }
    public bool ScrobbleListenBrainz { get; set; }
    public string LastFmUser { get; set; } = "";
    public string ListenBrainzUser { get; set; } = "";
    public bool CheckUpdates { get; set; } = true;   // ask GitHub once a day whether there is a newer Phoenix
    public long UpdateChecked { get; set; }
    public bool AutoUpdate { get; set; }                  // download a newer Phoenix when one is found and put it in place when Phoenix closes
    public bool KeepPrevious { get; set; } = true;        // keep the version being replaced, so an update can be undone
    public string PreviousVersion { get; set; } = "";     // the version that was replaced by the last update
    public string UpdateSkipped { get; set; } = "";       // a version you chose not to install (or went back from)
    /// <summary>Keys for scrobbling services, stored encrypted like the tokens below.</summary>
    public Dictionary<string, string> KeysEnc { get; set; } = new();
    [JsonIgnore] public Dictionary<string, string> Keys { get; set; } = new();

    public string JellyfinUrl { get; set; } = "";
    public string JellyfinUser { get; set; } = "";
    public string JellyfinUserId { get; set; } = "";
    public string PlexUrl { get; set; } = "";
    /// <summary>Pick up where you left off: the queue, the song and the place in it are put back (paused) at start.</summary>
    public bool ResumeOnStart { get; set; } = true;
    public List<long> ResumeTracks { get; set; } = new();
    public int ResumeIndex { get; set; }
    public long ResumeMs { get; set; }
    public string SyncHost { get; set; } = "";   // address of the Halcyon PC to copy music from, e.g. 192.168.1.20:47800
    public string SyncPcId { get; set; } = "";   // the paired PC's lasting id, to find it again when its address changes
    public string SyncPcName { get; set; } = "";
    public string SyncDeviceId { get; set; } = "";   // this phone's own id, so pairing again replaces its old key on the PC
    public bool SyncAuto { get; set; } = true;   // sync by itself when Phoenix is opened (and the PC can be reached)
    public long SyncLastAuto { get; set; }

    // Secrets are stored encrypted (Android Keystore AES-GCM key; see App.Secrets).
    public string DiscogsTokenEnc { get; set; } = "";
    public string JellyfinTokenEnc { get; set; } = "";
    public string PlexTokenEnc { get; set; } = "";
    public string SyncCodeEnc { get; set; } = "";
    [JsonIgnore] public string DiscogsToken { get; set; } = "";
    [JsonIgnore] public string JellyfinToken { get; set; } = "";
    [JsonIgnore] public string PlexToken { get; set; } = "";
    /// <summary>What the phone sends the PC: its own key from pairing, or the six-digit pairing code typed in.</summary>
    [JsonIgnore] public string SyncCode { get; set; } = "";

    public static Settings Load(Func<Settings>? defaults = null)
    {
        Settings s;
        try { s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsPath)) ?? new Settings(); }
        catch { s = defaults?.Invoke() ?? new Settings(); }
        s.DiscogsToken = Dec(s.DiscogsTokenEnc);
        s.JellyfinToken = Dec(s.JellyfinTokenEnc);
        s.PlexToken = Dec(s.PlexTokenEnc);
        s.SyncCode = Dec(s.SyncCodeEnc);
        s.Keys = s.KeysEnc.ToDictionary(k => k.Key, k => Dec(k.Value));
        return s;
    }

    public void Save()
    {
        DiscogsTokenEnc = Enc(DiscogsToken);
        JellyfinTokenEnc = Enc(JellyfinToken);
        PlexTokenEnc = Enc(PlexToken);
        SyncCodeEnc = Enc(SyncCode);
        KeysEnc = Keys.Where(k => k.Value != "").ToDictionary(k => k.Key, k => Enc(k.Value));
        File.WriteAllText(AppPaths.SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    static string Enc(string s) => s == "" ? "" : App.Secrets.Protect(s);

    static string Dec(string s)
    {
        try { return s == "" ? "" : App.Secrets.Unprotect(s); }
        catch { return ""; }
    }
}
