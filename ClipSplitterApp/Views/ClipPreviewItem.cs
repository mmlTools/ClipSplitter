using System;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using ClipSplitterApp.Services;

namespace ClipSplitterApp.Views;

/// <summary>One row of the clip list: a planned segment, which the user can trim.</summary>
public sealed class ClipPreviewItem : INotifyPropertyChanged
{
    /// <summary>A clip cannot be trimmed shorter than this.</summary>
    public static readonly TimeSpan MinLength = TimeSpan.FromSeconds(0.5);

    private bool _isSelected = true;
    private Bitmap? _thumbnail;
    private TimeSpan _start;
    private TimeSpan _end;

    public ClipPreviewItem(MediaInfo info, ClipSegment segment)
    {
        Info = info;
        Segment = segment;
        _start = segment.Start;
        _end = segment.End;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MediaInfo Info { get; }

    public string InputFile => Info.File;

    /// <summary>The segment as originally planned, before any trimming.</summary>
    public ClipSegment Segment { get; }

    public TimeSpan Start => _start;

    public TimeSpan End => _end;

    public TimeSpan Length => _end - _start;

    public bool IsTrimmed => _start != Segment.Start || _end != Segment.End;

    public string Label => $"Clip {Segment.Index + 1}";

    public string TimeRange => IsTrimmed
        ? $"{ClipFormat.PreciseTime(_start)} - {ClipFormat.PreciseTime(_end)}"
        : $"{ClipFormat.Time(_start)} - {ClipFormat.Time(_end)}";

    public string Duration => IsTrimmed ? ClipFormat.Length(Length) + " · trimmed" : ClipFormat.Length(Length);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            Raise(nameof(IsSelected));
        }
    }

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (_thumbnail == value)
                return;

            _thumbnail = value;
            Raise(nameof(Thumbnail));
        }
    }

    /// <summary>Changes the part of the segment that is used. Returns false if it would be too short.</summary>
    public bool SetRange(TimeSpan start, TimeSpan end)
    {
        if (start < Segment.Start)
            start = Segment.Start;
        if (end > Segment.End)
            end = Segment.End;
        if (end - start < MinLength)
            return false;

        _start = start;
        _end = end;
        Raise(nameof(Start));
        Raise(nameof(End));
        Raise(nameof(TimeRange));
        Raise(nameof(Duration));
        return true;
    }

    public void ResetRange() => SetRange(Segment.Start, Segment.End);

    /// <summary>The range to export, as a segment.</summary>
    public ClipSegment ToSegment() => new(Segment.Index, _start, _end);

    /// <summary>A snapshot of this clip that can be placed in a slot of the vertical canvas.</summary>
    public ClipSource ToClipSource() => new(InputFile, _start, _end, Info, Label);

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
