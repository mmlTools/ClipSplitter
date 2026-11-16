using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SkiaSharp;

namespace ClipSplitterApp.Captions;

/// <summary>
/// Turns a caption scene into the raw video stream ffmpeg lays over a short. Only the band of
/// the frame that captions touch is drawn and sent, and a frame is only redrawn when the
/// captions actually change.
/// </summary>
public sealed class CaptionFrameSource : IDisposable
{
    /// <summary>The ffmpeg name of the pixel layout this source produces.</summary>
    public const string PixelFormat = "rgba";

    /// <summary>Captions change a few times a second, so they do not need more frames than this.</summary>
    private const double MaxFrameRate = 30.5;

    private readonly CaptionScene _scene;
    private readonly SKBitmap _bitmap;
    private readonly SKCanvas _canvas;
    private readonly byte[] _frame;
    private readonly byte[] _blank;
    private readonly int _rateNumerator;
    private readonly int _rateDenominator;

    /// <param name="scene">The captions to draw. It must not be empty.</param>
    /// <param name="frameRate">The frame rate of the short, as a fraction.</param>
    public CaptionFrameSource(CaptionScene scene, (int Numerator, int Denominator) frameRate)
    {
        if (scene.IsEmpty)
            throw new ArgumentException("There are no captions to draw.", nameof(scene));

        _scene = scene;
        _rateNumerator = Math.Max(1, frameRate.Numerator);
        _rateDenominator = Math.Max(1, frameRate.Denominator);

        // Halving a high frame rate keeps every caption frame on a frame of the video.
        while ((double)_rateNumerator / _rateDenominator > MaxFrameRate)
            _rateDenominator *= 2;

        var band = scene.Band;
        _bitmap = new SKBitmap(new SKImageInfo(band.Width, band.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        _canvas = new SKCanvas(_bitmap);
        _frame = new byte[band.Width * band.Height * 4];
        _blank = new byte[_frame.Length];
    }

    /// <summary>Left edge of the band on the frame.</summary>
    public int X => _scene.Band.Left;

    /// <summary>Top edge of the band on the frame.</summary>
    public int Y => _scene.Band.Top;

    public int Width => _scene.Band.Width;

    public int Height => _scene.Band.Height;

    /// <summary>The frame rate of the stream, in the form ffmpeg takes.</summary>
    public string FrameRateArg => _rateDenominator == 1 ? $"{_rateNumerator}" : $"{_rateNumerator}/{_rateDenominator}";

    /// <summary>The time of a frame of the stream.</summary>
    public TimeSpan TimeOf(long frame) => TimeSpan.FromSeconds((double)frame * _rateDenominator / _rateNumerator);

    /// <summary>
    /// Writes the whole stream. Blocks while the reader is busy, so it belongs on a background
    /// thread. It stops quietly when the reader closes its end.
    /// </summary>
    public void WriteTo(Stream output, TimeSpan duration, CancellationToken token)
    {
        // One frame past the end, so the last caption is never held by a missing frame.
        var frames = (long)Math.Ceiling(duration.TotalSeconds * _rateNumerator / _rateDenominator) + 1;
        var shown = long.MinValue;
        var buffer = _blank;

        for (long frame = 0; frame < frames; frame++)
        {
            token.ThrowIfCancellationRequested();

            var time = TimeOf(frame);
            var state = CaptionPainter.StateAt(_scene, time);

            if (state == CaptionPainter.Blank)
            {
                buffer = _blank;
            }
            else if (state == CaptionPainter.Moving || state != shown)
            {
                Render(time);
                buffer = _frame;
            }

            shown = state;
            output.Write(buffer, 0, buffer.Length);
        }

        output.Flush();
    }

    /// <summary>Draws the band as it looks at <paramref name="time"/> and returns its pixels, four bytes each in R, G, B, A order.</summary>
    public byte[] Render(TimeSpan time)
    {
        _canvas.Clear(SKColors.Transparent);
        _canvas.Save();
        _canvas.Translate(-_scene.Band.Left, -_scene.Band.Top);
        CaptionPainter.Draw(_canvas, _scene, time);
        _canvas.Restore();
        _canvas.Flush();

        var pixels = _bitmap.GetPixels();
        var rowBytes = _bitmap.RowBytes;
        var stride = Width * 4;

        if (rowBytes == stride)
        {
            Marshal.Copy(pixels, _frame, 0, _frame.Length);
        }
        else
        {
            for (var row = 0; row < Height; row++)
                Marshal.Copy(pixels + row * rowBytes, _frame, row * stride, stride);
        }

        Unpremultiply(_frame);
        return _frame;
    }

    public void Dispose()
    {
        _canvas.Dispose();
        _bitmap.Dispose();
    }

    /// <summary>
    /// Skia stores colours already multiplied by their opacity; ffmpeg's overlay expects them
    /// plain. Without this step soft edges would come out too dark.
    /// </summary>
    private static void Unpremultiply(byte[] pixels)
    {
        for (var index = 0; index < pixels.Length; index += 4)
        {
            int alpha = pixels[index + 3];
            if (alpha == 0 || alpha == 255)
                continue;

            var half = alpha / 2;
            pixels[index] = (byte)Math.Min(255, (pixels[index] * 255 + half) / alpha);
            pixels[index + 1] = (byte)Math.Min(255, (pixels[index + 1] * 255 + half) / alpha);
            pixels[index + 2] = (byte)Math.Min(255, (pixels[index + 2] * 255 + half) / alpha);
        }
    }
}
