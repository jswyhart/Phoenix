using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Halcyon.UI;

/// <summary>
/// Makes the title bar Windows draws on every dialog and window follow Phoenix's light or dark setting. Left to
/// itself Windows draws it white, which is the bright strip above a dark dialog.
/// </summary>
public static class WindowTheme
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    const int UseDarkOld = 19, UseDark = 20, BorderColour = 34, CaptionColour = 35, TextColour = 36;

    /// <summary>Windows themed since the app started (for the self-test).</summary>
    public static int Applied { get; private set; }
    /// <summary>The last title bar colour asked for, as red-green-blue (for the self-test).</summary>
    public static Color LastCaption { get; private set; }

    /// <summary>From now on every window gets its title bar themed as it opens.</summary>
    public static void Enable() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => Apply((Window)sender)));

    static int Ref(Color c) => c.R | (c.G << 8) | (c.B << 16);   // Windows wants blue-green-red

    static Color Brush(string key, Color fallback) => Application.Current.Resources[key] is SolidColorBrush b ? b.Color : fallback;

    public static void Apply(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var dark = App.Settings?.Theme == "dark" ? 1 : 0;
            if (DwmSetWindowAttribute(handle, UseDark, ref dark, sizeof(int)) != 0) DwmSetWindowAttribute(handle, UseDarkOld, ref dark, sizeof(int));
            // Windows 11 can also take the exact colours, so the bar is the same shade as the window under it
            var back = Brush("Bg", dark == 1 ? Color.FromRgb(0x14, 0x14, 0x14) : Colors.White);
            int caption = Ref(back), text = Ref(Brush("Fg", dark == 1 ? Colors.White : Colors.Black)), border = Ref(Brush("Line", back));
            DwmSetWindowAttribute(handle, CaptionColour, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, TextColour, ref text, sizeof(int));
            DwmSetWindowAttribute(handle, BorderColour, ref border, sizeof(int));
            LastCaption = back;
            Applied++;
        }
        catch { /* an older Windows without these: the bar stays as Windows draws it */ }
    }

    /// <summary>The theme changed: every open window's title bar follows.</summary>
    public static void ApplyAll()
    {
        if (Application.Current == null) return;
        foreach (Window w in Application.Current.Windows) Apply(w);
    }
}
