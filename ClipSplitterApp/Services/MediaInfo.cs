using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClipSplitterApp.Services;

/// <summary>
/// What the app needs to know about a video file. Width and height are the displayed size,
/// after rotation metadata and non-square pixels have been applied.
/// </summary>
public sealed record MediaInfo(
    string File,
    TimeSpan Duration,
    int Width,
    int Height,
    double FrameRate,
    string FrameRateArg,
    bool HasAudio,
    bool HasNonSquarePixels)
{
    public double AspectRatio => Height > 0 ? (double)Width / Height : 16d / 9d;
}

public static class MediaProbe
{
    private const double MaxFrameRate = 60;
    private static readonly ConcurrentDictionary<string, MediaInfo> Cache = new();

    public static async Task<MediaInfo> ProbeAsync(string file, CancellationToken token)
    {
        var fileInfo = new FileInfo(file);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("Video file does not exist.", file);

        var key = $"{fileInfo.FullName}|{fileInfo.LastWriteTimeUtc.Ticks}|{fileInfo.Length}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        var json = await FfmpegTools.RunCaptureAsync(
            "ffprobe",
            new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", file },
            token);

        var info = Parse(file, json);
        Cache[key] = info;
        return info;
    }

    internal static MediaInfo Parse(string file, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var hasAudio = false;
        JsonElement? video = null;

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = GetString(stream, "codec_type");
                if (type == "audio")
                {
                    hasAudio = true;
                }
                else if (type == "video" && video == null && !IsAttachedPicture(stream))
                {
                    video = stream;
                }
            }
        }

        if (video is not { } videoStream)
            throw new InvalidOperationException($"No video stream found in {Path.GetFileName(file)}.");

        var width = GetInt(videoStream, "width");
        var height = GetInt(videoStream, "height");
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"Could not read the video size of {Path.GetFileName(file)}.");

        var nonSquare = false;
        if (TryParseRatio(GetString(videoStream, "sample_aspect_ratio"), ':', out var sar) && Math.Abs(sar - 1) > 0.01)
        {
            width = Math.Max(2, (int)Math.Round(width * sar));
            nonSquare = true;
        }

        if (Math.Abs(GetRotation(videoStream)) % 180 == 90)
            (width, height) = (height, width);

        var duration = TimeSpan.Zero;
        if (root.TryGetProperty("format", out var format) && TryParseSeconds(GetString(format, "duration"), out var formatSeconds))
            duration = TimeSpan.FromSeconds(formatSeconds);
        else if (TryParseSeconds(GetString(videoStream, "duration"), out var streamSeconds))
            duration = TimeSpan.FromSeconds(streamSeconds);

        if (duration <= TimeSpan.Zero)
            throw new InvalidOperationException("Could not detect video duration.");

        var (frameRate, frameRateArg) = ReadFrameRate(videoStream);
        return new MediaInfo(file, duration, width, height, frameRate, frameRateArg, hasAudio, nonSquare);
    }

    private static (double Value, string Arg) ReadFrameRate(JsonElement stream)
    {
        foreach (var property in new[] { "avg_frame_rate", "r_frame_rate" })
        {
            var text = GetString(stream, property);
            if (TryParseRatio(text, '/', out var rate) && rate >= 1 && rate < 1000)
                return rate > MaxFrameRate ? (MaxFrameRate, "60") : (rate, text!);
        }

        return (30, "30");
    }

    private static bool IsAttachedPicture(JsonElement stream)
        => stream.TryGetProperty("disposition", out var disposition)
           && disposition.TryGetProperty("attached_pic", out var attached)
           && attached.ValueKind == JsonValueKind.Number
           && attached.GetInt32() == 1;

    private static int GetRotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in sideData.EnumerateArray())
            {
                if (entry.TryGetProperty("rotation", out var rotation) && rotation.ValueKind == JsonValueKind.Number)
                    return (int)Math.Round(rotation.GetDouble());
            }
        }

        if (stream.TryGetProperty("tags", out var tags)
            && int.TryParse(GetString(tags, "rotate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tagRotation))
            return tagRotation;

        return 0;
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static bool TryParseSeconds(string? text, out double seconds)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds > 0;

    private static bool TryParseRatio(string? text, char separator, out double ratio)
    {
        ratio = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split(separator);
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            || numerator <= 0
            || denominator <= 0)
            return false;

        ratio = numerator / denominator;
        return true;
    }
}
