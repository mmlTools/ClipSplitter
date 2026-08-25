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
using ClipSplitterApp.Services;

namespace ClipSplitterApp.Views;

public partial class MainWindow : Window
{
    private const string DonationUrl = "https://www.paypal.com/donate/?hosted_button_id=ZKTLLYY9ADWYQ";
    private const string FfmpegDownloadUrl = "https://ffmpeg.org/download.html";

    private CancellationTokenSource? _splitCts;
    private CancellationTokenSource? _previewCts;

    public ObservableCollection<ClipPreviewItem> PreviewItems { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Log("Ready. Select a clip and output folder.");
        UpdatePreviewSummary();
    }

    private async void BrowseInput_Click(object? sender, RoutedEventArgs e)
    {
        BrowseInputButton.Content = "Opening...";
        BrowseInputButton.IsEnabled = false;

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select video clip",
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("Video files") { Patterns = new[] { "*.mp4", "*.mov", "*.mkv", "*.avi", "*.webm", "*.m4v" } },
                    FilePickerFileTypes.All
                }
            });

            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            {
                InputPathBox.Text = path;
                if (string.IsNullOrWhiteSpace(OutputPathBox.Text))
                    OutputPathBox.Text = Path.Combine(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory, "split-output");

                await RefreshPreviewAsync();
            }
        }
        finally
        {
            BrowseInputButton.Content = "Browse clip";
            BrowseInputButton.IsEnabled = true;
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
            {
                OutputPathBox.Text = path;
                RefreshPreviewFileNames();
            }
        }
        finally
        {
            BrowseOutputButton.Content = "Browse folder";
            BrowseOutputButton.IsEnabled = true;
        }
    }

    private async void SegmentSecondsBox_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        await RefreshPreviewAsync();
    }

    private async void Split_Click(object? sender, RoutedEventArgs e)
    {
        if (_splitCts != null)
        {
            _splitCts.Cancel();
            return;
        }

        var input = InputPathBox.Text?.Trim() ?? string.Empty;
        var output = OutputPathBox.Text?.Trim() ?? string.Empty;
        var seconds = (int)(SegmentSecondsBox.Value ?? 30);

        if (!File.Exists(input))
        {
            Log("ERROR: Input file does not exist.");
            return;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            Log("ERROR: Select an output folder.");
            return;
        }

        if (seconds < 1)
        {
            Log("ERROR: Segment length must be at least 1 second.");
            return;
        }

        if (PreviewItems.Count == 0)
            await RefreshPreviewAsync();

        var selectedSegments = PreviewItems
            .Where(item => item.IsSelected)
            .Select(item => item.Segment)
            .ToList();

        if (selectedSegments.Count == 0)
        {
            Log("ERROR: Select at least one preview clip to export.");
            return;
        }

        Directory.CreateDirectory(output);
        LogBox.Text = string.Empty;
        ProgressBar.Value = 0;
        SetExportingState(true);
        _splitCts = new CancellationTokenSource();
        var exportCompleted = false;

        try
        {
            var splitter = new FfmpegClipSplitter(Log);
            var options = new SplitOptions(input, output, seconds, ReencodeCheckBox.IsChecked == true, selectedSegments);
            await splitter.SplitAsync(options, progress => ProgressBar.Value = progress, _splitCts.Token);
            ProgressBar.Value = 100;
            Log($"Done. Exported {selectedSegments.Count} clip(s).");
            exportCompleted = true;
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled by user.");
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            if (IsMissingFfmpegException(ex))
                await ShowFfmpegDownloadDialogAsync();
        }
        finally
        {
            _splitCts.Dispose();
            _splitCts = null;
            SetExportingState(false);
        }

        if (exportCompleted)
            await ShowDonationDialogAsync(output);
    }

    private async System.Threading.Tasks.Task RefreshPreviewAsync()
    {
        var input = InputPathBox.Text?.Trim() ?? string.Empty;
        var seconds = (int)(SegmentSecondsBox.Value ?? 30);

        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;

        if (!File.Exists(input) || seconds < 1)
        {
            PreviewItems.Clear();
            UpdatePreviewSummary();
            return;
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;

        try
        {
            SetPreviewLoadingState(true);
            PreviewSummaryText.Text = "Building preview...";
            var splitter = new FfmpegClipSplitter(Log);
            var segments = await splitter.CreatePlanAsync(input, seconds, cts.Token);

            if (cts.IsCancellationRequested)
                return;

            PreviewItems.Clear();
            foreach (var segment in segments)
            {
                var item = CreatePreviewItem(input, segment);
                item.PropertyChanged += PreviewItem_PropertyChanged;
                PreviewItems.Add(item);
            }

            Log($"Preview ready: {PreviewItems.Count} clip(s).");
            UpdatePreviewSummary();
            await LoadThumbnailsAsync(input, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PreviewItems.Clear();
            UpdatePreviewSummary();
            Log("ERROR: " + ex.Message);
            if (IsMissingFfmpegException(ex))
                await ShowFfmpegDownloadDialogAsync();
        }
        finally
        {
            if (_previewCts == cts)
            {
                _previewCts = null;
                SetPreviewLoadingState(false);
            }

            cts.Dispose();
        }
    }

    private async Task LoadThumbnailsAsync(string input, CancellationToken token)
    {
        if (PreviewItems.Count == 0)
            return;

        PreviewSummaryText.Text = "Rendering thumbnails...";
        var splitter = new FfmpegClipSplitter(Log);
        var thumbnailFolder = GetPreviewCacheFolder(input, "thumbs");

        foreach (var item in PreviewItems)
        {
            token.ThrowIfCancellationRequested();

            var thumbnailPath = Path.Combine(thumbnailFolder, BuildPreviewCacheName(item.Segment, ".jpg"));
            try
            {
                if (!File.Exists(thumbnailPath))
                    await splitter.CreateThumbnailAsync(input, item.Segment, thumbnailPath, token);

                await using var stream = File.OpenRead(thumbnailPath);
                item.Thumbnail = new Bitmap(stream);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
        }

        UpdatePreviewSummary();
    }

    private ClipPreviewItem CreatePreviewItem(string input, ClipSegment segment)
    {
        var label = $"Clip {segment.Index + 1}";
        var timeRange = $"{FfmpegClipSplitter.FormatTime(segment.Start)} - {FfmpegClipSplitter.FormatTime(segment.End)}";
        var duration = FfmpegClipSplitter.FormatTime(segment.Length);
        var fileName = BuildOutputFileName(input, segment);

        return new ClipPreviewItem(input, segment, label, timeRange, duration, fileName);
    }

    private void RefreshPreviewFileNames()
    {
        var input = InputPathBox.Text?.Trim() ?? string.Empty;
        if (!File.Exists(input))
            return;

        foreach (var item in PreviewItems)
            item.FileName = BuildOutputFileName(input, item.Segment);
    }

    private static string BuildOutputFileName(string input, ClipSegment segment)
    {
        var inputName = Path.GetFileNameWithoutExtension(input);
        var extension = Path.GetExtension(input);
        return $"{SanitizeFileName(inputName)}-{FfmpegClipSplitter.FormatFileStamp(segment.Start)}-{FfmpegClipSplitter.FormatFileStamp(segment.End)}{extension}";
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        return name.Trim();
    }

    private void SelectAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in PreviewItems)
            item.IsSelected = true;

        UpdatePreviewSummary();
    }

    private void ClearSelection_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in PreviewItems)
            item.IsSelected = false;

        UpdatePreviewSummary();
    }

    private async void PreviewClip_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ClipPreviewItem item } || item.IsPreviewing)
            return;

        item.IsPreviewing = true;

        try
        {
            var previewFolder = GetPreviewCacheFolder(item.InputFile, "clips");
            var extension = Path.GetExtension(item.InputFile);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".mp4";

            var previewPath = Path.Combine(previewFolder, BuildPreviewCacheName(item.Segment, extension));
            if (!File.Exists(previewPath))
            {
                var splitter = new FfmpegClipSplitter(Log);
                await splitter.CreatePreviewClipAsync(item.InputFile, item.Segment, previewPath, CancellationToken.None);
            }

            OpenFile(previewPath);
        }
        catch (Exception ex)
        {
            Log("ERROR: Could not preview clip. " + ex.Message);
            if (IsMissingFfmpegException(ex))
                await ShowFfmpegDownloadDialogAsync();
        }
        finally
        {
            item.IsPreviewing = false;
        }
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
            ? "No preview yet"
            : $"{selected} of {PreviewItems.Count} selected";
    }

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

    private void SupportProject_Click(object? sender, RoutedEventArgs e)
    {
        OpenUrl(DonationUrl);
    }

    private void OpenFfmpegDownload_Click(object? sender, RoutedEventArgs e)
    {
        OpenUrl(FfmpegDownloadUrl);
    }

    private void SetExportingState(bool isExporting)
    {
        SplitButton.Content = isExporting ? "Cancel export" : "Export selected";
        BrowseInputButton.IsEnabled = !isExporting;
        BrowseOutputButton.IsEnabled = !isExporting;
        SegmentSecondsBox.IsEnabled = !isExporting;
        ReencodeCheckBox.IsEnabled = !isExporting;
        SelectAllButton.IsEnabled = !isExporting;
        ClearSelectionButton.IsEnabled = !isExporting;
    }

    private void SetPreviewLoadingState(bool isLoading)
    {
        SelectAllButton.IsEnabled = !isLoading;
        ClearSelectionButton.IsEnabled = !isLoading;
    }

    private async Task ShowDonationDialogAsync(string outputFolder)
    {
        var dialog = CreateDialogWindow("Export complete");

        var donateButton = CreateDialogButton("Donate", true);
        donateButton.Click += (_, _) => OpenUrl(DonationUrl);

        var openFolderButton = CreateDialogButton("Open folder");
        openFolderButton.Click += (_, _) => OpenFolder(outputFolder);

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
        downloadButton.Click += (_, _) => OpenUrl(FfmpegDownloadUrl);

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

    private static bool IsMissingFfmpegException(Exception ex)
    {
        return ex.Message.Contains("Could not start ffmpeg", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Could not start ffprobe", StringComparison.OrdinalIgnoreCase);
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private static void OpenFolder(string folder)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true
        });
    }

    private static void OpenFile(string file)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = true
        });
    }

    private static string GetPreviewCacheFolder(string inputFile, string kind)
    {
        var inputStamp = $"{Path.GetFileNameWithoutExtension(inputFile)}-{File.GetLastWriteTimeUtc(inputFile).Ticks}";
        var safeName = SanitizeFileName(inputStamp);
        return Path.Combine(Path.GetTempPath(), "ClipSplitter", safeName, kind);
    }

    private static string BuildPreviewCacheName(ClipSegment segment, string extension)
    {
        return $"{segment.Index:D4}-{FfmpegClipSplitter.FormatFileStamp(segment.Start)}-{FfmpegClipSplitter.FormatFileStamp(segment.End)}{extension}";
    }

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

    private void Log(string message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
            LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        });
    }
}

public sealed class ClipPreviewItem : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private string _fileName;
    private Bitmap? _thumbnail;
    private bool _isPreviewing;

    public ClipPreviewItem(string inputFile, ClipSegment segment, string label, string timeRange, string duration, string fileName)
    {
        InputFile = inputFile;
        Segment = segment;
        Label = label;
        TimeRange = timeRange;
        Duration = duration;
        _fileName = fileName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string InputFile { get; }
    public ClipSegment Segment { get; }
    public string Label { get; }
    public string TimeRange { get; }
    public string Duration { get; }
    public bool CanPreview => !IsPreviewing;
    public string PreviewButtonText => IsPreviewing ? "Loading..." : "Preview";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string FileName
    {
        get => _fileName;
        set
        {
            if (_fileName == value)
                return;

            _fileName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileName)));
        }
    }

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (_thumbnail == value)
                return;

            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public bool IsPreviewing
    {
        get => _isPreviewing;
        set
        {
            if (_isPreviewing == value)
                return;

            _isPreviewing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPreviewing)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanPreview)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewButtonText)));
        }
    }
}
