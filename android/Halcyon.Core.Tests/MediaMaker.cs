using System.IO.Compression;
using System.Text;

/// <summary>
/// Writes a small synthetic media set for device testing (`dotnet run -- makemedia &lt;folder&gt;`):
/// tagged WAV songs with embedded covers under Music\, PNG pictures under Pictures\, and an audiobook, an EPUB and a PDF under Books\.
/// </summary>
static class MediaMaker
{
    public static void Make(string root)
    {
        var albums = new[]
        {
            (artist: "The Sine Waves", album: "Pure Tones", year: 2021, genre: "Electronic", color: (224, 68, 124), titles: new[] { "Four Forty", "Octave Jump", "Slow Sweep" }),
            (artist: "The Sine Waves", album: "Second Harmonic", year: 2023, genre: "Electronic", color: (60, 120, 220), titles: new[] { "Fifths", "Arpeggio" }),
            (artist: "Square Dance", album: "Edges", year: 2019, genre: "Chiptune", color: (240, 150, 40), titles: new[] { "Pulse", "Staircase", "Gate" }),
        };
        var seed = 0;
        foreach (var a in albums)
        {
            var dir = Path.Combine(root, "Music", a.artist, a.album);
            Directory.CreateDirectory(dir);
            var cover = Png(300, 300, a.color, seed);
            for (var i = 0; i < a.titles.Length; i++)
            {
                var file = Path.Combine(dir, $"{i + 1:00} {a.titles[i]}.wav");
                WriteWav(file, 75 + 20 * i, seed++, a.genre == "Chiptune");
                using var f = TagLib.File.Create(file);
                f.Tag.Title = a.titles[i];
                f.Tag.Performers = new[] { a.artist };
                f.Tag.AlbumArtists = new[] { a.artist };
                f.Tag.Album = a.album;
                f.Tag.Year = (uint)a.year;
                f.Tag.Track = (uint)(i + 1);
                f.Tag.Genres = new[] { a.genre };
                f.Tag.Pictures = new TagLib.IPicture[] { new TagLib.Picture(new TagLib.ByteVector(cover)) { MimeType = "image/png" } };
                f.Save();
            }
        }
        var pics = Path.Combine(root, "Pictures", "Test Shots");
        Directory.CreateDirectory(pics);
        var colors = new[] { (200, 60, 60), (60, 180, 90), (70, 90, 210), (220, 190, 50), (150, 70, 190) };
        for (var i = 0; i < colors.Length; i++) File.WriteAllBytes(Path.Combine(pics, $"shot {i + 1}.png"), Png(640, 480, colors[i], i * 7));
        MakeBooks(Path.Combine(root, "Books"), 70);
        Console.WriteLine("media written to " + root);
    }

    /// <summary>A three-file audiobook (written out of order), an EPUB with three chapters and a cover, and a three-page PDF.</summary>
    public static void MakeBooks(string dir, int partSeconds)
    {
        var audio = Path.Combine(dir, "Jane Writer", "The Long Road");
        Directory.CreateDirectory(audio);
        foreach (var n in new[] { 10, 1, 2 })
        {
            var f = Path.Combine(audio, $"Chapter {n}.wav");
            WriteWav(f, partSeconds, n, false);
            using var tf = TagLib.File.Create(f);
            tf.Tag.Title = $"Part {n}"; tf.Tag.Album = "The Long Road"; tf.Tag.AlbumArtists = new[] { "Jane Writer" };
            tf.Save();
        }
        MakeEpub(Path.Combine(dir, "quiet.epub"));
        MakePdf(Path.Combine(dir, "Manual.pdf"), 3);
        MakeComics(Path.Combine(dir, "Hero Tales 3.cbz"), Path.Combine(dir, "Space Gang.cbr"));
    }

    /// <summary>
    /// Writes a .cbz (pages named so that plain sorting would put page10 before page2, plus ComicInfo.xml) and a .cbr
    /// (a stored RAR 4 archive); returns each one's page pictures in reading order.
    /// </summary>
    public static (List<byte[]> zip, List<byte[]> rar) MakeComics(string cbz, string cbr)
    {
        var zipPages = new List<byte[]> { Png(400, 600, (217, 74, 74), 1), Png(400, 600, (74, 127, 217), 2), Png(400, 600, (74, 217, 138), 3) };
        if (File.Exists(cbz)) File.Delete(cbz);
        using (var zip = ZipFile.Open(cbz, ZipArchiveMode.Create))
        {
            void Add(string name, byte[] bytes) { using var s = zip.CreateEntry(name).Open(); s.Write(bytes); }
            Add("page10.png", zipPages[2]);
            Add("page1.png", zipPages[0]);
            Add("page2.png", zipPages[1]);
            Add("ComicInfo.xml", Encoding.UTF8.GetBytes("<ComicInfo><Series>Hero Tales</Series><Number>3</Number><Writer>Pat Pencil</Writer></ComicInfo>"));
        }
        var rarPages = new List<byte[]> { Png(400, 600, (224, 160, 48), 4), Png(400, 600, (154, 74, 217), 5), Png(400, 600, (48, 176, 192), 6) };
        WriteRar4(cbr, new[] { ("p3.png", rarPages[2]), ("p1.png", rarPages[0]), ("p2.png", rarPages[1]) });
        return (zipPages, rarPages);
    }

    /// <summary>A RAR 4 archive with stored (uncompressed) files: enough to prove .cbr reading without a RAR writer.</summary>
    static void WriteRar4(string path, (string name, byte[] data)[] files)
    {
        using var o = new BinaryWriter(File.Create(path));
        o.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 });
        byte[] Block(Action<BinaryWriter> body)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            body(w);
            w.Flush();
            var raw = ms.ToArray();
            return BitConverter.GetBytes((ushort)(Crc(raw) & 0xFFFF)).Concat(raw).ToArray();
        }
        o.Write(Block(w => { w.Write((byte)0x73); w.Write((ushort)0); w.Write((ushort)13); w.Write((ushort)0); w.Write(0u); }));
        foreach (var (name, data) in files)
        {
            var nameBytes = Encoding.ASCII.GetBytes(name);
            o.Write(Block(w =>
            {
                w.Write((byte)0x74); w.Write((ushort)0x8000); w.Write((ushort)(32 + nameBytes.Length));
                w.Write((uint)data.Length); w.Write((uint)data.Length);
                w.Write((byte)2); w.Write(Crc(data)); w.Write(0x5A000000u); w.Write((byte)20); w.Write((byte)0x30);
                w.Write((ushort)nameBytes.Length); w.Write(0x20u); w.Write(nameBytes);
            }));
            o.Write(data);
        }
        o.Write(new byte[] { 0xC4, 0x3D, 0x7B, 0x00, 0x40, 0x07, 0x00 });
    }

    static void MakeEpub(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        var filler = string.Concat(Enumerable.Range(1, 60).Select(i => $"<p>Paragraph {i}. The road went on past the last houses and into the hills, and nobody who walked it that year could say afterwards exactly where the town had ended and the country begun.</p>"));
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string text) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(text); }
        Add("mimetype", "application/epub+zip");
        Add("META-INF/container.xml", """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""");
        Add("OEBPS/content.opf", """
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>A Quiet Book</dc:title><dc:creator>Sam Author</dc:creator><dc:identifier id="id">test-1</dc:identifier><dc:language>en</dc:language>
                <meta name="cover" content="cover-img"/>
              </metadata>
              <manifest>
                <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
                <item id="cover-img" href="images/front.png" media-type="image/png"/>
                <item id="c1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/>
                <item id="c2" href="text/ch2.xhtml" media-type="application/xhtml+xml"/>
                <item id="c3" href="text/ch3.xhtml" media-type="application/xhtml+xml"/>
              </manifest>
              <spine toc="ncx"><itemref idref="c1"/><itemref idref="c2"/><itemref idref="c3"/></spine>
            </package>
            """.Trim());
        Add("OEBPS/toc.ncx", """
            <?xml version="1.0" encoding="UTF-8"?>
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><navMap>
              <navPoint id="n1" playOrder="1"><navLabel><text>Opening</text></navLabel><content src="text/ch1.xhtml"/></navPoint>
              <navPoint id="n2" playOrder="2"><navLabel><text>The Middle</text></navLabel><content src="text/ch2.xhtml#start"/></navPoint>
              <navPoint id="n3" playOrder="3"><navLabel><text>The End</text></navLabel><content src="text/ch3.xhtml"/></navPoint>
            </navMap></ncx>
            """.Trim());
        string Chapter(string title, string body) => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>{title}</title></head><body><h1 id=\"start\">{title}</h1>{body}</body></html>";
        Add("OEBPS/text/ch1.xhtml", Chapter("Opening", "<p>It began quietly.</p><p><img src=\"../images/front.png\" alt=\"cover\"/></p>"));
        Add("OEBPS/text/ch2.xhtml", Chapter("The Middle", filler));
        Add("OEBPS/text/ch3.xhtml", Chapter("The End", "<p>And that was all.</p>"));
        using (var s = zip.CreateEntry("OEBPS/images/front.png").Open()) s.Write(Png(300, 450, (61, 126, 219), 3));
    }

    /// <summary>A small but well-formed PDF (with a cross-reference table) that says "Page N" on each page.</summary>
    static void MakePdf(string path, int pages)
    {
        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{4 + i * 2} 0 R"))}] /Count {pages} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        for (var i = 0; i < pages; i++)
        {
            var text = $"BT /F1 36 Tf 60 700 Td (Manual - Page {i + 1}) Tj ET";
            objs.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + i * 2} 0 R >>");
            objs.Add($"<< /Length {text.Length} >>\nstream\n{text}\nendstream");
        }
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objs.Count; i++) { offsets.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:0000000000} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
    }

    /// <summary>A looping melody: a new note every 400 ms, sine or square, with a short fade on each note.</summary>
    static void WriteWav(string path, int seconds, int seed, bool square)
    {
        const int rate = 22050;
        var n = rate * seconds;
        var scale = new[] { 220.0, 261.6, 293.7, 329.6, 392.0, 440.0, 523.3, 587.3, 659.3, 784.0 };
        var rnd = new Random(seed);
        var melody = Enumerable.Range(0, 16).Select(_ => scale[rnd.Next(scale.Length)]).ToArray();
        using var w = new BinaryWriter(File.Create(path));
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
        var noteLen = rate * 2 / 5;
        double phase = 0, bass = 0;
        for (var i = 0; i < n; i++)
        {
            var note = i / noteLen;
            var f = melody[note % melody.Length];
            phase += 2 * Math.PI * f / rate;
            bass += 2 * Math.PI * (f / 4) / rate;
            var env = Math.Min(1, Math.Min(i % noteLen, noteLen - i % noteLen) / (rate * 0.02));
            var s = square ? Math.Sign(Math.Sin(phase)) * 0.35 : Math.Sin(phase) * 0.55;
            s += Math.Sin(bass) * 0.25;
            w.Write((short)(s * env * 20000));
        }
    }

    /// <summary>A PNG with a diagonal gradient and a few lighter stripes, so thumbnails are tellable apart.</summary>
    public static byte[] Png(int w, int h, (int r, int g, int b) c, int seed)
    {
        var raw = new byte[(w * 3 + 1) * h];
        var stripe = 20 + seed % 5 * 9;
        for (var y = 0; y < h; y++)
        {
            var row = y * (w * 3 + 1);
            for (var x = 0; x < w; x++)
            {
                var t = 0.45 + 0.55 * (x + y) / (w + h);
                var light = (x + 2 * y) / stripe % 4 == 0 ? 50 : 0;
                raw[row + 1 + x * 3] = (byte)Math.Min(255, c.r * t + light);
                raw[row + 2 + x * 3] = (byte)Math.Min(255, c.g * t + light);
                raw[row + 3 + x * 3] = (byte)Math.Min(255, c.b * t + light);
            }
        }
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BitConverter.GetBytes(w).Reverse().ToArray().CopyTo(ihdr, 0);
        BitConverter.GetBytes(h).Reverse().ToArray().CopyTo(ihdr, 4);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var d = new ZLibStream(z, CompressionLevel.Optimal, true)) d.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        s.Write(BitConverter.GetBytes(data.Length).Reverse().ToArray());
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(body);
        s.Write(BitConverter.GetBytes(Crc(body)).Reverse().ToArray());
    }

    static uint Crc(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
