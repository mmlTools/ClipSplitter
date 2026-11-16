using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClipSplitterApp.Services;

namespace ClipSplitterApp.Views;

/// <summary>The effects tab: the colour look of a short, and how it fades in and out.</summary>
public partial class MainWindow
{
    private const double ShortFadeSeconds = 0.3;
    private const double MediumFadeSeconds = 0.6;
    private const double LongFadeSeconds = 1;

    private readonly List<RadioButton> _lookButtons = new();

    private void InitializeEffectControls()
    {
        _syncingUi = true;

        foreach (var look in VideoLook.All)
        {
            var button = new RadioButton
            {
                GroupName = "VideoLook",
                Content = look.Name,
                Tag = look,
                Margin = new Thickness(0, 0, 6, 6)
            };
            button.Classes.Add("segment");
            button.IsCheckedChanged += Look_Changed;

            _lookButtons.Add(button);
            LookPanel.Children.Add(button);
        }

        _syncingUi = false;
        UpdateEffectsPanel();
    }

    /// <summary>Makes the controls of the effects tab show what the composition holds.</summary>
    private void UpdateEffectsPanel()
    {
        var effects = _composition.Effects;
        var look = effects.Look;

        _syncingUi = true;

        foreach (var button in _lookButtons)
            button.IsChecked = ReferenceEquals(button.Tag, look);

        FadeInBox.IsChecked = effects.FadeIn;
        FadeOutBox.IsChecked = effects.FadeOut;

        var isShort = effects.FadeSeconds < (ShortFadeSeconds + MediumFadeSeconds) / 2;
        var isLong = effects.FadeSeconds > (MediumFadeSeconds + LongFadeSeconds) / 2;
        FadeShortRadio.IsChecked = isShort;
        FadeLongRadio.IsChecked = isLong;
        FadeMediumRadio.IsChecked = !isShort && !isLong;

        _syncingUi = false;
    }

    private void Look_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingUi || sender is not RadioButton { IsChecked: true, Tag: VideoLook look })
            return;

        _composition.Effects.LookId = look.Id;

        // The player applies the look as it decodes, so it picks up again from where it was.
        LoadComposition(keepPosition: true);
    }

    private void Fade_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingUi)
            return;

        _composition.Effects.FadeIn = FadeInBox.IsChecked == true;
        _composition.Effects.FadeOut = FadeOutBox.IsChecked == true;
        PreviewSurface.InvalidateVisual();
    }

    private void FadeLength_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingUi || sender is not RadioButton { IsChecked: true } radio)
            return;

        _composition.Effects.FadeSeconds = ReferenceEquals(radio, FadeShortRadio) ? ShortFadeSeconds
            : ReferenceEquals(radio, FadeLongRadio) ? LongFadeSeconds
            : MediumFadeSeconds;

        PreviewSurface.InvalidateVisual();
    }
}
