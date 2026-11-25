using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Halcyon.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Halcyon.UI;

/// <summary>
/// Ebook reader. EPUB chapters are shown one screen at a time (two facing pages on a wide window) in an embedded
/// Edge web view, with the app's own page layout injected; the place is saved on every page turn. PDFs use the
/// web view's built-in PDF viewer.
/// </summary>
public class ReaderWindow : Window
{
    const string Host = "book.halcyon";
    static readonly Dictionary<long, ReaderWindow> OpenReaders = new();

    readonly Book _book;
    readonly WebView2 _web = new();
    readonly TextBlock _pageText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), MinWidth = 150 };
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(40) };
    readonly ComboBox _toc = new() { Width = 280, VerticalAlignment = VerticalAlignment.Center };
    readonly TaskCompletionSource _ready = new();
    Button? _marksBtn;
    Epub? _epub;
    double _start;       // where to land in the chapter being loaded: 0..1, or -1 for its last page
    bool _settingToc;

    public int Chapter { get; private set; }
    public int Page { get; private set; }
    public int Pages { get; private set; } = 1;
    /// <summary>Completes when the first page has been laid out.</summary>
    public Task Ready => _ready.Task;
    public CoreWebView2 Core => _web.CoreWebView2;

    /// <summary>Opens the book at a saved place; if it is already open, turns to that place.</summary>
    public static ReaderWindow OpenAt(Book book, int chapter, double fraction)
    {
        if (OpenReaders.TryGetValue(book.Id, out var open)) { open.Activate(); open.Go(chapter, fraction); return open; }
        return Open(book);   // a new window starts from the book's saved place
    }

    public static ReaderWindow Open(Book book)
    {
        if (OpenReaders.TryGetValue(book.Id, out var open)) { open.Activate(); return open; }
        var w = new ReaderWindow(book);
        OpenReaders[book.Id] = w;
        w.Show();
        return w;
    }

    ReaderWindow(Book book)
    {
        _book = book;
        Title = book.Author == "" ? book.Title : $"{book.Title} — {book.Author}";
        Width = 1180; Height = 860; MinWidth = 520; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(FontFamilyProperty, "TextFont"); FontSize = 13;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");

        Button Btn(string text, string tip, Action click)
        {
            var b = new Button { Content = text, ToolTip = tip, Style = (Style)FindResource("TextBtn"), FontSize = 14, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(6, 2, 6, 2) };
            b.Click += (_, _) => click();
            return b;
        }
        var bar = new DockPanel { Height = 44, Margin = new Thickness(18, 0, 18, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (book.Format == "epub")
        {
            right.Children.Add(Btn("‹ back", "Previous page (Left arrow)", () => Run("halcyon.prev()")));
            right.Children.Add(Btn("next ›", "Next page (Right arrow or Space)", () => Run("halcyon.next()")));
            right.Children.Add(Btn("A−", "Smaller text", () => SetFont(-1)));
            right.Children.Add(Btn("A+", "Larger text", () => SetFont(1)));
            right.Children.Add(Btn("☆", "Bookmark this page (Ctrl+D)", AddBookmark));
            _marksBtn = Btn("bookmarks", "Your bookmarks in this book", ShowBookmarks);
            right.Children.Add(_marksBtn);
            right.Children.Add(_pageText);
        }
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        if (book.Format == "epub") bar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _toc } });
        else bar.Children.Add(new TextBlock { Text = book.Title, VerticalAlignment = VerticalAlignment.Center, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis });
        _toc.SelectionChanged += (_, _) => { if (!_settingToc && _toc.SelectedIndex >= 0) Go(_toc.SelectedIndex, 0); };

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        var body = new Grid();
        body.Children.Add(_web);
        body.Children.Add(_status);
        root.Children.Add(body);
        Content = root;
        _status.Text = "opening…";

        Loaded += async (_, _) =>
        {
            try { await StartAsync(); }
            catch (Exception ex) { _web.Visibility = Visibility.Collapsed; _status.Text = "This book can't be opened.\n\n" + ex.Message; _ready.TrySetException(ex); }
        };
        Closed += (_, _) => { OpenReaders.Remove(_book.Id); _web.Dispose(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control) { AddBookmark(); e.Handled = true; }
            else if (_epub != null && e.Key is Key.Right or Key.PageDown or Key.Space) { Run("halcyon.next()"); e.Handled = true; }
            else if (_epub != null && e.Key is Key.Left or Key.PageUp) { Run("halcyon.prev()"); e.Handled = true; }
        };
    }

    async Task StartAsync()
    {
        var file = _book.Source == "jellyfin" ? await App.Jellyfin.DownloadBookAsync(_book) : _book.SourceId;
        if (!File.Exists(file)) throw new FileNotFoundException("The book file is no longer where it was.", file);

        // Keep the browser profile with the app's own data, not next to the program.
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.DataDir, "webview"));
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;

        _book.LastOpened = Fmt.Now;
        UpdateMarks();
        if (_book.Format == "pdf")
        {
            core.SetVirtualHostNameToFolderMapping(Host, Path.GetDirectoryName(file)!, CoreWebView2HostResourceAccessKind.Allow);
            core.NavigationCompleted += (_, _) => { _status.Visibility = Visibility.Collapsed; _ready.TrySetResult(); };
            core.Navigate($"https://{Host}/{Uri.EscapeDataString(Path.GetFileName(file))}");
            if (_book.Progress <= 0) _book.Progress = 0.01;   // PDFs can't report a page, so just record that it was opened
            App.Lib.Db.SaveBookState(_book);
            _book.Raise();
            return;
        }

        _epub = Epub.Open(file);
        if (_epub.Chapters.Count == 0) throw new InvalidDataException("The book has no readable chapters.");
        var dir = Path.Combine(AppPaths.DataDir, "books", _book.Id.ToString());
        var stamp = Path.Combine(dir, ".extracted");
        var signature = $"{file}|{File.GetLastWriteTimeUtc(file).Ticks}";
        if (!File.Exists(stamp) || File.ReadAllText(stamp) != signature)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            await Task.Run(() => Epub.Extract(file, dir));
            File.WriteAllText(stamp, signature);
        }

        _settingToc = true;
        foreach (var c in _epub.Chapters) _toc.Items.Add(c.Title);
        _settingToc = false;

        core.Settings.AreDefaultContextMenusEnabled = false;
        core.SetVirtualHostNameToFolderMapping(Host, dir, CoreWebView2HostResourceAccessKind.Allow);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScript);
        core.WebMessageReceived += OnMessage;
        // A book stays inside its own files: links to the web are not followed.
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
        Go(_book.Finished ? 0 : _book.Part, _book.Finished ? 0 : _book.Fraction);
    }

    void AddBookmark()
    {
        if (_epub == null) return;
        var fraction = Pages > 1 ? (double)Page / (Pages - 1) : 0;
        var title = _epub.Chapters[Chapter].Title;
        var m = App.Lib.AddBookmark(_book, Chapter, fraction, $"{title}  ·  page {Page + 1} of {Pages}");
        _pageText.Text = $"bookmarked: {m.Label}";
        UpdateMarks();
    }

    void ShowBookmarks()
    {
        if (App.Lib.BookmarkCount(_book) == 0) { _pageText.Text = "no bookmarks yet — choose ☆ to add one"; return; }
        new BookmarksDialog(_book, m => { Activate(); Go(m.Part, m.Position); }).ShowDialog();
        UpdateMarks();
    }

    void UpdateMarks()
    {
        if (_marksBtn == null) return;
        var n = App.Lib.BookmarkCount(_book);
        _marksBtn.Content = n == 0 ? "bookmarks" : $"bookmarks ({n})";
    }

    void Run(string script)
    {
        if (_web.CoreWebView2 != null) _ = _web.CoreWebView2.ExecuteScriptAsync(script);
    }

    public Task NextAsync() => _web.CoreWebView2.ExecuteScriptAsync("halcyon.next()");
    public Task PrevAsync() => _web.CoreWebView2.ExecuteScriptAsync("halcyon.prev()");

    void SetFont(int delta)
    {
        App.Settings.ReaderFontSize = Math.Clamp(App.Settings.ReaderFontSize + delta * 2, 12, 40);
        Run($"halcyon.font({App.Settings.ReaderFontSize})");
    }

    void Go(int chapter, double start)
    {
        if (_epub == null) return;
        Chapter = Math.Clamp(chapter, 0, _epub.Chapters.Count - 1);
        _start = start;
        var url = string.Join("/", _epub.Chapters[Chapter].Href.Split('/').Select(Uri.EscapeDataString));
        _web.CoreWebView2.Navigate($"https://{Host}/{url}");
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_epub == null) return;
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        switch (m.GetProperty("type").GetString())
        {
            case "ready":
                // A link inside the book may have led to another chapter: follow where the page really is.
                var path = Uri.UnescapeDataString(m.GetProperty("path").GetString() ?? "").TrimStart('/');
                var found = _epub.Chapters.FindIndex(c => string.Equals(c.Href, path, StringComparison.OrdinalIgnoreCase));
                if (found >= 0 && found != Chapter) { Chapter = found; _start = 0; }
                var theme = Themes.Current;
                Run($"halcyon.init({JsonSerializer.Serialize(new { fontSize = App.Settings.ReaderFontSize, start = _start, bg = Themes.Hex(theme.Get("Bg")), fg = Themes.Hex(Themes.Over(theme.Get("Fg"), theme.Get("Bg"))) })})");
                break;
            case "state":
                Page = m.GetProperty("page").GetInt32();
                Pages = Math.Max(1, m.GetProperty("pages").GetInt32());
                _status.Visibility = Visibility.Collapsed;
                _settingToc = true;
                _toc.SelectedIndex = Chapter;
                _settingToc = false;
                var chapters = _epub.Chapters.Count;
                _book.Part = Chapter;
                _book.Fraction = Pages > 1 ? (double)Page / (Pages - 1) : 0;
                _book.Progress = Math.Clamp((Chapter + (Page + 1.0) / Pages) / chapters, 0, 1);
                _book.Finished = Chapter == chapters - 1 && Page == Pages - 1;
                _pageText.Text = $"page {Page + 1} of {Pages}   ·   {Math.Max(1, (int)(_book.Progress * 100))}%";
                App.Lib.Db.SaveBookState(_book);
                _book.Raise();
                _ready.TrySetResult();
                break;
            case "edge":
                var dir = m.GetProperty("dir").GetInt32();
                if (dir > 0 && Chapter < _epub.Chapters.Count - 1) Go(Chapter + 1, 0);
                else if (dir < 0 && Chapter > 0) Go(Chapter - 1, -1);
                break;
        }
    }

    // Runs in every chapter before its own content: lays the text out in screen-wide columns and turns pages by
    // sliding the body sideways. It talks to the window only through postMessage.
    const string PageScript = """
        (() => {
          if (window.top !== window) return;
          const post = m => window.chrome.webview.postMessage(m);
          let S = null, page = 0, pages = 1, moved = false, wheelAt = 0, resizeTimer = 0;
          const frac = () => pages > 1 ? page / (pages - 1) : 0;

          function show(p) {
            page = Math.max(0, Math.min(pages - 1, p));
            document.body.style.transform = `translateX(${-page * window.innerWidth}px)`;
            post({ type: 'state', page, pages });
          }
          function go(p) {
            moved = true;
            if (p < 0) post({ type: 'edge', dir: -1 });
            else if (p >= pages) post({ type: 'edge', dir: 1 });
            else show(p);
          }
          function layout(at) {
            if (!S || !document.body) return;
            const W = window.innerWidth, two = W >= 1150;
            const M = two ? 56 : Math.max(36, Math.min(170, (W - 720) / 2));
            const colW = (two ? W / 2 : W) - 2 * M;
            let st = document.getElementById('halcyon-style');
            if (!st) { st = document.createElement('style'); st.id = 'halcyon-style'; (document.head || document.documentElement).appendChild(st); }
            st.textContent = `
              html { height: 100% !important; overflow: hidden !important; background: ${S.bg} !important; }
              body { margin: 0 !important; padding: 44px ${M}px !important; box-sizing: border-box !important; height: 100vh !important;
                     width: auto !important; max-width: none !important; min-width: 0 !important;
                     column-width: ${colW}px !important; column-gap: ${2 * M}px !important; column-fill: auto !important;
                     font-size: ${S.fontSize}px !important; line-height: 1.6 !important;
                     background: ${S.bg} !important; color: ${S.fg} !important; overflow: visible !important; }
              body * { color: inherit !important; background-color: transparent !important; max-width: 100% !important; }
              img, svg, video { max-height: calc(100vh - 120px) !important; object-fit: contain; break-inside: avoid; }
              p { orphans: 2; widows: 2; }
              ::-webkit-scrollbar { display: none; }`;
            requestAnimationFrame(() => {
              const width = Math.max(document.body.scrollWidth, document.documentElement.scrollWidth);
              pages = Math.max(1, Math.ceil(width / W - 0.02));
              show(at < 0 ? pages - 1 : Math.round(at * (pages - 1)));
            });
          }

          window.halcyon = {
            init(s) { S = s; layout(S.start); },
            next() { go(page + 1); },
            prev() { go(page - 1); },
            font(px) { if (S) { S.fontSize = px; layout(frac()); } }
          };
          document.addEventListener('DOMContentLoaded', () => post({ type: 'ready', path: location.pathname }));
          // Pictures arriving late change the page count: settle again, staying where the reader was sent.
          window.addEventListener('load', () => { if (S) layout(moved ? frac() : S.start); });
          window.addEventListener('resize', () => { clearTimeout(resizeTimer); resizeTimer = setTimeout(() => layout(frac()), 120); });
          window.addEventListener('keydown', e => {
            if (['ArrowRight', 'PageDown', ' '].includes(e.key)) { go(page + 1); e.preventDefault(); }
            else if (['ArrowLeft', 'PageUp'].includes(e.key)) { go(page - 1); e.preventDefault(); }
            else if (e.key === 'Home') { go(0); e.preventDefault(); }
            else if (e.key === 'End') { go(pages - 1); e.preventDefault(); }
          });
          window.addEventListener('wheel', e => {
            e.preventDefault();
            const now = Date.now();
            if (now - wheelAt < 260 || Math.abs(e.deltaY) < 4) return;
            wheelAt = now;
            go(page + (e.deltaY > 0 ? 1 : -1));
          }, { passive: false });
          window.addEventListener('click', e => {
            if (e.target.closest('a')) return;
            if (e.clientX > window.innerWidth * 0.82) go(page + 1);
            else if (e.clientX < window.innerWidth * 0.18) go(page - 1);
          });
        })();
        """;
}
