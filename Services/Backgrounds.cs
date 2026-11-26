using System.IO;
using Halcyon.Core;

namespace Halcyon.Services;

/// <summary>
/// A picture of your own behind Phoenix's window (settings → display). It is copied into the data folder, so it stays
/// when the original moves, and goes with a backup. Shared by the Windows app and the Mac/Linux helper.
/// </summary>
public static class Backgrounds
{
    public static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };
    public static string Folder => Path.Combine(AppPaths.DataDir, "backgrounds");

    /// <summary>Copies the picture in and returns where it is kept now; the one before is removed.</summary>
    public static string Use(string file, string current)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("That picture isn't there any more.");
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (!Extensions.Contains(ext)) throw new InvalidOperationException("Choose a JPEG, PNG, BMP, GIF or WebP picture.");
        Directory.CreateDirectory(Folder);
        var kept = Path.Combine(Folder, $"background-{DateTime.UtcNow:yyyyMMddHHmmss}{ext}");
        File.Copy(file, kept, true);
        Clear(current);
        return kept;
    }

    /// <summary>Removes a picture Phoenix kept (one elsewhere is left alone).</summary>
    public static void Clear(string current)
    {
        if (current == "") return;
        try
        {
            if (Path.GetFullPath(current).StartsWith(Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase)) File.Delete(current);
        }
        catch { }
    }
}
