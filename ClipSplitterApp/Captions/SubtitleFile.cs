using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ClipSplitterApp.Captions;

/// <summary>Reads .srt and .vtt subtitle files into timed words.</summary>
public static class SubtitleFile
{
    private static readonly Regex TimingLine = new(
        @"^\s*(?:(?<h1>\d+):)?(?<m1>\d{1,2}):(?<s1>\d{2})[.,](?<f1>\d{1,3})\s*-->\s*(?:(?<h2>\d+):)?(?<m2>\d{1,2}):(?<s2>\d{2})[.,](?<f2>\d{1,3})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Markup = new(@"<[^>]*>|\{[^}]*\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly char[] Whitespace = { ' ', '\t', ' ' };

    public static IReadOnlyList<CaptionWord> Read(string path)
    {
        var text = File.ReadAllText(path);

        // Older subtitle files are often saved in a Western code page rather than UTF-8.
        if (text.Contains('�'))
            text = File.ReadAllText(path, Encoding.Latin1);

        var words = Parse(text);
        if (words.Count == 0)
            throw new InvalidDataException($"No subtitles were found in {Path.GetFileName(path)}. ClipSplitter reads .srt and .vtt files.");

        return words;
    }

    /// <summary>
    /// Turns subtitle text into words. A subtitle file only says when each line is shown, so the
    /// words of a line share its time by their length.
    /// </summary>
    public static IReadOnlyList<CaptionWord> Parse(string text)
    {
        var words = new List<CaptionWord>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var match = TimingLine.Match(lines[index]);
            if (!match.Success)
                continue;

            var start = ReadTime(match, "1");
            var end = ReadTime(match, "2");

            var cue = new StringBuilder();
            while (index + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[index + 1]) && !TimingLine.IsMatch(lines[index + 1]))
            {
                index++;
                cue.Append(lines[index]).Append(' ');
            }

            var plain = WebUtility.HtmlDecode(Markup.Replace(cue.ToString(), string.Empty));
            var tokens = plain.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 0 && end > start)
                words.AddRange(Transcript.Spread(tokens, start, end));
        }

        words.Sort((a, b) => a.Start.CompareTo(b.Start));
        return words;
    }

    private static TimeSpan ReadTime(Match match, string suffix)
    {
        var hours = match.Groups["h" + suffix].Success ? int.Parse(match.Groups["h" + suffix].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = int.Parse(match.Groups["m" + suffix].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups["s" + suffix].Value, CultureInfo.InvariantCulture);

        // "5" after the separator means half a second, not five milliseconds.
        var fraction = match.Groups["f" + suffix].Value.PadRight(3, '0');
        var milliseconds = int.Parse(fraction, CultureInfo.InvariantCulture);

        return new TimeSpan(0, hours, minutes, seconds, milliseconds);
    }
}
