using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using ClipSplitterApp.Services;

namespace ClipSplitterApp.Views;

/// <summary>
/// A video the user has loaded. Each one keeps its own clip length and its own list of clips,
/// so several long videos can be cut differently and then combined in the vertical studio.
/// </summary>
public sealed class SourceVideo
{
    public SourceVideo(MediaInfo info, int segmentSeconds)
    {
        Info = info;
        SegmentSeconds = segmentSeconds;
    }

    public MediaInfo Info { get; }

    public string File => Info.File;

    public string Name => Path.GetFileName(Info.File);

    /// <summary>Shown when the pointer rests on the video's chip.</summary>
    public string Tip => $"{Info.File}\n{Info.Width} × {Info.Height}  ·  {ClipFormat.Time(Info.Duration)}";

    /// <summary>The length of the clips this video is split into.</summary>
    public int SegmentSeconds { get; set; }

    public ObservableCollection<ClipPreviewItem> Clips { get; } = new();

    /// <summary>Cancels the thumbnails still being made for the current clips.</summary>
    internal CancellationTokenSource? Work { get; set; }
}

/// <summary>An entry of the format list: a preset, or the custom size.</summary>
public sealed record FormatChoice(OutputFormat? Format, string Title, string Detail)
{
    /// <summary>True for the entry that lets the user type a size.</summary>
    public bool IsCustom => Format == null;
}
