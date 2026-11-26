using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Halcyon.Core;

namespace Halcyon.Services;

/// <summary>
/// Correction for your headphones or speakers, from the AutoEq project (github.com/jaakkopasanen/AutoEq, MIT licence):
/// measured frequency responses of thousands of models, each with a ten-band correction towards a neutral target. Pick
/// your model and its correction goes on top of everything else the equalizer does. The list of models and the chosen
/// profile are fetched from AutoEq's GitHub when you choose one.
/// </summary>
public static class HeadphoneEq
{
    const string Raw = "https://raw.githubusercontent.com/jaakkopasanen/AutoEq/master/results/";

    public record Model(string Name, string Path, string Source)
    {
        public string Label => $"{Name}   (measured by {Source})";
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    static List<Model>? _index;
    static string IndexFile => System.IO.Path.Combine(AppPaths.DataDir, "autoeq-index.md");
    static readonly Regex Line = new(@"^- \[(?<name>.+?)\]\(\./(?<path>.+?)\) by (?<source>.+)$", RegexOptions.Compiled);

    static Task<string> Get(string url, CancellationToken cancel)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Phoenix/1.0");
        return Http.SendAsync(req, cancel).ContinueWith(t =>
        {
            using var resp = t.Result;
            resp.EnsureSuccessStatusCode();
            return resp.Content.ReadAsStringAsync(cancel).Result;
        }, cancel);
    }

    /// <summary>Every model AutoEq has a correction for (the list is kept for a week).</summary>
    public static async Task<List<Model>> IndexAsync(CancellationToken cancel = default)
    {
        if (_index != null) return _index;
        string text;
        if (File.Exists(IndexFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(IndexFile) < TimeSpan.FromDays(7)) text = await File.ReadAllTextAsync(IndexFile, cancel);
        else
        {
            text = await Get(Raw + "INDEX.md", cancel);
            try { Directory.CreateDirectory(AppPaths.DataDir); await File.WriteAllTextAsync(IndexFile, text, cancel); } catch { }
        }
        _index = text.Split('\n').Select(l => Line.Match(l.Trim())).Where(m => m.Success)
            .Select(m => new Model(m.Groups["name"].Value, m.Groups["path"].Value, m.Groups["source"].Value)).ToList();
        return _index;
    }

    /// <summary>Models whose names hold every word typed, best matches first.</summary>
    public static async Task<List<Model>> SearchAsync(string query, int max = 40, CancellationToken cancel = default)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return new();
        var all = await IndexAsync(cancel);
        return all.Where(m => words.All(w => m.Name.ToLowerInvariant().Contains(w)))
            .OrderBy(m => m.Name.Length).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    /// <summary>A model's ten-band correction (31 Hz to 16 kHz, dB) and its preamp.</summary>
    public static async Task<(double[] Gains, double Preamp)> FetchAsync(Model m, CancellationToken cancel = default)
    {
        var file = Uri.EscapeDataString(m.Name + " FixedBandEQ.txt");
        var text = await Get(Raw + m.Path + "/" + file, cancel);
        return Parse(text);
    }

    /// <summary>Reads AutoEq's FixedBandEQ.txt ("Preamp: -4.1 dB", "Filter 1: ON PK Fc 31 Hz Gain 3.9 dB Q 1.41"…).</summary>
    public static (double[] Gains, double Preamp) Parse(string text)
    {
        var gains = new double[AutoEq.Bands.Length];
        double preamp = 0;
        var found = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var p = Regex.Match(line, @"^Preamp:\s*(-?[\d.]+)\s*dB", RegexOptions.IgnoreCase);
            if (p.Success) { preamp = double.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
            var f = Regex.Match(line, @"Fc\s+([\d.]+)\s*Hz\s+Gain\s+(-?[\d.]+)\s*dB", RegexOptions.IgnoreCase);
            if (!f.Success) continue;
            var hz = double.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture);
            var band = Array.FindIndex(AutoEq.Bands, b => Math.Abs(b - hz) / b < 0.1);
            if (band < 0) continue;
            gains[band] = Math.Clamp(double.Parse(f.Groups[2].Value, CultureInfo.InvariantCulture), -12, 12);
            found++;
        }
        if (found < AutoEq.Bands.Length / 2) throw new InvalidDataException("That profile has no ten-band correction.");
        return (gains, preamp);
    }
}
