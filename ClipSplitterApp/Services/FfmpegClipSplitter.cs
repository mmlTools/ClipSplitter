using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClipSplitterApp.Services;

/// <summary>Plans fixed-length segments and exports them in the source's own shape.</summary>
public sealed class FfmpegClipSplitter
{
    private static readonly HashSet<string> ReencodeContainers = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".m4v" };

    private readonly Action<string> _log;

    public FfmpegClipSplitter(Action<string> log) => _log = log;

    public static IReadOnlyList<ClipSegment> CreatePlan(TimeSpan duration, int segmentSeconds)
    {
        if (duration <= TimeSpan.Zero)
            throw new InvalidOperationException("Could not detect video duration.");

        var totalSegments = (int)Math.Ceiling(duration.TotalSeconds / segmentSeconds);
        var segments = new List<ClipSegment>(totalSegments);

        for (var index = 0; index < totalSegments; index++)
        {
            var start = TimeSpan.FromSeconds((double)index * segmentSeconds);
            var end = TimeSpan.FromSeconds(Math.Min((index + 1d) * segmentSeconds, duration.TotalSeconds));
            if (end > start)
                segments.Add(new ClipSegment(index, start, end));
        }

        return segments;
    }

    /// <summary>
    /// The extension an exported clip gets. Stream copies keep the source container; re-encodes
    /// produce H.264/AAC, which only some containers can hold, so the rest fall back to MP4.
    /// </summary>
    public static string GetOutputExtension(string inputFile, bool reencode)
    {
        var extension = Path.GetExtension(inputFile);
        if (string.IsNullOrWhiteSpace(extension))
            return ".mp4";

        return !reencode || ReencodeContainers.Contains(extension) ? extension : ".mp4";
    }

    public async Task SplitAsync(SplitOptions options, Action<double> progress, CancellationToken token)
    {
        _log(options.ReencodeForExactCuts ? "Mode: re-encode for exact cuts" : "Mode: fast stream copy");

        var extension = GetOutputExtension(options.InputFile, options.ReencodeForExactCuts);
        var total = options.Segments.Count;

        for (var index = 0; index < total; index++)
        {
            token.ThrowIfCancellationRequested();

            var segment = options.Segments[index];
            if (segment.Length <= TimeSpan.Zero)
                continue;

            var outputFile = Path.Combine(
                options.OutputDirectory,
                ClipFormat.OutputFileName(options.InputFile, segment.Start, segment.End, extension));

            _log($"Creating {Path.GetFileName(outputFile)}");

            var done = index;
            await RunSegmentAsync(
                options.InputFile,
                outputFile,
                segment,
                options.ReencodeForExactCuts,
                fraction => progress((done + fraction) / total * 100d),
                token);
        }
    }

    public async Task CreateThumbnailAsync(string inputFile, ClipSegment segment, string outputFile, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? AppContext.BaseDirectory);

        var offset = segment.Start + TimeSpan.FromTicks(segment.Length.Ticks / 2);
        var args = new[]
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
            "-ss", FfmpegTools.ArgTime(offset),
            "-i", inputFile,
            "-map", "0:V:0",
            "-frames:v", "1",
            "-vf", "scale=256:-2",
            "-q:v", "4",
            outputFile
        };

        await FfmpegTools.RunCaptureAsync("ffmpeg", args, token);
    }

    private static async Task RunSegmentAsync(string inputFile, string outputFile, ClipSegment segment, bool reencode, Action<double> progress, CancellationToken token)
    {
        // Fast mode uses stream copy. It is quick, but cuts can align to keyframes.
        // Exact mode re-encodes and gives more accurate cut points.
        var args = new List<string>
        {
            "-ss", FfmpegTools.ArgTime(segment.Start),
            "-i", inputFile,
            "-t", FfmpegTools.ArgTime(segment.Length)
        };

        if (reencode)
            args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k" });
        else
            args.AddRange(new[] { "-c", "copy", "-avoid_negative_ts", "make_zero" });

        args.Add(outputFile);

        try
        {
            await FfmpegTools.RunFfmpegAsync(args, segment.Length, progress, token);
        }
        catch
        {
            TryDelete(outputFile);
            throw;
        }
    }

    internal static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
                File.Delete(file);
        }
        catch
        {
            // A half-written file we cannot remove is not worth failing the export over.
        }
    }
}
