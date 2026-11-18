using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClipSplitterApp.Services;

namespace ClipSplitterApp.Views;

/// <summary>The effects tab: the colour look of a short, its speed, what moves its picture, and how it fades in and out.</summary>
public partial class MainWindow
{
    private const double ShortFadeSeconds = 0.3;
    private const double MediumFadeSeconds = 0.6;
    private const double LongFadeSeconds = 1;

    private readonly List<RadioButton> _lookButtons = new();
    private readonly List<RadioButton> _speedButtons = new();
    private readonly List<RadioButton> _effectButtons = new();

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

        foreach (var speed in ShortEffects.Speeds)
        {
            var button = new RadioButton
            {
                GroupName = "ShortSpeed",
                Content = speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×",
                Tag = speed,
                Padding = new Thickness(2, 6),
                Margin = new Thickness(0, 0, 6, 0)
            };
            button.Classes.Add("segment");
            ToolTip.SetTip(button, speed < 1 ? "Slow motion" : speed > 1 ? "Faster than recorded" : "As recorded");
            button.IsCheckedChanged += Speed_Changed;

            _speedButtons.Add(button);
            SpeedPanel.Children.Add(button);
        }

        foreach (var effect in VideoEffect.All)
        {
            var button = new RadioButton
            {
                GroupName = "VideoEffect",
                Content = effect.Name,
                Tag = effect,
                Margin = new Thickness(0, 0, 6, 6)
            };
            button.Classes.Add("segment");
            ToolTip.SetTip(button, effect.Tip);
            button.IsCheckedChanged += Effect_Changed;

            _effectButtons.Add(button);
            EffectPanel.Children.Add(button);
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

        foreach (var button in _speedButtons)
            button.IsChecked = button.Tag is double speed && speed == effects.Speed;

        foreach (var button in _effectButtons)
            button.IsChecked = ReferenceEquals(button.Tag, effects.Effect);

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

    private void Speed_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingUi || sender is not RadioButton { IsChecked: true, Tag: double speed })
            return;

        _composition.Effects.Speed = speed;

        // The short is now a different length, so it starts over, and its captions are timed anew.
        LoadComposition(keepPosition: false);
        RefreshCaptions();
    }

    private void Effect_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingUi || sender is not RadioButton { IsChecked: true, Tag: VideoEffect effect })
            return;

        _composition.Effects.EffectId = effect.Id;
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
