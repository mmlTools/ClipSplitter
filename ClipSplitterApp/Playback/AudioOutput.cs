using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using ClipSplitterApp.Services;

namespace ClipSplitterApp.Playback;

/// <summary>Plays 48 kHz stereo 16-bit PCM.</summary>
internal interface IAudioOutput : IDisposable
{
    /// <summary>True when <see cref="Played"/> is accurate enough to pace the video.</summary>
    bool IsClock { get; }

    /// <summary>How much audio has actually come out of the speakers.</summary>
    TimeSpan Played { get; }

    /// <summary>True once enough audio is queued to start without stuttering.</summary>
    bool IsPrimed { get; }

    /// <summary>True while written audio is still waiting to be played.</summary>
    bool HasQueuedAudio { get; }

    /// <summary>Queues audio, blocking while the device is full.</summary>
    void Write(byte[] data, int count, CancellationToken token);

    /// <summary>Queues whatever is left in a partly filled buffer.</summary>
    void Flush();

    void Pause();

    void Resume();

    /// <summary>Silences the output at once. Safe to call from any thread, before <see cref="IDisposable.Dispose"/>.</summary>
    void Stop();
}

internal static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int BytesPerFrame = 4;
    public const int BytesPerSecond = SampleRate * BytesPerFrame;

    private static int _failureLogged;

    /// <summary>Opens the best output for this platform, or returns null when sound is unavailable.</summary>
    public static IAudioOutput? TryOpen(Action<string> log)
    {
        try
        {
            return OperatingSystem.IsWindows() ? new WaveOutAudioOutput() : new FfplayAudioOutput();
        }
        catch (Exception ex)
        {
            // Every seek reopens the output, so the reason is only worth reporting once.
            if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
                log("Preview plays without sound: " + ex.Message);

            return null;
        }
    }
}

/// <summary>
/// Windows audio through the built-in waveOut API. The device reports exactly how many samples it
/// has played, which makes it a reliable master clock for the picture.
/// </summary>
internal sealed class WaveOutAudioOutput : IAudioOutput
{
    private const int BufferBytes = AudioFormat.BytesPerSecond / 20;
    private const int BufferCount = 5;
    private const uint WaveMapper = 0xFFFFFFFF;
    private const int WhdrDone = 0x1;
    private const int WhdrPrepared = 0x2;
    private const uint TimeMs = 0x1;
    private const uint TimeSamples = 0x2;
    private const uint TimeBytes = 0x4;

    private static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));

    private readonly object _gate = new();
    private readonly IntPtr[] _headers = new IntPtr[BufferCount];
    private readonly IntPtr[] _blocks = new IntPtr[BufferCount];
    private readonly bool[] _submitted = new bool[BufferCount];
    private IntPtr _handle;
    private int _current = -1;
    private int _fill;
    private long _submittedBytes;
    private bool _stopped;

    public WaveOutAudioOutput()
    {
        var format = new WaveFormat
        {
            FormatTag = 1,
            Channels = AudioFormat.Channels,
            SamplesPerSec = AudioFormat.SampleRate,
            AvgBytesPerSec = AudioFormat.BytesPerSecond,
            BlockAlign = AudioFormat.BytesPerFrame,
            BitsPerSample = 16,
            Size = 0
        };

        var result = waveOutOpen(out _handle, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0)
            throw new InvalidOperationException($"no audio device could be opened (code {result}).");

        // Start paused so audio can be queued up and released at the same moment as the first frame.
        waveOutPause(_handle);

        for (var index = 0; index < BufferCount; index++)
        {
            _blocks[index] = Marshal.AllocHGlobal(BufferBytes);
            _headers[index] = Marshal.AllocHGlobal(HeaderSize);

            var header = new WaveHeader { Data = _blocks[index], BufferLength = BufferBytes };
            Marshal.StructureToPtr(header, _headers[index], false);
            waveOutPrepareHeader(_handle, _headers[index], HeaderSize);
        }
    }

    public bool IsClock => true;

    public bool IsPrimed => Interlocked.Read(ref _submittedBytes) >= BufferBytes * 2;

    public TimeSpan Played
    {
        get
        {
            lock (_gate)
            {
                if (_handle == IntPtr.Zero || _stopped)
                    return TimeSpan.Zero;

                var time = new MmTime { Type = TimeSamples };
                if (waveOutGetPosition(_handle, ref time, Marshal.SizeOf<MmTime>()) != 0)
                    return TimeSpan.Zero;

                return time.Type switch
                {
                    TimeSamples => TimeSpan.FromSeconds(time.Value / (double)AudioFormat.SampleRate),
                    TimeBytes => TimeSpan.FromSeconds(time.Value / (double)AudioFormat.BytesPerSecond),
                    TimeMs => TimeSpan.FromMilliseconds(time.Value),
                    _ => TimeSpan.Zero
                };
            }
        }
    }

    public bool HasQueuedAudio
    {
        get
        {
            lock (_gate)
            {
                if (_handle == IntPtr.Zero || _stopped)
                    return false;

                for (var index = 0; index < BufferCount; index++)
                {
                    if (_submitted[index] && (Marshal.ReadInt32(_headers[index], FlagsOffset) & WhdrDone) == 0)
                        return true;
                }

                return false;
            }
        }
    }

    public void Write(byte[] data, int count, CancellationToken token)
    {
        var offset = 0;
        while (offset < count)
        {
            if (_current < 0)
            {
                _current = AcquireBuffer(token);
                _fill = 0;
            }

            var chunk = Math.Min(count - offset, BufferBytes - _fill);
            Marshal.Copy(data, offset, _blocks[_current] + _fill, chunk);
            _fill += chunk;
            offset += chunk;

            if (_fill == BufferBytes)
                Submit();
        }
    }

    public void Flush()
    {
        if (_current < 0 || _fill == 0)
            return;

        // Buffers are always submitted whole, so the tail is padded with silence.
        var padding = new byte[BufferBytes - _fill];
        Marshal.Copy(padding, 0, _blocks[_current] + _fill, padding.Length);
        _fill = BufferBytes;
        Submit();
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_handle != IntPtr.Zero && !_stopped)
                waveOutPause(_handle);
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_handle != IntPtr.Zero && !_stopped)
                waveOutRestart(_handle);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero || _stopped)
                return;

            _stopped = true;
            waveOutReset(_handle);
        }
    }

    /// <summary>Frees the device. The writing thread must have finished before this is called.</summary>
    public void Dispose()
    {
        IntPtr handle;
        lock (_gate)
        {
            handle = _handle;
            _handle = IntPtr.Zero;
            _stopped = true;
        }

        if (handle == IntPtr.Zero)
            return;

        waveOutReset(handle);
        for (var index = 0; index < BufferCount; index++)
        {
            waveOutUnprepareHeader(handle, _headers[index], HeaderSize);
            Marshal.FreeHGlobal(_headers[index]);
            Marshal.FreeHGlobal(_blocks[index]);
        }

        waveOutClose(handle);
    }

    private int AcquireBuffer(CancellationToken token)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_handle == IntPtr.Zero || _stopped)
                    throw new OperationCanceledException();

                for (var index = 0; index < BufferCount; index++)
                {
                    if (!_submitted[index])
                        return index;

                    if ((Marshal.ReadInt32(_headers[index], FlagsOffset) & WhdrDone) != 0)
                    {
                        _submitted[index] = false;
                        return index;
                    }
                }
            }

            token.ThrowIfCancellationRequested();
            Thread.Sleep(4);
        }
    }

    private void Submit()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero || _stopped)
                throw new OperationCanceledException();

            Marshal.WriteInt32(_headers[_current], FlagsOffset, WhdrPrepared);
            if (waveOutWrite(_handle, _headers[_current], HeaderSize) != 0)
                throw new InvalidOperationException("The audio device rejected a buffer.");

            _submitted[_current] = true;
        }

        Interlocked.Add(ref _submittedBytes, BufferBytes);
        _current = -1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    // MMTIME is a 4-byte type followed by an 8-byte union; only the first union member is read.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct MmTime
    {
        public uint Type;
        public uint Value;
        public uint Padding;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr handle, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr handle, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutPause(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern int waveOutRestart(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern int waveOutGetPosition(IntPtr handle, ref MmTime time, int size);
}

/// <summary>
/// Fallback for Linux and macOS: pipes the audio into a hidden ffplay. It cannot report its
/// position, so the player paces the picture with a stopwatch instead.
/// </summary>
internal sealed class FfplayAudioOutput : IAudioOutput
{
    private readonly Process _process;
    private readonly Stream _input;
    private int _stopped;

    public FfplayAudioOutput()
    {
        _process = FfmpegTools.Start(
            "ffplay",
            new[] { "-hide_banner", "-loglevel", "quiet", "-nodisp", "-autoexit", "-fflags", "nobuffer", "-probesize", "4096", "-analyzeduration", "0", "-i", "pipe:0" },
            redirectInput: true);

        _process.OutputDataReceived += static (_, _) => { };
        _process.ErrorDataReceived += static (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _input = _process.StandardInput.BaseStream;
        WriteWaveHeader(_input);
    }

    public bool IsClock => false;

    public TimeSpan Played => TimeSpan.Zero;

    public bool IsPrimed => true;

    public bool HasQueuedAudio => false;

    public void Write(byte[] data, int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _stopped) != 0)
            throw new OperationCanceledException();

        _input.Write(data, 0, count);
    }

    public void Flush() => _input.Flush();

    public void Pause()
    {
    }

    public void Resume()
    {
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
            FfmpegTools.TryKill(_process);
    }

    public void Dispose()
    {
        Stop();
        _process.Dispose();
    }

    /// <summary>A WAV header of unknown length, so ffplay needs no format arguments.</summary>
    private static void WriteWaveHeader(Stream stream)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(uint.MaxValue);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)AudioFormat.Channels);
        writer.Write(AudioFormat.SampleRate);
        writer.Write(AudioFormat.BytesPerSecond);
        writer.Write((short)AudioFormat.BytesPerFrame);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(uint.MaxValue);
        writer.Flush();
    }
}
