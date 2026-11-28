using System.Collections.Concurrent;
using Halcyon.Core;
using Halcyon.Services;

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
        // your own equalizer plus the auto equalizer's adjustment for the song playing
        e.SetEqualizer(AutoEq.Combine(S, P.Current?.Track));
    }

    static CancellationTokenSource? _autoEqScan, _aiDownload;
    static string _autoEqStatus = "";

    static object AutoEqState()
    {
        var now = P.Current?.Track;
        var gains = now != null ? AutoEq.GainsFor(now, S) : null;
        return new
        {
            on = S.AutoEq, profile = S.AutoEqProfile, store = S.AutoEqStore,
            profiles = AutoEq.Profiles.Select(x => new { id = x.Id, name = x.Name, about = x.About }),
            stores = AutoEq.Stores.Select(x => new { id = x.Id, name = x.Name }),
            nowGains = gains, nowMeasured = gains != null, playing = now != null,
            nowStyles = now != null && S.AutoEqAi ? AutoEqAi.Describe(AutoEq.StylesFor(now)) : "",
            scanning = _autoEqScan != null, status = _autoEqStatus,
            ai = S.AutoEqAi, aiHave = AutoEqAi.Have, aiDownloading = _aiDownload != null, aiRanOn = AutoEqAi.RanOn, aiMb = AutoEqAi.Megabytes,
            headphoneOn = S.HeadphoneOn, headphoneName = S.HeadphoneName, headphoneSource = S.HeadphoneSource, headphoneGains = S.HeadphoneGains,
        };
    }

    static object PlaybackState() => new
    {
        crossfade = S.Crossfade, eqEnabled = S.EqEnabled, eqPreset = S.EqPreset, eqGains = S.EqGains,
        presets = EqPresets.Select(x => x.Name).Append("custom"), bands = Bands,
        levelling = S.Levelling, levelByAlbum = S.LevelByAlbum, levelMeasure = S.LevelMeasure,
        resumeOnStart = S.ResumeOnStart,
        autoEq = AutoEqState()
    };

    static void RegisterPlayback()
    {
        Program.Engine.MediaOptions = LevelOptions;
        // only music crossfades: not videos, podcasts or audiobooks
        P.CurrentChanged += () => Program.Engine.FadeThis = P.Current?.Track != null;
        // the auto equalizer follows the song playing, and measures a song the first time it plays if it hasn't been yet
        AutoEq.Decode = (file, cancel) => (Program.Engine.DecodeMono(file, 44100, cancel), 44100);
        P.CurrentChanged += () => { ApplyPlayback(); AutoEq.EnsureMeasured(S, P.Current?.Track); };
        AutoEq.Measured += t => MainLoop.Post(() => { if (P.Current?.Track == t) { ApplyPlayback(); Rpc.Emit("autoeq", AutoEqState()); } });
        ApplyPlayback();

        // the self-test: measure a song of noise, keep it in its album folder's file, and give its measurement to a song
        Rpc.On("smoke.autoEqNoise", async p =>
        {
            var dir = Path.Combine(p.Str("dir"), "autoeq", "Noise Album");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "01 Noise.wav");
            var rnd = new Random(3);
            using (var f = File.Create(file))
            using (var w = new BinaryWriter(f))
            {
                const int rate = 44100, seconds = 6;
                var data = rate * seconds * 2;
                w.Write("RIFF"u8.ToArray()); w.Write(36 + data); w.Write("WAVEfmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8.ToArray()); w.Write(data);
                for (var i = 0; i < rate * seconds; i++) w.Write((short)rnd.Next(-8000, 8000));
            }
            var noise = new Track { Source = "local", SourceId = file, Location = file, Title = "Noise" };
            var (_, measured) = await AutoEq.ScanAsync(new[] { noise }, "folder", null, CancellationToken.None);
            var levels = AutoEq.LevelsFor(noise);
            if (levels != null && Lib.ById.GetValueOrDefault(p.Long("id")) is { } song) AutoEq.Remember(song, levels, "phoenix");
            return new { gains = levels != null ? AutoEq.Gains(levels, "accuracy") : null, kept = measured == 1 && File.Exists(Path.Combine(dir, AutoEq.FolderFile)) };
        });

        // the AI part's model, downloaded on demand
        Rpc.On("autoeq.aiDownload", downloadParams =>
        {
            if (_aiDownload != null) { _aiDownload.Cancel(); return AutoEqState(); }
            _aiDownload = new CancellationTokenSource();
            var cancel = _aiDownload.Token;
            void Tell(string s) => MainLoop.Post(() => { _autoEqStatus = s; Rpc.Emit("autoeq", AutoEqState()); });
            _ = Task.Run(async () =>
            {
                try
                {
                    await AutoEqAi.DownloadAsync(f => Tell($"downloading the model: {Math.Round(f * 100)}%"), cancel);
                    MainLoop.Post(() => _aiDownload = null);
                    Tell("the model is here: choose MEASURE ALL SONGS NOW to listen to your songs, or they are listened to as they play");
                }
                catch (OperationCanceledException) { MainLoop.Post(() => _aiDownload = null); Tell(""); }
                catch (Exception ex) { MainLoop.Post(() => _aiDownload = null); Tell("couldn't download the model: " + ex.Message); }
            });
            return AutoEqState();
        });

        // headphones and speakers: AutoEq's list, and a profile put on the equalizer
        Rpc.On("headphones.search", async p => (await HeadphoneEq.SearchAsync(p.Str("q"))).Select(m => new { name = m.Name, path = m.Path, source = m.Source, label = m.Label }));
        Rpc.On("headphones.use", async p =>
        {
            var m = new HeadphoneEq.Model(p.Str("name"), p.Str("path"), p.Str("source"));
            var (gains, _) = await HeadphoneEq.FetchAsync(m);
            (S.HeadphoneName, S.HeadphoneSource, S.HeadphoneGains, S.HeadphoneOn) = (m.Name, m.Source, gains, true);
            Save();
            ApplyPlayback();
            return AutoEqState();
        });

        Rpc.On("autoeq.scan", scanParams =>
        {
            if (_autoEqScan != null) { _autoEqScan.Cancel(); return AutoEqState(); }
            _autoEqScan = new CancellationTokenSource();
            var cancel = _autoEqScan.Token;
            var songs = Lib.Tracks.ToList();
            var store = S.AutoEqStore;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            void Tell(string s) => MainLoop.Post(() => { _autoEqStatus = s; Rpc.Emit("autoeq", AutoEqState()); });
            _ = Task.Run(async () =>
            {
                try
                {
                    var (done, measured) = await AutoEq.ScanAsync(songs, store, (d, total, m) => Tell($"measuring {d + 1} of {total}   ({m} measured so far)"), cancel);
                    MainLoop.Post(() => _autoEqScan = null);
                    Tell(done == 0 ? "every song is measured already" : $"measured {measured} of {done} songs in {watch.Elapsed.TotalSeconds:0} s");
                }
                catch (OperationCanceledException) { MainLoop.Post(() => _autoEqScan = null); Tell("stopped; the songs measured so far are kept"); }
                MainLoop.Post(ApplyPlayback);
            });
            return AutoEqState();
        });

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
            if (p.Has("resumeOnStart")) { S.ResumeOnStart = p.Bool("resumeOnStart"); if (!S.ResumeOnStart) S.ResumeTracks = new(); }
            if (p.Has("levelByAlbum")) S.LevelByAlbum = p.Bool("levelByAlbum");
            if (p.Has("levelMeasure")) S.LevelMeasure = p.Bool("levelMeasure");
            if (p.Has("autoEq")) { S.AutoEq = p.Bool("autoEq"); AutoEq.EnsureMeasured(S, P.Current?.Track); }
            if (p.Has("autoEqProfile") && AutoEq.Profiles.Any(x => x.Id == p.Str("autoEqProfile"))) S.AutoEqProfile = p.Str("autoEqProfile");
            if (p.Has("autoEqStore") && AutoEq.Stores.Any(x => x.Id == p.Str("autoEqStore"))) S.AutoEqStore = p.Str("autoEqStore");
            if (p.Has("autoEqAi")) S.AutoEqAi = p.Bool("autoEqAi");
            if (p.Has("headphoneOn")) S.HeadphoneOn = p.Bool("headphoneOn") && S.HeadphoneGains.Length == AutoEq.Bands.Length;
            Save();
            ApplyPlayback();
            return PlaybackState();
        });
        Rpc.On("playback.bands", _ => Program.Engine.BandFrequencies);
    }
}
