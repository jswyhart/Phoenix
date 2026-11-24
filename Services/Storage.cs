using System.Diagnostics;
using System.IO;
using Halcyon.Core;

namespace Halcyon.Services;

/// <summary>
/// Where Phoenix keeps its library, settings, covers and caches, and moving all of that to a folder you choose.
/// The choice itself is noted in a small file in the standard place (%LocalAppData%\Phoenix\data-folder.txt), so the
/// app can find its data again wherever it is.
/// </summary>
public static class Storage
{
    // everything Phoenix itself puts in its data folder; only these are ever copied or cleaned up
    static readonly string[] Files = { "settings.json", "error.log" };
    static readonly string[] Folders = { "art", "imgcache", "podcasts", "podcast-cache", "lyrics", "translation" };
    static readonly string[] Regrown = { "webview" };   // the ebook reader's browser cache: rebuilt when needed, not worth moving

    /// <summary>The browser profiles of the streaming services (their sign-ins), one folder each.</summary>
    static IEnumerable<string> Profiles(string root)
    {
        try { return Directory.EnumerateDirectories(root, "webview-*").Select(d => Path.GetFileName(d)!).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    public static bool IsStandard => string.Equals(AppPaths.DataDir.TrimEnd('\\'), AppPaths.StandardDir, StringComparison.OrdinalIgnoreCase);
    public static bool HasLibrary(string folder) => File.Exists(Path.Combine(folder, "library.db"));

    public static long Size(string folder)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                try { total += new FileInfo(f).Length; } catch { }
        }
        catch { }
        return total;
    }

    public static string SizeText(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{Math.Max(1, bytes >> 20)} MB";

    /// <summary>Whether a folder can be written to.</summary>
    public static bool CanWrite(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".phoenix-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Copies the data to the target folder and notes it as the place to use from the next start. If the target
    /// already holds a Phoenix library, nothing is copied and that library is used. Returns files that could not be copied.
    /// </summary>
    public static async Task<int> MoveToAsync(string target, Action<string> status)
    {
        target = Path.GetFullPath(target).TrimEnd('\\');
        var from = AppPaths.DataDir.TrimEnd('\\');
        if (string.Equals(target, from, StringComparison.OrdinalIgnoreCase)) return 0;
        if (target.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("Choose a folder that is not inside the current data folder.");
        if (!CanWrite(target)) throw new Exception("Phoenix can't write to that folder.");
        var failed = 0;
        var adopt = HasLibrary(target);
        if (!adopt)
        {
            App.Settings.Save();
            failed = await Task.Run(() =>
            {
                var bad = 0;
                foreach (var name in Files)
                    try { if (File.Exists(Path.Combine(from, name))) File.Copy(Path.Combine(from, name), Path.Combine(target, name), true); } catch { bad++; }
                foreach (var folder in Folders.Concat(Profiles(from)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var src = Path.Combine(from, folder);
                    if (!Directory.Exists(src)) continue;
                    status($"copying {folder}…");
                    foreach (var file in Directory.EnumerateFiles(src, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                    {
                        try
                        {
                            var dest = Path.Combine(target, Path.GetRelativePath(from, file));
                            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                            File.Copy(file, dest, true);
                        }
                        catch { bad++; }   // a browser profile file in use: the login may need redoing, nothing else is lost
                    }
                }
                status("copying the library…");
                var db = Path.Combine(target, "library.db");
                if (File.Exists(db)) File.Delete(db);
                App.Lib.Db.CopyTo(db);   // a clean, complete copy even while the library is open
                return bad;
            });
        }
        var was = AppPaths.FormerRoots.Append(from).Where(r => !string.Equals(r.TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase);
        var cleanup = adopt ? AppPaths.CleanupRoots : AppPaths.CleanupRoots.Append(from).Distinct(StringComparer.OrdinalIgnoreCase);
        AppPaths.WritePointer(string.Equals(target, AppPaths.StandardDir, StringComparison.OrdinalIgnoreCase) ? "" : target, was, cleanup);
        return failed;
    }

    /// <summary>After a start from the new place went well: remove what Phoenix left in the old one.</summary>
    public static void CleanUpOld()
    {
        if (AppPaths.CleanupRoots.Count == 0) return;
        foreach (var root in AppPaths.CleanupRoots.ToList())
        {
            var dir = root.TrimEnd('\\');
            if (string.Equals(dir, AppPaths.DataDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(dir)) continue;
            foreach (var name in Files.Concat(new[] { "library.db", "library.db-wal", "library.db-shm" }))
                try { File.Delete(Path.Combine(dir, name)); } catch { }
            foreach (var folder in Folders.Concat(Regrown).Concat(Profiles(dir)).Distinct(StringComparer.OrdinalIgnoreCase))
                try { if (Directory.Exists(Path.Combine(dir, folder))) Directory.Delete(Path.Combine(dir, folder), true); } catch { }
            // the standard folder stays (it holds the note saying where the data went); any other is removed once empty
            try { if (!string.Equals(dir, AppPaths.StandardDir, StringComparison.OrdinalIgnoreCase) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }
        AppPaths.WritePointer(IsStandard ? "" : AppPaths.DataDir, AppPaths.FormerRoots, Array.Empty<string>());
    }

    public static void Restart()
    {
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false }); } catch { }
        System.Windows.Application.Current.Shutdown();
    }
}
