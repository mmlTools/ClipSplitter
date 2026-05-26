using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClipSplitterApp.Services;

public sealed class FfmpegClipSplitter
{
    private readonly Action<string> _log;

    public FfmpegClipSplitter(Action<string> log) => _log = log;

    public async Task<IReadOnlyList<ClipSegment>> CreatePlanAsync(string inputFile, int segmentSeconds, CancellationToken token)
    {
        var ffprobe = FindTool("ffprobe");
        var duration = await GetDurationAsync(ffprobe, inputFile, token);
        if (duration <= TimeSpan.Zero)
            throw new InvalidOperationException("Could not detect video duration.");

        var totalSegments = (int)Math.Ceiling(duration.TotalSeconds / segmentSeconds);
        var segments = new List<ClipSegment>(totalSegments);

        for (var index = 0; index < totalSegments; index++)
        {
            var start = TimeSpan.FromSeconds(index * segmentSeconds);
            var end = TimeSpan.FromSeconds(Math.Min((index + 1) * segmentSeconds, duration.TotalSeconds));
            if (end > start)
                segments.Add(new ClipSegment(index, start, end));
        }

        return segments;
    }

    public async Task SplitAsync(SplitOptions options, Action<double> progress, CancellationToken token)
    {
        var ffmpeg = FindTool("ffmpeg");

        _log($"Using FFmpeg: {ffmpeg}");
        _log($"Segment length: {options.SegmentSeconds} seconds");

        var inputName = Path.GetFileNameWithoutExtension(options.InputFile);
        var extension = Path.GetExtension(options.InputFile);
        var totalSegments = options.Segments.Count;

        for (var index = 0; index < totalSegments; index++)
        {
            token.ThrowIfCancellationRequested();

            var segment = options.Segments[index];
            var start = segment.Start;
            var end = segment.End;
            var length = segment.Length;
            if (length <= TimeSpan.Zero) break;

            var outputFile = Path.Combine(
                options.OutputDirectory,
                $"{SanitizeFileName(inputName)}-{FormatFileStamp(start)}-{FormatFileStamp(end)}{extension}");

            _log($"Creating {Path.GetFileName(outputFile)}");
            await RunFfmpegSegmentAsync(ffmpeg, options.InputFile, outputFile, start, length, options.ReencodeForExactCuts, token);

            progress(Math.Round(((index + 1d) / totalSegments) * 100d, 2));
        }
    }

    public async Task CreateThumbnailAsync(string inputFile, ClipSegment segment, string outputFile, CancellationToken token)
    {
        var ffmpeg = FindTool("ffmpeg");
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? AppContext.BaseDirectory);

        var offset = segment.Start + TimeSpan.FromTicks(segment.Length.Ticks / 2);
        var args = $"-y -ss {FormatArgTime(offset)} -i {Quote(inputFile)} -frames:v 1 -vf scale=112:-1:force_original_aspect_ratio=decrease {Quote(outputFile)}";
        await RunProcessCaptureAsync(ffmpeg, args, token);
    }

    public async Task CreatePreviewClipAsync(string inputFile, ClipSegment segment, string outputFile, CancellationToken token)
    {
        var ffmpeg = FindTool("ffmpeg");
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? AppContext.BaseDirectory);
        await RunFfmpegSegmentAsync(ffmpeg, inputFile, outputFile, segment.Start, segment.Length, true, token);
    }

    private static async Task<TimeSpan> GetDurationAsync(string ffprobe, string inputFile, CancellationToken token)
    {
        var args = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 {Quote(inputFile)}";
        var output = await RunProcessCaptureAsync(ffprobe, args, token);

        if (double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds);

        return TimeSpan.Zero;
    }

    private static async Task RunFfmpegSegmentAsync(string ffmpeg, string inputFile, string outputFile, TimeSpan start, TimeSpan length, bool reencode, CancellationToken token)
    {
        var codecArgs = reencode
            ? "-c:v libx264 -preset veryfast -crf 20 -c:a aac -b:a 192k"
            : "-c copy -avoid_negative_ts make_zero";

        // Fast mode uses stream copy. It is quick, but cuts can align to keyframes.
        // Exact mode re-encodes and gives more accurate cut points.
        var args = $"-y -ss {FormatArgTime(start)} -i {Quote(inputFile)} -t {FormatArgTime(length)} {codecArgs} {Quote(outputFile)}";
        await RunProcessCaptureAsync(ffmpeg, args, token);
    }

    private static async Task<string> RunProcessCaptureAsync(string executable, string arguments, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Could not start {executable}.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not start {executable}. Install FFmpeg and add it to PATH, or place it in tools/ffmpeg beside the app. Details: {ex.Message}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(token);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} failed. {error}".Trim());

        return output.Length > 0 ? output.ToString() : error.ToString();
    }

    private static string FindTool(string toolName)
    {
        var exe = OperatingSystem.IsWindows() ? toolName + ".exe" : toolName;
        var baseDir = AppContext.BaseDirectory;
        var local = Path.Combine(baseDir, "tools", "ffmpeg", exe);
        if (File.Exists(local)) return local;
        return exe;
    }

    private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";

    private static string FormatArgTime(TimeSpan time)
        => time.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    public static string FormatTime(TimeSpan time)
        => time.ToString(time.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);

    public static string FormatFileStamp(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return time.ToString(@"hh\-mm\-ss", CultureInfo.InvariantCulture);

        return time.ToString(@"mm\-ss", CultureInfo.InvariantCulture);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        return name.Trim();
    }
}
