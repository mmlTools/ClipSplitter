using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using ClipSplitterApp.Captions;
using ClipSplitterApp.Playback;
using ClipSplitterApp.Services;
using ClipSplitterApp.TextOverlays;
using SkiaSharp;

namespace ClipSplitterApp.Controls;

public enum PreviewMode
{
    /// <summary>One clip, shown in its own shape.</summary>
    Clip,

    /// <summary>The canvas of the vertical studio, in the shape of the chosen format.</summary>
    Vertical
}

/// <summary>
/// Draws the player's frames. In vertical mode it lays the clips out exactly as the exporter
/// will, and lets the user reframe them: drag a clip to move it, scroll to zoom, drag the
/// divider to resize the halves, double-click to reset. While the text is being edited it can be
/// dragged into place and resized by scrolling, too.
/// </summary>
public sealed class PreviewCanvas : Control
{
    private const double DividerGrab = 9;
    private const double CornerRadius = 12;

    /// <summary>A dragged text catches on the middle of the frame when it is within this share of the width.</summary>
    private const double TextSnap = 0.02;

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
    private static readonly IPen TextFramePen = new Pen(new SolidColorBrush(Color.Parse("#B33B82F6")), 1.5, new DashStyle(new double[] { 4, 3 }, 0));
    private static readonly IPen GuidePen = new Pen(new SolidColorBrush(Color.Parse("#CC3B82F6")), 1);
    private static readonly Cursor DividerCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);

    private ClipPlayer? _player;
    private VerticalComposition? _composition;
    private CaptionScene? _captions;
    private TimeSpan? _captionFreeze;
    private TextOverlayScene? _text;
    private bool _textFreeze;
    private bool _textEditable;
    private bool _textSnapped;
    private Point _textDragOrigin;
    private Point _textDragPress;
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
        Divider,
        Text
    }

    /// <summary>The user changed the framing or the split by interacting with the canvas.</summary>
    public event Action? CompositionEdited;

    /// <summary>The user dragged the text. The argument is where its middle should go, in frame pixels.</summary>
    public event Action<Point>? TextMoved;

    /// <summary>The user scrolled over the text. The argument is how much larger it should get; below 1 is smaller.</summary>
    public event Action<double>? TextResized;

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

    /// <summary>Captions to draw over the vertical canvas, laid out for the composition's format, or null for none.</summary>
    public CaptionScene? Captions
    {
        get => _captions;
        set
        {
            _captions = value;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// When set, the captions are shown as they look at this moment instead of following the
    /// player. Used for the sample text that stands in before a clip has captions of its own.
    /// </summary>
    public TimeSpan? CaptionFreeze
    {
        get => _captionFreeze;
        set
        {
            _captionFreeze = value;
            InvalidateVisual();
        }
    }

    /// <summary>The text to draw over the vertical canvas, laid out for the composition's format, or null for none.</summary>
    public TextOverlayScene? Text
    {
        get => _text;
        set
        {
            _text = value;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// When set, the text is shown at full strength whatever the moment. A text that only shows
    /// at the start of a short would otherwise be out of sight while it is being written.
    /// </summary>
    public bool TextFreeze
    {
        get => _textFreeze;
        set
        {
            if (_textFreeze == value)
                return;

            _textFreeze = value;
            InvalidateVisual();
        }
    }

    /// <summary>True while the text can be dragged and resized on the canvas. Elsewhere a drag reframes the clip under it.</summary>
    public bool TextEditable
    {
        get => _textEditable;
        set
        {
            if (_textEditable == value)
                return;

            _textEditable = value;
            if (!value && _drag == DragKind.Text)
                _drag = DragKind.None;

            _textSnapped = false;
            Cursor = null;
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

        var phone = GetCanvasRect();
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

        if (TextAt(point.Position) is { } text)
        {
            // The drag is measured from where it began, so the text follows the pointer exactly
            // and can leave the middle of the frame again after catching on it.
            _drag = DragKind.Text;
            _textDragPress = point.Position;
            _textDragOrigin = new Point(text.Bounds.MidX, text.Bounds.MidY);
        }
        else if (IsOverDivider(point.Position))
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

            case DragKind.Text:
            {
                var scale = GetCanvasRect().Width / _composition.CanvasWidth;
                if (scale <= 0)
                    return;

                var centre = _textDragOrigin + (position - _textDragPress) / scale;
                var middle = _composition.CanvasWidth / 2d;

                // Text is centred far more often than not, so it catches on the middle of the frame.
                _textSnapped = Math.Abs(centre.X - middle) < _composition.CanvasWidth * TextSnap;
                if (_textSnapped)
                    centre = new Point(middle, centre.Y);

                TextMoved?.Invoke(centre);
                InvalidateVisual();
                return;
            }

            case DragKind.Divider:
            {
                var phone = GetCanvasRect();
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

        EndDrag();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        _drag = DragKind.None;

        if (_textSnapped)
        {
            // The guide that marks the middle of the frame goes with the drag.
            _textSnapped = false;
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (_mode != PreviewMode.Vertical || _composition == null)
            return;

        if (TextAt(e.GetPosition(this)) != null)
        {
            TextResized?.Invoke(Math.Pow(1.08, e.Delta.Y));
            e.Handled = true;
            return;
        }

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

        var scale = GetCanvasRect().Width / _composition.CanvasWidth;
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
        if (TextAt(position) != null)
        {
            Cursor = MoveCursor;
            return;
        }

        if (IsOverDivider(position))
        {
            Cursor = DividerCursor;
            return;
        }

        var slot = HitTestSlot(position);
        Cursor = slot >= 0 && _composition?.GetSlot(slot) != null ? MoveCursor : null;
    }

    /// <summary>The text for the composition's frame, if there is one to draw.</summary>
    private TextOverlayScene? CurrentText
        => _text is { IsEmpty: false } text
           && _composition is { } composition
           && text.Width == composition.CanvasWidth
           && text.Height == composition.CanvasHeight
            ? text
            : null;

    /// <summary>The text under a point of the control, while it can be edited there.</summary>
    private TextOverlayScene? TextAt(Point position)
    {
        if (!_textEditable || _mode != PreviewMode.Vertical || CurrentText is not { } text)
            return null;

        // A text that has faded away while the short plays is not there to take hold of.
        if (!_textFreeze && text.OpacityAt(_player?.Position ?? TimeSpan.Zero) <= 0)
            return null;

        return ToCanvas(text.GrabBounds).Contains(position) ? text : null;
    }

    /// <summary>Turns an area of the frame into the area of the control it is drawn at.</summary>
    private Rect ToCanvas(SKRect area)
    {
        var phone = GetCanvasRect();
        var scale = phone.Width / (_composition?.CanvasWidth ?? OutputFormat.Vertical.Width);
        return new Rect(phone.X + area.Left * scale, phone.Y + area.Top * scale, area.Width * scale, area.Height * scale);
    }

    private bool IsOverDivider(Point position)
    {
        if (_composition is not { Layout: VerticalLayoutKind.Stacked })
            return false;

        var phone = GetCanvasRect();
        return position.X >= phone.X
               && position.X <= phone.Right
               && Math.Abs(position.Y - GetDividerY(phone)) <= DividerGrab;
    }

    private double GetDividerY(Rect phone)
        => _composition == null
            ? phone.Y + phone.Height / 2
            : phone.Y + _composition.TopHeight * phone.Height / _composition.CanvasHeight;

    /// <summary>The largest rectangle in the shape of the chosen format that fits in the control.</summary>
    private Rect GetCanvasRect()
    {
        var aspect = _composition?.CanvasAspect ?? OutputFormat.Vertical.Aspect;
        var height = Bounds.Height;
        var width = height * aspect;
        if (width > Bounds.Width)
        {
            width = Bounds.Width;
            height = width / aspect;
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
        var phone = GetCanvasRect();
        var composition = _composition;

        using (context.PushClip(new RoundedRect(phone, CornerRadius)))
        {
            context.FillRectangle(StageBrush, phone);

            if (composition != null)
            {
                var scale = phone.Width / composition.CanvasWidth;

                if (composition.Layout == VerticalLayoutKind.Blur && composition.Top != null && _player?.GetSurface(0) is { } backdrop)
                {
                    // Stretching the tiny averaged copy of the frame stands in for the exporter's blur.
                    context.DrawImage(backdrop.Blur, Cover(backdrop.BlurWidth, backdrop.BlurHeight, composition.CanvasAspect), phone);
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

                // Text and captions go over the picture and under the editing handles, in the
                // order the export lays them on.
                var text = CurrentText;
                var captions = _captions is { IsEmpty: false } candidate
                               && candidate.Width == composition.CanvasWidth
                               && candidate.Height == composition.CanvasHeight
                    ? candidate
                    : null;

                if (text != null || captions != null)
                {
                    var position = _player?.Position ?? TimeSpan.Zero;
                    var textOpacity = text == null ? 0 : _textFreeze ? 1 : text.OpacityAt(position);
                    context.Custom(new OverlayDrawOperation(text, textOpacity, captions, _captionFreeze ?? position, phone, scale));
                }

                // Fades are shown while the short plays. At rest the picture stays in full view, so
                // a fade-in does not leave a paused canvas black at its first frame.
                if (_player is { IsPlaying: true } playing && composition.Effects.HasFades)
                {
                    var opacity = composition.Effects.OpacityAt(playing.Position, playing.Duration);
                    if (opacity < 0.999)
                        context.FillRectangle(new SolidColorBrush(Colors.Black, 1 - opacity), phone);
                }

                if (composition.Layout == VerticalLayoutKind.Stacked)
                    DrawDivider(context, phone);

                // A frame marks the text as something to take hold of. It is never exported.
                if (_textEditable && text != null && _player is not { IsPlaying: true })
                {
                    context.DrawRectangle(null, TextFramePen, ToCanvas(text.GrabBounds).Inflate(3), 6, 6);

                    if (_textSnapped)
                    {
                        var middle = Math.Round(phone.X + phone.Width / 2) + 0.5;
                        context.DrawLine(GuidePen, new Point(middle, phone.Y), new Point(middle, phone.Bottom));
                    }
                }

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

    /// <summary>
    /// Draws the text and the captions with the same painters the exporter uses, straight onto
    /// the window's Skia canvas. It runs on the render thread, so it only holds values that never change.
    /// </summary>
    private sealed class OverlayDrawOperation : ICustomDrawOperation
    {
        private readonly TextOverlayScene? _text;
        private readonly double _textOpacity;
        private readonly CaptionScene? _captions;
        private readonly TimeSpan _time;
        private readonly double _scale;

        public OverlayDrawOperation(TextOverlayScene? text, double textOpacity, CaptionScene? captions, TimeSpan time, Rect area, double scale)
        {
            _text = text;
            _textOpacity = textOpacity;
            _captions = captions;
            _time = time;
            _scale = scale;
            Bounds = area;
        }

        public Rect Bounds { get; }

        public bool HitTest(Point p) => false;

        // Never equal, so a new moment is always drawn afresh.
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose()
        {
        }

        public void Render(ImmediateDrawingContext context)
        {
            // Only the Skia renderer can lend its canvas; any other simply shows no text or captions.
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
                return;

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;

            canvas.Save();
            canvas.ClipRect(new SKRect((float)Bounds.X, (float)Bounds.Y, (float)Bounds.Right, (float)Bounds.Bottom));
            canvas.Translate((float)Bounds.X, (float)Bounds.Y);
            canvas.Scale((float)_scale);

            if (_text != null)
                TextOverlayPainter.Draw(canvas, _text, _textOpacity);

            if (_captions != null)
                CaptionPainter.Draw(canvas, _captions, _time);

            canvas.Restore();
        }
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
