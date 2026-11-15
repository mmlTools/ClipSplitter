using System;
using System.Globalization;
using System.IO;

namespace ClipSplitterApp.Services;

/// <summary>Shared text and file-name formatting for clips.</summary>
public static class ClipFormat
{
    public static string Time(TimeSpan time)
        => time.ToString(time.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);

    /// <summary>Like <see cref="Time"/>, with tenths of a second for trimmed ranges.</summary>
    public static string PreciseTime(TimeSpan time)
        => time.ToString(time.TotalHours >= 1 ? @"hh\:mm\:ss\.f" : @"mm\:ss\.f", CultureInfo.InvariantCulture);

    public static string Length(TimeSpan length)
    {
        var seconds = Math.Max(0, length.TotalSeconds);
        var whole = Math.Abs(seconds - Math.Round(seconds)) < 0.05;
        if (seconds < 60)
            return (whole ? Math.Round(seconds).ToString("0", CultureInfo.InvariantCulture) : seconds.ToString("0.0", CultureInfo.InvariantCulture)) + "s";

        var minutes = (int)(seconds / 60);
        var rest = seconds - minutes * 60;
        return $"{minutes}m {(whole ? Math.Round(rest).ToString("00", CultureInfo.InvariantCulture) : rest.ToString("00.0", CultureInfo.InvariantCulture))}s";
    }

    public static string FileStamp(TimeSpan time)
        => time.ToString(time.TotalHours >= 1 ? @"hh\-mm\-ss" : @"mm\-ss", CultureInfo.InvariantCulture);

    public static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        return name.Trim();
    }

    /// <summary>Builds "name-start-end[-suffix].ext" for a clip cut from <paramref name="inputFile"/>.</summary>
    public static string OutputFileName(string inputFile, TimeSpan start, TimeSpan end, string extension, string? suffix = null)
    {
        var name = SanitizeFileName(Path.GetFileNameWithoutExtension(inputFile));
        var tail = string.IsNullOrEmpty(suffix) ? string.Empty : "-" + suffix;
        return $"{name}-{FileStamp(start)}-{FileStamp(end)}{tail}{extension}";
    }

    /// <summary>Returns <paramref name="path"/>, or a numbered variant of it when that file already exists.</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(folder, $"{name}-{index}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }
}
