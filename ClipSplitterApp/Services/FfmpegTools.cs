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
    /// <param name="feedInput">
    /// Writes the data ffmpeg reads from "pipe:0", when one of its inputs comes from this app.
    /// It runs on a background thread and may block; it is stopped when ffmpeg exits.
    /// </param>
    public static async Task RunFfmpegAsync(
        IEnumerable<string> arguments,
        TimeSpan expectedDuration,
        Action<double>? progress,
        CancellationToken token,
        Action<Stream, CancellationToken>? feedInput = null)
    {
        // "-nostdin" only stops ffmpeg treating the keyboard as a remote control. It still reads "pipe:0".
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };
        args.AddRange(arguments);

        using var process = Start("ffmpeg", args, redirectInput: feedInput != null);
        using var feedCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var feeding = feedInput == null ? Task.CompletedTask : Task.Run(() => Feed(process, feedInput, feedCancel.Token), CancellationToken.None);
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

        // ffmpeg has gone, so whatever the feeder was still writing has nowhere to go.
        feedCancel.Cancel();
        Exception? feedFailure = null;
        try
        {
            await feeding;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            feedFailure = ex;
        }

        token.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed. {error}".Trim());

        // A feeder that broke leaves a file that looks finished but is missing what it was sending.
        if (feedFailure != null)
            throw new InvalidOperationException("The export stopped early: " + feedFailure.Message, feedFailure);

        progress?.Invoke(1);
    }

    private static void Feed(Process process, Action<Stream, CancellationToken> feedInput, CancellationToken token)
    {
        var input = process.StandardInput.BaseStream;
        try
        {
            feedInput(input, token);
        }
        catch (IOException)
        {
            // ffmpeg closed its end: it has read all it wanted, or it stopped.
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            // Closing the pipe is what tells ffmpeg that this input has ended.
            try
            {
                input.Close();
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// Runs ffmpeg and returns everything it writes to standard output as raw bytes. Used to
    /// pull decoded sound out of a clip.
    /// </summary>
    public static async Task<byte[]> RunFfmpegToBytesAsync(IEnumerable<string> arguments, CancellationToken token)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
        args.AddRange(arguments);

        using var process = Start("ffmpeg", args);
        var error = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (error) error.AppendLine(e.Data); };
        process.BeginErrorReadLine();

        using var output = new MemoryStream();
        await using (token.Register(() => TryKill(process)))
        {
            try
            {
                await process.StandardOutput.BaseStream.CopyToAsync(output, CancellationToken.None);
            }
            catch (IOException)
            {
                // The process was stopped while a read was in flight.
            }

            await process.WaitForExitAsync(CancellationToken.None);
        }

        token.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed. {error}".Trim());

        return output.ToArray();
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
