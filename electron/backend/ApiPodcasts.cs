using Halcyon.Core;
using Halcyon.Services;

namespace Phoenix.Backend;

/// <summary>
/// settings → podcasts and the downloads behind it, as on Windows: new episodes looked for at start and every hour,
/// episodes kept downloaded per series, played ones removed, a download folder of your choice.
/// </summary>
public static partial class Api
{
    static readonly int[] KeepChoices = { 0, 1, 3, 5, 10, PodcastSync.All };
    static Timer? _podcastTimer;

    static object PodcastSettings()
    {
        var (count, bytes) = PodcastSync.Usage();
        return new
        {
            auto = S.PodcastAutoRefresh, keep = S.PodcastKeep, removePlayed = S.PodcastRemovePlayed, folder = PodcastSync.Folder, ownFolder = S.PodcastFolder != "",
            choices = KeepChoices.Select(n => new { n, text = PodcastSync.KeepText(n) + (n > 0 ? " of each series" : "") }),
            usage = count == 0 ? "No episodes are downloaded."
                : $"{count} episode{(count == 1 ? "" : "s")} downloaded, using {(bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1 << 20):0} MB")}."
        };
    }

    static void RegisterPodcastSync()
    {
        PodcastSync.Changed += () => MainLoop.Post(() => { EpisodeLists.Clear(); Rpc.Emit("podcasts"); });
        if (!Program.SmokeMode)
        {
            // the first look shortly after start, then every hour
            async Task Look() { if (S.PodcastAutoRefresh && !PodcastSync.IsRunning) await PodcastSync.RefreshAllAsync(App.SetStatus); }
            _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => MainLoop.Post(() => _ = Look()));
            _podcastTimer = MainLoop.Every(60 * 60 * 1000, () => _ = Look());
        }

        Rpc.On("podcastSettings.state", _ => PodcastSettings());
        Rpc.On("podcastSettings.set", async p =>
        {
            if (p.Has("auto")) S.PodcastAutoRefresh = p.Bool("auto");
            if (p.Has("removePlayed")) S.PodcastRemovePlayed = p.Bool("removePlayed");
            if (p.Has("keep")) S.PodcastKeep = Math.Max(0, p.Int("keep"));
            if (p.Has("folder")) S.PodcastFolder = p.Str("folder").Trim();
            Save();
            // bring the downloads in line with the new choices
            if ((p.Has("keep") || p.Has("removePlayed")) && !Program.SmokeMode) { await PodcastSync.DownloadsAsync(App.SetStatus); App.SetStatus(""); }
            return PodcastSettings();
        });
        Rpc.On("podcastSettings.removeAll", _ => { PodcastSync.RemoveAll(); return PodcastSettings(); });

        // one series: its own keep choice, download its unplayed episodes, or remove its downloads
        Rpc.On("podcast.keep", async p =>
        {
            if (Lib.Podcasts.FirstOrDefault(x => x.Id == p.Long("id")) is not { } pod) return;
            PodcastSync.SetKeep(pod, p.Has("keep") ? p.Int("keep") : null);
            if (!Program.SmokeMode) { await PodcastSync.DownloadsAsync(App.SetStatus); App.SetStatus(""); }
        });
        Rpc.On("podcast.keepOf", p => Lib.Podcasts.FirstOrDefault(x => x.Id == p.Long("id")) is { } pod
            ? new { keep = PodcastSync.KeepFor(pod), own = S.PodcastKeepBySeries.ContainsKey(pod.FeedUrl), choices = KeepChoices.Select(n => new { n, text = PodcastSync.KeepText(n) }) }
            : null);
        Rpc.On("podcast.downloadAll", async p =>
        {
            if (Lib.Podcasts.FirstOrDefault(x => x.Id == p.Long("id")) is not { } pod) return 0;
            var n = 0;
            foreach (var e in Lib.Db.LoadEpisodes(pod.Id).Where(e => !e.Played && e.File == ""))
            {
                App.SetStatus($"downloading {pod.Title}: {e.Title}…");
                try { await PodcastSync.DownloadAsync(pod, e); n++; } catch { }
            }
            App.SetStatus("");
            EpisodeLists.Remove(pod.Id);
            return n;
        });
        Rpc.On("podcast.removeDownloads", p =>
        {
            if (Lib.Podcasts.FirstOrDefault(x => x.Id == p.Long("id")) is not { } pod) return;
            foreach (var e in Lib.Db.LoadEpisodes(pod.Id).Where(e => e.File != "" && e.Id != P.Current?.Episode?.Id)) PodcastSync.RemoveDownload(e);
            EpisodeLists.Remove(pod.Id);
        });
        Rpc.On("episode.download", async p =>
        {
            if (Lib.Podcasts.FirstOrDefault(x => x.Id == p.Long("podcast")) is not { } pod) return;
            if (EpisodesOf(pod.Id, true).FirstOrDefault(e => e.Id == p.Long("episode")) is not { } ep) return;
            App.SetStatus($"downloading {ep.Title}…");
            try { await PodcastSync.DownloadAsync(pod, ep); }
            finally { App.SetStatus(""); }
        });
        Rpc.On("episode.removeDownload", p =>
        {
            if (EpisodesOf(p.Long("podcast"), true).FirstOrDefault(e => e.Id == p.Long("episode")) is { } ep) PodcastSync.RemoveDownload(ep);
        });
    }
}
