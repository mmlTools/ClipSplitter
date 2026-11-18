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
using ClipSplitterApp.Captions;
using ClipSplitterApp.Controls;
using ClipSplitterApp.Playback;
using ClipSplitterApp.Services;
using ClipSplitterApp.TextOverlays;

namespace ClipSplitterApp.Views;

public partial class MainWindow : Window
{
    private const string DonationUrl = "https://www.paypal.com/donate/?hosted_button_id=ZKTLLYY9ADWYQ";
    private const string ExportSelectedText = "Export selected";
    private const string ExportShortText = "Export this short";
    private const string CancelExportText = "Cancel export";
    private const double DragThreshold = 6;
    private const int ThumbnailWorkers = 3;
    private const int MaxLogLength = 20000;

    /// <summary>Height of the activity log while the vertical studio needs the room for its settings.</summary>
    private const double CompactLogHeight = 96;

    private static readonly string[] VideoPatterns = { "*.mp4", "*.mov", "*.mkv", "*.avi", "*.webm", "*.m4v", "*.flv", "*.ts" };

    private readonly ClipPlayer _player;
    private readonly AppSettings _settings;
    private readonly string? _settingsFile;
    private readonly VerticalComposition _composition = new();
    private readonly DispatcherTimer _segmentTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly bool _ready;

    private CancellationTokenSource? _workCts;
    private ClipPreviewItem? _clipItem;
    private PreviewMode _mode = PreviewMode.Vertical;
    private StudioTab _studioTab = StudioTab.Layout;
    private bool _syncingUi;
    private bool _ffmpegDialogOpen;
    private double _volumeBeforeMute = 1;

    private ClipPreviewItem? _dragItem;
    private Control? _dragRow;
    private Point _dragStart;
    private bool _dragging;

    public MainWindow()
        : this(null)
    {
    }

    /// <param name="startupFiles">Videos to load once the window is open.</param>
    /// <param name="settingsFile">Where remembered choices are kept, or null for the usual place.</param>
    public MainWindow(IReadOnlyList<string>? startupFiles, string? settingsFile = null)
    {
        _settingsFile = settingsFile;
        _settings = AppSettings.Load(settingsFile);

        // The window's bindings read these as soon as it is built.
        CaptionStyleTiles = CaptionStyle.Presets.Select(style => new CaptionStyleTile(style)).ToList();
        TextStyleTiles = TextOverlayStyle.Presets.Select(style => new TextStyleTile(style)).ToList();

        InitializeComponent();
        DataContext = this;
        ApplySettings();

        Title = $"ClipSplitter {AppInfo.Version}";
        TitleText.Text = $"ClipSplitter {AppInfo.Version}  ·  long videos into shorts";

        _player = new ClipPlayer(Log, callback => RequestAnimationFrame(_ => callback()));
        _player.FrameUpdated += () => PreviewSurface.InvalidateVisual();
        _player.StateChanged += UpdateTransport;
        _player.Failed += OnPlayerFailed;

        PreviewSurface.Player = _player;
        PreviewSurface.Composition = _composition;
        PreviewSurface.Mode = _mode;
        PreviewSurface.CompositionEdited += OnCanvasEdited;

        PositionBar.Seeking += fraction => _player.Seek(_player.Duration * fraction);
        VolumeBar.Seeking += volume =>
        {
            _player.Volume = volume;
            UpdateVolumeIcon();
        };

        _segmentTimer.Tick += (_, _) =>
        {
            _segmentTimer.Stop();
            if (_replanSource is { } source)
            {
                _replanSource = null;
                PlanClips(source);
            }
        };

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DragLeaveEvent, Window_DragLeave);
        AddHandler(DragDrop.DropEvent, Window_Drop);
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);

        Closing += (_, _) => SaveSettings();

        Closed += (_, _) =>
        {
            _segmentTimer.Stop();
            _workCts?.Cancel();
            StopWatchingObs();

            foreach (var source in Sources)
                source.Work?.Cancel();

            _player.Dispose();
            _speech.Dispose();
        };

        InitializeStudioControls();
        InitializeCaptionControls();
        InitializeTextControls();
        InitializeEffectControls();
        _ready = true;

        Log("Ready. Add videos, or drop them onto the window.");
        ApplyPanelLayout();
        UpdateSourcesUi();
        UpdateStudioPanel();
        UpdateClipPanel();
        UpdateTransport();
        RefreshCaptions();

        Opened += async (_, _) =>
        {
            RemoveLegacyPreviewCache();

            var ffmpegFound = await FfmpegTools.IsAvailableAsync();
            if (!ffmpegFound)
                Log("FFmpeg was not found. The arrow button in the title bar shows how to install it.");

            // Someone who had ClipSplitter picking up their OBS replays wants that again next time.
            if (_settings.WatchObsReplays && _obsWatcher == null && ObsStudio.FindSetup() is { } obs)
                ToggleObsWatch(obs);

            if (!ffmpegFound)
            {
                // No video can be read without FFmpeg, so the install steps come up instead.
                await ShowFfmpegGuideAsync();
                return;
            }

            var files = startupFiles?.Where(File.Exists).ToList();
            if (files is { Count: > 0 })
                await AddSourcesAsync(files);
        };
    }

    /// <summary>Puts the remembered choices in place before the controls are set up from them.</summary>
    private void ApplySettings()
    {
        _composition.Format = OutputFormat.Custom(_settings.FormatWidth, _settings.FormatHeight);

        var captions = _composition.Captions;
        captions.Enabled = _settings.CaptionsEnabled;
        captions.StyleId = CaptionStyle.Find(_settings.CaptionStyle).Id;
        captions.Position = Enum.TryParse<CaptionPosition>(_settings.CaptionPosition, ignoreCase: true, out var position) && Enum.IsDefined(position)
            ? position
            : CaptionPosition.Bottom;
        captions.Scale = double.IsFinite(_settings.CaptionScale)
            ? Math.Clamp(_settings.CaptionScale, CaptionSettings.MinScale, CaptionSettings.MaxScale)
            : 1;
        captions.Accent = SkiaSharp.SKColor.TryParse(_settings.CaptionAccent ?? string.Empty, out var accent) ? accent : null;
        captions.Uppercase = _settings.CaptionUppercase;

        // How a text looks and where it sits is remembered; what it says belongs to one short.
        var text = _composition.Text;
        text.StyleId = TextOverlayStyle.Find(_settings.TextStyle).Id;
        text.Anchor = Enum.TryParse<TextAnchor>(_settings.TextPosition, ignoreCase: true, out var anchor) && Enum.IsDefined(anchor)
            ? anchor
            : TextAnchor.Top;
        text.CentreX = double.IsFinite(_settings.TextX) ? Math.Clamp(_settings.TextX, 0, 1) : 0.5;
        text.CentreY = double.IsFinite(_settings.TextY) ? Math.Clamp(_settings.TextY, 0, 1) : 0.5;
        text.Scale = double.IsFinite(_settings.TextScale)
            ? Math.Clamp(_settings.TextScale, TextOverlaySettings.MinScale, TextOverlaySettings.MaxScale)
            : 1;
        text.Accent = SkiaSharp.SKColor.TryParse(_settings.TextAccent ?? string.Empty, out var textAccent) ? textAccent : null;
        text.ShowSeconds = NearestShowSeconds(_settings.TextShowSeconds);

        var effects = _composition.Effects;
        effects.LookId = VideoLook.Find(_settings.Look).Id;
        effects.EffectId = VideoEffect.Find(_settings.Effect).Id;
        effects.FadeIn = _settings.FadeIn;
        effects.FadeOut = _settings.FadeOut;
        effects.FadeSeconds = double.IsFinite(_settings.FadeSeconds)
            ? Math.Clamp(_settings.FadeSeconds, ShortEffects.MinFadeSeconds, ShortEffects.MaxFadeSeconds)
            : MediumFadeSeconds;

        SetPanelWidths(_settings.ClipListShare, _settings.SettingsPanelWidth);

        // The window is not ready yet, so this sets the starting clip length without replanning anything.
        SegmentSecondsBox.Value = Math.Clamp(_settings.SegmentSeconds, 1, 86400);
    }

    private void SaveSettings()
    {
        var captions = _composition.Captions;

        _settings.SegmentSeconds = CurrentSegmentSeconds();
        _settings.FormatWidth = _composition.CanvasWidth;
        _settings.FormatHeight = _composition.CanvasHeight;
        _settings.CaptionsEnabled = captions.Enabled;
        _settings.CaptionStyle = captions.StyleId;
        _settings.CaptionPosition = captions.Position.ToString();
        _settings.CaptionScale = captions.Scale;
        _settings.CaptionAccent = ColourText(captions.Accent);
        _settings.CaptionUppercase = captions.Uppercase;

        var text = _composition.Text;
        _settings.TextStyle = text.StyleId;
        _settings.TextPosition = text.Anchor.ToString();
        _settings.TextX = text.CentreX;
        _settings.TextY = text.CentreY;
        _settings.TextScale = text.Scale;
        _settings.TextAccent = ColourText(text.Accent);
        _settings.TextShowSeconds = text.ShowSeconds;
        _settings.SpeechModel = SelectedSpeechModel.Id;
        _settings.SpeechLanguage = SelectedSpeechLanguage;
        _settings.Look = _composition.Effects.LookId;
        _settings.Effect = _composition.Effects.EffectId;
        _settings.FadeIn = _composition.Effects.FadeIn;
        _settings.FadeOut = _composition.Effects.FadeOut;
        _settings.FadeSeconds = _composition.Effects.FadeSeconds;
        _settings.WatchObsReplays = _obsWatcher != null;

        // The two resizable panels left of the settings share what is left of the width.
        var columns = PanelsGrid.ColumnDefinitions;
        var shared = columns[0].Width.Value + columns[2].Width.Value;
        if (columns[0].Width.IsStar && columns[2].Width.IsStar && shared > 0)
            _settings.ClipListShare = columns[0].Width.Value / shared;

        _settings.SettingsPanelWidth = columns[4].Width.IsAbsolute ? columns[4].Width.Value : DefaultSettingsPanelWidth;
        _settings.Save(_settingsFile);
    }

    private const double DefaultClipListShare = 5d / 9d;
    private const double DefaultSettingsPanelWidth = 320;

    /// <summary>Sets the widths of the three panels: how the clip list and the player share their room, and how wide the settings are.</summary>
    private void SetPanelWidths(double clipListShare, double settingsWidth)
    {
        var columns = PanelsGrid.ColumnDefinitions;
        var share = double.IsFinite(clipListShare) ? Math.Clamp(clipListShare, 0.2, 0.8) : DefaultClipListShare;
        var width = double.IsFinite(settingsWidth) ? Math.Clamp(settingsWidth, columns[4].MinWidth, columns[4].MaxWidth) : DefaultSettingsPanelWidth;

        columns[0].Width = new GridLength(share, GridUnitType.Star);
        columns[2].Width = new GridLength(1 - share, GridUnitType.Star);
        columns[4].Width = new GridLength(width);
    }

    private void PanelGrip_DoubleTapped(object? sender, TappedEventArgs e)
    {
        SetPanelWidths(DefaultClipListShare, DefaultSettingsPanelWidth);
        e.Handled = true;
    }

    /// <summary>A colour as "#RRGGBB" for the settings file, or null for none.</summary>
    private static string? ColourText(SkiaSharp.SKColor? colour)
        => colour is { } value ? $"#{value.Red:X2}{value.Green:X2}{value.Blue:X2}" : null;

    private enum StudioTab
    {
        Layout,
        Captions,
        Text,
        Effects
    }

    /// <summary>True while an export or caption job is running. Its button acts as Cancel meanwhile.</summary>
    private bool IsBusy => _workCts != null;

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
            AssignClip(slot, item);
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
        if (!e.Data.Contains(DataFormats.Files) || IsBusy)
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

        if (IsBusy)
            return;

        var paths = e.Data.GetFiles()?
            .Select(file => file.TryGetLocalPath())
            .Where(candidate => candidate != null && File.Exists(candidate))
            .Select(candidate => candidate!)
            .ToList();

        if (paths == null || paths.Count == 0)
            return;

        // A file dropped on the vertical canvas goes straight into that slot; anywhere else the files join the source videos.
        if (slot >= 0)
            await AssignFileAsync(slot, paths[0]);
        else
            await AddSourcesAsync(paths);
    }

    private async Task AssignFileAsync(int slot, string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            var info = await MediaProbe.ProbeAsync(path, CancellationToken.None);
            EnsureOutputFolder(path);
            AssignSlot(slot, new ClipSource(path, TimeSpan.Zero, info.Duration, info, "Whole video"));
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
        ApplyPanelLayout();

        // Captions and text belong to the vertical canvas only.
        RefreshCaptionScene();
        UpdateTextMoment();

        if (!load)
            return;

        if (mode == PreviewMode.Vertical)
        {
            LoadComposition(keepPosition: false);
        }
        else
        {
            // The clip preview opens on the clip that was picked last.
            LoadClip(autoPlay: false);
            UpdateClipPanel();
        }
    }

    /// <summary>
    /// The vertical studio has many settings and gets most of the right-hand column; the clip
    /// preview has few, so the activity log takes the room instead.
    /// </summary>
    private void ApplyPanelLayout()
    {
        var studio = _mode == PreviewMode.Vertical;
        var fill = new GridLength(1, GridUnitType.Star);

        // The two rows trade places as the one that takes whatever height is left.
        RightColumn.RowDefinitions[0].Height = studio ? fill : GridLength.Auto;
        RightColumn.RowDefinitions[1].Height = studio ? GridLength.Auto : fill;
        LogBox.Height = studio ? CompactLogHeight : double.NaN;
    }

    private void ShowClip(ClipPreviewItem item, bool autoPlay)
    {
        SetPreviewClip(item);
        SetMode(PreviewMode.Clip, load: false);
        LoadClip(autoPlay);
        UpdateClipPanel();
    }

    /// <summary>
    /// Makes a clip the one the clip preview shows, and marks its row in the list. The preview
    /// follows whichever clip was picked last, whether it was selected, played or put on the
    /// canvas, so it is never left on the clip of an earlier short.
    /// </summary>
    private void SetPreviewClip(ClipPreviewItem item)
    {
        _clipItem = item;

        // Only the list that is showing can mark the row.
        if (_activeSource?.Clips.Contains(item) == true && !ReferenceEquals(PreviewList.SelectedItem, item))
            PreviewList.SelectedItem = item;
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
        var speed = _composition.Effects.Speed;
        var any = false;
        for (var slot = 0; slot < layers.Length; slot++)
        {
            if (_composition.GetSlot(slot) is not { } clip)
                continue;

            layers[slot] = new PlaybackLayer(clip.File, clip.Start, clip.Length / speed, clip.Info, _composition.Effects.PreviewFilter, speed);
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

        // Starting, stopping and seeking all change which caption the canvas should show, and
        // whether a text that is being edited stays in view.
        UpdateCaptionMoment();
        UpdateTextMoment();
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !_player.HasMedia)
            return;

        // Space still types into text boxes, presses a focused button and opens a focused list.
        if (FocusManager?.GetFocusedElement() is TextBox or Button or ComboBox or ComboBoxItem or Slider)
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

    // ---------------------------------------------------------------- Vertical studio: layout tab

    private void InitializeStudioControls()
    {
        _syncingUi = true;

        var formats = OutputFormat.Presets
            .Select(format => new FormatChoice(format, format.Title, format.Platforms))
            .ToList();
        formats.Add(new FormatChoice(null, "Custom size", "any width and height"));

        FormatBox.ItemsSource = formats;

        // The format may have been remembered from last time; a size that is no preset is the custom one.
        var current = formats.FirstOrDefault(choice => choice.Format == _composition.Format) ?? formats[^1];
        FormatBox.SelectedItem = current;
        CustomSizeRow.IsVisible = current.IsCustom;
        CustomWidthBox.Value = _composition.CanvasWidth;
        CustomHeightBox.Value = _composition.CanvasHeight;

        _syncingUi = false;
    }

    private void StudioTab_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingUi || sender is not RadioButton { IsChecked: true } radio)
            return;

        _studioTab = ReferenceEquals(radio, CaptionsTabRadio) ? StudioTab.Captions
            : ReferenceEquals(radio, TextTabRadio) ? StudioTab.Text
            : ReferenceEquals(radio, EffectsTabRadio) ? StudioTab.Effects
            : StudioTab.Layout;

        LayoutTab.IsVisible = _studioTab == StudioTab.Layout;
        CaptionsTab.IsVisible = _studioTab == StudioTab.Captions;
        TextTab.IsVisible = _studioTab == StudioTab.Text;
        EffectsTab.IsVisible = _studioTab == StudioTab.Effects;

        // The captions tab shows sample captions even before a clip has any of its own.
        RefreshCaptions();

        // The text can be dragged on the canvas only while its tab is open.
        UpdateTextMoment();
    }

    private void Format_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncingUi || FormatBox.SelectedItem is not FormatChoice choice)
            return;

        CustomSizeRow.IsVisible = choice.IsCustom;

        if (choice.Format is { } format)
        {
            ApplyFormat(format);
            return;
        }

        // The custom size starts from whatever was selected, so only what differs needs typing.
        _syncingUi = true;
        CustomWidthBox.Value = _composition.CanvasWidth;
        CustomHeightBox.Value = _composition.CanvasHeight;
        _syncingUi = false;
    }

    private void CustomSize_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!_ready || _syncingUi || FormatBox.SelectedItem is not FormatChoice { IsCustom: true })
            return;

        var width = (int)(CustomWidthBox.Value ?? _composition.CanvasWidth);
        var height = (int)(CustomHeightBox.Value ?? _composition.CanvasHeight);
        ApplyFormat(OutputFormat.Custom(width, height));
    }

    private void ApplyFormat(OutputFormat format)
    {
        if (_composition.Format == format)
            return;

        _composition.Format = format;

        // How far the blur layout may zoom depends on the shape of the canvas.
        for (var slot = 0; slot < 2; slot++)
        {
            var framing = _composition.GetFraming(slot);
            framing.Zoom = Math.Min(framing.Zoom, _composition.GetMaxZoom(slot));
        }

        UpdateStudioPanel();
        RefreshCaptions();
        RefreshText();
    }

    /// <summary>Puts a clip of the list on the canvas. It is also the clip the clip preview opens on next.</summary>
    private void AssignClip(int slot, ClipPreviewItem item)
    {
        SetPreviewClip(item);
        UpdateClipPanel();
        AssignSlot(slot, item.ToClipSource());
    }

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
        RefreshCaptions();

        // A text in the middle moves to the divider when the layout became the stacked one.
        RefreshText();
    }

    private void ClearTop_Click(object? sender, RoutedEventArgs e) => ClearSlot(0);

    private void ClearBottom_Click(object? sender, RoutedEventArgs e) => ClearSlot(1);

    private void ClearSlot(int slot)
    {
        _composition.SetSlot(slot, null);
        _composition.GetFraming(slot).Reset();
        UpdateStudioPanel();
        LoadComposition(keepPosition: true);
        RefreshCaptions();
    }

    private void SwapSlots_Click(object? sender, RoutedEventArgs e)
    {
        _composition.SwapSlots();

        // A caption source the user picked by hand follows its clip to the other half.
        if (_composition.Captions.Slot is { } captioned)
            _composition.Captions.Slot = 1 - captioned;

        UpdateStudioPanel();
        LoadComposition(keepPosition: true);
        RefreshCaptions();
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
        RefreshCaptions();
        RefreshText();
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

        // Unless the user chose otherwise, captions follow the clip that is heard.
        RefreshCaptions();
    }

    /// <summary>The user moved the divider or reframed a clip on the canvas.</summary>
    private void OnCanvasEdited()
    {
        if (_composition.Layout != VerticalLayoutKind.Stacked)
            return;

        // Captions and text in the middle sit on the divider, so they move with it.
        if (_composition.Captions.Position == CaptionPosition.Middle)
            RefreshCaptionScene();

        if (_composition.Text.Anchor == TextAnchor.Middle)
            RefreshText();
    }

    private void UpdateStudioPanel()
    {
        var stacked = _composition.Layout == VerticalLayoutKind.Stacked;
        var format = _composition.Format;

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

        StudioTitleText.Text = $"SHORT  ·  {format.Ratio}  ·  {format.SizeText}";
        FormatVerticalRadio.Content = $"Shorts ({format.Ratio})";

        TopSlotTitle.Text = SlotTitle(stacked ? "Top clip" : "Clip", _composition.Top);
        BottomSlotTitle.Text = SlotTitle("Bottom clip", _composition.Bottom);
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

    /// <summary>The heading of a slot row, which names the video its clip was cut from.</summary>
    private static string SlotTitle(string name, ClipSource? clip)
        => clip == null ? name : $"{name}  ·  {Path.GetFileName(clip.File)}";

    private static string DescribeSlot(ClipSource? clip)
        => clip == null
            ? "Drag a clip here"
            : $"{clip.Label}  ·  {ClipFormat.Time(clip.Start)} - {ClipFormat.Time(clip.End)}";

    // ---------------------------------------------------------------- Export

    private void ExportFormat_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;

        // Shorts are always re-encoded, so the option only applies to original clips.
        ReencodeCheckBox.IsEnabled = FormatOriginalRadio.IsChecked == true && !IsBusy;
    }

    private async void ExportSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (CancelRunningWork())
            return;

        var output = OutputPathBox.Text?.Trim() ?? string.Empty;

        if (_activeSource is not { } source)
        {
            Log("ERROR: Add a source video first.");
            return;
        }

        if (!File.Exists(source.File))
        {
            Log($"ERROR: {source.Name} is no longer where it was. Remove it from the list and add it again.");
            return;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            Log("ERROR: Select an output folder.");
            return;
        }

        var items = source.Clips.Where(item => item.IsSelected).ToList();
        if (items.Count == 0)
        {
            Log("ERROR: Select at least one clip to export.");
            return;
        }

        if (FormatVerticalRadio.IsChecked != true)
        {
            var options = new SplitOptions(source.File, output, ReencodeCheckBox.IsChecked == true, items.Select(item => item.ToSegment()).ToList());
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

        var clips = items.Select(item => item.ToClipSource()).ToList();
        await RunExportAsync(SplitButton, ExportSelectedText, output, async (progress, token) =>
        {
            for (var index = 0; index < clips.Count; index++)
            {
                token.ThrowIfCancellationRequested();

                // Each selected clip takes the top slot of the studio layout in turn.
                var composition = template.Clone();
                composition.Top = clips[index];

                // Numbered rather than overwritten, so a short that was framed by hand is never lost.
                var clip = clips[index];
                var file = ClipFormat.UniquePath(Path.Combine(
                    output,
                    ClipFormat.OutputFileName(clip.File, clip.Start, clip.Start + composition.SourceDuration, VerticalExporter.OutputExtension, composition.Format.FileTag)));

                Log($"Creating {Path.GetFileName(file)}");
                var done = index;
                await ExportShortAsync(composition, file, fraction => progress((done + fraction) / clips.Count * 100d), token);
            }

            return clips.Count;
        });
    }

    private async void ExportShort_Click(object? sender, RoutedEventArgs e)
    {
        if (CancelRunningWork())
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

        var name = ClipFormat.OutputFileName(first.File, first.Start, first.Start + composition.SourceDuration, VerticalExporter.OutputExtension, composition.Format.FileTag);

        await RunExportAsync(ExportShortButton, ExportShortText, output, async (progress, token) =>
        {
            // Numbered rather than overwritten, so trying another layout keeps the earlier short.
            var file = ClipFormat.UniquePath(Path.Combine(output, name));
            Log($"Creating {Path.GetFileName(file)}");
            await ExportShortAsync(composition, file, fraction => progress(fraction * 100d), token);
            return 1;
        });

        // An export can generate captions the panel does not know about yet.
        RefreshCaptions();
    }

    /// <summary>Renders one short, generating its captions first when they are wanted and missing.</summary>
    /// <param name="progress">Receives progress from 0 to 1.</param>
    private async Task ExportShortAsync(VerticalComposition composition, string file, Action<double> progress, CancellationToken token)
    {
        // Listening to a clip takes a noticeable part of the time when it has to be done first.
        const double listenShare = 0.35;

        var needsListening = NeedsTranscript(composition);
        var encodeFrom = needsListening ? listenShare : 0;

        var captions = await BuildCaptionSceneAsync(composition, fraction => progress(fraction * listenShare), token);
        await VerticalExporter.ExportAsync(composition, file, captions, fraction => progress(encodeFrom + fraction * (1 - encodeFrom)), token);
    }

    /// <summary>Stops the job in progress, if there is one. The button that started it acts as Cancel while it runs.</summary>
    private bool CancelRunningWork()
    {
        if (_workCts == null)
            return false;

        _workCts.Cancel();
        return true;
    }

    private async Task RunExportAsync(Button button, string idleText, string output, Func<Action<double>, CancellationToken, Task<int>> work)
    {
        var completed = await RunWorkAsync(button, idleText, CancelExportText, async (progress, token) =>
        {
            Directory.CreateDirectory(output);
            var count = await work(progress, token);
            Log($"Done. Exported {count} file(s) to {output}");
        });

        if (completed)
            await ShowDonationDialogAsync(output);
    }

    /// <summary>
    /// Runs a long job with the progress bar, turning the button that started it into Cancel.
    /// Returns true when the job finished, false when it failed or was cancelled.
    /// </summary>
    private async Task<bool> RunWorkAsync(Button button, string idleText, string cancelText, Func<Action<double>, CancellationToken, Task> work)
    {
        var cts = new CancellationTokenSource();
        _workCts = cts;
        var completed = false;

        // ffmpeg and the speech recognizer report progress from background threads.
        void ReportProgress(double percent) => Dispatcher.UIThread.Post(() =>
        {
            if (_workCts == cts && percent > ExportProgress.Value)
                ExportProgress.Value = percent;
        });

        _player.Pause();
        ExportProgress.Value = 0;
        SetBusyState(button, idleText, cancelText, true);

        try
        {
            await work(ReportProgress, cts.Token);
            ExportProgress.Value = 100;
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
            _workCts = null;
            cts.Dispose();
            SetBusyState(button, idleText, cancelText, false);
        }

        return completed;
    }

    private void SetBusyState(Button activeButton, string idleText, string cancelText, bool busy)
    {
        activeButton.Content = busy ? cancelText : idleText;

        foreach (var button in new[] { SplitButton, ExportShortButton, GenerateCaptionsButton, ImportCaptionsButton })
        {
            if (!ReferenceEquals(button, activeButton))
                button.IsEnabled = !busy;
        }

        AddVideosButton.IsEnabled = !busy;
        ObsButton.IsEnabled = !busy;
        BrowseOutputButton.IsEnabled = !busy;
        SegmentSecondsBox.IsEnabled = !busy;
        FormatOriginalRadio.IsEnabled = !busy;
        FormatVerticalRadio.IsEnabled = !busy;
        ReencodeCheckBox.IsEnabled = !busy && FormatOriginalRadio.IsChecked == true;
        CaptionLanguageBox.IsEnabled = !busy;
        CaptionModelBox.IsEnabled = !busy;

        // The caption buttons depend on more than whether a job is running.
        if (!busy)
            UpdateCaptionPanel();
    }

    // ---------------------------------------------------------------- Dialogs

    private async Task ReportErrorAsync(Exception ex)
    {
        Log("ERROR: " + ex.Message);

        if (ex is FfmpegNotFoundException)
            await ShowFfmpegGuideAsync();
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

    /// <summary>Shows the step-by-step FFmpeg install tutorial. Only one copy is ever open.</summary>
    private async Task ShowFfmpegGuideAsync()
    {
        if (_ffmpegDialogOpen)
            return;

        _ffmpegDialogOpen = true;
        try
        {
            var dialog = CreateDialogWindow(FfmpegInstallGuide.Title);
            dialog.Width = 480;
            dialog.MinWidth = 480;

            var downloadButton = CreateDialogButton("Open the FFmpeg download page");
            downloadButton.Click += (_, _) => OpenWithShell(FfmpegInstallGuide.DownloadUrl);

            var body = new StackPanel { Spacing = 16 };
            body.Children.Add(CreateFfmpegSteps());
            body.Children.Add(new TextBlock
            {
                Text = FfmpegInstallGuide.Fallback,
                FontSize = 12.5,
                Foreground = SolidColorBrush.Parse("#8a96a8"),
                TextWrapping = TextWrapping.Wrap
            });

            dialog.Content = CreateDialogContent(dialog, FfmpegInstallGuide.Title, FfmpegInstallGuide.Intro, body, downloadButton);

            await dialog.ShowDialog(this);
        }
        finally
        {
            _ffmpegDialogOpen = false;
        }
    }

    private static Control CreateFfmpegSteps()
    {
        var panel = new StackPanel { Spacing = 12 };
        var steps = FfmpegInstallGuide.Steps;

        for (var index = 0; index < steps.Count; index++)
        {
            var number = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = SolidColorBrush.Parse("#1b2b4d"),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = (index + 1).ToString(),
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = SolidColorBrush.Parse("#9cb7ff"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            var content = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new TextBlock
            {
                Text = steps[index],
                Foreground = SolidColorBrush.Parse("#e5ecf7"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });

            if (index == FfmpegInstallGuide.CommandStep)
                content.Children.Add(CreateCommandRow(FfmpegInstallGuide.Command));

            Grid.SetColumn(content, 1);
            panel.Children.Add(new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 12,
                Children = { number, content }
            });
        }

        return panel;
    }

    /// <summary>A command in a box, with a button that puts it on the clipboard.</summary>
    private static Control CreateCommandRow(string command)
    {
        var copyButton = CreateDialogButton("Copy", true);
        copyButton.MinWidth = 84;
        copyButton.Click += async (_, _) =>
        {
            try
            {
                if (GetTopLevel(copyButton)?.Clipboard is not { } clipboard)
                    return;

                await clipboard.SetTextAsync(command);
                copyButton.Content = "Copied";
                await Task.Delay(1500);
                copyButton.Content = "Copy";
            }
            catch
            {
                // The clipboard is busy; the command can still be typed by hand.
            }
        };
        Grid.SetColumn(copyButton, 1);

        var box = new Border
        {
            Height = 36,
            Padding = new Thickness(12, 0),
            Background = SolidColorBrush.Parse("#10141c"),
            BorderBrush = SolidColorBrush.Parse("#2f394a"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new SelectableTextBlock
            {
                Text = command,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            Children = { box, copyButton }
        };
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
        => CreateDialogContent(dialog, title, message, null, buttons);

    /// <param name="body">Extra content shown between the message and the buttons, if any.</param>
    private static Control CreateDialogContent(Window dialog, string title, string message, Control? body, params Button[] buttons)
    {
        var buttonPanel = new StackPanel
        {
            Spacing = 10
        };

        foreach (var button in buttons)
            buttonPanel.Children.Add(button);

        var content = new StackPanel
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
                }
            }
        };

        if (body != null)
            content.Children.Add(body);

        content.Children.Add(buttonPanel);

        return new Border
        {
            Padding = new Thickness(22),
            Background = SolidColorBrush.Parse("#151922"),
            BorderBrush = SolidColorBrush.Parse("#334155"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = content
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

    private async void FfmpegGuide_Click(object? sender, RoutedEventArgs e) => await ShowFfmpegGuideAsync();

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
