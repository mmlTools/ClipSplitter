using System;

namespace ClipSplitterApp.Services;

public sealed record ClipSegment(int Index, TimeSpan Start, TimeSpan End)
{
    public TimeSpan Length => End - Start;
}
