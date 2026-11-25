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

    /// <summary>Downloads a release's installer and checks it arrived whole. Returns where it was saved.</summary>
    public static async Task<string> DownloadAsync(Release release, Action<double> progress)
    {
        if (release.InstallerUrl == "") throw new Exception("This release has no installer for this kind of PC.");
        var dir = Path.Combine(Path.GetTempPath(), "Phoenix-update");
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
            if (release is { IsNewer: true } && release.VersionText != s.UpdateSkipped)
                App.Win.SetStatus($"Phoenix {release.VersionText} is available: settings → about");
        }
        catch { /* offline, or GitHub is busy: tomorrow */ }
    }
}
