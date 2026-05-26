using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClipSplitterApp.Services;

namespace ClipSplitterApp.Views;

public partial class MainWindow : Window
{
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

    private async void BrowseOutput_Click(object? sender, RoutedEventArgs e)
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
        SplitButton.Content = "Cancel";
        _splitCts = new CancellationTokenSource();

        try
        {
            var splitter = new FfmpegClipSplitter(Log);
            var options = new SplitOptions(input, output, seconds, ReencodeCheckBox.IsChecked == true, selectedSegments);
            await splitter.SplitAsync(options, progress => ProgressBar.Value = progress, _splitCts.Token);
            ProgressBar.Value = 100;
            Log($"Done. Exported {selectedSegments.Count} clip(s).");
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled by user.");
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
        }
        finally
        {
            _splitCts.Dispose();
            _splitCts = null;
            SplitButton.Content = "Export selected";
        }
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
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PreviewItems.Clear();
            UpdatePreviewSummary();
            Log("ERROR: " + ex.Message);
        }
        finally
        {
            if (_previewCts == cts)
                _previewCts = null;

            cts.Dispose();
        }
    }

    private ClipPreviewItem CreatePreviewItem(string input, ClipSegment segment)
    {
        var label = $"Clip {segment.Index + 1}";
        var timeRange = $"{FfmpegClipSplitter.FormatTime(segment.Start)} - {FfmpegClipSplitter.FormatTime(segment.End)}";
        var duration = FfmpegClipSplitter.FormatTime(segment.Length);
        var fileName = BuildOutputFileName(input, segment);

        return new ClipPreviewItem(segment, label, timeRange, duration, fileName);
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
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://paypal.me/mmltools",
            UseShellExecute = true
        });
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

    public ClipPreviewItem(ClipSegment segment, string label, string timeRange, string duration, string fileName)
    {
        Segment = segment;
        Label = label;
        TimeRange = timeRange;
        Duration = duration;
        _fileName = fileName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ClipSegment Segment { get; }
    public string Label { get; }
    public string TimeRange { get; }
    public string Duration { get; }

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
}
