using System.Text.Json;
using System.Text.Json.Serialization;

namespace Halcyon.Core;

public class Settings
{
    public List<string> MusicFolders { get; set; } = new();
    public List<string> VideoFolders { get; set; } = new();
    public List<string> PictureFolders { get; set; } = new();
    public List<string> BookFolders { get; set; } = new();
    public int ReaderFontSize { get; set; } = 19;
    public bool ComicRightToLeft { get; set; }   // manga: the next comic page is to the left
    public string Theme { get; set; } = "light";
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

    public string JellyfinUrl { get; set; } = "";
    public string JellyfinUser { get; set; } = "";
    public string JellyfinUserId { get; set; } = "";
    public string PlexUrl { get; set; } = "";
    public string SyncHost { get; set; } = "";   // address of the Halcyon PC to copy music from, e.g. 192.168.1.20:47800

    // Secrets are stored encrypted (Android Keystore AES-GCM key; see App.Secrets).
    public string DiscogsTokenEnc { get; set; } = "";
    public string JellyfinTokenEnc { get; set; } = "";
    public string PlexTokenEnc { get; set; } = "";
    public string SyncCodeEnc { get; set; } = "";
    [JsonIgnore] public string DiscogsToken { get; set; } = "";
    [JsonIgnore] public string JellyfinToken { get; set; } = "";
    [JsonIgnore] public string PlexToken { get; set; } = "";
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
        return s;
    }

    public void Save()
    {
        DiscogsTokenEnc = Enc(DiscogsToken);
        JellyfinTokenEnc = Enc(JellyfinToken);
        PlexTokenEnc = Enc(PlexToken);
        SyncCodeEnc = Enc(SyncCode);
        File.WriteAllText(AppPaths.SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    static string Enc(string s) => s == "" ? "" : App.Secrets.Protect(s);

    static string Dec(string s)
    {
        try { return s == "" ? "" : App.Secrets.Unprotect(s); }
        catch { return ""; }
    }
}
