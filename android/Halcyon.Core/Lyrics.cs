using System.IO;
using System.Text.RegularExpressions;

namespace Halcyon.Core;

// Shared with the Windows app (Core\Lyrics.cs).

public class LyricLine : Observable
{
    public long Ms { get; init; } = -1;     // when the line is sung; -1 when the lyrics carry no timing
    public string Text { get; init; } = "";
    bool _now;
    public bool IsNow { get => _now; set { if (Set(ref _now, value)) Raise(); } }
    public double Opacity => IsNow ? 1.0 : 0.5;
}

/// <summary>
/// Song lyrics from the song's own files: a .lrc or .txt file beside it with the same name, or lyrics stored in its
/// tags. Lines with [mm:ss.xx] times follow along with the music.
/// </summary>
public class Lyrics
{
    public List<LyricLine> Lines { get; } = new();
    public bool Synced { get; private set; }
    public string Source { get; private set; } = "";

    static readonly Regex Stamp = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    static readonly Regex MetaTag = new(@"^\[[a-zA-Z#]+:.*\]$", RegexOptions.Compiled);
    static readonly Regex WordStamp = new(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);

    /// <summary>Where lyrics you imported (found online, from a file, or pasted) are kept for a song.</summary>
    public static string StorePath(Track t)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(t.Source + "|" + t.SourceId)))[..24];
        return Path.Combine(AppPaths.DataDir, "lyrics", key + ".lrc");
    }

    public static bool HasStored(Track t) => File.Exists(StorePath(t));

    public static void Save(Track t, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath(t))!);
        File.WriteAllText(StorePath(t), text);
    }

    public static void Remove(Track t) { try { File.Delete(StorePath(t)); } catch { } }

    // ---------- lyrics the speech model wrote: a tab of their own, apart from the song's lyrics ----------

    public const string GeneratedMark = "[re:Phoenix speech model";
    public static string GeneratedPath(Track t) => Path.ChangeExtension(StorePath(t), ".generated.lrc");
    public static bool HasGenerated(Track t) => File.Exists(GeneratedPath(t));

    public static void SaveGenerated(Track t, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GeneratedPath(t))!);
        File.WriteAllText(GeneratedPath(t), text);
    }

    public static void RemoveGenerated(Track t) { try { File.Delete(GeneratedPath(t)); } catch { } }

    /// <summary>The lyrics the speech model wrote for a song, if it has.</summary>
    public static Lyrics? Generated(Track? t)
    {
        if (t == null) return null;
        try
        {
            if (!HasGenerated(t)) { For(t); if (!HasGenerated(t)) return null; }   // (For moves older ones over)
            var text = File.ReadAllText(GeneratedPath(t));
            var model = text.StartsWith(GeneratedMark) ? text[GeneratedMark.Length..].Split(']')[0].Trim() : "";
            return Parse(text, $"written by the speech model{(model != "" ? $" ({model})" : "")}; it may mishear");
        }
        catch { return null; }
    }

    public static Lyrics? For(Track? t)
    {
        if (t == null) return null;
        try
        {
            // lyrics you imported come first, and work for songs on a server too
            if (HasStored(t) && File.ReadAllText(StorePath(t)) is var kept)
            {
                // the speech model's lyrics were kept here once: they belong with the generated ones now
                if (kept.StartsWith(GeneratedMark)) { if (!HasGenerated(t)) SaveGenerated(t, kept); Remove(t); }
                else if (Parse(kept, "imported") is { } stored) return stored;
            }
            if (!t.IsLocal) return null;
            var file = t.Location;
            foreach (var ext in new[] { ".lrc", ".txt" })
            {
                var side = Path.ChangeExtension(file, ext);
                if (File.Exists(side) && new FileInfo(side).Length < 400_000 && Parse(File.ReadAllText(side), Path.GetFileName(side)) is { } fromFile) return fromFile;
            }
            if (!File.Exists(file)) return null;
            using var tags = TagLib.File.Create(file);
            return Parse(tags.Tag.Lyrics ?? "", "from the song's tags");
        }
        catch { return null; }
    }

    public static Lyrics? Parse(string text, string source)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var result = new Lyrics { Source = source };
        var timed = new List<LyricLine>();
        var plain = new List<LyricLine>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            var stamps = Stamp.Matches(line);
            if (stamps.Count == 0)
            {
                if (MetaTag.IsMatch(line)) continue;   // [ar:Artist], [ti:Title] and the like
                plain.Add(new LyricLine { Text = line });
                continue;
            }
            var words = WordStamp.Replace(Stamp.Replace(line, ""), "").Trim();
            foreach (Match m in stamps)
            {
                var frac = m.Groups[3].Value;
                var ms = frac == "" ? 0 : int.Parse(frac) * (frac.Length == 1 ? 100 : frac.Length == 2 ? 10 : 1);
                timed.Add(new LyricLine { Ms = int.Parse(m.Groups[1].Value) * 60000L + int.Parse(m.Groups[2].Value) * 1000L + ms, Text = words });
            }
        }
        if (timed.Count >= 2)
        {
            result.Synced = true;
            result.Lines.AddRange(timed.OrderBy(l => l.Ms));
        }
        else
        {
            // plain words: keep verse breaks, but not blank lines at the ends or runs of them
            foreach (var l in plain)
                if (l.Text != "" || (result.Lines.Count > 0 && result.Lines[^1].Text != "")) result.Lines.Add(l);
            while (result.Lines.Count > 0 && result.Lines[^1].Text == "") result.Lines.RemoveAt(result.Lines.Count - 1);
        }
        return result.Lines.Any(l => l.Text != "") ? result : null;
    }

    /// <summary>The line being sung at the given moment, or -1 before the first one (or with no timing).</summary>
    public int LineAt(long ms)
    {
        if (!Synced) return -1;
        int lo = 0, hi = Lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Lines[mid].Ms <= ms) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return found;
    }
}
