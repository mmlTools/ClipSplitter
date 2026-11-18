using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using ClipSplitterApp.Captions;
using ClipSplitterApp.Controls;
using ClipSplitterApp.TextOverlays;
using SkiaSharp;

namespace ClipSplitterApp.Views;

/// <summary>The text tab: a title or a hook written over the short, its look and its place.</summary>
public partial class MainWindow
{
    private const double ShortShowSeconds = 3;
    private const double LongShowSeconds = 5;

    private readonly List<RadioButton> _textSwatches = new();

    /// <summary>The text styles offered on the text tab, each with a picture of itself.</summary>
    public IReadOnlyList<TextStyleTile> TextStyleTiles { get; }

    private void InitializeTextControls()
    {
        _syncingUi = true;

        OverlayTextBox.MaxLength = TextOverlaySettings.MaxLength;

        // The first swatch stands for the colour the style comes with; the rest replace it.
        AddTextSwatch(null, "The style's own colour");
        foreach (var color in CaptionStyle.AccentChoices)
            AddTextSwatch(color, null);

        _syncingUi = false;

        PreviewSurface.TextMoved += OnTextMoved;
        PreviewSurface.TextResized += OnTextResized;

        UpdateTextPanel();
        RefreshText();
    }

    private void AddTextSwatch(SKColor? color, string? tip)
    {
        var swatch = CreateSwatch("TextAccent", color, tip);
        swatch.IsCheckedChanged += TextAccent_Changed;
        _textSwatches.Add(swatch);
        TextAccentPanel.Children.Add(swatch);
    }

    /// <summary>The time a remembered "show for" stands for: one of the choices on offer, or null for the whole short.</summary>
    private static double? NearestShowSeconds(double? seconds)
    {
        if (seconds is not { } shown || !double.IsFinite(shown) || shown <= 0)
            return null;

        return shown < (ShortShowSeconds + LongShowSeconds) / 2 ? ShortShowSeconds : LongShowSeconds;
    }

    // ---------------------------------------------------------------- Keeping the text up to date

    /// <summary>Lays the text out again after it, its look or the frame changed, and shows it on the canvas.</summary>
    private void RefreshText()
    {
        PreviewSurface.Text = _composition.LayOutText();
        UpdateTextMoment();
    }

    /// <summary>
    /// The text normally comes and goes with the player. While the text tab is open it can be
    /// moved on the canvas, and at rest it stays in view even if it only shows at the start of
    /// the short, because nothing can be written or placed out of sight.
    /// </summary>
    private void UpdateTextMoment()
    {
        var editing = _mode == PreviewMode.Vertical && _studioTab == StudioTab.Text;
        PreviewSurface.TextEditable = editing;
        PreviewSurface.TextFreeze = editing && !_player.IsPlaying;
    }

    /// <summary>Makes the controls of the text tab show what the composition holds.</summary>
    private void UpdateTextPanel()
    {
        var settings = _composition.Text;
        var style = settings.ResolveStyle();
        var percent = Math.Round(settings.Scale * 100);

        _syncingUi = true;

        if ((OverlayTextBox.Text ?? string.Empty) != settings.Text)
            OverlayTextBox.Text = settings.Text;

        TextStyleList.SelectedItem = TextStyleTiles.FirstOrDefault(tile => tile.Style.Id == style.Id);
        TextSizeSlider.Value = percent;

        TextShowAlwaysRadio.IsChecked = settings.ShowSeconds == null;
        TextShowThreeRadio.IsChecked = settings.ShowSeconds == ShortShowSeconds;
        TextShowFiveRadio.IsChecked = settings.ShowSeconds == LongShowSeconds;

        ShowOwnColour(_textSwatches[0], TextOverlayStyle.Find(settings.StyleId).Accent);
        foreach (var swatch in _textSwatches)
            swatch.IsChecked = Equals(swatch.Tag as SKColor?, settings.Accent);

        _syncingUi = false;

        TextSizeText.Text = $"{percent}%";
        TextAccentLabel.Text = style.AccentRole switch
        {
            TextAccent.Box => "Box colour",
            TextAccent.Glow => "Glow colour",
            TextAccent.Shadow => "Shadow colour",
            _ => "Text colour"
        };

        UpdateTextPositionButtons();
    }

    /// <summary>A text that was dragged somewhere sits at none of the three places, so none is lit.</summary>
    private void UpdateTextPositionButtons()
    {
        var anchor = _composition.Text.Anchor;

        _syncingUi = true;
        TextPositionTopRadio.IsChecked = anchor == TextAnchor.Top;
        TextPositionMiddleRadio.IsChecked = anchor == TextAnchor.Middle;
        TextPositionBottomRadio.IsChecked = anchor == TextAnchor.Bottom;
        _syncingUi = false;
    }

    // ---------------------------------------------------------------- Text tab

    private void OverlayText_Changed(object? sender, TextChangedEventArgs e)
    {
        if (!_ready || _syncingUi)
            return;

        _composition.Text.Text = OverlayTextBox.Text ?? string.Empty;
        RefreshText();
    }

    private void TextStyle_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncingUi || TextStyleList.SelectedItem is not TextStyleTile tile)
            return;

        var settings = _composition.Text;
        if (settings.StyleId == tile.Style.Id)
            return;

        // A colour chosen for one style rarely suits the next, so each style starts as designed.
        settings.StyleId = tile.Style.Id;
        settings.Accent = null;
        UpdateTextPanel();
        RefreshText();
    }

    private void TextPosition_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingUi || sender is not RadioButton { IsChecked: true } radio)
            return;

        _composition.Text.Anchor = ReferenceEquals(radio, TextPositionMiddleRadio) ? TextAnchor.Middle
            : ReferenceEquals(radio, TextPositionBottomRadio) ? TextAnchor.Bottom
            : TextAnchor.Top;

        RefreshText();
    }

    private void TextSize_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_ready || _syncingUi)
            return;

        _composition.Text.Scale = Math.Clamp(e.NewValue / 100d, TextOverlaySettings.MinScale, TextOverlaySettings.MaxScale);
        TextSizeText.Text = $"{Math.Round(e.NewValue)}%";
        RefreshText();
    }

    private void TextAccent_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingUi || sender is not RadioButton { IsChecked: true } swatch)
            return;

        _composition.Text.Accent = swatch.Tag as SKColor?;
        RefreshText();
    }

    private void TextShow_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingUi || sender is not RadioButton { IsChecked: true } radio)
            return;

        _composition.Text.ShowSeconds = ReferenceEquals(radio, TextShowThreeRadio) ? ShortShowSeconds
            : ReferenceEquals(radio, TextShowFiveRadio) ? LongShowSeconds
            : null;

        RefreshText();
    }

    /// <summary>The user dragged the text on the canvas.</summary>
    /// <param name="centre">Where the middle of the text should go, in frame pixels.</param>
    private void OnTextMoved(Point centre)
    {
        var settings = _composition.Text;
        settings.Anchor = TextAnchor.Custom;
        settings.CentreX = Math.Clamp(centre.X / _composition.CanvasWidth, 0, 1);
        settings.CentreY = Math.Clamp(centre.Y / _composition.CanvasHeight, 0, 1);

        UpdateTextPositionButtons();
        RefreshText();
    }

    /// <summary>The user scrolled over the text on the canvas.</summary>
    private void OnTextResized(double factor)
    {
        var settings = _composition.Text;
        settings.Scale = Math.Clamp(settings.Scale * factor, TextOverlaySettings.MinScale, TextOverlaySettings.MaxScale);

        UpdateTextPanel();
        RefreshText();
    }
}
