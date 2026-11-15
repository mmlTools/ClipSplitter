using System;
using System.Collections.Generic;

namespace ClipSplitterApp.Services;

public enum VerticalLayoutKind
{
    /// <summary>Two clips, one under the other.</summary>
    Stacked,

    /// <summary>One clip cropped to fill the whole 9:16 frame.</summary>
    Fill,

    /// <summary>One clip shown whole, over a blurred copy of itself.</summary>
    Blur
}

public enum VerticalAudioSource
{
    Top,
    Bottom,
    Mix,
    Mute
}

/// <summary>A stretch of a video file that can be placed in a slot.</summary>
public sealed record ClipSource(string File, TimeSpan Start, TimeSpan End, MediaInfo Info, string Label)
{
    public TimeSpan Length => End - Start;
}

/// <summary>How a clip is zoomed and positioned inside its slot.</summary>
public sealed class SlotFraming
{
    public const double MinZoom = 1;
    public const double MaxZoom = 5;

    public double Zoom { get; set; } = 1;

    /// <summary>Horizontal position of the crop window, from -1 (left edge) to 1 (right edge).</summary>
    public double PanX { get; set; }

    /// <summary>Vertical position of the crop window, from -1 (top edge) to 1 (bottom edge).</summary>
    public double PanY { get; set; }

    public bool IsDefault => Zoom == 1 && PanX == 0 && PanY == 0;

    public void Reset()
    {
        Zoom = 1;
        PanX = 0;
        PanY = 0;
    }

    public SlotFraming Clone() => new() { Zoom = Zoom, PanX = PanX, PanY = PanY };
}

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>Where a slot sits on the canvas and which part of its clip it shows.</summary>
public sealed record SlotPlacement(int Slot, RectD Dest, RectD? Crop);

/// <summary>A vertical short: up to two clips arranged on a 1080x1920 canvas.</summary>
public sealed class VerticalComposition
{
    public const int CanvasWidth = 1080;
    public const int CanvasHeight = 1920;
    public const double MinSplitRatio = 0.25;
    public const double MaxSplitRatio = 0.75;

    private readonly ClipSource?[] _slots = new ClipSource?[2];
    private readonly SlotFraming[] _framing = { new(), new() };

    public VerticalLayoutKind Layout { get; set; } = VerticalLayoutKind.Stacked;

    /// <summary>Share of the canvas height given to the top clip in the stacked layout.</summary>
    public double SplitRatio { get; set; } = 0.5;

    public VerticalAudioSource Audio { get; set; } = VerticalAudioSource.Top;

    public ClipSource? Top
    {
        get => _slots[0];
        set => _slots[0] = value;
    }

    public ClipSource? Bottom
    {
        get => _slots[1];
        set => _slots[1] = value;
    }

    /// <summary>Slots the current layout uses: two when stacked, otherwise only the top one.</summary>
    public int SlotCount => Layout == VerticalLayoutKind.Stacked ? 2 : 1;

    public ClipSource? GetSlot(int slot) => _slots[slot];

    public void SetSlot(int slot, ClipSource? clip) => _slots[slot] = clip;

    public SlotFraming GetFraming(int slot) => _framing[slot];

    public void SwapSlots()
    {
        (_slots[0], _slots[1]) = (_slots[1], _slots[0]);
        (_framing[0], _framing[1]) = (_framing[1], _framing[0]);
    }

    /// <summary>True when every slot of the current layout has a clip.</summary>
    public bool IsComplete
    {
        get
        {
            for (var slot = 0; slot < SlotCount; slot++)
            {
                if (_slots[slot] == null)
                    return false;
            }

            return true;
        }
    }

    /// <summary>The short ends when its shortest clip does.</summary>
    public TimeSpan Duration
    {
        get
        {
            TimeSpan? shortest = null;
            for (var slot = 0; slot < SlotCount; slot++)
            {
                if (_slots[slot] is { } clip && (shortest == null || clip.Length < shortest))
                    shortest = clip.Length;
            }

            return shortest ?? TimeSpan.Zero;
        }
    }

    /// <summary>Slots whose sound is used, taking the layout and missing audio tracks into account.</summary>
    public IReadOnlyList<int> GetAudioSlots()
    {
        var wanted = new List<int>(2);
        if (Audio == VerticalAudioSource.Mute)
            return wanted;

        if (SlotCount == 1)
        {
            wanted.Add(0);
        }
        else
        {
            if (Audio is VerticalAudioSource.Top or VerticalAudioSource.Mix)
                wanted.Add(0);
            if (Audio is VerticalAudioSource.Bottom or VerticalAudioSource.Mix)
                wanted.Add(1);
        }

        wanted.RemoveAll(slot => _slots[slot] is not { Info.HasAudio: true });
        return wanted;
    }

    /// <summary>
    /// The furthest a slot can usefully be zoomed. In the blur layout zooming stops once the clip
    /// covers the whole canvas, because past that point it is just the fill layout.
    /// </summary>
    public double GetMaxZoom(int slot)
    {
        if (Layout != VerticalLayoutKind.Blur || _slots[slot] is not { } clip)
            return SlotFraming.MaxZoom;

        const double canvasAspect = (double)CanvasWidth / CanvasHeight;
        var aspect = clip.Info.AspectRatio;
        var fillZoom = aspect >= canvasAspect ? aspect / canvasAspect : canvasAspect / aspect;
        return Math.Clamp(fillZoom, SlotFraming.MinZoom, SlotFraming.MaxZoom);
    }

    /// <summary>Top height in canvas pixels for the stacked layout, kept even for the encoder.</summary>
    public int TopHeight
    {
        get
        {
            var ratio = Math.Clamp(SplitRatio, MinSplitRatio, MaxSplitRatio);
            return (int)Math.Round(CanvasHeight * ratio / 2d) * 2;
        }
    }

    public VerticalComposition Clone()
    {
        var copy = new VerticalComposition
        {
            Layout = Layout,
            SplitRatio = SplitRatio,
            Audio = Audio
        };

        for (var slot = 0; slot < 2; slot++)
        {
            copy._slots[slot] = _slots[slot];
            copy._framing[slot] = _framing[slot].Clone();
        }

        return copy;
    }

    /// <summary>
    /// Works out, in canvas pixels, where each slot goes and which part of its clip is visible.
    /// The live preview and the exporter both draw from this, so they always agree.
    /// </summary>
    public IReadOnlyList<SlotPlacement> GetPlacements()
    {
        var placements = new List<SlotPlacement>(SlotCount);

        switch (Layout)
        {
            case VerticalLayoutKind.Stacked:
            {
                var top = TopHeight;
                placements.Add(Place(0, new RectD(0, 0, CanvasWidth, top)));
                placements.Add(Place(1, new RectD(0, top, CanvasWidth, CanvasHeight - top)));
                break;
            }

            case VerticalLayoutKind.Fill:
                placements.Add(Place(0, new RectD(0, 0, CanvasWidth, CanvasHeight)));
                break;

            default:
            {
                // Show the whole clip. Zooming grows it until it fills the canvas, cropping the overflow.
                var aspect = _slots[0]?.Info.AspectRatio ?? 16d / 9d;
                var zoom = Math.Clamp(_framing[0].Zoom, SlotFraming.MinZoom, SlotFraming.MaxZoom);
                const double canvasAspect = (double)CanvasWidth / CanvasHeight;

                double width, height;
                if (aspect >= canvasAspect)
                {
                    width = CanvasWidth;
                    height = CanvasWidth / aspect;
                }
                else
                {
                    height = CanvasHeight;
                    width = CanvasHeight * aspect;
                }

                width = Math.Min(CanvasWidth, width * zoom);
                height = Math.Min(CanvasHeight, height * zoom);

                var dest = new RectD((CanvasWidth - width) / 2, (CanvasHeight - height) / 2, width, height);
                var crop = _slots[0] is { } clip
                    ? CoverCrop(clip.Info.Width, clip.Info.Height, width / height, 1, _framing[0].PanX, _framing[0].PanY)
                    : (RectD?)null;

                placements.Add(new SlotPlacement(0, dest, crop));
                break;
            }
        }

        return placements;
    }

    private SlotPlacement Place(int slot, RectD dest)
    {
        if (_slots[slot] is not { } clip)
            return new SlotPlacement(slot, dest, null);

        var framing = _framing[slot];
        return new SlotPlacement(
            slot,
            dest,
            CoverCrop(clip.Info.Width, clip.Info.Height, dest.Width / dest.Height, framing.Zoom, framing.PanX, framing.PanY));
    }

    /// <summary>
    /// The largest window with the given aspect ratio that fits in the source, shrunk by the zoom
    /// factor and moved by the pan values.
    /// </summary>
    public static RectD CoverCrop(double sourceWidth, double sourceHeight, double destAspect, double zoom, double panX, double panY)
    {
        double width, height;
        if (sourceWidth / sourceHeight > destAspect)
        {
            height = sourceHeight;
            width = sourceHeight * destAspect;
        }
        else
        {
            width = sourceWidth;
            height = sourceWidth / destAspect;
        }

        zoom = Math.Clamp(zoom, SlotFraming.MinZoom, SlotFraming.MaxZoom);
        width /= zoom;
        height /= zoom;

        var x = (sourceWidth - width) / 2 * (1 + Math.Clamp(panX, -1, 1));
        var y = (sourceHeight - height) / 2 * (1 + Math.Clamp(panY, -1, 1));
        return new RectD(x, y, width, height);
    }
}
