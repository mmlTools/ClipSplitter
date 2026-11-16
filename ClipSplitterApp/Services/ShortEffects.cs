using System;
using System.Collections.Generic;

namespace ClipSplitterApp.Services;

/// <summary>
/// A colour treatment for a short. It is an ffmpeg filter, and the preview player runs that very
/// filter on the frames it decodes, so the look on the canvas is the look in the exported file.
/// </summary>
public sealed record VideoLook(string Id, string Name, string? Filter)
{
    /// <summary>The picture as it was recorded.</summary>
    public static readonly VideoLook Natural = new("natural", "Natural", null);

    public static IReadOnlyList<VideoLook> All { get; } = new[]
    {
        Natural,
        new VideoLook("vivid", "Vivid", "eq=saturation=1.35:contrast=1.06"),
        new VideoLook("mono", "B&W", "hue=s=0,eq=contrast=1.08"),
        new VideoLook("warm", "Warm", "colorbalance=rs=0.06:bs=-0.06:rm=0.07:bm=-0.07:rh=0.03:bh=-0.03"),
        new VideoLook("cool", "Cool", "colorbalance=rs=-0.06:bs=0.06:rm=-0.07:bm=0.07:rh=-0.03:bh=0.03"),
        new VideoLook("vintage", "Vintage", "curves=preset=vintage,eq=saturation=0.85")
    };

    public static VideoLook Find(string? id)
    {
        foreach (var look in All)
        {
            if (string.Equals(look.Id, id, StringComparison.OrdinalIgnoreCase))
                return look;
        }

        return Natural;
    }
}

/// <summary>The effects and transitions of a short: its colour look, and how it opens and closes.</summary>
public sealed class ShortEffects
{
    public const double MinFadeSeconds = 0.1;
    public const double MaxFadeSeconds = 3;

    public string LookId { get; set; } = VideoLook.Natural.Id;

    /// <summary>The short opens by fading in from black, with its sound rising.</summary>
    public bool FadeIn { get; set; }

    /// <summary>The short closes by fading out to black, with its sound falling.</summary>
    public bool FadeOut { get; set; }

    public double FadeSeconds { get; set; } = 0.6;

    public VideoLook Look => VideoLook.Find(LookId);

    /// <summary>True when the short has a fade at either end.</summary>
    public bool HasFades => FadeIn || FadeOut;

    /// <summary>
    /// The length of each fade for a short of the given length. Fades never take more than half
    /// of the short between them, so a very short clip still shows itself.
    /// </summary>
    public TimeSpan FadeLength(TimeSpan duration)
    {
        var seconds = double.IsFinite(FadeSeconds) ? Math.Clamp(FadeSeconds, MinFadeSeconds, MaxFadeSeconds) : MinFadeSeconds;
        var most = duration.TotalSeconds / 4;
        return TimeSpan.FromSeconds(Math.Max(0, Math.Min(seconds, most)));
    }

    /// <summary>
    /// How much of the picture shows at a moment: 0 is black, 1 is the full picture. This is the
    /// straight ramp ffmpeg's fade filter uses, so the preview dims exactly as the export does.
    /// </summary>
    public double OpacityAt(TimeSpan position, TimeSpan duration)
    {
        var fade = FadeLength(duration).TotalSeconds;
        if (fade <= 0)
            return 1;

        var opacity = 1d;
        if (FadeIn)
            opacity = Math.Min(opacity, position.TotalSeconds / fade);
        if (FadeOut)
            opacity = Math.Min(opacity, (duration - position).TotalSeconds / fade);

        return Math.Clamp(opacity, 0, 1);
    }

    public ShortEffects Clone() => (ShortEffects)MemberwiseClone();
}
