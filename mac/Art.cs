using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Halcyon.Core;

namespace Halcyon.Desktop;

/// <summary>
/// Attached property that loads artwork off the UI thread. Accepts a local image path, an http(s) URL (cached on disk),
/// or "thumb:&lt;video file&gt;", for which a picture from the video is made once and kept in the art folder.
/// </summary>
public class Art : AvaloniaObject
{
    public static readonly AttachedProperty<string?> PathProperty = AvaloniaProperty.RegisterAttached<Art, Image, string?>("Path");
    public static void SetPath(Image o, string? v) => o.SetValue(PathProperty, v);
    public static string? GetPath(Image o) => o.GetValue(PathProperty);

    public static readonly AttachedProperty<int> SizeProperty = AvaloniaProperty.RegisterAttached<Art, Image, int>("Size", 300);
    public static void SetSize(Image o, int v) => o.SetValue(SizeProperty, v);
    public static int GetSize(Image o) => o.GetValue(SizeProperty);

    static readonly Dictionary<string, Bitmap?> Cache = new();
    static readonly SemaphoreSlim Gate = new(4);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".img" };

    static Art() => PathProperty.Changed.AddClassHandler<Image>(async (img, e) =>
    {
        var path = e.NewValue as string;
        img.Source = null;
        if (string.IsNullOrEmpty(path)) return;
        var bmp = await LoadAsync(path, GetSize(img));
        if (GetPath(img) == path) img.Source = bmp;
    });

    public static void Invalidate(string path)
    {
        lock (Cache)
            foreach (var k in Cache.Keys.Where(k => k.StartsWith(path + "|")).ToList()) Cache.Remove(k);
    }

    public static async Task<Bitmap?> LoadAsync(string path, int size, bool cache = true)
    {
        var key = path + "|" + size;
        if (cache) lock (Cache) if (Cache.TryGetValue(key, out var hit)) return hit;
        await Gate.WaitAsync();
        Bitmap? bmp;
        try { bmp = await Task.Run(() => Load(path, size)); }
        finally { Gate.Release(); }
        if (cache)
            lock (Cache)
            {
                if (Cache.Count > 1500) Cache.Clear();
                Cache[key] = bmp;
            }
        return bmp;
    }

    /// <summary>
    /// The cached thumbnail for a video, made the first time it is asked for. Keyed by the file and its date, so an
    /// edited video gets a new one; a video that gives no picture is remembered so it is not tried every time.
    /// </summary>
    static string? VideoThumbnail(string video)
    {
        if (!File.Exists(video)) return null;
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(video + "|" + File.GetLastWriteTimeUtc(video).Ticks)));
        var png = Path.Combine(AppPaths.ArtDir, "video-" + key + ".png");
        var none = png + ".none";
        if (File.Exists(png)) return png;
        if (File.Exists(none)) return null;
        try
        {
            if (HalcyonApp.Engine.Thumbnail(video, png)) return png;
            File.WriteAllText(none, "");
        }
        catch { }
        return null;
    }

    // HEIC (iPhone photos), TIFF and camera RAW: the drawing library can't read them, but macOS can
    static readonly HashSet<string> Converted = new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif", ".tif", ".tiff", ".dng" };

    /// <summary>
    /// Has macOS's own image tool (sips) make a JPEG of the picture at the size asked for, kept in the image cache.
    /// Returns null elsewhere, or if the conversion fails.
    /// </summary>
    static string? ConvertWithMac(string file, int size)
    {
        if (!Platform.IsMac) return null;
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{file}|{File.GetLastWriteTimeUtc(file).Ticks}|{size}")));
        var jpg = Path.Combine(AppPaths.ImgCacheDir, key + ".jpg");
        if (File.Exists(jpg)) return jpg;
        try
        {
            Directory.CreateDirectory(AppPaths.ImgCacheDir);
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/sips",
                new[] { "-s", "format", "jpeg", "-Z", size.ToString(), file, "--out", jpg }) { RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return null;
            if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 && File.Exists(jpg) ? jpg : null;
        }
        catch { return null; }
    }

    static Bitmap? Load(string path, int size)
    {
        try
        {
            if (path.StartsWith("http://") || path.StartsWith("https://"))
            {
                var file = Path.Combine(AppPaths.ImgCacheDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path))) + ".img");
                if (!File.Exists(file)) File.WriteAllBytes(file, Http.GetByteArrayAsync(path).GetAwaiter().GetResult());
                path = file;
            }
            if (path.StartsWith("thumb:") && VideoThumbnail(path[6..]) is not { } thumb) return null;
            else if (path.StartsWith("thumb:")) path = VideoThumbnail(path[6..])!;
            if (Converted.Contains(Path.GetExtension(path)) && File.Exists(path)) path = ConvertWithMac(path, size) ?? path;
            if (!ImageExts.Contains(Path.GetExtension(path).ToLowerInvariant()) || !File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, size, BitmapInterpolationMode.MediumQuality);
        }
        catch { return null; }
    }
}

// ---------- small binding helpers ----------

public class Converter<T>(Func<T, object?> convert) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is T t ? convert(t) : null;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class RatingText() : Converter<int>(r => r > 0 ? "♥" : r < 0 ? "✕" : "♡");
public class RatingOpacity() : Converter<int>(r => r == 0 ? 0.3 : 1.0);
public class PlayedOpacity() : Converter<bool>(played => played ? 0.45 : 1.0);
public class NotEmpty() : Converter<string>(s => s != "");
public class GroupRowHeight() : Converter<string>(art => art != "" ? 70.0 : 32.0);
public class TrackNo() : Converter<int>(n => n > 0 ? n.ToString() : "");

public class RelayCommand(Action<object?> run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run(parameter);
}

public static class Cmd
{
    /// <summary>Cycles a song's rating: unrated → liked → disliked → unrated.</summary>
    public static readonly ICommand Rate = new RelayCommand(o =>
    {
        if (o is Track t) App.Lib.Rate(new[] { t }, t.Rating == 0 ? 1 : t.Rating == 1 ? -1 : 0);
    });
}

/// <summary>What the tile templates bind to.</summary>
public record Tile(object Item, string Title, string Caption, string ArtPath)
{
    public static Tile Of(Album a) => new(a, a.Title, a.Source == "local" ? a.Artist : $"{a.Artist}  ·  {a.Source}", a.ArtPath);
    public static Tile Of(QuickItem q) => new(q, q.Title, q.Subtitle, q.ArtPath);
    public static Tile Movie(VideoItem v) => new(v, v.Title, Fmt.Join(v.Year > 0 ? v.Year.ToString() : "", v.DurationText), v.ArtPath);
    public static Tile Video(VideoItem v) => new(v, v.DisplayTitle, v.DurationText, v.ArtPath);
    public static Tile Of(PictureItem p) => new(p, p.Name, "", p.ArtPath);
}
