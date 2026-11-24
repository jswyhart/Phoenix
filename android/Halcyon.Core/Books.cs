using System.IO;
using Microsoft.Data.Sqlite;

namespace Halcyon.Core;

public class BookPart
{
    public string Location { get; set; } = "";   // file path, or a server item location
    public string Title { get; set; } = "";
    public long DurationMs { get; set; }
}

/// <summary>An audiobook (one or more audio files played in order) or an ebook (EPUB or PDF).</summary>
public class Book : Observable
{
    public long Id { get; set; }
    public string Source { get; set; } = "local";   // local | jellyfin
    public string SourceId { get; set; } = "";      // folder or file path, or server id
    public string Kind { get; set; } = "ebook";     // audio | ebook
    public string Format { get; set; } = "";        // epub | pdf | comic | audio
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Series { get; set; } = "";
    public string ArtPath { get; set; } = "";
    public long DurationMs { get; set; }
    public long DateAdded { get; set; }
    public long LastOpened { get; set; }
    public long FileModified { get; set; }
    public bool Edited { get; set; }

    // where the reader/listener stopped
    public int Part { get; set; }           // audiobook file index, or ebook chapter index
    public long PositionMs { get; set; }    // audiobook: position inside that file
    public double Fraction { get; set; }    // ebook: how far through that chapter (0..1)
    public double Progress { get; set; }    // whole book, 0..1
    public bool Finished { get; set; }

    public List<BookPart> Parts { get; set; } = new();

    public bool IsAudio => Kind == "audio";
    public string AuthorOrUnknown => Author == "" ? "Unknown author" : Author;
    public string ProgressText => Finished ? "finished" : Progress > 0.004 ? $"{Math.Max(1, (int)(Progress * 100))}%" : "";
}

public partial class Database
{
    const string BookCols = "Source,SourceId,Kind,Format,Title,Author,Series,ArtPath,DurationMs,DateAdded,FileModified";

    void CreateBookTables()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS books(Id INTEGER PRIMARY KEY, Source TEXT NOT NULL, SourceId TEXT NOT NULL, Kind TEXT, Format TEXT,
                Title TEXT, Author TEXT, Series TEXT, ArtPath TEXT, DurationMs INT, DateAdded INT, FileModified INT, LastOpened INT DEFAULT 0,
                Part INT DEFAULT 0, PositionMs INT DEFAULT 0, Fraction REAL DEFAULT 0, Progress REAL DEFAULT 0, Finished INT DEFAULT 0, Edited INT DEFAULT 0,
                UNIQUE(Source,SourceId));
            CREATE TABLE IF NOT EXISTS book_parts(BookId INT, Idx INT, Location TEXT, Title TEXT, DurationMs INT);
            """);
    }

    public List<Book> LoadBooks()
    {
        var books = Query($"SELECT Id,{BookCols},LastOpened,Part,PositionMs,Fraction,Progress,Finished,Edited FROM books", r => new Book
        {
            Id = L(r, 0), Source = S(r, 1), SourceId = S(r, 2), Kind = S(r, 3), Format = S(r, 4), Title = S(r, 5), Author = S(r, 6), Series = S(r, 7),
            ArtPath = S(r, 8), DurationMs = L(r, 9), DateAdded = L(r, 10), FileModified = L(r, 11), LastOpened = L(r, 12),
            Part = (int)L(r, 13), PositionMs = L(r, 14), Fraction = D(r, 15), Progress = D(r, 16), Finished = L(r, 17) != 0, Edited = L(r, 18) != 0
        });
        var byId = books.ToDictionary(b => b.Id);
        foreach (var (bookId, part) in Query("SELECT BookId,Location,Title,DurationMs FROM book_parts ORDER BY BookId,Idx",
                     r => (L(r, 0), new BookPart { Location = S(r, 1), Title = S(r, 2), DurationMs = L(r, 3) })))
            if (byId.TryGetValue(bookId, out var b)) b.Parts.Add(part);
        return books;
    }

    static double D(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetDouble(i);

    /// <summary>Inserts or refreshes a book and its parts; reading position, date added and the Edited flag survive.</summary>
    public void UpsertBook(Book b) => Batch(() =>
    {
        var cols = BookCols.Split(',');
        b.Id = Query(
            $"INSERT INTO books({BookCols}) VALUES({string.Join(",", cols.Select((_, i) => "$p" + i))}) " +
            $"ON CONFLICT(Source,SourceId) DO UPDATE SET {string.Join(",", cols.Where(c => c is not ("Source" or "SourceId" or "DateAdded")).Select(c => $"{c}=excluded.{c}"))} RETURNING Id",
            r => L(r, 0), b.Source, b.SourceId, b.Kind, b.Format, b.Title, b.Author, b.Series, b.ArtPath, b.DurationMs, b.DateAdded, b.FileModified)[0];
        Exec("DELETE FROM book_parts WHERE BookId=$p0", b.Id);
        for (var i = 0; i < b.Parts.Count; i++)
            Exec("INSERT INTO book_parts(BookId,Idx,Location,Title,DurationMs) VALUES($p0,$p1,$p2,$p3,$p4)", b.Id, i, b.Parts[i].Location, b.Parts[i].Title, b.Parts[i].DurationMs);
    });

    public void SaveBookState(Book b) => Exec(
        "UPDATE books SET Part=$p0,PositionMs=$p1,Fraction=$p2,Progress=$p3,Finished=$p4,LastOpened=$p5 WHERE Id=$p6",
        b.Part, b.PositionMs, b.Fraction, b.Progress, b.Finished ? 1 : 0, b.LastOpened, b.Id);

    public void UpdateBookInfo(Book b) => Exec("UPDATE books SET Title=$p0,Author=$p1,Series=$p2,ArtPath=$p3,Edited=$p4 WHERE Id=$p5",
        b.Title, b.Author, b.Series, b.ArtPath, b.Edited ? 1 : 0, b.Id);

    public void DeleteBook(long id)
    {
        Exec("DELETE FROM books WHERE Id=$p0", id);
        Exec("DELETE FROM book_parts WHERE BookId=$p0", id);
        DeleteBookmarksOf(id);
    }
}

public partial class Library
{
    public static readonly HashSet<string> EbookExts = new(StringComparer.OrdinalIgnoreCase) { ".epub", ".pdf", ".cbz", ".cbr" };
    public List<Book> Books = new();

    /// <summary>
    /// Scans the book folders. An .epub or .pdf is an ebook. An .m4b file is an audiobook on its own; any other audio
    /// files are grouped by folder, one audiobook per folder, played in file-name order.
    /// </summary>
    async Task ScanBooksAsync(Action<string> status)
    {
        var folders = App.Settings.BookFolders.ToList();
        var existing = Books.Where(b => b.Source == "local").ToDictionary(b => b.SourceId, StringComparer.OrdinalIgnoreCase);
        if (folders.Count == 0 && existing.Count == 0) return;
        status("scanning books…");
        await Task.Run(() =>
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changed = new List<Book>();
            var audioDirs = new Dictionary<string, List<FoundFile>>(StringComparer.OrdinalIgnoreCase);

            void Consider(string key, long signature, Func<Book> read)
            {
                seen.Add(key);
                if (existing.TryGetValue(key, out var old) && old.FileModified == signature) return;
                Book b;
                try { b = read(); } catch { return; }   // unreadable file: leave it out rather than fail the scan
                b.Source = "local"; b.SourceId = key; b.FileModified = signature; b.DateAdded = Fmt.Now;
                if (old is { Edited: true }) { b.Title = old.Title; b.Author = old.Author; b.Series = old.Series; }
                Db.UpsertBook(b);
                if (old != null)
                {
                    // same book, refreshed: keep the place
                    b.Part = old.Part; b.PositionMs = old.PositionMs; b.Fraction = old.Fraction; b.Progress = old.Progress;
                    b.Finished = old.Finished; b.LastOpened = old.LastOpened; b.Edited = old.Edited; b.DateAdded = old.DateAdded;
                }
                changed.Add(b);
            }

            foreach (var f in Crawler.Crawl(folders, EbookExts))
                Consider(f.Path, f.Modified, () => ReadEbook(f.Path));

            foreach (var f in Crawler.Crawl(folders, AudioExts))
            {
                if (Path.GetExtension(f.Path).Equals(".m4b", StringComparison.OrdinalIgnoreCase))
                    Consider(f.Path, f.Modified, () => ReadAudiobook(new List<string> { f.Path }, Path.GetFileNameWithoutExtension(f.Path)));
                else
                {
                    var dir = Path.GetDirectoryName(f.Path)!;
                    if (!audioDirs.TryGetValue(dir, out var list)) audioDirs[dir] = list = new List<FoundFile>();
                    list.Add(f);
                }
            }
            foreach (var (dir, files) in audioDirs)
            {
                files.Sort((x, y) => NaturalCompare(x.Path, y.Path));
                var signature = files.Count * 1_000_003L + files.Max(f => f.Modified);
                Consider(dir, signature, () => ReadAudiobook(files.Select(f => f.Path).ToList(), Path.GetFileName(dir)));
            }

            var gone = existing.Values.Where(b => !seen.Contains(b.SourceId) && IsReallyGone(b.SourceId, folders)).ToList();
            foreach (var b in gone) Db.DeleteBook(b.Id);
            if (changed.Count > 0 || gone.Count > 0)
                Ui(() =>
                {
                    var ids = gone.Concat(changed).Select(b => b.Id).ToHashSet();
                    Books = Books.Where(b => !ids.Contains(b.Id)).Concat(changed).ToList();
                    Changed?.Invoke();
                });
        });
    }

    /// <summary>Orders "Chapter 2" before "Chapter 10".</summary>
    static int NaturalCompare(string a, string b)
    {
        static string Key(string s) => System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(s), @"\d+", m => m.Value.PadLeft(8, '0'));
        return string.Compare(Key(a), Key(b), StringComparison.OrdinalIgnoreCase);
    }

    static Book ReadEbook(string file)
    {
        var b = new Book { Kind = "ebook", Title = Path.GetFileNameWithoutExtension(file) };
        if (Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            b.Format = "pdf";
            b.ArtPath = "pdf:" + file;   // the app draws the first page as the cover
            return b;
        }
        if (Path.GetExtension(file) is { } x && (x.Equals(".cbz", StringComparison.OrdinalIgnoreCase) || x.Equals(".cbr", StringComparison.OrdinalIgnoreCase)))
        {
            b.Format = "comic";
            var (info, cover) = Comic.Read(file);
            if (info.Title != "") b.Title = info.Title;
            b.Author = info.Author;
            b.Series = info.Series;
            if (cover is { Length: > 0 })
            {
                var art = ArtCachePath("book", b.Author, b.Title);
                try { File.WriteAllBytes(art, cover); b.ArtPath = art; } catch { }
            }
            return b;
        }
        b.Format = "epub";
        var epub = Epub.Open(file);
        if (epub.Title != "") b.Title = epub.Title;
        b.Author = epub.Author;
        if (epub.Cover is { Length: > 0 })
        {
            var art = ArtCachePath("book", b.Author, b.Title);
            try { File.WriteAllBytes(art, epub.Cover); b.ArtPath = art; } catch { }
        }
        return b;
    }

    static Book ReadAudiobook(List<string> files, string fallbackTitle)
    {
        var b = new Book { Kind = "audio", Format = "audio", Title = fallbackTitle };
        byte[]? cover = null;
        for (var i = 0; i < files.Count; i++)
        {
            var part = new BookPart { Location = files[i], Title = Path.GetFileNameWithoutExtension(files[i]) };
            try
            {
                using var f = TagLib.File.Create(files[i]);
                part.DurationMs = (long)f.Properties.Duration.TotalMilliseconds;
                if (files.Count > 1 && !string.IsNullOrWhiteSpace(f.Tag.Title)) part.Title = f.Tag.Title.Trim();
                if (i == 0)
                {
                    if (!string.IsNullOrWhiteSpace(f.Tag.Album)) b.Title = f.Tag.Album.Trim();
                    else if (files.Count == 1 && !string.IsNullOrWhiteSpace(f.Tag.Title)) b.Title = f.Tag.Title.Trim();
                    b.Author = (f.Tag.FirstAlbumArtist ?? f.Tag.FirstPerformer ?? "").Trim();
                    if (f.Tag.Pictures.Length > 0) cover = f.Tag.Pictures[0].Data.Data;
                }
            }
            catch { }
            b.Parts.Add(part);
        }
        b.DurationMs = b.Parts.Sum(p => p.DurationMs);
        var art = ArtCachePath("book", b.Author, b.Title);
        if (cover is { Length: > 0 })
        {
            try { File.WriteAllBytes(art, cover); b.ArtPath = art; } catch { }
        }
        else
        {
            var dir = Path.GetDirectoryName(files[0])!;
            b.ArtPath = CoverNames.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists) ?? "";
        }
        return b;
    }

    /// <summary>Replaces one server's books with a fresh list, keeping reading positions and local edits.</summary>
    public async Task ReplaceBooksAsync(string source, List<Book> fresh)
    {
        var old = Books.Where(b => b.Source == source).ToDictionary(b => b.SourceId);
        await Task.Run(() => Db.Batch(() =>
        {
            foreach (var b in fresh)
            {
                old.TryGetValue(b.SourceId, out var o);
                if (o is { Edited: true }) { b.Title = o.Title; b.Author = o.Author; b.Series = o.Series; }
                Db.UpsertBook(b);
                if (o == null) continue;
                b.Part = o.Part; b.PositionMs = o.PositionMs; b.Fraction = o.Fraction; b.Progress = o.Progress;
                b.Finished = o.Finished; b.LastOpened = o.LastOpened; b.Edited = o.Edited; b.DateAdded = o.DateAdded;
            }
            var keep = fresh.Select(b => b.SourceId).ToHashSet();
            foreach (var o in old.Values.Where(o => !keep.Contains(o.SourceId))) Db.DeleteBook(o.Id);
        }));
        Books = Books.Where(b => b.Source != source).Concat(fresh).ToList();
        Changed?.Invoke();
    }

    public void SaveBookInfo(Book b)
    {
        b.Edited = true;
        Db.UpdateBookInfo(b);
        b.Raise();
        Changed?.Invoke();
    }

    /// <summary>Marks a book finished, or puts it back to the very beginning.</summary>
    public void SetBookFinished(Book b, bool finished)
    {
        b.Finished = finished;
        b.Part = 0; b.PositionMs = 0; b.Fraction = 0;
        b.Progress = finished ? 1 : 0;
        Db.SaveBookState(b);
        b.Raise();
        Changed?.Invoke();
    }
}
