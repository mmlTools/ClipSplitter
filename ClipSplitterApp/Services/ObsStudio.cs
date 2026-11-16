using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClipSplitterApp.Services;

/// <summary>A replay that OBS Studio saved from its replay buffer.</summary>
public sealed record ObsReplay(string File, DateTime SavedAt, long Size)
{
    public string Name => Path.GetFileName(File);
}

/// <summary>Where the current OBS profile writes its recordings, and how it names replays.</summary>
public sealed record ObsSetup(string ProfileName, string Folder, string ReplayPrefix, string ReplaySuffix, bool ReplayBufferEnabled)
{
    /// <summary>
    /// True when a file in the recordings folder is a replay rather than an ordinary recording.
    /// OBS names a replay "prefix date-and-time suffix", with underscores for spaces if asked to.
    /// </summary>
    public bool IsReplay(string file)
    {
        if (!ObsStudio.IsVideoFile(file))
            return false;

        // Without a prefix or a suffix nothing sets a replay apart, so every recording counts.
        var name = Normalize(Path.GetFileNameWithoutExtension(file));
        var prefix = Normalize(ReplayPrefix);
        var suffix = Normalize(ReplaySuffix);

        return (prefix.Length == 0 || name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
               && (suffix.Length == 0 || name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string text) => text.Trim().Replace('_', ' ');
}

/// <summary>
/// Finds the replays of OBS Studio by reading where OBS itself says it saves them. Nothing is
/// sent to OBS and none of its settings are changed.
/// </summary>
public static class ObsStudio
{
    private const string DefaultReplayPrefix = "Replay";

    // The containers OBS can record to.
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".flv", ".ts", ".m4v", ".webm"
    };

    public static bool IsVideoFile(string file) => VideoExtensions.Contains(Path.GetExtension(file));

    /// <summary>The folders OBS keeps its settings in, most likely first.</summary>
    public static IEnumerable<string> ConfigFolders()
    {
        // %APPDATA% on Windows, ~/.config on Linux, ~/Library/Application Support on macOS.
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(roaming))
            yield return Path.Combine(roaming, "obs-studio");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            yield break;

        if (OperatingSystem.IsMacOS())
            yield return Path.Combine(home, "Library", "Application Support", "obs-studio");

        if (OperatingSystem.IsLinux())
        {
            yield return Path.Combine(home, ".var", "app", "com.obsproject.Studio", "config", "obs-studio");
            yield return Path.Combine(home, "snap", "obs-studio", "current", ".config", "obs-studio");
        }
    }

    /// <summary>
    /// Reads the recording settings of the profile OBS is using. Returns null when OBS has never
    /// been set up on this computer.
    /// </summary>
    /// <param name="configFolder">The OBS settings folder, or null to look in the usual places.</param>
    public static ObsSetup? FindSetup(string? configFolder = null)
    {
        var folders = configFolder != null ? new[] { configFolder } : ConfigFolders();

        foreach (var folder in folders)
        {
            try
            {
                if (Directory.Exists(folder) && ReadSetup(folder) is { } setup)
                    return setup;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Settings that cannot be read are treated like settings that are not there.
            }
        }

        return null;
    }

    /// <summary>The newest replays in the OBS recordings folder, newest first.</summary>
    public static IReadOnlyList<ObsReplay> FindReplays(ObsSetup setup, int max)
    {
        try
        {
            if (!Directory.Exists(setup.Folder))
                return Array.Empty<ObsReplay>();

            return new DirectoryInfo(setup.Folder)
                .EnumerateFiles()
                .Where(file => setup.IsReplay(file.FullName) && file.Length > 0)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(max)
                .Select(file => new ObsReplay(file.FullName, file.LastWriteTime, file.Length))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<ObsReplay>();
        }
    }

    private static ObsSetup? ReadSetup(string configFolder)
    {
        var profileFolder = FindProfileFolder(configFolder);
        if (profileFolder == null)
            return null;

        var settings = IniFile.Read(Path.Combine(profileFolder, "basic.ini"));

        // OBS has two output modes, each with its own recording folder.
        var advanced = string.Equals(settings.Get("Output", "Mode"), "Advanced", StringComparison.OrdinalIgnoreCase);
        string? folder;
        bool enabled;

        if (advanced)
        {
            var custom = string.Equals(settings.Get("AdvOut", "RecType"), "FFmpeg", StringComparison.OrdinalIgnoreCase);
            folder = settings.Get("AdvOut", custom ? "FFFilePath" : "RecFilePath");
            enabled = settings.GetBool("AdvOut", "RecRB");
        }
        else
        {
            folder = settings.Get("SimpleOutput", "FilePath");
            enabled = settings.GetBool("SimpleOutput", "RecRB");
        }

        if (string.IsNullOrWhiteSpace(folder))
            folder = DefaultRecordingFolder();

        // OBS keeps the replay prefix and suffix under "SimpleOutput" in both output modes.
        var prefix = settings.Get("SimpleOutput", "RecRBPrefix") ?? DefaultReplayPrefix;
        var suffix = settings.Get("SimpleOutput", "RecRBSuffix") ?? string.Empty;
        var name = settings.Get("General", "Name") ?? Path.GetFileName(profileFolder);

        return new ObsSetup(name, Path.GetFullPath(folder), prefix, suffix, enabled);
    }

    private static string? FindProfileFolder(string configFolder)
    {
        var profiles = Path.Combine(configFolder, "basic", "profiles");
        if (!Directory.Exists(profiles))
            return null;

        // The profile in use is recorded in user.ini, or in global.ini by versions before OBS 31.
        foreach (var file in new[] { "user.ini", "global.ini" })
        {
            var path = Path.Combine(configFolder, file);
            if (!File.Exists(path))
                continue;

            var current = IniFile.Read(path).Get("Basic", "ProfileDir");
            if (!string.IsNullOrWhiteSpace(current))
            {
                var folder = Path.Combine(profiles, current);
                if (File.Exists(Path.Combine(folder, "basic.ini")))
                    return folder;
            }
        }

        // Failing that, the profile that was saved most recently is the best guess.
        return Directory.EnumerateDirectories(profiles)
            .Where(folder => File.Exists(Path.Combine(folder, "basic.ini")))
            .OrderByDescending(folder => File.GetLastWriteTimeUtc(Path.Combine(folder, "basic.ini")))
            .FirstOrDefault();
    }

    /// <summary>Where OBS records when no folder has been chosen: the user's video folder.</summary>
    private static string DefaultRecordingFolder()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        return string.IsNullOrEmpty(videos) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : videos;
    }

    /// <summary>The small part of the INI format that OBS writes its settings in.</summary>
    private sealed class IniFile
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public static IniFile Read(string path)
        {
            var ini = new IniFile();
            if (!File.Exists(path))
                return ini;

            var section = string.Empty;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';')
                    continue;

                if (line[0] == '[' && line[^1] == ']')
                {
                    section = line[1..^1].Trim();
                    continue;
                }

                var equals = line.IndexOf('=');
                if (equals > 0)
                    ini._values[$"{section}\n{line[..equals].Trim()}"] = Unescape(line[(equals + 1)..].Trim());
            }

            return ini;
        }

        public string? Get(string section, string key)
            => _values.TryGetValue($"{section}\n{key}", out var value) ? value : null;

        public bool GetBool(string section, string key)
            => Get(section, key) is { } value && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");

        /// <summary>OBS writes a backslash as two, which matters for Windows paths.</summary>
        private static string Unescape(string value)
        {
            if (!value.Contains('\\'))
                return value;

            var result = new System.Text.StringBuilder(value.Length);
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character != '\\' || index + 1 >= value.Length)
                {
                    result.Append(character);
                    continue;
                }

                var next = value[++index];
                result.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => next
                });
            }

            return result.ToString();
        }
    }
}
