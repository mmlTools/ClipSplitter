# ClipSplitter itch.io Page Information

## Basic Information

**Title:** ClipSplitter

**Short description:** A lightweight desktop app that turns long videos into shorts and reels: split, preview, stack two clips vertically, and export in 9:16.

**Classification:** Tool

**Kind of project:** Downloadable

**Release status:** Released

**Version:** 2.0.0

**Platform:** Windows

**Architecture:** win-x64

**Suggested pricing:** Free / Pay what you want

**Donation link:** https://www.paypal.com/donate/?hosted_button_id=ZKTLLYY9ADWYQ

## Suggested Tags

video, video-editing, shorts, reels, vertical-video, ffmpeg, clip, splitter, desktop, windows, productivity, utility, avalonia

## Page Description

ClipSplitter is a small desktop tool for turning a long video into short ones. Pick a source video, set a clip length, and it lays out every clip with a thumbnail. Play any of them right inside the app, trim the start and end, and export the ones you want.

The vertical studio is built for shorts and reels. Drag two clips onto a 9:16 canvas and they sit one under the other, for example a talking clip over gameplay. Drag each clip to choose what stays in frame, scroll to zoom, drag the divider to resize the halves, then export a 1080x1920 MP4 that matches the preview. A single clip can also fill the frame or sit on a blurred backdrop.

It is built for quick workflows where you need many short clips from a longer recording without opening a full video editor.

## Features

- Split one video into fixed-length clips and export only the ones you tick.
- Play clips inside the app, with sound, a seek bar and volume control.
- Trim the start and end of any clip.
- Vertical studio with a live 9:16 canvas:
  - Stacked: two clips, one under the other, with a draggable divider.
  - Fill: one clip cropped to fill the frame.
  - Blur: the whole clip over a blurred copy of itself.
- Drag clips from the list onto the canvas, or drop video files straight onto it.
- Reframe on the canvas: drag to move, scroll to zoom, double-click to reset.
- Choose the sound: top clip, bottom clip, both mixed, or muted.
- Batch export: turn every ticked clip into a vertical short in one go.
- Fast export mode using FFmpeg stream copy, which avoids quality loss when possible.
- Optional re-encode mode for more exact cut points.
- Clear timestamp-based output names, such as `originalfilename-00-00-00-30.mp4`.
- Progress bar, activity log, and cancel for every export.

## Supported Input Formats

The file picker highlights:

- MP4
- MOV
- MKV
- AVI
- WEBM
- M4V

Other video formats may work if FFmpeg can read them.

## Requirements

ClipSplitter requires FFmpeg and FFprobe.

Install FFmpeg and make sure both `ffmpeg` and `ffprobe` are available in your system `PATH`.

Alternatively, place the binaries beside the app in:

```text
tools/ffmpeg/ffmpeg.exe
tools/ffmpeg/ffprobe.exe
```

FFmpeg download page: https://ffmpeg.org/download.html

## How To Use

### Split a video

1. Open ClipSplitter.
2. Click **Browse video** and choose a video file, or drop one onto the window.
3. Set the clip length in seconds.
4. Tick the clips you want to export.
5. Choose an output folder.
6. Click **Export selected**.

Enable **Re-encode for exact cuts** if you need more precise clip boundaries. The default fast mode uses stream copy and may cut around nearby keyframes, depending on the source video.

### Preview and trim

Press the play button on a clip to watch it in the **Clip preview** tab. Pause where the clip should begin or end and press **Start here** or **End here** to trim it.

### Make a vertical short

1. Open the **Vertical studio** tab.
2. Drag one clip onto the top half of the canvas and another onto the bottom half.
3. Drag each clip to reframe it, scroll to zoom, and drag the divider to resize the halves.
4. Choose where the sound comes from.
5. Click **Export this short**.

To make many shorts at once, set up the layout, tick the clips, choose **Vertical shorts (9:16)** as the export format and click **Export selected**.

## Output Naming

Exported clips keep the original file name and append the start and end timestamps:

```text
originalfilename-00-00-00-30.mp4
originalfilename-00-30-01-00.mp4
originalfilename-01-00-01-30.mp4
```

Vertical shorts add `-vertical` and are always 1080x1920 MP4 files:

```text
originalfilename-00-30-01-00-vertical.mp4
```

For videos shorter than one hour, timestamps use `mm-ss`. For videos over one hour, timestamps use `hh-mm-ss`.

## Suggested Upload File

Upload the published Windows build as a ZIP file, for example:

```text
ClipSplitter-v2.0.0-win-x64.zip
```

Recommended upload label on itch.io:

```text
Windows 64-bit
```

If the ZIP includes FFmpeg binaries, mention that clearly in the file description. If it does not, mention that users must install FFmpeg separately.

## Suggested Download Instructions

Download the ZIP, extract it anywhere, and run `ClipSplitterApp.exe`.

If the app reports that FFmpeg is missing, install FFmpeg and add it to your system `PATH`, or place `ffmpeg.exe` and `ffprobe.exe` in a `tools/ffmpeg` folder beside the executable.

## Suggested Devlog / Release Notes

### ClipSplitter 2.0.0

ClipSplitter now makes shorts and reels, not just clips.

- Added an in-app player: clips play inside the app with sound, seeking and volume, instead of opening an external player.
- Added the vertical studio: drag two clips onto a 9:16 canvas to stack them one under the other, then export a 1080x1920 short.
- Added Fill and Blur layouts for single-clip shorts.
- Added on-canvas reframing: drag to move, scroll to zoom, drag the divider to resize the halves.
- Added a sound choice for shorts: top clip, bottom clip, both mixed, or muted.
- Added batch export of ticked clips as vertical shorts.
- Added clip trimming.
- Added drag and drop of video files onto the window and onto the canvas.
- Exports now show progress within each clip, and cancelling stops FFmpeg immediately and removes the unfinished file.
- Re-encoding a WEBM or AVI source now produces an MP4 instead of failing.
- Redesigned the window around a three-pane layout: clips, player, settings.

### ClipSplitter 1.0.0

Initial release.

- Added fixed-length video splitting.
- Added preview list with thumbnails.
- Added selectable segment export.
- Added individual clip preview.
- Added fast stream-copy export and optional exact re-encode mode.
- Added progress reporting, logs, and cancel support.

## Suggested Community Settings

**Comments:** Enabled

**Discussion board:** Optional

**Visibility:** Public, once the Windows ZIP has been tested on a clean machine.

## Suggested Metadata

**Made with:** Avalonia, .NET 8, FFmpeg

**Average session:** A few minutes

**Languages:** English

**Accessibility notes:** Desktop interface with text labels, tooltips, named controls for screen readers, file picker dialogs, and a built-in player. Space plays and pauses.
