using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace ClipSplitterApp.Captions;

/// <summary>A word placed on the frame.</summary>
public sealed class CaptionGlyphs
{
    public CaptionGlyphs(string text, SKPath path, SKRect cell, TimeSpan start, TimeSpan end)
    {
        Text = text;
        Path = path;
        Cell = cell;
        Start = start;
        End = end;
    }

    public string Text { get; }

    /// <summary>The word's outline, already at its place on the frame.</summary>
    public SKPath Path { get; }

    /// <summary>The room the word takes on its line.</summary>
    public SKRect Cell { get; }

    /// <summary>When the word starts being the one that is spoken.</summary>
    public TimeSpan Start { get; }

    /// <summary>When the next word takes over.</summary>
    public TimeSpan End { get; }
}

/// <summary>A group of words that is on screen together.</summary>
public sealed class CaptionPage
{
    public CaptionPage(int index, TimeSpan start, TimeSpan end, IReadOnlyList<CaptionGlyphs> words, SKRect bounds, float textSize, float wordGap, int firstWord, int wordCount)
    {
        Index = index;
        Start = start;
        End = end;
        Words = words;
        Bounds = bounds;
        TextSize = textSize;
        WordGap = wordGap;
        FirstWord = firstWord;
        WordCount = wordCount;
    }

    public int Index { get; }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }

    public IReadOnlyList<CaptionGlyphs> Words { get; }

    /// <summary>The area the words cover, without outline, shadow or box.</summary>
    public SKRect Bounds { get; }

    /// <summary>The text size used, which is smaller than the style's when a long word had to shrink.</summary>
    public float TextSize { get; }

    /// <summary>The space between two words on a line.</summary>
    public float WordGap { get; }

    /// <summary>Position of the page's first word in the list the scene was built from.</summary>
    public int FirstWord { get; }

    /// <summary>How many words of that list the page shows.</summary>
    public int WordCount { get; }
}

/// <summary>
/// Captions laid out for one short: every page, where each word sits, and when. The preview and
/// the exporter both draw from this, so they always agree.
/// </summary>
public sealed class CaptionScene
{
    public CaptionScene(CaptionStyle style, int width, int height, IReadOnlyList<CaptionPage> pages, SKRectI band)
    {
        Style = style;
        Width = width;
        Height = height;
        Pages = pages;
        Band = band;
    }

    public CaptionStyle Style { get; }

    public int Width { get; }

    public int Height { get; }

    public IReadOnlyList<CaptionPage> Pages { get; }

    /// <summary>The part of the frame captions are ever drawn in. Its edges sit on even pixels.</summary>
    public SKRectI Band { get; }

    public bool IsEmpty => Pages.Count == 0 || Band.Width <= 0 || Band.Height <= 0;

    /// <summary>The page on screen at the given time, if any.</summary>
    public CaptionPage? FindPage(TimeSpan time)
    {
        int low = 0, high = Pages.Count - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var page = Pages[middle];

            if (time < page.Start)
                high = middle - 1;
            else if (time >= page.End)
                low = middle + 1;
            else
                return page;
        }

        return null;
    }
}

/// <summary>Groups words into pages, breaks them into lines and places them on the frame.</summary>
public static class CaptionLayout
{
    /// <summary>A silence this long starts a new page, whatever the words around it.</summary>
    private const double PauseBreakSeconds = 0.45;

    /// <summary>A silence this long after a comma starts a new page.</summary>
    private const double ClausePauseSeconds = 0.18;
    private const double JoinGapSeconds = 0.45;
    private const double LingerSeconds = 0.3;
    private const double MinPageSeconds = 0.06;

    /// <param name="words">The words to show, timed from the start of the short.</param>
    /// <param name="duration">The length of the short. Words after it are left out.</param>
    /// <param name="style">The look, with the user's colour and capitals already applied.</param>
    /// <param name="position">Where on the frame the captions sit.</param>
    /// <param name="scale">Text size relative to the style's own.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="middleY">The line that <see cref="CaptionPosition.Middle"/> centres on, or null for the middle of the frame.</param>
    public static CaptionScene Build(
        IReadOnlyList<CaptionWord> words,
        TimeSpan duration,
        CaptionStyle style,
        CaptionPosition position,
        double scale,
        int width,
        int height,
        double? middleY = null)
    {
        var font = CaptionFont.Get(style.FontId);
        var textSize = TextSizeFor(style, scale, width, height);
        var maxLineWidth = (float)(width * style.MaxWidth);

        var items = Measure(words, duration, style, font, textSize);
        var groups = Paginate(items, style, font.SpaceWidth(textSize), maxLineWidth);

        var pages = new List<CaptionPage>(groups.Count);
        var bandBounds = SKRect.Empty;

        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            var start = group[0].Start;
            var end = PageEnd(group, index + 1 < groups.Count ? groups[index + 1][0].Start : (TimeSpan?)null, duration);
            if (end - start < TimeSpan.FromSeconds(MinPageSeconds))
                continue;

            var page = Place(pages.Count, group, start, end, style, font, textSize, maxLineWidth, position, width, height, middleY);
            pages.Add(page);

            var drawn = DrawnBounds(page, style);
            bandBounds = bandBounds.IsEmpty ? drawn : SKRect.Union(bandBounds, drawn);
        }

        foreach (var item in items)
            item.Shape.Path.Dispose();

        return new CaptionScene(style, width, height, pages, ToBand(bandBounds, width, height));
    }

    /// <summary>The text size in pixels for a frame. Wide frames are measured as if they were narrower, so text does not grow with them.</summary>
    public static float TextSizeFor(CaptionStyle style, double scale, int width, int height)
    {
        var reference = Math.Min(width, height * 0.75);
        scale = Math.Clamp(scale, CaptionSettings.MinScale, CaptionSettings.MaxScale);
        return (float)Math.Max(8, reference * style.Size * scale);
    }

    private static List<Item> Measure(IReadOnlyList<CaptionWord> words, TimeSpan duration, CaptionStyle style, CaptionFont font, float textSize)
    {
        var items = new List<Item>(words.Count);
        var previousStart = TimeSpan.Zero;

        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            var start = word.Start < previousStart ? previousStart : word.Start;
            if (start >= duration)
                break;

            var raw = word.Text.Trim();
            var text = Display(raw, style);
            if (text.Length == 0)
                continue;

            var end = word.End > duration ? duration : word.End;
            if (end < start)
                end = start;

            previousStart = start;
            items.Add(new Item(index, text, font.Shape(text, textSize), start, end, EndsSentence(raw), EndsClause(raw)));
        }

        return items;
    }

    /// <summary>The text as the style shows it: capitals where asked, and without the commas and full stops of a transcript.</summary>
    private static string Display(string raw, CaptionStyle style)
    {
        var text = raw;
        if (!style.KeepPunctuation)
        {
            text = text.TrimEnd('.', ',', ';', ':', '。', '、', '，').TrimStart('¿', '¡');
            if (text.Length == 0)
                return string.Empty;
        }

        return style.Uppercase ? text.ToUpperInvariant() : text;
    }

    private static bool EndsSentence(string raw)
        => raw.Length > 0 && raw[^1] is '.' or '!' or '?' or '…' or '。' or '！' or '？';

    private static bool EndsClause(string raw)
        => raw.Length > 0 && raw[^1] is ',' or ';' or ':' or '、' or '，' or '；' or '：';

    /// <summary>
    /// Groups words into pages. Speech is first cut into phrases, at the ends of sentences and
    /// wherever the speaker pauses, so a page never straddles a pause; each phrase is then
    /// shared out over as few pages as it needs.
    /// </summary>
    private static List<List<Item>> Paginate(List<Item> items, CaptionStyle style, float space, float maxLineWidth)
    {
        var groups = new List<List<Item>>();
        var phrase = new List<Item>();
        var maxWords = Math.Max(1, style.MaxWords);
        var maxLines = Math.Max(1, style.MaxLines);

        foreach (var item in items)
        {
            if (phrase.Count > 0 && EndsPhrase(phrase[^1], item))
            {
                AddPhrase(groups, phrase, maxWords, maxLines, space, maxLineWidth);
                phrase = new List<Item>();
            }

            phrase.Add(item);
        }

        if (phrase.Count > 0)
            AddPhrase(groups, phrase, maxWords, maxLines, space, maxLineWidth);

        return groups;
    }

    private static bool EndsPhrase(Item previous, Item next)
    {
        if (previous.EndsSentence)
            return true;

        // A comma only ends a phrase when the speaker actually pauses at it.
        var gap = (next.Start - previous.End).TotalSeconds;
        return gap > PauseBreakSeconds || (previous.EndsClause && gap >= ClausePauseSeconds);
    }

    private static void AddPhrase(List<List<Item>> groups, List<Item> phrase, int maxWords, int maxLines, float space, float maxLineWidth)
    {
        // Pages of even length read better than full pages followed by a leftover word.
        var pages = (phrase.Count + maxWords - 1) / maxWords;
        var even = new List<List<Item>>(pages);
        var taken = 0;

        for (var page = 0; page < pages; page++)
        {
            var size = phrase.Count / pages + (page < phrase.Count % pages ? 1 : 0);
            even.Add(phrase.GetRange(taken, size));
            taken += size;
        }

        if (even.All(page => page.Count == 1 || Fits(page, space, maxLineWidth, maxLines)))
        {
            groups.AddRange(even);
            return;
        }

        // Words too wide to be shared out evenly fill each page as far as it goes instead.
        var current = new List<Item>();
        foreach (var item in phrase)
        {
            if (current.Count > 0)
            {
                current.Add(item);
                if (current.Count <= maxWords && Fits(current, space, maxLineWidth, maxLines))
                    continue;

                current.RemoveAt(current.Count - 1);
                groups.Add(current);
                current = new List<Item>();
            }

            current.Add(item);
        }

        if (current.Count > 0)
            groups.Add(current);
    }

    private static bool Fits(List<Item> page, float space, float maxLineWidth, int maxLines)
    {
        var widths = new float[page.Count];
        for (var index = 0; index < page.Count; index++)
            widths[index] = page[index].Shape.Width;

        return BreakLines(widths, space, maxLineWidth, maxLines, out _) <= maxLineWidth;
    }

    /// <summary>
    /// Splits words into as few lines as will fit, keeping the lines as even as possible.
    /// Returns the width of the widest line; <paramref name="lineStarts"/> holds the position of
    /// the first word of each line.
    /// </summary>
    private static float BreakLines(float[] widths, float space, float maxLineWidth, int maxLines, out int[] lineStarts)
    {
        var best = float.MaxValue;
        lineStarts = new[] { 0 };

        for (var lines = 1; lines <= Math.Min(maxLines, widths.Length); lines++)
        {
            var starts = new int[lines];
            var candidate = new int[lines];
            var widest = float.MaxValue;
            Search(widths, space, 1, starts, candidate, ref widest);

            if (widest < best)
            {
                best = widest;
                lineStarts = candidate;
            }

            if (best <= maxLineWidth)
                break;
        }

        return best;
    }

    /// <summary>Tries every way of starting the remaining lines and keeps the one whose widest line is narrowest.</summary>
    private static void Search(float[] widths, float space, int line, int[] starts, int[] bestStarts, ref float bestWidest)
    {
        if (line == starts.Length)
        {
            float widest = 0;
            for (var index = 0; index < starts.Length; index++)
            {
                var from = starts[index];
                var to = index + 1 < starts.Length ? starts[index + 1] : widths.Length;
                widest = Math.Max(widest, LineWidth(widths, space, from, to));
            }

            if (widest < bestWidest)
            {
                bestWidest = widest;
                Array.Copy(starts, bestStarts, starts.Length);
            }

            return;
        }

        // Each later line needs at least one word, which bounds where this one may start.
        for (var start = starts[line - 1] + 1; start <= widths.Length - (starts.Length - line); start++)
        {
            starts[line] = start;
            Search(widths, space, line + 1, starts, bestStarts, ref bestWidest);
        }
    }

    private static float LineWidth(float[] widths, float space, int from, int to)
    {
        float total = 0;
        for (var index = from; index < to; index++)
            total += widths[index];

        return total + space * Math.Max(0, to - from - 1);
    }

    private static TimeSpan PageEnd(List<Item> group, TimeSpan? nextStart, TimeSpan duration)
    {
        var spoken = group[^1].End;
        var end = spoken + TimeSpan.FromSeconds(LingerSeconds);

        if (nextStart is { } next)
        {
            // Pages that follow closely hand over without a blink; otherwise the caption lingers
            // a moment and clears.
            if ((next - spoken).TotalSeconds <= JoinGapSeconds || end > next)
                end = next;
        }

        return end > duration ? duration : end;
    }

    private static CaptionPage Place(
        int pageIndex,
        List<Item> group,
        TimeSpan start,
        TimeSpan end,
        CaptionStyle style,
        CaptionFont font,
        float textSize,
        float maxLineWidth,
        CaptionPosition position,
        int width,
        int height,
        double? middleY)
    {
        var widths = group.Select(item => item.Shape.Width).ToArray();
        var space = font.SpaceWidth(textSize);
        var widest = BreakLines(widths, space, maxLineWidth, Math.Max(1, style.MaxLines), out var lineStarts);

        // A word too long for the line shrinks the whole page until it fits.
        var shrink = widest > maxLineWidth ? maxLineWidth / widest : 1f;
        var size = textSize * shrink;
        space *= shrink;

        var ascent = font.Ascent * size;
        var descent = font.Descent * size;
        var lineAdvance = (float)(size * style.LineHeight);
        var blockHeight = ascent + descent + lineAdvance * (lineStarts.Length - 1);
        var top = BlockTop(position, blockHeight, width, height, middleY);

        // Captions for boxes hug the letters rather than the font's full line, which is mostly air.
        var inkTop = style.Uppercase ? font.CapHeight * size + size * 0.17f : ascent * 0.86f;
        var inkBottom = style.Uppercase ? size * 0.17f : descent * 0.86f;

        var placed = new List<CaptionGlyphs>(group.Count);
        var bounds = SKRect.Empty;
        var rightToLeft = IsRightToLeft(group[0].Text);

        for (var line = 0; line < lineStarts.Length; line++)
        {
            var from = lineStarts[line];
            var to = line + 1 < lineStarts.Length ? lineStarts[line + 1] : group.Count;
            var lineWidth = LineWidth(widths, space / shrink, from, to) * shrink;
            var baseline = top + ascent + lineAdvance * line;
            var pen = (width - lineWidth) / 2f;

            for (var step = 0; step < to - from; step++)
            {
                // Right-to-left scripts fill the line from its far end.
                var index = rightToLeft ? to - 1 - step : from + step;
                var item = group[index];
                var wordWidth = item.Shape.Width * shrink;

                var path = new SKPath(item.Shape.Path);
                var matrix = SKMatrix.CreateScale(shrink, shrink);
                matrix = matrix.PostConcat(SKMatrix.CreateTranslation(pen, baseline));
                path.Transform(matrix);

                var cell = new SKRect(pen, baseline - inkTop, pen + wordWidth, baseline + inkBottom);
                var activeFrom = index == 0 ? start : item.Start;
                var activeTo = index + 1 < group.Count ? group[index + 1].Start : end;
                if (activeTo < activeFrom)
                    activeTo = activeFrom;

                placed.Add(new CaptionGlyphs(item.Text, path, cell, activeFrom, activeTo));
                bounds = bounds.IsEmpty ? cell : SKRect.Union(bounds, cell);
                pen += wordWidth + space;
            }
        }

        // Words are kept in the order they are spoken, whatever order they were placed in.
        placed.Sort((a, b) => a.Start.CompareTo(b.Start));

        return new CaptionPage(pageIndex, start, end, placed, bounds, size, space, group[0].SourceIndex, group[^1].SourceIndex - group[0].SourceIndex + 1);
    }

    private static float BlockTop(CaptionPosition position, float blockHeight, int width, int height, double? middleY)
    {
        // Tall frames are watched in apps that cover the top and bottom with their own buttons
        // and text, so captions keep clear of those areas.
        var tall = (double)width / height < 0.65;
        var topSafe = tall ? 0.11 : 0.07;
        var bottomSafe = tall ? 0.2 : 0.08;

        var top = position switch
        {
            CaptionPosition.Top => height * topSafe,
            CaptionPosition.Bottom => height * (1 - bottomSafe) - blockHeight,
            _ => (middleY ?? height / 2d) - blockHeight / 2d
        };

        var margin = height * 0.02;
        return (float)Math.Clamp(top, margin, Math.Max(margin, height - blockHeight - margin));
    }

    /// <summary>Everything a page can draw: the words plus their outline, shadow, glow, box and the room animations take.</summary>
    private static SKRect DrawnBounds(CaptionPage page, CaptionStyle style)
    {
        var size = page.TextSize;
        var bounds = page.Bounds;

        var boxX = style.PageBox != null || style.ActiveBox != null ? (float)(style.BoxPadX * size) : 0;
        var boxY = style.PageBox != null || style.ActiveBox != null ? (float)(style.BoxPadY * size) : 0;
        var shadow = (float)((style.ShadowOffset + style.ShadowBlur * 3) * size);
        var glow = style.Glow != null ? (float)(style.GlowBlur * 3 * size) : 0;
        var edge = (float)(style.Outline * size) + Math.Max(shadow, glow) + 4;

        // Popping in and the growing active word both reach past the resting size.
        var growX = bounds.Width * 0.07f + size * 0.1f;
        var growY = bounds.Height * 0.07f + size * 0.3f;

        var tilt = (float)Math.Abs(Math.Sin(style.Tilt * Math.PI / 180));
        var tiltX = tilt * bounds.Height / 2;
        var tiltY = tilt * bounds.Width / 2;

        return SKRect.Inflate(bounds, boxX + edge + growX + tiltX, boxY + edge + growY + tiltY);
    }

    private static SKRectI ToBand(SKRect bounds, int width, int height)
    {
        if (bounds.IsEmpty)
            return SKRectI.Empty;

        // Even edges keep the band aligned with the colour samples of the video it is laid over.
        var left = Math.Clamp((int)Math.Floor(bounds.Left) / 2 * 2, 0, width);
        var top = Math.Clamp((int)Math.Floor(bounds.Top) / 2 * 2, 0, height);
        var right = Math.Clamp(((int)Math.Ceiling(bounds.Right) + 1) / 2 * 2, 0, width);
        var bottom = Math.Clamp(((int)Math.Ceiling(bounds.Bottom) + 1) / 2 * 2, 0, height);

        return right - left < 2 || bottom - top < 2 ? SKRectI.Empty : new SKRectI(left, top, right, bottom);
    }

    private static bool IsRightToLeft(string text)
    {
        foreach (var character in text)
        {
            if (character is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= 'ﻼ')
                return true;
            if (char.IsLetter(character))
                return false;
        }

        return false;
    }

    private sealed record Item(int SourceIndex, string Text, ShapedText Shape, TimeSpan Start, TimeSpan End, bool EndsSentence, bool EndsClause);
}
