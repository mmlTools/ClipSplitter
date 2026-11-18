using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClipSplitterApp.Controls;
using ClipSplitterApp.Services;

namespace ClipSplitterApp.Views;

/// <summary>The source videos, their clip lists, and replays picked up from OBS Studio.</summary>
public partial class MainWindow
{
    private const int ObsMenuReplays = 6;

    private readonly HashSet<string> _obsPending = new(StringComparer.OrdinalIgnoreCase);
    private SourceVideo? _activeSource;
    private SourceVideo? _replanSource;
    private FileSystemWatcher? _obsWatcher;
    private ObsSetup? _obsWatched;

    /// <summary>The videos that have been loaded. Each has its own clip length and its own clips.</summary>
    public ObservableCollection<SourceVideo> Sources { get; } = new();

    // ---------------------------------------------------------------- Source videos

    private async void AddVideos_Click(object? sender, RoutedEventArgs e)
    {
        AddVideosButton.Content = "Opening...";
        AddVideosButton.IsEnabled = false;

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add videos",
                AllowMultiple = true,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("Video files") { Patterns = VideoPatterns },
                    FilePickerFileTypes.All
                }
            });

            var paths = files
                .Select(file => file.TryGetLocalPath())
                .Where(path => path != null)
                .Select(path => path!)
                .ToList();

            if (paths.Count > 0)
                await AddSourcesAsync(paths);
        }
        finally
        {
            AddVideosButton.Content = "Add videos";
            AddVideosButton.IsEnabled = !IsBusy;
        }
    }

    private async void BrowseOutput_Click(object? sender, RoutedEventArgs e)
    {
        BrowseOutputButton.Content = "Opening...";
        BrowseOutputButton.IsEnabled = false;

        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select output folder",
                AllowMultiple = false
            });

            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
                OutputPathBox.Text = path;
        }
        finally
        {
            BrowseOutputButton.Content = "Browse folder";
            BrowseOutputButton.IsEnabled = !IsBusy;
        }
    }

    /// <summary>
    /// Loads videos into the list of sources and shows the first of them. A video that is already
    /// loaded is simply shown again.
    /// </summary>
    private async Task AddSourcesAsync(IReadOnlyList<string> paths)
    {
        SourceVideo? toShow = null;

        foreach (var raw in paths)
        {
            try
            {
                var path = Path.GetFullPath(raw);
                var existing = Sources.FirstOrDefault(source => string.Equals(source.File, path, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    toShow ??= existing;
                    continue;
                }

                PreviewSummaryText.Text = "Reading video...";
                var info = await MediaProbe.ProbeAsync(path, CancellationToken.None);

                // Two drops of the same file can both get this far while the first is still being read.
                if (Sources.Any(source => string.Equals(source.File, path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var added = new SourceVideo(info, CurrentSegmentSeconds());
                Sources.Add(added);
                EnsureOutputFolder(path);
                PlanClips(added);

                Log($"{added.Name}: {info.Width}x{info.Height}, {ClipFormat.Time(info.Duration)} long, {added.Clips.Count} clip(s).");
                toShow ??= added;
            }
            catch (Exception ex)
            {
                await ReportErrorAsync(ex);
            }
        }

        if (toShow != null)
            SetActiveSource(toShow);

        UpdateSourcesUi();
    }

    /// <summary>Shows a source's clips in the list and its clip length in the box.</summary>
    private void SetActiveSource(SourceVideo? source)
    {
        _activeSource = source;

        // The clip preview belongs to the list that is showing, so it lets go of a clip from another video.
        if (_clipItem != null && source?.Clips.Contains(_clipItem) != true)
        {
            _clipItem = null;
            if (_mode == PreviewMode.Clip)
                LoadClip(autoPlay: false);

            UpdateClipPanel();
        }

        _syncingUi = true;
        SourceList.SelectedItem = source;
        if (source != null)
            SegmentSecondsBox.Value = source.SegmentSeconds;
        _syncingUi = false;

        PreviewList.ItemsSource = source?.Clips;
        if (source != null)
            SourceList.ScrollIntoView(source);

        UpdateSourcesUi();
    }

    private void SourceList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncingUi)
            return;

        if (SourceList.SelectedItem is SourceVideo source)
        {
            if (!ReferenceEquals(source, _activeSource))
                SetActiveSource(source);
        }
        else if (_activeSource != null)
        {
            // One video is always shown while any are loaded.
            _syncingUi = true;
            SourceList.SelectedItem = _activeSource;
            _syncingUi = false;
        }
    }

    /// <summary>The row of videos scrolls sideways, which a mouse wheel does not do by itself.</summary>
    private void SourceList_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (SourceList.FindDescendantOfType<ScrollViewer>() is not { } viewer)
            return;

        var furthest = Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width);
        var offset = Math.Clamp(viewer.Offset.X - (e.Delta.Y + e.Delta.X) * 70, 0, furthest);
        viewer.Offset = new Vector(offset, 0);
        e.Handled = true;
    }

    private void RemoveSource_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SourceVideo source })
            RemoveSource(source);
    }

    private void RemoveSource(SourceVideo source)
    {
        var index = Sources.IndexOf(source);
        if (index < 0)
            return;

        source.Work?.Cancel();
        ReleaseClips(source);
        Sources.RemoveAt(index);

        if (ReferenceEquals(_replanSource, source))
            _replanSource = null;

        if (ReferenceEquals(_activeSource, source))
            SetActiveSource(Sources.Count == 0 ? null : Sources[Math.Min(index, Sources.Count - 1)]);

        UpdateSourcesUi();
    }

    /// <summary>Suggests an output folder next to the given video when none has been chosen yet.</summary>
    private void EnsureOutputFolder(string videoFile)
    {
        if (string.IsNullOrWhiteSpace(OutputPathBox.Text))
            OutputPathBox.Text = Path.Combine(Path.GetDirectoryName(videoFile) ?? Environment.CurrentDirectory, "split-output");
    }

    private int CurrentSegmentSeconds() => Math.Max(1, (int)(SegmentSecondsBox.Value ?? 30));

    private void SegmentSecondsBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        // With no video loaded the box simply sets the clip length the next one starts with.
        if (!_ready || _syncingUi || _activeSource is not { } source)
            return;

        source.SegmentSeconds = CurrentSegmentSeconds();
        _replanSource = source;

        // Wait for the typing or spinning to settle before rebuilding the whole list.
        _segmentTimer.Stop();
        _segmentTimer.Start();
    }

    /// <summary>Splits a source into clips of its own length, replacing the clips it had.</summary>
    private void PlanClips(SourceVideo source)
    {
        source.Work?.Cancel();
        var work = new CancellationTokenSource();
        source.Work = work;

        ReleaseClips(source);

        try
        {
            foreach (var segment in FfmpegClipSplitter.CreatePlan(source.Info.Duration, source.SegmentSeconds))
            {
                var item = new ClipPreviewItem(source.Info, segment);
                item.PropertyChanged += PreviewItem_PropertyChanged;
                source.Clips.Add(item);
            }
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
        }

        UpdateSourcesUi();
        _ = LoadSourceThumbnailsAsync(source, work);
    }

    /// <summary>Empties a source's clip list, taking its clip out of the player first.</summary>
    private void ReleaseClips(SourceVideo source)
    {
        // The clip in the player belongs to the list that is about to go.
        if (_clipItem != null && source.Clips.Contains(_clipItem))
        {
            _clipItem = null;
            if (_mode == PreviewMode.Clip)
                _player.Unload();

            UpdateClipPanel();
        }

        foreach (var item in source.Clips)
            item.PropertyChanged -= PreviewItem_PropertyChanged;

        source.Clips.Clear();
    }

    private async Task LoadSourceThumbnailsAsync(SourceVideo source, CancellationTokenSource work)
    {
        try
        {
            await LoadThumbnailsAsync(source.File, source.Clips.ToList(), work.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
        finally
        {
            if (ReferenceEquals(source.Work, work))
                source.Work = null;

            work.Dispose();
        }
    }

    private async Task LoadThumbnailsAsync(string input, IReadOnlyList<ClipPreviewItem> items, CancellationToken token)
    {
        var splitter = new FfmpegClipSplitter(Log);
        var folder = GetThumbnailFolder(input);
        using var gate = new SemaphoreSlim(ThumbnailWorkers);

        async Task LoadAsync(ClipPreviewItem item)
        {
            await gate.WaitAsync(token);
            try
            {
                var path = Path.Combine(folder, BuildThumbnailName(item.Segment));
                if (!File.Exists(path))
                {
                    // Written under a temporary name so a cancelled run never leaves a broken thumbnail behind.
                    var partial = path + ".part.jpg";
                    await splitter.CreateThumbnailAsync(input, item.Segment, partial, token);
                    File.Move(partial, path, overwrite: true);
                }

                token.ThrowIfCancellationRequested();
                await using var stream = File.OpenRead(path);
                item.Thumbnail = new Bitmap(stream);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A missing thumbnail is not worth interrupting the user for.
            }
            finally
            {
                gate.Release();
            }
        }

        await Task.WhenAll(items.Select(LoadAsync));
    }

    private void SelectAll_Click(object? sender, RoutedEventArgs e)
    {
        if (_activeSource == null)
            return;

        foreach (var item in _activeSource.Clips)
            item.IsSelected = true;
    }

    private void ClearSelection_Click(object? sender, RoutedEventArgs e)
    {
        if (_activeSource == null)
            return;

        foreach (var item in _activeSource.Clips)
            item.IsSelected = false;
    }

    private void PreviewItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClipPreviewItem.IsSelected))
            UpdateSourcesUi();
    }

    /// <summary>Refreshes everything that depends on which videos are loaded and which clips are ticked.</summary>
    private void UpdateSourcesUi()
    {
        var source = _activeSource;
        var hasClips = source is { Clips.Count: > 0 };

        SourcesEmptyText.IsVisible = Sources.Count == 0;
        SourceList.IsVisible = Sources.Count > 0;
        SelectAllButton.IsEnabled = hasClips;
        ClearSelectionButton.IsEnabled = hasClips;

        if (source == null)
        {
            PreviewSummaryText.Text = "No video loaded";
            return;
        }

        var selected = source.Clips.Count(item => item.IsSelected);
        PreviewSummaryText.Text = Sources.Count > 1
            ? $"{selected} of {source.Clips.Count} clips selected in this video"
            : $"{selected} of {source.Clips.Count} clips selected";
    }

    private void PreviewList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || PreviewList.SelectedItem is not ClipPreviewItem item || item == _clipItem)
            return;

        if (_mode == PreviewMode.Clip)
        {
            // While the clip preview is open, clicking a row shows that clip.
            ShowClip(item, autoPlay: false);
        }
        else
        {
            // Otherwise the row is remembered, so the clip preview opens on it.
            _clipItem = item;
            UpdateClipPanel();
        }
    }

    private void PlayClip_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ClipPreviewItem item })
            ShowClip(item, autoPlay: true);
    }

    private void ClipRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: ClipPreviewItem item } row)
            return;

        // Double-clicking the checkbox or one of the row's buttons is not a request to play.
        for (var visual = e.Source as Visual; visual != null && !ReferenceEquals(visual, row); visual = visual.GetVisualParent())
        {
            if (visual is Button)
                return;
        }

        ShowClip(item, autoPlay: true);
    }

    private void SendToTop_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ClipPreviewItem item })
            AssignClip(0, item);
    }

    private void SendToBottom_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ClipPreviewItem item })
            AssignClip(1, item);
    }

    // ---------------------------------------------------------------- OBS Studio replays

    /// <summary>Opens the list of replays that OBS Studio has saved, newest first.</summary>
    private void ObsReplay_Click(object? sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var setup = ObsStudio.FindSetup();

        if (setup == null)
        {
            menu.Items.Add(new MenuItem { Header = "OBS Studio was not found on this computer", IsEnabled = false });
            menu.ShowAt(ObsButton);
            return;
        }

        var replays = ObsStudio.FindReplays(setup, ObsMenuReplays);
        if (replays.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = $"No replays in {Path.GetFileName(setup.Folder.TrimEnd(Path.DirectorySeparatorChar))} yet", IsEnabled = false });
            menu.Items.Add(new MenuItem
            {
                Header = setup.ReplayBufferEnabled
                    ? "Press Save Replay in OBS and it shows up here"
                    : "Turn on the replay buffer in OBS: Settings, Output, Replay Buffer",
                IsEnabled = false
            });
        }

        for (var index = 0; index < replays.Count; index++)
        {
            var replay = replays[index];
            var item = new MenuItem { Header = DescribeReplay(replay, latest: index == 0) };
            item.Click += async (_, _) => await AddSourcesAsync(new[] { replay.File });
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var watch = new MenuItem
        {
            Header = "Add new replays as OBS saves them",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _obsWatcher != null
        };
        watch.Click += (_, _) => ToggleObsWatch(setup);
        menu.Items.Add(watch);

        var open = new MenuItem { Header = "Open the OBS recordings folder" };
        open.Click += (_, _) => OpenWithShell(setup.Folder);
        menu.Items.Add(open);

        menu.ShowAt(ObsButton);
    }

    private static Control DescribeReplay(ObsReplay replay, bool latest)
    {
        var name = new TextBlock
        {
            Text = replay.Name,
            FontWeight = latest ? FontWeight.SemiBold : FontWeight.Normal,
            MaxWidth = 300,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var age = new TextBlock
        {
            Text = (latest ? "latest  ·  " : string.Empty) + DescribeAge(DateTime.Now - replay.SavedAt),
            Foreground = SolidColorBrush.Parse("#7C8BA3"),
            Margin = new Thickness(14, 0, 0, 0)
        };

        return new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { name, age } };
    }

    private static string DescribeAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
            return "just now";
        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1))
            return $"{(int)age.TotalHours} h ago";

        return $"{(int)age.TotalDays} d ago";
    }

    private void ToggleObsWatch(ObsSetup setup)
    {
        if (_obsWatcher != null)
        {
            StopWatchingObs();
            Log("Stopped watching for OBS replays.");
            return;
        }

        try
        {
            if (!Directory.Exists(setup.Folder))
            {
                Log($"ERROR: The OBS recordings folder does not exist yet: {setup.Folder}");
                return;
            }

            var watcher = new FileSystemWatcher(setup.Folder)
            {
                NotifyFilter = NotifyFilters.FileName,
                IncludeSubdirectories = false
            };

            // OBS may write under a temporary name and rename the file when it is complete.
            watcher.Created += (_, args) => OnObsFileAppeared(args.FullPath);
            watcher.Renamed += (_, args) => OnObsFileAppeared(args.FullPath);
            watcher.EnableRaisingEvents = true;

            _obsWatcher = watcher;
            _obsWatched = setup;
            Log($"Watching {setup.Folder}. Save a replay in OBS and it is added to the source videos.");
        }
        catch (Exception ex)
        {
            Log("ERROR: The OBS recordings folder could not be watched. " + ex.Message);
        }
    }

    private void StopWatchingObs()
    {
        _obsWatcher?.Dispose();
        _obsWatcher = null;
        _obsWatched = null;
    }

    /// <summary>Called on a background thread when a file shows up in the OBS recordings folder.</summary>
    private void OnObsFileAppeared(string path)
    {
        if (_obsWatched is not { } setup || !setup.IsReplay(path))
            return;

        lock (_obsPending)
        {
            if (!_obsPending.Add(path))
                return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (!await WaitUntilWrittenAsync(path))
                    return;

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (_obsWatcher == null)
                        return;

                    Log($"New OBS replay: {Path.GetFileName(path)}");
                    await AddSourcesAsync(new[] { path });
                });
            }
            finally
            {
                lock (_obsPending)
                    _obsPending.Remove(path);
            }
        });
    }

    /// <summary>Waits until OBS has finished writing a file: it can be opened and has stopped growing.</summary>
    private static async Task<bool> WaitUntilWrittenAsync(string path)
    {
        long lastSize = -1;
        var steady = 0;

        // Saving a replay takes a moment; two minutes covers even a very long buffer on a slow disk.
        for (var attempt = 0; attempt < 240; attempt++)
        {
            await Task.Delay(500);

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    if (attempt >= 6)
                        return false;

                    continue;
                }

                // While OBS still has the file open for writing, this open is refused.
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                }

                steady = info.Length > 0 && info.Length == lastSize ? steady + 1 : 0;
                lastSize = info.Length;

                if (steady >= 2)
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                steady = 0;
            }
        }

        return false;
    }
}
