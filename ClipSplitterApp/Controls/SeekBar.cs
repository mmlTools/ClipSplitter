using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace ClipSplitterApp.Controls;

/// <summary>A slim slider for the transport bar: click or drag anywhere on it to set the value.</summary>
public sealed class SeekBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<SeekBar, double>(nameof(Value), coerce: (_, value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1));

    private const double TrackHeight = 4;
    private const double ThumbRadius = 6;

    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#2B3242"));
    private static readonly IBrush FillBrush = new SolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush ThumbBrush = new SolidColorBrush(Color.Parse("#F1F5F9"));

    private bool _dragging;

    static SeekBar()
    {
        AffectsRender<SeekBar>(ValueProperty, IsEnabledProperty);
    }

    public SeekBar()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>The user moved the thumb; the argument is the new value, from 0 to 1.</summary>
    public event Action<double>? Seeking;

    /// <summary>Position of the thumb, from 0 to 1.</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Transparent, bounds);

        var left = ThumbRadius;
        var width = Math.Max(0, bounds.Width - ThumbRadius * 2);
        var centre = bounds.Height / 2;
        var x = left + width * Value;

        using (context.PushOpacity(IsEffectivelyEnabled ? 1 : 0.4))
        {
            context.DrawRectangle(TrackBrush, null, new Rect(left, centre - TrackHeight / 2, width, TrackHeight), 2, 2);
            context.DrawRectangle(FillBrush, null, new Rect(left, centre - TrackHeight / 2, x - left, TrackHeight), 2, 2);
            context.DrawEllipse(ThumbBrush, null, new Point(x, centre), ThumbRadius, ThumbRadius);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width, 18);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _dragging = true;
        e.Pointer.Capture(this);
        e.Handled = true;
        MoveTo(e.GetPosition(this).X);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_dragging)
            MoveTo(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!_dragging)
            return;

        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    private void MoveTo(double x)
    {
        var width = Bounds.Width - ThumbRadius * 2;
        if (width <= 0)
            return;

        var value = Math.Clamp((x - ThumbRadius) / width, 0, 1);
        Value = value;
        Seeking?.Invoke(value);
    }
}
