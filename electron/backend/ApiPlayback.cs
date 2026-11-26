using System.Collections.Concurrent;
using Halcyon.Core;

namespace Phoenix.Backend;

/// <summary>
/// settings → playback, as on Windows: crossfade, volume levelling and the equalizer, worked by VLC's engine.
/// Levelling uses the ReplayGain tags in the files; songs without them are given an average gain, or evened out as
/// they play by VLC's volume normaliser when "measure" is on (VLC can't measure a song ahead of playing it).
/// </summary>
public static partial class Api
{
    /// <summary>The Windows app's equalizer presets: ten bands, 31 Hz to 16 kHz, in decibels.</summary>
    static readonly (string Name, double[] Gains)[] EqPresets =
    {
        ("flat", new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
        ("acoustic", new double[] { 4, 4, 3, 1, 1.5, 1.5, 3, 3.5, 3, 2 }),
        ("bass boost", new double[] { 6, 5, 4, 2.5, 1, 0, 0, 0, 0, 0 }),
        ("classical", new double[] { 4, 3, 2.5, 2, -1, -1, 0, 2, 3, 3.5 }),
        ("dance", new double[] { 4, 6, 4.5, 0, 1.5, 3, 4.5, 4, 3, 0 }),
        ("electronic", new double[] { 4, 3.5, 1, 0, -2, 2, 1, 1.5, 4, 4.5 }),
        ("hip hop", new double[] { 5, 4, 1.5, 3, -1, -1, 1.5, -0.5, 2, 3 }),
        ("jazz", new double[] { 4, 3, 1.5, 2, -1.5, -1.5, 0, 1.5, 3, 3.5 }),
        ("pop", new double[] { -1.5, -1, 0, 2, 4, 4, 2, 0, -1, -1.5 }),
        ("rock", new double[] { 5, 4, 3, 1.5, -0.5, -1, 0.5, 2.5, 3.5, 4.5 }),
        ("treble boost", new double[] { 0, 0, 0, 0, 0, 1, 2.5, 4, 5, 6 }),
        ("vocal", new double[] { -2, -3, -3, 1.5, 4, 4, 3, 1.5, 0, -1.5 }),
    };
    static readonly float[] Bands = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    static readonly ConcurrentDictionary<string, bool> HasGain = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a file carries ReplayGain tags (read once per file).</summary>
    static bool CarriesGain(string file) => HasGain.GetOrAdd(file, f =>
    {
        try { using var t = TagLib.File.Create(f); return !double.IsNaN(t.Tag.ReplayGainTrackGain) || !double.IsNaN(t.Tag.ReplayGainAlbumGain); }
        catch { return false; }
    });

    /// <summary>What VLC is told for each song about levelling.</summary>
    static IEnumerable<string> LevelOptions(string url, bool isVideo)
    {
        if (!S.Levelling || isVideo || P.Current?.Track == null) yield break;
        yield return ":audio-replay-gain-mode=" + (S.LevelByAlbum ? "album" : "track");
        yield return ":audio-replay-gain-preamp=0";
        yield return ":audio-replay-gain-default=-7";
        if (S.LevelMeasure && !url.Contains("://") && File.Exists(url) && !CarriesGain(url)) yield return ":audio-filter=normvol";
    }

    static void ApplyPlayback()
    {
        var e = Program.Engine;
        e.Crossfade = S.Crossfade;
        if (S.EqGains.Length != Bands.Length) S.EqGains = new double[Bands.Length];
        e.SetEqualizer(S.EqEnabled ? S.EqGains : null);
    }

    static object PlaybackState() => new
    {
        crossfade = S.Crossfade, eqEnabled = S.EqEnabled, eqPreset = S.EqPreset, eqGains = S.EqGains,
        presets = EqPresets.Select(x => x.Name).Append("custom"), bands = Bands,
        levelling = S.Levelling, levelByAlbum = S.LevelByAlbum, levelMeasure = S.LevelMeasure
    };

    static void RegisterPlayback()
    {
        Program.Engine.MediaOptions = LevelOptions;
        // only music crossfades: not videos, podcasts or audiobooks
        P.CurrentChanged += () => Program.Engine.FadeThis = P.Current?.Track != null;
        ApplyPlayback();

        Rpc.On("playback.state", _ => PlaybackState());
        Rpc.On("playback.set", p =>
        {
            if (p.Has("crossfade")) S.Crossfade = Math.Clamp(Math.Round(p.Num("crossfade")), 0, 12);
            if (p.Has("eqEnabled")) S.EqEnabled = p.Bool("eqEnabled");
            if (p.Has("eqPreset"))
            {
                S.EqPreset = p.Str("eqPreset");
                var preset = EqPresets.FirstOrDefault(x => x.Name == S.EqPreset);
                if (preset.Gains != null) S.EqGains = preset.Gains.ToArray();
            }
            if (p.Has("band"))
            {
                // moving a band by hand leaves the preset behind
                var i = p.Int("band");
                if (S.EqGains.Length != Bands.Length) S.EqGains = new double[Bands.Length];
                if (i >= 0 && i < Bands.Length) S.EqGains[i] = Math.Clamp(Math.Round(p.Num("gain") * 2) / 2, -12, 12);
                S.EqPreset = "custom";
            }
            if (p.Has("levelling")) S.Levelling = p.Bool("levelling");
            if (p.Has("levelByAlbum")) S.LevelByAlbum = p.Bool("levelByAlbum");
            if (p.Has("levelMeasure")) S.LevelMeasure = p.Bool("levelMeasure");
            Save();
            ApplyPlayback();
            return PlaybackState();
        });
        Rpc.On("playback.bands", _ => Program.Engine.BandFrequencies);
    }
}
