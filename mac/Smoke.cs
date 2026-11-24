using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Halcyon.Core;

namespace Halcyon.Desktop;

/// <summary>
/// Developer self-test: `Halcyon.exe --smoke &lt;dir&gt;` (with HALCYON_DATA pointing at a throwaway folder) opens each screen
/// of the Mac/desktop app over a sample library, saves screenshots and checks a few behaviours. Run on Windows to test the UI.
/// </summary>
public static class Smoke
{
    static readonly StringBuilder Log = new();
    static int _fails;
    static string _dir = "";

    static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _fails++;
        Log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
    }

    public static async Task RunAsync(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        try { await RunCore(); }
        catch (Exception ex) { _fails++; Log.AppendLine("EXCEPTION " + ex); }
        Log.AppendLine(_fails == 0 ? "ALL PASSED" : $"{_fails} FAILED");
        File.WriteAllText(Path.Combine(dir, "smoke.log"), Log.ToString());
        Environment.Exit(_fails == 0 ? 0 : 1);
    }

    static async Task Settle(int ms = 350)
    {
        await Task.Delay(ms);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    static async Task Shot(string name)
    {
        await Settle();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var win = HalcyonApp.Win;
            var size = new PixelSize((int)win.Bounds.Width, (int)win.Bounds.Height);
            if (size.Width < 10) return;
            using var bmp = new RenderTargetBitmap(size, new Vector(96, 96));
            bmp.Render(win);
            bmp.Save(Path.Combine(_dir, name + ".png"));
        });
    }

    /// <summary>A flat-colour picture: enough to tell the comic pages apart.</summary>
    static byte[] Png(int shade)
    {
        using var bmp = new WriteableBitmap(new PixelSize(64, 96), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        using (var fb = bmp.Lock())
        {
            var pixels = new byte[fb.RowBytes * fb.Size.Height];
            for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = (byte)shade; pixels[i + 1] = (byte)(255 - shade); pixels[i + 2] = (byte)(shade / 2); pixels[i + 3] = 255; }
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms);
        return ms.ToArray();
    }

    static void WriteWav(string path, int seconds)
    {
        const int rate = 8000;
        var n = rate * seconds;
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write("RIFF"u8); bw.Write(36 + n); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1); bw.Write(rate); bw.Write(rate); bw.Write((short)1); bw.Write((short)8);
        bw.Write("data"u8); bw.Write(n);
        bw.Write(Enumerable.Repeat((byte)128, n).ToArray());
    }

    /// <summary>
    /// An uncompressed AVI (24-bit frames, 10 per second) whose colour changes over time, so a thumbnail can be told
    /// from a blank frame. Small enough to write in a test, and every VLC can play it.
    /// </summary>
    static void WriteAvi(string path, int width, int height, int frames)
    {
        var frameSize = width * 3 * height;   // width is a multiple of 4, so rows need no padding
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void Four(string s) => w.Write(Encoding.ASCII.GetBytes(s));
        long Begin(string id) { Four(id); w.Write(0); return ms.Position; }
        void End(long start) { var end = ms.Position; ms.Position = start - 4; w.Write((int)(end - start)); ms.Position = end; }

        var riff = Begin("RIFF"); Four("AVI ");
        var hdrl = Begin("LIST"); Four("hdrl");
        var avih = Begin("avih");
        w.Write(100_000); w.Write(frameSize * 10); w.Write(0); w.Write(0x10); w.Write(frames); w.Write(0); w.Write(1); w.Write(frameSize);
        w.Write(width); w.Write(height); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        End(avih);
        var strl = Begin("LIST"); Four("strl");
        var strh = Begin("strh");
        Four("vids"); Four("DIB "); w.Write(0); w.Write((short)0); w.Write((short)0); w.Write(0); w.Write(1); w.Write(10); w.Write(0); w.Write(frames);
        w.Write(frameSize); w.Write(-1); w.Write(0); w.Write((short)0); w.Write((short)0); w.Write((short)width); w.Write((short)height);
        End(strh);
        var strf = Begin("strf");
        w.Write(40); w.Write(width); w.Write(height); w.Write((short)1); w.Write((short)24); w.Write(0); w.Write(frameSize); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        End(strf);
        End(strl);
        End(hdrl);
        var movi = Begin("LIST"); var moviData = ms.Position; Four("movi");
        var offsets = new List<int>();
        var frame = new byte[frameSize];
        for (var f = 0; f < frames; f++)
        {
            for (var i = 0; i < frameSize; i += 3) { frame[i] = (byte)(40 + f * 4); frame[i + 1] = (byte)(200 - f * 3); frame[i + 2] = (byte)(90 + (i / 3 % width) * 150 / width); }
            offsets.Add((int)(ms.Position - moviData));
            var chunk = Begin("00db"); w.Write(frame); End(chunk);
        }
        End(movi);
        var idx = Begin("idx1");
        foreach (var o in offsets) { Four("00db"); w.Write(0x10); w.Write(o); w.Write(frameSize); }
        End(idx);
        End(riff);
        File.WriteAllBytes(path, ms.ToArray());
    }

    static async Task RunCore()
    {
        var win = HalcyonApp.Win;
        var lib = App.Lib;
        win.Width = 1320; win.Height = 820;
        // the built-in typeface: each weight must come from Selawik, not a system fallback
        foreach (var (label, weight) in new[] { ("regular", Avalonia.Media.FontWeight.Normal), ("light", Avalonia.Media.FontWeight.Light), ("semibold", Avalonia.Media.FontWeight.SemiBold), ("bold", Avalonia.Media.FontWeight.Bold) })
        {
            var ok = Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(new Avalonia.Media.Typeface(new Avalonia.Media.FontFamily(Program.Font), Avalonia.Media.FontStyle.Normal, weight), out var face);
            Check($"Selawik {label} is built in", ok && face!.FamilyName.StartsWith("Selawik") && Math.Abs((int)face.Weight - (int)weight) <= 100, ok ? $"{face!.FamilyName} {face.Weight}" : "not found");
        }
        await Settle(600);
        Check("app starts with an empty library", lib.Tracks.Count == 0);
        await Shot("01-empty");

        // a small library: the three kinds of book
        var root = Path.Combine(_dir, "media");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var books = Path.Combine(root, "books");
        Directory.CreateDirectory(books);

        var cbz = Path.Combine(books, "Hero Tales 3.cbz");
        var pages = new[] { Png(40), Png(120), Png(200) };
        using (var zip = System.IO.Compression.ZipFile.Open(cbz, System.IO.Compression.ZipArchiveMode.Create))
        {
            void Add(string name, byte[] bytes) { using var s = zip.CreateEntry(name).Open(); s.Write(bytes); }
            Add("page10.png", pages[2]); Add("page1.png", pages[0]); Add("page2.png", pages[1]);
            Add("ComicInfo.xml", Encoding.UTF8.GetBytes("<ComicInfo><Series>Hero Tales</Series><Number>3</Number><Writer>Pat Pencil</Writer></ComicInfo>"));
        }
        var epub = Path.Combine(books, "quiet.epub");
        using (var zip = System.IO.Compression.ZipFile.Open(epub, System.IO.Compression.ZipArchiveMode.Create))
        {
            void Add(string name, string text) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(text); }
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""");
            Add("OEBPS/content.opf", """<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>A Quiet Book</dc:title><dc:creator>Sam Author</dc:creator><dc:identifier id="id">t1</dc:identifier></metadata><manifest><item id="c1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/><item id="c2" href="text/ch2.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="c1"/><itemref idref="c2"/></spine></package>""");
            var filler = string.Concat(Enumerable.Range(1, 40).Select(i => $"<p>Paragraph {i}. The road went on past the last houses and into the hills.</p>"));
            Add("OEBPS/text/ch1.xhtml", $"<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>One</title></head><body><h1>Opening</h1>{filler}</body></html>");
            Add("OEBPS/text/ch2.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Two</title></head><body><h1>The End</h1><p>And that was all.</p></body></html>");
        }
        // a small music library, so the screens can be compared with the Windows app's
        var music = Path.Combine(root, "music");
        var albums = new[] { ("Quiet Trio", "First Record", 40, "Jazz"), ("Second Example", "Later Record", 120, "Rock"), ("The Example Band", "First Record", 200, "Pop") };
        foreach (var (artist, album, shade, genre) in albums)
        {
            var dir = Path.Combine(music, artist, album);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "cover.png"), Png(shade));
            for (var n = 1; n <= 4; n++)
            {
                var f = Path.Combine(dir, $"{n:00} Song {n}.wav");
                WriteWav(f, 4);
                using var tf = TagLib.File.Create(f);
                tf.Tag.Title = $"Song {n}"; tf.Tag.Performers = new[] { artist }; tf.Tag.AlbumArtists = new[] { artist }; tf.Tag.Album = album; tf.Tag.Track = (uint)n; tf.Tag.Genres = new[] { genre };
                tf.Save();
            }
        }
        App.Settings.MusicFolders = new() { music };
        var pictures = Path.Combine(root, "pictures");
        Directory.CreateDirectory(pictures);
        for (var i = 1; i <= 3; i++) File.WriteAllBytes(Path.Combine(pictures, $"photo {i}.png"), Png(60 * i));
        File.WriteAllBytes(Path.Combine(pictures, "photo 4.png"), Encoding.ASCII.GetBytes("not really a picture"));
        App.Settings.PictureFolders = new() { pictures };
        var videos = Path.Combine(root, "videos");
        Directory.CreateDirectory(videos);
        var clip = Path.Combine(videos, "Sample Clip.avi");
        WriteAvi(clip, 320, 180, 40);
        App.Settings.VideoFolders = new() { videos };
        App.Settings.BookFolders = new() { books };
        await App.RefreshCollectionAsync();
        await Settle();
        var comic = lib.Books.FirstOrDefault(b => b.Format == "comic");
        var ebook = lib.Books.FirstOrDefault(b => b.Format == "epub");
        Check("books scanned", lib.Books.Count == 2, $"{lib.Books.Count} books");
        Check("comic metadata from ComicInfo.xml", comic is { Title: "Hero Tales #3", Author: "Pat Pencil" } && File.Exists(comic.ArtPath), comic?.Title ?? "missing");
        Check("epub metadata", ebook is { Title: "A Quiet Book", Author: "Sam Author" }, ebook?.Title ?? "missing");

        Check("music scanned", lib.Tracks.Count == 12 && lib.Albums.Count == 3, $"{lib.Tracks.Count} songs, {lib.Albums.Count} albums");
        var vid = lib.Videos.FirstOrDefault();
        Check("video scanned with a thumbnail marker", vid is { } && vid.ArtPath.StartsWith("thumb:"), vid?.ArtPath ?? "none");
        if (vid != null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var thumb = await Art.LoadAsync(vid.ArtPath, 200);
            var saved = Directory.GetFiles(AppPaths.ArtDir, "video-*.png");
            Check("video thumbnail is made from the video", thumb != null && thumb.PixelSize.Width > 100 && saved.Length == 1,
                $"{(thumb == null ? "none" : $"{thumb.PixelSize.Width}x{thumb.PixelSize.Height}")}, {saved.Length} saved, {sw.ElapsedMilliseconds} ms");
            if (saved.Length == 1)
            {
                // not a blank frame: the clip is coloured
                using var check = new Bitmap(saved[0]);
                Check("video thumbnail has the picture's shape", Math.Abs(check.PixelSize.Width / (double)check.PixelSize.Height - 320 / 180.0) < 0.05, $"{check.PixelSize.Width}x{check.PixelSize.Height}");
                File.Copy(saved[0], Path.Combine(_dir, "09-video-thumbnail.png"), true);
            }
            // pictures: one click opens the viewer; the arrows move on even past a picture that can't be read
            win.Navigate("pictures");
            var pics = win.Page<PicturesView>("pictures");
            await pics.ReloadAsync();
            await Settle(500);
            await Shot("10-pictures");
            // click the second tile as the mouse would: a pointer press on the picture inside it
            var tiles = pics.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("tile")).ToList();
            var image = tiles.Count > 1 ? tiles[1].GetVisualDescendants().OfType<Image>().FirstOrDefault() : null;
            if (image != null)
            {
                var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true);
                var props = new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed);
                image.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(image, pointer, win, new Point(20, 20), 0, props, Avalonia.Input.KeyModifiers.None, 1));
                await Settle(400);
            }
            Check("one click on a picture opens it in the viewer", pics.ViewerOpen && pics.ViewerCaption.StartsWith("photo 2.png"), image == null ? "no tile found" : pics.ViewerCaption);
            await Shot("10-picture-clicked");
            pics.Close();
            pics.OpenAt(0);
            await Settle(400);
            Check("picture viewer opens on the first picture", pics.ShowingPicture && pics.ViewerCaption.StartsWith("photo 1.png") && pics.ViewerCaption.Contains("1 of 4"), pics.ViewerCaption);
            // clicking the arrow twice quickly is a double-click on it: the viewer must stay open
            if (pics.NextButton is { } nextBtn)
            {
                nextBtn.RaiseEvent(new Avalonia.Input.TappedEventArgs(Avalonia.Input.Gestures.DoubleTappedEvent, null!));
                await Settle(200);
            }
            Check("a quick double click on the arrow doesn't close the viewer", pics.ViewerOpen, pics.ViewerCaption);
            pics.Close();
            pics.OpenAt(0);
            await Settle(300);
            pics.Next(); await Settle(300);
            Check("next arrow moves to the next picture", pics.ShowingPicture && pics.ViewerCaption.StartsWith("photo 2.png"), pics.ViewerCaption);
            pics.Next(); await Settle(300); pics.Next(); await Settle(400);
            Check("an unreadable picture says so instead of keeping the last one up", !pics.ShowingPicture && pics.ViewerCaption.StartsWith("photo 4.png") && pics.ViewerCaption.Contains("can't be shown"), pics.ViewerCaption);
            pics.Next(); await Settle(300);
            Check("next arrow carries on past it", pics.ShowingPicture && pics.ViewerCaption.StartsWith("photo 1.png"), pics.ViewerCaption);
            await Shot("10-picture-viewer");
            pics.Close();

            win.Navigate("videos");
            win.Page<VideosView>("videos").ShowTab("all");
            await Shot("09-videos");

            // play it: VLC's frames must arrive in the app's own image (no native video window)
            HalcyonApp.Engine.Volume = 0;
            App.Player.Play(new[] { Menus.Item(vid) });
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (waited.ElapsedMilliseconds < 8000 && (win.Frames.FramesShown < 10 || App.Player.PositionMs < 600)) await Task.Delay(100);
            Check("video frames are drawn in the window", win.Frames.FramesShown >= 10 && win.Frames.Size.Width == 320 && win.Frames.Size.Height == 180,
                $"{win.Frames.FramesShown} frames, {win.Frames.Size.Width}x{win.Frames.Size.Height}, {waited.ElapsedMilliseconds} ms");
            Check("video plays with the clock running", App.Player.PositionMs >= 600, $"{App.Player.PositionMs} ms");
            await Shot("09-video-playing");
            App.Player.Stop();
            win.ShowNow(false);
        }
        lib.Rate(new[] { lib.Tracks[1] }, 1);
        win.Navigate("books");
        await Shot("02-books");
        var longPath = Path.Combine(root, "A very long folder name that goes on and on", "and a second level that is also long", "and a third so the path cannot fit in the box", "My Music");
        App.Settings.MusicFolders.Add(longPath);
        win.Navigate("settings");
        var settings = win.Page<SettingsView>("settings");
        settings.ShowCategory("software");
        Check("settings open on Software, Collection", settings.Shown == "Collection");
        await Shot("03-settings");
        var pathText = settings.GetVisualDescendants().OfType<PathText>().FirstOrDefault(t => t.Path == longPath);
        var sepChar = Path.DirectorySeparatorChar;
        Check("a long folder path is shortened to fit its box, keeping its start and its last folder",
            pathText != null && pathText.Shown != longPath && pathText.Shown.Contains(sepChar + "..." + sepChar) && pathText.Shown.EndsWith(sepChar + "My Music")
                && longPath.StartsWith(pathText.Shown[..pathText.Shown.IndexOf("...")]) && pathText.ShownWidth <= pathText.Bounds.Width + 1 && pathText.Bounds.Width < 620,
            pathText == null ? "not found" : $"{pathText.Shown}  ({pathText.ShownWidth:0} of {pathText.Bounds.Width:0} px)");
        var shortText = settings.GetVisualDescendants().OfType<PathText>().FirstOrDefault(t => t.Path != longPath && t.Path is { Length: > 0 and < 30 });
        if (shortText != null) Check("a short folder path is shown whole", shortText.Shown == shortText.Path, shortText.Shown);
        App.Settings.MusicFolders.Remove(longPath);
        settings.ShowCategory("servers", "Plex");
        Check("settings: Servers shows Plex", settings.Shown == "Plex");
        await Shot("03-settings-plex");
        settings.ShowCategory("software");
        win.Navigate("quickplay");
        await Shot("04-quickplay");
        win.Navigate("music");
        await Shot("05-music");
        win.Page<MusicView>("music").SetMode("songs");
        await Shot("05-music-songs");
        win.Page<MusicView>("music").SetMode("artists");
        HalcyonApp.ApplyTheme("dark");
        win.Navigate("quickplay");
        await Shot("08-dark-quickplay");
        win.Navigate("music");
        await Shot("08-dark-music");
        HalcyonApp.ApplyTheme("light");

        if (comic != null)
        {
            var reader = ComicWindow.Open(comic);
            await Task.WhenAny(reader.Opening ?? Task.Delay(1), Task.Delay(15000));
            reader.Show(1);
            Check("comic reader opens and turns pages", reader.Pages == 3 && reader.Page == 1 && Math.Abs(comic.Progress - 2.0 / 3) < 0.01, $"{reader.Pages} pages, on {reader.Page + 1}");
            reader.Show(99);
            Check("comic reader ends on the last page", reader.Page == 2 && comic.Finished);
            reader.Close();
        }
        if (ebook != null)
        {
            var reader = EpubWindow.Open(ebook);
            await Task.WhenAny(reader.Opening ?? Task.Delay(1), Task.Delay(15000));
            await Settle();
            Check("epub reader shows the chapter text", reader.ChapterText.Contains("Opening") && reader.ChapterText.Contains("Paragraph 7") && !reader.ChapterText.Contains("<p>"), reader.ChapterText.Length + " characters");
            reader.Close();
            Check("epub text extraction", EpubWindow.HtmlText("<p>One &amp; two</p><p>Three<br/>four</p>") == "One & two\n\nThree\n\nfour");
        }

        // --- visualizer styles: draw each one from a synthetic signal and check nothing throws ---
        {
            var viz = new Viz { Width = 900, Height = 520 };
            var holder = new Window { Width = 900, Height = 520, Content = viz, Background = Avalonia.Media.Brushes.Black };
            holder.Show();
            await Settle(300);
            var failed = new List<string>();
            for (var style = 1; style < Viz.Styles.Length; style++)
            {
                viz.Mode = style;
                viz.Palette = (style - 1) % Viz.Palettes.Length;
                for (var frame = 0; frame < 40; frame++)
                {
                    // a kick drum every few frames over a chord
                    var kick = frame % 8 < 2 ? 0.8f : 0.05f;
                    var samples = new float[1024];
                    for (var i = 0; i < samples.Length; i++)
                        samples[i] = (float)(kick * Math.Sin(2 * Math.PI * 60 * (frame * 1024 + i) / 44100.0) + 0.25 * Math.Sin(2 * Math.PI * 440 * (frame * 1024 + i) / 44100.0) + 0.15 * Math.Sin(2 * Math.PI * 3000 * (frame * 1024 + i) / 44100.0));
                    viz.Spectrum.Feed(samples, 44100);
                    viz.Step();
                }
                try
                {
                    using var bmp = new RenderTargetBitmap(new PixelSize(900, 520), new Vector(96, 96));
                    viz.InvalidateVisual();
                    bmp.Render(viz);
                    if (style is 8 or 11 or 12 or 14) bmp.Save(Path.Combine(_dir, $"07-viz-{style}-{Viz.Styles[style].Replace(' ', '-')}.png"));
                }
                catch (Exception ex) { failed.Add(Viz.Styles[style] + ": " + ex.Message); }
            }
            Check("every visualizer style draws", failed.Count == 0, string.Join("; ", failed));
            holder.Close();
        }

        // --- bookmarks ---
        if (comic != null && ebook != null)
        {
            var m1 = lib.AddBookmark(comic, 2, 0);
            var m2 = lib.AddBookmark(comic, 0, 0, "  Cover  ");
            Check("bookmarks listed in reading order with default or custom labels", lib.BookmarksOf(comic).Select(m => m.Label).SequenceEqual(new[] { "Cover", "Page 3" }), string.Join(" | ", lib.BookmarksOf(comic).Select(m => m.Label)));
            var reader = ComicWindow.Open(comic);
            await Task.WhenAny(reader.Opening ?? Task.Delay(1), Task.Delay(15000));
            reader.Show(0);
            BooksView.OpenBookmark(comic, m1);
            Check("jumping to a comic bookmark turns the open reader to that page", reader.Page == 2, $"on page {reader.Page + 1}");
            reader.Close();
            var em = lib.AddBookmark(ebook, 1, 0.5);
            var er = EpubWindow.Open(ebook);
            await Task.WhenAny(er.Opening ?? Task.Delay(1), Task.Delay(15000));
            await Settle();
            BooksView.OpenBookmark(ebook, em);
            await Settle();
            Check("jumping to an ebook bookmark turns the open reader to that chapter", er.Chapter == 1 && er.ChapterText.Contains("The End"), $"chapter {er.Chapter + 1}");
            er.Close();
            lib.DeleteBookmark(m2);
            Check("deleting a bookmark removes it", lib.BookmarkCount(comic) == 1);
            win.Navigate("books");
            await Shot("06-books-after");
        }
    }
}
