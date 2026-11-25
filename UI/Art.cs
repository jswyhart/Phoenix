using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Halcyon.Core;

namespace Halcyon.UI;

/// <summary>
/// Attached property that loads artwork off the UI thread. Accepts a local image path, an http(s) URL
/// (cached on disk), or "shell:&lt;path&gt;" for a Windows shell thumbnail (videos, pictures).
/// </summary>
public static class Art
{
    public static readonly DependencyProperty PathProperty =
        DependencyProperty.RegisterAttached("Path", typeof(string), typeof(Art), new PropertyMetadata(null, OnPathChanged));
    public static void SetPath(DependencyObject o, string? v) => o.SetValue(PathProperty, v);
    public static string? GetPath(DependencyObject o) => (string?)o.GetValue(PathProperty);

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.RegisterAttached("Size", typeof(int), typeof(Art), new PropertyMetadata(300));
    public static void SetSize(DependencyObject o, int v) => o.SetValue(SizeProperty, v);
    public static int GetSize(DependencyObject o) => (int)o.GetValue(SizeProperty);

    static readonly Dictionary<string, ImageSource?> Cache = new();
    static readonly SemaphoreSlim Gate = new(4);
    // Thumbnails Windows already has are quick to fetch, so they get a wide lane of their own and never wait
    // behind pictures that still have to be opened and shrunk.
    static readonly SemaphoreSlim CacheLane = new(Math.Clamp(Environment.ProcessorCount, 4, 12));
    static readonly SemaphoreSlim MakeLane = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 6));
    static int _fromWindows, _made;
    /// <summary>How many thumbnails came from Windows' own cache, and how many had to be made (for the self-test).</summary>
    public static (int FromWindows, int Made) ThumbnailCounts => (_fromWindows, _made);
    public static void ForgetAll() { lock (Cache) Cache.Clear(); if (Application.Current?.Dispatcher.CheckAccess() == true) D3DSurface.ForgetBitmaps(); }
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static void Invalidate(string path)
    {
        lock (Cache)
            foreach (var k in Cache.Keys.Where(k => k.StartsWith(path + "|")).ToList()) Cache.Remove(k);
        if (Application.Current?.Dispatcher.CheckAccess() == true) D3DSurface.ForgetBitmaps(path + "|");
        else Application.Current?.Dispatcher.BeginInvoke(() => D3DSurface.ForgetBitmaps(path + "|"));
    }

    static async void OnPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image img) return;
        var path = e.NewValue as string;
        img.Source = null;
        if (string.IsNullOrEmpty(path)) return;
        var size = GetSize(img);
        var src = await LoadAsync(path, size);
        if (GetPath(img) == path) img.Source = src;
    }

    public static async Task<ImageSource?> LoadAsync(string path, int size, bool cache = true)
    {
        var key = path + "|" + size;
        if (!cache) return await Task.Run(() => Load(path, size));
        lock (Cache) if (Cache.TryGetValue(key, out var hit)) return hit;
        ImageSource? src = null;
        if (path.StartsWith("shell:"))
        {
            // first what Windows already has (the same thumbnails File Explorer shows)…
            await CacheLane.WaitAsync();
            try { src = await Task.Run(() => { try { return ShellThumbnail(path[6..], size, cachedOnly: true); } catch { return null; } }); }
            finally { CacheLane.Release(); }
            if (src != null) Interlocked.Increment(ref _fromWindows);
            else
            {
                // …and only then the ones that have to be made; Windows keeps those for next time
                await MakeLane.WaitAsync();
                try { src = await Task.Run(() => Load(path, size)); }
                finally { MakeLane.Release(); }
                if (src != null) Interlocked.Increment(ref _made);
            }
        }
        else
        {
            await Gate.WaitAsync();
            try { src = await Task.Run(() => Load(path, size)); }
            finally { Gate.Release(); }
        }
        lock (Cache)
        {
            if (Cache.Count > 1500) Cache.Clear();
            Cache[key] = src;
        }
        return src;
    }

    static ImageSource? Load(string path, int size)
    {
        try
        {
            if (path.StartsWith("shell:")) return ShellThumbnail(path[6..], size);
            if (path.StartsWith("http://") || path.StartsWith("https://"))
            {
                var file = System.IO.Path.Combine(AppPaths.ImgCacheDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path))) + ".img");
                if (!File.Exists(file))
                {
                    var bytes = Http.GetByteArrayAsync(path).GetAwaiter().GetResult();
                    File.WriteAllBytes(file, bytes);
                }
                path = file;
            }
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = size;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // ---- Windows shell thumbnails ----

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr h);

    const int ThumbnailOnly = 0x8, InCacheOnly = 0x10;   // SIIGBF flags

    static ImageSource? ShellThumbnail(string file, int size, bool cachedOnly = false)
    {
        SHCreateItemFromParsingName(file, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
        try
        {
            // cachedOnly: answer from Windows' thumbnail cache or not at all (no file is opened, nothing is decoded)
            if (factory.GetImage(new SIZE { cx = size, cy = size }, cachedOnly ? ThumbnailOnly | InCacheOnly : 0, out var hbmp) != 0 || hbmp == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DeleteObject(hbmp); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }
}
