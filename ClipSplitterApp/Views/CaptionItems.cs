using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ClipSplitterApp.Captions;
using ClipSplitterApp.Services;
using SkiaSharp;

namespace ClipSplitterApp.Views;

/// <summary>One row of the caption text list: the words shown together, which the user can correct.</summary>
public sealed class CaptionLineItem : INotifyPropertyChanged
{
    private string _text;

    public CaptionLineItem(TimeSpan start, string text, int firstWord, int wordCount)
    {
        Start = start;
        _text = text;
        Original = text;
        FirstWord = firstWord;
        WordCount = wordCount;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>When the line appears, measured from the start of the short.</summary>
    public TimeSpan Start { get; private set; }

    public string TimeText => ClipFormat.PreciseTime(Start);

    /// <summary>The text as the transcript has it, to tell whether the row has been edited.</summary>
    public string Original { get; private set; }

    /// <summary>Position of the line's first word in the transcript.</summary>
    public int FirstWord { get; private set; }

    /// <summary>How many words of the transcript the line covers.</summary>
    public int WordCount { get; private set; }

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
                return;

            _text = value;
            Raise(nameof(Text));
        }
    }

    /// <summary>
    /// Points the row at a different line. Rows are reused when the captions are laid out again,
    /// so the text box the user is in is not replaced under them.
    /// </summary>
    public void Set(TimeSpan start, string text, int firstWord, int wordCount)
    {
        FirstWord = firstWord;
        WordCount = wordCount;
        Original = text;
        Text = text;

        if (Start != start)
        {
            Start = start;
            Raise(nameof(TimeText));
        }
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>Draws the small pictures of the style pickers: a look, shown over a backdrop that stands in for video.</summary>
internal static class StyleTileImage
{
    public const int Width = 280;
    public const int Height = 100;

    /// <summary>
    /// Paints the backdrop, lets <paramref name="draw"/> put a sample on it, and returns the
    /// picture. The sample is given a canvas whose origin is where its middle belongs.
    /// </summary>
    /// <returns>The picture, or null when it could not be drawn. A tile without one still shows the style's name.</returns>
    public static Bitmap? Render(Action<SKCanvas> draw)
    {
        try
        {
            using var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap))
            {
                using var shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0),
                    new SKPoint(Width, Height),
                    new[] { new SKColor(0x2B, 0x4A, 0x66), new SKColor(0x4A, 0x35, 0x5E) },
                    null,
                    SKShaderTileMode.Clamp);
                using var backdrop = new SKPaint { Shader = shader };
                canvas.DrawRect(0, 0, Width, Height, backdrop);

                // A little below the middle, which leaves the top of the tile to the style's name.
                canvas.Translate(Width / 2f, Height * 0.58f);
                draw(canvas);
            }

            var result = new WriteableBitmap(new PixelSize(Width, Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var target = result.Lock())
            {
                var pixels = bitmap.GetPixels();
                var line = new byte[Width * 4];
                for (var row = 0; row < Height; row++)
                {
                    Marshal.Copy(pixels + row * bitmap.RowBytes, line, 0, line.Length);
                    Marshal.Copy(line, 0, target.Address + row * target.RowBytes, line.Length);
                }
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The scale that fits an area of a sample into the tile.</summary>
    public static float Fit(SKRect area) => Math.Min(Width * 0.8f / area.Width, Height * 0.44f / area.Height);
}

/// <summary>A caption style in the style picker, with a small picture of how it looks.</summary>
public sealed class CaptionStyleTile
{
    public CaptionStyleTile(CaptionStyle style)
    {
        Style = style;
        Sample = RenderSample(style);
    }

    public CaptionStyle Style { get; }

    public string Name => Style.Name;

    public Bitmap? Sample { get; }

    /// <summary>
    /// Draws two sample words in the style, the second one being spoken, over a backdrop that
    /// stands in for video. It uses the same painter as the preview and the exporter.
    /// </summary>
    private static Bitmap? RenderSample(CaptionStyle style)
        => StyleTileImage.Render(canvas =>
        {
            var words = new[]
            {
                new CaptionWord("Your", TimeSpan.Zero, TimeSpan.FromSeconds(1)),
                new CaptionWord("words", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3))
            };

            // The style is laid out on a frame of its own, then fitted into the tile.
            var scene = CaptionLayout.Build(words, TimeSpan.FromSeconds(3), style, CaptionPosition.Middle, 1, 1080, 1080);
            if (scene.FindPage(TimeSpan.FromSeconds(2.5)) is not { } page)
                return;

            var bounds = page.Bounds;
            canvas.Scale(StyleTileImage.Fit(bounds));
            canvas.Translate(-bounds.MidX, -bounds.MidY);
            CaptionPainter.Draw(canvas, scene, TimeSpan.FromSeconds(2.5));
        });
}
