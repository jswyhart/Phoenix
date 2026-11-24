using System.IO;
using System.Xml.Linq;
using SharpCompress.Archives;

namespace Halcyon.Core;

/// <summary>Comic book archives: .cbz (zip) and .cbr (rar) are folders of page pictures, read in file-name order.</summary>
public static class Comic
{
    public static readonly HashSet<string> PageExts = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };

    public record Info(string Title, string Series, string Author, int Pages);

    static string Natural(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.Replace('\\', '/'), @"\d+", m => m.Value.PadLeft(8, '0'));

    static List<IArchiveEntry> PageEntries(IArchive a) =>
        a.Entries.Where(e => !e.IsDirectory && PageExts.Contains(Path.GetExtension(e.Key ?? "")) && !(e.Key ?? "").Contains("__MACOSX"))
            .OrderBy(e => Natural(e.Key ?? ""), StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Title, series, author (from ComicInfo.xml when the archive has one), page count and the first page as the cover.</summary>
    public static (Info info, byte[]? cover) Read(string file)
    {
        using var a = ArchiveFactory.OpenArchive(file);
        var pages = PageEntries(a);
        if (pages.Count == 0) throw new InvalidDataException("The archive has no pages.");
        string title = "", series = "", author = "";
        var meta = a.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.Key ?? ""), "ComicInfo.xml", StringComparison.OrdinalIgnoreCase));
        if (meta != null)
        {
            try
            {
                using var s = meta.OpenEntryStream();
                var x = XDocument.Load(s).Root;
                string V(string n) => ((string?)x?.Element(n))?.Trim() ?? "";
                series = V("Series");
                var number = V("Number");
                title = V("Title");
                if (title == "" && series != "") title = number != "" ? $"{series} #{number}" : series;
                author = V("Writer");
            }
            catch { }
        }
        byte[]? cover = null;
        try
        {
            using var s = pages[0].OpenEntryStream();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            cover = ms.ToArray();
        }
        catch { }
        return (new Info(title, series, author, pages.Count), cover);
    }

    /// <summary>Writes the pages to <paramref name="dir"/> as 00001.jpg, 00002.png… and returns them in reading order.</summary>
    public static List<string> Extract(string file, string dir)
    {
        Directory.CreateDirectory(dir);
        using var a = ArchiveFactory.OpenArchive(file);
        var result = new List<string>();
        var n = 0;
        foreach (var e in PageEntries(a))
        {
            var target = Path.Combine(dir, $"{++n:00000}{Path.GetExtension(e.Key ?? ".jpg").ToLowerInvariant()}");
            using (var src = e.OpenEntryStream())
            using (var dst = File.Create(target))
                src.CopyTo(dst);
            result.Add(target);
        }
        return result;
    }

    /// <summary>The pages already extracted for this archive, extracting them first if needed.</summary>
    public static List<string> Pages(string file, string dir)
    {
        var stamp = Path.Combine(dir, ".extracted");
        var signature = $"{file}|{File.GetLastWriteTimeUtc(file).Ticks}";
        if (File.Exists(stamp) && File.ReadAllText(stamp) == signature)
        {
            var existing = Directory.EnumerateFiles(dir).Where(f => PageExts.Contains(Path.GetExtension(f))).OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (existing.Count > 0) return existing;
        }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var pages = Extract(file, dir);
        File.WriteAllText(stamp, signature);
        return pages;
    }
}
