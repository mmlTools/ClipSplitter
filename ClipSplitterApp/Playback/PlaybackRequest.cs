using System;
using System.Collections.Generic;
using System.Text;
using ClipSplitterApp.Services;

namespace ClipSplitterApp.Playback;

/// <summary>One video to decode: a stretch of a file, shown in its own layer.</summary>
public sealed record PlaybackLayer(string File, TimeSpan Start, TimeSpan Length, MediaInfo Info);

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
                builder.Append(layer == null ? "-" : LayerKey(layer)).Append(';');

            builder.Append("audio=").Append(string.Join(',', AudioLayers));
            builder.Append(";duration=").Append(Duration.Ticks);
            return builder.ToString();
        }
    }

    public static string LayerKey(PlaybackLayer layer) => $"{layer.File}|{layer.Start.Ticks}";
}
