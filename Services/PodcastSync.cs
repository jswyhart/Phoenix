using System.IO;
using System.Text.RegularExpressions;
using Halcyon.Core;

namespace Halcyon.Services;

/// <summary>
/// Keeps podcast subscriptions current: looks for new episodes when the app starts and every hour after, and keeps
/// episodes downloaded as the settings ask (how many per series, in which folder, whether played ones are removed).
/// Episodes downloaded by hand are kept until they are played or removed by hand.
/// </summary>
public static class PodcastSync
{
    /// <summary>"Keep downloaded" choice meaning every unplayed episode.</summary>
    public const int All = 9999;

    static readonly System.Windows.Threading.DispatcherTimer Timer = new() { Interval = TimeSpan.FromMinutes(60) };
    static bool _running;

    /// <summary>Raised (on the UI thread) when subscriptions or episodes changed.</summary>
    public static event Action? Changed;
    public static bool IsRunning => _running;

    public static void Start()
    {
        Timer.Tick += async (_, _) => { if (App.Settings.PodcastAutoRefresh) await RefreshAllAsync(null); };
        Timer.Start();
        var first = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        first.Tick += async (_, _) => { first.Stop(); if (App.Settings.PodcastAutoRefresh) await RefreshAllAsync(null); };
        first.Start();
    }

    // ---------- where downloads go ----------

    /// <summary>The folder downloads are saved in: the one chosen in settings, or "podcasts" in the app's data folder.</summary>
    public static string Folder => App.Settings.PodcastFolder.Trim() is { Length: > 0 } chosen ? chosen : Path.Combine(AppPaths.DataDir, "podcasts");

    static string Clean(string name, int max)
    {
        name = Regex.Replace(name, "[<>:\"/\\\\|?*\\x00-\\x1F]", " ");
        name = Regex.Replace(name, @"\s+", " ").Trim().TrimEnd('.');
        return name.Length > max ? name[..max].Trim() : name == "" ? "untitled" : name;
    }

    static readonly string[] AudioExts = { ".mp3", ".m4a", ".mp4", ".m4b", ".aac", ".ogg", ".opus", ".wav", ".flac" };

    /// <summary>Series folder, then "2026-03-14 Episode title.mp3": a name a person (and another player) can read.</summary>
    public static string FileFor(Podcast p, Episode e, string address)
    {
        var ext = "";
        try { ext = Path.GetExtension(new Uri(address).AbsolutePath).ToLowerInvariant(); } catch { }
        if (!AudioExts.Contains(ext) && !(e.IsVideo && ext is ".mp4" or ".m4v" or ".mov" or ".webm" or ".mkv")) ext = e.IsVideo ? ".mp4" : ".mp3";
        var date = e.Published > 0 ? DateTimeOffset.FromUnixTimeSeconds(e.Published).LocalDateTime.ToString("yyyy-MM-dd") + " " : "";
        return Path.Combine(Folder, Clean(p.Title, 80), Clean(date + e.Title, 120) + ext);
    }

    /// <summary>How many of a series' newest unplayed episodes are kept downloaded: its own choice, or the general one.</summary>
    public static int KeepFor(Podcast p) => App.Settings.PodcastKeepBySeries.TryGetValue(p.FeedUrl, out var n) ? n : App.Settings.PodcastKeep;

    public static void SetKeep(Podcast p, int? keep)
    {
        if (keep == null) App.Settings.PodcastKeepBySeries.Remove(p.FeedUrl); else App.Settings.PodcastKeepBySeries[p.FeedUrl] = keep.Value;
        if (!App.SmokeMode) App.Settings.Save();
    }

    public static string KeepText(int n) => n <= 0 ? "nothing (stream episodes when played)" : n >= All ? "every unplayed episode"
        : n == 1 ? "the newest unplayed episode" : $"the {n} newest unplayed episodes";

    /// <summary>What a series' downloads should be: (episodes to have, downloads to remove).</summary>
    public static (List<Episode> want, List<Episode> remove) Plan(List<Episode> newestFirst, int keep, bool removePlayed, long? playingId = null)
    {
        var want = keep <= 0 ? new List<Episode>() : newestFirst.Where(e => !e.Played && !e.IsVideo).Take(keep).ToList();
        var remove = newestFirst.Where(e => e.File != "" && e.Id != playingId
            && (e.Played ? removePlayed : !e.Manual && !want.Contains(e))).ToList();
        return (want, remove);
    }

    // ---------- refreshing ----------

    /// <summary>Fetches every feed, then tidies downloads. Returns (new episodes found, feeds that failed).</summary>
    public static async Task<(int added, int failed)> RefreshAllAsync(Action<string>? status)
    {
        if (_running) return (0, 0);
        _running = true;
        int added = 0, failed = 0;
        try
        {
            foreach (var p in App.Lib.Podcasts.ToList())
            {
                status?.Invoke($"refreshing {p.Title}…");
                try
                {
                    var (podcast, episodes) = await PodcastService.FetchAsync(p.FeedUrl);
                    added += App.Lib.Db.SavePodcast(podcast, episodes);
                }
                catch { failed++; }
            }
            App.Lib.Podcasts = App.Lib.Db.LoadPodcasts();
            Changed?.Invoke();
            await DownloadsAsync(status);
            status?.Invoke(failed > 0 ? $"{failed} podcast feed(s) could not be refreshed"
                : added > 0 ? (added == 1 ? "1 new podcast episode" : $"{added} new podcast episodes") : "");
        }
        finally { _running = false; }
        return (added, failed);
    }

    static void Delete(Episode e)
    {
        try
        {
            if (File.Exists(e.File)) File.Delete(e.File);
            // leave no empty series folder behind (only inside the downloads folder)
            var dir = Path.GetDirectoryName(e.File);
            if (dir != null && dir.StartsWith(Folder, StringComparison.OrdinalIgnoreCase) && dir.Length > Folder.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { return; }
        e.File = "";
        e.Manual = false;
        App.Lib.Db.SaveEpisodeFile(e);
    }

    static async Task FetchAsync(Podcast p, Episode e, bool manual)
    {
        var address = await PodcastService.ResolveAsync(e.Url);
        var file = FileFor(p, e, address);
        await PodcastService.DownloadToAsync(address, file);
        e.File = file;
        e.Manual = manual;
        App.Lib.Db.SaveEpisodeFile(e);
    }

    /// <summary>Brings the downloaded files in line with the settings.</summary>
    public static async Task DownloadsAsync(Action<string>? status)
    {
        var changed = false;
        foreach (var p in App.Lib.Podcasts.ToList())
        {
            var episodes = App.Lib.Db.LoadEpisodes(p.Id);
            // a file that was deleted outside the app is no longer a download
            foreach (var e in episodes.Where(e => e.File != "" && !File.Exists(e.File))) { e.File = ""; e.Manual = false; App.Lib.Db.SaveEpisodeFile(e); changed = true; }
            var (want, remove) = Plan(episodes, KeepFor(p), App.Settings.PodcastRemovePlayed, App.Player.Current?.Episode?.Id);
            foreach (var e in remove) { Delete(e); changed = true; }
            foreach (var e in want.Where(e => e.File == ""))
            {
                status?.Invoke($"downloading {p.Title}: {e.Title}…");
                try { await FetchAsync(p, e, manual: false); changed = true; }
                catch { }
            }
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Downloads one episode now, whatever the settings say; it is kept until played or removed by hand.</summary>
    public static async Task DownloadAsync(Podcast p, Episode e)
    {
        await FetchAsync(p, e, manual: true);
        e.Raise();
    }

    public static void RemoveDownload(Episode e)
    {
        if (e.File != "") Delete(e);
        e.Raise();
    }

    /// <summary>How many episodes are downloaded, and how much room they take.</summary>
    public static (int count, long bytes) Usage()
    {
        var count = 0; long bytes = 0;
        foreach (var p in App.Lib.Podcasts)
            foreach (var file in App.Lib.Db.EpisodeFiles(p.Id))
                try { var info = new FileInfo(file); if (info.Exists) { count++; bytes += info.Length; } } catch { }
        return (count, bytes);
    }

    /// <summary>Removes every downloaded episode (the subscriptions and what you have played stay).</summary>
    public static int RemoveAll()
    {
        var n = 0;
        foreach (var p in App.Lib.Podcasts.ToList())
            foreach (var e in App.Lib.Db.LoadEpisodes(p.Id).Where(e => e.File != "" && e.Id != App.Player.Current?.Episode?.Id)) { Delete(e); n++; }
        Changed?.Invoke();
        return n;
    }
}
