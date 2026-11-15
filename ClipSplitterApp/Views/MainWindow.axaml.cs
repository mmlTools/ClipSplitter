using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClipSplitterApp.Controls;
using ClipSplitterApp.Playback;
using ClipSplitterApp.Services;

namespace ClipSplitterApp.Views;

public partial class MainWindow : Window
{
    private const string DonationUrl = "https://www.paypal.com/donate/?hosted_button_id=ZKTLLYY9ADWYQ";
    private const string FfmpegDownloadUrl = "https://ffmpeg.org/download.html";
    private const string ExportSelectedText = "Export selected";
    private const string ExportShortText = "Export this short";
    private const string VerticalSuffix = "vertical";
    private const double DragThreshold = 6;
    private const int ThumbnailWorkers = 3;
    private const int MaxLogLength = 20000;

    private readonly ClipPlayer _player;
    private readonly VerticalComposition _composition = new();
    private readonly DispatcherTimer _segmentTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly bool _ready;

    private CancellationTokenSource? _exportCts;
    private CancellationTokenSource? _previewCts;
    private ClipPreviewItem? _clipItem;
    private PreviewMode _mode = PreviewMode.Vertical;
    private bool _syncingUi;
    private bool _ffmpegDialogOpen;
    private double _volumeBeforeMute = 1;

    private ClipPreviewItem? _dragItem;
    private Control? _dragRow;
    private Point _dragStart;
    private bool _dragging;

    public ObservableCollection<ClipPreviewItem> PreviewItems { get; } = new();

    public MainWindow()
        : this(null)
    {
    }

    public MainWindow(string? startupFile)
    {
        InitializeComponent();
        DataContext = this;

        Title = $"ClipSplitter {AppInfo.Version}";
        TitleText.Text = $"ClipSplitter {AppInfo.Version}  ·  long videos into shorts";

        _player = new ClipPlayer(Log, callback => RequestAnimationFrame(_ => callback()));
        _player.FrameUpdated += () => PreviewSurface.InvalidateVisual();
        _player.StateChanged += UpdateTransport;
        _player.Failed += OnPlayerFailed;

        PreviewSurface.Player = _player;
        PreviewSurface.Composition = _composition;
        PreviewSurface.Mode = _mode;

        PositionBar.Seeking += fraction => _player.Seek(_player.Duration * fraction);
        VolumeBar.Seeking += volume =>
        {
            _player.Volume = volume;
            UpdateVolumeIcon();
        };

        _segmentTimer.Tick += async (_, _) =>
        {
            _segmentTimer.Stop();
            await RefreshPreviewAsync();
        };

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DragLeaveEvent, Window_DragLeave);
        AddHandler(DragDrop.DropEvent, Window_Drop);
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);

        Closed += (_, _) =>
        {
            _segmentTimer.Stop();
            _exportCts?.Cancel();
            _previewCts?.Cancel();
            _player.Dispose();
        };

        _ready = true;

        Log("Ready. Select a video, or drop one onto the window.");
        UpdatePreviewSummary();
        UpdateStudioPanel();
        UpdateClipPanel();
        UpdateTransport();

        Opened += async (_, _) =>
        {
            RemoveLegacyPreviewCache();

            if (!await FfmpegTools.IsAvailableAsync())
                Log("FFmpeg was not found. Install FFmpeg and ffprobe and add them to PATH, or place them in a tools/ffmpeg folder beside the app.");

            if (!string.IsNullOrWhiteSpace(startupFile) && File.Exists(startupFile))
                await LoadSourceAsync(startupFile);
        };
    }

    // ---------------------------------------------------------------- Source video and clip list

    private async void BrowseInput_Click(object? sender, RoutedEventArgs e)
    {
        BrowseInputButton.Content = "Opening...";
        BrowseInputButton.IsEnabled = false;

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select video",
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("Video files") { Patterns = new[] { "*.mp4", "*.mov", "*.mkv", "*.avi", "*.webm", "*.m4v" } },
                    FilePickerFileTypes.All
                }
            });

            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
                await LoadSourceAsync(path);
        }
        finally
        {
            BrowseInputButton.Content = "Browse video";
            BrowseInputButton.IsEnabled = _exportCts == null;
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
            BrowseOutputButton.IsEnabled = _exportCts == null;
        }
    }

    private async Task LoadSourceAsync(string path)
    {
        InputPathBox.Text = path;
        EnsureOutputFolder(path);
        await RefreshPreviewAsync();
    }

    /// <summary>Suggests an output folder next to the given video when none has been chosen yet.</summary>
    private void EnsureOutputFolder(string videoFile)
    {
        if (string.IsNullOrWhiteSpace(OutputPathBox.Text))
            OutputPathBox.Text = Path.Combine(Path.GetDirectoryName(videoFile) ?? Environment.CurrentDirectory, "split-output");
    }

    private void SegmentSecondsBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!_ready)
            return;

        // Wait for the typing or spinning to settle before rebuilding the whole list.
        _segmentTimer.Stop();
        _segmentTimer.Start();
    }

    private async Task RefreshPreviewAsync()
    {
        var input = InputPathBox.Text?.Trim() ?? string.Empty;
        var seconds = (int)(SegmentSecondsBox.Value ?? 30);

        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        var token = cts.Token;

        // The clip in the player belongs to the list that is about to be replaced.
        if (_clipItem != null)
        {
            _clipItem = null;
            if (_mode == PreviewMode.Clip)
                _player.Unload();

            UpdateClipPanel();
        }

        try
        {
            if (!File.Exists(input) || seconds < 1)
            {
                ClearPreviewItems();
                return;
            }

            PreviewSummaryText.Text = "Reading video...";
            var info = await MediaProbe.ProbeAsync(input, token);
            token.ThrowIfCancellationRequested();

            ClearPreviewItems();
            foreach (var segment in FfmpegClipSplitter.CreatePlan(info.Duration, seconds))
            {
                var item = new ClipPreviewItem(info, segment);
                item.PropertyChanged += PreviewItem_PropertyChanged;
                PreviewItems.Add(item);
            }

            Log($"{Path.GetFileName(input)}: {info.Width}x{info.Height}, {ClipFormat.Time(info.Duration)} long, {PreviewItems.Count} clip(s).");
            UpdatePreviewSummary();
            await LoadThumbnailsAsync(input, PreviewItems.ToList(), token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ClearPreviewItems();
            await ReportErrorAsync(ex);
        }
        finally
        {
            if (_previewCts == cts)
                _previewCts = null;

            cts.Dispose();
        }
    }

    private void ClearPreviewItems()
    {
        foreach (var item in PreviewItems)
            item.PropertyChanged -= PreviewItem_PropertyChanged;

        PreviewItems.Clear();
        UpdatePreviewSummary();
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
        foreach (var item in PreviewItems)
            item.IsSelected = true;
    }

    private void ClearSelection_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in PreviewItems)
            item.IsSelected = false;
    }

    private void PreviewItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClipPreviewItem.IsSelected))
            UpdatePreviewSummary();
    }

    private void UpdatePreviewSummary()
    {
        var selected = PreviewItems.Count(item => item.IsSelected);
        PreviewSummaryText.Text = PreviewItems.Count == 0
            ? "No video loaded"
            : $"{selected} of {PreviewItems.Count} clips selected";
    }

    private void PreviewList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // While the clip preview is open, clicking a row shows that clip.
        if (_ready && _mode == PreviewMode.Clip && PreviewList.SelectedItem is ClipPreviewItem item && item != _clipItem)
            ShowClip(item, autoPlay: false);
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
            AssignSlot(0, item.ToClipSource());
    }

    private void SendToBottom_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ClipPreviewItem item })
            AssignSlot(1, item.ToClipSource());
    }

    // ---------------------------------------------------------------- Dragging clips onto the canvas

    private void ClipRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ClipPreviewItem item } row
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _dragItem = item;
        _dragRow = row;
        _dragStart = e.GetPosition(this);
        _dragging = false;
    }

    private void ClipRow_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem == null || !ReferenceEquals(sender, _dragRow))
            return;

        var position = e.GetPosition(this);

        if (!_dragging)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                EndDrag(null);
                return;
            }

            if (Math.Abs(position.X - _dragStart.X) < DragThreshold && Math.Abs(position.Y - _dragStart.Y) < DragThreshold)
                return;

            _dragging = true;
            e.Pointer.Capture(_dragRow);
            SetMode(PreviewMode.Vertical);

            DragGhostImage.Source = _dragItem.Thumbnail;
            DragGhostText.Text = _dragItem.Label;
            DragGhost.IsVisible = true;
        }

        Canvas.SetLeft(DragGhost, position.X + 14);
        Canvas.SetTop(DragGhost, position.Y + 12);
        PreviewSurface.HighlightSlot = PreviewSurface.HitTestSlot(e.GetPosition(PreviewSurface));
    }

    private void ClipRow_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragItem == null)
            return;

        var item = _dragItem;
        var slot = _dragging ? PreviewSurface.HitTestSlot(e.GetPosition(PreviewSurface)) : -1;
        EndDrag(e.Pointer);

        if (slot >= 0)
            AssignSlot(slot, item.ToClipSource());
    }

    private void ClipRow_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag(null);

    private void EndDrag(IPointer? pointer)
    {
        var wasDragging = _dragging;
        _dragItem = null;
        _dragRow = null;
        _dragging = false;
        DragGhost.IsVisible = false;
        DragGhostImage.Source = null;
        PreviewSurface.HighlightSlot = -1;

        if (wasDragging)
            pointer?.Capture(null);
    }

    private void Window_DragOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files) || _exportCts != null)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        PreviewSurface.HighlightSlot = PreviewSurface.HitTestSlot(e.GetPosition(PreviewSurface));
    }

    private void Window_DragLeave(object? sender, DragEventArgs e) => PreviewSurface.HighlightSlot = -1;

    private async void Window_Drop(object? sender, DragEventArgs e)
    {
        var slot = PreviewSurface.HitTestSlot(e.GetPosition(PreviewSurface));
        PreviewSurface.HighlightSlot = -1;

        if (_exportCts != null)
            return;

        var path = e.Data.GetFiles()?
            .Select(file => file.TryGetLocalPath())
            .FirstOrDefault(candidate => candidate != null && File.Exists(candidate));

        if (path == null)
            return;

        // A file dropped on the vertical canvas goes straight into that slot; anywhere else it becomes the source video.
        if (slot >= 0)
            await AssignFileAsync(slot, path);
        else
            await LoadSourceAsync(path);
    }

    private async Task AssignFileAsync(int slot, string path)
    {
        try
        {
            var info = await MediaProbe.ProbeAsync(path, CancellationToken.None);
            EnsureOutputFolder(path);
            AssignSlot(slot, new ClipSource(path, TimeSpan.Zero, info.Duration, info, Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
    }

    // ---------------------------------------------------------------- Player

    private void PreviewMode_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingUi || sender is not RadioButton { IsChecked: true } radio)
            return;

        SetMode(ReferenceEquals(radio, ClipModeRadio) ? PreviewMode.Clip : PreviewMode.Vertical);
    }

    /// <summary>Switches between the vertical studio and the clip preview.</summary>
    /// <param name="mode">The view to show.</param>
    /// <param name="load">False when the caller is about to load the player itself.</param>
    private void SetMode(PreviewMode mode, bool load = true)
    {
        if (_mode == mode)
            return;

        _mode = mode;
        _player.Pause();

        _syncingUi = true;
        VerticalModeRadio.IsChecked = mode == PreviewMode.Vertical;
        ClipModeRadio.IsChecked = mode == PreviewMode.Clip;
        _syncingUi = false;

        PreviewSurface.Mode = mode;
        VerticalPanel.IsVisible = mode == PreviewMode.Vertical;
        ClipPanel.IsVisible = mode == PreviewMode.Clip;

        if (!load)
            return;

        if (mode == PreviewMode.Vertical)
        {
            LoadComposition(keepPosition: false);
        }
        else
        {
            // Opening the clip preview shows the row that is selected in the list, if any.
            _clipItem ??= PreviewList.SelectedItem as ClipPreviewItem;
            LoadClip(autoPlay: false);
            UpdateClipPanel();
        }
    }

    private void ShowClip(ClipPreviewItem item, bool autoPlay)
    {
        _clipItem = item;
        SetMode(PreviewMode.Clip, load: false);
        LoadClip(autoPlay);
        UpdateClipPanel();
    }

    private void LoadClip(bool autoPlay, TimeSpan startAt = default)
    {
        if (_mode != PreviewMode.Clip)
            return;

        if (_clipItem is not { } item)
        {
            _player.Unload();
        }
        else
        {
            var layers = new PlaybackLayer?[] { new(item.InputFile, item.Start, item.Length, item.Info) };
            var audio = item.Info.HasAudio ? new[] { 0 } : Array.Empty<int>();
            _player.Load(new PlaybackRequest(layers, audio, item.Length), autoPlay, startAt);
        }

        PreviewSurface.InvalidateVisual();
        UpdateTransport();
    }

    /// <summary>Hands the vertical composition to the player, unless it is already playing exactly that.</summary>
    private void LoadComposition(bool keepPosition)
    {
        if (_mode != PreviewMode.Vertical)
            return;

        var layers = new PlaybackLayer?[_composition.SlotCount];
        var any = false;
        for (var slot = 0; slot < layers.Length; slot++)
        {
            if (_composition.GetSlot(slot) is not { } clip)
                continue;

            layers[slot] = new PlaybackLayer(clip.File, clip.Start, clip.Length, clip.Info);
            any = true;
        }

        if (!any)
        {
            _player.Unload();
        }
        else
        {
            var request = new PlaybackRequest(layers, _composition.GetAudioSlots(), _composition.Duration);
            if (request.Signature != _player.Signature)
            {
                var startAt = keepPosition && _player.Position < request.Duration ? _player.Position : TimeSpan.Zero;
                _player.Load(request, _player.IsPlaying, startAt);
            }
        }

        PreviewSurface.InvalidateVisual();
        UpdateTransport();
    }

    private void PlayPause_Click(object? sender, RoutedEventArgs e) => _player.TogglePlay();

    private void Mute_Click(object? sender, RoutedEventArgs e)
    {
        if (_player.Volume > 0)
        {
            _volumeBeforeMute = _player.Volume;
            _player.Volume = 0;
        }
        else
        {
            _player.Volume = _volumeBeforeMute > 0.05 ? _volumeBeforeMute : 1;
        }

        VolumeBar.Value = _player.Volume;
        UpdateVolumeIcon();
    }

    private void UpdateVolumeIcon()
    {
        var audible = _player.Volume > 0.001;
        VolumeOnIcon.IsVisible = audible;
        VolumeOffIcon.IsVisible = !audible;
    }

    private void UpdateTransport()
    {
        var hasMedia = _player.HasMedia;
        var duration = _player.Duration;
        var position = _player.Position;

        PlayIcon.IsVisible = !_player.IsPlaying;
        PauseIcon.IsVisible = _player.IsPlaying;
        PlayPauseButton.IsEnabled = hasMedia;
        PositionBar.IsEnabled = hasMedia;
        PositionBar.Value = duration > TimeSpan.Zero ? position / duration : 0;
        TimeText.Text = $"{ClipFormat.Time(position)} / {ClipFormat.Time(duration)}";
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !_player.HasMedia)
            return;

        // Space still types into text boxes and still presses a focused button.
        if (FocusManager?.GetFocusedElement() is TextBox or Button)
            return;

        _player.TogglePlay();
        e.Handled = true;
    }

    private async void OnPlayerFailed(Exception ex) => await ReportErrorAsync(ex);

    // ---------------------------------------------------------------- Clip preview panel

    private void UpdateClipPanel()
    {
        var item = _clipItem;
        var hasClip = item != null;

        ClipTitleText.Text = hasClip ? $"{item!.Label}  ·  {Path.GetFileName(item.InputFile)}" : "No clip loaded";
        ClipRangeText.Text = hasClip
            ? $"{item!.TimeRange}  ({item.Duration})"
            : "Press play on a clip in the list to watch it here.";

        TrimStartButton.IsEnabled = hasClip;
        TrimEndButton.IsEnabled = hasClip;
        TrimResetButton.IsEnabled = hasClip && item!.IsTrimmed;
        ClipToTopButton.IsEnabled = hasClip;
        ClipToBottomButton.IsEnabled = hasClip;
    }

    private void TrimStart_Click(object? sender, RoutedEventArgs e)
    {
        if (_clipItem is not { } item)
            return;

        if (!item.SetRange(item.Start + _player.Position, item.End))
        {
            Log("Trim: a clip must stay at least half a second long.");
            return;
        }

        LoadClip(autoPlay: false);
        UpdateClipPanel();
    }

    private void TrimEnd_Click(object? sender, RoutedEventArgs e)
    {
        if (_clipItem is not { } item)
            return;

        if (!item.SetRange(item.Start, item.Start + _player.Position))
        {
            Log("Trim: a clip must stay at least half a second long.");
            return;
        }

        // Park just before the new ending so one press of play checks it.
        var lead = TimeSpan.FromSeconds(2);
        LoadClip(autoPlay: false, item.Length > lead ? item.Length - lead : TimeSpan.Zero);
        UpdateClipPanel();
    }

    private void TrimReset_Click(object? sender, RoutedEventArgs e)
    {
        if (_clipItem is not { } item)
            return;

        item.ResetRange();
        LoadClip(autoPlay: false);
        UpdateClipPanel();
    }

    private void ClipToTop_Click(object? sender, RoutedEventArgs e)
    {
        if (_clipItem is { } item)
            AssignSlot(0, item.ToClipSource());
    }

    private void ClipToBottom_Click(object? sender, RoutedEventArgs e)
    {
        if (_clipItem is { } item)
            AssignSlot(1, item.ToClipSource());
    }

    // ---------------------------------------------------------------- Vertical studio panel

    private void AssignSlot(int slot, ClipSource clip)
    {
        // Only the stacked layout has a bottom slot, so asking for it switches to that layout.
        if (slot == 1 && _composition.Layout != VerticalLayoutKind.Stacked)
            _composition.Layout = VerticalLayoutKind.Stacked;

        _composition.SetSlot(slot, clip);
        _composition.GetFraming(slot).Reset();

        SetMode(PreviewMode.Vertical, load: false);
        UpdateStudioPanel();
        LoadComposition(keepPosition: false);
    }

    private void ClearTop_Click(object? sender, RoutedEventArgs e) => ClearSlot(0);

    private void ClearBottom_Click(object? sender, RoutedEventArgs e) => ClearSlot(1);

    private void ClearSlot(int slot)
    {
        _composition.SetSlot(slot, null);
        _composition.GetFraming(slot).Reset();
        UpdateStudioPanel();
        LoadComposition(keepPosition: true);
    }

    private void SwapSlots_Click(object? sender, RoutedEventArgs e)
    {
        _composition.SwapSlots();
        UpdateStudioPanel();
        LoadComposition(keepPosition: true);
    }

    private void Layout_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingUi || sender is not RadioButton { IsChecked: true } radio)
            return;

        var previous = _composition.Layout;
        _composition.Layout = ReferenceEquals(radio, LayoutFillRadio) ? VerticalLayoutKind.Fill
            : ReferenceEquals(radio, LayoutBlurRadio) ? VerticalLayoutKind.Blur
            : VerticalLayoutKind.Stacked;

        // Zoom enlarges the clip in the blur layout but crops it in the others, so a framing made
        // for one does not carry over to the other.
        if (previous != _composition.Layout
            && (previous == VerticalLayoutKind.Blur || _composition.Layout == VerticalLayoutKind.Blur))
            _composition.GetFraming(0).Reset();

        UpdateStudioPanel();
        LoadComposition(keepPosition: true);
    }

    private void Audio_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingUi || sender is not RadioButton { IsChecked: true } radio)
            return;

        _composition.Audio = ReferenceEquals(radio, AudioBottomRadio) ? VerticalAudioSource.Bottom
            : ReferenceEquals(radio, AudioMixRadio) ? VerticalAudioSource.Mix
            : ReferenceEquals(radio, AudioMuteRadio) ? VerticalAudioSource.Mute
            : VerticalAudioSource.Top;

        LoadComposition(keepPosition: true);
    }

    private void UpdateStudioPanel()
    {
        var stacked = _composition.Layout == VerticalLayoutKind.Stacked;

        _syncingUi = true;

        LayoutStackedRadio.IsChecked = stacked;
        LayoutFillRadio.IsChecked = _composition.Layout == VerticalLayoutKind.Fill;
        LayoutBlurRadio.IsChecked = _composition.Layout == VerticalLayoutKind.Blur;

        // With one clip on the canvas the only choice is its own sound or none.
        AudioBottomRadio.IsVisible = stacked;
        AudioMixRadio.IsVisible = stacked;
        AudioTopRadio.Content = stacked ? "Top" : "Clip sound";

        var audio = _composition.Audio;
        AudioMuteRadio.IsChecked = audio == VerticalAudioSource.Mute;
        AudioBottomRadio.IsChecked = stacked && audio == VerticalAudioSource.Bottom;
        AudioMixRadio.IsChecked = stacked && audio == VerticalAudioSource.Mix;
        AudioTopRadio.IsChecked = audio == VerticalAudioSource.Top
                                  || (!stacked && audio is VerticalAudioSource.Bottom or VerticalAudioSource.Mix);

        _syncingUi = false;

        TopSlotTitle.Text = stacked ? "Top clip" : "Clip";
        TopSlotIconFill.Height = stacked ? 7 : 15;
        TopSlotText.Text = DescribeSlot(_composition.Top);
        BottomSlotText.Text = DescribeSlot(_composition.Bottom);
        ClearTopButton.IsVisible = _composition.Top != null;
        ClearBottomButton.IsVisible = _composition.Bottom != null;
        BottomSlotRow.IsVisible = stacked;
        SwapSlotsButton.IsVisible = stacked;
        SwapSlotsButton.IsEnabled = _composition.Top != null || _composition.Bottom != null;

        StudioHintText.Text = _composition.Layout switch
        {
            VerticalLayoutKind.Stacked => "Drag a clip on the canvas to reframe it and scroll to zoom. Drag the divider to resize the halves. Double-click resets.",
            VerticalLayoutKind.Fill => "Drag the clip on the canvas to choose what stays in frame and scroll to zoom. Double-click resets.",
            _ => "Scroll on the canvas to enlarge the clip over its blurred backdrop, then drag to reframe. Double-click resets."
        };

        PreviewSurface.InvalidateVisual();
    }

    private static string DescribeSlot(ClipSource? clip)
        => clip == null
            ? "Drag a clip here"
            : $"{clip.Label}  ·  {ClipFormat.Time(clip.Start)} - {ClipFormat.Time(clip.End)}";

    // ---------------------------------------------------------------- Export

    private void ExportFormat_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;

        // Vertical shorts are always re-encoded, so the option only applies to original clips.
        ReencodeCheckBox.IsEnabled = FormatOriginalRadio.IsChecked == true && _exportCts == null;
    }

    private async void ExportSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (CancelRunningExport())
            return;

        var input = InputPathBox.Text?.Trim() ?? string.Empty;
        var output = OutputPathBox.Text?.Trim() ?? string.Empty;

        if (!File.Exists(input))
        {
            Log("ERROR: Select a source video first.");
            return;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            Log("ERROR: Select an output folder.");
            return;
        }

        var items = PreviewItems.Where(item => item.IsSelected).ToList();
        if (items.Count == 0)
        {
            Log("ERROR: Select at least one clip to export.");
            return;
        }

        if (FormatVerticalRadio.IsChecked != true)
        {
            var options = new SplitOptions(input, output, ReencodeCheckBox.IsChecked == true, items.Select(item => item.ToSegment()).ToList());
            await RunExportAsync(SplitButton, ExportSelectedText, output, async (progress, token) =>
            {
                await new FfmpegClipSplitter(Log).SplitAsync(options, progress, token);
                return items.Count;
            });
            return;
        }

        var template = _composition.Clone();
        if (template.Layout == VerticalLayoutKind.Stacked && template.Bottom == null)
        {
            Log("ERROR: The stacked layout needs a bottom clip. Drag one into the bottom slot, or choose Fill or Blur.");
            return;
        }

        var sources = items.Select(item => item.ToClipSource()).ToList();
        await RunExportAsync(SplitButton, ExportSelectedText, output, async (progress, token) =>
        {
            for (var index = 0; index < sources.Count; index++)
            {
                token.ThrowIfCancellationRequested();

                // Each selected clip takes the top slot of the studio layout in turn.
                var composition = template.Clone();
                composition.Top = sources[index];

                // Numbered rather than overwritten, so a short that was framed by hand is never lost.
                var source = sources[index];
                var file = ClipFormat.UniquePath(Path.Combine(
                    output,
                    ClipFormat.OutputFileName(source.File, source.Start, source.Start + composition.Duration, VerticalExporter.OutputExtension, VerticalSuffix)));

                Log($"Creating {Path.GetFileName(file)}");
                var done = index;
                await VerticalExporter.ExportAsync(composition, file, fraction => progress((done + fraction) / sources.Count * 100d), token);
            }

            return sources.Count;
        });
    }

    private async void ExportShort_Click(object? sender, RoutedEventArgs e)
    {
        if (CancelRunningExport())
            return;

        var composition = _composition.Clone();
        var first = composition.Top ?? (composition.SlotCount == 2 ? composition.Bottom : null);

        if (first == null)
        {
            Log("ERROR: Drag a clip onto the vertical canvas first.");
            return;
        }

        if (!composition.IsComplete)
        {
            Log("ERROR: The stacked layout needs two clips. Add the missing one, or choose Fill or Blur.");
            return;
        }

        EnsureOutputFolder(first.File);
        var output = OutputPathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(output))
        {
            Log("ERROR: Select an output folder.");
            return;
        }

        var name = ClipFormat.OutputFileName(first.File, first.Start, first.Start + composition.Duration, VerticalExporter.OutputExtension, VerticalSuffix);

        await RunExportAsync(ExportShortButton, ExportShortText, output, async (progress, token) =>
        {
            // Numbered rather than overwritten, so trying another layout keeps the earlier short.
            var file = ClipFormat.UniquePath(Path.Combine(output, name));
            Log($"Creating {Path.GetFileName(file)}");
            await VerticalExporter.ExportAsync(composition, file, fraction => progress(fraction * 100d), token);
            return 1;
        });
    }

    /// <summary>Stops the export in progress, if there is one. Both export buttons act as Cancel while it runs.</summary>
    private bool CancelRunningExport()
    {
        if (_exportCts == null)
            return false;

        _exportCts.Cancel();
        return true;
    }

    private async Task RunExportAsync(Button button, string idleText, string output, Func<Action<double>, CancellationToken, Task<int>> work)
    {
        var cts = new CancellationTokenSource();
        _exportCts = cts;
        var completed = false;

        // ffmpeg reports progress from a background thread.
        void ReportProgress(double percent) => Dispatcher.UIThread.Post(() =>
        {
            if (_exportCts == cts && percent > ExportProgress.Value)
                ExportProgress.Value = percent;
        });

        _player.Pause();
        ExportProgress.Value = 0;
        SetExportingState(button, idleText, true);

        try
        {
            Directory.CreateDirectory(output);
            var count = await work(ReportProgress, cts.Token);
            ExportProgress.Value = 100;
            Log($"Done. Exported {count} file(s) to {output}");
            completed = true;
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled by user.");
            ExportProgress.Value = 0;
        }
        catch (Exception ex)
        {
            ExportProgress.Value = 0;
            await ReportErrorAsync(ex);
        }
        finally
        {
            _exportCts = null;
            cts.Dispose();
            SetExportingState(button, idleText, false);
        }

        if (completed)
            await ShowDonationDialogAsync(output);
    }

    private void SetExportingState(Button activeButton, string idleText, bool isExporting)
    {
        activeButton.Content = isExporting ? "Cancel export" : idleText;

        foreach (var button in new[] { SplitButton, ExportShortButton })
        {
            if (!ReferenceEquals(button, activeButton))
                button.IsEnabled = !isExporting;
        }

        BrowseInputButton.IsEnabled = !isExporting;
        BrowseOutputButton.IsEnabled = !isExporting;
        SegmentSecondsBox.IsEnabled = !isExporting;
        FormatOriginalRadio.IsEnabled = !isExporting;
        FormatVerticalRadio.IsEnabled = !isExporting;
        ReencodeCheckBox.IsEnabled = !isExporting && FormatOriginalRadio.IsChecked == true;
    }

    // ---------------------------------------------------------------- Dialogs

    private async Task ReportErrorAsync(Exception ex)
    {
        Log("ERROR: " + ex.Message);

        if (ex is not FfmpegNotFoundException || _ffmpegDialogOpen)
            return;

        _ffmpegDialogOpen = true;
        try
        {
            await ShowFfmpegDownloadDialogAsync();
        }
        finally
        {
            _ffmpegDialogOpen = false;
        }
    }

    private async Task ShowDonationDialogAsync(string outputFolder)
    {
        var dialog = CreateDialogWindow("Export complete");

        var donateButton = CreateDialogButton("Donate", true);
        donateButton.Click += (_, _) => OpenWithShell(DonationUrl);

        var openFolderButton = CreateDialogButton("Open folder");
        openFolderButton.Click += (_, _) => OpenWithShell(outputFolder);

        dialog.Content = CreateDialogContent(
            dialog,
            "Export complete",
            "If Clip Splitter saved you some time, a small donation helps keep the project moving.",
            donateButton,
            openFolderButton);

        await dialog.ShowDialog(this);
    }

    private async Task ShowFfmpegDownloadDialogAsync()
    {
        var dialog = CreateDialogWindow("FFmpeg required");

        var downloadButton = CreateDialogButton("Download FFmpeg", true);
        downloadButton.Click += (_, _) => OpenWithShell(FfmpegDownloadUrl);

        dialog.Content = CreateDialogContent(
            dialog,
            "FFmpeg required",
            "Install FFmpeg and ffprobe, add them to PATH, or place them in a tools/ffmpeg folder beside the app.",
            downloadButton);

        await dialog.ShowDialog(this);
    }

    private static Window CreateDialogWindow(string title)
    {
        return new Window
        {
            Title = title,
            Width = 440,
            MinWidth = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            SystemDecorations = SystemDecorations.None,
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.Transparent
        };
    }

    private static Control CreateDialogContent(Window dialog, string title, string message, params Button[] buttons)
    {
        var buttonPanel = new StackPanel
        {
            Spacing = 10
        };

        foreach (var button in buttons)
            buttonPanel.Children.Add(button);

        return new Border
        {
            Padding = new Thickness(22),
            Background = SolidColorBrush.Parse("#151922"),
            BorderBrush = SolidColorBrush.Parse("#334155"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = new StackPanel
            {
                Spacing = 18,
                Children =
                {
                    CreateDialogHeader(dialog, title),
                    new TextBlock
                    {
                        Text = message,
                        Foreground = SolidColorBrush.Parse("#cbd5e1"),
                        TextWrapping = TextWrapping.Wrap
                    },
                    buttonPanel
                }
            }
        };
    }

    private static Grid CreateDialogHeader(Window dialog, string title)
    {
        var closeButton = new Button
        {
            Content = "X",
            Width = 30,
            Height = 30,
            MinWidth = 30,
            MinHeight = 30,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = Brushes.Transparent,
            Foreground = SolidColorBrush.Parse("#aeb8c7"),
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            FontWeight = FontWeight.SemiBold
        };
        closeButton.Click += (_, _) => dialog.Close();
        Grid.SetColumn(closeButton, 1);

        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    Foreground = Brushes.White,
                    FontSize = 22,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                },
                closeButton
            }
        };
    }

    private static Button CreateDialogButton(string text, bool isPrimary = false)
    {
        return new Button
        {
            Content = text,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = SolidColorBrush.Parse(isPrimary ? "#2563eb" : "#202735"),
            Foreground = Brushes.White,
            BorderBrush = SolidColorBrush.Parse(isPrimary ? "#3b82f6" : "#334155"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            FontWeight = FontWeight.SemiBold
        };
    }

    // ---------------------------------------------------------------- Window chrome

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void SupportProject_Click(object? sender, RoutedEventArgs e) => OpenWithShell(DonationUrl);

    private void OpenFfmpegDownload_Click(object? sender, RoutedEventArgs e) => OpenWithShell(FfmpegDownloadUrl);

    private void ResizeTop_PointerPressed(object? sender, PointerPressedEventArgs e) => Resize(WindowEdge.North, e);
    private void ResizeBottom_PointerPressed(object? sender, PointerPressedEventArgs e) => Resize(WindowEdge.South, e);
    private void ResizeLeft_PointerPressed(object? sender, PointerPressedEventArgs e) => Resize(WindowEdge.West, e);
    private void ResizeRight_PointerPressed(object? sender, PointerPressedEventArgs e) => Resize(WindowEdge.East, e);
    private void ResizeTopLeft_PointerPressed(object? sender, PointerPressedEventArgs e) => Resize(WindowEdge.NorthWest, e);
    private void ResizeTopRight_PointerPressed(object? sender, PointerPressedEventArgs e) => Resize(WindowEdge.NorthEast, e);
    private void ResizeBottomLeft_PointerPressed(object? sender, PointerPressedEventArgs e) => Resize(WindowEdge.SouthWest, e);
    private void ResizeBottomRight_PointerPressed(object? sender, PointerPressedEventArgs e) => Resize(WindowEdge.SouthEast, e);

    private void Resize(WindowEdge edge, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(edge, e);
    }

    // ---------------------------------------------------------------- Helpers

    /// <summary>Opens a web page or a folder with whatever the system uses for it.</summary>
    private void OpenWithShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log($"Could not open {target}: {ex.Message}");
        }
    }

    private static string GetThumbnailFolder(string inputFile)
    {
        var stamp = $"{Path.GetFileNameWithoutExtension(inputFile)}-{File.GetLastWriteTimeUtc(inputFile).Ticks}";
        return Path.Combine(Path.GetTempPath(), "ClipSplitter", ClipFormat.SanitizeFileName(stamp), "thumbs-256");
    }

    private static string BuildThumbnailName(ClipSegment segment)
        => $"{segment.Index:D4}-{ClipFormat.FileStamp(segment.Start)}-{ClipFormat.FileStamp(segment.End)}.jpg";

    /// <summary>
    /// Version 1 previewed a clip by rendering it to a temporary file and opening an external
    /// player. Those files are never used again, so they are removed to give the disk space back.
    /// </summary>
    private static void RemoveLegacyPreviewCache()
    {
        Task.Run(() =>
        {
            try
            {
                var root = Path.Combine(Path.GetTempPath(), "ClipSplitter");
                if (!Directory.Exists(root))
                    return;

                foreach (var source in Directory.GetDirectories(root))
                {
                    var clips = Path.Combine(source, "clips");
                    if (Directory.Exists(clips))
                        Directory.Delete(clips, recursive: true);
                }
            }
            catch
            {
                // Leftover cache files are harmless.
            }
        });
    }

    private void Log(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var text = LogBox.Text + $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
            if (text.Length > MaxLogLength)
            {
                var cut = text.IndexOf('\n', text.Length - MaxLogLength);
                text = cut >= 0 ? text[(cut + 1)..] : text[^MaxLogLength..];
            }

            LogBox.Text = text;
            LogBox.CaretIndex = text.Length;
        });
    }
}
