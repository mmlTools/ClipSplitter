using System.Collections.Generic;

namespace ClipSplitterApp.Services;

public sealed record SplitOptions(
    string InputFile,
    string OutputDirectory,
    bool ReencodeForExactCuts,
    IReadOnlyList<ClipSegment> Segments);
