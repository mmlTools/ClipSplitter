using System;
using System.Collections.Generic;

namespace ClipSplitterApp.Services;

/// <summary>The frame a short is rendered in: its shape, its pixel size, and where that shape is used.</summary>
public sealed record OutputFormat(string Ratio, int Width, int Height, string Platforms)
{
    public const int MinSide = 240;
    public const int MaxSide = 4096;

    /// <summary>The 9:16 frame used by TikTok, Reels and Shorts. This is the default.</summary>
    public static readonly OutputFormat Vertical = new("9:16", 1080, 1920, "TikTok, Reels, Shorts");

    public static IReadOnlyList<OutputFormat> Presets { get; } = new[]
    {
        Vertical,
        new OutputFormat("4:5", 1080, 1350, "Instagram and Facebook feed"),
        new OutputFormat("3:4", 1080, 1440, "Instagram grid"),
        new OutputFormat("1:1", 1080, 1080, "Square posts"),
        new OutputFormat("16:9", 1920, 1080, "YouTube, landscape")
    };

    public double Aspect => (double)Width / Height;

    public string SizeText => $"{Width} × {Height}";

    /// <summary>Short text for file names, such as "9x16", or "1000x1500" for a custom size.</summary>
    public string FileTag => Platforms.Length == 0 ? $"{Width}x{Height}" : Ratio.Replace(':', 'x');

    /// <summary>Text for the format list, such as "9:16  ·  1080 × 1920".</summary>
    public string Title => $"{Ratio}  ·  {SizeText}";

    /// <summary>
    /// A format of any size. Sides are clamped to what the encoder accepts and made even, and a
    /// size that matches a preset becomes that preset.
    /// </summary>
    public static OutputFormat Custom(int width, int height)
    {
        width = MakeEven(width);
        height = MakeEven(height);

        foreach (var preset in Presets)
        {
            if (preset.Width == width && preset.Height == height)
                return preset;
        }

        return new OutputFormat(DescribeRatio(width, height), width, height, string.Empty);
    }

    private static int MakeEven(int side) => Math.Clamp(side, MinSide, MaxSide) / 2 * 2;

    private static string DescribeRatio(int width, int height)
    {
        var divisor = GreatestCommonDivisor(width, height);
        var x = width / divisor;
        var y = height / divisor;

        // Sizes such as 1078x1920 do not reduce to a ratio anyone would recognise.
        return x <= 64 && y <= 64 ? $"{x}:{y}" : "Custom";
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);

        return Math.Max(1, a);
    }
}
