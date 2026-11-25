using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Halcyon.Core;

namespace Halcyon.UI;

/// <summary>
/// Comic reader for .cbz / .cbr books: one page at a time, scaled to fit. Click the right or left edge, use the arrow
/// keys, Space, or the mouse wheel to turn pages; the place is saved on every turn.
/// </summary>
public class ComicWindow : Window
{
    static readonly Dictionary<long, ComicWindow> OpenReaders = new();

    readonly Book _book;
    readonly Image _image = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, SnapsToDevicePixels = true };
    readonly TextBlock _pageText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), MinWidth = 120 };
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(40) };
    readonly Button _dirBtn;
    readonly Button _marksBtn;
    List<string> _pages = new();
    bool _rtl;        // manga: the next page is to the left
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
        _dirBtn = Btn("left to right", "Reading direction (use right to left for manga)", () => { _rtl = !_rtl; UpdateDirection(); });
        right.Children.Add(_dirBtn);
        right.Children.Add(Btn("‹ back", "Previous page", () => Turn(-1)));
        right.Children.Add(Btn("next ›", "Next page", () => Turn(1)));
        right.Children.Add(Btn("☆", "Bookmark this page (Ctrl+D)", AddBookmark));
        _marksBtn = Btn("bookmarks", "Your bookmarks in this comic", ShowBookmarks);
        right.Children.Add(_marksBtn);
        right.Children.Add(_pageText);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(new TextBlock { Text = book.Title, VerticalAlignment = VerticalAlignment.Center, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis });

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        var body = new Grid { Background = Brushes.Transparent };
        body.Children.Add(_image);
        body.Children.Add(_status);
        body.MouseLeftButtonUp += (_, e) =>
        {
            var x = e.GetPosition(body).X / Math.Max(1, body.ActualWidth);
            if (x > 0.7) Turn(_rtl ? -1 : 1); else if (x < 0.3) Turn(_rtl ? 1 : -1);
        };
        body.MouseWheel += (_, e) =>
        {
            var now = Environment.TickCount64;
            if (now - _wheelAt < 150) return;
            _wheelAt = now;
            Turn(e.Delta < 0 ? 1 : -1);
        };
        root.Children.Add(body);
        Content = root;
        _status.Text = "opening…";

        Loaded += (_, _) => Opening = OpenAsync();
        Closed += (_, _) => OpenReaders.Remove(_book.Id);
        PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape: Close(); break;
                case Key.D when Keyboard.Modifiers == ModifierKeys.Control: AddBookmark(); break;
                case Key.Right: Turn(_rtl ? -1 : 1); break;
                case Key.Left: Turn(_rtl ? 1 : -1); break;
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
        var m = App.Lib.AddBookmark(_book, Page, 0, $"Page {Page + 1} of {_pages.Count}");
        _pageText.Text = $"bookmarked page {Page + 1}";
        UpdateMarks();
    }

    void ShowBookmarks()
    {
        if (App.Lib.BookmarkCount(_book) == 0) { _pageText.Text = "no bookmarks yet — choose ☆"; return; }
        new BookmarksDialog(_book, m => { Activate(); Show(m.Part); }).ShowDialog();
        UpdateMarks();
    }

    void UpdateMarks()
    {
        var n = App.Lib.BookmarkCount(_book);
        _marksBtn.Content = n == 0 ? "bookmarks" : $"bookmarks ({n})";
    }

    void UpdateDirection() => _dirBtn.Content = _rtl ? "right to left" : "left to right";

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
        catch (Exception ex) { _image.Visibility = Visibility.Collapsed; _status.Text = "This comic can't be opened.\n\n" + ex.Message; }
    }

    void Turn(int delta)
    {
        if (_pages.Count == 0) return;
        Show(Page + delta);
    }

    static BitmapImage Load(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.DecodePixelHeight = 2600;   // plenty for any screen; keeps huge scans light
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public void Show(int page)
    {
        if (_pages.Count == 0) return;
        Page = Math.Clamp(page, 0, _pages.Count - 1);
        try { _image.Source = Load(_pages[Page]); _status.Visibility = Visibility.Collapsed; }
        catch (Exception ex) { _image.Source = null; _status.Visibility = Visibility.Visible; _status.Text = "This page can't be shown.\n\n" + ex.Message; }
        _pageText.Text = $"page {Page + 1} of {_pages.Count}";
        _book.Part = Page;
        _book.Progress = (Page + 1.0) / _pages.Count;
        _book.PositionMs = _pages.Count;                       // for a comic this holds the number of pages (shown on its cover)
        if (Page == _pages.Count - 1) _book.Finished = true;   // read to the end: it stays marked read until started over by hand
        App.Lib.Db.SaveBookState(_book);
        _book.Raise();
        // have the next page ready
        if (Page + 1 < _pages.Count) { var next = _pages[Page + 1]; _ = Task.Run(() => { try { Load(next); } catch { } }); }
    }
}
