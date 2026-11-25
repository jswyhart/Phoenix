using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Halcyon.Core;

namespace Halcyon.Services;

/// <summary>
/// Finding the same song stored twice, and songs whose files have gone. Duplicates are found by name and length;
/// with Chromaprint's fpcalc (the same program "identify by sound" uses) they can also be found, or confirmed, by
/// what they sound like, which catches copies whose names differ.
/// </summary>
public static class LibraryCare
{
    public class DupGroup
    {
        public List<Track> Tracks = new();
        /// <summary>Why these are thought to be the same song.</summary>
        public string Why = "";
        /// <summary>The copy worth keeping: the best quality, then the most played.</summary>
        public Track Best => Tracks.OrderByDescending(Quality).ThenByDescending(t => t.Rating).ThenByDescending(t => t.PlayCount).ThenBy(t => t.DateAdded).First();
    }

    static long Size(Track t) { try { return new FileInfo(t.SourceId).Length; } catch { return 0; } }

    /// <summary>A rough measure of a file's sound quality: lossless first, then the higher bitrate.</summary>
    public static double Quality(Track t)
    {
        var ext = Path.GetExtension(t.SourceId).ToLowerInvariant();
        var kbps = t.DurationMs > 0 ? Size(t) * 8.0 / t.DurationMs : 0;
        return (ext is ".flac" or ".wav" ? 100_000 : 0) + kbps * (ext is ".m4a" or ".aac" or ".opus" or ".ogg" ? 1.3 : 1);
    }

    public static string Describe(Track t)
    {
        var size = Size(t);
        var kbps = t.DurationMs > 0 && size > 0 ? $"{size * 8.0 / t.DurationMs:0} kbps" : "";
        return string.Join("  ·  ", new[] { Path.GetExtension(t.SourceId).TrimStart('.').ToUpperInvariant(), kbps, size > 0 ? $"{size / 1048576.0:0.0} MB" : "file missing",
            t.DurationText, t.PlayCount > 0 ? $"played {t.PlayCount}×" : "", t.Rating > 0 ? "liked" : "" }.Where(s => s != ""));
    }

    static List<Track> Local => App.Lib.Tracks.Where(t => t.IsLocal).ToList();

    /// <summary>Songs with the same artist and title whose lengths are within three seconds of each other.</summary>
    public static List<DupGroup> ByName()
    {
        var groups = new List<DupGroup>();
        foreach (var same in Local.Where(t => t.Title.Trim() != "").GroupBy(t => Metadata.Norm(t.Artist) + "\u001f" + Metadata.Norm(t.Title)).Where(g => g.Count() > 1 && !g.Key.EndsWith('\u001f')))
        {
            // within the same name, songs of clearly different length are different recordings (a live version, a single edit)
            var byLength = same.OrderBy(t => t.DurationMs).ToList();
            var run = new List<Track> { byLength[0] };
            void Close()
            {
                if (run.Count < 2) return;
                // the same song on an album and on a collection is worth showing, but it may well be wanted in both
                var albums = run.Select(t => t.AlbumKey).Distinct().Count();
                groups.Add(new DupGroup { Tracks = run.ToList(), Why = albums > 1 ? "same artist, title and length, on different albums" : "same artist, title and length" });
            }
            for (var i = 1; i < byLength.Count; i++)
            {
                if (byLength[i].DurationMs - run[^1].DurationMs <= 3000) { run.Add(byLength[i]); continue; }
                Close();
                run = new List<Track> { byLength[i] };
            }
            Close();
        }
        return groups.OrderBy(g => g.Tracks[0].Artist, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Tracks[0].Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------- by sound ----------

    public static bool CanFingerprint => Metadata.FpcalcPath() != "";

    /// <summary>The Chromaprint fingerprint of a song's first two minutes, from the library if it was taken before.</summary>
    public static async Task<uint[]?> FingerprintAsync(Track t)
    {
        if (App.Lib.Db.Fingerprint(t.Id, t.FileModified) is { } stored) return Unpack(stored.Print);
        if (!File.Exists(t.SourceId)) return null;
        try
        {
            var info = new ProcessStartInfo(Metadata.FpcalcPath()) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-raw", "-json", "-length", "120", t.SourceId }) info.ArgumentList.Add(a);
            using var p = Process.Start(info);
            if (p == null) return null;
            var text = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            var print = ParseRaw(text);
            if (print == null || print.Length < 30) return null;
            App.Lib.Db.SaveFingerprint(t.Id, t.FileModified, (int)(t.DurationMs / 1000), Pack(print));
            return print;
        }
        catch { return null; }
    }

    public static uint[]? ParseRaw(string json)
    {
        try
        {
            var r = J.Parse(json);
            var a = r.A("fingerprint");
            return a.Length == 0 ? null : a.Select(x => x.TryGetUInt32(out var u) ? u : unchecked((uint)x.GetInt64())).ToArray();
        }
        catch { return null; }
    }

    static byte[] Pack(uint[] print) { var b = new byte[print.Length * 4]; Buffer.BlockCopy(print, 0, b, 0, b.Length); return b; }
    static uint[] Unpack(byte[] b) { var p = new uint[b.Length / 4]; Buffer.BlockCopy(b, 0, p, 0, p.Length * 4); return p; }

    /// <summary>
    /// How alike two fingerprints are, 0 to 1: the share of matching bits at the best alignment (one copy may start a
    /// moment later than the other). Two encodings of one recording score about 0.9 or more; different songs about 0.5.
    /// </summary>
    public static double Similarity(uint[] a, uint[] b)
    {
        double best = 0;
        for (var shift = -24; shift <= 24; shift++)
        {
            int ia = Math.Max(0, shift), ib = Math.Max(0, -shift), n = Math.Min(a.Length - ia, b.Length - ib);
            if (n < 40) continue;
            long wrong = 0;
            for (var i = 0; i < n; i++) wrong += BitOperations.PopCount(a[ia + i] ^ b[ib + i]);
            best = Math.Max(best, 1 - wrong / (32.0 * n));
        }
        return best;
    }

    /// <summary>Listens to the copies in a group and says whether they really are the same recording.</summary>
    public static async Task ConfirmAsync(DupGroup g)
    {
        var prints = new List<uint[]?>();
        foreach (var t in g.Tracks) prints.Add(await FingerprintAsync(t));
        if (prints.Any(p => p == null)) { g.Why += "  ·  a file could not be listened to"; return; }
        var worst = 1.0;
        for (var i = 1; i < prints.Count; i++) worst = Math.Min(worst, Similarity(prints[0]!, prints[i]!));
        g.Why = worst >= 0.85 ? $"the same recording ({worst:P0} alike by sound)" : $"same name, but they sound different ({worst:P0} alike): probably different versions";
    }

    /// <summary>Listens to every song on this PC and groups the ones that are the same recording, whatever they are called.</summary>
    public static async Task<List<DupGroup>> BySoundAsync(Action<int, int> progress, CancellationToken stop)
    {
        var tracks = Local.Where(t => t.DurationMs > 20_000).OrderBy(t => t.DurationMs).ToList();
        var prints = new uint[]?[tracks.Count];
        var done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, tracks.Count), new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 6), CancellationToken = stop }, async (i, _) =>
        {
            prints[i] = await FingerprintAsync(tracks[i]);
            progress(Interlocked.Increment(ref done), tracks.Count);
        });
        // songs sorted by length: only neighbours within a few seconds can be the same recording
        var parent = Enumerable.Range(0, tracks.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var alike = new Dictionary<int, double>();
        await Task.Run(() =>
        {
            for (var i = 0; i < tracks.Count && !stop.IsCancellationRequested; i++)
            {
                if (prints[i] == null) continue;
                for (var j = i + 1; j < tracks.Count && tracks[j].DurationMs - tracks[i].DurationMs <= 4000; j++)
                {
                    if (prints[j] == null) continue;
                    var s = Similarity(prints[i]!, prints[j]!);
                    if (s < 0.85) continue;
                    int a = Find(i), b = Find(j);
                    if (a != b) parent[b] = a;
                    alike[Find(i)] = Math.Min(alike.GetValueOrDefault(Find(i), 1), s);
                }
            }
        }, stop);
        return Enumerable.Range(0, tracks.Count).GroupBy(Find).Where(g => g.Count() > 1)
            .Select(g => new DupGroup { Tracks = g.Select(i => tracks[i]).ToList(), Why = $"the same recording ({alike.GetValueOrDefault(g.Key, 0.85):P0} alike by sound)" })
            .OrderBy(g => g.Tracks[0].Artist, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Tracks[0].Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Keeps one copy and removes the others from the library, passing their plays, rating and playlist places to the
    /// one kept. With <paramref name="recycle"/> the removed files go to the Recycle Bin. Returns files that could not be moved there.
    /// </summary>
    public static int Resolve(DupGroup g, Track keep, bool recycle)
    {
        var failed = 0;
        var gone = g.Tracks.Where(t => !ReferenceEquals(t, keep)).ToList();
        foreach (var t in gone)
        {
            App.Lib.Absorb(keep, t);
            if (!recycle || !File.Exists(t.SourceId)) continue;
            try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(t.SourceId, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); }
            catch { failed++; }
        }
        keep.Raise();
        App.Lib.RemoveTracks(gone);
        return failed;
    }

    // ---------- missing files ----------

    /// <summary>Songs in the library whose file is not where it was.</summary>
    public static List<Track> Missing() => Local.Where(t => !File.Exists(t.SourceId)).OrderBy(t => t.SourceId, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// For missing songs, looks through the music folders for a file of the same name that the library does not know
    /// yet (a folder that was renamed or moved). Returns where each one was found.
    /// </summary>
    public static Dictionary<Track, string> Locate(List<Track> missing)
    {
        var found = new Dictionary<Track, string>();
        if (missing.Count == 0) return found;
        var known = Local.Select(t => t.SourceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byName = Crawler.Crawl(App.Settings.MusicFolders.ToList(), Library.AudioExts).Select(f => f.Path).Where(p => !known.Contains(p))
            .GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in missing)
        {
            if (!byName.TryGetValue(Path.GetFileName(t.SourceId), out var places)) continue;
            // several files of that name: prefer the one whose folder is named like the old one
            var oldFolder = Path.GetFileName(Path.GetDirectoryName(t.SourceId) ?? "");
            var pick = places.Where(p => !taken.Contains(p)).OrderByDescending(p => string.Equals(Path.GetFileName(Path.GetDirectoryName(p) ?? ""), oldFolder, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            if (pick == null) continue;
            taken.Add(pick);
            found[t] = pick;
        }
        return found;
    }
}
