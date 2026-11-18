using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ClipSplitterApp.Services;

/// <summary>
/// A colour treatment for a short. It is an ffmpeg filter, and the preview player runs that very
/// filter on the frames it decodes, so the look on the canvas is the look in the exported file.
/// </summary>
/// <param name="Filter">The filter, as a template for <see cref="EffectFilter.Resolve"/>, or null for none.</param>
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
        new VideoLook("vintage", "Vintage", "curves=preset=vintage,eq=saturation=0.85"),
        new VideoLook("punch", "Punch", "eq=contrast=1.18:saturation=1.25,unsharp=5:5:0.8"),
        new VideoLook("dream", "Dream", "gblur=sigma={p:0.3},eq=brightness=0.05:saturation=1.2:contrast=0.95"),
        new VideoLook("vhs", "VHS", "chromashift=cbh={i:0.4}:crh=-{i:0.4},noise=alls=9:allf=t,eq=saturation=1.25:contrast=0.94:brightness=0.02")
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

/// <summary>
/// Something that moves or disturbs the picture of a short, such as a wave or a shake. Like a
/// look it is an ffmpeg filter that the preview player and the exporter both run, on every clip
/// of the short as it comes out of its file.
/// </summary>
/// <param name="Filter">The filter, as a template for <see cref="EffectFilter.Resolve"/>, or null for none.</param>
/// <param name="Tip">What the effect does, in a few words.</param>
public sealed record VideoEffect(string Id, string Name, string? Filter, string Tip)
{
    public static readonly VideoEffect None = new("none", "None", null, "The picture as it is");

    // The wave slides every row of the picture sideways by an amount that travels down the
    // frame. The amounts are drawn as a tiny picture and stretched to the frame, which is many
    // times faster than working them out pixel by pixel. Colour is kept at full resolution
    // meanwhile, so that it moves exactly with the brightness.
    private const string WaveAmount = "128+{p:1.4}*sin(2*PI*(2.5*Y/H+0.7*T))";
    private const string Wave =
        "scale={W}:{H}:flags=bilinear,format=yuv444p[fxs{ID}];"
        + "color=c=gray:s=16x180:r={FPS},format=yuv444p,geq=lum='" + WaveAmount + "':cb='" + WaveAmount + "':cr='" + WaveAmount + "',scale={W}:{H}:flags=bilinear[fxx{ID}];"
        + "color=c=gray:s=16x16:r={FPS},format=yuv444p,geq=lum=128:cb=128:cr=128,scale={W}:{H}:flags=neighbor[fxy{ID}];"
        + "[fxs{ID}][fxx{ID}][fxy{ID}]displace=edge=smear";

    // The picture is enlarged a little and a window of the original size wanders over it.
    private const string Shake =
        "scale={ZW}:{ZH}:flags=bilinear,"
        + "crop=w=iw/1.06:h=ih/1.06:x='(iw-ow)/2*(1+0.9*sin(2*PI*t*2.3)*sin(2*PI*t*0.37+1))':y='(ih-oh)/2*(1+0.9*cos(2*PI*t*1.7)*sin(2*PI*t*0.29))',"
        + "scale={W}:{H}:flags=bilinear";

    // Colours sit slightly apart all the time, and tear apart in short bursts.
    private const string Glitch =
        "chromashift=cbh={i:0.3}:crh=-{i:0.3},"
        + "chromashift=cbh={i:1.3}:crh=-{i:1.3}:cbv={i:0.4}:enable='lt(mod(t\\,1.7)\\,0.16)',"
        + "noise=alls=10:allf=t:enable='lt(mod(t\\,1.7)\\,0.16)'";

    public static IReadOnlyList<VideoEffect> All { get; } = new[]
    {
        None,
        new VideoEffect("wave", "Wave", Wave, "The picture ripples from side to side"),
        new VideoEffect("shake", "Shake", Shake, "A restless, hand-held camera"),
        new VideoEffect("glitch", "Glitch", Glitch, "Colours split apart in short bursts"),
        new VideoEffect("grain", "Grain", "noise=alls=16:allf=t+u", "Film grain over the picture")
    };

    public static VideoEffect Find(string? id)
    {
        foreach (var effect in All)
        {
            if (string.Equals(effect.Id, id, StringComparison.OrdinalIgnoreCase))
                return effect;
        }

        return None;
    }
}

/// <summary>
/// Turns a filter template into a filter for frames of one size. A look or an effect is written
/// once and runs on small preview frames and on full-size ones alike, so lengths in it are given
/// as shares of the frame rather than in pixels.
/// </summary>
public static class EffectFilter
{
    private static readonly Regex Share = new(@"\{(p|i):([0-9.]+)\}", RegexOptions.Compiled);

    /// <param name="template">
    /// A filter in which {W} and {H} stand for the frame's size, {ZW} and {ZH} for that size
    /// enlarged by 6 percent, {FPS} for the frame rate, {ID} for text that keeps labels apart,
    /// {p:1.4} for 1.4 percent of the frame's shorter side, and {i:1.4} for the same as a whole
    /// number of at least 1.
    /// </param>
    /// <param name="frameRate">The frame rate, in the form ffmpeg takes.</param>
    /// <param name="id">Added to every label in the filter. Two uses of a filter in one graph need different ones.</param>
    public static string Resolve(string template, int width, int height, string frameRate, string id)
    {
        var side = Math.Min(width, height);

        var filter = Share.Replace(template, match =>
        {
            var amount = side * double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) / 100d;
            return match.Groups[1].Value == "i"
                ? Math.Max(1, (int)Math.Round(amount)).ToString(CultureInfo.InvariantCulture)
                : amount.ToString("0.###", CultureInfo.InvariantCulture);
        });

        return filter
            .Replace("{ZW}", Even(width * 1.06).ToString(CultureInfo.InvariantCulture))
            .Replace("{ZH}", Even(height * 1.06).ToString(CultureInfo.InvariantCulture))
            .Replace("{W}", width.ToString(CultureInfo.InvariantCulture))
            .Replace("{H}", height.ToString(CultureInfo.InvariantCulture))
            .Replace("{FPS}", frameRate)
            .Replace("{ID}", id);
    }

    private static int Even(double value) => Math.Max(2, (int)Math.Ceiling(value / 2d) * 2);
}

/// <summary>The effects and transitions of a short: its look, its speed, what moves its picture, and how it opens and closes.</summary>
public sealed class ShortEffects
{
    public const double MinFadeSeconds = 0.1;
    public const double MaxFadeSeconds = 3;

    /// <summary>The speeds on offer. Below 1 is slow motion.</summary>
    public static IReadOnlyList<double> Speeds { get; } = new[] { 0.5, 0.75, 1, 1.5, 2 };

    private double _speed = 1;

    public string LookId { get; set; } = VideoLook.Natural.Id;

    public string EffectId { get; set; } = VideoEffect.None.Id;

    /// <summary>
    /// How fast the short plays compared with its clips. Picture and sound change speed
    /// together, and the sound keeps its pitch. Anything but one of <see cref="Speeds"/> is 1.
    /// </summary>
    public double Speed
    {
        get => _speed;
        set
        {
            _speed = 1;
            foreach (var speed in Speeds)
            {
                if (Math.Abs(speed - value) < 0.01)
                    _speed = speed;
            }
        }
    }

    /// <summary>The short opens by fading in from black, with its sound rising.</summary>
    public bool FadeIn { get; set; }

    /// <summary>The short closes by fading out to black, with its sound falling.</summary>
    public bool FadeOut { get; set; }

    public double FadeSeconds { get; set; } = 0.6;

    public VideoLook Look => VideoLook.Find(LookId);

    public VideoEffect Effect => VideoEffect.Find(EffectId);

    /// <summary>
    /// The template of everything the preview player runs on a clip's frames: the look, then the
    /// effect. Null when the short has neither.
    /// </summary>
    public string? PreviewFilter
    {
        get
        {
            var look = Look.Filter;
            var effect = Effect.Filter;
            return look != null && effect != null ? look + "," + effect : look ?? effect;
        }
    }

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
