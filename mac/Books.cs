using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Halcyon.Core;

namespace Halcyon.Desktop;

/// <summary>Audiobooks, ebooks and comics together, by author. Opening one picks up where it was left.</summary>
public class BooksView : UserControl
{
    readonly TileGrid _tiles = new("PosterTile", 162);
    readonly ListBox _authors = Ui.List("NameRow");
    readonly TextBlock _authorCap = Ui.Cap("AUTHORS"), _bookCap = Ui.Cap("BOOKS"), _count = Ui.Text("", "cap"),
        _empty = Ui.Note("Add a book folder in settings. Halcyon picks up audiobooks (a folder of audio files, or an .m4b) and ebooks (EPUB, PDF, and comics as CBZ or CBR).");
    readonly Action<string> _selectTab;
    string _tab = "all";
    string? _author;
    bool _loading;

    public BooksView()
    {
        var (tabs, select, _) = Ui.Tabs("bookTabs", new[] { ("all", "all"), ("audio", "audiobooks"), ("ebook", "ebooks"), ("progress", "in progress") }, ShowTab);
        _selectTab = select;
        select("all");
        _count.Margin = new Thickness(18, 2, 0, 0);
        _count.VerticalAlignment = VerticalAlignment.Center;
        tabs.Children.Add(_count);
        tabs.Margin = new Thickness(8, 0, 0, 12);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.TextWrapping = TextWrapping.Wrap;
        _empty.MaxWidth = 520;
        var grid = Ui.Cells("260,*", "*", Ui.Under(_authorCap, _authors), Ui.Under(_bookCap, _tiles, new Thickness(10, 0, 0, 0)).At(1), _empty.At(0, 0, 2));
        Content = Ui.Under(tabs, grid, new Thickness(20, 8, 0, 0));

        _tiles.ItemClicked += o => _tiles.SelectedItem = o;
        _tiles.ItemActivated += o => OpenBook((Book)o);
        _tiles.ItemContext += (o, el) => MenuFor((Book)o).Show(el);
        _authors.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _author = _authors.SelectedIndex <= 0 ? null : (string?)_authors.SelectedItem;
            LoadBooks();
        };
        App.Lib.Changed += () => Dispatcher.UIThread.Post(Reload);
        // progress changes while reading or listening; refresh when the page comes back into view
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible) Reload(); };
        Reload();
    }

    public static void OpenBook(Book b, bool restart = false)
    {
        if (b.IsAudio) { App.Player.PlayBook(b, restart); return; }
        if (restart) App.Lib.SetBookFinished(b, false);
        switch (b.Format)
        {
            case "comic": ComicWindow.Open(b); break;
            case "pdf": OpenWithSystem(b); break;
            default: EpubWindow.Open(b); break;
        }
    }

    /// <summary>Takes the book to a bookmark: plays the audiobook from there, or opens the reader at that place.</summary>
    public static void OpenBookmark(Book b, Bookmark m)
    {
        if (!App.Lib.MoveToBookmark(m)) return;
        if (b.IsAudio) App.Player.PlayBookAt(b, m.Part, (long)m.Position);
        else if (b.Format == "comic") ComicWindow.OpenAt(b, m.Part);
        else if (b.Format == "epub") EpubWindow.OpenAt(b, m.Part, m.Position);
    }

    /// <summary>PDFs go to the Mac's own viewer (Preview).</summary>
    static async void OpenWithSystem(Book b)
    {
        try
        {
            var file = b.Source == "jellyfin" ? await App.Jellyfin.DownloadBookAsync(b) : b.SourceId;
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            b.LastOpened = Fmt.Now;
            if (b.Progress <= 0) b.Progress = 0.01;   // a PDF can't report a page, so just record that it was opened
            App.Lib.Db.SaveBookState(b);
            b.Raise();
        }
        catch (Exception ex) { await Dialog.Message("This book can't be opened.\n\n" + ex.Message); }
    }

    public void ShowTab(string tab)
    {
        _tab = tab;
        _author = null;
        _selectTab(tab);
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
        _empty.IsVisible = all.Count == 0;
        _count.Text = string.Join("  ·  ", new[] { Plural(all.Count(b => b.IsAudio), "audiobook"), Plural(all.Count(b => !b.IsAudio), "ebook") }.Where(s => s != ""));
        var authors = InTab().Select(b => b.AuthorOrUnknown).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
        _authorCap.Text = $"AUTHORS  {authors.Count}";
        authors.Insert(0, "all authors");
        _loading = true;
        _authors.ItemsSource = authors;
        var idx = _author == null ? 0 : authors.FindIndex(a => string.Equals(a, _author, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) { idx = 0; _author = null; }
        _authors.SelectedIndex = idx;
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
        _bookCap.Text = $"BOOKS  {list.Count}";
        _tiles.SetItems(list.Select(b => new Tile(b, b.Title,
            string.Join("  ·  ", new[] { b.AuthorOrUnknown, b.IsAudio ? Fmt.Time(b.DurationMs) : b.Format, b.ProgressText }.Where(s => s != "")), b.ArtPath)));
    }

    ContextMenu MenuFor(Book b)
    {
        var verb = b.IsAudio ? "Listen" : "Read";
        return Ui.Menu(
            Ui.MenuItem(b.Progress > 0 && !b.Finished ? $"Continue ({b.ProgressText})" : verb, () => OpenBook(b)),
            Ui.MenuItem("Start from the beginning", () => OpenBook(b, restart: true), b.Progress > 0 || b.Finished),
            b.Format == "pdf" ? null : Ui.MenuItem(App.Lib.BookmarkCount(b) == 0 ? "Bookmarks" : $"Bookmarks ({App.Lib.BookmarkCount(b)})…", () => _ = new BookmarksDialog(b, m => OpenBookmark(b, m)).ShowDialog(HalcyonApp.Win), App.Lib.BookmarkCount(b) > 0),
            b.IsAudio && App.Player.Current?.Book == b ? Ui.MenuItem("Bookmark the current spot", () => App.SetStatus("bookmarked " + App.Lib.AddBookmark(b, App.Player.Current!.BookPart, App.Player.PositionMs).Label)) : null,
            new Separator(),
            Ui.MenuItem(b.Finished ? "Mark as not started" : "Mark as finished", () => App.Lib.SetBookFinished(b, !b.Finished)),
            Ui.MenuItem("Edit book info…", () => EditBook(b)),
            b.Source == "local" ? Ui.MenuItem("Show in Finder", () => Platform.RevealFile(b.SourceId)) : null);
    }

    static async void EditBook(Book b)
    {
        var title = await Dialog.Ask("Edit book info", "Title", b.Title);
        if (title == null) return;
        var author = await Dialog.Ask("Edit book info", "Author", b.Author);
        if (author == null) return;
        var series = await Dialog.Ask("Edit book info", "Series (leave blank if none)", b.Series);
        if (series == null) return;
        if (title.Trim() != "") b.Title = title.Trim();
        b.Author = author.Trim();
        b.Series = series.Trim();
        App.Lib.SaveBookInfo(b);
    }
}

/// <summary>
/// Comic reader for .cbz / .cbr books: one page at a time, scaled to fit. Click the right or left edge, use the arrow
/// keys, Space, or the mouse wheel to turn pages; the place is saved on every turn.
/// </summary>
public class ComicWindow : Window
{
    static readonly Dictionary<long, ComicWindow> OpenReaders = new();

    readonly Book _book;
    readonly Image _image = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
    readonly TextBlock _pageText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), MinWidth = 120 };
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(40), Text = "opening…" };
    readonly Button _dirBtn, _marksBtn;
    List<string> _pages = new();
    long _wheelAt;

    public int Page { get; private set; }
    public int Pages => _pages.Count;
    public Task? Opening { get; private set; }

    /// <summary>Opens the comic at a saved page; if it is already open, turns to that page.</summary>
    public static ComicWindow OpenAt(Book book, int page)
    {
        if (OpenReaders.TryGetValue(book.Id, out var open)) { open.Activate(); open.Show(page); return open; }
        return Open(book);
    }

    public static ComicWindow Open(Book book)
    {
        if (OpenReaders.TryGetValue(book.Id, out var open)) { open.Activate(); return open; }
        var w = new ComicWindow(book);
        OpenReaders[book.Id] = w;
        w.Show();
        return w;
    }

    ComicWindow(Book book)
    {
        _book = book;
        Title = book.Author == "" ? book.Title : $"{book.Title} — {book.Author}";
        Width = 1000; Height = 900; MinWidth = 480; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _dirBtn = Ui.TextBtn("", () => { App.Settings.ComicRightToLeft = !App.Settings.ComicRightToLeft; UpdateDirection(); }, new Thickness(10, 0, 0, 0));
        UpdateDirection();
        _marksBtn = Ui.TextBtn("bookmarks", ShowBookmarks, new Thickness(10, 0, 0, 0));
        var right = Ui.Row(_dirBtn, Ui.TextBtn("‹ back", () => Turn(-1), new Thickness(10, 0, 0, 0)), Ui.TextBtn("next ›", () => Turn(1), new Thickness(10, 0, 0, 0)),
            Ui.TextBtn("⚑", AddBookmark, new Thickness(10, 0, 0, 0)), _marksBtn, _pageText);
        var bar = Ui.Split(new TextBlock { Text = book.Title, VerticalAlignment = VerticalAlignment.Center, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis }, right);
        bar.Height = 44;
        bar.Margin = new Thickness(18, 0);
        var body = new Grid { Background = Brushes.Transparent, Children = { _image, _status } };
        body.PointerReleased += (_, e) =>
        {
            var x = e.GetPosition(body).X / Math.Max(1, body.Bounds.Width);
            var rtl = App.Settings.ComicRightToLeft;
            if (x > 0.7) Turn(rtl ? -1 : 1); else if (x < 0.3) Turn(rtl ? 1 : -1);
        };
        body.PointerWheelChanged += (_, e) =>
        {
            var now = Environment.TickCount64;
            if (now - _wheelAt < 150) return;
            _wheelAt = now;
            Turn(e.Delta.Y < 0 ? 1 : -1);
        };
        Content = Ui.Under(bar, body);

        Opened += (_, _) => Opening = OpenAsync();
        Closed += (_, _) => OpenReaders.Remove(_book.Id);
        KeyDown += (_, e) =>
        {
            var rtl = App.Settings.ComicRightToLeft;
            switch (e.Key)
            {
                case Key.Escape: Close(); break;
                case Key.D when e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta): AddBookmark(); break;
                case Key.Right: Turn(rtl ? -1 : 1); break;
                case Key.Left: Turn(rtl ? 1 : -1); break;
                case Key.PageDown: case Key.Space: Turn(1); break;
                case Key.PageUp: Turn(-1); break;
                case Key.Home: Show(0); break;
                case Key.End: Show(Pages - 1); break;
                default: return;
            }
            e.Handled = true;
        };
    }

    void AddBookmark()
    {
        if (_pages.Count == 0) return;
        App.Lib.AddBookmark(_book, Page, 0, $"Page {Page + 1} of {_pages.Count}");
        _pageText.Text = $"bookmarked page {Page + 1}";
        UpdateMarks();
    }

    void ShowBookmarks()
    {
        if (App.Lib.BookmarkCount(_book) == 0) { _pageText.Text = "no bookmarks yet — choose ⚑"; return; }
        _ = new BookmarksDialog(_book, m => { Activate(); Show(m.Part); }).ShowDialog(this);
    }

    void UpdateMarks()
    {
        var n = App.Lib.BookmarkCount(_book);
        _marksBtn.Content = n == 0 ? "bookmarks" : $"bookmarks ({n})";
    }

    void UpdateDirection() => _dirBtn.Content = App.Settings.ComicRightToLeft ? "right to left" : "left to right";

    async Task OpenAsync()
    {
        try
        {
            var file = _book.Source == "jellyfin" ? await App.Jellyfin.DownloadBookAsync(_book) : _book.SourceId;
            if (!File.Exists(file)) throw new FileNotFoundException("The comic file is no longer where it was.", file);
            var dir = Path.Combine(AppPaths.DataDir, "books", _book.Id.ToString());
            _pages = await Task.Run(() => Comic.Pages(file, dir));
            if (_pages.Count == 0) throw new InvalidDataException("The comic has no pages.");
            _book.LastOpened = Fmt.Now;
            UpdateMarks();
            Show(_book.Finished ? 0 : _book.Part);
        }
        catch (Exception ex) { _image.IsVisible = false; _status.Text = "This comic can't be opened.\n\n" + ex.Message; }
    }

    void Turn(int delta)
    {
        if (_pages.Count > 0) Show(Page + delta);
    }

    public void Show(int page)
    {
        if (_pages.Count == 0) return;
        Page = Math.Clamp(page, 0, _pages.Count - 1);
        try
        {
            using var fs = File.OpenRead(_pages[Page]);
            _image.Source = Bitmap.DecodeToHeight(fs, 2600);   // plenty for any screen; keeps huge scans light
            _status.IsVisible = false;
        }
        catch (Exception ex) { _image.Source = null; _status.IsVisible = true; _status.Text = "This page can't be shown.\n\n" + ex.Message; }
        _pageText.Text = $"page {Page + 1} of {_pages.Count}";
        _book.Part = Page;
        _book.Progress = (Page + 1.0) / _pages.Count;
        _book.Finished = Page == _pages.Count - 1;
        App.Lib.Db.SaveBookState(_book);
        _book.Raise();
    }
}

/// <summary>
/// EPUB reader: the text of one chapter at a time in a column that scrolls, with chapter jumping and a text-size
/// setting. Pictures inside the book are not drawn.
/// </summary>
public class EpubWindow : Window
{
    static readonly Dictionary<long, EpubWindow> OpenReaders = new();

    readonly Book _book;
    readonly SelectableTextBlock _text = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(40, 24, 40, 60) };
    readonly ScrollViewer _scroll = new();
    readonly TextBlock _pageText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), MinWidth = 150 };
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(40), Text = "opening…" };
    readonly ComboBox _toc = new() { Width = 280, VerticalAlignment = VerticalAlignment.Center };
    readonly Button _marksBtn;
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    Epub? _epub;
    string _dir = "";
    bool _settingToc;
    double _startFraction;

    public int Chapter { get; private set; }
    public Task? Opening { get; private set; }
    public string ChapterText => _text.Text ?? "";

    /// <summary>Opens the book at a saved place; if it is already open, turns to that place.</summary>
    public static EpubWindow OpenAt(Book book, int chapter, double fraction)
    {
        if (OpenReaders.TryGetValue(book.Id, out var open)) { open.Activate(); open.Go(chapter, fraction); return open; }
        return Open(book);   // a new window starts from the book's saved place
    }

    public static EpubWindow Open(Book book)
    {
        if (OpenReaders.TryGetValue(book.Id, out var open)) { open.Activate(); return open; }
        var w = new EpubWindow(book);
        OpenReaders[book.Id] = w;
        w.Show();
        return w;
    }

    EpubWindow(Book book)
    {
        _book = book;
        Title = book.Author == "" ? book.Title : $"{book.Title} — {book.Author}";
        Width = 960; Height = 860; MinWidth = 520; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _text.FontSize = App.Settings.ReaderFontSize;
        _text.LineHeight = App.Settings.ReaderFontSize * 1.6;

        var right = Ui.Row(Ui.TextBtn("‹ chapter", () => Go(Chapter - 1, 0), new Thickness(10, 0, 0, 0)), Ui.TextBtn("chapter ›", () => Go(Chapter + 1, 0), new Thickness(10, 0, 0, 0)),
            Ui.TextBtn("A−", () => SetFont(-1), new Thickness(10, 0, 0, 0)), Ui.TextBtn("A+", () => SetFont(1), new Thickness(10, 0, 0, 0)),
            Ui.TextBtn("⚑", AddBookmark, new Thickness(10, 0, 0, 0)), _marksBtn = Ui.TextBtn("bookmarks", ShowBookmarks, new Thickness(10, 0, 0, 0)), _pageText);
        var bar = Ui.Split(_toc, right);
        bar.Height = 44;
        bar.Margin = new Thickness(18, 0);
        _scroll.Content = _text;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.ScrollChanged += (_, _) => { _saveTimer.Stop(); _saveTimer.Start(); };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveState(); };
        _toc.SelectionChanged += (_, _) => { if (!_settingToc && _toc.SelectedIndex >= 0) Go(_toc.SelectedIndex, 0); };
        Content = Ui.Under(bar, new Grid { Children = { _scroll, _status } });

        Opened += (_, _) => Opening = OpenAsync();
        Closed += (_, _) => { SaveState(); OpenReaders.Remove(_book.Id); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.D && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))) AddBookmark();
            else if (e.Key == Key.Right) Go(Chapter + 1, 0);
            else if (e.Key == Key.Left) Go(Chapter - 1, 0);
            else return;
            e.Handled = true;
        };
    }

    async Task OpenAsync()
    {
        try
        {
            var file = _book.Source == "jellyfin" ? await App.Jellyfin.DownloadBookAsync(_book) : _book.SourceId;
            if (!File.Exists(file)) throw new FileNotFoundException("The book file is no longer where it was.", file);
            _epub = await Task.Run(() => Epub.Open(file));
            if (_epub.Chapters.Count == 0) throw new InvalidDataException("The book has no readable chapters.");
            _dir = Path.Combine(AppPaths.DataDir, "books", _book.Id.ToString());
            var stamp = Path.Combine(_dir, ".extracted");
            var signature = $"{file}|{File.GetLastWriteTimeUtc(file).Ticks}";
            if (!File.Exists(stamp) || File.ReadAllText(stamp) != signature)
            {
                if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
                await Task.Run(() => Epub.Extract(file, _dir));
                File.WriteAllText(stamp, signature);
            }
            _settingToc = true;
            _toc.ItemsSource = _epub.Chapters.Select(c => c.Title).ToList();
            _settingToc = false;
            _book.LastOpened = Fmt.Now;
            UpdateMarks();
            Go(_book.Finished ? 0 : _book.Part, _book.Finished ? 0 : _book.Fraction);
        }
        catch (Exception ex) { _scroll.IsVisible = false; _status.Text = "This book can't be opened.\n\n" + ex.Message; }
    }

    void AddBookmark()
    {
        if (_epub == null) return;
        var max = Math.Max(1, _scroll.Extent.Height - _scroll.Viewport.Height);
        var fraction = Math.Clamp(_scroll.Offset.Y / max, 0, 1);
        var m = App.Lib.AddBookmark(_book, Chapter, fraction, $"{_epub.Chapters[Chapter].Title}  ·  {(int)(fraction * 100)}%");
        _pageText.Text = $"bookmarked: {m.Label}";
        UpdateMarks();
    }

    void ShowBookmarks()
    {
        if (App.Lib.BookmarkCount(_book) == 0) { _pageText.Text = "no bookmarks yet — choose ⚑"; return; }
        _ = new BookmarksDialog(_book, m => { Activate(); Go(m.Part, m.Position); }).ShowDialog(this);
    }

    void UpdateMarks()
    {
        var n = App.Lib.BookmarkCount(_book);
        _marksBtn.Content = n == 0 ? "bookmarks" : $"bookmarks ({n})";
    }

    void SetFont(int delta)
    {
        var s = App.Settings;
        s.ReaderFontSize = Math.Clamp(s.ReaderFontSize + delta * 2, 12, 40);
        _text.FontSize = s.ReaderFontSize;
        _text.LineHeight = s.ReaderFontSize * 1.6;
    }

    void Go(int chapter, double fraction)
    {
        if (_epub == null) return;
        Chapter = Math.Clamp(chapter, 0, _epub.Chapters.Count - 1);
        _startFraction = fraction;
        string html;
        try { html = File.ReadAllText(Path.Combine(_dir, _epub.Chapters[Chapter].Href.Replace('/', Path.DirectorySeparatorChar))); }
        catch (Exception ex) { _text.Text = "This chapter can't be read: " + ex.Message; html = ""; }
        if (html != "") _text.Text = HtmlText(html);
        _status.IsVisible = false;
        _settingToc = true;
        _toc.SelectedIndex = Chapter;
        _settingToc = false;
        // the page has not been measured yet: land on the saved place once it has
        Dispatcher.UIThread.Post(() =>
        {
            var max = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
            _scroll.Offset = new Vector(0, _startFraction * max);
            SaveState();
        }, DispatcherPriority.Background);
    }

    void SaveState()
    {
        if (_epub == null) return;
        var max = Math.Max(1, _scroll.Extent.Height - _scroll.Viewport.Height);
        var fraction = Math.Clamp(_scroll.Offset.Y / max, 0, 1);
        var chapters = _epub.Chapters.Count;
        _book.Part = Chapter;
        _book.Fraction = fraction;
        _book.Progress = Math.Clamp((Chapter + fraction) / chapters, 0, 1);
        _book.Finished = Chapter == chapters - 1 && fraction > 0.97;
        _pageText.Text = $"chapter {Chapter + 1} of {chapters}   ·   {Math.Max(1, (int)(_book.Progress * 100))}%";
        App.Lib.Db.SaveBookState(_book);
        _book.Raise();
    }

    /// <summary>The readable text of a chapter: paragraphs separated by blank lines, markup removed.</summary>
    public static string HtmlText(string html)
    {
        var s = Regex.Replace(html, @"<(script|style|head)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</(p|div|h[1-6]|li|blockquote|tr)\s*>|<br\s*/?>", "\n\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"[ \t ]+", " ");
        s = Regex.Replace(s, @" ?\n ?", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }
}

/// <summary>A book's bookmarks: jump to one (double-click or "go to"), rename it, or delete it.</summary>
public class BookmarksDialog : Dialog
{
    readonly Book _book;
    readonly Action<Bookmark> _jump;
    readonly ListBox _list = new() { Height = 260 };

    public BookmarksDialog(Book book, Action<Bookmark> jump) : base("Bookmarks", 520)
    {
        _book = book;
        _jump = jump;
        _list.Classes.Add("boxed");
        _list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Bookmark>((m, _) =>
        {
            var when = new TextBlock { Text = m?.CreatedText ?? "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Opacity = 0.6 };
            DockPanel.SetDock(when, Dock.Right);
            return new DockPanel { Height = 38, Children = { when, new TextBlock { Text = m?.Label ?? "", TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center } } };
        });
        _list.DoubleTapped += (_, e) => { if (Ui.Item<Bookmark>(e) != null) Go(); };
        Body.Children.Add(Ui.H2("bookmarks"));
        Body.Children.Add(Ui.Note(book.Title));
        Body.Children.Add(_list);
        var go = Ui.Pill("go to", Go);
        go.IsDefault = true;
        var close = Ui.Pill("close", Close);
        close.Margin = default;
        Body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0),
            Children = { go, Ui.Pill("rename", Rename), Ui.Pill("delete", Delete), close }
        });
        Reload();
    }

    Bookmark? Selected => _list.SelectedItem as Bookmark;

    void Reload()
    {
        var keep = Selected?.Id;
        var marks = App.Lib.BookmarksOf(_book);
        _list.ItemsSource = marks;
        _list.SelectedItem = marks.FirstOrDefault(m => m.Id == keep) ?? marks.FirstOrDefault();
    }

    void Go()
    {
        if (Selected is not { } m) return;
        Close();
        _jump(m);
    }

    async void Rename()
    {
        if (Selected is not { } m) return;
        var name = await Dialog.Ask("Rename bookmark", "Name", m.Label);
        if (string.IsNullOrWhiteSpace(name)) return;
        App.Lib.RenameBookmark(m, name);
        Reload();
    }

    void Delete()
    {
        if (Selected is not { } m) return;
        App.Lib.DeleteBookmark(m);
        Reload();
    }
}
