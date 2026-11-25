using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Halcyon.Services;

/// <summary>
/// Automatic updates: asks GitHub for Phoenix's newest release and, if it is newer than this copy, offers to download
/// and run its installer. Nothing about you or your library is sent: only a request for the public release list.
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

    public record Release(Version Version, string Name, string Notes, string Page, string InstallerUrl, string InstallerName, long InstallerSize, string Sha256)
    {
        public bool IsNewer => Version > Current;
        public string VersionText => Version.ToString(3);
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
        var asset = r.A("assets").FirstOrDefault(a => a.S("name").EndsWith($"win-{arch}-setup.exe", StringComparison.OrdinalIgnoreCase));
        string url = "", name = "", hash = "";
        long size = 0;
        if (asset.ValueKind == JsonValueKind.Object && asset.S("browser_download_url").StartsWith(ReleasePrefix + "download/", StringComparison.OrdinalIgnoreCase))
        {
            url = asset.S("browser_download_url"); name = asset.S("name"); size = asset.L("size");
            if (asset.S("digest").StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) hash = asset.S("digest")[7..];
        }
        var page = r.S("html_url").StartsWith(ReleasePrefix, StringComparison.OrdinalIgnoreCase) ? r.S("html_url") : ReleasesPage;
        return new Release(version, r.S("name") is { Length: > 0 } n ? n : r.S("tag_name"), r.S("body"), page, url, name, size, hash);
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

    /// <summary>The release one step back from a version: the newest one that is older and has an installer for this PC.</summary>
    public static Release? Before(IEnumerable<Release> releases, Version version) =>
        releases.Where(x => x.Version < version && x.InstallerUrl != "").OrderByDescending(x => x.Version).FirstOrDefault();

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
        if (!s.KeepPrevious || !IsInstalled) return false;
        if (s.PreviousVersion == CurrentText && File.Exists(s.PreviousInstaller)) return true;
        var mine = (known ?? await AllAsync()).FirstOrDefault(x => x.Version == new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build)) && x.InstallerUrl != "");
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
        if (!IsInstalled) throw new Exception("This is a portable copy: unpack the earlier zip from the release page over it.");
        var file = Kept?.File;
        if (file == null)
        {
            status("looking for the version before this one…");
            var earlier = Before(await AllAsync(), Current) ?? throw new Exception("No earlier version has been published to go back to.");
            file = await DownloadAsync(earlier, done => status($"downloading Phoenix {earlier.VersionText}… {done:P0}"));
        }
        var s = App.Settings;
        s.UpdateSkipped = CurrentText;   // do not come straight back to the version just left
        s.PreviousVersion = s.PreviousInstaller = "";
        s.Save();
        status("starting the installer…");
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

    /// <summary>When Phoenix closes: runs the waiting installer silently (it does not start Phoenix again).</summary>
    public static void InstallWaiting()
    {
        if (Waiting is not { } w || !File.Exists(w.File)) return;
        try { Process.Start(new ProcessStartInfo(w.File, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true }); } catch { }
        Waiting = null;
    }

    /// <summary>Downloads a release's installer and checks it arrived whole. Returns where it was saved.</summary>
    public static async Task<string> DownloadAsync(Release release, Action<double> progress, string? folder = null)
    {
        if (release.InstallerUrl == "") throw new Exception("This release has no installer for this kind of PC.");
        var dir = folder ?? Path.Combine(Path.GetTempPath(), "Phoenix-update");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Path.GetFileName(release.InstallerName));
        using (var response = await Http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.InstallerSize;
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
        if (release.InstallerSize > 0 && size != release.InstallerSize) { File.Delete(file); throw new Exception("The download is incomplete; try again."); }
        if (release.Sha256 != "")
        {
            await using var check = File.OpenRead(file);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(check));
            if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) { check.Close(); File.Delete(file); throw new Exception("The download does not match what GitHub published; it was discarded."); }
        }
        return file;
    }

    /// <summary>Runs the downloaded installer and closes Phoenix so it can be replaced.</summary>
    public static void Install(string file)
    {
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
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
            if (!s.AutoUpdate || !IsInstalled || release.InstallerUrl == "")
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
