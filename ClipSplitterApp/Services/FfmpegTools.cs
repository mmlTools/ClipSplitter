using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClipSplitterApp.Services;

/// <summary>Raised when ffmpeg, ffprobe or ffplay cannot be launched at all.</summary>
public sealed class FfmpegNotFoundException : InvalidOperationException
{
    public FfmpegNotFoundException(string tool, Exception inner)
        : base($"Could not start {tool}. Install FFmpeg and add it to PATH, or place it in tools/ffmpeg beside the app. Details: {inner.Message}", inner)
    {
    }
}

/// <summary>Locates and runs the FFmpeg command line tools.</summary>
public static class FfmpegTools
{
    public static string FindTool(string toolName)
    {
        var exe = OperatingSystem.IsWindows() ? toolName + ".exe" : toolName;
        var local = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", exe);
        return File.Exists(local) ? local : exe;
    }

    /// <summary>Formats a time for -ss / -t style arguments.</summary>
    public static string ArgTime(TimeSpan time)
        => Math.Max(0, time.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);

    public static string ArgNumber(double value)
        => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Starts a tool with stdout and stderr redirected. The caller owns the process.</summary>
    public static Process Start(string toolName, IEnumerable<string> arguments, bool redirectInput = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FindTool(toolName),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new FfmpegNotFoundException(toolName, ex);
        }

        return process;
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process already exited or was never started.
        }
    }

    /// <summary>Runs a tool to completion and returns its standard output.</summary>
    public static async Task<string> RunCaptureAsync(string toolName, IEnumerable<string> arguments, CancellationToken token)
    {
        using var process = Start(toolName, arguments);
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (error) error.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using (token.Register(() => TryKill(process)))
            await process.WaitForExitAsync(CancellationToken.None);

        token.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{toolName} failed. {error}".Trim());

        return output.ToString();
    }

    /// <summary>
    /// Runs an ffmpeg encode and reports progress from 0 to 1, measured against <paramref name="expectedDuration"/>.
    /// Cancelling the token stops ffmpeg straight away.
    /// </summary>
    public static async Task RunFfmpegAsync(IEnumerable<string> arguments, TimeSpan expectedDuration, Action<double>? progress, CancellationToken token)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };
        args.AddRange(arguments);

        using var process = Start("ffmpeg", args);
        var error = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null || progress == null || expectedDuration <= TimeSpan.Zero)
                return;

            // "out_time_ms" is also expressed in microseconds, despite its name.
            var line = e.Data;
            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) && !line.StartsWith("out_time_ms=", StringComparison.Ordinal))
                return;

            if (long.TryParse(line.AsSpan(line.IndexOf('=') + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
                progress(Math.Clamp(microseconds / 1_000_000d / expectedDuration.TotalSeconds, 0, 1));
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (error) error.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using (token.Register(() => TryKill(process)))
            await process.WaitForExitAsync(CancellationToken.None);

        token.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed. {error}".Trim());

        progress?.Invoke(1);
    }

    /// <summary>Returns true when ffmpeg and ffprobe can both be launched.</summary>
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            await RunCaptureAsync("ffmpeg", new[] { "-version" }, CancellationToken.None);
            await RunCaptureAsync("ffprobe", new[] { "-version" }, CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
