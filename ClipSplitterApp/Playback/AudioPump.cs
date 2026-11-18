using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClipSplitterApp.Services;

namespace ClipSplitterApp.Playback;

/// <summary>
/// Runs one ffmpeg that decodes (and, for two clips, mixes) the sound to PCM, and feeds it to the
/// audio output on a background thread.
/// </summary>
internal sealed class AudioPump : IDisposable
{
    private const int ChunkBytes = AudioFormat.BytesPerSecond / 20;

    private readonly Process _process;
    private readonly IAudioOutput _output;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancel = new();
    private readonly ManualResetEventSlim _startGate = new(false);
    private readonly Action _onDrained;
    private volatile bool _finished;
    private volatile float _volume;
    private int _disposed;

    /// <param name="sources">The clips whose sound is played; two are mixed together.</param>
    /// <param name="onDrained">Called once all sound has been played.</param>
    public AudioPump(IReadOnlyList<PlaybackLayer> sources, TimeSpan offset, TimeSpan length, IAudioOutput output, double volume, Action onDrained)
    {
        _output = output;
        _onDrained = onDrained;
        _volume = (float)volume;

        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
        foreach (var source in sources)
        {
            args.AddRange(new[]
            {
                "-ss", FfmpegTools.ArgTime(source.SourceTime(offset)),
                "-t", FfmpegTools.ArgTime(length * source.Speed),
                "-vn", "-sn", "-dn",
                "-i", source.File
            });
        }

        // first_pts=0 pads a late-starting audio track with silence so it stays aligned with the
        // picture. A clip that plays slower or faster has its sound stretched to match, at its own pitch.
        static string Prepare(PlaybackLayer source)
            => "aresample=48000:async=1:first_pts=0" + (source.Speed == 1 ? string.Empty : $",atempo={FfmpegTools.ArgNumber(source.Speed)}");

        var graph = sources.Count == 1
            ? $"[0:a:0]{Prepare(sources[0])}[aout]"
            : $"[0:a:0]{Prepare(sources[0])}[a0];[1:a:0]{Prepare(sources[1])}[a1];[a0][a1]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0,alimiter=limit=0.97[aout]";

        args.AddRange(new[] { "-filter_complex", graph, "-map", "[aout]", "-ac", "2", "-ar", "48000", "-f", "s16le", "pipe:1" });

        _process = FfmpegTools.Start("ffmpeg", args);
        _process.ErrorDataReceived += static (_, _) => { };
        _process.BeginErrorReadLine();

        _thread = new Thread(Pump) { IsBackground = true, Name = "ClipSplitter audio pump" };
        _thread.Start();
    }

    public IAudioOutput Output => _output;

    /// <summary>True once enough sound is queued for playback to begin.</summary>
    public bool IsReady => _finished || _output.IsPrimed;

    /// <summary>True once all the sound has been decoded and played.</summary>
    public bool IsFinished => _finished;

    public double Volume
    {
        get => _volume;
        set => _volume = (float)Math.Clamp(value, 0, 1);
    }

    /// <summary>Outputs that cannot be queued up while paused only receive audio from this point on.</summary>
    public void NotifyClockStarted() => _startGate.Set();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cancel.Cancel();
        _output.Stop();
        FfmpegTools.TryKill(_process);

        // The device is released only after the pump thread has stopped writing to it.
        Task.Run(() =>
        {
            if (_thread.Join(TimeSpan.FromSeconds(5)))
                _output.Dispose();

            _process.Dispose();
            _cancel.Dispose();
            _startGate.Dispose();
        });
    }

    private void Pump()
    {
        var token = _cancel.Token;
        try
        {
            if (!_output.IsClock)
                _startGate.Wait(token);

            var stream = _process.StandardOutput.BaseStream;
            var chunk = new byte[ChunkBytes];

            while (!token.IsCancellationRequested)
            {
                var count = ReadChunk(stream, chunk);
                if (count <= 0)
                    break;

                ApplyVolume(chunk, count, _volume);
                _output.Write(chunk, count, token);

                if (count < chunk.Length)
                    break;
            }

            _output.Flush();

            while (!token.IsCancellationRequested && _output.HasQueuedAudio)
                Thread.Sleep(10);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process or the device went away mid-write; playback carries on silently.
        }
        finally
        {
            _finished = true;
            if (!token.IsCancellationRequested)
                _onDrained();
        }
    }

    /// <summary>Fills the chunk completely unless the stream ends, so samples are never split.</summary>
    private static int ReadChunk(Stream stream, byte[] chunk)
    {
        var read = 0;
        while (read < chunk.Length)
        {
            var count = stream.Read(chunk, read, chunk.Length - read);
            if (count <= 0)
                break;

            read += count;
        }

        return read - read % AudioFormat.BytesPerFrame;
    }

    private static void ApplyVolume(byte[] chunk, int count, float volume)
    {
        if (volume >= 0.999f)
            return;

        var samples = MemoryMarshal.Cast<byte, short>(chunk.AsSpan(0, count));
        if (volume <= 0.001f)
        {
            samples.Clear();
            return;
        }

        // A squared curve makes the slider feel even to the ear.
        var gain = volume * volume;
        for (var index = 0; index < samples.Length; index++)
            samples[index] = (short)(samples[index] * gain);
    }
}
