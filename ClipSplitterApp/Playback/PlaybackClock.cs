using System;
using System.Diagnostics;

namespace ClipSplitterApp.Playback;

/// <summary>
/// Tells the player how far playback has got. While sound is playing the audio device is the
/// master, so the picture follows what is actually heard; otherwise a stopwatch keeps time.
/// </summary>
internal sealed class PlaybackClock
{
    private readonly object _gate = new();
    private readonly Stopwatch _stopwatch = new();
    private TimeSpan _stopwatchBase;
    private IAudioOutput? _audio;
    private bool _running;

    public PlaybackClock(IAudioOutput? audioMaster) => _audio = audioMaster;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _running;
        }
    }

    /// <summary>Time elapsed since the start of the current session.</summary>
    public TimeSpan Position
    {
        get
        {
            lock (_gate)
                return _audio != null ? _audio.Played : _stopwatchBase + _stopwatch.Elapsed;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running)
                return;

            _running = true;
            if (_audio != null)
                _audio.Resume();
            else
                _stopwatch.Start();
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_running)
                return;

            _running = false;
            if (_audio != null)
                _audio.Pause();
            else
                _stopwatch.Stop();
        }
    }

    /// <summary>
    /// Called when the sound has run out before the picture: carries on from the same position
    /// using the stopwatch.
    /// </summary>
    public void DetachAudio()
    {
        lock (_gate)
        {
            if (_audio == null)
                return;

            _stopwatchBase = _audio.Played;
            _audio = null;
            _stopwatch.Reset();
            if (_running)
                _stopwatch.Start();
        }
    }
}
