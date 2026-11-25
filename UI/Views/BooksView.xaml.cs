using System.Windows;
using System.Windows.Controls;
using Halcyon.Core;

namespace Halcyon.UI;

public record BookTile(Book Book, string Title, string Caption, string ArtPath, double ProgressWidth)
{
    /// <summary>The note in the cover's corner: "read", the page you are on ("12 / 180"), or how far through you are.</summary>
    public string Counter => CounterFor(Book);

    public static string CounterFor(Book b)
    {
        if (b.Finished) return "read";
        if (b.Progress <= 0.004) return "";
        if (b.Format == "comic" && b.PositionMs > 0) return $"{Math.Min(b.Part + 1, b.PositionMs)} / {b.PositionMs}";
        return $"{Math.Max(1, (int)(b.Progress * 100))}%";
    }
    public bool IsAudio => Book.IsAudio;
    public string Glyph => Book.IsAudio ? "" : "";
}

/// <summary>Audiobooks and ebooks together, by author. Opening one picks up where it was left.</summary>
public partial class BooksView : UserControl
{
    string _tab = "all";
    string? _author;
    bool _loading;

    public BooksView()
    {
        InitializeComponent();
        Tiles.ItemClicked += o => Tiles.SelectedItem = o;
        Tiles.ItemActivated += o => OpenBook(((BookTile)o).Book);
        Tiles.ItemContext += (o, el) => Menus.Open(MenuFor(((BookTile)o).Book), el);
        Tiles.DrawWith(TileStyle.Book, o => o is BookTile b
            ? new TileLook(b.ArtPath, b.Title, b.Caption, b.Glyph, Counter: b.Counter, Audio: b.IsAudio, Progress: b.ProgressWidth / 150) : default);
        App.Lib.Changed += Reload;
        // Progress changes while reading or listening; refresh when the page comes back into view.
        IsVisibleChanged += (_, _) => { if (IsVisible) Reload(); };
        Reload();
    }

    public static void OpenBook(Book b, bool restart = false)
    {
        if (b.IsAudio) App.Player.PlayBook(b, restart);
        else
        {
            if (restart) App.Lib.SetBookFinished(b, false);
            if (b.Format == "comic") ComicWindow.Open(b); else ReaderWindow.Open(b);
        }
    }

    /// <summary>Takes the book to a bookmark: plays the audiobook from there, or opens the reader at that place.</summary>
    public static void OpenBookmark(Book b, Bookmark m)
    {
        if (!App.Lib.MoveToBookmark(m)) return;
        if (b.IsAudio) App.Player.PlayBookAt(b, m.Part, (long)m.Position);
        else if (b.Format == "comic") ComicWindow.OpenAt(b, m.Part);
        else if (b.Format == "epub") ReaderWindow.OpenAt(b, m.Part, m.Position);
    }

    void Tab_Click(object sender, RoutedEventArgs e) => ShowTab((string)((FrameworkElement)sender).Tag);

    public void ShowTab(string tab)
    {
        _tab = tab;
        _author = null;
        Reload();
    }

    IEnumerable<Book> InTab() => _tab switch
    {
        "audio" => App.Lib.Books.Where(b => b.IsAudio),
        "ebook" => App.Lib.Books.Where(b => !b.IsAudio),
        "progress" => App.Lib.Books.Where(b => b.Progress > 0 && !b.Finished),
        _ => App.Lib.Books
    };

    void Reload()
    {
        var all = App.Lib.Books;
        EmptyPanel.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = string.Join("  ·  ", new[] { Plural(all.Count(b => b.IsAudio), "audiobook"), Plural(all.Count(b => !b.IsAudio), "ebook") }.Where(s => s != ""));

        var authors = InTab().Select(b => b.AuthorOrUnknown).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
        AuthorCap.Text = $"AUTHORS  {authors.Count}";
        authors.Insert(0, "all authors");
        _loading = true;
        AuthorList.ItemsSource = authors;
        var idx = _author == null ? 0 : authors.FindIndex(a => string.Equals(a, _author, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) { idx = 0; _author = null; }
        AuthorList.SelectedIndex = idx;
        _loading = false;
        LoadBooks();
    }

    static string Plural(int n, string noun) => n == 0 ? "" : $"{n} {noun}{(n == 1 ? "" : "s")}";

    void LoadBooks()
    {
        var books = InTab().Where(b => _author == null || string.Equals(b.AuthorOrUnknown, _author, StringComparison.OrdinalIgnoreCase));
        books = _tab == "progress"
            ? books.OrderByDescending(b => b.LastOpened)
            : books.OrderBy(b => b.AuthorOrUnknown, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Series, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase);
        var list = books.ToList();
        BookCap.Text = $"BOOKS  {list.Count}";
        Tiles.SetItems(list.Select(b => new BookTile(b, b.Title,
            string.Join("  ·  ", new[] { b.AuthorOrUnknown, b.IsAudio ? Fmt.Time(b.DurationMs) : b.Format, b.ProgressText }.Where(s => s != "")),
            b.ArtPath, 150 * (b.Finished ? 1 : b.Progress))));
    }

    /// <summary>For the self-test: rows of covers built right now.</summary>
    public int BuiltRows => Tiles.BuiltRows;
    public ScrollViewer TileArea => Tiles.ScrollArea;
    public TileGrid Grid => Tiles;

    void Author_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _author = AuthorList.SelectedIndex <= 0 ? null : (string)AuthorList.SelectedItem;
        LoadBooks();
    }

    ContextMenu MenuFor(Book b)
    {
        var verb = b.IsAudio ? "Listen" : "Read";
        var m = new ContextMenu();
        m.Items.Add(Menus.Item(b.Progress > 0 && !b.Finished ? $"Continue ({b.ProgressText})" : verb, () => OpenBook(b)));
        m.Items.Add(Menus.Item("Start from the beginning", () => OpenBook(b, restart: true), b.Progress > 0 || b.Finished));
        if (b.Format != "pdf")
        {
            var n = App.Lib.BookmarkCount(b);
            if (b.IsAudio && App.Player.Current?.Book == b)
                m.Items.Add(Menus.Item("Bookmark the current spot", () => App.Win.SetStatus("bookmarked " + App.Lib.AddBookmark(b, App.Player.Current!.BookPart, Math.Round(App.Player.Position.TotalMilliseconds)).Label)));
            m.Items.Add(Menus.Item(n == 0 ? "Bookmarks" : $"Bookmarks ({n})…", () => new BookmarksDialog(b, mark => OpenBookmark(b, mark)).ShowDialog(), n > 0));
        }
        m.Items.Add(new Separator());
        m.Items.Add(Menus.Item(b.Finished ? "Mark as not started" : "Mark as finished", () => App.Lib.SetBookFinished(b, !b.Finished)));
        m.Items.Add(Menus.Item("Edit book info…", () => EditBook(b)));
        m.Items.Add(Menus.Item("Edit tags…", () => new EditTagsDialog(Library.BookTag, new[] { b.Id }, $"“{b.Title}”").ShowDialog()));
        if (b.Source == "local")
            m.Items.Add(Menus.Item("Show in File Explorer", () => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{b.SourceId}\"")));
        return m;
    }

    static void EditBook(Book b)
    {
        var title = InputDialog.Ask("Edit book info", "Title", b.Title);
        if (title == null) return;
        var author = InputDialog.Ask("Edit book info", "Author", b.Author);
        if (author == null) return;
        var series = InputDialog.Ask("Edit book info", "Series (leave blank if none)", b.Series);
        if (series == null) return;
        if (title.Trim() != "") b.Title = title.Trim();
        b.Author = author.Trim();
        b.Series = series.Trim();
        App.Lib.SaveBookInfo(b);
    }

    void OpenSettings_Click(object sender, RoutedEventArgs e) => App.Win.Navigate("settings");
}
