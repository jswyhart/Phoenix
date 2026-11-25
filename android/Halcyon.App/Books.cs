using Android.Graphics;
using Android.Graphics.Pdf;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using Halcyon.Core;
using System.Text.Json;
using Java.Interop;

namespace Halcyon.Droid;

/// <summary>Audiobooks and ebooks together: tabs, tiles with a progress bar, and the same menu as on the PC.</summary>
public static class BookScreens
{
    static string _tab = "all";

    public static Page Books(MainActivity a)
    {
        var page = new Page(a, "books");
        page.Chips(() => new[] { "all", "audiobooks", "ebooks", "in progress" },
            () => _tab switch { "audio" => "audiobooks", "ebook" => "ebooks", "progress" => "in progress", _ => "all" },
            c => _tab = c switch { "audiobooks" => "audio", "ebooks" => "ebook", "in progress" => "progress", _ => "all" });
        page.Subtitle = () =>
        {
            var all = App.Lib.Books;
            return Fmt.Join(Fmt.Plural(all.Count(b => b.IsAudio), "audiobook"), Fmt.Plural(all.Count(b => !b.IsAudio), "ebook"));
        };
        var view = new ViewChoice("books");
        var sorts = new[] { ("", "author"), ("title", "title"), ("added", "date added"), ("opened", "last opened"), ("progress", "progress") };
        var shows = new[] { ("", "everything"), ("new", "not started"), ("started", "in progress"), ("done", "finished"), ("comic", "comics"), ("epub", "EPUB"), ("pdf", "PDF") };
        return page.Grid(() =>
        {
            page.Actions(page.Opts(view, sorts, shows).ToArray());
            var books = App.Lib.Books.AsEnumerable();
            books = _tab switch
            {
                "audio" => books.Where(b => b.IsAudio),
                "ebook" => books.Where(b => !b.IsAudio),
                "progress" => books.Where(b => b.Progress > 0 && !b.Finished).OrderByDescending(b => b.LastOpened),
                _ => books
            };
            if (_tab != "progress")
                books = books.OrderBy(b => b.AuthorOrUnknown, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Series, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase);
            var byName = StringComparer.OrdinalIgnoreCase;
            books = view.Order(books.Where(b => (view.Show switch
            {
                "new" => b.Progress <= 0.004 && !b.Finished, "started" => b.Progress > 0.004 && !b.Finished, "done" => b.Finished,
                "comic" => b.Format == "comic", "epub" => b.Format == "epub", "pdf" => b.Format == "pdf", _ => true
            }) && view.Matches(b.Title, b.Author, b.Series)), key => key switch
            {
                "title" => l => l.OrderBy(b => b.Title, byName),
                "added" => l => l.OrderByDescending(b => b.DateAdded),
                "opened" => l => l.OrderByDescending(b => b.LastOpened),
                "progress" => l => l.OrderByDescending(b => b.Finished ? 1 : b.Progress).ThenBy(b => b.Title, byName),
                _ => null
            });
            return books.Select(b => new Row
            {
                Title = b.Title,
                Sub = Fmt.Join(b.AuthorOrUnknown, b.IsAudio ? Fmt.Time(b.DurationMs) : b.Format, b.ProgressText),
                Art = b.ArtPath, ArtShape = 2,
                Click = () => Open(a, b),
                Long = () => Menu(a, b)
            }).ToList();
        }, 120, 1.5f, "No books yet.\n\nAdd a folder of audiobooks, EPUBs or PDFs under settings → book folders, or connect a Jellyfin server that has book libraries.");
    }

    public static void Open(MainActivity a, Book b, bool restart = false)
    {
        if (b.IsAudio) { App.Player.PlayBook(b, restart); a.ShowNowPlaying(); }
        else
        {
            if (restart) App.Lib.SetBookFinished(b, false);
            a.ShowReader(b);
        }
    }

    public static void Menu(MainActivity a, Book b)
    {
        var verb = b.IsAudio ? "Listen" : "Read";
        var items = new List<(string, Action)>
        {
            (b.Progress > 0 && !b.Finished ? $"Continue ({b.ProgressText})" : verb, () => Open(a, b))
        };
        if (b.Progress > 0 || b.Finished) items.Add(("Start from the beginning", () => Open(a, b, true)));
        items.Add((b.Finished ? "Mark as not started" : "Mark as finished", () => App.Lib.SetBookFinished(b, !b.Finished)));
        if (b.Format != "pdf")
        {
            var n = App.Lib.BookmarkCount(b);
            items.Add((n == 0 ? "Bookmarks…" : $"Bookmarks ({n})…", () => BookmarkMenu(a, b, null)));
        }
        items.Add(("Tags…", () => Menus.EditTags(a, Library.BookTag, new List<long> { b.Id }, "Tags: " + b.Title)));
        items.Add(("Edit book info…", () => Edit(a, b)));
        Ui.Menu(a, b.Title, items.ToArray());
    }

    /// <summary>
    /// A book's bookmarks. With <paramref name="here"/> (the place being read or heard right now) the list starts with
    /// "add a bookmark here". Tap a bookmark to go to it, rename it or delete it.
    /// </summary>
    public static void BookmarkMenu(MainActivity a, Book b, Func<(int part, double position)>? here)
    {
        var items = new List<(string, Action)>();
        if (here != null)
            items.Add(("＋  Add a bookmark here", () =>
            {
                var (part, position) = here();
                var m = App.Lib.AddBookmark(b, part, position);
                Ui.Toast(a, $"Bookmarked: {m.Label}");
            }));
        foreach (var m in App.Lib.BookmarksOf(b))
        {
            var mark = m;
            items.Add((Fmt.Join(mark.Label, mark.CreatedText), () => Ui.Menu(a, mark.Label,
                ("Go to", () => Jump(a, mark)),
                ("Rename…", () => Ui.Ask(a, "Rename bookmark", "Name", mark.Label, name => { App.Lib.RenameBookmark(mark, name); BookmarkMenu(a, b, here); })),
                ("Delete", () => { App.Lib.DeleteBookmark(mark); BookmarkMenu(a, b, here); }))));
        }
        if (items.Count == 0) { Ui.Alert(a, "No bookmarks yet.\n\nOpen the book, tap the bookmark icon and choose “add a bookmark here”."); return; }
        Ui.Menu(a, "Bookmarks — " + b.Title, items.ToArray());
    }

    /// <summary>Opens the book (or starts the audiobook) at a bookmark.</summary>
    public static void Jump(MainActivity a, Bookmark m)
    {
        if (!App.Lib.MoveToBookmark(m)) { Ui.Toast(a, "That book is no longer in the collection."); return; }
        var b = App.Lib.Books.First(x => x.Id == m.BookId);
        if (b.IsAudio) { App.Player.PlayBookAt(b, m.Part, (long)m.Position); a.ShowNowPlaying(); }
        else a.ShowReader(b);
    }

    static void Edit(MainActivity a, Book b)
    {
        var (form, field) = Menus.Form(a);
        var title = field($"Title|{b.Title}");
        var author = field($"Author|{b.Author}");
        var series = field($"Series (leave blank if none)|{b.Series}");
        Menus.ShowForm(a, "Edit book info", form, () =>
        {
            if ((title.Text ?? "").Trim() != "") b.Title = title.Text!.Trim();
            b.Author = (author.Text ?? "").Trim();
            b.Series = (series.Text ?? "").Trim();
            App.Lib.SaveBookInfo(b);
        });
    }
}

/// <summary>
/// Full-screen ebook reader. An EPUB's chapters are shown in a web view with Halcyon's own page layout injected (swipe or
/// tap the edges to turn pages); the place is saved on every page turn. A PDF is drawn page by page with the system renderer.
/// </summary>
public class BookReader : FrameLayout
{
    const string Host = "book.halcyon";
    readonly MainActivity _a;
    readonly TextView _title, _page, _status;
    readonly LinearLayout _tools, _comicTools;
    readonly TextView _direction;
    List<string> _comicPages = new();
    int _comicPage;
    ImageView? _comicImg;
    readonly FrameLayout _body;
    WebView? _web;
    ListView? _pdfList;
    PdfRenderer? _pdf;
    ParcelFileDescriptor? _pdfFd;
    Book? _book;
    Epub? _epub;
    string _dir = "";
    string _script = "";
    double _start;
    int _chapter, _pages = 1, _pageNo;
    long _opening;
    readonly Bridge _bridge;

    public bool IsOpen => Visibility == ViewStates.Visible;
    public int Chapter => _chapter;

    public BookReader(MainActivity a) : base(a)
    {
        _a = a;
        SetBackgroundColor(Th.Bg);
        Clickable = true;
        Visibility = ViewStates.Gone;
        _bridge = new Bridge(this);

        var column = new LinearLayout(a) { Orientation = Orientation.Vertical };
        _title = Ui.Text(a, "", 15, Th.Fg);
        _title.SetPadding(Th.Dp(4), 0, Th.Dp(8), 0);
        _page = Ui.Text(a, "", 12, Th.FgDim);
        _page.SetPadding(Th.Dp(8), 0, Th.Dp(8), 0);
        var bar = Ui.RowOf(a, Ui.Icon(a, IconKind.Back, Th.Fg, 22, Close, 12), _title);
        bar.SetBackgroundColor(Th.BgAlt);
        ((LinearLayout.LayoutParams)(_title.LayoutParameters = Ui.Weight())).Gravity = GravityFlags.CenterVertical;
        _tools = Ui.RowOf(a, Ui.Icon(a, IconKind.Bookmark, Th.Fg, 20, () => OpenBookmarks(), 10), Link("contents", ShowContents), Link("A−", () => ChangeFont(-2)), Link("A+", () => ChangeFont(2)));
        bar.AddView(_tools);
        _direction = Link("", ToggleDirection);
        _comicTools = Ui.RowOf(a, Ui.Icon(a, IconKind.Bookmark, Th.Fg, 20, () => OpenBookmarks(), 10), _direction);
        _comicTools.Visibility = ViewStates.Gone;
        bar.AddView(_comicTools);
        column.AddView(bar, Ui.Fill());
        _body = new FrameLayout(a);
        column.AddView(_body, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        _page.SetBackgroundColor(Th.BgAlt);
        _page.SetPadding(Th.Dp(16), Th.Dp(5), Th.Dp(16), Th.Dp(5));
        column.AddView(_page, Ui.Fill());
        AddView(column, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        _status = Ui.Text(a, "", 15, Th.FgDim, null, 0);
        _status.Gravity = GravityFlags.Center;
        _status.SetPadding(Th.Dp(30), 0, Th.Dp(30), 0);
        AddView(_status, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
    }

    TextView Link(string text, Action act)
    {
        var t = Ui.Link(_a, text, act);
        t.SetPadding(Th.Dp(8), Th.Dp(9), Th.Dp(8), Th.Dp(9));
        return t;
    }

    public async void Open(Book book)
    {
        _book = book;
        var token = ++_opening;
        Teardown();
        _title.Text = book.Title;
        _page.Text = "";
        _status.Text = "opening…";
        _status.Visibility = ViewStates.Visible;
        Visibility = ViewStates.Visible;
        try
        {
            var file = book.Source == "jellyfin" ? await App.Jellyfin.DownloadBookAsync(book) : book.SourceId;
            if (token != _opening) return;
            if (!File.Exists(file)) throw new FileNotFoundException("The book file is no longer where it was.");
            book.LastOpened = Fmt.Now;
            _tools.Visibility = book.Format == "epub" ? ViewStates.Visible : ViewStates.Gone;
            _comicTools.Visibility = book.Format == "comic" ? ViewStates.Visible : ViewStates.Gone;
            if (book.Format == "pdf") OpenPdf(file);
            else if (book.Format == "comic") await OpenComic(file, token);
            else await OpenEpub(file, token);
        }
        catch (Exception ex)
        {
            if (token == _opening) _status.Text = "This book can't be opened.\n\n" + ex.Message;
        }
    }

    void Teardown()
    {
        Save();
        _body.RemoveAllViews();
        if (_web != null) { _web.StopLoading(); _web.Destroy(); _web = null; }
        _pdfList = null;
        _comicImg = null; _comicPages = new();
        try { _pdf?.Close(); _pdfFd?.Close(); } catch { }
        _pdf = null; _pdfFd = null;
        _epub = null;
    }

    public void Close()
    {
        if (!IsOpen) return;
        _opening++;
        Teardown();
        Visibility = ViewStates.Gone;
        _book = null;
    }

    /// <summary>The app is going away from the screen: keep the place.</summary>
    public void Pause() { if (IsOpen) Save(); }

    void Save()
    {
        if (_book == null) return;
        App.Lib.Db.SaveBookState(_book);
        _book.Raise();
    }

    // ---------- EPUB ----------

    async Task OpenEpub(string file, long token)
    {
        var book = _book!;
        _epub = await Task.Run(() => Epub.Open(file));
        if (_epub.Chapters.Count == 0) throw new InvalidDataException("The book has no readable chapters.");
        var dir = System.IO.Path.Combine(AppPaths.DataDir, "books", book.Id.ToString());
        var stamp = System.IO.Path.Combine(dir, ".extracted");
        var signature = $"{file}|{File.GetLastWriteTimeUtc(file).Ticks}";
        await Task.Run(() =>
        {
            if (!File.Exists(stamp) || File.ReadAllText(stamp) != signature)
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Epub.Extract(file, dir);
                File.WriteAllText(stamp, signature);
            }
        });
        if (token != _opening) return;
        _dir = dir;
        _script = ReaderScript.Build(ReaderScript.PostAndroid, relayoutOnHeight: false);

        _web = new WebView(_a);
        _web.SetBackgroundColor(Th.Dark ? Color.ParseColor("#141414") : Color.White);
        _web.Settings.JavaScriptEnabled = true;
        _web.Settings.AllowFileAccess = false;
        _web.Settings.AllowContentAccess = false;
        _web.Settings.SetSupportZoom(false);
        _web.Settings.BuiltInZoomControls = false;
        _web.HorizontalScrollBarEnabled = false;
        _web.VerticalScrollBarEnabled = false;
        _web.OverScrollMode = OverScrollMode.Never;
        _web.AddJavascriptInterface(_bridge, "HalcyonHost");
        _web.SetWebViewClient(new BookClient(this));
        _body.AddView(_web, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        Go(book.Finished ? 0 : book.Part, book.Finished ? 0 : book.Fraction);
    }

    void Go(int chapter, double start)
    {
        if (_epub == null || _web == null) return;
        _chapter = Math.Clamp(chapter, 0, _epub.Chapters.Count - 1);
        _start = start;
        var url = string.Join("/", _epub.Chapters[_chapter].Href.Split('/').Select(Uri.EscapeDataString));
        _web.LoadUrl($"https://{Host}/{url}");
    }

    void Run(string js) => _web?.EvaluateJavascript(js, null);

    void ChangeFont(int delta)
    {
        if (_epub == null) return;
        App.Settings.ReaderFontSize = Math.Clamp(App.Settings.ReaderFontSize + delta, 12, 40);
        Run($"halcyon.font({App.Settings.ReaderFontSize})");
    }

    void OpenBookmarks()
    {
        if (_book == null) return;
        var book = _book;
        BookScreens.BookmarkMenu(_a, book, () => book.Format == "comic" ? (_comicPage, 0) : (_chapter, book.Fraction));
    }

    void ShowContents()
    {
        if (_epub == null) return;
        var items = _epub.Chapters.Select((c, i) => (i == _chapter ? "▸ " + c.Title : c.Title, (Action)(() => Go(i, 0)))).ToArray();
        Ui.Menu(_a, "Contents", items);
    }

    /// <summary>Serves the unpacked book to the web view as if it were a web site, adding the page script to every chapter.</summary>
    WebResourceResponse? Serve(string url)
    {
        var uri = Android.Net.Uri.Parse(url);
        if (uri?.Host != Host) return new WebResourceResponse("text/plain", "utf-8", 403, "Blocked", new Dictionary<string, string>(), new MemoryStream());
        var rel = Uri.UnescapeDataString(uri.Path ?? "").TrimStart('/');
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(_dir, rel));
        var root = System.IO.Path.GetFullPath(_dir) + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal) || !File.Exists(full))
            return new WebResourceResponse("text/plain", "utf-8", 404, "Not found", new Dictionary<string, string>(), new MemoryStream());
        var ext = System.IO.Path.GetExtension(full).ToLowerInvariant();
        if (ext is ".xhtml" or ".html" or ".htm" or ".xml")
        {
            var html = File.ReadAllText(full);
            var tag = "<script>" + _script + "</script>";
            var head = System.Text.RegularExpressions.Regex.Match(html, @"<head[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            html = head.Success ? html.Insert(head.Index + head.Length, tag) : tag + html;
            return new WebResourceResponse("text/html", "utf-8", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html)));
        }
        var mime = ext switch
        {
            ".css" => "text/css", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".svg" => "image/svg+xml",
            ".webp" => "image/webp", ".ttf" => "font/ttf", ".otf" => "font/otf", ".woff" => "font/woff", ".woff2" => "font/woff2", ".js" => "application/javascript", _ => "application/octet-stream"
        };
        return new WebResourceResponse(mime, null, File.OpenRead(full));
    }

    void OnMessage(string json)
    {
        HalcyonApp.Main.Post(() =>
        {
            if (_epub == null || _book == null) return;
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            switch (m.GetProperty("type").GetString())
            {
                case "ready":
                    // A link inside the book may have led to another chapter: follow where the page really is.
                    var path = Uri.UnescapeDataString(m.GetProperty("path").GetString() ?? "").TrimStart('/');
                    var found = _epub.Chapters.FindIndex(c => string.Equals(c.Href, path, StringComparison.OrdinalIgnoreCase));
                    if (found >= 0 && found != _chapter) { _chapter = found; _start = 0; }
                    var dark = Th.Dark;
                    Run("halcyon.init(" + JsonSerializer.Serialize(new { fontSize = App.Settings.ReaderFontSize, start = _start, bg = dark ? "#141414" : "#FFFFFF", fg = dark ? "#E8E8E8" : "#1A1A1A" }) + ")");
                    break;
                case "state":
                    _pageNo = m.GetProperty("page").GetInt32();
                    _pages = Math.Max(1, m.GetProperty("pages").GetInt32());
                    _status.Visibility = ViewStates.Gone;
                    var chapters = _epub.Chapters.Count;
                    _book.Part = _chapter;
                    _book.Fraction = _pages > 1 ? (double)_pageNo / (_pages - 1) : 0;
                    _book.Progress = Math.Clamp((_chapter + (_pageNo + 1.0) / _pages) / chapters, 0, 1);
                    _book.Finished = _chapter == chapters - 1 && _pageNo == _pages - 1;
                    _page.Text = $"{_epub.Chapters[_chapter].Title}   ·   page {_pageNo + 1} of {_pages}   ·   {Math.Max(1, (int)(_book.Progress * 100))}%";
                    Save();
                    break;
                case "edge":
                    var dir = m.GetProperty("dir").GetInt32();
                    if (dir > 0 && _chapter < _epub.Chapters.Count - 1) Go(_chapter + 1, 0);
                    else if (dir < 0 && _chapter > 0) Go(_chapter - 1, -1);
                    break;
            }
        });
    }

    class Bridge(BookReader r) : Java.Lang.Object
    {
        [JavascriptInterface, Export("post")]
        public void Post(string json) => r.OnMessage(json);
    }

    class BookClient(BookReader r) : WebViewClient
    {
        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request) => r.Serve(request?.Url?.ToString() ?? "");
        // A book stays inside its own files: links to the web are not followed.
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request) => request?.Url?.Host != Host;
    }

    // ---------- comics (.cbz / .cbr) ----------

    bool Rtl => App.Settings.ComicRightToLeft;

    void ToggleDirection()
    {
        App.Settings.ComicRightToLeft = !Rtl;
        _direction.Text = Rtl ? "right to left" : "left to right";
    }

    async Task OpenComic(string file, long token)
    {
        var book = _book!;
        var dir = System.IO.Path.Combine(AppPaths.DataDir, "books", book.Id.ToString());
        var pages = await Task.Run(() => Comic.Pages(file, dir));
        if (token != _opening) return;
        if (pages.Count == 0) throw new InvalidDataException("The comic has no pages.");
        _comicPages = pages;
        _direction.Text = Rtl ? "right to left" : "left to right";

        var layer = new FrameLayout(_a);
        layer.SetBackgroundColor(Color.Black);
        _comicImg = new ImageView(_a);
        _comicImg.SetScaleType(ImageView.ScaleType.FitCenter);
        layer.AddView(_comicImg, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        // Tap the right or left edge to turn the page (swapped for right-to-left reading); a sideways swipe works too.
        var zones = new LinearLayout(_a) { Orientation = Orientation.Horizontal };
        View Zone(int dir) { var v = new View(_a) { Clickable = true }; v.Click += (_, _) => TurnComic(dir); return v; }
        zones.AddView(Zone(-1), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 3));
        zones.AddView(new View(_a), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 4));
        zones.AddView(Zone(1), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 3));
        layer.AddView(zones, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        float downX = 0;
        layer.Touch += (_, e) =>
        {
            var m = e.Event;
            if (m == null) { e.Handled = false; return; }
            if (m.Action == MotionEventActions.Down) downX = m.GetX();
            else if (m.Action == MotionEventActions.Up && Math.Abs(m.GetX() - downX) > Th.Dp(60)) { TurnComic(m.GetX() < downX ? 1 : -1); e.Handled = true; return; }
            e.Handled = false;
        };
        _body.AddView(layer, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _status.Visibility = ViewStates.Gone;
        ShowComic(book.Finished ? 0 : book.Part);
    }

    /// <summary>+1 is the next page in reading order; on a manga the next page is to the left.</summary>
    void TurnComic(int screenDirection)
    {
        // Zones: -1 is the left edge, 1 the right edge.
        var step = Rtl ? -screenDirection : screenDirection;
        ShowComic(_comicPage + step);
    }

    void ShowComic(int page)
    {
        if (_book == null || _comicPages.Count == 0 || _comicImg == null) return;
        page = Math.Clamp(page, 0, _comicPages.Count - 1);
        _comicPage = page;
        var img = _comicImg;
        var shown = page;
        _ = Task.Run(async () =>
        {
            var bmp = await Art.LoadAsync(_comicPages[shown], 2400);
            if (page + 1 < _comicPages.Count) _ = Art.LoadAsync(_comicPages[page + 1], 2400);   // have the next page ready
            if (bmp != null) HalcyonApp.Main.Post(() => { if (_comicPage == shown && _comicImg == img) img.SetImageBitmap(bmp); });
        });
        _page.Text = $"page {page + 1} of {_comicPages.Count}";
        _book.Part = page;
        _book.Progress = (page + 1.0) / _comicPages.Count;
        _book.Finished = page == _comicPages.Count - 1;
        Save();
    }

    // ---------- PDF ----------

    void OpenPdf(string file)
    {
        _pdfFd = ParcelFileDescriptor.Open(new Java.IO.File(file), ParcelFileMode.ReadOnly);
        _pdf = new PdfRenderer(_pdfFd!);
        var count = _pdf.PageCount;
        var book = _book!;
        _pdfList = new ListView(_a) { Divider = null };
        _pdfList.SetBackgroundColor(Th.BgAlt);
        _pdfList.Adapter = new PdfAdapter(_a, _pdf, count);
        _pdfList.Scroll += (_, _) => UpdatePdfPosition(count);
        _body.AddView(_pdfList, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _status.Visibility = ViewStates.Gone;
        var at = book.Finished ? 0 : Math.Clamp(book.Part, 0, count - 1);
        _pdfList.Post(() => _pdfList?.SetSelection(at));
        if (book.Progress <= 0) book.Progress = 0.01;
        _page.Text = $"page {at + 1} of {count}";
        Save();
    }

    void UpdatePdfPosition(int count)
    {
        if (_pdfList == null || _book == null || !IsOpen) return;
        var first = _pdfList.FirstVisiblePosition;
        if (first == _book.Part && _page.Text?.StartsWith($"page {first + 1} ") == true) return;
        _book.Part = first;
        _book.Progress = count > 1 ? Math.Clamp((first + 1.0) / count, 0.01, 1) : 1;
        _book.Finished = first >= count - 1 && count > 1;
        _page.Text = $"page {first + 1} of {count}   ·   {Math.Max(1, (int)(_book.Progress * 100))}%";
        Save();
    }

    class PdfAdapter(Android.Content.Context c, PdfRenderer pdf, int count) : BaseAdapter
    {
        readonly object _lock = new();
        public override int Count => count;
        public override Java.Lang.Object? GetItem(int position) => null;
        public override long GetItemId(int position) => position;

        public override View? GetView(int position, View? convertView, ViewGroup? parent)
        {
            var img = convertView as ImageView ?? new ImageView(c);
            img.SetAdjustViewBounds(true);
            img.SetPadding(0, Th.Dp(4), 0, Th.Dp(4));
            var width = Math.Max(Th.ScreenW, 600);
            img.Tag = new Java.Lang.Integer(position);
            img.SetImageDrawable(new Android.Graphics.Drawables.ColorDrawable(Color.White));
            img.LayoutParameters = new AbsListView.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(width * 1.41f));
            _ = Task.Run(() =>
            {
                Bitmap? bmp = null;
                lock (_lock)
                {
                    try
                    {
                        using var page = pdf.OpenPage(position);
                        bmp = Bitmap.CreateBitmap(width, (int)(width * (double)page.Height / page.Width), Bitmap.Config.Argb8888!)!;
                        bmp.EraseColor(Color.White);
                        page.Render(bmp, null, null, PdfRenderMode.ForDisplay);
                    }
                    catch { }
                }
                if (bmp == null) return;
                HalcyonApp.Main.Post(() =>
                {
                    if ((img.Tag as Java.Lang.Integer)?.IntValue() != position) return;
                    img.SetImageBitmap(bmp);
                    img.LayoutParameters = new AbsListView.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                });
            });
            return img;
        }
    }
}
