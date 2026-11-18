using System;
using System.Collections.Generic;
using System.Linq;
using ClipSplitterApp.Captions;
using SkiaSharp;

namespace ClipSplitterApp.TextOverlays;

/// <summary>One line of a text, placed on the frame.</summary>
public sealed class TextOverlayLine
{
    public TextOverlayLine(SKPath path, SKRect ink, float capTop, float baseline)
    {
        Path = path;
        Ink = ink;
        CapTop = capTop;
        Baseline = baseline;
    }

    /// <summary>The outline of the line's letters, already at their place on the frame.</summary>
    public SKPath Path { get; }

    /// <summary>The room the letters take, without outline, shadow or box.</summary>
    public SKRect Ink { get; }

    /// <summary>The top of a capital letter on this line.</summary>
    public float CapTop { get; }

    public float Baseline { get; }
}

/// <summary>
/// A text laid out for one short: where every line sits and how it looks. The preview and the
/// exporter both draw from this, so they always agree.
/// </summary>
public sealed class TextOverlayScene
{
    /// <summary>A text that leaves before the short ends takes this long to fade away.</summary>
    public static readonly TimeSpan FadeOut = TimeSpan.FromSeconds(0.3);

    public TextOverlayScene(
        TextOverlayStyle style,
        int width,
        int height,
        IReadOnlyList<TextOverlayLine> lines,
        float textSize,
        SKRect bounds,
        SKPath? box,
        SKRectI band,
        TimeSpan? showFor)
    {
        Style = style;
        Width = width;
        Height = height;
        Lines = lines;
        TextSize = textSize;
        Bounds = bounds;
        Box = box;
        Band = band;
        ShowFor = showFor;
    }

    public TextOverlayStyle Style { get; }

    public int Width { get; }

    public int Height { get; }

    public IReadOnlyList<TextOverlayLine> Lines { get; }

    /// <summary>The text size used, which is smaller than the style's when the text had to shrink to fit.</summary>
    public float TextSize { get; }

    /// <summary>The area the letters cover, without outline, shadow or box.</summary>
    public SKRect Bounds { get; }

    /// <summary>The shape of the box behind the lines, square-cornered; the painter rounds it. Null for styles without a box.</summary>
    public SKPath? Box { get; }

    /// <summary>The part of the frame the text is ever drawn in. Its edges sit on even pixels.</summary>
    public SKRectI Band { get; }

    /// <summary>How long the text stays from the start of the short before fading away, or null for the whole short.</summary>
    public TimeSpan? ShowFor { get; }

    public bool IsEmpty => Lines.Count == 0 || Band.Width <= 0 || Band.Height <= 0;

    /// <summary>The area that counts as the text when it is pointed at: the letters and their box, with a little room to grab.</summary>
    public SKRect GrabBounds
    {
        get
        {
            var padX = Style.Box != null ? Style.BoxPadX * TextSize : 0;
            var padY = Style.Box != null ? Style.BoxPadY * TextSize : 0;
            var least = TextSize * 0.2;
            return SKRect.Inflate(Bounds, (float)Math.Max(padX, least), (float)Math.Max(padY, least));
        }
    }

    /// <summary>
    /// How strongly the text shows at a moment: 1 is fully there, 0 is gone. This is the straight
    /// ramp ffmpeg's fade filter uses, so the preview fades exactly as the export does.
    /// </summary>
    public double OpacityAt(TimeSpan time)
    {
        if (ShowFor is not { } shown || time < shown)
            return 1;

        return Math.Clamp(1 - (time - shown) / FadeOut, 0, 1);
    }
}

/// <summary>Breaks a text into lines and places it on the frame.</summary>
public static class TextOverlayLayout
{
    /// <summary>The text never takes more of the frame's height than this, however much is written.</summary>
    private const double MaxHeight = 0.8;

    /// <param name="settings">What is written and how it should look.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="middleY">The line that <see cref="TextAnchor.Middle"/> centres on, or null for the middle of the frame.</param>
    /// <returns>The laid out text, or null when nothing is written.</returns>
    public static TextOverlayScene? Build(TextOverlaySettings settings, int width, int height, double? middleY = null)
    {
        var style = settings.ResolveStyle();
        var paragraphs = Paragraphs(settings.Text, style);
        if (paragraphs.Count == 0)
            return null;

        var font = CaptionFont.Get(style.FontId);
        var textSize = TextSizeFor(style, settings.Scale, width, height);
        var space = font.SpaceWidth(textSize);
        var boxed = style.Box != null;

        // The box counts towards the width the text may take, so it never reaches the frame's edge.
        var maxLineWidth = (float)Math.Max(textSize, width * style.MaxWidth - (boxed ? 2 * style.BoxPadX * textSize : 0));

        var rows = new List<List<Word>>();
        var shapes = new List<Word>();

        foreach (var words in paragraphs)
        {
            var shaped = words.Select(word => new Word(word, font.Shape(word, textSize))).ToList();
            shapes.AddRange(shaped);

            foreach (var (from, to) in Wrap(shaped.Select(word => word.Shape.Width).ToArray(), space, maxLineWidth))
                rows.Add(shaped.GetRange(from, to - from));
        }

        var rowWidths = rows.Select(row => row.Sum(word => word.Shape.Width) + space * (row.Count - 1)).ToArray();
        var widest = rowWidths.Max();

        // Lines hug the letters rather than the font's full line, which is mostly air.
        float InkTop(float size) => style.Uppercase ? font.CapHeight * size + size * 0.17f : font.Ascent * size * 0.86f;
        float InkBottom(float size) => style.Uppercase ? size * 0.17f : font.Descent * size * 0.86f;
        float BlockHeight(float size) => InkTop(size) + InkBottom(size) + (float)(size * style.LineHeight) * (rows.Count - 1);

        // A word too long for the line, or more lines than the frame has room for, shrinks the whole text.
        var roomY = (float)(height * MaxHeight - (boxed ? 2 * style.BoxPadY * textSize : 0));
        var fit = Math.Min(1f, Math.Min(maxLineWidth / Math.Max(1f, widest), roomY / Math.Max(1f, BlockHeight(textSize))));
        var size = textSize * fit;

        var inkTop = InkTop(size);
        var inkBottom = InkBottom(size);
        var lineAdvance = (float)(size * style.LineHeight);
        var blockWidth = widest * fit;
        var blockHeight = BlockHeight(size);

        var padX = boxed ? (float)(style.BoxPadX * size) : 0;

        // The boxes of neighbouring lines always meet, so that they join into one shape.
        var padY = boxed ? Math.Max((float)(style.BoxPadY * size), (lineAdvance - inkTop - inkBottom) / 2 + 0.5f) : 0;

        var centre = Place(settings, blockWidth + 2 * padX, blockHeight + 2 * padY, width, height, middleY);
        var top = centre.Y - blockHeight / 2;

        var lines = new List<TextOverlayLine>(rows.Count);
        var bounds = SKRect.Empty;

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var rowWidth = rowWidths[index] * fit;
            var baseline = top + inkTop + lineAdvance * index;
            var pen = centre.X - rowWidth / 2;
            var path = new SKPath();

            // Right-to-left scripts fill the line from its far end.
            var ordered = CaptionLayout.IsRightToLeft(row[0].Text) ? Enumerable.Reverse(row) : row;
            foreach (var word in ordered)
            {
                using var letters = new SKPath(word.Shape.Path);
                var matrix = SKMatrix.CreateScale(fit, fit);
                matrix = matrix.PostConcat(SKMatrix.CreateTranslation(pen, baseline));
                letters.Transform(matrix);
                path.AddPath(letters);
                pen += (word.Shape.Width + space) * fit;
            }

            var ink = new SKRect(centre.X - rowWidth / 2, baseline - inkTop, centre.X + rowWidth / 2, baseline + inkBottom);
            lines.Add(new TextOverlayLine(path, ink, baseline - font.CapHeight * size, baseline));
            bounds = bounds.IsEmpty ? ink : SKRect.Union(bounds, ink);
        }

        foreach (var word in shapes)
            word.Shape.Path.Dispose();

        var box = boxed ? BuildBox(lines, padX, padY, (float)(style.BoxRadius * size)) : null;
        var drawn = DrawnBounds(bounds, style, size, padX, padY);
        var showFor = settings.ShowSeconds is { } seconds && double.IsFinite(seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : (TimeSpan?)null;

        return new TextOverlayScene(style, width, height, lines, size, bounds, box, CaptionLayout.ToBand(drawn, width, height), showFor);
    }

    /// <summary>The text size in pixels for a frame. Wide frames are measured as if they were narrower, so text does not grow with them.</summary>
    private static float TextSizeFor(TextOverlayStyle style, double scale, int width, int height)
    {
        var reference = Math.Min(width, height * 0.75);
        scale = double.IsFinite(scale) ? Math.Clamp(scale, TextOverlaySettings.MinScale, TextOverlaySettings.MaxScale) : 1;
        return (float)Math.Max(8, reference * style.Size * scale);
    }

    /// <summary>The words of every line the user wrote, in the capitals the style asks for. Empty lines are left out.</summary>
    private static List<string[]> Paragraphs(string? text, TextOverlayStyle style)
    {
        var paragraphs = new List<string[]>();
        if (string.IsNullOrWhiteSpace(text))
            return paragraphs;

        if (text.Length > TextOverlaySettings.MaxLength)
            text = text[..TextOverlaySettings.MaxLength];

        foreach (var line in text.Split('\n', '\r'))
        {
            var words = (style.Uppercase ? line.ToUpperInvariant() : line)
                .Split(new[] { ' ', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (words.Length > 0)
                paragraphs.Add(words);
        }

        return paragraphs;
    }

    /// <summary>
    /// Breaks words into as few lines as will fit, then narrows the lines as far as they go
    /// without needing another one. That leaves lines of similar length instead of full lines
    /// followed by a leftover word.
    /// </summary>
    private static List<(int From, int To)> Wrap(float[] widths, float space, float maxWidth)
    {
        var best = Fill(widths, space, maxWidth);
        if (best.Count <= 1)
            return best;

        var low = widths.Max();
        var high = maxWidth;

        for (var step = 0; step < 14 && high - low > 1; step++)
        {
            var middle = (low + high) / 2;
            var attempt = Fill(widths, space, middle);

            if (attempt.Count <= best.Count)
            {
                best = attempt;
                high = middle;
            }
            else
            {
                low = middle;
            }
        }

        return best;
    }

    /// <summary>Fills each line as far as it goes. A word wider than the limit gets a line of its own.</summary>
    private static List<(int From, int To)> Fill(float[] widths, float space, float limit)
    {
        var lines = new List<(int From, int To)>();
        var from = 0;
        float used = 0;

        for (var index = 0; index < widths.Length; index++)
        {
            var needed = index == from ? widths[index] : used + space + widths[index];
            if (index > from && needed > limit)
            {
                lines.Add((from, index));
                from = index;
                needed = widths[index];
            }

            used = needed;
        }

        lines.Add((from, widths.Length));
        return lines;
    }

    /// <summary>Works out where the middle of the text goes, keeping all of it inside the frame.</summary>
    private static SKPoint Place(TextOverlaySettings settings, float outerWidth, float outerHeight, int width, int height, double? middleY)
    {
        // Tall frames are watched in apps that cover the top and bottom with their own buttons
        // and text, so a text keeps clear of those areas unless it is dragged there.
        var tall = (double)width / height < 0.65;
        var topSafe = tall ? 0.11 : 0.07;
        var bottomSafe = tall ? 0.2 : 0.08;

        double x = width / 2d;
        double y = settings.Anchor switch
        {
            TextAnchor.Top => height * topSafe + outerHeight / 2,
            TextAnchor.Bottom => height * (1 - bottomSafe) - outerHeight / 2,
            TextAnchor.Custom => Fraction(settings.CentreY) * height,
            _ => middleY ?? height / 2d
        };

        if (settings.Anchor == TextAnchor.Custom)
            x = Fraction(settings.CentreX) * width;

        var margin = Math.Min(width, height) * 0.02;
        return new SKPoint(
            (float)Within(x, margin + outerWidth / 2, width - margin - outerWidth / 2, width / 2d),
            (float)Within(y, margin + outerHeight / 2, height - margin - outerHeight / 2, height / 2d));
    }

    private static double Fraction(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.5;

    /// <summary>Clamps a value, or returns <paramref name="fallback"/> when there is no room to clamp to.</summary>
    private static double Within(double value, double least, double most, double fallback)
        => least > most ? fallback : Math.Clamp(value, least, most);

    /// <summary>The boxes behind the lines, joined into one outline.</summary>
    private static SKPath BuildBox(List<TextOverlayLine> lines, float padX, float padY, float radius)
    {
        var rects = lines.Select(line => SKRect.Inflate(line.Ink, padX, padY)).ToArray();

        // A step between two lines needs room for two rounded corners. Lines closer in width
        // than that share the wider edge, or the step would be a sliver.
        var least = radius * 2;
        for (var pass = 0; pass < rects.Length; pass++)
        {
            var changed = false;
            for (var index = 1; index < rects.Length; index++)
            {
                var a = rects[index - 1];
                var b = rects[index];
                var step = Math.Abs(a.Left - b.Left);
                if (step < 0.01f || step >= least)
                    continue;

                var left = Math.Min(a.Left, b.Left);
                var right = Math.Max(a.Right, b.Right);
                rects[index - 1] = new SKRect(left, a.Top, right, a.Bottom);
                rects[index] = new SKRect(left, b.Top, right, b.Bottom);
                changed = true;
            }

            if (!changed)
                break;
        }

        var box = new SKPath();
        box.AddRect(rects[0]);

        for (var index = 1; index < rects.Length; index++)
        {
            using var next = new SKPath();
            next.AddRect(rects[index]);

            if (box.Op(next, SKPathOp.Union) is { } joined)
            {
                box.Dispose();
                box = joined;
            }
            else
            {
                box.AddRect(rects[index]);
            }
        }

        return box;
    }

    /// <summary>Everything the text can draw: the letters plus their outline, shadow, glow and box.</summary>
    private static SKRect DrawnBounds(SKRect bounds, TextOverlayStyle style, float size, float padX, float padY)
    {
        var outer = SKRect.Inflate(bounds, padX, padY);

        var shadow = (float)((style.ShadowOffset + style.ShadowBlur * 3) * size);
        var glow = style.Glow != null ? (float)(style.GlowBlur * 3 * size + size * 0.05) : 0;
        var edge = (float)(style.Outline * size) + Math.Max(shadow, glow) + 4;

        var tilt = (float)Math.Abs(Math.Sin(style.Tilt * Math.PI / 180));
        return SKRect.Inflate(outer, edge + tilt * outer.Height / 2, edge + tilt * outer.Width / 2);
    }

    private readonly record struct Word(string Text, ShapedText Shape);
}
