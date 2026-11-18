using Avalonia.Media.Imaging;
using ClipSplitterApp.TextOverlays;

namespace ClipSplitterApp.Views;

/// <summary>A text style in the text tab's picker, with a small picture of how it looks.</summary>
public sealed class TextStyleTile
{
    public TextStyleTile(TextOverlayStyle style)
    {
        Style = style;
        Sample = RenderSample(style);
    }

    public TextOverlayStyle Style { get; }

    public string Name => Style.Name;

    public Bitmap? Sample { get; }

    /// <summary>Draws two sample words in the style, with the same painter as the preview and the exporter.</summary>
    private static Bitmap? RenderSample(TextOverlayStyle style)
        => StyleTileImage.Render(canvas =>
        {
            // The style is laid out on a frame of its own, then fitted into the tile.
            var settings = new TextOverlaySettings { Text = "Your text", StyleId = style.Id, Anchor = TextAnchor.Middle };
            if (TextOverlayLayout.Build(settings, 1080, 1080) is not { IsEmpty: false } scene)
                return;

            // A box is part of the look, so it has to fit in the tile as well.
            var bounds = scene.Style.Box != null ? scene.GrabBounds : scene.Bounds;
            canvas.Scale(StyleTileImage.Fit(bounds));
            canvas.Translate(-bounds.MidX, -bounds.MidY);
            TextOverlayPainter.Draw(canvas, scene);
        });
}
