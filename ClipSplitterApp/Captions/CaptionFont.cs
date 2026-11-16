using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using HarfBuzzSharp;
using SkiaSharp;

namespace ClipSplitterApp.Captions;

/// <summary>A word turned into outlines: its shape and how much room it takes on the line.</summary>
public readonly record struct ShapedText(SKPath Path, float Width);

/// <summary>
/// A caption typeface. Fonts are embedded in the app and shaped with HarfBuzz, so a caption is
/// laid out identically on every system, in the preview and in the exported file alike.
/// </summary>
public sealed class CaptionFont
{
    public const string MontserratBold = "Montserrat-Bold";
    public const string MontserratBlack = "Montserrat-Black";
    public const string Anton = "Anton-Regular";
    public const string Bangers = "Bangers-Regular";

    /// <summary>Words are shaped once at this size and scaled to the size that is needed.</summary>
    private const float DesignSize = 200;
    private const int MaxCachedWords = 4000;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, CaptionFont> Fonts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CaptionFont?> Fallbacks = new(StringComparer.OrdinalIgnoreCase);

    private readonly SKTypeface _typeface;
    private readonly Font _shaper;
    private readonly SKFont _outlines;
    private readonly float _unitsPerEm;
    private readonly Dictionary<string, ShapedText> _cache = new(StringComparer.Ordinal);

    private CaptionFont(SKTypeface typeface, byte[] data, int faceIndex)
    {
        _typeface = typeface;

        // HarfBuzz reads the font straight from this array, which therefore stays pinned for the
        // life of the app. Fonts are loaded once and never unloaded.
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        var blob = new Blob(pinned.AddrOfPinnedObject(), data.Length, MemoryMode.ReadOnly, () => pinned.Free());
        var face = new Face(blob, faceIndex);
        _unitsPerEm = Math.Max(1, face.UnitsPerEm);
        _shaper = new Font(face);
        _shaper.SetFunctionsOpenType();

        _outlines = new SKFont(typeface, DesignSize)
        {
            Hinting = SKFontHinting.None,
            Subpixel = true,
            LinearMetrics = true,
            Edging = SKFontEdging.Antialias
        };

        var metrics = _outlines.Metrics;
        Ascent = -metrics.Ascent / DesignSize;
        Descent = metrics.Descent / DesignSize;
        CapHeight = metrics.CapHeight > 0 ? metrics.CapHeight / DesignSize : Ascent * 0.72f;
    }

    /// <summary>Height above the baseline, as a fraction of the text size.</summary>
    public float Ascent { get; }

    /// <summary>Depth below the baseline, as a fraction of the text size.</summary>
    public float Descent { get; }

    /// <summary>Height of a capital letter, as a fraction of the text size.</summary>
    public float CapHeight { get; }

    /// <summary>Returns one of the embedded fonts by the name of its file.</summary>
    public static CaptionFont Get(string id)
    {
        lock (Gate)
        {
            if (Fonts.TryGetValue(id, out var font))
                return font;

            using var stream = typeof(CaptionFont).Assembly.GetManifestResourceStream($"Fonts/{id}.ttf")
                               ?? throw new InvalidOperationException($"The caption font {id} is missing from the app.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var data = buffer.ToArray();

            using var skData = SKData.CreateCopy(data);
            var typeface = SKTypeface.FromData(skData)
                           ?? throw new InvalidOperationException($"The caption font {id} could not be read.");

            font = new CaptionFont(typeface, data, 0);
            Fonts[id] = font;
            return font;
        }
    }

    /// <summary>The width of a space at the given text size.</summary>
    public float SpaceWidth(float size) => Shape(" ", size).Width;

    /// <summary>
    /// Shapes a word at the given size. The outline starts at x = 0 with its baseline at y = 0.
    /// The returned path belongs to the caller.
    /// </summary>
    public ShapedText Shape(string text, float size)
    {
        ShapedText design;
        lock (Gate)
        {
            if (!_cache.TryGetValue(text, out design))
            {
                if (_cache.Count >= MaxCachedWords)
                {
                    foreach (var entry in _cache.Values)
                        entry.Path.Dispose();
                    _cache.Clear();
                }

                design = ShapeAtDesignSize(text);
                _cache[text] = design;
            }
        }

        var scale = size / DesignSize;
        var path = new SKPath(design.Path);
        path.Transform(SKMatrix.CreateScale(scale, scale));
        return new ShapedText(path, design.Width * scale);
    }

    private ShapedText ShapeAtDesignSize(string text)
    {
        // A word the font cannot draw, such as one in another script, is drawn with a system
        // font that can. The whole word moves over so that it stays in one typeface.
        var font = FindFontFor(text) ?? this;
        return font.ShapeWithOwnGlyphs(text);
    }

    private ShapedText ShapeWithOwnGlyphs(string text)
    {
        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();
        _shaper.Shape(buffer);

        var infos = buffer.GlyphInfos;
        var positions = buffer.GlyphPositions;
        var scale = DesignSize / _unitsPerEm;
        var path = new SKPath();
        float pen = 0;

        for (var index = 0; index < infos.Length; index++)
        {
            using var glyph = _outlines.GetGlyphPath((ushort)infos[index].Codepoint);
            if (glyph != null)
                path.AddPath(glyph, pen + positions[index].XOffset * scale, -positions[index].YOffset * scale);

            pen += positions[index].XAdvance * scale;
        }

        return new ShapedText(path, pen);
    }

    private CaptionFont? FindFontFor(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var codepoint = char.ConvertToUtf32(text, index);
            if (char.IsHighSurrogate(text[index]))
                index++;

            if (codepoint <= ' ' || _typeface.ContainsGlyph(codepoint))
                continue;

            return GetFallback(codepoint);
        }

        return null;
    }

    private static CaptionFont? GetFallback(int codepoint)
    {
        try
        {
            // Caption fonts are all heavy, so the stand-in should be too.
            using var match = SKFontManager.Default.MatchCharacter(
                                  null,
                                  SKFontStyleWeight.Bold,
                                  SKFontStyleWidth.Normal,
                                  SKFontStyleSlant.Upright,
                                  null,
                                  codepoint)
                              ?? SKFontManager.Default.MatchCharacter(codepoint);
            if (match == null)
                return null;

            var key = $"{match.FamilyName}|{match.FontWeight}|{match.FontSlant}";
            if (Fallbacks.TryGetValue(key, out var known))
                return known;

            CaptionFont? font = null;
            using (var stream = match.OpenStream(out var faceIndex))
            {
                if (stream is { Length: > 0 })
                {
                    var data = new byte[stream.Length];
                    if (stream.Read(data, data.Length) == data.Length)
                    {
                        using var skData = SKData.CreateCopy(data);
                        var typeface = SKTypeface.FromData(skData, faceIndex);
                        if (typeface != null)
                            font = new CaptionFont(typeface, data, faceIndex);
                    }
                }
            }

            Fallbacks[key] = font;
            return font;
        }
        catch
        {
            // Without a fallback the word is drawn with the caption font's "missing" boxes.
            return null;
        }
    }
}
