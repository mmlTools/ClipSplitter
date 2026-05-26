using System.Collections.Generic;

namespace ClipSplitterApp.Services;

public sealed record SplitOptions(
    string InputFile,
    string OutputDirectory,
    int SegmentSeconds,
    bool ReencodeForExactCuts,
    IReadOnlyList<ClipSegment> Segments);
