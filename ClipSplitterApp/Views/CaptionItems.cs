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

/// <summary>A caption style in the style picker, with a small picture of how it looks.</summary>
public sealed class CaptionStyleTile
{
    private const int SampleWidth = 280;
    private const int SampleHeight = 100;

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
    {
        try
        {
            var words = new[]
            {
                new CaptionWord("Your", TimeSpan.Zero, TimeSpan.FromSeconds(1)),
                new CaptionWord("words", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3))
            };

            // The style is laid out on a frame of its own, then fitted into the tile.
            var scene = CaptionLayout.Build(words, TimeSpan.FromSeconds(3), style, CaptionPosition.Middle, 1, 1080, 1080);
            var page = scene.FindPage(TimeSpan.FromSeconds(2.5));
            if (page == null)
                return null;

            using var bitmap = new SKBitmap(new SKImageInfo(SampleWidth, SampleHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap))
            {
                using var shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0),
                    new SKPoint(SampleWidth, SampleHeight),
                    new[] { new SKColor(0x2B, 0x4A, 0x66), new SKColor(0x4A, 0x35, 0x5E) },
                    null,
                    SKShaderTileMode.Clamp);
                using var backdrop = new SKPaint { Shader = shader };
                canvas.DrawRect(0, 0, SampleWidth, SampleHeight, backdrop);

                var bounds = page.Bounds;
                var fit = Math.Min(SampleWidth * 0.8f / bounds.Width, SampleHeight * 0.44f / bounds.Height);
                canvas.Translate(SampleWidth / 2f, SampleHeight * 0.58f);
                canvas.Scale(fit);
                canvas.Translate(-bounds.MidX, -bounds.MidY);
                CaptionPainter.Draw(canvas, scene, TimeSpan.FromSeconds(2.5));
            }

            var result = new WriteableBitmap(new PixelSize(SampleWidth, SampleHeight), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var target = result.Lock())
            {
                var pixels = bitmap.GetPixels();
                var line = new byte[SampleWidth * 4];
                for (var row = 0; row < SampleHeight; row++)
                {
                    Marshal.Copy(pixels + row * bitmap.RowBytes, line, 0, line.Length);
                    Marshal.Copy(line, 0, target.Address + row * target.RowBytes, line.Length);
                }
            }

            return result;
        }
        catch
        {
            // A tile without a picture still shows the style's name.
            return null;
        }
    }
}
