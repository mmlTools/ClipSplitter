using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClipSplitterApp.Captions;

namespace ClipSplitterApp.Services;

/// <summary>Renders a <see cref="VerticalComposition"/> to an H.264/AAC MP4 of the composition's format.</summary>
public static class VerticalExporter
{
    public const string OutputExtension = ".mp4";

    /// <param name="composition">What to render.</param>
    /// <param name="outputFile">Where to write the short.</param>
    /// <param name="captions">Captions to burn in, laid out for this composition, or null for none.</param>
    /// <param name="progress">Receives progress from 0 to 1.</param>
    /// <param name="token">Cancels the export.</param>
    public static async Task ExportAsync(VerticalComposition composition, string outputFile, CaptionScene? captions, Action<double>? progress, CancellationToken token)
    {
        if (composition.Duration <= TimeSpan.Zero)
            throw new InvalidOperationException("Add a clip to the vertical canvas first.");

        using var frames = captions is { IsEmpty: false }
            ? new CaptionFrameSource(captions, PrimaryClip(composition).Info.FrameRateFraction)
            : null;

        var args = BuildArguments(composition, outputFile, frames, out var duration);
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? AppContext.BaseDirectory);

        try
        {
            // The caption frames are drawn while ffmpeg encodes, and handed to it through its standard input.
            Action<Stream, CancellationToken>? feed = frames == null ? null : (stream, cancel) => frames.WriteTo(stream, duration, cancel);
            await FfmpegTools.RunFfmpegAsync(args, duration, progress, token, feed);
        }
        catch
        {
            FfmpegClipSplitter.TryDelete(outputFile);
            throw;
        }
    }

    /// <param name="composition">What to render.</param>
    /// <param name="outputFile">Where to write the short.</param>
    /// <param name="captions">The caption stream that will be fed to ffmpeg's standard input, or null for none.</param>
    /// <param name="duration">Receives the length of the short.</param>
    public static List<string> BuildArguments(VerticalComposition composition, string outputFile, CaptionFrameSource? captions, out TimeSpan duration)
    {
        duration = composition.Duration;
        if (duration <= TimeSpan.Zero)
            throw new InvalidOperationException("Add a clip to the vertical canvas first.");

        // One ffmpeg input per filled slot, each already trimmed to the length of the short.
        var args = new List<string>();
        var inputOfSlot = new int[2] { -1, -1 };
        var inputCount = 0;

        for (var slot = 0; slot < composition.SlotCount; slot++)
        {
            if (composition.GetSlot(slot) is not { } clip)
                continue;

            inputOfSlot[slot] = inputCount++;
            args.AddRange(new[] { "-ss", FfmpegTools.ArgTime(clip.Start), "-t", FfmpegTools.ArgTime(duration), "-i", clip.File });
        }

        var primary = PrimaryClip(composition);
        var fps = primary.Info.FrameRateArg;
        var filters = new List<string>();

        var effects = composition.Effects;
        var look = effects.Look.Filter;
        var fade = effects.FadeLength(duration);
        var fades = effects.HasFades && fade > TimeSpan.Zero;

        // The clips are arranged first. Whatever is added afterwards works on the finished
        // picture, in this order: the look, then the captions, then the fades, so that captions
        // keep their own colours and fade with everything else.
        var picture = look == null && captions == null && !fades ? "vout" : "vpicture";

        if (composition.Layout == VerticalLayoutKind.Blur)
            BuildBlurGraph(composition, fps, picture, filters);
        else
            BuildSlotGraph(composition, inputOfSlot, fps, duration, picture, filters);

        if (look != null)
        {
            filters.Add($"[{picture}]{look},format=yuv420p[vlooked]");
            picture = "vlooked";
        }

        if (captions != null)
        {
            var captionInput = inputCount;
            args.AddRange(new[]
            {
                "-f", "rawvideo",
                "-pixel_format", CaptionFrameSource.PixelFormat,
                "-video_size", $"{captions.Width}x{captions.Height}",
                "-framerate", captions.FrameRateArg,
                "-i", "pipe:0"
            });

            // The captions are converted with the same colour matrix as the footage, so a yellow
            // word is the same yellow in the file as on the canvas.
            filters.Add($"[{captionInput}:v]scale=out_color_matrix={primary.Info.ColorMatrixArg}:out_range={primary.Info.ColorRangeArg},format=yuva420p[captions]");
            filters.Add($"[{picture}][captions]overlay=x={captions.X}:y={captions.Y}:eof_action=pass,format=yuv420p[vcaptioned]");
            picture = "vcaptioned";
        }

        if (fades)
        {
            filters.Add($"[{picture}]{string.Join(",", FadeFilters("fade", effects, fade, duration))}[vfaded]");
            picture = "vfaded";
        }

        var audioMap = BuildAudioGraph(composition, inputOfSlot, fades ? "," + string.Join(",", FadeFilters("afade", effects, fade, duration)) : string.Empty, filters);

        args.AddRange(new[] { "-filter_complex", string.Join(";", filters), "-map", $"[{picture}]" });

        if (audioMap == null)
            args.Add("-an");
        else
            args.AddRange(new[] { "-map", audioMap, "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2" });

        args.AddRange(new[]
        {
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "19", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            "-map_chapters", "-1",
            "-t", FfmpegTools.ArgTime(duration),
            outputFile
        });

        return args;
    }

    /// <summary>The clip that sets the frame rate and colours of the short: the first slot that is filled.</summary>
    private static ClipSource PrimaryClip(VerticalComposition composition)
    {
        for (var slot = 0; slot < composition.SlotCount; slot++)
        {
            if (composition.GetSlot(slot) is { } clip)
                return clip;
        }

        throw new InvalidOperationException("Add a clip to the vertical canvas first.");
    }

    private static void BuildSlotGraph(VerticalComposition composition, int[] inputOfSlot, string fps, TimeSpan duration, string output, List<string> filters)
    {
        var placements = composition.GetPlacements();

        foreach (var placement in placements)
        {
            var width = Even(placement.Dest.Width);
            var height = Even(placement.Dest.Height);
            var label = placements.Count == 1 ? output : $"v{placement.Slot}";

            if (composition.GetSlot(placement.Slot) is { } clip && placement.Crop is { } crop)
            {
                var tail = placements.Count == 1 ? ",format=yuv420p" : string.Empty;
                filters.Add($"[{inputOfSlot[placement.Slot]}:V:0]{SquarePixels(clip.Info)}{Crop(clip.Info, crop)},scale={width}:{height}:flags=lanczos,setsar=1,fps={fps}{tail}[{label}]");
            }
            else
            {
                // An empty half of a stacked short is rendered black.
                filters.Add($"color=c=black:s={width}x{height}:r={fps}:d={FfmpegTools.ArgTime(duration)},setsar=1[{label}]");
            }
        }

        if (placements.Count == 2)
            filters.Add($"[v0][v1]vstack=inputs=2,format=yuv420p[{output}]");
    }

    private static void BuildBlurGraph(VerticalComposition composition, string fps, string output, List<string> filters)
    {
        var clip = composition.Top!;
        var placement = composition.GetPlacements()[0];
        var crop = placement.Crop!.Value;

        var canvasWidth = composition.CanvasWidth;
        var canvasHeight = composition.CanvasHeight;
        var width = Math.Min(canvasWidth, Even(placement.Dest.Width));
        var height = Math.Min(canvasHeight, Even(placement.Dest.Height));
        var x = (canvasWidth - width) / 2;
        var y = (canvasHeight - height) / 2;

        // The backdrop is blurred at a quarter of the output size, which is far cheaper and looks the same.
        var smallWidth = Even(canvasWidth / 4d);
        var smallHeight = Even(canvasHeight / 4d);

        filters.Add($"[0:V:0]{SquarePixels(clip.Info)}fps={fps},split=2[bgsrc][fgsrc]");
        filters.Add($"[bgsrc]scale={smallWidth}:{smallHeight}:force_original_aspect_ratio=increase,crop={smallWidth}:{smallHeight},gblur=sigma=14,lutyuv=y=val*0.6,"
                    + $"scale={canvasWidth}:{canvasHeight}:flags=bilinear,setsar=1[bg]");
        filters.Add($"[fgsrc]{Crop(clip.Info, crop)},scale={width}:{height}:flags=lanczos,setsar=1[fg]");
        filters.Add($"[bg][fg]overlay={x}:{y},format=yuv420p[{output}]");
    }

    /// <summary>
    /// The fade-in and fade-out filters of a short, for its picture ("fade") or its sound
    /// ("afade"). Both take the same options, which keeps the two in step.
    /// </summary>
    private static IEnumerable<string> FadeFilters(string filter, ShortEffects effects, TimeSpan fade, TimeSpan duration)
    {
        var length = FfmpegTools.ArgTime(fade);

        if (effects.FadeIn)
            yield return $"{filter}=t=in:st=0:d={length}";

        if (effects.FadeOut)
            yield return $"{filter}=t=out:st={FfmpegTools.ArgTime(duration - fade)}:d={length}";
    }

    /// <summary>Adds the audio filters, if any, and returns what to pass to -map (null for a silent short).</summary>
    /// <param name="finish">Filters to run on the finished sound, each with a leading comma, or an empty string.</param>
    private static string? BuildAudioGraph(VerticalComposition composition, int[] inputOfSlot, string finish, List<string> filters)
    {
        var slots = composition.GetAudioSlots();
        if (slots.Count == 0)
            return null;

        // first_pts=0 pads a late-starting audio track so sound stays in step with the picture.
        const string resample = "aresample=48000:async=1:first_pts=0";

        if (slots.Count == 1)
        {
            filters.Add($"[{inputOfSlot[slots[0]]}:a:0]{resample}{finish}[aout]");
            return "[aout]";
        }

        filters.Add($"[{inputOfSlot[slots[0]]}:a:0]{resample}[a0]");
        filters.Add($"[{inputOfSlot[slots[1]]}:a:0]{resample}[a1]");
        filters.Add($"[a0][a1]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0,alimiter=limit=0.97{finish}[aout]");
        return "[aout]";
    }

    private static string SquarePixels(MediaInfo info)
        => info.HasNonSquarePixels ? $"scale={info.Width}:{info.Height},setsar=1," : string.Empty;

    private static string Crop(MediaInfo info, RectD crop)
    {
        var width = Math.Clamp((int)Math.Round(crop.Width), 2, info.Width);
        var height = Math.Clamp((int)Math.Round(crop.Height), 2, info.Height);
        var x = Math.Clamp((int)Math.Round(crop.X), 0, info.Width - width);
        var y = Math.Clamp((int)Math.Round(crop.Y), 0, info.Height - height);
        return string.Create(CultureInfo.InvariantCulture, $"crop={width}:{height}:{x}:{y}");
    }

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2d) * 2);
}
