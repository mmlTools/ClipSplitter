using System;
using System.IO;
using SkiaSharp;

namespace ClipSplitterApp.TextOverlays;

/// <summary>
/// Draws a laid out text. The preview canvas and the exporter both call this, which is what
/// makes the text in the exported short match the preview.
/// </summary>
public static class TextOverlayPainter
{
    /// <summary>Draws the text in frame pixels.</summary>
    /// <param name="opacity">How strongly the text shows, from 0 to 1.</param>
    public static void Draw(SKCanvas canvas, TextOverlayScene scene, double opacity = 1)
    {
        if (scene.IsEmpty || opacity <= 0.001)
            return;

        var style = scene.Style;
        var size = scene.TextSize;
        var outline = (float)(style.Outline * size);

        canvas.Save();

        if (style.Tilt != 0)
            canvas.RotateDegrees((float)style.Tilt, scene.Bounds.MidX, scene.Bounds.MidY);

        var fading = opacity < 0.999;
        if (fading)
        {
            using var layer = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(opacity * 255)) };
            canvas.SaveLayer(layer);
        }

        if (scene.Box is { } box && style.Box is { } boxColor)
        {
            // Rounding the joined outline rounds the inner corners between lines as well as the outer ones.
            using var corners = SKPathEffect.CreateCorner((float)(style.BoxRadius * size));
            using var paint = new SKPaint { Color = boxColor, IsAntialias = true, PathEffect = corners };
            canvas.DrawPath(box, paint);
        }

        // Each layer is finished for every line before the next begins, so one line's outline
        // never covers the letters of the line above it.
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
                foreach (var line in scene.Lines)
                    canvas.DrawPath(line.Path, paint);
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
            foreach (var line in scene.Lines)
                canvas.DrawPath(line.Path, paint);

            canvas.Restore();
        }

        if (outline > 0)
        {
            using var stroke = new SKPaint
            {
                Color = style.OutlineColor,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = outline * 2,
                StrokeJoin = SKStrokeJoin.Round
            };

            foreach (var line in scene.Lines)
                canvas.DrawPath(line.Path, stroke);
        }

        using var fill = new SKPaint { Color = style.Fill, IsAntialias = true, Style = SKPaintStyle.Fill };
        foreach (var line in scene.Lines)
        {
            if (style.Shine <= 0)
            {
                canvas.DrawPath(line.Path, fill);
                continue;
            }

            // Every line runs from light to full colour over the height of its own capitals.
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, line.CapTop),
                new SKPoint(0, line.Baseline),
                new[] { Lighten(style.Fill, style.Shine), style.Fill },
                null,
                SKShaderTileMode.Clamp);

            fill.Shader = shader;
            canvas.DrawPath(line.Path, fill);
            fill.Shader = null;
        }

        if (fading)
            canvas.Restore();

        canvas.Restore();
    }

    /// <summary>
    /// Saves the text as a picture with a see-through background, for ffmpeg to lay over a short.
    /// Only the part of the frame the text touches is saved; <see cref="TextOverlayScene.Band"/> says where it goes.
    /// </summary>
    public static void SaveImage(TextOverlayScene scene, string file)
    {
        if (scene.IsEmpty)
            throw new ArgumentException("There is no text to draw.", nameof(scene));

        var band = scene.Band;
        using var bitmap = new SKBitmap(new SKImageInfo(band.Width, band.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(-band.Left, -band.Top);
            Draw(canvas, scene);
        }

        // The encoder stores colours without their opacity multiplied in, which is how ffmpeg reads a PNG.
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
                         ?? throw new InvalidOperationException("The text could not be turned into a picture.");
        using var stream = File.Create(file);
        data.SaveTo(stream);
    }

    private static SKColor Lighten(SKColor color, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte Mix(byte channel) => (byte)Math.Round(channel + (255 - channel) * amount);
        return new SKColor(Mix(color.Red), Mix(color.Green), Mix(color.Blue), color.Alpha);
    }
}
