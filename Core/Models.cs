using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Halcyon.Core;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
    /// <summary>Signals that every property changed.</summary>
    public void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}

public class RelayCommand(Action<object?> run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run(parameter);
}

public static class Fmt
{
    public static string Time(long ms)
    {
        if (ms <= 0) return "";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>A long stretch of time in words: "2 days 7 hours", "5 hours 12 minutes", "48 minutes".</summary>
    public static string Long(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        static string N(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";
        if (t.TotalDays >= 1) return t.Hours > 0 ? $"{N((int)t.TotalDays, "day")} {N(t.Hours, "hour")}" : N((int)t.TotalDays, "day");
        if (t.TotalHours >= 1) return t.Minutes > 0 ? $"{N((int)t.TotalHours, "hour")} {N(t.Minutes, "minute")}" : N((int)t.TotalHours, "hour");
        return N((int)t.TotalMinutes, "minute");
    }
}

public static class AppPaths
{
    // read from the note below while the data folder is being worked out, so they are declared first
    static readonly List<string> Was = new(), Cleanup = new();
    static string? _missing;

    /// <summary>A data folder given on the environment (PHOENIX_DATA, or the older HALCYON_DATA), for tests.</summary>
    public static readonly string? Override = Environment.GetEnvironmentVariable("PHOENIX_DATA") ?? Environment.GetEnvironmentVariable("HALCYON_DATA");
    public static readonly string DataDir = Override ?? Default();
    /// <summary>Where the data lived when the app was called Halcyon.</summary>
    public static string OldDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halcyon");
    /// <summary>The standard data folder, %LocalAppData%\Phoenix. It always holds the note saying where the data is, if it was moved.</summary>
    public static string StandardDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Phoenix");
    public static string PointerFile => Path.Combine(Override ?? StandardDir, "data-folder.txt");
    /// <summary>Folders the data used to be in; paths recorded under them are pointed at the current folder.</summary>
    public static IReadOnlyList<string> FormerRoots => Was;
    /// <summary>Old folders whose copies are removed once a start from the new folder has gone well.</summary>
    public static IReadOnlyList<string> CleanupRoots => Cleanup;
    /// <summary>The chosen data folder, when it could not be found at start (a drive that is not connected).</summary>
    public static string? ChosenMissing => _missing;

    /// <summary>
    /// The folder noted in data-folder.txt if you chose one, else %LocalAppData%\Phoenix. The app used to be called
    /// Halcyon: a library left in that folder is moved over the first time, and used where it is if it cannot be moved.
    /// </summary>
    static string Default()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var now = Path.Combine(local, "Phoenix");
        var before = Path.Combine(local, "Halcyon");
        var standard = now;
        if (!Directory.Exists(now) && Directory.Exists(before))
        {
            try { Directory.Move(before, now); }
            catch { standard = before; }
        }
        try
        {
            var note = Path.Combine(now, "data-folder.txt");
            if (File.Exists(note))
            {
                var chosen = "";
                foreach (var line in File.ReadAllLines(note).Select(l => l.Trim()).Where(l => l != ""))
                {
                    if (line.StartsWith("was=")) Was.Add(line[4..]);
                    else if (line.StartsWith("cleanup=")) Cleanup.Add(line[8..]);
                    else if (line.StartsWith("folder=")) chosen = line[7..];
                }
                if (chosen != "")
                {
                    if (Directory.Exists(chosen)) return chosen;
                    _missing = chosen;
                }
            }
        }
        catch { }
        return standard;
    }

    /// <summary>Writes the note that says where the data is ("" = the standard folder).</summary>
    public static void WritePointer(string chosen, IEnumerable<string> was, IEnumerable<string> cleanup)
    {
        var lines = new List<string> { "This file tells Phoenix where its library, settings, covers and caches are kept. Change it from settings, storage." };
        if (chosen != "") lines.Add("folder=" + chosen);
        lines.AddRange(was.Select(w => "was=" + w));
        lines.AddRange(cleanup.Select(c => "cleanup=" + c));
        Directory.CreateDirectory(Path.GetDirectoryName(PointerFile)!);
        File.WriteAllLines(PointerFile, lines);
    }

    public static string ArtDir => Path.Combine(DataDir, "art");
    public static string ImgCacheDir => Path.Combine(DataDir, "imgcache");
    public static string DbPath => Path.Combine(DataDir, "library.db");
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
}

public class Track : Observable
{
    public long Id { get; set; }
    public string Source { get; set; } = "local";      // local | jellyfin | plex
    public string SourceId { get; set; } = "";         // file path or remote item id
    public string Location { get; set; } = "";         // file path, jellyfin item id, or plex part key
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Genre { get; set; } = "";
    public int Year { get; set; }
    public int TrackNo { get; set; }
    public int DiscNo { get; set; }
    public long DurationMs { get; set; }
    public string ArtPath { get; set; } = "";
    public long FileModified { get; set; }
    public long DateAdded { get; set; }
    public string GroupId { get; set; } = "";          // server-side album id for jellyfin/plex tracks
    public bool Edited { get; set; }                   // user changed the info here; a server sync must not overwrite it
    public int Rating { get; set; }                    // 1 like, -1 dislike, 0 unrated
    public long RatedAt { get; set; }                  // when the rating was last changed
    public string TagText => App.Lib.TagText(Library.SongTag, Id);   // your own tags, for the song list
    public int PlayCount { get; set; }
    public long LastPlayed { get; set; }

    public bool IsLocal => Source == "local";
    // Server tracks group by the server's own album unless the user regrouped them here.
    public string AlbumKey => GroupId != "" && !Edited
        ? $"{Source}\u001fid\u001f{GroupId}"
        : $"{Source}\u001f{AlbumArtist.ToLowerInvariant()}\u001f{Album.ToLowerInvariant()}";
    public string DurationText => Fmt.Time(DurationMs);
    public string TrackNoText => TrackNo > 0 ? TrackNo.ToString() : "";
    public string RatingGlyph => Rating > 0 ? "" : Rating < 0 ? "" : "";
    public double RatingOpacity => Rating == 0 ? 0.22 : 1.0;
}

public class Album
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Source { get; set; } = "local";
    public int Year { get; set; }
    public string Genre { get; set; } = "";
    public string ArtPath { get; set; } = "";
    public long DateAdded { get; set; }
    public List<Track> Tracks { get; set; } = new();
    public string Subtitle => Artist;
    public string SourceBadge => Source == "local" ? "" : Source;
}

public class VideoItem : Observable
{
    public long Id { get; set; }
    public string Source { get; set; } = "local";
    public string SourceId { get; set; } = "";
    public string Location { get; set; } = "";
    public string Title { get; set; } = "";
    public long DurationMs { get; set; }
    public string ArtPath { get; set; } = "";
    public long DateAdded { get; set; }
    public string Kind { get; set; } = "";             // movie | episode | video ("" = not classified yet)
    public string Series { get; set; } = "";           // series name for episodes; folder/group name for other videos
    public int Season { get; set; }
    public int Episode { get; set; }
    public int Year { get; set; }
    public string Collection { get; set; } = "";
    public string GroupArt { get; set; } = "";         // series poster
    public bool Edited { get; set; }

    public string EpisodeCode => Kind == "episode" && (Season > 0 || Episode > 0) ? $"S{Season:00}E{Episode:00}" : "";
    public string DisplayTitle => Kind == "episode"
        ? string.Join(" – ", new[] { Series, EpisodeCode, Title }.Where(s => s != ""))
        : Kind == "movie" && Year > 0 ? $"{Title} ({Year})" : Title;
    public string RowTitle => Kind == "episode" && Episode > 0 ? $"{Episode}. {Title}" : Title;
    public string DurationText => Fmt.Time(DurationMs);
    public string Subtitle => string.Join("  ·  ", new[] { Fmt.Time(DurationMs), Source == "local" ? "" : Source }.Where(s => s != ""));
}

public class Playlist
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public List<long> TrackIds { get; set; } = new();
}

public class Podcast
{
    public long Id { get; set; }
    public string FeedUrl { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string ArtUrl { get; set; } = "";
    /// <summary>Episodes that arrived since you last listened and have not been played.</summary>
    public int NewCount { get; set; }
    public string NewText => NewCount > 0 ? $"{NewCount} new" : "";
}

public class Episode : Observable
{
    public long Id { get; set; }
    public long PodcastId { get; set; }
    public string Guid { get; set; } = "";
    public string Title { get; set; } = "";
    public long Published { get; set; }
    public string Url { get; set; } = "";
    public long DurationMs { get; set; }
    public bool Played { get; set; }
    public long PositionMs { get; set; }
    public string Description { get; set; } = "";
    public bool IsVideo { get; set; }
    public bool Fresh { get; set; }                     // arrived in a refresh and not played yet
    public string File { get; set; } = "";              // downloaded copy, if any
    public bool Manual { get; set; }                    // downloaded by hand: kept until played or removed by hand
    public string StateText => File != "" ? "downloaded" : Fresh && !Played ? "new" : "";
    public string DateText => Published > 0 ? DateTimeOffset.FromUnixTimeSeconds(Published).LocalDateTime.ToString("MMM d, yyyy") : "";
    public string DurationText => Fmt.Time(DurationMs);
    public double TitleOpacity => Played ? 0.45 : 1.0;
    /// <summary>A small film mark in front of a video episode's name.</summary>
    public string KindGlyph => IsVideo ? "\uE714" : "";
    public string KindGap => IsVideo ? "  " : "";
}

public record Pin(string Kind, string Key, string Title);

/// <summary>A playlist that fills itself from rules instead of a fixed list of songs.</summary>
public class SmartPlaylist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Genre { get; set; } = "";     // contains; several separated by commas
    public string Artist { get; set; } = "";    // contains; several separated by commas
    public int YearFrom { get; set; }
    public int YearTo { get; set; }
    public int Rating { get; set; }             // 0 any, 1 liked, 2 not disliked, 3 not rated yet
    public int NotPlayedDays { get; set; }      // only songs not played in this many days (0 = no rule)
    public int AddedDays { get; set; }          // only songs added in the last this many days (0 = no rule)
    public int MinPlays { get; set; }
    public int MaxPlays { get; set; } = -1;     // -1 = no upper limit
    public int Sort { get; set; }               // index into SmartPlaylist.Sorts
    public int Limit { get; set; } = 50;        // 0 = no limit

    public static readonly string[] Ratings = { "any rating", "liked", "not disliked", "not rated yet" };
    public static readonly string[] Sorts = { "random", "most played", "least played", "newest in collection", "longest since played", "artist and album" };
}

public class PlayItem : Observable
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Album { get; set; } = "";
    public string ArtPath { get; set; } = "";
    public string Url { get; set; } = "";
    public bool IsVideo { get; set; }
    public long DurationMs { get; set; }
    public Track? Track { get; set; }
    public Episode? Episode { get; set; }
    public Book? Book { get; set; }
    public int BookPart { get; set; }
    /// <summary>A radio station: an endless stream with no length.</summary>
    public Station? Station { get; set; }
    /// <summary>This listen has been written to the play log (so it is not written twice).</summary>
    public bool Logged { get; set; }
    /// <summary>Podcast episodes: the address after any tracking redirects, and whether we already fell back to downloading the file.</summary>
    public string? Resolved { get; set; }
    public bool Downloaded { get; set; }
    /// <summary>For streams the server transcodes on the fly (not seekable): builds a URL that starts at the given offset.</summary>
    public Func<long, string>? SeekUrl { get; set; }
    bool _isCurrent;
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
    public string DurationText => Fmt.Time(DurationMs);
}

public static class Cmd
{
    public static readonly ICommand Rate = new RelayCommand(o =>
    {
        if (o is not Track t) return;
        App.Lib.Rate(new[] { t }, t.Rating == 0 ? 1 : t.Rating == 1 ? -1 : 0);
    });
}
