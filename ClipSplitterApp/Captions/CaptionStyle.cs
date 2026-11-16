using System;
using System.Collections.Generic;
using SkiaSharp;

namespace ClipSplitterApp.Captions;

public enum CaptionPosition
{
    Top,

    /// <summary>The middle of the frame, or the divider when two clips are stacked.</summary>
    Middle,

    Bottom
}

/// <summary>How a group of words arrives on screen.</summary>
public enum CaptionEntrance
{
    None,
    Fade,
    Pop,
    Rise
}

/// <summary>Which part of a style the user's accent colour replaces.</summary>
public enum CaptionAccent
{
    /// <summary>The text itself.</summary>
    Fill,

    /// <summary>The word being spoken.</summary>
    ActiveFill,

    /// <summary>The word being spoken and every word already spoken.</summary>
    Progress,

    /// <summary>The box behind the word being spoken.</summary>
    ActiveBox,

    /// <summary>The box behind the whole caption.</summary>
    PageBox,

    /// <summary>The glow around the text.</summary>
    Glow
}

/// <summary>
/// A caption look. Lengths are fractions of the text size, so a style scales with the frame and
/// with the size the user picks.
/// </summary>
public sealed record CaptionStyle
{
    public static readonly SKColor Yellow = new(0xFF, 0xE0, 0x1A);
    public static readonly SKColor Green = new(0x3D, 0xFF, 0x8B);
    public static readonly SKColor Cyan = new(0x2F, 0xE3, 0xFF);
    public static readonly SKColor Pink = new(0xFF, 0x4F, 0xA3);
    public static readonly SKColor Orange = new(0xFF, 0x8A, 0x1F);
    public static readonly SKColor Red = new(0xFF, 0x3B, 0x3B);
    public static readonly SKColor Purple = new(0x8B, 0x5C, 0xF6);

    /// <summary>The accent colours offered next to each style's own.</summary>
    public static IReadOnlyList<SKColor> AccentChoices { get; } = new[] { Yellow, Green, Cyan, Pink, Orange, Red, Purple, SKColors.White };

    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string FontId { get; init; }

    /// <summary>Text size as a fraction of the frame's width (of a narrower width for wide frames).</summary>
    public double Size { get; init; } = 0.075;

    public bool Uppercase { get; init; }

    /// <summary>Keeps the commas and full stops of the transcript. Punchy styles read better without them.</summary>
    public bool KeepPunctuation { get; init; }

    /// <summary>The text colour, and the colour of words that have not been spoken yet.</summary>
    public SKColor Fill { get; init; } = SKColors.White;

    /// <summary>The colour of the word being spoken, when it differs.</summary>
    public SKColor? ActiveFill { get; init; }

    /// <summary>The colour of words that have been spoken, when it differs.</summary>
    public SKColor? SpokenFill { get; init; }

    public double Outline { get; init; }

    public SKColor OutlineColor { get; init; } = SKColors.Black;

    public double ShadowOffset { get; init; }

    public double ShadowBlur { get; init; }

    public SKColor ShadowColor { get; init; } = new(0, 0, 0, 0xB0);

    public SKColor? Glow { get; init; }

    public double GlowBlur { get; init; }

    /// <summary>A box behind the whole caption.</summary>
    public SKColor? PageBox { get; init; }

    /// <summary>A box behind the word being spoken.</summary>
    public SKColor? ActiveBox { get; init; }

    public double BoxRadius { get; init; } = 0.22;

    public double BoxPadX { get; init; } = 0.3;

    public double BoxPadY { get; init; } = 0.06;

    /// <summary>The most words shown together.</summary>
    public int MaxWords { get; init; } = 4;

    public int MaxLines { get; init; } = 2;

    /// <summary>The widest a line may be, as a fraction of the frame's width.</summary>
    public double MaxWidth { get; init; } = 0.86;

    /// <summary>Distance between baselines, as a multiple of the text size.</summary>
    public double LineHeight { get; init; } = 1.16;

    /// <summary>How much the word being spoken grows. 1 leaves it as it is.</summary>
    public double ActiveScale { get; init; } = 1;

    public CaptionEntrance Entrance { get; init; } = CaptionEntrance.None;

    /// <summary>Rotation of the caption, in degrees.</summary>
    public double Tilt { get; init; }

    public CaptionAccent AccentRole { get; init; } = CaptionAccent.Fill;

    /// <summary>The colour the accent role has in this style, before the user changes it.</summary>
    public SKColor Accent => AccentRole switch
    {
        CaptionAccent.ActiveFill or CaptionAccent.Progress => ActiveFill ?? Fill,
        CaptionAccent.ActiveBox => ActiveBox ?? Fill,
        CaptionAccent.PageBox => PageBox ?? Fill,
        CaptionAccent.Glow => Glow ?? Fill,
        _ => Fill
    };

    public static CaptionStyle Default => Presets[0];

    public static IReadOnlyList<CaptionStyle> Presets { get; } = new[]
    {
        new CaptionStyle
        {
            Id = "highlight",
            Name = "Highlight",
            FontId = CaptionFont.MontserratBlack,
            Size = 0.08,
            Uppercase = true,
            ActiveFill = Yellow,
            Outline = 0.085,
            ShadowOffset = 0.04,
            ShadowBlur = 0.05,
            MaxWords = 3,
            ActiveScale = 1.08,
            Entrance = CaptionEntrance.Pop,
            AccentRole = CaptionAccent.ActiveFill
        },
        new CaptionStyle
        {
            Id = "classic",
            Name = "Classic",
            FontId = CaptionFont.MontserratBold,
            Size = 0.058,
            KeepPunctuation = true,
            Outline = 0.075,
            ShadowOffset = 0.04,
            ShadowBlur = 0.06,
            MaxWords = 7,
            Entrance = CaptionEntrance.Fade,
            AccentRole = CaptionAccent.Fill
        },
        new CaptionStyle
        {
            Id = "karaoke",
            Name = "Karaoke",
            FontId = CaptionFont.MontserratBlack,
            Size = 0.068,
            Uppercase = true,
            ActiveFill = Green,
            SpokenFill = Green,
            Outline = 0.085,
            ShadowOffset = 0.04,
            ShadowBlur = 0.05,
            MaxWords = 4,
            Entrance = CaptionEntrance.Fade,
            AccentRole = CaptionAccent.Progress
        },
        new CaptionStyle
        {
            Id = "marker",
            Name = "Marker",
            FontId = CaptionFont.MontserratBlack,
            Size = 0.07,
            Uppercase = true,
            ActiveBox = Purple,
            Outline = 0.03,
            ShadowOffset = 0.05,
            ShadowBlur = 0.08,
            BoxRadius = 0.2,
            BoxPadX = 0.16,
            BoxPadY = 0.02,
            MaxWords = 3,
            LineHeight = 1.28,
            Entrance = CaptionEntrance.Fade,
            AccentRole = CaptionAccent.ActiveBox
        },
        new CaptionStyle
        {
            Id = "boxed",
            Name = "Boxed",
            FontId = CaptionFont.MontserratBold,
            Size = 0.052,
            KeepPunctuation = true,
            PageBox = new SKColor(0x0B, 0x0D, 0x12, 0xD9),
            BoxRadius = 0.32,
            BoxPadX = 0.5,
            BoxPadY = 0.2,
            MaxWords = 7,
            MaxWidth = 0.8,
            Entrance = CaptionEntrance.Rise,
            AccentRole = CaptionAccent.PageBox
        },
        new CaptionStyle
        {
            Id = "oneword",
            Name = "One word",
            FontId = CaptionFont.Anton,
            Size = 0.15,
            Uppercase = true,
            Outline = 0.055,
            ShadowOffset = 0.035,
            ShadowBlur = 0.05,
            MaxWords = 1,
            MaxLines = 1,
            Entrance = CaptionEntrance.Pop,
            AccentRole = CaptionAccent.Fill
        },
        new CaptionStyle
        {
            Id = "comic",
            Name = "Comic",
            FontId = CaptionFont.Bangers,
            Size = 0.1,
            Uppercase = true,
            Fill = Yellow,
            Outline = 0.075,
            ShadowOffset = 0.075,
            ShadowColor = SKColors.Black,
            MaxWords = 3,
            LineHeight = 1.08,
            Tilt = -4,
            Entrance = CaptionEntrance.Pop,
            AccentRole = CaptionAccent.Fill
        },
        new CaptionStyle
        {
            Id = "neon",
            Name = "Neon",
            FontId = CaptionFont.MontserratBold,
            Size = 0.066,
            Uppercase = true,
            Glow = Cyan,
            GlowBlur = 0.16,
            ShadowOffset = 0.02,
            ShadowBlur = 0.05,
            MaxWords = 4,
            Entrance = CaptionEntrance.Fade,
            AccentRole = CaptionAccent.Glow
        }
    };

    public static CaptionStyle Find(string? id)
    {
        foreach (var preset in Presets)
        {
            if (string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase))
                return preset;
        }

        return Default;
    }

    /// <summary>This style with the accent role recoloured.</summary>
    public CaptionStyle WithAccent(SKColor accent) => AccentRole switch
    {
        CaptionAccent.ActiveFill => this with { ActiveFill = accent },
        CaptionAccent.Progress => this with { ActiveFill = accent, SpokenFill = accent },
        CaptionAccent.ActiveBox => this with { ActiveBox = accent },

        // A box keeps its own see-through level, whatever colour it is given.
        CaptionAccent.PageBox => this with { PageBox = accent.WithAlpha((PageBox ?? accent).Alpha) },
        CaptionAccent.Glow => this with { Glow = accent },
        _ => this with { Fill = accent }
    };
}

/// <summary>The caption choices made in the vertical studio.</summary>
public sealed class CaptionSettings
{
    public const double MinScale = 0.6;
    public const double MaxScale = 1.6;

    public bool Enabled { get; set; }

    public string StyleId { get; set; } = CaptionStyle.Default.Id;

    public CaptionPosition Position { get; set; } = CaptionPosition.Bottom;

    /// <summary>Text size relative to the style's own size.</summary>
    public double Scale { get; set; } = 1;

    /// <summary>The accent colour, or null to keep the style's own.</summary>
    public SKColor? Accent { get; set; }

    /// <summary>Forces capitals on or off, or null to keep the style's own choice.</summary>
    public bool? Uppercase { get; set; }

    /// <summary>The slot whose speech is captioned, or null to follow the sound.</summary>
    public int? Slot { get; set; }

    /// <summary>The chosen style with the user's colour and capitals applied.</summary>
    public CaptionStyle ResolveStyle()
    {
        var style = CaptionStyle.Find(StyleId);
        if (Accent is { } accent)
            style = style.WithAccent(accent);
        if (Uppercase is { } uppercase)
            style = style with { Uppercase = uppercase };

        return style;
    }

    public CaptionSettings Clone() => (CaptionSettings)MemberwiseClone();
}
