using System.IO;

namespace Halcyon.Core;

/// <summary>
/// A saved place in a book. <see cref="Part"/> is the audiobook file, the ebook chapter or the comic page;
/// <see cref="Position"/> is how far in: milliseconds for an audiobook file, 0..1 through an ebook chapter, 0 for a comic.
/// </summary>
public class Bookmark : Observable
{
    public long Id { get; set; }
    public long BookId { get; set; }
    public int Part { get; set; }
    public double Position { get; set; }
    public long Created { get; set; }

    string _label = "";
    public string Label { get => _label; set => Set(ref _label, value); }

    public string CreatedText => Created > 0 ? DateTimeOffset.FromUnixTimeSeconds(Created).LocalDateTime.ToString("MMM d, yyyy") : "";
}

public partial class Database
{
    bool _bookmarkTable;

    void EnsureBookmarkTable()
    {
        if (_bookmarkTable) return;
        Exec("CREATE TABLE IF NOT EXISTS bookmarks(Id INTEGER PRIMARY KEY, BookId INT NOT NULL, Part INT, Position REAL, Label TEXT, Created INT)");
        Exec("CREATE INDEX IF NOT EXISTS bookmarks_book ON bookmarks(BookId)");
        _bookmarkTable = true;
    }

    public List<Bookmark> LoadBookmarks()
    {
        EnsureBookmarkTable();
        return Query("SELECT Id,BookId,Part,Position,Label,Created FROM bookmarks ORDER BY BookId,Part,Position",
            r => new Bookmark { Id = L(r, 0), BookId = L(r, 1), Part = (int)L(r, 2), Position = D(r, 3), Label = S(r, 4), Created = L(r, 5) });
    }

    public long AddBookmark(Bookmark b)
    {
        EnsureBookmarkTable();
        return Query("INSERT INTO bookmarks(BookId,Part,Position,Label,Created) VALUES($p0,$p1,$p2,$p3,$p4) RETURNING Id",
            r => L(r, 0), b.BookId, b.Part, b.Position, b.Label, b.Created)[0];
    }

    public void RenameBookmark(Bookmark b)
    {
        EnsureBookmarkTable();
        Exec("UPDATE bookmarks SET Label=$p0 WHERE Id=$p1", b.Label, b.Id);
    }

    public void DeleteBookmark(long id)
    {
        EnsureBookmarkTable();
        Exec("DELETE FROM bookmarks WHERE Id=$p0", id);
    }

    public void DeleteBookmarksOf(long bookId)
    {
        EnsureBookmarkTable();
        Exec("DELETE FROM bookmarks WHERE BookId=$p0", bookId);
    }
}

public partial class Library
{
    List<Bookmark>? _bookmarks;

    /// <summary>Raised when a bookmark is added, renamed or removed.</summary>
    public event Action? BookmarksChanged;

    List<Bookmark> AllBookmarks => _bookmarks ??= Db.LoadBookmarks();

    /// <summary>The book's bookmarks in reading order.</summary>
    public List<Bookmark> BookmarksOf(Book b) =>
        AllBookmarks.Where(m => m.BookId == b.Id).OrderBy(m => m.Part).ThenBy(m => m.Position).ToList();

    public int BookmarkCount(Book b) => AllBookmarks.Count(m => m.BookId == b.Id);

    /// <summary>A short description of a place, used when the reader does not supply its own label.</summary>
    public static string DescribePlace(Book b, int part, double position)
    {
        if (b.IsAudio)
        {
            var name = b.Parts.Count > 1 && part >= 0 && part < b.Parts.Count ? b.Parts[part].Title : "";
            var time = Fmt.Time((long)position);
            return string.Join("  ·  ", new[] { name, time == "" ? "start" : time }.Where(s => s != ""));
        }
        if (b.Format == "comic") return $"Page {part + 1}";
        return $"Chapter {part + 1}  ·  {Math.Max(0, (int)(position * 100))}%";
    }

    public Bookmark AddBookmark(Book b, int part, double position, string label = "")
    {
        var all = AllBookmarks;   // load the list first: loading it after the insert would pick the new row up twice
        var m = new Bookmark { BookId = b.Id, Part = part, Position = position, Created = Fmt.Now, Label = label.Trim() != "" ? label.Trim() : DescribePlace(b, part, position) };
        m.Id = Db.AddBookmark(m);
        all.Add(m);
        BookmarksChanged?.Invoke();
        return m;
    }

    public void RenameBookmark(Bookmark m, string label)
    {
        if (label.Trim() == "") return;
        m.Label = label.Trim();
        Db.RenameBookmark(m);
        BookmarksChanged?.Invoke();
    }

    public void DeleteBookmark(Bookmark m)
    {
        Db.DeleteBookmark(m.Id);
        AllBookmarks.Remove(m);
        BookmarksChanged?.Invoke();
    }

    /// <summary>
    /// Makes the book's saved place the bookmark's, so opening (or playing) the book lands there. Returns false when the
    /// book is gone. The reader or player still has to be opened by the caller.
    /// </summary>
    public bool MoveToBookmark(Bookmark m)
    {
        var b = Books.FirstOrDefault(x => x.Id == m.BookId);
        if (b == null) return false;
        b.Finished = false;
        b.Part = m.Part;
        if (b.IsAudio)
        {
            b.PositionMs = (long)m.Position;
            var before = b.Parts.Take(Math.Min(m.Part, b.Parts.Count)).Sum(p => p.DurationMs);
            b.Progress = b.DurationMs > 0 ? Math.Clamp((before + b.PositionMs) / (double)b.DurationMs, 0, 1) : b.Progress;
        }
        else b.Fraction = m.Position;
        b.LastOpened = Fmt.Now;
        Db.SaveBookState(b);
        b.Raise();
        return true;
    }
}
