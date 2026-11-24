using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

namespace Halcyon.Core;

public record EpubChapter(string Href, string Title);

/// <summary>Reads what a reader needs from an EPUB (a zip of XHTML chapters): title, author, cover, and the reading order.</summary>
public class Epub
{
    public string Title { get; private set; } = "";
    public string Author { get; private set; } = "";
    public byte[]? Cover { get; private set; }
    /// <summary>Chapters in reading order; Href is the path inside the zip.</summary>
    public List<EpubChapter> Chapters { get; } = new();

    static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";
    static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";

    public static Epub Open(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var book = new Epub();

        var containerXml = Load(zip, "META-INF/container.xml") ?? throw new InvalidDataException("Not an EPUB file.");
        var opfPath = (string?)containerXml.Descendants(Container + "rootfile").FirstOrDefault()?.Attribute("full-path")
                      ?? throw new InvalidDataException("EPUB has no package file.");
        var opf = Load(zip, opfPath) ?? throw new InvalidDataException("EPUB package file is missing.");
        var baseDir = Dir(opfPath);

        var meta = opf.Root!.Element(Opf + "metadata");
        book.Title = ((string?)meta?.Element(Dc + "title"))?.Trim() ?? "";
        book.Author = string.Join(", ", meta?.Elements(Dc + "creator").Select(c => c.Value.Trim()).Where(c => c != "") ?? Array.Empty<string>());

        // manifest: id -> (path in zip, media type, properties)
        var manifest = opf.Root.Element(Opf + "manifest")?.Elements(Opf + "item")
            .Where(i => i.Attribute("id") != null && i.Attribute("href") != null)
            .GroupBy(i => (string)i.Attribute("id")!)
            .ToDictionary(g => g.Key, g => (href: Resolve(baseDir, (string)g.First().Attribute("href")!),
                type: (string?)g.First().Attribute("media-type") ?? "", props: (string?)g.First().Attribute("properties") ?? ""))
            ?? new();

        // chapter titles from the EPUB 3 nav document, else the EPUB 2 NCX
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nav = manifest.Values.FirstOrDefault(m => m.props.Split(' ').Contains("nav"));
        if (nav.href != null && Load(zip, nav.href) is { } navDoc)
            foreach (var a in navDoc.Descendants(Xhtml + "a").Where(a => a.Attribute("href") != null))
                titles.TryAdd(Resolve(Dir(nav.href), (string)a.Attribute("href")!), Clean(a.Value));
        var spine = opf.Root.Element(Opf + "spine");
        if (titles.Count == 0 && (string?)spine?.Attribute("toc") is { } tocId && manifest.TryGetValue(tocId, out var toc) && Load(zip, toc.href) is { } ncx)
            foreach (var p in ncx.Descendants(Ncx + "navPoint"))
            {
                var src = (string?)p.Element(Ncx + "content")?.Attribute("src");
                if (src != null) titles.TryAdd(Resolve(Dir(toc.href), src), Clean(p.Element(Ncx + "navLabel")?.Value ?? ""));
            }

        var n = 0;
        foreach (var itemref in spine?.Elements(Opf + "itemref") ?? Enumerable.Empty<XElement>())
        {
            if ((string?)itemref.Attribute("linear") == "no") continue;
            if (!manifest.TryGetValue((string?)itemref.Attribute("idref") ?? "", out var item)) continue;
            n++;
            book.Chapters.Add(new EpubChapter(item.href, titles.TryGetValue(item.href, out var t) && t != "" ? t : $"Section {n}"));
        }

        // cover: EPUB 3 property, EPUB 2 <meta name="cover">, or an image that is plainly named as one
        var coverHref = manifest.Values.FirstOrDefault(m => m.props.Split(' ').Contains("cover-image")).href;
        if (coverHref == null && (string?)meta?.Elements(Opf + "meta").FirstOrDefault(m => (string?)m.Attribute("name") == "cover")?.Attribute("content") is { } coverId
            && manifest.TryGetValue(coverId, out var coverItem)) coverHref = coverItem.href;
        coverHref ??= manifest.Values.FirstOrDefault(m => m.type.StartsWith("image/") && m.href.Contains("cover", StringComparison.OrdinalIgnoreCase)).href;
        if (coverHref != null && Entry(zip, coverHref) is { } entry)
        {
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            book.Cover = ms.ToArray();
        }
        return book;
    }

    /// <summary>Unpacks the book into a folder so a web view can display it.</summary>
    public static void Extract(string path, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var root = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(path);
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith('/')) continue;
            var target = Path.GetFullPath(Path.Combine(destDir, e.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // never write outside the folder
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, true);
        }
    }

    static ZipArchiveEntry? Entry(ZipArchive zip, string name) =>
        zip.GetEntry(name) ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));

    static XDocument? Load(ZipArchive zip, string name)
    {
        var entry = Entry(zip, name);
        if (entry == null) return null;
        using var s = entry.Open();
        try { return XDocument.Load(s); } catch { return null; }
    }

    static string Dir(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    static string Clean(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Turns a relative link into a path inside the zip (drops any #fragment, handles "..").</summary>
    static string Resolve(string baseDir, string href)
    {
        href = Uri.UnescapeDataString(href.Split('#')[0]);
        var parts = new List<string>(baseDir == "" ? Array.Empty<string>() : baseDir.Split('/'));
        foreach (var p in href.Split('/'))
        {
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (p != "." && p != "") parts.Add(p);
        }
        return string.Join("/", parts);
    }
}
