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
        if (!SmokeMode) Storage.CleanUpOld();   // the library opened from here, so copies left in a folder it was moved from can go
        Player = new Player();
        Discogs = new DiscogsClient();
        Jellyfin = new JellyfinClient();
        Plex = new PlexClient();
        if (Settings.SyncEnabled && !SmokeMode) Sync.Start();

        Win = new MainWindow();
        MainWindow = Win;
        Win.Show();

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
            _ = Updater.DailyAsync();
        }
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
        try { if (!SmokeMode) Settings.Save(); } catch { }
        base.OnExit(e);
    }

    public static void ApplyTheme(string name)
    {
        var dark = name == "dark";
        void B(string key, string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            Current.Resources[key] = b;
        }
        B("Bg", dark ? "#141414" : "#FFFFFF");
        B("BgAlt", dark ? "#1E1E1E" : "#F3F3F3");
        B("Fg", dark ? "#F2F2F2" : "#1A1A1A");
        B("FgDim", dark ? "#A6A6A6" : "#666666");
        B("FgFaint", dark ? "#6A6A6A" : "#A9A9A9");
        B("Line", dark ? "#2E2E2E" : "#E2E2E2");
        B("Hover", dark ? "#16FFFFFF" : "#0E000000");
        B("Selected", dark ? "#26FFFFFF" : "#1C000000");
    }
}
