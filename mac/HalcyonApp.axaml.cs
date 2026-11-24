using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Halcyon.Core;

namespace Halcyon.Desktop;

public class HalcyonApp : Application
{
    public static MainWindow Win = null!;
    public static VlcEngine Engine = null!;
    public static bool SmokeMode;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? Array.Empty<string>();
            var smoke = Array.IndexOf(args, "--smoke");
            SmokeMode = smoke >= 0;

            var dataDir = Environment.GetEnvironmentVariable("HALCYON_DATA") ?? Platform.DefaultDataDir();
            Directory.CreateDirectory(dataDir);
            App.Ui = a => { if (Dispatcher.UIThread.CheckAccess()) a(); else Dispatcher.UIThread.Invoke(a); };
            App.Secrets = new Platform.Secrets(dataDir);
            App.ArtInvalidate = Art.Invalidate;
            App.DeviceName = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Mac" : "PC";
            try { Engine = new VlcEngine(); }
            catch (Exception ex)
            {
                // no playback engine: say so plainly instead of crashing, and keep the details for troubleshooting
                try { File.AppendAllText(Path.Combine(dataDir, "error.log"), $"{DateTime.Now:u} starting the playback engine failed\n{ex}\n\n"); } catch { }
                var note = new Window { Title = "Halcyon", Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterScreen, CanResize = false,
                    Content = new TextBlock { Text = ex.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(24) } };
                note.Closed += (_, _) => desktop.Shutdown();
                desktop.MainWindow = note;
                base.OnFrameworkInitializationCompleted();
                return;
            }
            App.Init(dataDir, Engine, () => SmokeMode ? new Settings { WriteTagsToFiles = true } : new Settings
            {
                WriteTagsToFiles = true,
                MusicFolders = Existing(Environment.SpecialFolder.MyMusic),
                VideoFolders = Existing(Environment.SpecialFolder.MyVideos),
                PictureFolders = Existing(Environment.SpecialFolder.MyPictures)
            });
            ApplyTheme(App.Settings.Theme);

            Win = new MainWindow();
            desktop.MainWindow = Win;
            desktop.ShutdownRequested += (_, _) => Shutdown();
            Win.Closing += (_, _) => Shutdown();

            if (SmokeMode) Dispatcher.UIThread.Post(async () => await Smoke.RunAsync(args[smoke + 1]), DispatcherPriority.Background);
            else _ = App.RefreshCollectionAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    static List<string> Existing(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        return path != "" && Directory.Exists(path) ? new List<string> { path } : new List<string>();
    }

    static bool _shutDown;
    static void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        try
        {
            App.Player.SaveEpisodePosition();
            App.Settings.Shuffle = App.Player.Shuffle;
            App.Settings.Repeat = App.Player.Repeat;
            if (!SmokeMode) App.Settings.Save();
        }
        catch { }
        Engine.Dispose();
    }

    public static void ApplyTheme(string name) =>
        Current!.RequestedThemeVariant = name == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
}
