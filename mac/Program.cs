using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;

namespace Halcyon.Desktop;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--display-probe")) { Console.WriteLine(MacDisplay.Describe()); return 0; }
        MacDisplay.WaitUntilReady();
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("RenderTimer"))
        {
            // the screen went away between the check and the start: start again in a moment rather than crash
            Console.Error.WriteLine("Halcyon: the display isn't ready (" + ex.Message + "); starting again shortly.");
            Thread.Sleep(2000);
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!);
                foreach (var a in args) info.ArgumentList.Add(a);
                System.Diagnostics.Process.Start(info);
            }
            catch { }
            return 75;   // temporary failure
        }
    }

    /// <summary>The app's own typeface (Selawik, built in), so the Mac and Linux builds look like the Windows one.</summary>
    public const string Font = "avares://Halcyon/Assets/Fonts#Selawik";

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HalcyonApp>().UsePlatformDetect()
        .With(new FontManagerOptions { DefaultFamilyName = Font })
        .LogToTrace();
}

/// <summary>
/// On a Mac the window toolkit cannot start while no screen is on (display asleep at a login-item or remote launch):
/// its drawing timer, a display link, fails to start. So before the toolkit starts, wait for a screen.
/// </summary>
static class MacDisplay
{
    const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";

    [DllImport(CoreGraphics)] static extern uint CGMainDisplayID();
    [DllImport(CoreGraphics)] static extern int CGDisplayIsAsleep(uint display);
    [DllImport(CoreGraphics)] static extern int CGGetActiveDisplayList(uint max, IntPtr displays, out uint count);
    [DllImport(CoreVideo)] static extern int CVDisplayLinkCreateWithActiveCGDisplays(out IntPtr link);
    [DllImport(CoreVideo)] static extern int CVDisplayLinkSetCurrentCGDisplay(IntPtr link, uint display);
    [DllImport(CoreVideo)] static extern void CVDisplayLinkRelease(IntPtr link);

    /// <summary>Whether a display link (what the toolkit's drawing timer is) can be made right now.</summary>
    static bool LinkWorks(out string detail)
    {
        var made = CVDisplayLinkCreateWithActiveCGDisplays(out var link);
        if (made != 0 || link == IntPtr.Zero) { detail = $"create {made}"; return false; }
        var set = CVDisplayLinkSetCurrentCGDisplay(link, CGMainDisplayID());
        CVDisplayLinkRelease(link);
        detail = $"create 0, set display {set}";
        return set == 0;
    }

    public static string Describe()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "not a Mac";
        try
        {
            CGGetActiveDisplayList(0, IntPtr.Zero, out var count);
            var link = LinkWorks(out var detail);
            return $"active displays {count}, main display asleep {CGDisplayIsAsleep(CGMainDisplayID()) != 0}, display link {(link ? "ok" : "fails")} ({detail})";
        }
        catch (Exception ex) { return "probe failed: " + ex.Message; }
    }

    public static void WaitUntilReady()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;
        try
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            var said = false;
            while (true)
            {
                var link = LinkWorks(out _);
                var asleep = CGDisplayIsAsleep(CGMainDisplayID()) != 0;
                // a sleeping screen is waited for; but if the link itself keeps working, don't wait on that sign alone for ever
                if (link && (!asleep || waited.Elapsed.TotalSeconds > 20)) return;
                if (!said) { Console.Error.WriteLine("Halcyon: waiting for the display to wake (" + Describe() + ")"); said = true; }
                Thread.Sleep(500);
            }
        }
        catch { }   // the check itself is not worth failing a launch over
    }
}
