using System;
using System.Collections.Generic;
using System.Text;
using ClipSplitterApp.Services;

namespace ClipSplitterApp.Playback;

/// <summary>One video to decode: a stretch of a file, shown in its own layer.</summary>
/// <param name="File">The video file.</param>
/// <param name="Start">Where in the file the stretch begins.</param>
/// <param name="Length">How long the stretch plays for. At a speed other than 1 that differs from how much of the file it covers.</param>
/// <param name="Info">What is known about the file.</param>
/// <param name="VideoFilter">
/// An ffmpeg filter to run on every frame, such as a colour look or an effect, written as a
/// template for <see cref="EffectFilter.Resolve"/>. Null for none.
/// </param>
/// <param name="Speed">How fast the stretch plays: 0.5 is slow motion at half speed, 2 is twice as fast.</param>
public sealed record PlaybackLayer(string File, TimeSpan Start, TimeSpan Length, MediaInfo Info, string? VideoFilter = null, double Speed = 1)
{
    /// <summary>The place in the file that is shown at a moment of playback.</summary>
    public TimeSpan SourceTime(TimeSpan played) => Start + played * Speed;
}

/// <summary>
/// Everything the player plays together. Layers line up with the slots of the preview canvas;
/// a null layer is an empty slot.
/// </summary>
public sealed record PlaybackRequest(IReadOnlyList<PlaybackLayer?> Layers, IReadOnlyList<int> AudioLayers, TimeSpan Duration)
{
    /// <summary>Identifies what is decoded, so an unchanged request does not restart playback.</summary>
    public string Signature
    {
        get
        {
            var builder = new StringBuilder();
            foreach (var layer in Layers)
                builder.Append(layer == null ? "-" : LayerKey(layer)).Append('|').Append(layer?.VideoFilter).Append('|').Append(layer?.Speed).Append(';');

            builder.Append("audio=").Append(string.Join(',', AudioLayers));
            builder.Append(";duration=").Append(Duration.Ticks);
            return builder.ToString();
        }
    }

    /// <summary>
    /// Identifies the clip a layer shows. The filter is left out on purpose: a layer whose look
    /// changes keeps its last frame on screen until the first frame with the new look arrives.
    /// </summary>
    public static string LayerKey(PlaybackLayer layer) => $"{layer.File}|{layer.Start.Ticks}";
}
