using System;
using SkiaSharp;

namespace ClipSplitterApp.Captions;

/// <summary>
/// Draws a caption scene as it looks at one moment. The preview canvas and the exporter both
/// call this, which is what makes the exported captions match the preview.
/// </summary>
public static class CaptionPainter
{
    /// <summary>Returned by <see cref="StateAt"/> when nothing is on screen.</summary>
    public const long Blank = -1;

    /// <summary>Returned by <see cref="StateAt"/> while an animation is running, when every frame differs.</summary>
    public const long Moving = -2;

    private const double EntranceSeconds = 0.14;
    private const double ActiveSeconds = 0.1;

    /// <summary>
    /// Identifies what the scene looks like at a moment. Two moments with the same value, other
    /// than <see cref="Moving"/>, draw exactly the same picture.
    /// </summary>
    public static long StateAt(CaptionScene scene, TimeSpan time)
    {
        var page = scene.FindPage(time);
        if (page == null)
            return Blank;

        var style = scene.Style;
        if (style.Entrance != CaptionEntrance.None && (time - page.Start).TotalSeconds < EntranceSeconds)
            return Moving;

        var active = ActiveWord(page, time);
        if (active >= 0 && style.ActiveScale != 1 && (time - page.Words[active].Start).TotalSeconds < ActiveSeconds)
            return Moving;

        return ((long)page.Index << 16) | (uint)(active + 1);
    }

    /// <summary>Draws the scene at <paramref name="time"/>, in frame pixels.</summary>
    public static void Draw(SKCanvas canvas, CaptionScene scene, TimeSpan time)
    {
        var page = scene.FindPage(time);
        if (page == null || page.Words.Count == 0)
            return;

        var style = scene.Style;
        var size = page.TextSize;
        var bounds = page.Bounds;
        var centre = new SKPoint(bounds.MidX, bounds.MidY);
        var active = ActiveWord(page, time);

        var entrance = style.Entrance == CaptionEntrance.None
            ? 1
            : Math.Clamp((time - page.Start).TotalSeconds / EntranceSeconds, 0, 1);

        var opacity = 1d;
        canvas.Save();

        switch (style.Entrance)
        {
            case CaptionEntrance.Pop:
            {
                var grown = (float)(0.84 + 0.16 * EaseOutBack(entrance));
                canvas.Scale(grown, grown, centre.X, centre.Y);
                opacity = Math.Min(1, entrance * 3);
                break;
            }

            case CaptionEntrance.Rise:
                canvas.Translate(0, (float)((1 - EaseOutCubic(entrance)) * size * 0.45));
                opacity = entrance;
                break;

            case CaptionEntrance.Fade:
                opacity = entrance;
                break;
        }

        if (style.Tilt != 0)
            canvas.RotateDegrees((float)style.Tilt, centre.X, centre.Y);

        var fading = opacity < 0.999;
        if (fading)
        {
            using var layer = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(opacity * 255)) };
            canvas.SaveLayer(layer);
        }

        DrawBoxes(canvas, page, style, size, active);
        DrawWords(canvas, page, style, size, active, time);

        if (fading)
            canvas.Restore();

        canvas.Restore();
    }

    private static void DrawBoxes(SKCanvas canvas, CaptionPage page, CaptionStyle style, float size, int active)
    {
        var padX = (float)(style.BoxPadX * size);
        var padY = (float)(style.BoxPadY * size);
        var radius = (float)(style.BoxRadius * size);

        if (style.PageBox is { } pageColor)
        {
            using var paint = new SKPaint { Color = pageColor, IsAntialias = true };
            canvas.DrawRoundRect(SKRect.Inflate(page.Bounds, padX, padY), radius, radius, paint);
        }

        if (style.ActiveBox is { } activeColor && active >= 0)
        {
            using var paint = new SKPaint { Color = activeColor, IsAntialias = true };
            canvas.DrawRoundRect(SKRect.Inflate(page.Words[active].Cell, padX, padY), radius, radius, paint);
        }
    }

    private static void DrawWords(SKCanvas canvas, CaptionPage page, CaptionStyle style, float size, int active, TimeSpan time)
    {
        var outline = (float)(style.Outline * size);
        var grows = active >= 0 && style.ActiveScale != 1;

        // Each layer is finished for every word before the next begins, so one word's outline
        // never covers its neighbour's letters.
        if (style.Glow is { } glow && style.GlowBlur > 0)
        {
            using var paint = new SKPaint
            {
                Color = glow,
                IsAntialias = true,
                Style = SKPaintStyle.StrokeAndFill,
                StrokeWidth = size * 0.05f,
                StrokeJoin = SKStrokeJoin.Round,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)(style.GlowBlur * size))
            };

            // Two passes build a bright core with a soft halo around it.
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var word in page.Words)
                    canvas.DrawPath(word.Path, paint);
            }
        }

        if (style.ShadowOffset > 0 || style.ShadowBlur > 0)
        {
            using var paint = new SKPaint
            {
                Color = style.ShadowColor,
                IsAntialias = true,
                Style = outline > 0 ? SKPaintStyle.StrokeAndFill : SKPaintStyle.Fill,
                StrokeWidth = outline * 2,
                StrokeJoin = SKStrokeJoin.Round
            };

            if (style.ShadowBlur > 0)
                paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)(style.ShadowBlur * size));

            var offset = (float)(style.ShadowOffset * size);
            canvas.Save();
            canvas.Translate(offset, offset);
            for (var index = 0; index < page.Words.Count; index++)
            {
                if (!grows || index != active)
                    canvas.DrawPath(page.Words[index].Path, paint);
            }

            canvas.Restore();
        }

        using var stroke = new SKPaint
        {
            Color = style.OutlineColor,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = outline * 2,
            StrokeJoin = SKStrokeJoin.Round
        };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

        if (outline > 0)
        {
            for (var index = 0; index < page.Words.Count; index++)
            {
                if (!grows || index != active)
                    canvas.DrawPath(page.Words[index].Path, stroke);
            }
        }

        for (var index = 0; index < page.Words.Count; index++)
        {
            if (grows && index == active)
                continue;

            fill.Color = FillFor(style, index, active);
            canvas.DrawPath(page.Words[index].Path, fill);
        }

        if (!grows)
            return;

        // The word being spoken is drawn last and a little larger, on top of its neighbours.
        // A long word grows by less, so that it never closes the gaps to the words beside it.
        var spoken = page.Words[active];
        var room = page.WordGap * 0.35f;
        var most = 1 + 2 * room / Math.Max(1f, spoken.Cell.Width);
        var grown = Math.Max(1, Math.Min(style.ActiveScale, most));
        var progress = Math.Clamp((time - spoken.Start).TotalSeconds / ActiveSeconds, 0, 1);
        var scale = (float)(1 + (grown - 1) * EaseOutBack(progress));

        canvas.Save();
        canvas.Scale(scale, scale, spoken.Cell.MidX, spoken.Cell.MidY);

        if (style.ShadowOffset > 0 || style.ShadowBlur > 0)
        {
            using var shadow = new SKPaint
            {
                Color = style.ShadowColor,
                IsAntialias = true,
                Style = outline > 0 ? SKPaintStyle.StrokeAndFill : SKPaintStyle.Fill,
                StrokeWidth = outline * 2,
                StrokeJoin = SKStrokeJoin.Round
            };

            if (style.ShadowBlur > 0)
                shadow.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)(style.ShadowBlur * size));

            var offset = (float)(style.ShadowOffset * size);
            canvas.Save();
            canvas.Translate(offset, offset);
            canvas.DrawPath(spoken.Path, shadow);
            canvas.Restore();
        }

        if (outline > 0)
            canvas.DrawPath(spoken.Path, stroke);

        fill.Color = FillFor(style, active, active);
        canvas.DrawPath(spoken.Path, fill);
        canvas.Restore();
    }

    private static SKColor FillFor(CaptionStyle style, int index, int active)
    {
        if (index == active)
            return style.ActiveFill ?? style.SpokenFill ?? style.Fill;

        return index < active ? style.SpokenFill ?? style.Fill : style.Fill;
    }

    /// <summary>The word being spoken at the given time, or -1 before the first one.</summary>
    private static int ActiveWord(CaptionPage page, TimeSpan time)
    {
        var active = -1;
        for (var index = 0; index < page.Words.Count; index++)
        {
            if (time >= page.Words[index].Start)
                active = index;
            else
                break;
        }

        return active;
    }

    private static double EaseOutCubic(double value) => 1 - Math.Pow(1 - value, 3);

    /// <summary>Overshoots slightly before settling, which gives a pop its bounce.</summary>
    private static double EaseOutBack(double value)
    {
        const double overshoot = 1.70158;
        var shifted = value - 1;
        return 1 + (overshoot + 1) * shifted * shifted * shifted + overshoot * shifted * shifted;
    }
}
