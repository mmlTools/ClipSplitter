using System;
using System.IO;
using System.Text.Json;

namespace ClipSplitterApp.Services;

/// <summary>
/// The choices that are remembered between runs: how shorts are shaped and captioned, not what
/// was loaded. A missing or unreadable file simply means the defaults.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public int SegmentSeconds { get; set; } = 30;

    public int FormatWidth { get; set; } = OutputFormat.Vertical.Width;

    public int FormatHeight { get; set; } = OutputFormat.Vertical.Height;

    public bool CaptionsEnabled { get; set; }

    public string CaptionStyle { get; set; } = "highlight";

    public string CaptionPosition { get; set; } = "Bottom";

    public double CaptionScale { get; set; } = 1;

    /// <summary>The accent colour as "#RRGGBB", or null for the style's own.</summary>
    public string? CaptionAccent { get; set; }

    public bool? CaptionUppercase { get; set; }

    public string SpeechModel { get; set; } = "small";

    public string SpeechLanguage { get; set; } = "auto";

    /// <summary>The colour look of shorts, by its id.</summary>
    public string Look { get; set; } = VideoLook.Natural.Id;

    public bool FadeIn { get; set; }

    public bool FadeOut { get; set; }

    public double FadeSeconds { get; set; } = 0.6;

    /// <summary>Add replays to the source videos as OBS Studio saves them.</summary>
    public bool WatchObsReplays { get; set; }

    public static string FilePath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipSplitter", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        try
        {
            path ??= FilePath;
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Settings are a convenience; the app works the same without them.
        }

        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        try
        {
            path ??= FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Written beside the real file and swapped in, so a crash mid-write cannot leave half a file.
            var partial = path + ".part";
            File.WriteAllText(partial, JsonSerializer.Serialize(this, Options));
            File.Move(partial, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Not being able to remember a preference is not worth an error message.
        }
    }
}
