using Halcyon.Services;

namespace Halcyon.Core;

/// <summary>Encrypts secrets at rest. Android backs this with a Keystore key; tests use the pass-through.</summary>
public interface ISecretProtector
{
    string Protect(string plain);
    string Unprotect(string stored);
}

public class PlainSecrets : ISecretProtector
{
    public string Protect(string plain) => plain;
    public string Unprotect(string stored) => stored;
}

/// <summary>Process-wide services, mirroring the statics on the Windows app's App class.</summary>
public static class App
{
    public static Settings Settings = null!;
    public static Library Lib = null!;
    public static Player Player = null!;
    public static JellyfinClient Jellyfin = null!;
    public static PlexClient Plex = null!;
    public static DiscogsClient Discogs = null!;
    public static SyncClient Sync = null!;
    /// <summary>Where music copied from the PC is kept (inside a scanned music folder).</summary>
    public static string SyncDir = "";

    /// <summary>Runs an action on the UI thread and waits for it (set by the host; tests run inline).</summary>
    public static Action<Action> Ui = a => a();
    public static ISecretProtector Secrets = new PlainSecrets();
    /// <summary>Tells the image cache that a cover file was rewritten.</summary>
    public static Action<string> ArtInvalidate = _ => { };
    public static string DeviceName = "Android";
    /// <summary>A self-test is running: settings are not written to disk.</summary>
    public static bool Testing;

    /// <summary>Status line text ("scanning…", "jellyfin: 400 of 2176 videos"); raised on any thread.</summary>
    public static event Action<string>? Status;
    public static void SetStatus(string s) => Status?.Invoke(s);

    public static void Init(string dataDir, IMediaEngine engine, Func<Settings>? defaults = null)
    {
        AppPaths.DataDir = dataDir;
        Directory.CreateDirectory(AppPaths.DataDir);
        Directory.CreateDirectory(AppPaths.ArtDir);
        Directory.CreateDirectory(AppPaths.ImgCacheDir);
        Backup.ApplyPending();   // a backup chosen for restoring last time is put in place before anything is opened
        Settings = Settings.Load(defaults);
        Lib = new Library(new Database(AppPaths.DbPath));
        Lib.Load();
        Jellyfin = new JellyfinClient();
        Plex = new PlexClient();
        Discogs = new DiscogsClient();
        Sync = new SyncClient();
        if (SyncDir == "") SyncDir = Path.Combine(dataDir, "synced");
        Player = new Player(engine);
    }

    /// <summary>
    /// Pick up where you left off: the songs in the queue, which one was playing and how far in are kept, to be put
    /// back (paused) at the next start. Videos, podcasts, books and radio keep their own places.
    /// </summary>
    public static void RememberQueue()
    {
        var s = Settings;
        var cur = Player?.Current;
        if (s == null || cur == null) return;   // nothing playing now: what was kept before stays
        if (!s.ResumeOnStart || cur.Track == null) { s.ResumeTracks = new(); return; }
        var songs = Player!.Queue.Where(i => i.Track != null).ToList();
        s.ResumeTracks = songs.Select(i => i.Track!.Id).Take(2000).ToList();
        s.ResumeIndex = Math.Max(0, songs.IndexOf(cur));
        s.ResumeMs = Player.PositionMs;
    }

    /// <summary>At start: puts the kept queue back, paused where it was. False when there was nothing to put back.</summary>
    public static bool Resume()
    {
        var s = Settings;
        if (!s.ResumeOnStart || s.ResumeTracks.Count == 0 || Player.Current != null) return false;
        var items = new List<PlayItem>();
        var index = 0;
        for (var i = 0; i < s.ResumeTracks.Count; i++)
        {
            if (!Lib.ById.TryGetValue(s.ResumeTracks[i], out var t)) continue;   // a song gone since
            if (i == s.ResumeIndex) index = items.Count;
            items.Add(Player.From(t));
        }
        if (items.Count == 0) return false;
        Player.Cue(items, index, s.ResumeIndex < s.ResumeTracks.Count && Lib.ById.ContainsKey(s.ResumeTracks[s.ResumeIndex]) ? s.ResumeMs : 0);
        return true;
    }

    /// <summary>Rescans the library folders, then refreshes connected servers.</summary>
    public static async Task RefreshCollectionAsync()
    {
        await Lib.ScanAsync(SetStatus);
        await Lib.SyncServersAsync(SetStatus);
    }
}
