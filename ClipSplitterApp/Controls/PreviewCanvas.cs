using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClipSplitterApp.Playback;
using ClipSplitterApp.Services;

namespace ClipSplitterApp.Controls;

public enum PreviewMode
{
    /// <summary>One clip, shown in its own shape.</summary>
    Clip,

    /// <summary>The 9:16 canvas of the vertical studio.</summary>
    Vertical
}

/// <summary>
/// Draws the player's frames. In vertical mode it lays the clips out exactly as the exporter
/// will, and lets the user reframe them: drag a clip to move it, scroll to zoom, drag the
/// divider to resize the halves, double-click to reset.
/// </summary>
public sealed class PreviewCanvas : Control
{
    private const double CanvasAspect = (double)VerticalComposition.CanvasWidth / VerticalComposition.CanvasHeight;
    private const double DividerGrab = 9;
    private const double CornerRadius = 12;

    private static readonly IBrush StageBrush = new SolidColorBrush(Color.Parse("#05070B"));
    private static readonly IBrush EmptySlotBrush = new SolidColorBrush(Color.Parse("#0B0F16"));
    private static readonly IBrush BlurShadeBrush = new SolidColorBrush(Color.Parse("#66000000"));
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.Parse("#402563EB"));
    private static readonly IBrush TitleBrush = new SolidColorBrush(Color.Parse("#9CB7FF"));
    private static readonly IBrush HintBrush = new SolidColorBrush(Color.Parse("#8A96A8"));
    private static readonly IBrush DividerBrush = new SolidColorBrush(Color.Parse("#0E1118"));
    private static readonly IBrush HandleBrush = new SolidColorBrush(Color.Parse("#E5ECF7"));
    private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.Parse("#2B3242")), 1);
    private static readonly IPen HighlightPen = new Pen(new SolidColorBrush(Color.Parse("#3B82F6")), 3);
    private static readonly IPen DashedPen = new Pen(new SolidColorBrush(Color.Parse("#3A465C")), 1.5, new DashStyle(new double[] { 4, 4 }, 0));
    private static readonly Cursor DividerCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);

    private ClipPlayer? _player;
    private VerticalComposition? _composition;
    private PreviewMode _mode = PreviewMode.Vertical;
    private int _highlightSlot = -1;
    private DragKind _drag;
    private int _dragSlot;
    private Point _lastPoint;

    public PreviewCanvas()
    {
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    private enum DragKind
    {
        None,
        Pan,
        Divider
    }

    /// <summary>The user changed the framing or the split by interacting with the canvas.</summary>
    public event Action? CompositionEdited;

    public ClipPlayer? Player
    {
        get => _player;
        set
        {
            _player = value;
            InvalidateVisual();
        }
    }

    public VerticalComposition? Composition
    {
        get => _composition;
        set
        {
            _composition = value;
            InvalidateVisual();
        }
    }

    public PreviewMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
                return;

            _mode = value;
            _drag = DragKind.None;
            Cursor = null;
            InvalidateVisual();
        }
    }

    /// <summary>Slot to outline as a drop target, or -1 for none.</summary>
    public int HighlightSlot
    {
        get => _highlightSlot;
        set
        {
            if (_highlightSlot == value)
                return;

            _highlightSlot = value;
            InvalidateVisual();
        }
    }

    /// <summary>Shown in clip mode while nothing is loaded.</summary>
    public string EmptyClipText { get; set; } = "Press play on a clip to preview it here";

    /// <summary>Returns the slot a clip dropped at <paramref name="point"/> would land in, or -1.</summary>
    public int HitTestSlot(Point point)
    {
        if (_mode != PreviewMode.Vertical || _composition == null)
            return -1;

        var phone = GetPhoneRect();
        if (!phone.Contains(point))
            return -1;

        if (_composition.Layout != VerticalLayoutKind.Stacked)
            return 0;

        return point.Y < GetDividerY(phone) ? 0 : 1;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);

        // A transparent fill makes the whole control respond to the pointer.
        context.FillRectangle(Brushes.Transparent, bounds);

        if (bounds.Width < 8 || bounds.Height < 8)
            return;

        if (_mode == PreviewMode.Clip)
            RenderClip(context, bounds);
        else
            RenderVertical(context);
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(
            double.IsInfinity(availableSize.Width) ? 270 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 480 : availableSize.Height);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (_mode != PreviewMode.Vertical || _composition == null)
            return;

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        if (IsOverDivider(point.Position))
        {
            if (e.ClickCount >= 2)
            {
                _composition.SplitRatio = 0.5;
                NotifyEdited();
                return;
            }

            _drag = DragKind.Divider;
        }
        else
        {
            var slot = HitTestSlot(point.Position);
            if (slot < 0 || _composition.GetSlot(slot) == null)
                return;

            if (e.ClickCount >= 2)
            {
                _composition.GetFraming(slot).Reset();
                NotifyEdited();
                return;
            }

            _drag = DragKind.Pan;
            _dragSlot = slot;
        }

        _lastPoint = point.Position;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_mode != PreviewMode.Vertical || _composition == null)
            return;

        var position = e.GetPosition(this);

        switch (_drag)
        {
            case DragKind.None:
                UpdateCursor(position);
                return;

            case DragKind.Divider:
            {
                var phone = GetPhoneRect();
                var ratio = (position.Y - phone.Y) / phone.Height;
                if (Math.Abs(ratio - 0.5) < 0.012)
                    ratio = 0.5;

                _composition.SplitRatio = Math.Clamp(ratio, VerticalComposition.MinSplitRatio, VerticalComposition.MaxSplitRatio);
                break;
            }

            case DragKind.Pan:
                Pan(_dragSlot, position - _lastPoint);
                break;
        }

        _lastPoint = position;
        NotifyEdited();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_drag == DragKind.None)
            return;

        _drag = DragKind.None;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = DragKind.None;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (_mode != PreviewMode.Vertical || _composition == null)
            return;

        var slot = HitTestSlot(e.GetPosition(this));
        if (slot < 0 || _composition.GetSlot(slot) == null)
            return;

        var framing = _composition.GetFraming(slot);
        framing.Zoom = Math.Clamp(framing.Zoom * Math.Pow(1.12, e.Delta.Y), SlotFraming.MinZoom, _composition.GetMaxZoom(slot));
        e.Handled = true;
        NotifyEdited();
    }

    private void NotifyEdited()
    {
        InvalidateVisual();
        CompositionEdited?.Invoke();
    }

    /// <summary>Moves the visible part of a clip so the picture follows the pointer.</summary>
    private void Pan(int slot, Vector delta)
    {
        if (_composition?.GetSlot(slot) is not { } clip)
            return;

        SlotPlacement? placement = null;
        foreach (var candidate in _composition.GetPlacements())
        {
            if (candidate.Slot == slot)
                placement = candidate;
        }

        if (placement?.Crop is not { } crop)
            return;

        var scale = GetPhoneRect().Width / VerticalComposition.CanvasWidth;
        var pixelsPerSourcePixel = placement.Dest.Width * scale / crop.Width;
        if (pixelsPerSourcePixel <= 0)
            return;

        var framing = _composition.GetFraming(slot);
        var freeX = (clip.Info.Width - crop.Width) / 2;
        var freeY = (clip.Info.Height - crop.Height) / 2;

        if (freeX > 0.5)
            framing.PanX = Math.Clamp(framing.PanX - delta.X / pixelsPerSourcePixel / freeX, -1, 1);
        if (freeY > 0.5)
            framing.PanY = Math.Clamp(framing.PanY - delta.Y / pixelsPerSourcePixel / freeY, -1, 1);
    }

    private void UpdateCursor(Point position)
    {
        if (IsOverDivider(position))
        {
            Cursor = DividerCursor;
            return;
        }

        var slot = HitTestSlot(position);
        Cursor = slot >= 0 && _composition?.GetSlot(slot) != null ? MoveCursor : null;
    }

    private bool IsOverDivider(Point position)
    {
        if (_composition is not { Layout: VerticalLayoutKind.Stacked })
            return false;

        var phone = GetPhoneRect();
        return position.X >= phone.X
               && position.X <= phone.Right
               && Math.Abs(position.Y - GetDividerY(phone)) <= DividerGrab;
    }

    private double GetDividerY(Rect phone)
        => phone.Y + (_composition?.TopHeight ?? VerticalComposition.CanvasHeight / 2) * phone.Height / VerticalComposition.CanvasHeight;

    /// <summary>The largest 9:16 rectangle that fits in the control.</summary>
    private Rect GetPhoneRect()
    {
        var height = Bounds.Height;
        var width = height * CanvasAspect;
        if (width > Bounds.Width)
        {
            width = Bounds.Width;
            height = width / CanvasAspect;
        }

        return new Rect(Math.Round((Bounds.Width - width) / 2), Math.Round((Bounds.Height - height) / 2), Math.Round(width), Math.Round(height));
    }

    private void RenderClip(DrawingContext context, Rect bounds)
    {
        using (context.PushClip(new RoundedRect(bounds, CornerRadius)))
        {
            context.FillRectangle(StageBrush, bounds);

            if (_player?.GetSurface(0) is { } surface)
            {
                context.DrawImage(surface.Bitmap, new Rect(0, 0, surface.Width, surface.Height), Contain(surface.Width, surface.Height, bounds));
            }
            else
            {
                DrawLabel(context, bounds, "CLIP PREVIEW", EmptyClipText);
            }
        }

        context.DrawRectangle(null, BorderPen, bounds.Deflate(0.5), CornerRadius, CornerRadius);
    }

    private void RenderVertical(DrawingContext context)
    {
        var phone = GetPhoneRect();
        var composition = _composition;

        using (context.PushClip(new RoundedRect(phone, CornerRadius)))
        {
            context.FillRectangle(StageBrush, phone);

            if (composition != null)
            {
                var scale = phone.Width / VerticalComposition.CanvasWidth;

                if (composition.Layout == VerticalLayoutKind.Blur && composition.Top != null && _player?.GetSurface(0) is { } backdrop)
                {
                    // Stretching the tiny averaged copy of the frame stands in for the exporter's blur.
                    context.DrawImage(backdrop.Blur, Cover(backdrop.BlurWidth, backdrop.BlurHeight, CanvasAspect), phone);
                    context.FillRectangle(BlurShadeBrush, phone);
                }

                foreach (var placement in composition.GetPlacements())
                {
                    var dest = new Rect(
                        phone.X + placement.Dest.X * scale,
                        phone.Y + placement.Dest.Y * scale,
                        placement.Dest.Width * scale,
                        placement.Dest.Height * scale);

                    var clip = composition.GetSlot(placement.Slot);
                    if (clip == null)
                    {
                        DrawEmptySlot(context, dest, placement.Slot, composition);
                    }
                    else if (placement.Crop is { } crop && _player?.GetSurface(placement.Slot) is { } surface)
                    {
                        // The crop is measured on the full-size video; the surface is a scaled-down copy of it.
                        var sx = surface.Width / (double)clip.Info.Width;
                        var sy = surface.Height / (double)clip.Info.Height;
                        context.DrawImage(surface.Bitmap, new Rect(crop.X * sx, crop.Y * sy, crop.Width * sx, crop.Height * sy), dest);
                    }
                    else if (composition.Layout != VerticalLayoutKind.Blur)
                    {
                        context.FillRectangle(EmptySlotBrush, dest);
                    }
                }

                if (composition.Layout == VerticalLayoutKind.Stacked)
                    DrawDivider(context, phone);

                if (_highlightSlot >= 0)
                    DrawHighlight(context, phone, composition);
            }
        }

        context.DrawRectangle(null, BorderPen, phone.Deflate(0.5), CornerRadius, CornerRadius);
    }

    private void DrawEmptySlot(DrawingContext context, Rect dest, int slot, VerticalComposition composition)
    {
        context.FillRectangle(EmptySlotBrush, dest);

        var inner = dest.Deflate(10);
        if (inner.Width > 20 && inner.Height > 20)
            context.DrawRectangle(null, DashedPen, inner, 8, 8);

        var title = composition.Layout == VerticalLayoutKind.Stacked
            ? slot == 0 ? "TOP CLIP" : "BOTTOM CLIP"
            : "YOUR CLIP";

        DrawLabel(context, dest, title, "Drag a clip here");
    }

    private void DrawDivider(DrawingContext context, Rect phone)
    {
        var y = Math.Round(GetDividerY(phone));
        context.FillRectangle(DividerBrush, new Rect(phone.X, y - 1.5, phone.Width, 3));

        var handle = new Rect(phone.X + phone.Width / 2 - 20, y - 3, 40, 6);
        context.DrawRectangle(DividerBrush, null, handle.Inflate(2), 5, 5);
        context.DrawRectangle(HandleBrush, null, handle, 3, 3);
    }

    private void DrawHighlight(DrawingContext context, Rect phone, VerticalComposition composition)
    {
        var area = phone;
        if (composition.Layout == VerticalLayoutKind.Stacked)
        {
            var divider = GetDividerY(phone);
            area = _highlightSlot == 0
                ? new Rect(phone.X, phone.Y, phone.Width, divider - phone.Y)
                : new Rect(phone.X, divider, phone.Width, phone.Bottom - divider);
        }

        context.FillRectangle(HighlightBrush, area);
        context.DrawRectangle(null, HighlightPen, area.Deflate(1.5), 8, 8);
    }

    private void DrawLabel(DrawingContext context, Rect area, string title, string hint)
    {
        var width = Math.Max(20, area.Width - 24);
        var family = TextElement.GetFontFamily(this);

        var titleText = new FormattedText(title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(family, FontStyle.Normal, FontWeight.Bold), 11, TitleBrush)
        {
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = width
        };

        var hintText = new FormattedText(hint, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(family), 12.5, HintBrush)
        {
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = width
        };

        var total = titleText.Height + 6 + hintText.Height;
        if (total > area.Height - 8)
            return;

        var top = area.Y + (area.Height - total) / 2;
        context.DrawText(titleText, new Point(area.X + 12, top));
        context.DrawText(hintText, new Point(area.X + 12, top + titleText.Height + 6));
    }

    /// <summary>Scales a picture to fit inside <paramref name="area"/> without cropping it.</summary>
    private static Rect Contain(double width, double height, Rect area)
    {
        var scale = Math.Min(area.Width / width, area.Height / height);
        var w = width * scale;
        var h = height * scale;
        return new Rect(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    /// <summary>The centred part of a picture that has the given aspect ratio.</summary>
    private static Rect Cover(double width, double height, double aspect)
    {
        var crop = VerticalComposition.CoverCrop(width, height, aspect, 1, 0, 0);
        return new Rect(crop.X, crop.Y, crop.Width, crop.Height);
    }
}
