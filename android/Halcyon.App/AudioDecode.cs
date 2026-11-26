using Android.Media;

namespace Halcyon.Droid;

/// <summary>A song decoded to mono samples with the phone's own decoders (for the auto equalizer's measurement).</summary>
public static class AudioDecode
{
    public static (float[] Samples, int Rate) Mono(string file, CancellationToken cancel)
    {
        using var extractor = new MediaExtractor();
        extractor.SetDataSource(file);
        var track = -1;
        MediaFormat? format = null;
        for (var i = 0; i < extractor.TrackCount; i++)
        {
            var f = extractor.GetTrackFormat(i);
            if (f.GetString(MediaFormat.KeyMime)?.StartsWith("audio/") == true) { track = i; format = f; break; }
        }
        if (track < 0 || format == null) throw new InvalidOperationException("no sound in this file");
        extractor.SelectTrack(track);
        var rate = format.GetInteger(MediaFormat.KeySampleRate);
        var channels = format.GetInteger(MediaFormat.KeyChannelCount);
        using var codec = MediaCodec.CreateDecoderByType(format.GetString(MediaFormat.KeyMime)!);
        codec.Configure(format, null, null, MediaCodecConfigFlags.None);
        codec.Start();
        var samples = new List<float>(rate * 60 * 4);
        var info = new MediaCodec.BufferInfo();
        bool inputDone = false, outputDone = false;
        try
        {
            while (!outputDone)
            {
                cancel.ThrowIfCancellationRequested();
                if (!inputDone)
                {
                    var inIndex = codec.DequeueInputBuffer(10_000);
                    if (inIndex >= 0)
                    {
                        var buffer = codec.GetInputBuffer(inIndex)!;
                        var size = extractor.ReadSampleData(buffer, 0);
                        if (size < 0) { codec.QueueInputBuffer(inIndex, 0, 0, 0, MediaCodecBufferFlags.EndOfStream); inputDone = true; }
                        else { codec.QueueInputBuffer(inIndex, 0, size, extractor.SampleTime, MediaCodecBufferFlags.None); extractor.Advance(); }
                    }
                }
                var outIndex = codec.DequeueOutputBuffer(info, 10_000);
                if (outIndex == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    var of = codec.OutputFormat;
                    rate = of.GetInteger(MediaFormat.KeySampleRate);
                    channels = of.GetInteger(MediaFormat.KeyChannelCount);
                }
                else if (outIndex >= 0)
                {
                    if (info.Size > 0)
                    {
                        var output = codec.GetOutputBuffer(outIndex)!;
                        output.Position(info.Offset);
                        var bytes = new byte[info.Size];
                        output.Get(bytes);
                        // 16-bit PCM, channels interleaved: mixed down to one
                        var frames = bytes.Length / 2 / Math.Max(1, channels);
                        for (var i = 0; i < frames; i++)
                        {
                            float sum = 0;
                            for (var c = 0; c < channels; c++) sum += BitConverter.ToInt16(bytes, (i * channels + c) * 2);
                            samples.Add(sum / channels / 32768f);
                        }
                    }
                    codec.ReleaseOutputBuffer(outIndex, false);
                    if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) outputDone = true;
                }
            }
        }
        finally { try { codec.Stop(); } catch { } }
        return (samples.ToArray(), rate);
    }
}
