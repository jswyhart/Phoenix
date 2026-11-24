using System.IO;
using System.IO.Enumeration;

namespace Halcyon.Core;

/// <summary>A file found while crawling, with the timestamp and size the directory listing already carried.</summary>
public readonly record struct FoundFile(string Path, long Modified, long Size);

/// <summary>
/// Walks a folder tree once and returns the matching files together with their modified time and size. The listing
/// itself supplies those (no separate stat call per file), which is what makes re-checking a large library fast.
/// </summary>
public static class Crawler
{
    public static List<FoundFile> Crawl(string folder, HashSet<string> extensions)
    {
        if (!Directory.Exists(folder)) return new List<FoundFile>();
        var lookup = extensions.GetAlternateLookup<ReadOnlySpan<char>>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        var files = new FileSystemEnumerable<FoundFile>(folder,
            (ref FileSystemEntry e) => new FoundFile(e.ToFullPath(), e.LastWriteTimeUtc.UtcTicks, e.Length), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && lookup.Contains(Path.GetExtension(e.FileName))
        };
        return files.ToList();
    }

    /// <summary>Crawls several folders; a file reachable from more than one is returned once.</summary>
    public static List<FoundFile> Crawl(IEnumerable<string> folders, HashSet<string> extensions) =>
        folders.SelectMany(f => Crawl(f, extensions)).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
}
