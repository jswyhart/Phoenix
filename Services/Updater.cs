using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Halcyon.Services;

/// <summary>
/// Automatic updates: asks GitHub for Phoenix's newest release and, if it is newer than this copy, downloads it and puts
/// it in place by itself: an installed copy runs the installer silently, a portable copy (unpacked from the zip) swaps
/// its own files for the new zip's. Phoenix then starts again. Nothing about you or your library is sent: only a
/// request for the public release list.
/// </summary>
public static class Updater
{
    public const string Repo = "jswyhart/Phoenix";
    static string ReleasePrefix => $"https://github.com/{Repo}/releases/";

    static readonly HttpClient Http = Create();
    static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PhoenixMediaPlayer/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
    public static string CurrentText => Current.ToString(3);
    public static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    /// <summary>This copy was put here by the installer (which can then update it); a copy unpacked from the zip is updated by hand.</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));
    public static string ReleasesPage => ReleasePrefix + "latest";

    public record Release(Version Version, string Name, string Notes, string Page, string InstallerUrl, string InstallerName, long InstallerSize, string Sha256,
        string ZipUrl = "", string ZipName = "", long ZipSize = 0, string ZipSha256 = "")
    {
        public bool IsNewer => Version > Current;
        public string VersionText => Version.ToString(3);
        /// <summary>What updates this copy: the installer for an installed copy, the zip for a portable one.</summary>
        public (string Url, string Name, long Size, string Sha256) Package(bool installed) =>
            installed ? (InstallerUrl, InstallerName, InstallerSize, Sha256) : (ZipUrl, ZipName, ZipSize, ZipSha256);
        public bool CanUpdate(bool installed) => Package(installed).Url != "";
        public bool CanUpdate() => CanUpdate(IsInstalled);
    }

    /// <summary>The newest release that was found (kept for settings to show).</summary>
    public static Release? Latest { get; private set; }

    public static Version? ParseVersion(string tag)
    {
        var digits = new string(tag.TrimStart('v', 'V').TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        if (digits == "") return null;
        var parts = digits.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).Concat(new[] { 0, 0, 0 }).Take(3).ToArray();
        return new Version(parts[0], parts[1], parts[2]);
    }

    public static Release? Parse(JsonElement r, string arch)
    {
        if (r.ValueKind != JsonValueKind.Object || r.O("draft").ValueKind == JsonValueKind.True) return null;
        var version = ParseVersion(r.S("tag_name"));
        if (version == null) return null;
        (string Url, string Name, long Size, string Hash) Asset(string ending)
        {
            var asset = r.A("assets").FirstOrDefault(a => a.S("name").EndsWith(ending, StringComparison.OrdinalIgnoreCase));
            if (asset.ValueKind != JsonValueKind.Object || !asset.S("browser_download_url").StartsWith(ReleasePrefix + "download/", StringComparison.OrdinalIgnoreCase)) return ("", "", 0, "");
            var digest = asset.S("digest");
            return (asset.S("browser_download_url"), asset.S("name"), asset.L("size"), digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : "");
        }
        var setup = Asset($"win-{arch}-setup.exe");
        var zip = Asset($"win-{arch}.zip");
        var page = r.S("html_url").StartsWith(ReleasePrefix, StringComparison.OrdinalIgnoreCase) ? r.S("html_url") : ReleasesPage;
        return new Release(version, r.S("name") is { Length: > 0 } n ? n : r.S("tag_name"), r.S("body"), page, setup.Url, setup.Name, setup.Size, setup.Hash,
            zip.Url, zip.Name, zip.Size, zip.Hash);
    }

    /// <summary>Asks GitHub for the newest release. Null when there is none yet.</summary>
    public static async Task<Release?> CheckAsync()
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return Latest = null;
        response.EnsureSuccessStatusCode();
        return Latest = Parse(J.Parse(await response.Content.ReadAsStringAsync()), Arch);
    }

    /// <summary>Every published release, newest version first.</summary>
    public static async Task<List<Release>> AllAsync()
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases?per_page=30");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new();
        response.EnsureSuccessStatusCode();
        return ParseAll(J.Parse(await response.Content.ReadAsStringAsync()), Arch);
    }

    public static List<Release> ParseAll(JsonElement r, string arch) => r.ValueKind != JsonValueKind.Array ? new()
        : r.EnumerateArray().Where(x => x.O("prerelease").ValueKind != JsonValueKind.True).Select(x => Parse(x, arch)).Where(x => x != null).Select(x => x!)
            .OrderByDescending(x => x.Version).ToList();

    /// <summary>The release one step back from a version: the newest one that is older and can update this kind of copy.</summary>
    public static Release? Before(IEnumerable<Release> releases, Version version, bool installed = true) =>
        releases.Where(x => x.Version < version && x.CanUpdate(installed)).OrderByDescending(x => x.Version).FirstOrDefault();

    // ---------- going back one version ----------

    /// <summary>Where the installer of the replaced version is kept (in the data folder, so it survives the update).</summary>
    public static string PreviousFolder => Path.Combine(Halcyon.Core.AppPaths.DataDir, "previous-version");

    /// <summary>The version an update can be undone to, if its installer was kept: (version, installer file).</summary>
    public static (string Version, string File)? Kept
    {
        get
        {
            var s = App.Settings;
            return s.PreviousVersion != "" && s.PreviousInstaller != "" && File.Exists(s.PreviousInstaller) && ParseVersion(s.PreviousVersion) is { } v && v != Current
                ? (s.PreviousVersion, s.PreviousInstaller) : null;
        }
    }

    /// <summary>
    /// Before an update: fetches the installer of the version that is running now and keeps it, so the update can be
    /// undone. False when that version was never published (there is then nothing to keep).
    /// </summary>
    public static async Task<bool> KeepCurrentAsync(IEnumerable<Release>? known = null)
    {
        var s = App.Settings;
        if (!s.KeepPrevious) return false;
        if (s.PreviousVersion == CurrentText && File.Exists(s.PreviousInstaller) && s.PreviousInstaller.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) != IsInstalled) return true;
        var mine = (known ?? await AllAsync()).FirstOrDefault(x => x.Version == new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build)) && x.CanUpdate());
        if (mine == null) return false;
        var file = await DownloadAsync(mine, _ => { }, PreviousFolder);
        // only one earlier version is kept
        foreach (var old in Directory.GetFiles(PreviousFolder).Where(f => !string.Equals(f, file, StringComparison.OrdinalIgnoreCase))) try { File.Delete(old); } catch { }
        s.PreviousVersion = CurrentText;
        s.PreviousInstaller = file;
        if (!App.SmokeMode) s.Save();
        return true;
    }

    /// <summary>
    /// Goes back to the version before this one: its kept installer if there is one, otherwise the release before this
    /// one from GitHub. The version being left is not offered or installed again by itself.
    /// </summary>
    public static async Task RevertAsync(Action<string> status)
    {
        var file = Kept?.File;
        if (file == null)
        {
            status("looking for the version before this one…");
            var earlier = Before(await AllAsync(), Current, IsInstalled) ?? throw new Exception("No earlier version has been published to go back to.");
            file = await DownloadAsync(earlier, done => status($"downloading Phoenix {earlier.VersionText}… {done:P0}"));
        }
        var s = App.Settings;
        s.UpdateSkipped = CurrentText;   // do not come straight back to the version just left
        s.PreviousVersion = s.PreviousInstaller = "";
        s.Save();
        status(IsInstalled ? "installing… Phoenix starts again when it's done" : "putting it in place…");
        Install(file);
    }

    public static void ForgetKept()
    {
        try { if (Directory.Exists(PreviousFolder)) Directory.Delete(PreviousFolder, true); } catch { }
        App.Settings.PreviousVersion = App.Settings.PreviousInstaller = "";
    }

    // ---------- updating by itself ----------

    /// <summary>An update that has been downloaded and is installed, without questions, when Phoenix closes.</summary>
    public static (string Version, string File)? Waiting { get; private set; }

    /// <summary>When Phoenix closes: puts the waiting update in place silently (Phoenix is not started again).</summary>
    public static void InstallWaiting()
    {
        if (Waiting is not { } w || !File.Exists(w.File)) return;
        try
        {
            if (IsZip(w.File)) SwapIn(w.File, AppContext.BaseDirectory);
            else RunSilently(w.File, relaunch: false);
        }
        catch { }
        Waiting = null;
    }

    static bool IsZip(string file) => file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs an installer with no wizard. "/RELAUNCH" has it start Phoenix again when it is done (as you, not as an
    /// administrator, if an all-users install had to ask for permission).
    /// </summary>
    static void RunSilently(string installer, bool relaunch) =>
        Process.Start(new ProcessStartInfo(installer, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS" + (relaunch ? " /RELAUNCH" : "")) { UseShellExecute = true });

    /// <summary>
    /// A portable copy updating itself: the new zip is unpacked beside the app, then each file Phoenix is running from
    /// is renamed out of the way (Windows allows that for a running program and its libraries) and the new one put in
    /// its place. If anything goes wrong the old files are put back. The renamed ones are cleared away at the next start.
    /// </summary>
    public static void SwapIn(string zip, string appDir)
    {
        var stage = Path.Combine(appDir, ".update");
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        System.IO.Compression.ZipFile.ExtractToDirectory(zip, stage);
        // a zip with everything in one folder: use what is inside it
        var root = stage;
        while (!File.Exists(Path.Combine(root, "Phoenix.exe")) && Directory.GetFiles(root).Length == 0 && Directory.GetDirectories(root) is { Length: 1 } only) root = only[0];
        if (!File.Exists(Path.Combine(root, "Phoenix.exe"))) { Directory.Delete(stage, true); throw new Exception("The download has no Phoenix.exe in it."); }
        var moved = new List<(string From, string To)>();
        var added = new List<string>();
        try
        {
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(appDir, Path.GetRelativePath(root, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var aside = target + ".old";
                    if (File.Exists(aside)) File.Delete(aside);
                    File.Move(target, aside);
                    moved.Add((target, aside));
                }
                File.Move(file, target);
                added.Add(target);
            }
        }
        catch
        {
            foreach (var f in added) try { File.Delete(f); } catch { }
            foreach (var (from, to) in moved) try { File.Move(to, from); } catch { }
            try { Directory.Delete(stage, true); } catch { }
            throw;
        }
        try { Directory.Delete(stage, true); } catch { }
        // the list of what was set aside, for the next start to clear away
        try { File.AppendAllLines(Path.Combine(appDir, SwappedList), moved.Select(m => Path.GetRelativePath(appDir, m.To))); } catch { }
    }

    const string SwappedList = ".update-old.txt";

    /// <summary>At start: clears away the files a portable copy's last update renamed out of the way.</summary>
    public static void ClearSwapped(string? appDir = null)
    {
        try
        {
            var dir = appDir ?? AppContext.BaseDirectory;
            var list = Path.Combine(dir, SwappedList);
            if (File.Exists(list))
            {
                var left = File.ReadAllLines(list).Where(rel => rel.EndsWith(".old", StringComparison.OrdinalIgnoreCase)).Where(rel =>
                {
                    var f = Path.GetFullPath(Path.Combine(dir, rel));
                    if (!f.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return false;
                    try { File.Delete(f); return false; } catch { return File.Exists(f); }
                }).ToList();
                if (left.Count == 0) File.Delete(list); else File.WriteAllLines(list, left);
            }
            var stage = Path.Combine(dir, ".update");
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
        catch { }
    }

    /// <summary>Downloads a release's installer and checks it arrived whole. Returns where it was saved.</summary>
    public static async Task<string> DownloadAsync(Release release, Action<double> progress, string? folder = null)
    {
        var package = release.Package(IsInstalled);
        if (package.Url == "") throw new Exception(IsInstalled ? "This release has no installer for this kind of PC." : "This release has no zip for this kind of PC.");
        var dir = folder ?? Path.Combine(Path.GetTempPath(), "Phoenix-update");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Path.GetFileName(package.Name));
        using (var response = await Http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? package.Size;
            await using var from = await response.Content.ReadAsStreamAsync();
            await using var to = File.Create(file);
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await from.ReadAsync(buffer)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n));
                done += n;
                if (total > 0) progress(done / (double)total);
            }
        }
        var size = new FileInfo(file).Length;
        if (package.Size > 0 && size != package.Size) { File.Delete(file); throw new Exception("The download is incomplete; try again."); }
        if (package.Sha256 != "")
        {
            await using var check = File.OpenRead(file);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(check));
            if (!hash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)) { check.Close(); File.Delete(file); throw new Exception("The download does not match what GitHub published; it was discarded."); }
        }
        return file;
    }

    /// <summary>
    /// Puts a downloaded update in place and starts Phoenix again: an installer runs silently once Phoenix has closed
    /// (it starts the new version itself), a zip is swapped in at once and the new version started.
    /// </summary>
    public static void Install(string file)
    {
        App.Settings.Save();
        if (IsZip(file))
        {
            SwapIn(file, AppContext.BaseDirectory);
            Storage.Restart();
            return;
        }
        RunSilently(file, relaunch: true);
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>At start: once a day, look for a newer release and say so in the status line.</summary>
    public static async Task DailyAsync()
    {
        var s = App.Settings;
        if (!s.CheckUpdates || Halcyon.Core.Fmt.Now - s.UpdateChecked < 20 * 3600) return;
        try
        {
            var release = await CheckAsync();
            s.UpdateChecked = Halcyon.Core.Fmt.Now;
            if (release is not { IsNewer: true } || release.VersionText == s.UpdateSkipped) return;
            if (!s.AutoUpdate || !release.CanUpdate())
            {
                App.Win.SetStatus($"Phoenix {release.VersionText} is available: settings → updates");
                return;
            }
            // updating by itself: fetch it now, install it when Phoenix is closed
            try { await KeepCurrentAsync(); } catch { }
            var file = await DownloadAsync(release, _ => { });
            Waiting = (release.VersionText, file);
            App.Win.SetStatus($"Phoenix {release.VersionText} will be installed when you close Phoenix");
        }
        catch { /* offline, or GitHub is busy: tomorrow */ }
    }
}
