using Halcyon.Core;
using Halcyon.Services;

namespace Phoenix.Backend;

/// <summary>Lyrics written by the speech model on this computer, as on Windows (settings → translation → lyrics, and now playing).</summary>
public static partial class Api
{
    static CancellationTokenSource? _speechDownload, _speechAll, _listening;
    static long _listeningTo = -1;
    static string _speechAllStatus = "";
    static List<SpeechLyrics.Gpu>? _gpus;   // asking Vulkan takes a moment: once is enough

    static object SpeechState() => new
    {
        models = SpeechLyrics.Models.Select(m => new { id = m.Id, name = m.Name, mb = m.Megabytes, note = m.Note, have = SpeechLyrics.Have(m) }),
        recommended = SpeechLyrics.Recommended().Id,
        ready = ReadyModel != null,
        model = S.LyricsModel,
        gpus = (_gpus ??= SpeechLyrics.Gpus()).Select(g => new { index = g.Index, name = g.Name, kind = g.Kind, memoryMb = g.MemoryMb }),
        gpu = S.LyricsGpu,
        listen = S.LyricsListen,
        ranOn = SpeechLyrics.RanOn,
        downloading = _speechDownload != null,
        writingAll = _speechAll != null,
        allStatus = _speechAllStatus,
        listeningTo = _listeningTo,
    };

    static SpeechLyrics.Model? ReadyModel => SpeechLyrics.Find(S.LyricsModel) is { } m && SpeechLyrics.Have(m) ? m : null;

    static void RegisterSpeech()
    {
        SpeechLyrics.Decode = (source, cancel) => Program.Engine.DecodeForSpeech(source, cancel);
        SpeechLyrics.GpuChoice = S.LyricsGpu;

        Rpc.On("speech.state", _ => SpeechState());
        Rpc.On("speech.set", p =>
        {
            if (p.Has("model") && p.Str("model") == "") S.LyricsModel = "";
            else if (p.Has("model") && SpeechLyrics.Find(p.Str("model")) is { } m && SpeechLyrics.Have(m)) S.LyricsModel = m.Id;
            if (p.Has("gpu")) { S.LyricsGpu = SpeechLyrics.GpuChoice = (int)p.Long("gpu"); SpeechLyrics.Unload(); }
            if (p.Has("listen")) S.LyricsListen = p.Bool("listen");
            Save();
            Rpc.Emit("speech", new { state = SpeechState(), error = (string?)null });
            return SpeechState();
        });
        Rpc.On("speech.download", downloadParams =>
        {
            if (_speechDownload != null) { _speechDownload.Cancel(); return SpeechState(); }
            var m = SpeechLyrics.Find(downloadParams.Str("id")) ?? throw new InvalidOperationException("No such model.");
            _speechDownload = new CancellationTokenSource();
            var cancel = _speechDownload.Token;
            _ = Task.Run(async () =>
            {
                string? error = null;
                try { await SpeechLyrics.DownloadAsync(m, f => Rpc.Emit("speech.progress", new { id = m.Id, done = f }), cancel); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { error = ex.Message; }
                MainLoop.Post(() =>
                {
                    _speechDownload = null;
                    if (error == null && SpeechLyrics.Have(m)) { S.LyricsModel = m.Id; Save(); }
                    Rpc.Emit("speech", new { state = SpeechState(), error });
                });
            });
            return SpeechState();
        });
        Rpc.On("speech.delete", p =>
        {
            if (SpeechLyrics.Find(p.Str("id")) is { } m)
            {
                SpeechLyrics.Delete(m);
                if (S.LyricsModel == m.Id) { S.LyricsModel = ""; Save(); }
            }
            Rpc.Emit("speech", new { state = SpeechState(), error = (string?)null });
            return SpeechState();
        });

        Rpc.On("lyrics.generated", p => TrackOf(p.Long("id")) is { } t ? LyricsShape(Lyrics.Generated(t)) : (object?)null);
        Rpc.On("lyrics.removeGenerated", p =>
        {
            if (TrackOf(p.Long("id")) is { } t) Lyrics.RemoveGenerated(t);
            return (object?)null;
        });

        // the speech model listens to a song; each line is sent as it is written ("speech.line"), the lyrics at the end
        Rpc.On("lyrics.listen", async p =>
        {
            if (TrackOf(p.Long("id")) is not { } t) return null;
            if (_listening != null && _listeningTo == t.Id) { _listening.Cancel(); return null; }
            var m = ReadyModel ?? throw new InvalidOperationException("Download a speech model in settings → translation first.");
            _listening?.Cancel();
            var cancel = new CancellationTokenSource();
            _listening = cancel;
            _listeningTo = t.Id;
            try
            {
                var source = t.IsLocal ? t.Location : Player.From(t).Url;
                var lrc = await SpeechLyrics.TranscribeAsync(source, m, (ms, text) => MainLoop.Post(() => Rpc.Emit("speech.line", new { id = t.Id, ms, text })), cancel.Token, t.Title, t.Artist);
                if (lrc == null) throw new InvalidOperationException("The speech model heard no words in this song.");
                Lyrics.SaveGenerated(t, lrc);
                return LyricsShape(Lyrics.Generated(t));
            }
            catch (OperationCanceledException) { return LyricsShape(Lyrics.Generated(t)); }
            finally { if (_listening == cancel) { _listening = null; _listeningTo = -1; } }
        });

        // every song in the collection that has no lyrics, one at a time
        Rpc.On("speech.writeAll", allParams =>
        {
            if (_speechAll != null) { _speechAll.Cancel(); return SpeechState(); }
            var m = ReadyModel ?? throw new InvalidOperationException("Download a speech model first.");
            _speechAll = new CancellationTokenSource();
            var cancel = _speechAll.Token;
            _ = Task.Run(async () =>
            {
                var songs = Lib.Tracks.Where(t => t.IsLocal && Lyrics.For(t) == null && !Lyrics.HasGenerated(t)).ToList();
                int done = 0, wrote = 0;
                void Tell(string s) => MainLoop.Post(() => { _speechAllStatus = s; Rpc.Emit("speech", new { state = SpeechState(), error = (string?)null }); });
                try
                {
                    foreach (var t in songs)
                    {
                        Tell($"listening to {done + 1} of {songs.Count}: {t.Title}   ({wrote} written so far)");
                        try { if (await SpeechLyrics.TranscribeAsync(t.Location, m, null, cancel, t.Title, t.Artist) is { } lrc) { Lyrics.SaveGenerated(t, lrc); wrote++; } }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                        done++;
                    }
                    MainLoop.Post(() => _speechAll = null);
                    Tell(songs.Count == 0 ? "every song has lyrics already" : $"wrote lyrics for {wrote} of {songs.Count} songs (the rest had no words the model could hear) on {SpeechLyrics.RanOn}");
                }
                catch (OperationCanceledException) { MainLoop.Post(() => _speechAll = null); Tell($"stopped; wrote lyrics for {wrote} songs"); }
            });
            return SpeechState();
        });

        // a small picture for the self-test (a background picture of your own)
        Rpc.On("smoke.writePicture", p =>
        {
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGP4z8DAwMDAxMDAwMDAAAANBAEB5vUvXQAAAABJRU5ErkJggg==");
            File.WriteAllBytes(p.Str("file"), png);
            return (object?)null;
        });
        Rpc.On("smoke.saveGenerated", p =>
        {
            if (TrackOf(p.Long("id")) is { } t) Lyrics.SaveGenerated(t, p.Str("text"));
            return (object?)null;
        });

        // the self-test: listen to a file
        // (a recording of speech is given in PHOENIX_SPEECH_SAMPLE, the tiny model in PHOENIX_MODELS)
        Rpc.On("smoke.listenFile", async p =>
        {
            var m = SpeechLyrics.Find(p.Str("model")) ?? throw new InvalidOperationException("No such model.");
            var file = Environment.GetEnvironmentVariable("PHOENIX_SPEECH_SAMPLE") ?? "";
            if (!SpeechLyrics.Have(m) || !File.Exists(file)) return new { lrc = (string?)null, lines = new List<string>(), ranOn = "", skipped = true };
            var lines = new List<string>();
            var lrc = await SpeechLyrics.TranscribeAsync(file, m, (_, text) => { lock (lines) lines.Add(text); }, CancellationToken.None);
            return new { lrc, lines, ranOn = SpeechLyrics.RanOn, skipped = false };
        });
    }
}
