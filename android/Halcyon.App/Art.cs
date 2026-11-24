using System.Security.Cryptography;
using System.Text;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Util;
using Halcyon.Core;

namespace Halcyon.Droid;

/// <summary>
/// Loads cover art and thumbnails off the UI thread: local image files, "thumb:&lt;video file&gt;" frames and http(s)
/// images (kept in a disk cache), with a small in-memory cache in front.
/// </summary>
public static class Art
{
    static readonly HttpClient Http = Create();
    static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("HalcyonMediaPlayer/1.0");
        return c;
    }
    static readonly SemaphoreSlim Gate = new(4, 4);
    static readonly Cache Mem = new((int)Math.Min(Java.Lang.Runtime.GetRuntime()!.MaxMemory() / 6, 96L * 1024 * 1024));

    class Cache(int max) : LruCache(max)
    {
        protected override int SizeOf(Java.Lang.Object? key, Java.Lang.Object? value) => (value as Bitmap)?.ByteCount ?? 1;
    }

    static string Key(string path, int size) => size + "|" + path;

    /// <summary>A cover file was rewritten: forget decoded copies (covers change rarely, so drop them all).</summary>
    public static void Invalidate(string path) => Mem.EvictAll();

    /// <summary>Shows an image in a view once it is loaded; a recycled view that has moved on to another image is left alone.</summary>
    public static void Into(ImageView view, string path, int sizePx)
    {
        var key = Key(path, sizePx);
        view.Tag = new Java.Lang.String(key);
        if (path == "") { view.SetImageDrawable(new ColorDrawable(Th.BgAlt)); return; }
        if (Mem.Get(key) is Bitmap hit) { view.SetImageBitmap(hit); return; }
        view.SetImageDrawable(new ColorDrawable(Th.BgAlt));
        _ = Task.Run(async () =>
        {
            var bmp = await LoadAsync(path, sizePx);
            if (bmp == null) return;
            HalcyonApp.Main.Post(() => { if (view.Tag?.ToString() == key) view.SetImageBitmap(bmp); });
        });
    }

    public static async Task<Bitmap?> LoadAsync(string path, int sizePx)
    {
        if (path == "") return null;
        var key = Key(path, sizePx);
        if (Mem.Get(key) is Bitmap hit) return hit;
        await Gate.WaitAsync();
        try
        {
            Bitmap? bmp;
            if (path.StartsWith("thumb:")) bmp = VideoFrame(path[6..], sizePx);
            else if (path.StartsWith("pdf:")) bmp = PdfCover(path[4..], sizePx);
            else if (path.StartsWith("http://") || path.StartsWith("https://")) bmp = Decode(await CachedAsync(path), sizePx);
            else bmp = Decode(path, sizePx);
            if (bmp != null) Mem.Put(key, bmp);
            return bmp;
        }
        catch { return null; }
        finally { Gate.Release(); }
    }

    static async Task<string> CachedAsync(string url)
    {
        var file = System.IO.Path.Combine(AppPaths.ImgCacheDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..24]);
        if (File.Exists(file) && new FileInfo(file).Length > 0) return file;
        var bytes = await Http.GetByteArrayAsync(url);
        var tmp = file + ".tmp" + Environment.CurrentManagedThreadId;
        await File.WriteAllBytesAsync(tmp, bytes);
        File.Move(tmp, file, true);
        return file;
    }

    static Bitmap? Decode(string file, int sizePx)
    {
        if (!File.Exists(file)) return null;
        var o = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeFile(file, o);
        if (o.OutWidth <= 0) return null;
        var sample = 1;
        while (Math.Max(o.OutWidth, o.OutHeight) / (sample * 2) >= sizePx) sample *= 2;
        return BitmapFactory.DecodeFile(file, new BitmapFactory.Options { InSampleSize = sample });
    }

    /// <summary>The first page of a PDF, as its cover.</summary>
    static Bitmap? PdfCover(string file, int sizePx)
    {
        if (!File.Exists(file)) return null;
        using var fd = Android.OS.ParcelFileDescriptor.Open(new Java.IO.File(file), Android.OS.ParcelFileMode.ReadOnly);
        using var pdf = new Android.Graphics.Pdf.PdfRenderer(fd!);
        if (pdf.PageCount == 0) return null;
        using var page = pdf.OpenPage(0);
        var w = Math.Max(sizePx, 200);
        var bmp = Bitmap.CreateBitmap(w, (int)(w * (double)page.Height / page.Width), Bitmap.Config.Argb8888!)!;
        bmp.EraseColor(Color.White);
        page.Render(bmp, null, null, Android.Graphics.Pdf.PdfRenderMode.ForDisplay);
        return bmp;
    }

    static Bitmap? VideoFrame(string file, int sizePx)
    {
        if (!File.Exists(file)) return null;
        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Q)
            return Android.Media.ThumbnailUtils.CreateVideoThumbnail(new Java.IO.File(file), new Size(sizePx, sizePx * 9 / 16), null);
#pragma warning disable CA1422
        return Android.Media.ThumbnailUtils.CreateVideoThumbnail(file, Android.Provider.ThumbnailKind.MiniKind);
#pragma warning restore CA1422
    }
}
