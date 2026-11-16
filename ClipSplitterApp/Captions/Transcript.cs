using System;
using System.Collections.Generic;
using System.Linq;

namespace ClipSplitterApp.Captions;

/// <summary>One spoken word and when it is said.</summary>
public sealed record CaptionWord(string Text, TimeSpan Start, TimeSpan End);

/// <summary>
/// The words spoken in a stretch of a video file. Times are measured from the start of the file,
/// so the same transcript serves any clip cut from that stretch, however it is trimmed.
/// </summary>
public sealed class Transcript
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(60);
    private static readonly char[] Whitespace = { ' ', '\t', '\r', '\n', ' ' };

    public Transcript(string file, TimeSpan start, TimeSpan end, IEnumerable<CaptionWord> words, string origin, string? language)
    {
        File = file;
        Start = start;
        End = end;
        Origin = origin;
        Language = language;
        Words = words.OrderBy(word => word.Start).ToList();
    }

    public string File { get; }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }

    /// <summary>Where the words came from, for example "speech recognition".</summary>
    public string Origin { get; }

    /// <summary>The language that was recognised, as a two-letter code, when known.</summary>
    public string? Language { get; }

    public List<CaptionWord> Words { get; }

    /// <summary>True once the user has corrected the text.</summary>
    public bool IsEdited { get; private set; }

    public bool Covers(string file, TimeSpan start, TimeSpan end)
        => string.Equals(File, file, StringComparison.OrdinalIgnoreCase)
           && start >= Start - Tolerance
           && end <= End + Tolerance;

    /// <summary>The words that fall inside a stretch of the file, as a position and a count in <see cref="Words"/>.</summary>
    public (int First, int Count) Range(TimeSpan start, TimeSpan end)
    {
        var first = -1;
        var count = 0;

        for (var index = 0; index < Words.Count; index++)
        {
            // A word belongs to the stretch that holds its middle, so a cut never shows half a word twice.
            var middle = Words[index].Start + (Words[index].End - Words[index].Start) / 2;
            if (middle < start)
                continue;
            if (middle >= end)
                break;

            if (first < 0)
                first = index;
            count++;
        }

        return (Math.Max(first, 0), count);
    }

    /// <summary>
    /// Replaces a run of words with new text. The same number of words keeps every timing; a
    /// different number shares the run's time between the new words by their length.
    /// </summary>
    public void ReplaceText(int first, int count, string text)
    {
        if (count <= 0 || first < 0 || first + count > Words.Count)
            return;

        var tokens = text.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);
        var replacement = new List<CaptionWord>(tokens.Length);

        if (tokens.Length == count)
        {
            for (var index = 0; index < count; index++)
                replacement.Add(Words[first + index] with { Text = tokens[index] });
        }
        else if (tokens.Length > 0)
        {
            replacement.AddRange(Spread(tokens, Words[first].Start, Words[first + count - 1].End));
        }

        if (replacement.Count == count && replacement.SequenceEqual(Words.GetRange(first, count)))
            return;

        Words.RemoveRange(first, count);
        Words.InsertRange(first, replacement);
        IsEdited = true;
    }

    /// <summary>Shares a time span between words, giving longer words more of it.</summary>
    public static IEnumerable<CaptionWord> Spread(IReadOnlyList<string> tokens, TimeSpan start, TimeSpan end)
    {
        if (tokens.Count == 0)
            yield break;

        if (end < start)
            end = start;

        double total = 0;
        foreach (var token in tokens)
            total += Weight(token);

        var span = (end - start).TotalSeconds;
        double used = 0;

        for (var index = 0; index < tokens.Count; index++)
        {
            var from = start + TimeSpan.FromSeconds(span * used / total);
            used += Weight(tokens[index]);
            var to = index == tokens.Count - 1 ? end : start + TimeSpan.FromSeconds(span * used / total);
            yield return new CaptionWord(tokens[index], from, to);
        }
    }

    private static double Weight(string token) => token.Length + 1;
}

/// <summary>The transcripts made or imported in this session, newest first.</summary>
public sealed class TranscriptStore
{
    private readonly List<Transcript> _items = new();

    /// <summary>The newest transcript that covers the given stretch of a file, if there is one.</summary>
    public Transcript? Find(string file, TimeSpan start, TimeSpan end)
    {
        foreach (var item in _items)
        {
            if (item.Covers(file, start, end))
                return item;
        }

        return null;
    }

    public void Add(Transcript transcript)
    {
        // A new transcript replaces older ones it makes redundant.
        _items.RemoveAll(item => transcript.Covers(item.File, item.Start, item.End));
        _items.Insert(0, transcript);
    }
}
