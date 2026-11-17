using System;
using System.Collections.Generic;

namespace ClipSplitterApp.Services;

/// <summary>
/// The short "how to install FFmpeg" tutorial, worded for the system the app is running on.
/// The app shows it whenever FFmpeg cannot be found.
/// </summary>
public static class FfmpegInstallGuide
{
    public const string DownloadUrl = "https://ffmpeg.org/download.html";

    public const string Title = "Install FFmpeg";

    public const string Intro = "ClipSplitter uses FFmpeg to read, play and export video. It is free, and installing it takes one command.";

    /// <summary>Index of the step in <see cref="Steps"/> that <see cref="Command"/> belongs under.</summary>
    public const int CommandStep = 1;

    /// <summary>The command that installs FFmpeg.</summary>
    public static string Command
        => OperatingSystem.IsWindows() ? "winget install ffmpeg"
            : OperatingSystem.IsMacOS() ? "brew install ffmpeg"
            : "sudo apt install ffmpeg";

    /// <summary>The numbered steps, in order.</summary>
    public static IReadOnlyList<string> Steps
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return new[]
                {
                    "Press Win + R.",
                    "Type this and press Enter:",
                    "Wait until the window closes by itself. If it asks a question first, type Y and press Enter.",
                    "Restart ClipSplitter. Done."
                };
            }

            return new[]
            {
                OperatingSystem.IsMacOS() ? "Open Terminal." : "Open a terminal.",
                "Type this and press Enter:",
                "Wait until it finishes.",
                "Restart ClipSplitter. Done."
            };
        }
    }

    /// <summary>What to do when the command is not available.</summary>
    public static string Fallback
        => OperatingSystem.IsWindows()
            ? "No winget on this PC? Download FFmpeg and put ffmpeg.exe and ffprobe.exe in a tools\\ffmpeg folder next to ClipSplitterApp.exe."
            : OperatingSystem.IsMacOS()
                ? "No Homebrew? Get it from brew.sh, or download FFmpeg and put ffmpeg, ffprobe and ffplay in a tools/ffmpeg folder next to the app."
                : "Not on Ubuntu or Debian? Install the ffmpeg package with your own package manager, or put ffmpeg, ffprobe and ffplay in a tools/ffmpeg folder next to the app.";
}
