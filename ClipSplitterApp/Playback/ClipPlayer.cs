using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Threading;
using ClipSplitterApp.Services;

namespace ClipSplitterApp.Playback;

/// <summary>
/// The in-app player. It decodes one or two clips with ffmpeg, keeps them in step with the sound,
/// and exposes each clip's current frame as a bitmap for the preview canvas to draw.
/// All members must be used from the UI thread.
/// </summary>
public sealed class ClipPlayer : IDisposable
{
    private const int PreviewShortSide = 480;
    private const int PreviewLongSide = 1280;
    private const double MaxPreviewFps = 30;

    private static readonly TimeSpan SeekInterval = TimeSpan.FromMilliseconds(110);
    private static readonly TimeSpan PrimeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EndGuard = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan NotifyInterval = TimeSpan.FromMilliseconds(66);

    private readonly Action<string> _log;
    private readonly Action<Action> _requestFrame;
    private LayerSurface?[] _surfaces = new LayerSurface?[2];
    private PlaybackRequest? _request;
    private Session? _session;
    private TimeSpan _position;
    private TimeSpan? _pendingSeek;
    private double _volume = 1;
    private long _lastSessionStart;
    private long _lastNotify;
    private bool _playing;
    private bool _ended;
    private bool _resumeNeedsRestart;
    private bool _tickScheduled;
    private bool _seekScheduled;
    private bool _heavyNoticeShown;
    private bool _disposed;

    /// <param name="log">Receives messages worth showing to the user.</param>
    /// <param name="requestFrame">Schedules a callback for the next display refresh.</param>
    public ClipPlayer(Action<string> log, Action<Action> requestFrame)
    {
        _log = log;
        _requestFrame = requestFrame;
    }

    /// <summary>A layer has a new frame to draw.</summary>
    public event Action? FrameUpdated;

    /// <summary>Position, duration or the playing state changed.</summary>
    public event Action? StateChanged;

    /// <summary>Playback could not start, for example because ffmpeg is missing.</summary>
    public event Action<Exception>? Failed;

    public bool HasMedia => _request != null;

    public bool IsPlaying => _playing;

    public TimeSpan Duration => _request?.Duration ?? TimeSpan.Zero;

    /// <summary>Signature of the loaded request, or null when nothing is loaded.</summary>
    public string? Signature => _request?.Signature;

    public TimeSpan Position
    {
        get
        {
            if (_request == null)
                return TimeSpan.Zero;

            if (_pendingSeek is { } pending)
                return pending;

            if (_playing && _session is { Primed: true } session)
                return Clamp(session.Offset + session.Clock.Position);

            return _position;
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            if (_session?.Audio is { } audio)
                audio.Volume = _volume;
        }
    }

    /// <summary>The surface holding the current frame of a layer, or null if that layer has none yet.</summary>
    public LayerSurface? GetSurface(int layer)
        => layer >= 0 && layer < _surfaces.Length && _surfaces[layer] is { HasFrame: true } surface ? surface : null;

    public void Load(PlaybackRequest request, bool autoPlay, TimeSpan startAt = default)
    {
        if (_disposed)
            return;

        _request = request;
        if (request.Layers.Count > _surfaces.Length)
            Array.Resize(ref _surfaces, request.Layers.Count);

        // Keep showing the last frame of layers whose clip did not change; blank the others.
        for (var index = 0; index < _surfaces.Length; index++)
        {
            if (_surfaces[index] is not { } surface)
                continue;

            var layer = index < request.Layers.Count ? request.Layers[index] : null;
            if (layer == null || surface.SourceKey != PlaybackRequest.LayerKey(layer))
                surface.HasFrame = false;
        }

        _playing = autoPlay;
        StartSession(startAt);
        FrameUpdated?.Invoke();
    }

    public void Unload()
    {
        if (_request == null)
            return;

        DisposeSession();
        _request = null;
        _playing = false;
        _ended = false;
        _pendingSeek = null;
        _position = TimeSpan.Zero;

        foreach (var surface in _surfaces)
        {
            if (surface != null)
                surface.HasFrame = false;
        }

        FrameUpdated?.Invoke();
        StateChanged?.Invoke();
    }

    public void Play()
    {
        if (_disposed || _request == null || _playing)
            return;

        _playing = true;

        if (_ended)
        {
            StartSession(TimeSpan.Zero);
            return;
        }

        if (_pendingSeek != null)
        {
            // The seek that is about to be applied starts playback itself.
            StateChanged?.Invoke();
            return;
        }

        if (_session == null || _resumeNeedsRestart)
        {
            StartSession(_position);
            return;
        }

        // While buffering, the next refresh restarts the clock once the decoder has caught up.
        if (_session.Primed && !_session.IsBuffering)
            _session.StartClock();

        EnsureTicking();
        StateChanged?.Invoke();
    }

    public void Pause()
    {
        if (!_playing)
            return;

        _position = Position;
        _playing = false;

        if (_session is { } session)
        {
            session.Clock.Pause();

            // An output that cannot be paused is stopped instead, and playback restarts from here.
            if (session.Audio is { Output.IsClock: false } audio)
            {
                audio.Dispose();
                _resumeNeedsRestart = true;
            }
        }

        StateChanged?.Invoke();
    }

    public void TogglePlay()
    {
        if (_playing)
            Pause();
        else
            Play();
    }

    public void Seek(TimeSpan position)
    {
        if (_disposed || _request == null)
            return;

        position = Clamp(position);
        _pendingSeek = position;
        _position = position;
        _ended = false;
        StateChanged?.Invoke();

        if (_seekScheduled)
            return;

        // Restarting ffmpeg for every pointer move would thrash, so seeks are spaced out.
        var wait = SeekInterval - Stopwatch.GetElapsedTime(_lastSessionStart);
        if (wait <= TimeSpan.Zero)
        {
            ApplySeek();
            return;
        }

        _seekScheduled = true;
        DispatcherTimer.RunOnce(
            () =>
            {
                _seekScheduled = false;
                ApplySeek();
            },
            wait);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisposeSession();

        for (var index = 0; index < _surfaces.Length; index++)
        {
            _surfaces[index]?.Dispose();
            _surfaces[index] = null;
        }
    }

    private void ApplySeek()
    {
        if (_disposed || _request == null || _pendingSeek is not { } target)
            return;

        StartSession(target);
    }

    private void StartSession(TimeSpan offset)
    {
        DisposeSession();
        _pendingSeek = null;
        _ended = false;
        _resumeNeedsRestart = false;

        var request = _request;
        if (request == null || request.Duration <= TimeSpan.Zero)
        {
            _playing = false;
            _position = TimeSpan.Zero;
            StateChanged?.Invoke();
            return;
        }

        // Stop just short of the end so there is always a frame to show.
        var lastStart = request.Duration > EndGuard ? request.Duration - EndGuard : TimeSpan.Zero;
        if (offset > lastStart)
            offset = lastStart;
        if (offset < TimeSpan.Zero)
            offset = TimeSpan.Zero;

        _position = offset;
        _lastSessionStart = Stopwatch.GetTimestamp();

        try
        {
            _session = new Session(request, offset, _volume, EnsureSurface, _log);
        }
        catch (Exception ex)
        {
            _session = null;
            _playing = false;
            StateChanged?.Invoke();
            Failed?.Invoke(ex);
            return;
        }

        EnsureTicking();
        StateChanged?.Invoke();
    }

    private void DisposeSession()
    {
        _session?.Dispose();
        _session = null;
    }

    private LayerSurface EnsureSurface(int index, PlaybackLayer layer)
    {
        var (width, height) = GetPreviewSize(layer.Info);
        var key = PlaybackRequest.LayerKey(layer);
        var surface = _surfaces[index];

        if (surface == null || surface.Width != width || surface.Height != height)
        {
            surface?.Dispose();
            surface = new LayerSurface(width, height);
            _surfaces[index] = surface;
        }

        surface.SourceKey = key;
        return surface;
    }

    private void EnsureTicking()
    {
        if (_tickScheduled || _disposed)
            return;

        _tickScheduled = true;
        _requestFrame(OnFrame);
    }

    private void OnFrame()
    {
        _tickScheduled = false;
        if (_disposed)
            return;

        if (Tick())
            EnsureTicking();
    }

    /// <summary>Advances playback by one display refresh. Returns false when no more refreshes are needed.</summary>
    private bool Tick()
    {
        var session = _session;
        var request = _request;
        if (session == null || request == null)
            return false;

        if (!session.Primed)
        {
            // Wait until every clip has its first frame and the sound is queued, so they start together.
            if (!session.IsPrimeReady && Stopwatch.GetElapsedTime(session.StartedAt) < PrimeTimeout)
                return true;

            session.Primed = true;
            if (session.Present(TimeSpan.Zero, _surfaces))
                FrameUpdated?.Invoke();

            if (_playing)
                session.StartClock();

            StateChanged?.Invoke();
            return _playing;
        }

        if (!_playing)
            return false;

        if (session.IsBuffering)
        {
            // The clock is held until the decoder has caught up, so sound and picture resume together.
            if (!session.IsRefilled)
                return true;

            session.IsBuffering = false;
            session.StartClock();
        }

        var elapsed = session.Clock.Position;
        if (session.Present(elapsed, _surfaces))
            FrameUpdated?.Invoke();

        var position = session.Offset + elapsed;
        if (position >= request.Duration || session.IsFinished)
        {
            _position = request.Duration;
            _playing = false;
            _ended = true;
            DisposeSession();
            StateChanged?.Invoke();
            return false;
        }

        if (session.IsStarved(elapsed))
        {
            session.HoldForBuffering();
            if (!_heavyNoticeShown)
            {
                _heavyNoticeShown = true;
                _log("This video is too heavy to preview in real time on this computer, so playback pauses briefly to keep sound and picture together. Exports are not affected.");
            }
        }

        _position = position;
        if (Stopwatch.GetElapsedTime(_lastNotify) >= NotifyInterval)
        {
            _lastNotify = Stopwatch.GetTimestamp();
            StateChanged?.Invoke();
        }

        return true;
    }

    private TimeSpan Clamp(TimeSpan position)
    {
        var duration = Duration;
        if (position < TimeSpan.Zero)
            return TimeSpan.Zero;

        return position > duration ? duration : position;
    }

    private static (int Width, int Height) GetPreviewSize(MediaInfo info)
    {
        var shortSide = Math.Min(info.Width, info.Height);
        var longSide = Math.Max(info.Width, info.Height);
        var scale = Math.Min(1d, Math.Min((double)PreviewShortSide / shortSide, (double)PreviewLongSide / longSide));

        return (Math.Max(2, (int)Math.Round(info.Width * scale)), Math.Max(2, (int)Math.Round(info.Height * scale)));
    }

    private static double GetPreviewFps(MediaInfo info) => Math.Clamp(info.FrameRate, 1, MaxPreviewFps);

    /// <summary>One run of the decoders from a given offset. Seeking replaces the session.</summary>
    private sealed class Session : IDisposable
    {
        private readonly LayerDecoder?[] _decoders;

        public Session(PlaybackRequest request, TimeSpan offset, double volume, Func<int, PlaybackLayer, LayerSurface> surfaceFor, Action<string> log)
        {
            Offset = offset;
            StartedAt = Stopwatch.GetTimestamp();
            _decoders = new LayerDecoder?[request.Layers.Count];

            var remaining = request.Duration - offset;
            IAudioOutput? output = null;

            try
            {
                for (var index = 0; index < request.Layers.Count; index++)
                {
                    if (request.Layers[index] is not { } layer)
                        continue;

                    var length = layer.Length - offset < remaining ? layer.Length - offset : remaining;
                    if (length <= TimeSpan.Zero)
                        continue;

                    var surface = surfaceFor(index, layer);
                    _decoders[index] = new LayerDecoder(layer, offset, length, surface.Width, surface.Height, GetPreviewFps(layer.Info), log);
                }

                var audioSources = new List<PlaybackLayer>(2);
                foreach (var index in request.AudioLayers)
                {
                    if (index >= 0 && index < request.Layers.Count
                        && request.Layers[index] is { Info.HasAudio: true } layer
                        && layer.Length > offset)
                        audioSources.Add(layer);
                }

                if (audioSources.Count > 0)
                    output = AudioFormat.TryOpen(log);

                // The clock exists before the pump starts, because the pump reports back to it.
                Clock = new PlaybackClock(output is { IsClock: true } ? output : null);

                if (output != null)
                {
                    Audio = new AudioPump(audioSources, offset, remaining, output, volume, Clock.DetachAudio);
                    output = null;
                }
            }
            catch
            {
                if (output != null)
                {
                    output.Stop();
                    output.Dispose();
                }

                Dispose();
                throw;
            }
        }

        public TimeSpan Offset { get; }

        public long StartedAt { get; }

        public PlaybackClock Clock { get; } = new(null);

        public AudioPump? Audio { get; }

        public bool Primed { get; set; }

        public bool IsPrimeReady
        {
            get
            {
                foreach (var decoder in _decoders)
                {
                    if (decoder is { HasReadyFrame: false, IsCompleted: false })
                        return false;
                }

                return Audio == null || Audio.IsReady;
            }
        }

        /// <summary>True when every frame has been shown and all the sound has been played.</summary>
        public bool IsFinished
        {
            get
            {
                foreach (var decoder in _decoders)
                {
                    if (decoder is { IsDrained: false })
                        return false;
                }

                return Audio == null || Audio.IsFinished;
            }
        }

        /// <summary>True while playback waits for a decoder that fell behind.</summary>
        public bool IsBuffering { get; set; }

        /// <summary>True once every decoder that ran dry has frames queued again.</summary>
        public bool IsRefilled
        {
            get
            {
                foreach (var decoder in _decoders)
                {
                    if (decoder is { IsRefilled: false })
                        return false;
                }

                return true;
            }
        }

        /// <summary>True when a frame is overdue because a clip cannot be decoded fast enough.</summary>
        public bool IsStarved(TimeSpan position)
        {
            foreach (var decoder in _decoders)
            {
                if (decoder != null && decoder.IsStarved(position))
                    return true;
            }

            return false;
        }

        /// <summary>Stops the clock, and with it the sound, until the decoders have caught up.</summary>
        public void HoldForBuffering()
        {
            IsBuffering = true;
            Clock.Pause();

            // An output that cannot be paused would run ahead of the picture, so it is silenced instead.
            if (Audio is { Output.IsClock: false })
                Audio.Dispose();

            foreach (var decoder in _decoders)
                decoder?.GrowBuffers();
        }

        public void StartClock()
        {
            Clock.Start();
            Audio?.NotifyClockStarted();
        }

        /// <summary>Draws the frames that are due at <paramref name="position"/>. Returns true if anything changed.</summary>
        public bool Present(TimeSpan position, LayerSurface?[] surfaces)
        {
            var changed = false;
            for (var index = 0; index < _decoders.Length; index++)
            {
                if (_decoders[index] is not { } decoder || !decoder.TryTakeFrame(position, out var buffer))
                    continue;

                surfaces[index]?.Write(buffer);
                decoder.Return(buffer);
                changed = true;
            }

            return changed;
        }

        public void Dispose()
        {
            Audio?.Dispose();
            foreach (var decoder in _decoders)
                decoder?.Dispose();
        }
    }
}
