using System.IO;
using System.Net.Http;
using Halcyon.Core;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Halcyon.Services;

/// <summary>
/// Lyrics written down by a speech model on this computer (whisper.cpp): it listens to a song and writes what is sung,
/// with timing, so the lyrics follow the song. Nothing is sent anywhere. It runs on the graphics card when it can, through
/// Vulkan on Windows and Linux (any AMD, Intel or NVIDIA graphics, built in or separate) and Metal on Macs with Apple
/// chips, and on the processor otherwise. Shared by the Windows app and the Mac/Linux helper; each supplies
/// <see cref="Decode"/>, which turns a song into 16 kHz mono samples.
/// </summary>
public static class SpeechLyrics
{
    public record Model(string Id, string Name, string File, int Megabytes, string Note);

    /// <summary>The models to choose from; bigger ones hear sung words better and take longer.</summary>
    public static readonly Model[] Models =
    {
        new("tiny", "tiny", "ggml-tiny.bin", 75, "quick, but often mishears singing"),
        new("base", "base", "ggml-base.bin", 142, "quick; fine for clear vocals"),
        new("small", "small", "ggml-small.bin", 466, "good; a few seconds a song on a graphics card"),
        new("turbo", "large v3 turbo, compact", "ggml-large-v3-turbo-q5_0.bin", 547, "good at sung words; fine on built-in graphics"),
        new("turbo-full", "large v3 turbo", "ggml-large-v3-turbo.bin", 1549, "better; for a graphics card with 3 GB or more"),
        new("large", "large v3", "ggml-large-v3.bin", 2952, "the most accurate; for a graphics card with 6 GB or more"),
    };

    public const string Mark = Lyrics.GeneratedMark;

    /// <summary>
    /// The model that suits this computer: the most accurate one the graphics can hold comfortably (a separate card with
    /// 6 GB or more takes large v3), down to "small" for the processor.
    /// </summary>
    public static Model Recommended()
    {
        if (GpuChoice == -1) return Find("small")!;
        if (OperatingSystem.IsMacOS()) return Find(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "turbo-full" : "small")!;
        var gpus = _gpusSeen ??= Gpus();
        var g = GpuChoice >= 0 ? gpus.FirstOrDefault(x => x.Index == GpuChoice) : gpus.FirstOrDefault(x => x.Kind == "separate") ?? gpus.FirstOrDefault();
        if (g == null) return Find("small")!;
        if (g.Kind != "separate") return Find("turbo")!;
        return Find(g.MemoryMb >= 5800 ? "large" : g.MemoryMb >= 3000 ? "turbo-full" : "turbo")!;
    }
    static List<Gpu>? _gpusSeen;

    public static Model? Find(string id) => Models.FirstOrDefault(m => m.Id == id);

    /// <summary>Where downloaded models are kept (PHOENIX_MODELS overrides it, for the self-test).</summary>
    public static string ModelsDir => Environment.GetEnvironmentVariable("PHOENIX_MODELS") is { Length: > 0 } d ? d : Path.Combine(AppPaths.DataDir, "models");
    public static string PathOf(Model m) => Path.Combine(ModelsDir, m.File);
    public static bool Have(Model m) => File.Exists(PathOf(m));

    /// <summary>Turns a file or stream address into 16 kHz mono samples (-1..1). Set by the app.</summary>
    public static Func<string, CancellationToken, float[]>? Decode;

    // ---------- downloading a model ----------

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>Downloads a model from whisper.cpp's own page on Hugging Face. Progress is 0..1.</summary>
    public static async Task DownloadAsync(Model m, Action<double> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(ModelsDir);
        var part = PathOf(m) + ".part";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{m.File}");
            req.Headers.UserAgent.ParseAdd("Phoenix/1.0");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? m.Megabytes * 1_048_576L;
            await using (var src = await resp.Content.ReadAsStreamAsync(cancel))
            await using (var dst = File.Create(part))
            {
                var buf = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, cancel)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), cancel);
                    done += n;
                    progress(Math.Min(1, (double)done / total));
                }
            }
            // a whisper.cpp model starts with "lmgg"
            var head = new byte[4];
            await using (var f = File.OpenRead(part)) _ = await f.ReadAsync(head, cancel);
            if (Encoding.ASCII.GetString(head) != "lmgg") throw new InvalidDataException("The download isn't a speech model.");
            File.Move(part, PathOf(m), true);
        }
        finally { try { File.Delete(part); } catch { } }
    }

    public static void Delete(Model m)
    {
        Unload();
        try { File.Delete(PathOf(m)); } catch { }
    }

    // ---------- the graphics card ----------

    public record Gpu(int Index, string Name, string Kind, long MemoryMb);

    /// <summary>Graphics cards Vulkan can use, in Vulkan's order (empty where there is no Vulkan, as on Macs).</summary>
    public static List<Gpu> Gpus()
    {
        var list = new List<Gpu>();
        try
        {
            if (OperatingSystem.IsMacOS()) return list;
            IntPtr lib = IntPtr.Zero;
            foreach (var name in OperatingSystem.IsWindows() ? new[] { "vulkan-1.dll" } : new[] { "libvulkan.so.1", "libvulkan.so" })
                if (NativeLibrary.TryLoad(name, out lib)) break;
            if (lib == IntPtr.Zero) return list;
            var create = Marshal.GetDelegateForFunctionPointer<VkCreateInstance>(NativeLibrary.GetExport(lib, "vkCreateInstance"));
            var destroy = Marshal.GetDelegateForFunctionPointer<VkDestroyInstance>(NativeLibrary.GetExport(lib, "vkDestroyInstance"));
            var enumerate = Marshal.GetDelegateForFunctionPointer<VkEnumeratePhysicalDevices>(NativeLibrary.GetExport(lib, "vkEnumeratePhysicalDevices"));
            var props = Marshal.GetDelegateForFunctionPointer<VkGetPhysicalDeviceProperties>(NativeLibrary.GetExport(lib, "vkGetPhysicalDeviceProperties"));
            var memory = Marshal.GetDelegateForFunctionPointer<VkGetPhysicalDeviceProperties>(NativeLibrary.GetExport(lib, "vkGetPhysicalDeviceMemoryProperties"));
            var info = new VkInstanceCreateInfo { sType = 1 };
            if (create(ref info, IntPtr.Zero, out var instance) != 0) return list;
            try
            {
                uint count = 0;
                enumerate(instance, ref count, null);
                var devices = new IntPtr[count];
                enumerate(instance, ref count, devices);
                for (var i = 0; i < count; i++)
                {
                    var p = new byte[2048];   // VkPhysicalDeviceProperties is 824 bytes
                    props(devices[i], p);
                    var type = BitConverter.ToUInt32(p, 16);
                    var name = Encoding.UTF8.GetString(p, 20, 256).Split('\0')[0];
                    if (type == 4) continue;   // a software renderer (llvmpipe and the like): whisper.cpp leaves those out
                    var kind = type switch { 1 => "built in", 2 => "separate", 3 => "virtual", _ => "other" };
                    // VkPhysicalDeviceMemoryProperties: the largest heap on the card itself
                    var mem = new byte[1024];
                    memory(devices[i], mem);
                    long biggest = 0;
                    var heaps = BitConverter.ToUInt32(mem, 260);
                    for (var hp = 0; hp < Math.Min(16, heaps); hp++)
                        if ((BitConverter.ToUInt32(mem, 264 + hp * 16 + 8) & 1) != 0) biggest = Math.Max(biggest, (long)BitConverter.ToUInt64(mem, 264 + hp * 16));
                    list.Add(new Gpu(list.Count, name, kind, biggest / 1_048_576));
                }
            }
            finally { destroy(instance, IntPtr.Zero); }
        }
        catch { }
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct VkInstanceCreateInfo
    {
        public int sType; public IntPtr pNext; public uint flags; public IntPtr pApplicationInfo;
        public uint enabledLayerCount; public IntPtr ppEnabledLayerNames; public uint enabledExtensionCount; public IntPtr ppEnabledExtensionNames;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int VkCreateInstance(ref VkInstanceCreateInfo info, IntPtr allocator, out IntPtr instance);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void VkDestroyInstance(IntPtr instance, IntPtr allocator);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int VkEnumeratePhysicalDevices(IntPtr instance, ref uint count, [Out] IntPtr[]? devices);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void VkGetPhysicalDeviceProperties(IntPtr device, [Out] byte[] properties);

    // ---------- listening ----------

    static WhisperFactory? _factory;
    static string _factoryKey = "";
    static readonly SemaphoreSlim One = new(1, 1);
    static Timer? _idle;
    static bool _libraryChosen;

    /// <summary>What the model ran on last time ("graphics card (Vulkan)", "processor"…), for settings.</summary>
    public static string RanOn { get; private set; } = "";

    /// <summary>
    /// The graphics card to use: -2 lets whisper.cpp choose (the separate card if there is one, otherwise the built-in
    /// one), -1 the processor only, or a Vulkan device from <see cref="Gpus"/> (whisper.cpp numbers them the same way).
    /// </summary>
    public static int GpuChoice { get; set; } = -2;

    static WhisperFactory Factory(Model m)
    {
        var key = m.File + "|" + GpuChoice;
        if (_factory != null && _factoryKey == key) return _factory;
        Unload();
        if (!_libraryChosen)
        {
            _libraryChosen = true;
            RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary> { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu };   // on Macs the processor build has Metal in it
        }
        _factory = WhisperFactory.FromPath(PathOf(m), new WhisperFactoryOptions { UseGpu = GpuChoice != -1, GpuDevice = Math.Max(0, GpuChoice) });
        _factoryKey = key;
        var lib = RuntimeOptions.LoadedLibrary;
        var metal = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        RanOn = GpuChoice == -1 ? "the processor"
            : lib == RuntimeLibrary.Vulkan ? "the graphics card (Vulkan)" : metal ? "the graphics chip (Metal)" : "the processor";
        return _factory;
    }

    /// <summary>Lets go of the model (and the graphics memory it holds).</summary>
    public static void Unload()
    {
        _factory?.Dispose();
        _factory = null;
        _factoryKey = "";
    }

    /// <summary>
    /// Listens to a song and writes its lyrics as timed .lrc text. <paramref name="line"/> hears each line as it is
    /// written (from another thread), so they can be shown while the song plays. One song at a time.
    /// </summary>
    public static async Task<string?> TranscribeAsync(string source, Model m, Action<long, string>? line, CancellationToken cancel, string title = "", string artist = "")
    {
        if (!Have(m)) throw new InvalidOperationException("Download a speech model in settings → lyrics first.");
        var decode = Decode ?? throw new NotSupportedException("Not available here.");
        await One.WaitAsync(cancel);
        try
        {
            _idle?.Dispose();
            var samples = await Task.Run(() => decode(source, cancel), cancel);
            if (samples.Length < 16000) return null;
            var factory = Factory(m);
            // Songs trip whisper up in ways speech doesn't: it can get stuck repeating a line, so each 30-second window
            // starts afresh (with the song's name as a hint), long sentences are split into lyric-sized lines, and on a
            // graphics card it weighs several readings of each passage (beam search), which hears sung words better.
            var hint = title != "" ? $"Lyrics of “{title}”{(artist != "" ? $" by {artist}" : "")}." : "Song lyrics.";
            var builder = factory.CreateBuilder()
                .WithLanguage("auto")
                .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 1, 8))
                .WithPrompt(hint)
                .WithCarryInitialPrompt(true)
                .WithNoContext()
                .WithTokenTimestamps()
                .SplitOnWord()
                .WithMaxSegmentLength(64);
            if (RanOn != "the processor") builder.WithBeamSearchSamplingStrategy(b => b.WithBeamSize(5));
            using var processor = builder.Build();
            var lines = new List<(long ms, string text)>();
            await foreach (var seg in processor.ProcessAsync(samples, cancel))
            {
                var text = Clean(seg.Text);
                if (text == "" || IsNoise(text)) continue;
                // a model stuck on one line repeats it: keep it at most twice in a row
                if (lines.Count >= 2 && lines[^1].text == text && lines[^2].text == text) continue;
                var ms = (long)seg.Start.TotalMilliseconds;
                lines.Add((ms, text));
                line?.Invoke(ms, text);
            }
            if (lines.Count == 0) return null;
            var sb = new StringBuilder();
            sb.Append(Mark).Append(' ').Append(m.Name).AppendLine("]");
            foreach (var (ms, text) in lines) sb.Append($"[{ms / 60000:00}:{ms / 1000 % 60:00}.{ms % 1000 / 10:00}]").AppendLine(text);
            return sb.ToString();
        }
        finally
        {
            // let go of the graphics memory when nothing has been asked for a while
            _idle = new Timer(_ => { if (One.Wait(0)) try { Unload(); } finally { One.Release(); } }, null, TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
            One.Release();
        }
    }

    static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    static readonly Regex Bracketed = new(@"^[\[\(\*].*[\]\)\*]$", RegexOptions.Compiled);

    static string Clean(string text) => Spaces.Replace(text.Replace("♪", " ").Replace("♫", " "), " ").Trim().Trim('-', ' ');

    /// <summary>What whisper writes for music with no words ("[Music]", "(upbeat music)"), and the lines it is known to make up.</summary>
    static bool IsNoise(string text)
    {
        if (Bracketed.IsMatch(text)) return true;
        var t = text.ToLowerInvariant().TrimEnd('.', '!');
        return t is "thank you" or "thanks for watching" or "thank you for watching" or "you" or "song lyrics" or "music" or "lyrics"
               || t.StartsWith("lyrics of “")
               || t.StartsWith("subtitles by") || t.StartsWith("transcribed by") || t.Contains("amara.org");
    }
}
