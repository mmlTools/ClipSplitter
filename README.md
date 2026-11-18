# ClipSplitter

A small Avalonia desktop app that turns long videos into short ones. It splits videos into clips, plays them inside the app, and builds shorts and reels out of them: two clips stacked one under the other, in the format each platform wants, with captions that light up word by word.

## What it does

- **Split** videos into fixed-length clips and export only the ones you tick.
- **Several videos at once.** Load as many videos as you like. Each keeps its own clip length and its own list of clips, and clips from different videos can be combined in one short.
- **Preview in the app.** Play any clip with sound, scrub through it, and trim its start and end.
- **Vertical studio.** Drag clips onto a canvas and export a short:
  - **Stacked**: two clips, one under the other. Drag the divider to resize the halves.
  - **Fill**: one clip cropped to fill the whole frame.
  - **Blur**: the whole clip over a blurred copy of itself.
- **Any format.** 9:16 for TikTok, Reels and Shorts, 4:5 and 3:4 for the Instagram feed, 1:1, 16:9 for YouTube, or a size of your own.
- **Captions.** ClipSplitter listens to a clip and writes down what is said, on your computer. Pick one of eight styles, a colour, a size, and whether the captions sit at the top, in the middle or at the bottom. Correct any word before you export.
- **Text.** Write a title or a hook over the short. Pick one of eight styles and a colour, drag the text where you want it on the canvas, and choose whether it stays for the whole short or only opens it.
- **Effects and transitions.** Give a short a colour look, play it in slow motion or sped up, add a wave, a shake, a glitch or film grain, and fade it in and out, picture and sound together.
- **Panels you can resize.** Drag the gap between the clip list, the player and the settings to give each the width you want.
- **Reframe on the canvas.** Drag a clip to choose what stays in frame, scroll to zoom, double-click to reset.
- **Choose the sound**: top clip, bottom clip, both mixed, or none.
- **Batch shorts.** Export every ticked clip as a short in one go, each with captions for its own speech.
- **OBS Studio replays.** Load the latest replay from the replay buffer with two clicks, or have new replays appear as OBS saves them.

The canvas preview and the exporter share the same layout maths and the same drawing code for text and captions, so the exported short matches what you see.

## Requirements

- .NET 8 SDK (to build)
- FFmpeg, with `ffmpeg` and `ffprobe` available in your system `PATH`. The steps are below.

### Install FFmpeg

FFmpeg is free. On Windows:

1. Press **Win + R**.
2. Type `winget install ffmpeg` and press Enter.
3. Wait until the window closes by itself. If it asks a question first, type `Y` and press Enter.
4. Restart ClipSplitter. Done.

On macOS, open Terminal and run `brew install ffmpeg` (this needs [Homebrew](https://brew.sh)). On Linux, open a terminal and run `sudo apt install ffmpeg`, or install the `ffmpeg` package with your distribution's package manager. Then restart ClipSplitter.

ClipSplitter shows these steps itself when it cannot find FFmpeg, and the arrow button in its title bar opens them again.

If `winget` is not available, [download FFmpeg](https://ffmpeg.org/download.html) and place the programs in a `tools/ffmpeg` folder beside the app:

```text
ClipSplitterApp.exe
tools/ffmpeg/ffmpeg.exe
tools/ffmpeg/ffprobe.exe
```

When running from source, the app is in `ClipSplitterApp/bin/Release/net8.0/<runtime>/`.

On Linux/macOS, omit `.exe`. On those systems the preview plays its sound through `ffplay`, so keep it next to `ffmpeg`; without it the preview is silent. Exports are unaffected.

### For captions

Captions need nothing extra from FFmpeg; any build will do. Generating them from speech needs:

- An internet connection the first time, to download a speech recognition model (60 MB, 190 MB or 624 MB depending on the quality you pick). It is kept in your local app data folder and reused.
- A 64-bit processor with AVX2. On Windows, the Microsoft Visual C++ 2022 runtime, which most computers already have.

Recognition runs on your computer. No audio is uploaded. If it cannot run, you can still import captions from an `.srt` or `.vtt` file.

## Run

```bash
dotnet restore
dotnet run --project ClipSplitterApp/ClipSplitterApp.csproj
```

You can also pass videos to open at startup:

```bash
dotnet run --project ClipSplitterApp/ClipSplitterApp.csproj -- "path/to/video.mp4" "path/to/another.mp4"
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

1. Click **Add videos**, or drop videos onto the window.
2. Set the clip length in seconds, for example `30`.
3. Tick the clips you want.
4. Choose an output folder and click **Export selected**.

By default clips are exported with FFmpeg stream copy (`-c copy`), which is fast and lossless but cuts at the nearest keyframes. Enable **Re-encode for exact cuts** when the cut points must be exact.

### Work with several videos

Every video you add appears as a chip above the clip list. Click a chip to see that video's clips; the **x** on a chip removes the video from the list.

Each video has its own clip length. A long talk can be cut every 45 seconds while the gameplay under it is cut every 60: select the video, then change **Split every**. **Export selected** exports the ticked clips of the video that is showing.

Clips already on the canvas stay there when you switch videos or remove one, so a short can be built from sections of different videos.

### Preview and trim a clip

Press the play button on a clip to watch it in the **Clip preview** tab. To trim it, pause where the clip should begin or end and press **Start here** or **End here**. **Reset** restores the full clip. Space plays and pauses.

The clip preview follows the clip you picked last. Selecting a clip in the list, or putting one on the canvas, makes it the clip the **Clip preview** tab opens on, so it is ready to trim when you move on to the next short. Switching to another video empties the preview until you pick one of its clips.

### Make a short

1. Open the **Vertical studio** tab.
2. Drag a clip from the list onto the top half of the canvas and another onto the bottom half. The buttons on each clip row do the same. The two clips can come from different videos.
   You can also drop a video file straight from your file manager onto a half, for example gameplay footage to sit under a talking clip.
3. Reframe each clip: drag it, scroll to zoom, drag the divider to resize the halves.
4. Pick where the sound comes from.
5. Click **Export this short**.

The short is as long as its shortest clip.

### Choose the format

On the **Layout** tab, **Format** sets the shape and size of the short:

| Format | Size | Used by |
| --- | --- | --- |
| 9:16 | 1080 x 1920 | TikTok, Reels, Shorts |
| 4:5 | 1080 x 1350 | Instagram and Facebook feed |
| 3:4 | 1080 x 1440 | Instagram grid |
| 1:1 | 1080 x 1080 | Square posts |
| 16:9 | 1920 x 1080 | YouTube, landscape |
| Custom size | 240 to 4096 pixels a side | Anything else |

The canvas takes the shape you pick, and your framing is kept. To publish the same short in two formats, export it, change the format and export it again: the file names tell them apart.

### Add captions

1. Put the clip with the talking on the canvas.
2. On the **Captions** tab, click **Generate captions**. The first time, the speech model is downloaded.
3. Read through the text. Click a line to correct it and press Enter; the time next to a line jumps there. Words keep their timing. If you change the number of words in a line, the line's time is shared between them.
4. Further down the same tab, under **Caption style**, pick a look, a position, a size and a colour. The canvas shows the result as you choose. Captions show on the canvas only while **Add captions to the short** is ticked.
5. Export. The captions are burned into the picture.

Things worth knowing:

- **Language.** It is detected automatically. Pick it yourself if the detection gets it wrong.
- **Quality.** *Fast* is quick and fine for clear English. *Accurate* makes fewer mistakes and copes better with accents and other languages. *Best* is the most accurate and much slower.
- **Speech from.** With two clips stacked, captions follow the clip you can hear. Choose the other one here if that is where the talking is.
- **Subtitle files.** **Import .srt** uses an `.srt` or `.vtt` file made for the source video instead of speech recognition. Its times are read as times in that video, so one file covers every clip cut from it.
- **Batch shorts.** With **Add captions to the short** ticked, each short in a batch gets captions for its own clip. Clips that have none yet are listened to on the way.
- **Middle** puts the captions on the divider when two clips are stacked.

### Add text

1. On the **Text** tab, write the text. Press Enter to start a new line. Long lines wrap by themselves, into lines of similar length.
2. Pick a style: *Bold*, *Label*, *Banner*, *Shade*, *Gold*, *Comic*, *Neon* or *Poster*. The canvas shows the text as you type and choose.
3. Place it. **Top**, **Middle** and **Bottom** put it in the usual places; **Middle** is the divider when two clips are stacked. For anywhere else, drag the text on the canvas. It catches on the middle of the frame as it passes, and never leaves the frame.
4. Set the size with the slider, or scroll over the text on the canvas.
5. Pick a colour. Depending on the style it colours the letters, the box behind them, the glow or the shadow. On a box, the letters turn dark or white by themselves so that they stay readable.
6. Export. The text is burned into the picture.

Things worth knowing:

- **Show.** *Always* keeps the text for the whole short. *First 3 s* and *First 5 s* make it a hook: it opens the short and then fades away. While the Text tab is open the text stays on the canvas at any moment, so you can still work on it; press play, or open another tab, to see it come and go.
- **Moving and resizing on the canvas** work while the Text tab is open. On the other tabs, dragging and scrolling reframe the clip under the text, as before.
- **The text stays** when you put another clip on the canvas, and it goes on every short of a batch. Clear the box for a short without text.
- **Text and captions** can be used together. The text is drawn under the captions, and both keep their own colours whatever look the short has.
- Text is limited to 200 characters. Text that does not fit the frame at the chosen size is made smaller until it does.

### Add effects and transitions

On the **Effects** tab:

- **Look** changes the colours of the whole short: *Vivid*, *B&W*, *Warm*, *Cool*, *Vintage*, *Punch*, *Dream* or *VHS*. *Natural* leaves the picture as it was recorded. The canvas shows the look exactly as it will be exported, because the preview runs the same FFmpeg filter as the export. Text and captions keep their own colours.
- **Fade in at the start** and **Fade out at the end** open the short from black and close it to black. The sound fades with the picture. **Length** sets how long each fade takes: 0.3, 0.6 or 1 second. On the canvas, fades show while the short is playing; when it is paused the picture stays in full view so you can still frame it.

- **Speed** plays the short at 0.5×, 0.75×, 1.5× or 2× the speed of its clips. Below 1× is slow motion. Sound changes speed with the picture and keeps its pitch, captions follow, and the short gets longer or shorter to match: a 30 second clip at 0.5× is a 60 second short.
- **Effect** moves or disturbs the picture: *Wave* ripples it from side to side, *Shake* is a restless hand-held camera, *Glitch* splits the colours apart in short bursts, and *Grain* adds film grain. One effect at a time. In a stacked short each clip gets the effect on its own.

The speed is not remembered between runs; the look and the effect are.

### Resize the panels

Drag the gap between two panels to resize them; the pointer turns into a double arrow there. Double-click the gap to put all three back. The widths are remembered. Each panel keeps a least width, so on a small window there is little to move.

Looks and effects use FFmpeg's `eq`, `hue`, `colorbalance`, `curves`, `unsharp`, `gblur`, `chromashift`, `noise`, `displace` and `geq` filters, and speed uses `setpts` and `atempo`. An FFmpeg build that can export shorts at all, which needs libx264, has them.

### Make many shorts at once

Set up the format, layout, text, captions and effects in the vertical studio, tick the clips you want, switch the export format to **Shorts** and click **Export selected**. Each ticked clip becomes its own short. In the stacked layout each clip takes the top half and the bottom clip stays the same. Every short gets the same text.

### Load a replay from OBS Studio

Save a replay in OBS, then click **OBS replay** in ClipSplitter and pick it from the list, newest first. It is added to the source videos like any other.

Tick **Add new replays as OBS saves them** in the same menu and you do not even need to do that: press your Save Replay hotkey in OBS and the replay appears in ClipSplitter, ready to cut. The choice is remembered.

ClipSplitter finds the replays by reading where OBS says it saves them: the recording folder and the replay name prefix of your current OBS profile. It changes nothing in OBS, and it works whether or not OBS is running. If no replays show up, check that the replay buffer is enabled in OBS under Settings, Output, Replay Buffer.

## Output naming

Clips keep the original file name and append the start and end timestamps:

```text
originalfilename-00-00-00-30.mp4
originalfilename-00-30-01-00.mp4
```

Shorts add their format and are always MP4 (H.264 + AAC):

```text
originalfilename-00-30-01-00-9x16.mp4
originalfilename-00-30-01-00-4x5.mp4
originalfilename-00-30-01-00-1000x1500.mp4
```

For videos shorter than one hour, timestamps are `mm-ss`. For videos over one hour they become `hh-mm-ss`. A short never overwrites an existing file; a number is added instead.

## Remembered settings

The format, whether captions are on, the caption style, position, size and colour, the caption language and quality, the style, place, size and colour of the text and how long it shows, the look, the effect and the fades, the widths of the three panels, the clip length, and whether to watch for OBS replays are remembered between runs, in `settings.json` in the `ClipSplitter` folder of your local app data. What the text says is not remembered: every session starts without one. Delete the file to return to the defaults.

## Versioning

The version lives in `presets.json`. Local builds read it from there, the release workflow passes it to the build, and the app shows it in its title bar.

## Third-party work

The caption fonts (Montserrat, Anton and Bangers) are embedded in the app under the SIL Open Font License. Their license texts are in `ClipSplitterApp/Assets/Fonts` and ship in the `licenses` folder next to the app. Speech recognition uses [Whisper.net](https://github.com/sandrohanea/whisper.net) and [whisper.cpp](https://github.com/ggml-org/whisper.cpp), both under the MIT license, with the Whisper models published by OpenAI.
