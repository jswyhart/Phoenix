using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Halcyon.Core;
using Halcyon.Services;

namespace Phoenix.Backend;

/// <summary>
/// Updates for the Mac and Linux builds, as the Windows app has them: a daily look at GitHub for a newer release,
/// updating by itself (downloaded when found, put in place when Phoenix closes), and going back one version. A Mac
/// build is Phoenix.app; a Linux build is the folder install.sh made (~/.local/opt/phoenix). Either is swapped for the
/// new one by a small script that runs after Phoenix has closed. Nothing about you or your library is sent.
/// </summary>
public static class Updates
{
    const string Repo = Halcyon.Services.Updates.Repo;
    static string ReleasePrefix => $"https://github.com/{Repo}/releases/";
    static Settings S => App.Settings;

    static readonly HttpClient Http = Create();
    static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PhoenixMediaPlayer/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 0, 0);
    public static string CurrentText => Current.ToString(3);
    static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    /// <summary>The release file for this kind of computer ("Phoenix-mac-arm64.tar.gz").</summary>
    public static string AssetEnding => $"{(Program.IsMac ? "mac" : "linux")}-{Arch}.tar.gz";

    public record Release(Version Version, string Name, string Notes, string Page, string FileUrl, string FileName, long FileSize, string Sha256)
    {
        public bool IsNewer => Version > Current;
        public string VersionText => Version.ToString(3);
    }

    public static Release? Latest { get; private set; }
    /// <summary>A new version that has been unpacked and is put in place when Phoenix closes.</summary>
    static (string Version, string Folder)? _waiting;
    /// <summary>Set when Phoenix is to start again right after the swap (installing now, or going back).</summary>
    static bool _restart;

    static Release? Parse(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object || r.O("draft").ValueKind == JsonValueKind.True) return null;
        var version = Halcyon.Services.Updates.ParseVersion(r.S("tag_name"));
        if (version == null) return null;
        var asset = r.A("assets").FirstOrDefault(a => a.S("name").EndsWith(AssetEnding, StringComparison.OrdinalIgnoreCase)
                                                      && a.S("browser_download_url").StartsWith(ReleasePrefix + "download/", StringComparison.OrdinalIgnoreCase));
        string url = "", name = "", hash = "";
        long size = 0;
        if (asset.ValueKind == JsonValueKind.Object)
        {
            url = asset.S("browser_download_url"); name = asset.S("name"); size = asset.L("size");
            if (asset.S("digest").StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) hash = asset.S("digest")[7..];
        }
        var page = r.S("html_url").StartsWith(ReleasePrefix, StringComparison.OrdinalIgnoreCase) ? r.S("html_url") : ReleasePrefix + "latest";
        return new Release(version, r.S("name") is { Length: > 0 } n ? n : r.S("tag_name"), r.S("body"), page, url, name, size, hash);
    }

    public static async Task<Release?> CheckAsync()
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return Latest = null;
        response.EnsureSuccessStatusCode();
        return Latest = Parse(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement);
    }

    static async Task<List<Release>> AllAsync()
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases?per_page=30");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new();
        response.EnsureSuccessStatusCode();
        var r = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return r.ValueKind != JsonValueKind.Array ? new()
            : r.EnumerateArray().Where(x => x.O("prerelease").ValueKind != JsonValueKind.True).Select(Parse).Where(x => x != null).Select(x => x!).OrderByDescending(x => x.Version).ToList();
    }

    // ---------- where this copy lives ----------

    /// <summary>The folder that is swapped on an update: Phoenix.app, or the Linux install folder. Null when this copy can't update itself.</summary>
    public static string? InstallFolder
    {
        get
        {
            if (!Program.IsMac && !Program.IsLinux) return null;   // developing on Windows: nothing to replace
            var backend = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            // Mac: Phoenix.app/Contents/Resources/backend   Linux: <install>/resources/backend
            var folder = Program.IsMac ? Path.GetFullPath(Path.Combine(backend, "..", "..", "..")) : Path.GetFullPath(Path.Combine(backend, "..", ".."));
            if (Program.IsMac && !folder.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return null;
            if (Program.IsLinux && !File.Exists(Path.Combine(folder, "phoenix"))) return null;
            return Writable(Path.GetDirectoryName(folder)!) && Writable(folder) ? folder : null;
        }
    }

    static bool Writable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".phoenix-write-test-" + Guid.NewGuid().ToString("N")[..6]);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    static string Work => Path.Combine(AppPaths.DataDir, "update");
    static string PreviousFolder => Path.Combine(AppPaths.DataDir, "previous-version");
    /// <summary>The kept copy of the version before this one, if there is one.</summary>
    static string? Kept
    {
        get
        {
            var name = Program.IsMac ? "Phoenix.app" : "phoenix";
            var dir = Path.Combine(PreviousFolder, name);
            return S.PreviousVersion != "" && S.PreviousVersion != CurrentText && Directory.Exists(dir) ? dir : null;
        }
    }

    // ---------- downloading and unpacking ----------

    static async Task<string> DownloadAsync(Release release, Action<string> status)
    {
        if (release.FileUrl == "") throw new Exception($"Phoenix {release.VersionText} has no download for this kind of computer.");
        Directory.CreateDirectory(Work);
        var file = Path.Combine(Work, Path.GetFileName(release.FileName));
        using (var response = await Http.GetAsync(release.FileUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.FileSize;
            await using var from = await response.Content.ReadAsStreamAsync();
            await using var to = File.Create(file);
            var buffer = new byte[1 << 16];
            long done = 0, said = -1;
            int n;
            while ((n = await from.ReadAsync(buffer)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n));
                done += n;
                var pct = total > 0 ? done * 100 / total : 0;
                if (pct != said) { said = pct; status($"downloading Phoenix {release.VersionText}… {pct}%"); }
            }
        }
        if (release.FileSize > 0 && new FileInfo(file).Length != release.FileSize) { File.Delete(file); throw new Exception("The download is incomplete; try again."); }
        if (release.Sha256 != "")
        {
            string hash;
            await using (var check = File.OpenRead(file)) hash = Convert.ToHexString(await SHA256.HashDataAsync(check));
            if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) { File.Delete(file); throw new Exception("The download does not match what GitHub published; it was discarded."); }
        }
        return file;
    }

    /// <summary>Unpacks a downloaded release next to the library and returns the new app folder inside it.</summary>
    static async Task<string> UnpackAsync(string archive)
    {
        var dir = Path.Combine(Work, "new");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        // the system's tar keeps the links and permissions the app needs
        var tar = Process.Start(new ProcessStartInfo("tar", $"-xzf \"{archive}\" -C \"{dir}\"") { UseShellExecute = false, RedirectStandardError = true })
                  ?? throw new Exception("tar could not be started.");
        var errors = await tar.StandardError.ReadToEndAsync();
        await tar.WaitForExitAsync();
        if (tar.ExitCode != 0) throw new Exception("The download could not be unpacked: " + errors.Trim());
        try { File.Delete(archive); } catch { }
        var app = Program.IsMac ? Path.Combine(dir, "Phoenix.app") : Path.Combine(dir, "phoenix", "app");
        if (!Directory.Exists(app)) throw new Exception("The download does not hold Phoenix as expected.");
        return app;
    }

    // ---------- putting it in place ----------

    static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Writes and starts the script that waits for Phoenix to close, keeps or removes the version being replaced, moves
    /// the new one in and (if asked) starts Phoenix again.
    /// </summary>
    static void StartSwap(string newApp, bool keepOld, bool restart)
    {
        var dest = InstallFolder ?? throw new Exception("This copy of Phoenix can't replace itself here.");
        var keep = Path.Combine(PreviousFolder, Program.IsMac ? "Phoenix.app" : "phoenix");
        var lines = new List<string>
        {
            "#!/bin/sh",
            "# Puts a new version of Phoenix in place once Phoenix has closed (written by Phoenix's updater).",
            $"while kill -0 {Environment.ProcessId} 2>/dev/null || kill -0 {Program.ParentPid} 2>/dev/null; do sleep 0.3; done",
            $"rm -rf {Q(keep)}",
            keepOld ? $"mkdir -p {Q(PreviousFolder)} && mv {Q(dest)} {Q(keep)} || rm -rf {Q(dest)}" : $"rm -rf {Q(dest)}",
            $"mv {Q(newApp)} {Q(dest)}",
        };
        if (Program.IsMac)
        {
            // a download is marked as coming from the internet, and Apple Silicon needs every app signed (locally is enough)
            lines.Add($"xattr -cr {Q(dest)} 2>/dev/null");
            lines.Add($"codesign --force --deep --sign - {Q(dest)} 2>/dev/null");
        }
        if (restart) lines.Add(Program.IsMac ? $"open {Q(dest)}" : $"nohup {Q(Path.Combine(dest, "phoenix"))} >/dev/null 2>&1 &");
        lines.Add($"rm -rf {Q(Work)}");
        Directory.CreateDirectory(Work);
        var script = Path.Combine(AppPaths.DataDir, "update-swap.sh");
        File.WriteAllText(script, string.Join("\n", lines) + "\n");
        // detached, so it outlives Phoenix
        Process.Start(new ProcessStartInfo("/bin/sh", $"-c \"nohup /bin/sh {Q(script).Replace("\"", "\\\"")} >/dev/null 2>&1 &\"") { UseShellExecute = false });
    }

    /// <summary>When Phoenix closes: a waiting update goes in.</summary>
    public static void OnShutdown()
    {
        if (_waiting is not { } w || !Directory.Exists(w.Folder)) return;
        try
        {
            if (S.KeepPrevious) S.PreviousVersion = CurrentText; else { S.PreviousVersion = ""; try { Directory.Delete(PreviousFolder, true); } catch { } }
            if (!Program.SmokeMode) S.Save();
            StartSwap(w.Folder, S.KeepPrevious, _restart);
        }
        catch (Exception ex) { Program.LogError("update", ex); }
        _waiting = null;
    }

    static void Say(string text) => Rpc.Emit("update.status", text);

    /// <summary>Fetches a release and has it put in place when Phoenix closes.</summary>
    static async Task StageAsync(Release release)
    {
        var archive = await DownloadAsync(release, Say);
        Say("unpacking…");
        _waiting = (release.VersionText, await UnpackAsync(archive));
    }

    /// <summary>At start: once a day, look for a newer release; with "update by itself" on, fetch it to go in when Phoenix closes.</summary>
    public static async Task DailyAsync()
    {
        if (!S.CheckUpdates || Fmt.Now - S.UpdateChecked < 20 * 3600) return;
        try
        {
            var release = await CheckAsync();
            S.UpdateChecked = Fmt.Now;
            if (!Program.SmokeMode) S.Save();
            if (release is not { IsNewer: true } || release.VersionText == S.UpdateSkipped) return;
            if (!S.AutoUpdate || InstallFolder == null || release.FileUrl == "")
            {
                App.SetStatus($"Phoenix {release.VersionText} is available: settings → updates");
                return;
            }
            await StageAsync(release);
            App.SetStatus($"Phoenix {release.VersionText} will be put in place when you close Phoenix");
        }
        catch { /* offline, or GitHub is busy: tomorrow */ }
    }

    static object State() => new
    {
        version = CurrentText,
        check = S.CheckUpdates, auto = S.AutoUpdate, keepPrevious = S.KeepPrevious,
        canInstall = InstallFolder != null,
        installFolder = InstallFolder ?? "",
        waiting = _waiting?.Version,
        kept = Kept != null ? S.PreviousVersion : null,
        latest = Latest == null ? null : new { version = Latest.VersionText, name = Latest.Name, notes = Latest.Notes, page = Latest.Page, newer = Latest.IsNewer, hasFile = Latest.FileUrl != "", skipped = Latest.VersionText == S.UpdateSkipped }
    };

    public static void Register()
    {
        Rpc.On("updates.state", _ => State());
        Rpc.On("updates.set", p =>
        {
            if (p.Has("check")) S.CheckUpdates = p.Bool("check");
            if (p.Has("auto")) S.AutoUpdate = p.Bool("auto");
            if (p.Has("keepPrevious"))
            {
                S.KeepPrevious = p.Bool("keepPrevious");
                if (!S.KeepPrevious) { try { if (Directory.Exists(PreviousFolder)) Directory.Delete(PreviousFolder, true); } catch { } S.PreviousVersion = ""; }
            }
            if (!Program.SmokeMode) S.Save();
            return State();
        });
        Rpc.On("updates.check", async _ =>
        {
            var release = await CheckAsync();
            S.UpdateChecked = Fmt.Now;
            if (!Program.SmokeMode) S.Save();
            return new
            {
                text = release == null ? "No version of Phoenix has been published yet."
                    : !release.IsNewer ? $"This is the newest version ({CurrentText})."
                    : $"Phoenix {release.VersionText} is available (this is {CurrentText})." + (release.FileUrl == "" ? " It has no download for this kind of computer yet." : ""),
                state = State()
            };
        });
        Rpc.On("updates.skip", _ => { if (Latest != null) { S.UpdateSkipped = Latest.VersionText; if (!Program.SmokeMode) S.Save(); } return State(); });
        // installs the newest version now: it is fetched, Phoenix closes, the new one goes in and starts
        Rpc.On("updates.install", async _ =>
        {
            if (InstallFolder == null) throw new InvalidOperationException("This copy of Phoenix is in a folder it can't change. Download the new version from the release page.");
            var release = Latest is { IsNewer: true } ? Latest : await CheckAsync();
            if (release is not { IsNewer: true }) throw new InvalidOperationException("There is no newer version.");
            if (_waiting?.Version != release.VersionText) await StageAsync(release);
            _restart = true;
            Say($"Phoenix {release.VersionText} is ready: Phoenix will close and start again");
            return true;
        });
        // goes back to the version before this one: the kept copy, or the release before this one from GitHub
        Rpc.On("updates.revert", async _ =>
        {
            if (InstallFolder == null) throw new InvalidOperationException("This copy of Phoenix is in a folder it can't change. Download the earlier version from the release page.");
            string folder, version;
            if (Kept is { } kept)
            {
                // the kept copy is moved back; this version is not kept in its place
                folder = Path.Combine(Work, "new", Path.GetFileName(kept));
                Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                Directory.Move(kept, folder);
                version = S.PreviousVersion;
            }
            else
            {
                Say("looking for the version before this one…");
                var earlier = (await AllAsync()).Where(x => x.Version < Current && x.FileUrl != "").OrderByDescending(x => x.Version).FirstOrDefault()
                              ?? throw new InvalidOperationException("No earlier version has been published to go back to.");
                folder = await UnpackAsync(await DownloadAsync(earlier, Say));
                version = earlier.VersionText;
            }
            S.UpdateSkipped = CurrentText;   // do not come straight back to the version being left
            S.PreviousVersion = "";
            if (!Program.SmokeMode) S.Save();
            _waiting = null;
            StartSwapLater(folder, version);
            return version;
        });
    }

    /// <summary>Going back: the swap must not keep this version as "the one before".</summary>
    static void StartSwapLater(string folder, string version)
    {
        _revert = (version, folder);
        _restart = true;
        Say($"going back to Phoenix {version}: Phoenix will close and start again");
    }

    static (string Version, string Folder)? _revert;

    /// <summary>Called on shutdown before <see cref="OnShutdown"/>: a revert takes precedence over a waiting update.</summary>
    public static void OnShutdownRevert()
    {
        if (_revert is not { } r || !Directory.Exists(r.Folder)) return;
        try { StartSwap(r.Folder, keepOld: false, restart: true); } catch (Exception ex) { Program.LogError("going back", ex); }
        _revert = null;
        _waiting = null;
    }
}
