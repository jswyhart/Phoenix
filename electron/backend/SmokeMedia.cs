using System.IO.Compression;
using System.Text;
using Halcyon.Core;

namespace Phoenix.Backend;

/// <summary>
/// The self-test's sample library (the same one the Avalonia build's self-test makes): three tagged albums, four
/// pictures (one unreadable), a short video, a comic and an ebook. Only available when started with --smoke.
/// </summary>
public static partial class Api
{
    static void RegisterSmoke()
    {
        if (!Program.SmokeMode) return;
        Rpc.On("smoke.media", async p =>
        {
            var root = Path.GetFullPath(Path.Combine(p.Str("dir"), "media"));
            SmokeMedia.Write(root);
            S.MusicFolders = new() { Path.Combine(root, "music") };
            S.PictureFolders = new() { Path.Combine(root, "pictures") };
            S.VideoFolders = new() { Path.Combine(root, "videos") };
            S.BookFolders = new() { Path.Combine(root, "books") };
            await App.RefreshCollectionAsync();
            return new { root, counts = Counts(), sep = Path.DirectorySeparatorChar.ToString() };
        });
        Rpc.On("smoke.books", _ => Lib.Books.Select(b => new
        {
            id = b.Id, format = b.Format, title = b.Title, author = b.Author, artExists = File.Exists(b.ArtPath), progress = b.Progress,
            finished = b.Finished, part = b.Part, marks = Lib.BookmarksOf(b).Select(m => m.Label)
        }));
        Rpc.On("smoke.videos", _ => Lib.Videos.Select(v => new { id = v.Id, art = v.ArtPath, title = v.Title }));
        Rpc.On("smoke.htmlText", p => HtmlText(p.Str("html")));
        // listens for the stats page: each song given is logged as heard in full, a minute apart, ending now
        Rpc.On("smoke.plays", p =>
        {
            var tracks = Tracks(p);
            var at = Fmt.Now - tracks.Count * 60;
            foreach (var t in tracks) { Lib.Db.LogPlay(new PlayRow(at, t.Id, t.Title, t.Artist, t.Album, Math.Max(t.DurationMs, 1000), true)); at += 60; }
            return tracks.Count;
        });
        // a stand-in for a streaming service's page: a song in an <audio> element and a media session, as a web player has
        Rpc.On("smoke.webPage", p =>
        {
            var song = Lib.Tracks.Where(t => t.IsLocal).OrderBy(t => t.Id).First();
            var html = $$"""
                <!doctype html><html><head><meta charset="utf-8"><title>Stand-in Player</title></head>
                <body style="font: 20px sans-serif"><h1>Stand-in Player</h1>
                <audio id="a" src="{{new Uri(song.SourceId).AbsoluteUri}}" loop></audio>
                <script>
                  var a = document.getElementById('a');
                  navigator.mediaSession.metadata = new MediaMetadata({ title: 'Web Song', artist: 'Web Artist', album: 'Web Album' });
                  navigator.mediaSession.setActionHandler('play', function () { a.play(); });
                  navigator.mediaSession.setActionHandler('pause', function () { a.pause(); });
                  navigator.mediaSession.setActionHandler('nexttrack', function () { document.title = 'next pressed'; });
                </script></body></html>
                """;
            var file = Path.Combine(p.Str("dir"), "standin-player.html");
            File.WriteAllText(file, html);
            return new Uri(file).AbsoluteUri;
        });
        // a second copy of a song (for the duplicate finder), and taking a file away (for the missing-file finder)
        Rpc.On("smoke.copySong", async p =>
        {
            var t = Lib.ById[p.Long("id")];
            var copy = Path.Combine(Path.GetDirectoryName(t.SourceId)!, "copy of " + Path.GetFileName(t.SourceId));
            File.Copy(t.SourceId, copy, true);
            await App.RefreshCollectionAsync();
            return new { copy, tracks = Lib.Tracks.Count };
        });
        Rpc.On("smoke.deleteFile", p => { File.Delete(p.Str("path")); return !File.Exists(p.Str("path")); });
        Rpc.On("smoke.playback", p => new
        {
            handovers = Program.Engine.Handovers,
            options = LevelOptions(Lib.ById[p.Long("id")].SourceId, false).ToList()
        });
        // a stand-in podcast on this machine: a feed with three episodes, each one of the sample songs
        Rpc.On("smoke.podcastFeed", feedParams =>
        {
            var port = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            port.Start();
            var n = ((System.Net.IPEndPoint)port.LocalEndpoint).Port;
            port.Stop();
            var root = $"http://localhost:{n}/";
            var listener = new System.Net.HttpListener();
            listener.Prefixes.Add(root);
            listener.Start();
            var audio = File.ReadAllBytes(Lib.Tracks.First(t => t.IsLocal).SourceId);
            var items = string.Concat(Enumerable.Range(1, 3).Select(i =>
                $"<item><title>Episode {i}</title><guid>ep{i}</guid><pubDate>{DateTime.UtcNow.AddDays(-i):R}</pubDate><enclosure url=\"{root}ep{i}.wav\" type=\"audio/wav\" length=\"{audio.Length}\"/></item>"));
            var feed = Encoding.UTF8.GetBytes($"<?xml version=\"1.0\"?><rss version=\"2.0\"><channel><title>Test Cast</title><description>A stand-in</description>{items}</channel></rss>");
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    try
                    {
                        var c = await listener.GetContextAsync();
                        var body = c.Request.Url!.AbsolutePath.EndsWith(".xml") ? feed : audio;
                        c.Response.ContentType = c.Request.Url.AbsolutePath.EndsWith(".xml") ? "application/rss+xml" : "audio/wav";
                        c.Response.ContentLength64 = body.Length;
                        await c.Response.OutputStream.WriteAsync(body);
                        c.Response.Close();
                    }
                    catch { }
                }
            });
            return root + "feed.xml";
        });
        Rpc.On("smoke.settings", _ => new { views = S.Views, tabsOff = S.TabsOff, speeds = S.Speeds, stations = Lib.Stations.Count });
        // a large uncompressed clip for measuring how fast frames reach the window (about 6 MB a frame at 1080p)
        Rpc.On("smoke.bigVideo", async p =>
        {
            var file = Path.Combine(p.Str("dir"), $"bench-{p.Int("width")}x{p.Int("height")}.avi");
            await Task.Run(() => SmokeMedia.WriteAviToDisk(file, p.Int("width"), p.Int("height"), p.Int("frames"), p.Int("fps")));
            Program.Frames.ResetStats();
            P.Play(new[] { new PlayItem { Title = "bench", Url = file, IsVideo = true } });
            return file;
        });
        Rpc.On("smoke.state", _ => new
        {
            counts = Counts(),
            position = P.PositionMs,
            playing = P.IsPlaying,
            videoFrames = Program.Engine.Video.Frames,
            framesSent = Program.Frames.Sent,
            sendMs = Program.Frames.Sent > 0 ? Program.Frames.SendMs / Program.Frames.Sent : 0,
            videoWidth = Program.Engine.Video.Width,
            videoHeight = Program.Engine.Video.Height,
            thumbnails = Directory.Exists(AppPaths.ArtDir) ? Directory.GetFiles(AppPaths.ArtDir, "video-*.png") : Array.Empty<string>()
        });
    }
}

public static class SmokeMedia
{
    /// <summary>A flat-colour picture: enough to tell covers and comic pages apart.</summary>
    public static byte[] Picture(int shade) => Png.Flat(64, 96, (byte)(shade / 2), (byte)(255 - shade), (byte)shade);

    public static void Write(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var books = Path.Combine(root, "books");
        Directory.CreateDirectory(books);

        var pages = new[] { Picture(40), Picture(120), Picture(200) };
        using (var zip = ZipFile.Open(Path.Combine(books, "Hero Tales 3.cbz"), ZipArchiveMode.Create))
        {
            void Add(string name, byte[] bytes) { using var s = zip.CreateEntry(name).Open(); s.Write(bytes); }
            Add("page10.png", pages[2]); Add("page1.png", pages[0]); Add("page2.png", pages[1]);
            Add("ComicInfo.xml", Encoding.UTF8.GetBytes("<ComicInfo><Series>Hero Tales</Series><Number>3</Number><Writer>Pat Pencil</Writer></ComicInfo>"));
        }
        using (var zip = ZipFile.Open(Path.Combine(books, "quiet.epub"), ZipArchiveMode.Create))
        {
            void Add(string name, string text) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(text); }
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""");
            Add("OEBPS/content.opf", """<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>A Quiet Book</dc:title><dc:creator>Sam Author</dc:creator><dc:identifier id="id">t1</dc:identifier></metadata><manifest><item id="c1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/><item id="c2" href="text/ch2.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="c1"/><itemref idref="c2"/></spine></package>""");
            var filler = string.Concat(Enumerable.Range(1, 40).Select(i => $"<p>Paragraph {i}. The road went on past the last houses and into the hills.</p>"));
            Add("OEBPS/text/ch1.xhtml", $"<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>One</title></head><body><h1>Opening</h1>{filler}</body></html>");
            Add("OEBPS/text/ch2.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Two</title></head><body><h1>The End</h1><p>And that was all.</p></body></html>");
        }

        var music = Path.Combine(root, "music");
        var albums = new[] { ("Quiet Trio", "First Record", 40, "Jazz"), ("Second Example", "Later Record", 120, "Rock"), ("The Example Band", "First Record", 200, "Pop") };
        foreach (var (artist, album, shade, genre) in albums)
        {
            var dir = Path.Combine(music, artist, album);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "cover.png"), Picture(shade));
            for (var n = 1; n <= 4; n++)
            {
                var f = Path.Combine(dir, $"{n:00} Song {n}.wav");
                WriteWav(f, 4, 220 * n);
                using var tf = TagLib.File.Create(f);
                tf.Tag.Title = $"Song {n}"; tf.Tag.Performers = new[] { artist }; tf.Tag.AlbumArtists = new[] { artist }; tf.Tag.Album = album; tf.Tag.Track = (uint)n; tf.Tag.Genres = new[] { genre };
                tf.Save();
            }
        }

        var pictures = Path.Combine(root, "pictures");
        Directory.CreateDirectory(pictures);
        for (var i = 1; i <= 3; i++) File.WriteAllBytes(Path.Combine(pictures, $"photo {i}.png"), Picture(60 * i));
        File.WriteAllBytes(Path.Combine(pictures, "photo 4.png"), Encoding.ASCII.GetBytes("not really a picture"));

        var videos = Path.Combine(root, "videos");
        Directory.CreateDirectory(videos);
        WriteAvi(Path.Combine(videos, "Sample Clip.avi"), 320, 180, 40);
    }

    /// <summary>Like <see cref="WriteAvi"/> but written frame by frame, for clips too large to hold in memory; a bar moves across so motion is visible.</summary>
    public static void WriteAviToDisk(string path, int width, int height, int frames, int fps)
    {
        var frameSize = width * 3 * height;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        using var w = new BinaryWriter(fs);
        void Four(string s) => w.Write(Encoding.ASCII.GetBytes(s));
        long Begin(string id) { Four(id); w.Write(0); return fs.Position; }
        void End(long start) { var end = fs.Position; fs.Position = start - 4; w.Write((int)(end - start)); fs.Position = end; }
        var riff = Begin("RIFF"); Four("AVI ");
        var hdrl = Begin("LIST"); Four("hdrl");
        var avih = Begin("avih");
        w.Write(1_000_000 / fps); w.Write(frameSize * fps); w.Write(0); w.Write(0x10); w.Write(frames); w.Write(0); w.Write(1); w.Write(frameSize);
        w.Write(width); w.Write(height); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        End(avih);
        var strl = Begin("LIST"); Four("strl");
        var strh = Begin("strh");
        Four("vids"); Four("DIB "); w.Write(0); w.Write((short)0); w.Write((short)0); w.Write(0); w.Write(1); w.Write(fps); w.Write(0); w.Write(frames);
        w.Write(frameSize); w.Write(-1); w.Write(0); w.Write((short)0); w.Write((short)0); w.Write((short)width); w.Write((short)height);
        End(strh);
        var strf = Begin("strf");
        w.Write(40); w.Write(width); w.Write(height); w.Write((short)1); w.Write((short)24); w.Write(0); w.Write(frameSize); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        End(strf); End(strl); End(hdrl);
        var movi = Begin("LIST"); var moviData = fs.Position; Four("movi");
        var offsets = new List<int>();
        var back = new byte[frameSize];
        for (var i = 0; i < frameSize; i += 3) { back[i] = 70; back[i + 1] = (byte)(60 + (i / 3 / width) * 120 / height); back[i + 2] = (byte)(40 + (i / 3 % width) * 180 / width); }
        var frame = new byte[frameSize];
        for (var f = 0; f < frames; f++)
        {
            Buffer.BlockCopy(back, 0, frame, 0, frameSize);
            var x0 = (int)((long)f * (width - 80) / Math.Max(1, frames - 1));
            for (var y = 0; y < height; y++) Array.Fill(frame, (byte)255, (y * width + x0) * 3, 80 * 3);
            offsets.Add((int)(fs.Position - moviData));
            var chunk = Begin("00db"); w.Write(frame); End(chunk);
        }
        End(movi);
        var idx = Begin("idx1");
        foreach (var o in offsets) { Four("00db"); w.Write(0x10); w.Write(o); w.Write(frameSize); }
        End(idx);
        End(riff);
    }

    /// <summary>A quiet tone (8-bit mono), so the visualizer has something to show while the speakers stay near silent.</summary>
    public static void WriteWav(string path, int seconds, int hz)
    {
        const int rate = 8000;
        var n = rate * seconds;
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write("RIFF"u8); bw.Write(36 + n); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1); bw.Write(rate); bw.Write(rate); bw.Write((short)1); bw.Write((short)8);
        bw.Write("data"u8); bw.Write(n);
        var data = new byte[n];
        for (var i = 0; i < n; i++) data[i] = (byte)(128 + 40 * Math.Sin(2 * Math.PI * hz * i / rate));
        bw.Write(data);
    }

    /// <summary>
    /// An uncompressed AVI (24-bit frames, 10 per second) whose colour changes over time, so a thumbnail can be told
    /// from a blank frame. Small enough to write in a test, and every VLC can play it.
    /// </summary>
    public static void WriteAvi(string path, int width, int height, int frames)
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
}
