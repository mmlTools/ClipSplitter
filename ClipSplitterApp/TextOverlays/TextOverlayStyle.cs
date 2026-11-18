using System;
using System.Collections.Generic;
using ClipSplitterApp.Captions;
using SkiaSharp;

namespace ClipSplitterApp.TextOverlays;

/// <summary>Where on the frame a text sits.</summary>
public enum TextAnchor
{
    Top,

    /// <summary>The middle of the frame, or the divider when two clips are stacked.</summary>
    Middle,

    Bottom,

    /// <summary>Wherever the user dragged it.</summary>
    Custom
}

/// <summary>Which part of a text style the user's colour replaces.</summary>
public enum TextAccent
{
    /// <summary>The letters.</summary>
    Fill,

    /// <summary>The box behind the lines.</summary>
    Box,

    /// <summary>The glow around the letters.</summary>
    Glow,

    /// <summary>The solid shadow behind the letters.</summary>
    Shadow
}

/// <summary>
/// The look of a text placed on a short, such as a title or a hook. Lengths are fractions of the
/// text size, so a style scales with the frame and with the size the user picks.
/// </summary>
public sealed record TextOverlayStyle
{
    private static readonly SKColor Ink = new(0x11, 0x14, 0x1A);

    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string FontId { get; init; }

    /// <summary>Text size as a fraction of the frame's width (of a narrower width for wide frames).</summary>
    public double Size { get; init; } = 0.085;

    public bool Uppercase { get; init; }

    public SKColor Fill { get; init; } = SKColors.White;

    /// <summary>
    /// How much lighter the top of each line is than its bottom, from 0 to 1. Anything above 0
    /// fills the letters with a gradient.
    /// </summary>
    public double Shine { get; init; }

    public double Outline { get; init; }

    public SKColor OutlineColor { get; init; } = SKColors.Black;

    public double ShadowOffset { get; init; }

    public double ShadowBlur { get; init; }

    public SKColor ShadowColor { get; init; } = new(0, 0, 0, 0xB0);

    public SKColor? Glow { get; init; }

    public double GlowBlur { get; init; }

    /// <summary>A box behind every line. The boxes of neighbouring lines join into one shape.</summary>
    public SKColor? Box { get; init; }

    public double BoxRadius { get; init; } = 0.26;

    public double BoxPadX { get; init; } = 0.42;

    public double BoxPadY { get; init; } = 0.2;

    /// <summary>Distance between baselines, as a multiple of the text size.</summary>
    public double LineHeight { get; init; } = 1.18;

    /// <summary>The widest the text may be, box included, as a fraction of the frame's width.</summary>
    public double MaxWidth { get; init; } = 0.86;

    /// <summary>Rotation of the text, in degrees.</summary>
    public double Tilt { get; init; }

    public TextAccent AccentRole { get; init; } = TextAccent.Fill;

    /// <summary>The colour the accent role has in this style, before the user changes it.</summary>
    public SKColor Accent => AccentRole switch
    {
        TextAccent.Box => (Box ?? Fill).WithAlpha(255),
        TextAccent.Glow => Glow ?? Fill,
        TextAccent.Shadow => ShadowColor.WithAlpha(255),
        _ => Fill
    };

    public static TextOverlayStyle Default => Presets[0];

    public static IReadOnlyList<TextOverlayStyle> Presets { get; } = new[]
    {
        new TextOverlayStyle
        {
            Id = "bold",
            Name = "Bold",
            FontId = CaptionFont.MontserratBlack,
            Size = 0.086,
            Uppercase = true,
            Outline = 0.085,
            ShadowOffset = 0.045,
            ShadowBlur = 0.06,
            AccentRole = TextAccent.Fill
        },
        new TextOverlayStyle
        {
            Id = "label",
            Name = "Label",
            FontId = CaptionFont.MontserratBold,
            Size = 0.062,
            Fill = Ink,
            Box = SKColors.White,
            LineHeight = 1.42,
            AccentRole = TextAccent.Box
        },
        new TextOverlayStyle
        {
            Id = "banner",
            Name = "Banner",
            FontId = CaptionFont.Anton,
            Size = 0.1,
            Uppercase = true,
            Box = CaptionStyle.Red,
            BoxRadius = 0.1,
            BoxPadX = 0.3,
            BoxPadY = 0.12,
            LineHeight = 1.3,
            AccentRole = TextAccent.Box
        },
        new TextOverlayStyle
        {
            Id = "shade",
            Name = "Shade",
            FontId = CaptionFont.MontserratBold,
            Size = 0.058,
            Box = new SKColor(0x0B, 0x0D, 0x12, 0xD9),
            LineHeight = 1.42,
            AccentRole = TextAccent.Box
        },
        new TextOverlayStyle
        {
            Id = "gold",
            Name = "Gold",
            FontId = CaptionFont.MontserratBlack,
            Size = 0.09,
            Uppercase = true,
            Fill = new SKColor(0xFF, 0xA2, 0x1F),
            Shine = 0.75,
            Outline = 0.075,
            OutlineColor = new SKColor(0x2A, 0x14, 0x00),
            ShadowOffset = 0.07,
            ShadowColor = new SKColor(0, 0, 0, 0xD0),
            AccentRole = TextAccent.Fill
        },
        new TextOverlayStyle
        {
            Id = "comic",
            Name = "Comic",
            FontId = CaptionFont.Bangers,
            Size = 0.115,
            Uppercase = true,
            Fill = CaptionStyle.Yellow,
            Outline = 0.075,
            ShadowOffset = 0.08,
            ShadowColor = SKColors.Black,
            LineHeight = 1.06,
            Tilt = -4,
            AccentRole = TextAccent.Fill
        },
        new TextOverlayStyle
        {
            Id = "neon",
            Name = "Neon",
            FontId = CaptionFont.MontserratBold,
            Size = 0.074,
            Uppercase = true,
            Glow = CaptionStyle.Pink,
            GlowBlur = 0.17,
            ShadowOffset = 0.02,
            ShadowBlur = 0.05,
            AccentRole = TextAccent.Glow
        },
        new TextOverlayStyle
        {
            Id = "poster",
            Name = "Poster",
            FontId = CaptionFont.Anton,
            Size = 0.135,
            Uppercase = true,
            Outline = 0.03,
            ShadowOffset = 0.07,
            ShadowColor = CaptionStyle.Cyan,
            LineHeight = 1.14,
            AccentRole = TextAccent.Shadow
        }
    };

    public static TextOverlayStyle Find(string? id)
    {
        foreach (var preset in Presets)
        {
            if (string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase))
                return preset;
        }

        return Default;
    }

    /// <summary>This style with the accent role recoloured.</summary>
    public TextOverlayStyle WithAccent(SKColor accent) => AccentRole switch
    {
        // A box keeps its own see-through level, and its letters turn dark or light to stay readable on it.
        TextAccent.Box => this with { Box = accent.WithAlpha((Box ?? accent).Alpha), Fill = ReadableOn(accent) },
        TextAccent.Glow => this with { Glow = accent },
        TextAccent.Shadow => this with { ShadowColor = accent.WithAlpha(ShadowColor.Alpha) },
        _ => this with { Fill = accent }
    };

    /// <summary>Dark letters for a light background, white ones for a dark background.</summary>
    private static SKColor ReadableOn(SKColor background)
    {
        var brightness = 0.299 * background.Red + 0.587 * background.Green + 0.114 * background.Blue;
        return brightness > 150 ? Ink : SKColors.White;
    }
}

/// <summary>The text placed on a short, and the choices made for it in the vertical studio.</summary>
public sealed class TextOverlaySettings
{
    public const double MinScale = 0.5;
    public const double MaxScale = 2;

    /// <summary>The most characters a text may have. Longer text is a caption, not a title.</summary>
    public const int MaxLength = 200;

    /// <summary>What is written, with a line break wherever the user pressed Enter. Empty for no text.</summary>
    public string Text { get; set; } = string.Empty;

    public string StyleId { get; set; } = TextOverlayStyle.Default.Id;

    /// <summary>The accent colour, or null to keep the style's own.</summary>
    public SKColor? Accent { get; set; }

    /// <summary>Text size relative to the style's own size.</summary>
    public double Scale { get; set; } = 1;

    public TextAnchor Anchor { get; set; } = TextAnchor.Top;

    /// <summary>Where the middle of the text is, as a fraction of the frame's width. Used by <see cref="TextAnchor.Custom"/>.</summary>
    public double CentreX { get; set; } = 0.5;

    /// <summary>Where the middle of the text is, as a fraction of the frame's height. Used by <see cref="TextAnchor.Custom"/>.</summary>
    public double CentreY { get; set; } = 0.5;

    /// <summary>How long the text stays on screen from the start of the short, or null for the whole short.</summary>
    public double? ShowSeconds { get; set; }

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    /// <summary>The chosen style with the user's colour applied.</summary>
    public TextOverlayStyle ResolveStyle()
    {
        var style = TextOverlayStyle.Find(StyleId);
        return Accent is { } accent ? style.WithAccent(accent) : style;
    }

    public TextOverlaySettings Clone() => (TextOverlaySettings)MemberwiseClone();
}
