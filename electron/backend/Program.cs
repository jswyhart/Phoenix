using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Halcyon.Core;

namespace Phoenix.Backend;

/// <summary>
/// The helper's one working thread. Every request from the window, every engine event and everything the shared core
/// asks to run "on the UI thread" happens here, so the core is used exactly as the other apps use it.
/// </summary>
public static class MainLoop
{
    static readonly BlockingCollection<Action> Queue = new();
    static int _thread = -1;

    public static bool OnThread => Environment.CurrentManagedThreadId == _thread;
    public static void Post(Action a) { try { Queue.Add(a); } catch (InvalidOperationException) { /* shutting down */ } }

    /// <summary>Runs an action on the working thread and waits for it.</summary>
    public static void Invoke(Action a)
    {
        if (OnThread) { a(); return; }
        var done = new ManualResetEventSlim();
        Exception? error = null;
        Post(() => { try { a(); } catch (Exception ex) { error = ex; } finally { done.Set(); } });
        done.Wait();
        if (error != null) throw error;
    }

    public static void Run()
    {
        _thread = Environment.CurrentManagedThreadId;
        SynchronizationContext.SetSynchronizationContext(new Context());
        foreach (var a in Queue.GetConsumingEnumerable())
            try { a(); }
            catch (Exception ex) { Program.LogError("unhandled", ex); }
    }

    public static void Quit() => Queue.CompleteAdding();

    /// <summary>Calls back on the working thread at a steady interval.</summary>
    public static Timer Every(int ms, Action a) => new(_ => Post(a), null, ms, ms);

    sealed class Context : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => MainLoop.Post(() => d(state));
        public override void Send(SendOrPostCallback d, object? state) => Invoke(() => d(state));
    }
}

/// <summary>JSON lines over stdin/stdout: requests {id, method, params} in, {id, result|error} and {event, data} out.</summary>
public static class Rpc
{
    static readonly Stream Out = Console.OpenStandardOutput();
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true };
    static readonly Dictionary<string, Func<JsonElement, Task<object?>>> Handlers = new();

    public static void On(string method, Func<JsonElement, Task<object?>> handler) => Handlers[method] = handler;
    public static void On(string method, Func<JsonElement, object?> handler) => Handlers[method] = p => Task.FromResult(handler(p));
    public static void On(string method, Action<JsonElement> handler) => Handlers[method] = p => { handler(p); return Task.FromResult<object?>(null); };
    public static void On(string method, Func<JsonElement, Task> handler) => Handlers[method] = async p => { await handler(p); return null; };

    public static void Emit(string name, object? data = null) => Write(new { @event = name, data });

    static void Write(object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        lock (Out)
        {
            try { Out.Write(bytes); Out.WriteByte((byte)'\n'); Out.Flush(); }
            catch { /* the window has gone */ }
        }
    }

    /// <summary>Reads requests until the window closes its end of the pipe, then shuts the helper down.</summary>
    public static void Start()
    {
        new Thread(() =>
        {
            try
            {
                using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                while (reader.ReadLine() is { } line)
                    if (line.Length > 0) { var l = line; MainLoop.Post(() => Handle(l)); }
            }
            catch { }
            MainLoop.Post(Program.Shutdown);
        }) { IsBackground = true, Name = "rpc" }.Start();
    }

    static async void Handle(string line)
    {
        long id = 0;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetInt64();
            var method = root.GetProperty("method").GetString() ?? "";
            if (!Handlers.TryGetValue(method, out var handler)) throw new InvalidOperationException("unknown method " + method);
            var result = await handler(root.TryGetProperty("params", out var p) ? p.Clone() : default);
            Write(new { id, result });
        }
        catch (Exception ex)
        {
            Program.LogError("request", ex);
            Write(new { id, error = ex.Message });
        }
    }
}

/// <summary>Reading request parameters.</summary>
public static class Params
{
    static bool Has(JsonElement p, string name, out JsonElement v)
    {
        v = default;
        return p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out v) && v.ValueKind != JsonValueKind.Null;
    }

    public static string Str(this JsonElement p, string name, string fallback = "") => Has(p, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
    public static string? StrOrNull(this JsonElement p, string name) => Has(p, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static long Long(this JsonElement p, string name, long fallback = 0) => Has(p, name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : fallback;
    public static long? LongOrNull(this JsonElement p, string name) => Has(p, name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : null;
    public static int Int(this JsonElement p, string name, int fallback = 0) => (int)p.Long(name, fallback);
    public static double Num(this JsonElement p, string name, double fallback = 0) => Has(p, name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
    public static bool Bool(this JsonElement p, string name, bool fallback = false) => Has(p, name, out var v) ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && fallback) : fallback;
    public static bool Has(this JsonElement p, string name) => Has(p, name, out _);
    public static List<long> Longs(this JsonElement p, string name) => Has(p, name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => (long)x.GetDouble()).ToList() : new();
    public static List<string> Strs(this JsonElement p, string name) => Has(p, name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new();
    public static JsonElement Obj(this JsonElement p, string name) => Has(p, name, out var v) ? v : default;
}

static class Program
{
    public static VlcEngine Engine = null!;
    public static VideoSocket Frames = null!;
    public static bool SmokeMode;
    public static string DataDir = "";
    static bool _shutDown;

    public static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public static void LogError(string what, Exception ex)
    {
        try { File.AppendAllText(Path.Combine(DataDir == "" ? Path.GetTempPath() : DataDir, "error.log"), $"{DateTime.Now:u} {what}\n{ex}\n\n"); } catch { }
    }

    static int Main(string[] args)
    {
        // stdout carries the protocol and nothing else
        Console.SetOut(Console.Error);
        var data = Array.IndexOf(args, "--data");
        if (data < 0 || data + 1 >= args.Length) { Console.Error.WriteLine("usage: Phoenix.Backend --data <folder> [--smoke]"); return 64; }
        DataDir = Path.GetFullPath(args[data + 1]);
        SmokeMode = args.Contains("--smoke");
        Directory.CreateDirectory(DataDir);

        MainLoop.Post(Start);
        MainLoop.Run();
        return 0;
    }

    static void Start()
    {
        App.Ui = MainLoop.Invoke;
        App.Secrets = new Secrets(DataDir);
        App.DeviceName = IsMac ? "Mac" : "PC";
        AppPaths.DataDir = DataDir;
        try { Engine = new VlcEngine(); }
        catch (Exception ex)
        {
            // no playback engine: say so plainly instead of crashing, and keep the details for troubleshooting
            LogError("starting the playback engine failed", ex);
            Rpc.Emit("fatal", ex.Message);
            Environment.Exit(2);
        }
        Frames = new VideoSocket(Engine.Video);
        Engine.Video.Cleared += () => Rpc.Emit("video.cleared");
        App.Init(DataDir, Engine, () => SmokeMode ? new Settings { WriteTagsToFiles = true } : new Settings
        {
            WriteTagsToFiles = true,
            MusicFolders = Existing(Environment.SpecialFolder.MyMusic),
            VideoFolders = Existing(Environment.SpecialFolder.MyVideos),
            PictureFolders = Existing(Environment.SpecialFolder.MyPictures)
        });
        Api.Register();
        Rpc.Start();
        Rpc.Emit("ready");
    }

    static List<string> Existing(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        return path != "" && Directory.Exists(path) ? new List<string> { path } : new List<string>();
    }

    public static void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        try
        {
            App.Player.SaveEpisodePosition();
            App.Settings.Shuffle = App.Player.Shuffle;
            App.Settings.Repeat = App.Player.Repeat;
            if (!SmokeMode) App.Settings.Save();
        }
        catch { }
        try { Api.Sync.Stop(); } catch { }
        try { Engine.Dispose(); } catch { }
        Environment.Exit(0);
    }
}
