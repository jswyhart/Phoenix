using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Halcyon.Core;
using Halcyon.Services;
using Halcyon.UI;

namespace Halcyon;

public partial class App : Application
{
    public static Settings Settings = null!;
    public static Library Lib = null!;
    public static Player Player = null!;
    public static DiscogsClient Discogs = null!;
    public static JellyfinClient Jellyfin = null!;
    public static PlexClient Plex = null!;
    public static readonly SyncServer Sync = new();
    public static MainWindow Win = null!;
    public static readonly System.Collections.ObjectModel.ObservableCollection<Track> BurnList = new();
    public static bool SmokeMode;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        D3DSurface.Warm();       // Direct3D 11 for the visualizer and cover grids, set up while the window opens
        WindowTheme.Enable();    // every dialog's title bar follows the light or dark setting
        SmoothScroll.Enable();   // the mouse wheel glides at the display's refresh rate instead of jumping
        // Let WPF render on the GPU (the default); never fall back to software unless the driver forces it.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;
        var smoke = Array.IndexOf(e.Args, "--smoke");
        var probe = Array.IndexOf(e.Args, "--probe");
        var bench = Array.IndexOf(e.Args, "--bench");
        var perf = Array.IndexOf(e.Args, "--perf");
        SmokeMode = smoke >= 0 || probe >= 0 || bench >= 0 || perf >= 0;

        if (AppPaths.ChosenMissing is { } gone && !SmokeMode)
        {
            // the data folder you chose is not there (a drive that is not connected?): never quietly start a new, empty library
            var answer = System.Windows.MessageBox.Show(
                $"Phoenix keeps its library in\n\n{gone}\n\nbut that folder can't be found. If it is on a drive that is not connected, choose No, connect it and start Phoenix again.\n\n" +
                "Start with the standard folder instead? (That is a separate library; the one above is not touched.)", "Phoenix", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) { Shutdown(); return; }
            AppPaths.WritePointer("", AppPaths.FormerRoots, Array.Empty<string>());
        }
        Directory.CreateDirectory(AppPaths.DataDir);
        Directory.CreateDirectory(AppPaths.ArtDir);
        Directory.CreateDirectory(AppPaths.ImgCacheDir);

        DispatcherUnhandledException += (_, a) =>
        {
            try { File.AppendAllText(Path.Combine(AppPaths.DataDir, "error.log"), $"{DateTime.Now:u} {a.Exception}\n\n"); } catch { }
            a.Handled = true;
            if (SmokeMode) { Shutdown(2); return; }
            UI.MessageBox.Show(a.Exception.Message, "Phoenix", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        var restoredFrom = Backup.ApplyPending();   // a backup chosen for restoring last time is put in place before anything is opened
        Settings = Settings.Load();
        ApplyTheme(Settings.Theme);
        Lib = new Library(new Database(AppPaths.DbPath));
        if (!string.IsNullOrEmpty(restoredFrom) && !string.Equals(restoredFrom.TrimEnd('\\'), AppPaths.DataDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            Lib.Db.MoveDataFolder(restoredFrom.TrimEnd('\\') + "\\", AppPaths.DataDir.TrimEnd('\\') + "\\");
        Lib.Load();
        Translator.Attach(Lib.Db);
        Halcyon.Services.SpeechLyrics.Decode = Halcyon.Services.CdAudio.DecodeForSpeech;
        Halcyon.Services.AutoEq.Decode = (file, cancel) => (Halcyon.Services.CdAudio.DecodeMono(file, 44100, cancel), 44100);
        Halcyon.Services.SpeechLyrics.GpuChoice = Settings.LyricsGpu;
        if (!SmokeMode) Storage.CleanUpOld();   // the library opened from here, so copies left in a folder it was moved from can go
        Player = new Player();
        // the auto equalizer follows the song playing, and measures a song the first time it plays if it hasn't been yet
        Player.CurrentChanged += () =>
        {
            Player.ApplyEqualizer();
            Halcyon.Services.AutoEq.EnsureMeasured(Settings, Player.Current?.Track);
        };
        Halcyon.Services.AutoEq.Measured += t => Current.Dispatcher.BeginInvoke(() => { if (Player.Current?.Track == t) Player.ApplyEqualizer(); });
        Discogs = new DiscogsClient();
        Jellyfin = new JellyfinClient();
        Plex = new PlexClient();
        if (SmokeMode) PhonePairing.InMemory = true;
        PhonePairing.Asked += r => Current.Dispatcher.BeginInvoke(() => AskToPair(r));
        PhonePairing.Activity += (phone, text) => Current.Dispatcher.BeginInvoke(() => Win?.SetStatus(text.StartsWith("done", StringComparison.OrdinalIgnoreCase)
            ? $"{phone.Name} synced: {text[4..].Trim(' ', ':')}" : $"{phone.Name}: {text}"));
        if (Settings.SyncEnabled && !SmokeMode) Sync.Start();

        Win = new MainWindow();
        MainWindow = Win;
        Win.Show();
        if (!SmokeMode) Resume();

        if (perf >= 0)
            Dispatcher.BeginInvoke(async () => await Smoke.RunAsync(e.Args[perf + 1], perf: true), DispatcherPriority.ApplicationIdle);
        else if (bench >= 0)
        {
            var dir = e.Args[bench + 1];
            Dispatcher.BeginInvoke(async () => await Smoke.RunAsync(dir, bench: true), DispatcherPriority.ApplicationIdle);
        }
        else if (probe >= 0)
        {
            var dir = e.Args[probe + 1];
            Dispatcher.BeginInvoke(async () => await Smoke.RunAsync(dir, server: true), DispatcherPriority.ApplicationIdle);
        }
        else if (SmokeMode)
        {
            var dir = e.Args[smoke + 1];
            Dispatcher.BeginInvoke(async () => await Smoke.RunAsync(dir), DispatcherPriority.ApplicationIdle);
        }
        else
        {
            _ = RefreshCollectionAsync();
            PodcastSync.Start();
            _ = Listening.FlushAsync();   // listens that could not be sent last time
            Updater.ClearSwapped();   // what a portable copy's last update renamed out of the way
            _ = Updater.DailyAsync();
        }
    }

    /// <summary>
    /// Pick up where you left off: when Phoenix closes, the songs in the queue, which one was playing and how far in
    /// are kept; at the next start they are put back, paused there. (Videos, podcasts, books and radio keep their own places.)
    /// </summary>
    public static void RememberQueue()
    {
        var s = Settings;
        var cur = Player.Current;
        if (!s.ResumeOnStart || cur?.Track == null) { s.ResumeTracks = new(); return; }
        var songs = Player.Queue.Where(i => i.Track != null).ToList();
        s.ResumeTracks = songs.Select(i => i.Track!.Id).Take(2000).ToList();
        s.ResumeIndex = Math.Max(0, songs.IndexOf(cur));
        s.ResumeMs = (long)Player.Position.TotalMilliseconds;
    }

    public static void Resume()
    {
        var s = Settings;
        if (!s.ResumeOnStart || s.ResumeTracks.Count == 0 || Player.Current != null) return;
        var items = new List<PlayItem>();
        var index = 0;
        for (var i = 0; i < s.ResumeTracks.Count; i++)
        {
            if (!Lib.ById.TryGetValue(s.ResumeTracks[i], out var t)) continue;   // a song gone since
            if (i == s.ResumeIndex) index = items.Count;
            items.Add(Player.From(t));
        }
        if (items.Count == 0) return;
        Player.Cue(items, index, s.ResumeIndex < s.ResumeTracks.Count && Lib.ById.ContainsKey(s.ResumeTracks[s.ResumeIndex]) ? s.ResumeMs : 0);
        Win.SetStatus($"ready to carry on with “{Player.Current?.Title}”: press play");
    }

    /// <summary>For the self-test: answers a phone's request to pair instead of asking.</summary>
    public static bool? PairAnswerForTest;

    /// <summary>A phone asks to pair: Phoenix comes to the front and asks whether to allow it.</summary>
    static void AskToPair(PhonePairing.Request r)
    {
        if (PairAnswerForTest is { } answer) { PhonePairing.Decide(r.Id, answer); return; }
        if (Win == null) { PhonePairing.Decide(r.Id, false); return; }
        if (!Win.IsVisible) Win.Show();
        if (Win.WindowState == WindowState.Minimized) Win.WindowState = WindowState.Normal;
        Win.Activate();
        var allow = Halcyon.UI.MessageBox.Show(Win, $"“{r.Name}” ({r.Address}) wants to pair with this PC: to copy music from it, bring likes and playlists across, and work it as a remote.\n\nAllow it?",
            "Pair a phone", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        PhonePairing.Decide(r.Id, allow);
        Win.SetStatus(allow ? $"{r.Name} is paired with this PC" : $"{r.Name} was not allowed to pair");
    }

    /// <summary>Rescans the library folders, then refreshes connected servers.</summary>
    public static async Task RefreshCollectionAsync(Action<string>? status = null)
    {
        void Report(string s) { Win.SetStatus(s); status?.Invoke(s); }
        await Lib.ScanAsync(Report);
        await Lib.SyncServersAsync(Report);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Halcyon.UI.Tray.Dispose();
        try { if (!SmokeMode) { RememberQueue(); Settings.Save(); } } catch { }
        if (!SmokeMode) Updater.InstallWaiting();   // an update fetched earlier goes in now that Phoenix is closing
        base.OnExit(e);
    }

    /// <summary>Shows a theme by id: "light", "dark", or one of yours from the themes folder (light if it is gone).</summary>
    public static void ApplyTheme(string id) => Themes.Apply(Themes.Find(id));
}
