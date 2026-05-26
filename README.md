# Avalonia Clip Splitter

A small Avalonia desktop application that splits one video clip into fixed-length segments.

Output naming format:

```text
originalfilename-00-00-00-30.mp4
originalfilename-00-30-01-00.mp4
originalfilename-01-00-01-30.mp4
```

For videos shorter than one hour, timestamps are `mm-ss`. For videos over one hour, timestamps become `hh-mm-ss`.

## Requirements

- .NET 8 SDK
- FFmpeg + FFprobe

Install FFmpeg and make sure `ffmpeg` and `ffprobe` are available in your system `PATH`.

Alternative: place the binaries here after publishing/building:

```text
ClipSplitterApp/bin/Release/net8.0/<runtime>/tools/ffmpeg/ffmpeg.exe
ClipSplitterApp/bin/Release/net8.0/<runtime>/tools/ffmpeg/ffprobe.exe
```

On Linux/macOS, omit `.exe`.

## Run

```bash
dotnet restore
dotnet run --project ClipSplitterApp/ClipSplitterApp.csproj
```

## Publish for Windows

```bash
dotnet publish ClipSplitterApp/ClipSplitterApp.csproj -c Release -r win-x64 --self-contained true
```

The executable will be generated under:

```text
ClipSplitterApp/bin/Release/net8.0/win-x64/publish/
```

## How it works

1. Select the input video.
2. Select the output folder.
3. Set the split length in seconds, for example `30`.
4. Click **Split clip**.

By default, the app uses FFmpeg stream copy mode with `-c copy`, which is fast and avoids quality loss. This can cut around keyframes, so if you need more precise segment boundaries, enable **Re-encode for exact cuts**.
