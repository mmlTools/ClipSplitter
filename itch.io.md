# ClipSplitter itch.io Page Information

## Basic Information

**Title:** ClipSplitter

**Short description:** A lightweight Windows desktop app for splitting video clips into fixed-length segments with preview and selective export.

**Classification:** Tool

**Kind of project:** Downloadable

**Release status:** Released

**Version:** 1.0.0

**Platform:** Windows

**Architecture:** win-x64

**Suggested pricing:** Free / Pay what you want

**Donation link:** https://www.paypal.com/donate/?hosted_button_id=ZKTLLYY9ADWYQ

## Suggested Tags

video, video-editing, ffmpeg, clip, splitter, desktop, windows, productivity, utility, avalonia

## Page Description

ClipSplitter is a small desktop tool for cutting a video into fixed-length segments. Pick a source clip, choose an output folder, set the segment length in seconds, preview the generated cuts, select only the clips you want, and export them.

It is built for quick workflows where you need many short clips from a longer recording without opening a full video editor.

## Features

- Split one video into fixed-length segments.
- Preview the generated cut list before exporting.
- Select all clips, clear the selection, or export only specific segments.
- Generate thumbnails for each planned segment.
- Preview individual clips before export.
- Fast export mode using FFmpeg stream copy, which avoids quality loss when possible.
- Optional re-encode mode for more exact cut points.
- Clear timestamp-based output names, such as `originalfilename-00-00-00-30.mp4`.
- Progress bar and export log.
- Cancel an export while it is running.

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

1. Open ClipSplitter.
2. Click **Browse clip** and choose a video file.
3. Choose an output folder.
4. Set the segment length in seconds.
5. Review the generated cut preview.
6. Select the segments you want to export.
7. Click **Export selected**.

Enable **Re-encode for exact cuts** if you need more precise segment boundaries. The default fast mode uses stream copy and may cut around nearby keyframes, depending on the source video.

## Output Naming

Exported clips keep the original file name and append the start and end timestamps:

```text
originalfilename-00-00-00-30.mp4
originalfilename-00-30-01-00.mp4
originalfilename-01-00-01-30.mp4
```

For videos shorter than one hour, timestamps use `mm-ss`. For videos over one hour, timestamps use `hh-mm-ss`.

## Suggested Upload File

Upload the published Windows build as a ZIP file, for example:

```text
ClipSplitter-1.0.0-win-x64.zip
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

**Accessibility notes:** Mouse-driven desktop interface with text labels, file picker dialogs, and standard Windows media playback for previews.
