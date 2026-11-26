using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using Halcyon.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Halcyon.Services;

/// <summary>
/// The auto equalizer's AI part: Google's YAMNet sound-recognition model (Apache 2.0, ONNX) listens to a few stretches of
/// a song and recognises its style (electronic, hip hop, rock, pop, acoustic, jazz, classical) and whether it is
/// mostly singing or speech, so each song is evened out towards a target that suits it rather than one general target.
/// It runs through ONNX Runtime on the graphics: DirectML on Windows (any AMD, Intel or NVIDIA graphics), Core ML on
/// Macs, NNAPI on Android, and on the processor otherwise. The model (about 15 MB) is downloaded the first time it is
/// turned on.
/// </summary>
public static class AutoEqAi
{
    const string Repo = "https://huggingface.co/audiomagic/yamnet-onnx/resolve/main/";
    public const int Megabytes = 15;

    public static string ModelsDir => Environment.GetEnvironmentVariable("PHOENIX_MODELS") is { Length: > 0 } d ? d : Path.Combine(AppPaths.DataDir, "models");
    static string ModelFile => Path.Combine(ModelsDir, "yamnet.onnx");
    static string ClassFile => Path.Combine(ModelsDir, "yamnet_class_map.csv");
    public static bool Have => File.Exists(ModelFile) && File.Exists(ClassFile);

    /// <summary>The styles it tells apart, with how each shifts the target (dB per band, 31 Hz to 16 kHz).</summary>
    public record Style(string Id, string Name, string[] Classes, double[] Shift, bool Genre = true);
    public static readonly Style[] Styles =
    {
        new("electronic", "electronic", new[] { "Electronic music", "House music", "Techno", "Dubstep", "Drum and bass", "Electronica", "Electronic dance music", "Trance music", "Disco" },
            new double[] { 3, 3, 2, 0, 0, 0, 0, 0, 0.5, 0.5 }),
        new("hiphop", "hip hop", new[] { "Hip hop music", "Rapping", "Reggae", "Funk", "Grime music" },
            new double[] { 3, 3, 1.5, 0, -0.5, 0, 0.5, 0, 0, 0 }),
        new("rock", "rock", new[] { "Rock music", "Heavy metal", "Punk rock", "Grunge", "Progressive rock", "Rock and roll", "Psychedelic rock" },
            new double[] { 1, 1, 0, 0, -0.5, 0, 0.5, 1, 0.5, 0 }),
        new("pop", "pop", new[] { "Pop music" },
            new double[] { 1, 1, 0, 0, 0, 0, 0.5, 0.5, 0.5, 0 }),
        new("acoustic", "acoustic", new[] { "Folk music", "Country", "Acoustic guitar", "Bluegrass", "Middle Eastern music", "Music of Latin America" },
            new double[] { -1, 0, 0, 0, 0, 0, 0.5, 0.5, 0.5, 0 }),
        new("jazz", "jazz and soul", new[] { "Jazz", "Blues", "Soul music", "Rhythm and blues", "Swing music" },
            new double[] { 0, 0.5, 0.5, 0.5, 0, 0, 0, -0.5, -0.5, 0 }),
        new("classical", "classical", new[] { "Classical music", "Opera", "Orchestra", "Choir", "Ambient music", "New-age music" },
            new double[] { -2, -1, 0, 0, 0, 0, 0, -0.5, -1, -1 }),
        new("vocal", "vocal", new[] { "Singing", "Vocal music", "A capella", "Male singing", "Female singing" },
            new double[] { 0, 0, 0, -0.5, 0, 0.5, 1, 1, 0, 0 }, false),
        new("speech", "speech", new[] { "Speech", "Narration, monologue", "Conversation" },
            new double[] { -4, -3, -1, 0, 0, 1, 1.5, 1, 0, -2 }, false),
    };

    // ---------- downloading ----------

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static async Task DownloadAsync(Action<double> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(ModelsDir);
        foreach (var (name, part) in new[] { ("yamnet_class_map.csv", 0.02), ("yamnet.onnx", 0.98) })
        {
            var target = Path.Combine(ModelsDir, name);
            var temp = target + ".part";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, Repo + name);
                req.Headers.UserAgent.ParseAdd("Phoenix/1.0");
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? Megabytes * 1_048_576L;
                await using (var src = await resp.Content.ReadAsStreamAsync(cancel))
                await using (var dst = File.Create(temp))
                {
                    var buf = new byte[1 << 16];
                    long done = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, cancel)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), cancel);
                        done += n;
                        progress((name.EndsWith(".onnx") ? 0.02 : 0) + part * Math.Min(1, (double)done / total));
                    }
                }
                if (name.EndsWith(".onnx") && new FileInfo(temp).Length < 1_000_000) throw new InvalidDataException("The download isn't the model.");
                File.Move(temp, target, true);
            }
            finally { try { File.Delete(temp); } catch { } }
        }
        Unload();
    }

    public static void Delete()
    {
        Unload();
        try { File.Delete(ModelFile); File.Delete(ClassFile); } catch { }
    }

    // ---------- running it ----------

    static InferenceSession? _session;
    static Dictionary<int, int>? _classToStyle;   // YAMNet class index → index into Styles
    static readonly object Gate = new();

    /// <summary>What the model last ran on ("the graphics card (DirectML)", "the processor"…), for settings.</summary>
    public static string RanOn { get; private set; } = "";

    public static void Unload()
    {
        lock (Gate) { _session?.Dispose(); _session = null; }
    }

    static InferenceSession Session()
    {
        if (_session != null) return _session;
        var options = new SessionOptions();
        RanOn = "the processor";
        try
        {
            if (OperatingSystem.IsWindows()) { options.AppendExecutionProvider_DML(0); RanOn = "the graphics card (DirectML)"; }
            else if (OperatingSystem.IsMacOS()) { options.AppendExecutionProvider_CoreML(); RanOn = "the graphics and neural engine (Core ML)"; }
            else if (OperatingSystem.IsAndroid()) { options.AppendExecutionProvider_Nnapi(); RanOn = "the phone's AI accelerator (NNAPI)"; }
        }
        catch { options = new SessionOptions(); RanOn = "the processor"; }
        try { _session = new InferenceSession(ModelFile, options); }
        catch
        {
            // a graphics driver that refuses the model: the processor does it
            _session = new InferenceSession(ModelFile);
            RanOn = "the processor";
        }
        // which of the 521 sound classes belong to which style
        _classToStyle = new();
        var lines = File.ReadAllLines(ClassFile).Skip(1);
        foreach (var line in lines)
        {
            var cols = SplitCsv(line);
            if (cols.Count < 3 || !int.TryParse(cols[0], out var index)) continue;
            for (var s = 0; s < Styles.Length; s++)
                if (Styles[s].Classes.Contains(cols[2], StringComparer.OrdinalIgnoreCase)) { _classToStyle[index] = s; break; }
        }
        return _session;
    }

    static List<string> SplitCsv(string line)
    {
        var cols = new List<string>();
        var cur = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { cols.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        cols.Add(cur.ToString());
        return cols;
    }

    /// <summary>
    /// How strongly a song is each style (0..1): YAMNet listens to three 10-second stretches (a quarter, half and three
    /// quarters of the way in). Takes mono samples at any rate. Null when the model isn't here or can't be run.
    /// </summary>
    public static Dictionary<string, double>? Recognise(float[] samples, int rate)
    {
        if (!Have || samples.Length < rate) return null;
        lock (Gate)
        {
            try
            {
                var session = Session();
                var map = _classToStyle!;
                var mono16 = Resample(samples, rate, 16000);
                var styleScore = new double[Styles.Length];
                var frames = 0;
                const int stretch = 16000 * 10;
                foreach (var at in new[] { 0.25, 0.5, 0.75 })
                {
                    var start = Math.Clamp((int)(mono16.Length * at) - stretch / 2, 0, Math.Max(0, mono16.Length - stretch));
                    var len = Math.Min(stretch, mono16.Length - start);
                    if (len < 16000) continue;
                    var input = new DenseTensor<float>(mono16.AsMemory(start, len), new[] { len });
                    using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), input) });
                    var scores = results.First().AsTensor<float>();
                    int rows = scores.Dimensions[0], classes = scores.Dimensions[1];
                    for (var r = 0; r < rows; r++)
                    {
                        // each style counts its strongest class in this frame
                        var best = new double[Styles.Length];
                        foreach (var (cls, s) in map)
                            if (cls < classes) best[s] = Math.Max(best[s], scores[r, cls]);
                        for (var s = 0; s < Styles.Length; s++) styleScore[s] += best[s];
                        frames++;
                    }
                }
                if (frames == 0) return null;
                var result = new Dictionary<string, double>();
                for (var s = 0; s < Styles.Length; s++)
                {
                    var v = styleScore[s] / frames;
                    if (v >= 0.03) result[Styles[s].Id] = Math.Round(v, 2);
                }
                return result;
            }
            catch { return null; }
        }
    }

    static float[] Resample(float[] input, int from, int to)
    {
        if (from == to) return input;
        var n = (int)((long)input.Length * to / from);
        var output = new float[n];
        var step = (double)from / to;
        for (var i = 0; i < n; i++)
        {
            var pos = i * step;
            var j = (int)pos;
            var f = (float)(pos - j);
            output[i] = j + 1 < input.Length ? input[j] * (1 - f) + input[j + 1] * f : input[Math.Min(j, input.Length - 1)];
        }
        return output;
    }

    // ---------- from the styles to the target ----------

    /// <summary>
    /// How the target shifts for a song's styles: the genres share one shift in proportion to how strongly each was
    /// heard, and singing or speech add their own on top.
    /// </summary>
    public static double[] TargetShift(IReadOnlyDictionary<string, double> styles)
    {
        var shift = new double[AutoEq.Bands.Length];
        var genres = Styles.Where(s => s.Genre && styles.GetValueOrDefault(s.Id) >= 0.05).ToList();
        var total = genres.Sum(s => styles[s.Id]);
        foreach (var g in genres)
            for (var b = 0; b < shift.Length; b++) shift[b] += g.Shift[b] * styles[g.Id] / total;
        foreach (var extra in Styles.Where(s => !s.Genre && styles.GetValueOrDefault(s.Id) >= 0.05))
        {
            var weight = Math.Min(1, styles[extra.Id] * 2);
            for (var b = 0; b < shift.Length; b++) shift[b] += extra.Shift[b] * weight;
        }
        return shift;
    }

    /// <summary>The styles in words, strongest first ("rock, pop, vocal").</summary>
    public static string Describe(IReadOnlyDictionary<string, double>? styles) =>
        styles == null || styles.Count == 0 ? "" : string.Join(", ", styles.Where(kv => kv.Value >= 0.05).OrderByDescending(kv => kv.Value)
            .Select(kv => Styles.FirstOrDefault(s => s.Id == kv.Key)?.Name ?? kv.Key));

    public static string Format(IReadOnlyDictionary<string, double> styles) =>
        string.Join(",", styles.Select(kv => $"{kv.Key}:{kv.Value.ToString("0.00", CultureInfo.InvariantCulture)}"));

    public static Dictionary<string, double>? Parse(string text)
    {
        var d = new Dictionary<string, double>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            if (kv.Length == 2 && double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) d[kv[0]] = v;
        }
        return d;
    }
}
