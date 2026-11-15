using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClipSplitterApp.Services;

/// <summary>Renders a <see cref="VerticalComposition"/> to a 1080x1920 H.264/AAC MP4.</summary>
public static class VerticalExporter
{
    public const string OutputExtension = ".mp4";

    public static async Task ExportAsync(VerticalComposition composition, string outputFile, Action<double>? progress, CancellationToken token)
    {
        var args = BuildArguments(composition, outputFile, out var duration);
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? AppContext.BaseDirectory);

        try
        {
            await FfmpegTools.RunFfmpegAsync(args, duration, progress, token);
        }
        catch
        {
            FfmpegClipSplitter.TryDelete(outputFile);
            throw;
        }
    }

    public static List<string> BuildArguments(VerticalComposition composition, string outputFile, out TimeSpan duration)
    {
        duration = composition.Duration;
        if (duration <= TimeSpan.Zero)
            throw new InvalidOperationException("Add a clip to the vertical canvas first.");

        // One ffmpeg input per filled slot, each already trimmed to the length of the short.
        var args = new List<string>();
        var inputOfSlot = new int[2] { -1, -1 };
        var inputCount = 0;
        ClipSource? primary = null;

        for (var slot = 0; slot < composition.SlotCount; slot++)
        {
            if (composition.GetSlot(slot) is not { } clip)
                continue;

            primary ??= clip;
            inputOfSlot[slot] = inputCount++;
            args.AddRange(new[] { "-ss", FfmpegTools.ArgTime(clip.Start), "-t", FfmpegTools.ArgTime(duration), "-i", clip.File });
        }

        var fps = primary!.Info.FrameRateArg;
        var filters = new List<string>();

        if (composition.Layout == VerticalLayoutKind.Blur)
            BuildBlurGraph(composition, fps, filters);
        else
            BuildSlotGraph(composition, inputOfSlot, fps, duration, filters);

        var audioMap = BuildAudioGraph(composition, inputOfSlot, filters);

        args.AddRange(new[] { "-filter_complex", string.Join(";", filters), "-map", "[vout]" });

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

    private static void BuildSlotGraph(VerticalComposition composition, int[] inputOfSlot, string fps, TimeSpan duration, List<string> filters)
    {
        var placements = composition.GetPlacements();

        foreach (var placement in placements)
        {
            var width = Even(placement.Dest.Width);
            var height = Even(placement.Dest.Height);
            var label = placements.Count == 1 ? "vout" : $"v{placement.Slot}";

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
            filters.Add("[v0][v1]vstack=inputs=2,format=yuv420p[vout]");
    }

    private static void BuildBlurGraph(VerticalComposition composition, string fps, List<string> filters)
    {
        var clip = composition.Top!;
        var placement = composition.GetPlacements()[0];
        var crop = placement.Crop!.Value;

        var width = Math.Min(VerticalComposition.CanvasWidth, Even(placement.Dest.Width));
        var height = Math.Min(VerticalComposition.CanvasHeight, Even(placement.Dest.Height));
        var x = (VerticalComposition.CanvasWidth - width) / 2;
        var y = (VerticalComposition.CanvasHeight - height) / 2;

        // The backdrop is blurred at a quarter of the output size, which is far cheaper and looks the same.
        filters.Add($"[0:V:0]{SquarePixels(clip.Info)}fps={fps},split=2[bgsrc][fgsrc]");
        filters.Add("[bgsrc]scale=270:480:force_original_aspect_ratio=increase,crop=270:480,gblur=sigma=14,lutyuv=y=val*0.6,"
                    + $"scale={VerticalComposition.CanvasWidth}:{VerticalComposition.CanvasHeight}:flags=bilinear,setsar=1[bg]");
        filters.Add($"[fgsrc]{Crop(clip.Info, crop)},scale={width}:{height}:flags=lanczos,setsar=1[fg]");
        filters.Add($"[bg][fg]overlay={x}:{y},format=yuv420p[vout]");
    }

    /// <summary>Adds the audio filters, if any, and returns what to pass to -map (null for a silent short).</summary>
    private static string? BuildAudioGraph(VerticalComposition composition, int[] inputOfSlot, List<string> filters)
    {
        var slots = composition.GetAudioSlots();
        if (slots.Count == 0)
            return null;

        // first_pts=0 pads a late-starting audio track so sound stays in step with the picture.
        const string resample = "aresample=48000:async=1:first_pts=0";

        if (slots.Count == 1)
        {
            filters.Add($"[{inputOfSlot[slots[0]]}:a:0]{resample}[aout]");
            return "[aout]";
        }

        filters.Add($"[{inputOfSlot[slots[0]]}:a:0]{resample}[a0]");
        filters.Add($"[{inputOfSlot[slots[1]]}:a:0]{resample}[a1]");
        filters.Add("[a0][a1]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0,alimiter=limit=0.97[aout]");
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
