using Halcyon.Core;

namespace Phoenix.Backend;

/// <summary>
/// Moving the data folder, as on Windows: everything Phoenix keeps is copied to a folder you choose, Phoenix starts
/// again from there, and what it left in the old place is removed once the new start went well. The choice is noted
/// in data-folder.txt in the standard place, which the window reads at start.
/// </summary>
public static partial class Api
{
    /// <summary>The standard data folder (the window passes it; the data folder may be elsewhere).</summary>
    public static string StandardDir = "";
    static string Pointer => Path.Combine(StandardDir, "data-folder.txt");
    // the browser engine's caches are made again; its saved sign-ins (Partitions) are kept
    static readonly string[] SkipDirs = { "Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache", "ShaderCache", "Crashpad" };

    static object StorageState()
    {
        long size = 0;
        try { size = new DirectoryInfo(DataDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
        return new
        {
            folder = DataDir, standard = StandardDir,
            isStandard = StandardDir != "" && string.Equals(Path.GetFullPath(DataDir).TrimEnd('/', '\\'), Path.GetFullPath(StandardDir).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase),
            used = size >= 1 << 30 ? $"{size / (double)(1 << 30):0.0} GB used" : $"{size / (double)(1 << 20):0} MB used"
        };
    }

    static int CopyData(string from, string to)
    {
        var bad = 0;
        foreach (var file in Directory.EnumerateFiles(from, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            var rel = Path.GetRelativePath(from, file);
            var parts = rel.Split('/', '\\');
            if (parts.Any(p => SkipDirs.Contains(p)) || parts[^1].StartsWith("library.db") || parts[^1] == "data-folder.txt" || parts[^1].EndsWith(".log")) continue;
            try
            {
                var dest = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, true);
            }
            catch { bad++; }   // a file in use (a browser sign-in): it may need doing again, nothing else is lost
        }
        return bad;
    }

    /// <summary>At start: the move before this one went well, so the old folder's copy is removed.</summary>
    static void CleanUpOldData()
    {
        try
        {
            if (Program.SmokeMode || StandardDir == "" || !File.Exists(Pointer)) return;
            var lines = File.ReadAllLines(Pointer);
            var old = lines.Where(l => l.StartsWith("cleanup:")).Select(l => l[8..].Trim()).FirstOrDefault();
            if (string.IsNullOrEmpty(old) || !Directory.Exists(old) || string.Equals(Path.GetFullPath(old), Path.GetFullPath(DataDir), StringComparison.OrdinalIgnoreCase)) return;
            foreach (var entry in Directory.EnumerateFileSystemEntries(old))
            {
                var name = Path.GetFileName(entry);
                if (name == "data-folder.txt") continue;   // the note in the standard place stays
                try { if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry); } catch { }
            }
            File.WriteAllLines(Pointer, lines.Where(l => !l.StartsWith("cleanup:")));
        }
        catch { }
    }

    static void RegisterStorage()
    {
        CleanUpOldData();
        Rpc.On("storage.state", _ => StorageState());
        // copies everything to the folder chosen (or uses the library already there), notes it for the next start
        Rpc.On("storage.move", async p =>
        {
            var target = Path.GetFullPath(p.Str("folder")).TrimEnd('/', '\\');
            var from = Path.GetFullPath(DataDir).TrimEnd('/', '\\');
            if (string.Equals(target, from, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("That is the folder in use now.");
            if (target.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose a folder that is not inside the current data folder.");
            try { Directory.CreateDirectory(target); var probe = Path.Combine(target, ".phoenix-test"); File.WriteAllText(probe, ""); File.Delete(probe); }
            catch { throw new InvalidOperationException("Phoenix can't write to that folder."); }
            var adopt = File.Exists(Path.Combine(target, "library.db"));
            var failed = 0;
            if (!adopt)
            {
                S.Save();
                Rpc.Emit("storage.progress", "copying…");
                failed = await Task.Run(() => CopyData(from, target));
                Rpc.Emit("storage.progress", "copying the library…");
                Lib.Db.CopyTo(Path.Combine(target, "library.db"));   // a clean, complete copy even while the library is open
            }
            // the self-test copies but never changes where the real Phoenix keeps its data
            if (!Program.SmokeMode && StandardDir != "")
            {
                Directory.CreateDirectory(StandardDir);
                var isStandard = string.Equals(target, Path.GetFullPath(StandardDir).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
                var lines = new List<string>();
                if (!isStandard) lines.Add(target);
                if (!adopt) lines.Add("cleanup:" + from);
                File.WriteAllLines(Pointer, lines);
            }
            return new { adopted = adopt, failed };
        });
    }
}
