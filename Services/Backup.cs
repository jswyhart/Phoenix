using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Halcyon.Core;

namespace Halcyon.Services;

/// <summary>
/// Backup and restore: one file holding the library (songs' ratings and play counts, playlists, bookmarks, reading
/// progress, podcasts, pins, tags, the play log), your settings and imported lyrics, and optionally the album covers.
/// Your music, videos, pictures and books themselves are not in it.
/// </summary>
public static class Backup
{
    public const string Extension = ".phoenixbackup";
    const string Pending = "restore-pending";

    public record Manifest(string App, string Version, string Created, string DataDir, bool Covers, int Songs, int Playlists);

    static string PendingDir => Path.Combine(AppPaths.DataDir, Pending);

    public static string SuggestedName => $"Phoenix backup {DateTime.Now:yyyy-MM-dd}{Extension}";

    /// <summary>Writes a backup. Returns its size in bytes.</summary>
    public static async Task<long> CreateAsync(string file, bool covers, Action<string> status)
    {
        App.Settings.Save();
        var work = Path.Combine(Path.GetTempPath(), "phoenix-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            return await Task.Run(() =>
            {
                status("copying the library…");
                var db = Path.Combine(work, "library.db");
                App.Lib.Db.CopyTo(db);   // a clean, complete copy even while the library is open
                if (File.Exists(file)) File.Delete(file);
                using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
                {
                    var manifest = new Manifest("Phoenix", Updater.Current.ToString(3), DateTime.Now.ToString("s"), AppPaths.DataDir.TrimEnd('\\'), covers, App.Lib.Tracks.Count, App.Lib.Playlists.Count);
                    using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open())) w.Write(JsonSerializer.Serialize(manifest));
                    zip.CreateEntryFromFile(db, "library.db", CompressionLevel.Optimal);
                    if (File.Exists(AppPaths.SettingsPath)) zip.CreateEntryFromFile(AppPaths.SettingsPath, "settings.json", CompressionLevel.Optimal);
                    foreach (var folder in covers ? new[] { "lyrics", "art" } : new[] { "lyrics" })
                    {
                        var root = Path.Combine(AppPaths.DataDir, folder);
                        if (!Directory.Exists(root)) continue;
                        status($"adding {folder}…");
                        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                            try { zip.CreateEntryFromFile(f, folder + "/" + Path.GetRelativePath(root, f).Replace('\\', '/'), folder == "art" ? CompressionLevel.NoCompression : CompressionLevel.Optimal); } catch { }
                    }
                }
                return new FileInfo(file).Length;
            });
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    /// <summary>What a backup file says about itself, or null if it is not a Phoenix backup.</summary>
    public static Manifest? Read(string file)
    {
        try
        {
            using var zip = ZipFile.OpenRead(file);
            if (zip.GetEntry("library.db") == null || zip.GetEntry("manifest.json") is not { } entry) return null;
            using var r = new StreamReader(entry.Open());
            var m = JsonSerializer.Deserialize<Manifest>(r.ReadToEnd());
            return m is { App: "Phoenix" } ? m : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Unpacks a backup beside the library, to be put in place the next time Phoenix starts (the library cannot be
    /// swapped while it is open). The library in use now is kept as "library.before-restore.db".
    /// </summary>
    public static void Stage(string file)
    {
        if (Read(file) == null) throw new Exception("That file is not a Phoenix backup.");
        if (Directory.Exists(PendingDir)) Directory.Delete(PendingDir, true);
        Directory.CreateDirectory(PendingDir);
        var root = Path.GetFullPath(PendingDir) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(file);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            var top = entry.FullName.Split('/')[0];
            if (top is not ("manifest.json" or "library.db" or "settings.json" or "lyrics" or "art")) continue;
            var target = Path.GetFullPath(Path.Combine(PendingDir, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // never write outside the folder
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    /// <summary>
    /// At start, before the library is opened: puts a staged restore in place. Returns the data folder the backup was
    /// made in (so covers recorded under it can be pointed at this one), or null if nothing was waiting.
    /// </summary>
    public static string? ApplyPending()
    {
        var dir = PendingDir;
        var db = Path.Combine(dir, "library.db");
        if (!File.Exists(db)) return null;
        try
        {
            var from = "";
            try { from = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")))?.DataDir ?? ""; } catch { }
            if (File.Exists(AppPaths.DbPath)) File.Copy(AppPaths.DbPath, Path.Combine(AppPaths.DataDir, "library.before-restore.db"), true);
            foreach (var extra in new[] { "-wal", "-shm" }) try { File.Delete(AppPaths.DbPath + extra); } catch { }
            File.Copy(db, AppPaths.DbPath, true);
            var settings = Path.Combine(dir, "settings.json");
            if (File.Exists(settings)) File.Copy(settings, AppPaths.SettingsPath, true);
            foreach (var folder in new[] { "lyrics", "art" })
            {
                var src = Path.Combine(dir, folder);
                if (!Directory.Exists(src)) continue;
                foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(AppPaths.DataDir, folder, Path.GetRelativePath(src, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(f, target, true);
                }
            }
            return from;
        }
        catch { return null; }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
