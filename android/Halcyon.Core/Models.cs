using System.ComponentModel;
using System.Runtime.CompilerServices;

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

public static class Fmt
{
    public static string Time(long ms)
    {
        if (ms <= 0) return "";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
    public static string Clock(long ms) => ms <= 0 ? "0:00" : Time(ms);
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    /// <summary>How long ago a moment was, in words: "just now", "5 minutes ago", "yesterday", "3 days ago".</summary>
    public static string Ago(long unixSeconds)
    {
        var s = Math.Max(0, Now - unixSeconds);
        return s < 60 ? "just now" : s < 3600 ? Plural((int)(s / 60), "minute") + " ago" : s < 86400 ? Plural((int)(s / 3600), "hour") + " ago"
             : s < 172800 ? "yesterday" : s < 60 * 86400 ? $"{s / 86400} days ago" : DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime.ToString("d MMM yyyy");
    }
    public static string Plural(int n, string noun) => n == 0 ? "" : $"{n} {noun}{(n == 1 ? "" : "s")}";
    public static string Join(params string[] parts) => string.Join("  ·  ", parts.Where(s => s != ""));

    /// <summary>A long stretch of time in words: "2 days 7 hours", "3 hours 5 minutes", "1 minute".</summary>
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
    /// <summary>Set once at startup (the app's private files directory on Android).</summary>
    public static string DataDir { get; set; } = Path.Combine(Path.GetTempPath(), "Halcyon");
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
    public string TagText => App.Lib.TagText(Library.SongTag, Id);   // your own tags, for the song list
    public int Rating { get; set; }                    // 1 like, -1 dislike, 0 unrated
    public long RatedAt { get; set; }                  // when the rating was last changed
    public int PlayCount { get; set; }
    public long LastPlayed { get; set; }

    public bool IsLocal => Source == "local";
    // Server tracks group by the server's own album unless the user regrouped them here.
    public string AlbumKey => GroupId != "" && !Edited
        ? $"{Source}\u001fid\u001f{GroupId}"
        : $"{Source}\u001f{AlbumArtist.ToLowerInvariant()}\u001f{Album.ToLowerInvariant()}";
    public string DurationText => Fmt.Time(DurationMs);
    public string TrackNoText => TrackNo > 0 ? TrackNo.ToString() : "";
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
    public string Subtitle => Fmt.Join(Fmt.Time(DurationMs), Source == "local" ? "" : Source);
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
    /// <summary>The downloaded file, if the episode has been downloaded ("" if not).</summary>
    public string File { get; set; } = "";
    /// <summary>Downloaded by hand: kept until played or removed by hand, whatever the settings say.</summary>
    public bool Manual { get; set; }
    public string DateText => Published > 0 ? DateTimeOffset.FromUnixTimeSeconds(Published).LocalDateTime.ToString("MMM d, yyyy") : "";
    public string DurationText => Fmt.Time(DurationMs);
}

public record Pin(string Kind, string Key, string Title);

public class PlayItem : Observable
{
    /// <summary>For a podcast episode: remember where its redirects led, so a fallback download fetches that address directly.</summary>
    public PlayItem WithResolvedDownload()
    {
        var resolve = Resolve!;
        var original = Url;
        string? real = null;
        Resolve = async () => real = await resolve();
        FallbackAsync = () => Halcyon.Services.PodcastService.DownloadAsync(real ?? original);
        return this;
    }

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
    public int BookPart { get; set; }   // which file of the audiobook this is
    /// <summary>A radio station: an endless stream with no length.</summary>
    public Station? Station { get; set; }
    /// <summary>This listen has been written to the play log (so it is not written twice).</summary>
    public bool Logged { get; set; }
    /// <summary>Works out the stream URL when the item actually starts (server items: direct play or a fresh conversion session).</summary>
    public Func<Task<string>>? Resolve { get; set; }
    /// <summary>A second way to play the item, tried once when the first URL fails (e.g. a server-converted stream).</summary>
    public Func<string>? Fallback { get; set; }
    /// <summary>Like <see cref="Fallback"/> but needs to do work first (e.g. download the file); tried once when the first URL fails.</summary>
    public Func<Task<string>>? FallbackAsync { get; set; }
    /// <summary>For streams the server transcodes on the fly (not seekable): builds a URL that starts at the given offset.</summary>
    public Func<long, string>? SeekUrl { get; set; }
    bool _isCurrent;
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
    public string DurationText => Fmt.Time(DurationMs);
}
