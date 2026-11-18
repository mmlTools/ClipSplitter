using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClipSplitterApp.Services;

namespace ClipSplitterApp.Playback;

/// <summary>
/// Runs one ffmpeg that turns a clip into raw BGRA frames and reads them on a background thread.
/// Only a few frames are buffered: when the queue is full the pipe backs up and ffmpeg waits, so
/// decoding never runs ahead of playback.
/// </summary>
internal sealed class LayerDecoder : IDisposable
{
    private const int InitialBuffers = 3;
    private const int MaxBuffers = 16;
    private const long MaxBufferBytes = 64L * 1024 * 1024;

    // Above 1080p60, software decoding stops keeping up on ordinary laptops.
    private const double HeavyPixelsPerSecond = 1920d * 1080d * 60d * 1.05;

    private static readonly TimeSpan PresentTolerance = TimeSpan.FromMilliseconds(4);

    private readonly Process _process;
    private readonly Thread _thread;
    private readonly BlockingCollection<byte[]> _free = new(new ConcurrentQueue<byte[]>());
    private readonly ConcurrentQueue<DecodedFrame> _ready = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly int _frameBytes;
    private readonly double _fps;
    private volatile bool _completed;
    private string? _lastError;
    private long _lastTakenIndex = -1;
    private int _bufferCount = InitialBuffers;
    private int _disposed;

    public LayerDecoder(PlaybackLayer layer, TimeSpan offset, TimeSpan length, int width, int height, double fps, Action<string> log)
    {
        _frameBytes = width * height * 4;
        _fps = fps;

        for (var index = 0; index < InitialBuffers; index++)
            _free.Add(ArrayPool<byte>.Shared.Rent(_frameBytes));

        // start_time=0 repeats the first frame if the video track begins late, keeping frame
        // numbers in step with the clock.
        var rate = FfmpegTools.ArgNumber(fps);
        var filter = $"fps={rate}:start_time=0,scale={width}:{height}:flags=fast_bilinear,setsar=1";

        // Slow motion and speed-up stretch or squeeze the time between frames before they are counted.
        if (layer.Speed != 1)
            filter = $"setpts=PTS/{FfmpegTools.ArgNumber(layer.Speed)}," + filter;

        // Looks and effects are applied to the small preview frame, where they cost next to
        // nothing, with the same filters the exporter uses on the full-size picture.
        if (!string.IsNullOrEmpty(layer.VideoFilter))
            filter += "," + EffectFilter.Resolve(layer.VideoFilter, width, height, rate, string.Empty);

        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };

        // Hardware decoding starts slower and is no faster for ordinary video, so it is kept for
        // the heavy sources that need it. ffmpeg falls back to software when it is unavailable.
        if ((double)layer.Info.Width * layer.Info.Height * layer.Info.FrameRate > HeavyPixelsPerSecond)
            args.AddRange(new[] { "-hwaccel", "auto" });

        args.AddRange(new[]
        {
            "-ss", FfmpegTools.ArgTime(layer.SourceTime(offset)),
            "-t", FfmpegTools.ArgTime(length * layer.Speed),
            "-an", "-sn", "-dn",
            "-i", layer.File,
            "-map", "0:V:0",
            "-vf", filter,
            "-pix_fmt", "bgra",
            "-f", "rawvideo",
            "pipe:1"
        });

        _process = FfmpegTools.Start("ffmpeg", args);
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _lastError = e.Data;
        };
        _process.BeginErrorReadLine();

        _thread = new Thread(() => ReadFrames(log)) { IsBackground = true, Name = "ClipSplitter video decoder" };
        _thread.Start();
    }

    public bool HasReadyFrame => !_ready.IsEmpty;

    /// <summary>True once ffmpeg has stopped producing frames.</summary>
    public bool IsCompleted => _completed;

    /// <summary>True once every decoded frame has been shown.</summary>
    public bool IsDrained => _completed && _ready.IsEmpty;

    /// <summary>True once the queue is full again after running dry.</summary>
    public bool IsRefilled => _completed || _ready.Count >= _bufferCount - 1;

    /// <summary>
    /// True when a frame is overdue and none has been decoded yet: the video is too heavy to
    /// decode in real time.
    /// </summary>
    public bool IsStarved(TimeSpan position)
        => !_completed && _ready.IsEmpty && position.TotalSeconds * _fps > _lastTakenIndex + 2.5;

    /// <summary>
    /// Lets the decoder work further ahead. Called once playback has stalled, so the next stretch
    /// plays smoothly for longer before it has to wait again.
    /// </summary>
    public void GrowBuffers()
    {
        var limit = (int)Math.Clamp(MaxBufferBytes / _frameBytes, InitialBuffers + 1, MaxBuffers);
        while (_bufferCount < limit)
        {
            _free.Add(ArrayPool<byte>.Shared.Rent(_frameBytes));
            _bufferCount++;
        }
    }

    /// <summary>
    /// Hands over the newest frame that is due at <paramref name="position"/>, skipping any that are
    /// already late. The caller must pass the buffer back to <see cref="Return"/>.
    /// </summary>
    public bool TryTakeFrame(TimeSpan position, out byte[] buffer)
    {
        buffer = Array.Empty<byte>();
        var found = false;
        var due = position + PresentTolerance;

        while (_ready.TryPeek(out var frame) && TimeSpan.FromSeconds(frame.Index / _fps) <= due)
        {
            if (!_ready.TryDequeue(out frame))
                break;

            if (found)
                _free.Add(buffer);

            buffer = frame.Buffer;
            _lastTakenIndex = frame.Index;
            found = true;
        }

        return found;
    }

    public void Return(byte[] buffer) => _free.Add(buffer);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cancel.Cancel();
        FfmpegTools.TryKill(_process);

        // Buffers go back to the pool only after the reader has stopped writing into them.
        Task.Run(() =>
        {
            if (_thread.Join(TimeSpan.FromSeconds(5)))
            {
                while (_free.TryTake(out var buffer))
                    ArrayPool<byte>.Shared.Return(buffer);
                while (_ready.TryDequeue(out var frame))
                    ArrayPool<byte>.Shared.Return(frame.Buffer);
            }

            _process.Dispose();
            _cancel.Dispose();
        });
    }

    private void ReadFrames(Action<string> log)
    {
        try
        {
            var stream = _process.StandardOutput.BaseStream;
            long index = 0;

            while (!_cancel.IsCancellationRequested)
            {
                var buffer = _free.Take(_cancel.Token);
                if (!ReadFrame(stream, buffer))
                {
                    _free.Add(buffer);
                    break;
                }

                _ready.Enqueue(new DecodedFrame(buffer, index++));
            }

            if (index == 0 && !_cancel.IsCancellationRequested)
                log("Preview could not decode this clip." + (_lastError == null ? string.Empty : " " + _lastError));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process was killed while a read was in flight.
        }
        finally
        {
            _completed = true;
        }
    }

    private bool ReadFrame(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < _frameBytes)
        {
            var count = stream.Read(buffer, read, _frameBytes - read);
            if (count <= 0)
                return false;

            read += count;
        }

        return true;
    }

    private readonly record struct DecodedFrame(byte[] Buffer, long Index);
}
