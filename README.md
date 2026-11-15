# ClipSplitter

A small Avalonia desktop app that turns long videos into short ones. It splits a video into fixed-length clips, plays them inside the app, and builds vertical 9:16 shorts and reels out of them, including two clips stacked one under the other.

## What it does

- **Split** a video into fixed-length clips and export only the ones you tick.
- **Preview in the app.** Play any clip with sound, scrub through it, and trim its start and end.
- **Vertical studio.** Drag clips onto a 9:16 canvas and export a 1080x1920 short:
  - **Stacked**: two clips, one under the other. Drag the divider to resize the halves.
  - **Fill**: one clip cropped to fill the whole frame.
  - **Blur**: the whole clip over a blurred copy of itself.
- **Reframe on the canvas.** Drag a clip to choose what stays in frame, scroll to zoom, double-click to reset.
- **Choose the sound**: top clip, bottom clip, both mixed, or none.
- **Batch shorts.** Export every ticked clip as a vertical short in one go, using the layout on the canvas.

The canvas preview and the exporter share the same layout maths, so the exported short matches what you see.

## Requirements

- .NET 8 SDK (to build)
- FFmpeg, with `ffmpeg` and `ffprobe` available in your system `PATH`

Alternative: place the binaries beside the built app:

```text
ClipSplitterApp/bin/Release/net8.0/<runtime>/tools/ffmpeg/ffmpeg.exe
ClipSplitterApp/bin/Release/net8.0/<runtime>/tools/ffmpeg/ffprobe.exe
```

On Linux/macOS, omit `.exe`. On those systems the preview plays its sound through `ffplay`, so keep it next to `ffmpeg`; without it the preview is silent. Exports are unaffected.

## Run

```bash
dotnet restore
dotnet run --project ClipSplitterApp/ClipSplitterApp.csproj
```

You can also pass a video to open at startup:

```bash
dotnet run --project ClipSplitterApp/ClipSplitterApp.csproj -- "path/to/video.mp4"
```

## Publish for Windows

```bash
dotnet publish ClipSplitterApp/ClipSplitterApp.csproj -c Release -r win-x64 --self-contained true
```

The executable will be generated under:

```text
ClipSplitterApp/bin/Release/net8.0/win-x64/publish/
```

## How to use

### Split a video into clips

1. Click **Browse video**, or drop a video onto the window.
2. Set the clip length in seconds, for example `30`.
3. Tick the clips you want.
4. Choose an output folder and click **Export selected**.

By default clips are exported with FFmpeg stream copy (`-c copy`), which is fast and lossless but cuts at the nearest keyframes. Enable **Re-encode for exact cuts** when the cut points must be exact.

### Preview and trim a clip

Press the play button on a clip to watch it in the **Clip preview** tab. To trim it, pause where the clip should begin or end and press **Start here** or **End here**. **Reset** restores the full clip. Space plays and pauses.

### Make a vertical short

1. Open the **Vertical studio** tab.
2. Drag a clip from the list onto the top half of the canvas and another onto the bottom half. The buttons on each clip row do the same.
   You can also drop a video file straight from your file manager onto a half, for example gameplay footage to sit under a talking clip.
3. Reframe each clip: drag it, scroll to zoom, drag the divider to resize the halves.
4. Pick where the sound comes from.
5. Click **Export this short**.

The short is as long as its shortest clip. To switch the source video without losing the canvas, just browse to another video: the clips already on the canvas stay there.

### Make many shorts at once

Set up the layout in the vertical studio, tick the clips you want, switch the export format to **Vertical shorts (9:16)** and click **Export selected**. Each ticked clip becomes its own short. In the stacked layout each clip takes the top half and the bottom clip stays the same.

## Output naming

Clips keep the original file name and append the start and end timestamps:

```text
originalfilename-00-00-00-30.mp4
originalfilename-00-30-01-00.mp4
```

Vertical shorts add `-vertical` and are always MP4 (H.264 + AAC, 1080x1920):

```text
originalfilename-00-30-01-00-vertical.mp4
```

For videos shorter than one hour, timestamps are `mm-ss`. For videos over one hour they become `hh-mm-ss`. A vertical short never overwrites an existing file; a number is added instead.

## Versioning

The version lives in `presets.json`. Local builds read it from there, the release workflow passes it to the build, and the app shows it in its title bar.
