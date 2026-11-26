namespace Halcyon.Core;

/// <summary>The equalizer's presets, the same as the desktop apps': ten bands, 31 Hz to 16 kHz, in decibels.</summary>
public static class EqPresets
{
    public static readonly float[] Bands = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    public static readonly (string Name, double[] Gains)[] All =
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

    /// <summary>The gains in use: the preset's, or your own for "custom".</summary>
    public static double[] Current(Settings s)
    {
        var p = All.FirstOrDefault(x => x.Name == s.EqPreset);
        if (p.Gains != null) return p.Gains;
        return s.EqGains.Length == Bands.Length ? s.EqGains : new double[Bands.Length];
    }

    /// <summary>The gain at any frequency, read off the ten bands on a logarithmic scale (for equalizers with other bands).</summary>
    public static double At(double[] gains, double hz)
    {
        if (hz <= Bands[0]) return gains[0];
        if (hz >= Bands[^1]) return gains[^1];
        for (var i = 0; i < Bands.Length - 1; i++)
            if (hz <= Bands[i + 1])
            {
                var f = (Math.Log(hz) - Math.Log(Bands[i])) / (Math.Log(Bands[i + 1]) - Math.Log(Bands[i]));
                return gains[i] + (gains[i + 1] - gains[i]) * f;
            }
        return 0;
    }

    /// <summary>A song's ReplayGain in dB (by album or by song, whichever there is), or null when it carries none.</summary>
    public static double? ReplayGain(string file, bool byAlbum)
    {
        try
        {
            using var t = TagLib.File.Create(file);
            double track = t.Tag.ReplayGainTrackGain, album = t.Tag.ReplayGainAlbumGain;
            var g = byAlbum ? (double.IsNaN(album) ? track : album) : (double.IsNaN(track) ? album : track);
            return double.IsNaN(g) ? null : Math.Clamp(g, -18, 12);
        }
        catch { return null; }
    }
}
