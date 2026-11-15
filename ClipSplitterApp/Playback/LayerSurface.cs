using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClipSplitterApp.Playback;

/// <summary>
/// The bitmap a layer's frames are drawn from, plus a tiny averaged copy that the canvas
/// stretches to preview the blurred backdrop.
/// </summary>
public sealed class LayerSurface : IDisposable
{
    private const int BlurDivisor = 16;
    private const int BlurSamples = 4;

    private readonly byte[] _blurPixels;

    public LayerSurface(int width, int height)
    {
        Width = width;
        Height = height;
        BlurWidth = Math.Max(2, width / BlurDivisor);
        BlurHeight = Math.Max(2, height / BlurDivisor);
        _blurPixels = new byte[BlurWidth * BlurHeight * 4];

        Bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        Blur = new WriteableBitmap(new PixelSize(BlurWidth, BlurHeight), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
    }

    public int Width { get; }

    public int Height { get; }

    public int BlurWidth { get; }

    public int BlurHeight { get; }

    public WriteableBitmap Bitmap { get; }

    public WriteableBitmap Blur { get; }

    /// <summary>False until a frame of the current clip has arrived.</summary>
    public bool HasFrame { get; internal set; }

    /// <summary>Identifies the clip whose frames are currently on the surface.</summary>
    internal string? SourceKey { get; set; }

    /// <summary>Copies a decoded BGRA frame onto the surface. UI thread only.</summary>
    internal void Write(byte[] frame)
    {
        CopyTo(Bitmap, frame, Width, Height);
        BuildBlur(frame);
        CopyTo(Blur, _blurPixels, BlurWidth, BlurHeight);
        HasFrame = true;
    }

    public void Dispose()
    {
        Bitmap.Dispose();
        Blur.Dispose();
    }

    private static void CopyTo(WriteableBitmap bitmap, byte[] pixels, int width, int height)
    {
        using var buffer = bitmap.Lock();
        var stride = width * 4;

        if (buffer.RowBytes == stride)
        {
            Marshal.Copy(pixels, 0, buffer.Address, stride * height);
            return;
        }

        for (var row = 0; row < height; row++)
            Marshal.Copy(pixels, row * stride, buffer.Address + row * buffer.RowBytes, stride);
    }

    private void BuildBlur(byte[] frame)
    {
        for (var by = 0; by < BlurHeight; by++)
        {
            var y0 = by * Height / BlurHeight;
            var y1 = Math.Max(y0 + 1, (by + 1) * Height / BlurHeight);

            for (var bx = 0; bx < BlurWidth; bx++)
            {
                var x0 = bx * Width / BlurWidth;
                var x1 = Math.Max(x0 + 1, (bx + 1) * Width / BlurWidth);

                int blue = 0, green = 0, red = 0;
                for (var sy = 0; sy < BlurSamples; sy++)
                {
                    var y = Math.Min(Height - 1, y0 + (y1 - y0) * sy / BlurSamples);
                    for (var sx = 0; sx < BlurSamples; sx++)
                    {
                        var x = Math.Min(Width - 1, x0 + (x1 - x0) * sx / BlurSamples);
                        var index = (y * Width + x) * 4;
                        blue += frame[index];
                        green += frame[index + 1];
                        red += frame[index + 2];
                    }
                }

                const int count = BlurSamples * BlurSamples;
                var target = (by * BlurWidth + bx) * 4;
                _blurPixels[target] = (byte)(blue / count);
                _blurPixels[target + 1] = (byte)(green / count);
                _blurPixels[target + 2] = (byte)(red / count);
                _blurPixels[target + 3] = 255;
            }
        }
    }
}
