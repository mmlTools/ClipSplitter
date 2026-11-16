using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClipSplitterApp.Captions;
using ClipSplitterApp.Controls;
using ClipSplitterApp.Services;
using SkiaSharp;

namespace ClipSplitterApp.Views;

/// <summary>Captions: recognising speech, importing subtitles, correcting the text and choosing the look.</summary>
public partial class MainWindow
{
    private const string GenerateCaptionsText = "Generate captions";
    private const string RegenerateCaptionsText = "Generate again";

    /// <summary>Shown in a style's look while the clip on the canvas has no captions of its own.</summary>
    private static readonly string[] SampleWords = { "Your", "captions", "look", "like", "this" };

    private readonly TranscriptStore _transcripts = new();
    private readonly SpeechRecognizer _speech = new();
    private readonly List<RadioButton> _accentSwatches = new();

    /// <summary>The moment the sample words are shown at: well into the second word, with it lit up.</summary>
    private static readonly TimeSpan SampleMoment = TimeSpan.FromSeconds(1.6);

    private Transcript? _captionTranscript;
    private CaptionScene? _captionScene;
    private int _captionFirstWord;
    private bool _captionRefreshQueued;
    private bool _sampleShown;

    /// <summary>The caption text of the short on the canvas, one row per group of words.</summary>
    public ObservableCollection<CaptionLineItem> CaptionLines { get; } = new();

    /// <summary>The styles offered on the style tab, each with a picture of itself.</summary>
    public IReadOnlyList<CaptionStyleTile> CaptionStyleTiles { get; }

    private SpeechModel SelectedSpeechModel => CaptionModelBox.SelectedItem as SpeechModel ?? SpeechModel.Accurate;

    private string SelectedSpeechLanguage => (CaptionLanguageBox.SelectedItem as SpeechLanguage)?.Code ?? SpeechLanguage.AutoCode;

    private void InitializeCaptionControls()
    {
        _syncingUi = true;

        CaptionLanguageBox.ItemsSource = SpeechLanguage.All;
        CaptionLanguageBox.SelectedItem = SpeechLanguage.All.FirstOrDefault(language => language.Code == _settings.SpeechLanguage) ?? SpeechLanguage.All[0];
        CaptionModelBox.ItemsSource = SpeechModel.All;
        CaptionModelBox.SelectedItem = SpeechModel.Find(_settings.SpeechModel);

        // The first swatch stands for the colour the style comes with; the rest replace it.
        AddAccentSwatch(null, "The style's own colour");
        foreach (var color in CaptionStyle.AccentChoices)
            AddAccentSwatch(color, null);

        _syncingUi = false;
    }

    private void AddAccentSwatch(SKColor? color, string? tip)
    {
        var swatch = new RadioButton
        {
            GroupName = "CaptionAccent",
            Tag = color,
            Margin = new Avalonia.Thickness(0, 0, 4, 4)
        };
        swatch.Classes.Add("swatch");

        if (color is { } fixedColor)
        {
            swatch.Background = ToBrush(fixedColor);
        }
        else
        {
            swatch.Content = new TextBlock { Text = "A", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = SolidColorBrush.Parse("#0E1118") };
            ToolTip.SetTip(swatch, tip);
        }

        swatch.IsCheckedChanged += CaptionAccent_Changed;
        _accentSwatches.Add(swatch);
        CaptionAccentPanel.Children.Add(swatch);
    }

    private static IBrush ToBrush(SKColor color) => new SolidColorBrush(Color.FromArgb(255, color.Red, color.Green, color.Blue));

    // ---------------------------------------------------------------- Keeping captions up to date

    /// <summary>
    /// Works out which captions belong to the short on the canvas and shows them everywhere:
    /// on the canvas, in the text list and in the status line.
    /// </summary>
    private void RefreshCaptions()
    {
        var clip = _composition.CaptionClip;
        _captionTranscript = clip == null
            ? null
            : _transcripts.Find(clip.File, clip.Start, clip.Start + _composition.Duration);

        RefreshCaptionScene();
        UpdateCaptionPanel();
    }

    /// <summary>Lays the captions out again after something about their look or the frame changed.</summary>
    private void RefreshCaptionScene()
    {
        _captionScene = null;
        _captionFirstWord = 0;

        if (_captionTranscript is { } transcript && _composition.CaptionClip is { } clip)
        {
            var words = WordsFor(transcript, clip, _composition.Duration, out _captionFirstWord);
            if (words.Count > 0)
                _captionScene = BuildScene(words, _composition);
        }

        RebuildCaptionLines();

        // The style tab shows captions even while they are off or the canvas is empty, so a
        // style can be chosen by eye.
        var settings = _composition.Captions;
        var visible = _mode == PreviewMode.Vertical
                      && (_studioTab == StudioTab.Style || (settings.Enabled && _composition.CaptionClip != null));
        CaptionScene? shown = null;
        _sampleShown = false;

        if (visible)
        {
            shown = _captionScene;

            // Before a clip has captions, sample words stand in for them.
            if (shown == null && _captionTranscript == null)
            {
                shown = BuildSampleScene();
                _sampleShown = true;
            }
        }

        PreviewSurface.Captions = shown;
        UpdateCaptionMoment();
    }

    /// <summary>
    /// Chooses the moment the canvas shows captions at. They normally follow the player; sample
    /// words hold still, and so does the style tab while the player rests between two lines,
    /// because a style cannot be judged with nothing on screen.
    /// </summary>
    private void UpdateCaptionMoment()
    {
        TimeSpan? freeze = null;

        if (PreviewSurface.Captions is { } scene)
        {
            if (_sampleShown)
                freeze = SampleMoment;
            else if (_studioTab == StudioTab.Style && !_player.IsPlaying && scene.FindPage(_player.Position) == null)
                freeze = NearestCaptionMoment(scene, _player.Position);
        }

        if (PreviewSurface.CaptionFreeze != freeze)
            PreviewSurface.CaptionFreeze = freeze;
    }

    /// <summary>A moment inside the next caption page after <paramref name="position"/>, with a word lit up.</summary>
    private static TimeSpan? NearestCaptionMoment(CaptionScene scene, TimeSpan position)
    {
        if (scene.Pages.Count == 0)
            return null;

        var page = scene.Pages.FirstOrDefault(candidate => candidate.End > position) ?? scene.Pages[^1];
        var word = page.Words[Math.Min(1, page.Words.Count - 1)];
        var moment = word.Start + (word.End - word.Start) / 2;

        // Past the point where the page has finished arriving, but still inside it.
        var settled = page.Start + TimeSpan.FromMilliseconds(260);
        if (moment < settled)
            moment = settled;

        var last = page.End - TimeSpan.FromMilliseconds(10);
        return moment > last ? last : moment;
    }

    private static CaptionScene BuildScene(IReadOnlyList<CaptionWord> words, VerticalComposition composition)
    {
        var settings = composition.Captions;
        return CaptionLayout.Build(
            words,
            composition.Duration,
            settings.ResolveStyle(),
            settings.Position,
            settings.Scale,
            composition.CanvasWidth,
            composition.CanvasHeight,
            composition.CaptionMiddle);
    }

    private CaptionScene BuildSampleScene()
    {
        var words = new List<CaptionWord>(SampleWords.Length);
        for (var index = 0; index < SampleWords.Length; index++)
            words.Add(new CaptionWord(SampleWords[index], TimeSpan.FromSeconds(index), TimeSpan.FromSeconds(index + 1)));

        var settings = _composition.Captions;
        return CaptionLayout.Build(
            words,
            TimeSpan.FromSeconds(SampleWords.Length + 1),
            settings.ResolveStyle(),
            settings.Position,
            settings.Scale,
            _composition.CanvasWidth,
            _composition.CanvasHeight,
            _composition.CaptionMiddle);
    }

    /// <summary>The words of a transcript that fall inside a clip, timed from the start of the short.</summary>
    /// <param name="first">Receives the position in the transcript of the first word returned.</param>
    private static List<CaptionWord> WordsFor(Transcript transcript, ClipSource clip, TimeSpan duration, out int first)
    {
        var (start, count) = transcript.Range(clip.Start, clip.Start + duration);
        first = start;

        return transcript.Words
            .GetRange(start, count)
            .Select(word => new CaptionWord(word.Text, word.Start - clip.Start, word.End - clip.Start))
            .ToList();
    }

    /// <summary>
    /// Makes the text list match the caption pages. Rows are updated where they stand, so the
    /// box being typed in is never taken away.
    /// </summary>
    private void RebuildCaptionLines()
    {
        var pages = _captionScene?.Pages ?? Array.Empty<CaptionPage>();
        var transcript = _captionTranscript;

        for (var index = 0; index < pages.Count && transcript != null; index++)
        {
            var page = pages[index];
            var first = _captionFirstWord + page.FirstWord;

            // The row shows the words as they are in the transcript, punctuation included, even
            // when the style leaves the punctuation out.
            var text = string.Join(' ', transcript.Words.GetRange(first, page.WordCount).Select(word => word.Text.Trim()));

            if (index < CaptionLines.Count)
                CaptionLines[index].Set(page.Start, text, first, page.WordCount);
            else
                CaptionLines.Add(new CaptionLineItem(page.Start, text, first, page.WordCount));
        }

        var wanted = transcript == null ? 0 : pages.Count;
        while (CaptionLines.Count > wanted)
            CaptionLines.RemoveAt(CaptionLines.Count - 1);
    }

    private void UpdateCaptionPanel()
    {
        var settings = _composition.Captions;
        var style = settings.ResolveStyle();
        var clip = _composition.CaptionClip;

        _syncingUi = true;

        CaptionsEnabledBox.IsChecked = settings.Enabled;
        CaptionSlotRow.IsVisible = _composition.Layout == VerticalLayoutKind.Stacked;
        CaptionTopRadio.IsChecked = _composition.CaptionSlot == 0;
        CaptionBottomRadio.IsChecked = _composition.CaptionSlot == 1;

        CaptionStyleList.SelectedItem = CaptionStyleTiles.FirstOrDefault(tile => tile.Style.Id == style.Id);
        CaptionPositionTopRadio.IsChecked = settings.Position == CaptionPosition.Top;
        CaptionPositionMiddleRadio.IsChecked = settings.Position == CaptionPosition.Middle;
        CaptionPositionBottomRadio.IsChecked = settings.Position == CaptionPosition.Bottom;
        CaptionSizeSlider.Value = Math.Round(settings.Scale * 100);
        CaptionCapsBox.IsChecked = style.Uppercase;

        // The first swatch shows the colour the chosen style comes with, marked with a letter
        // that stays readable on it.
        var own = CaptionStyle.Find(settings.StyleId).Accent;
        _accentSwatches[0].Background = ToBrush(own);
        if (_accentSwatches[0].Content is TextBlock mark)
        {
            var brightness = 0.299 * own.Red + 0.587 * own.Green + 0.114 * own.Blue;
            mark.Foreground = brightness > 140 ? SolidColorBrush.Parse("#0E1118") : Brushes.White;
        }

        foreach (var swatch in _accentSwatches)
            swatch.IsChecked = Equals(swatch.Tag as SKColor?, settings.Accent);

        _syncingUi = false;

        CaptionSizeText.Text = $"{Math.Round(settings.Scale * 100)}%";
        StyleOffHintText.IsVisible = !settings.Enabled;
        CaptionAccentLabel.Text = style.AccentRole switch
        {
            CaptionAccent.ActiveFill => "Highlight colour",
            CaptionAccent.Progress => "Spoken colour",
            CaptionAccent.ActiveBox => "Marker colour",
            CaptionAccent.PageBox => "Box colour",
            CaptionAccent.Glow => "Glow colour",
            _ => "Text colour"
        };

        CaptionStatusText.Text = DescribeCaptions(clip);
        CaptionLinesList.IsVisible = CaptionLines.Count > 0;

        // While a job runs, its button is the Cancel button and the others stay locked.
        if (IsBusy)
            return;

        GenerateCaptionsButton.Content = _captionTranscript == null ? GenerateCaptionsText : RegenerateCaptionsText;
        GenerateCaptionsButton.IsEnabled = clip is { Info.HasAudio: true };
        ImportCaptionsButton.IsEnabled = clip != null;
    }

    private string DescribeCaptions(ClipSource? clip)
    {
        if (clip == null)
            return "Put a clip on the canvas, then generate its captions here.";

        if (_captionTranscript is { } transcript)
        {
            var count = transcript.Range(clip.Start, clip.Start + _composition.Duration).Count;
            if (count == 0)
                return $"No speech was found in this part of {Path.GetFileName(clip.File)}.";

            var language = string.IsNullOrEmpty(transcript.Language) ? string.Empty : $"  ·  {SpeechLanguage.NameOf(transcript.Language)}";
            var edited = transcript.IsEdited ? ", edited" : string.Empty;
            return $"{count} words{language}  ·  {transcript.Origin}{edited}. Correct any line below and press Enter.";
        }

        if (!clip.Info.HasAudio)
            return "This clip has no sound to listen to. A subtitle file can still be imported.";

        var model = SelectedSpeechModel;
        var where = _speech.HasModel(model)
            ? "Captions are made on this computer; nothing is uploaded."
            : $"The first time, a speech model of about {model.SizeMb} MB is downloaded. After that it all runs on this computer.";

        return _composition.Captions.Enabled
            ? $"This clip has no captions yet, so sample words are shown. Generate them now, or simply export: missing captions are made on the way. {where}"
            : where;
    }

    // ---------------------------------------------------------------- Captions tab

    private void CaptionsEnabled_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingUi)
            return;

        _composition.Captions.Enabled = CaptionsEnabledBox.IsChecked == true;
        RefreshCaptionScene();
        UpdateCaptionPanel();
    }

    private void CaptionSlot_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingUi || sender is not RadioButton { IsChecked: true } radio)
            return;

        _composition.Captions.Slot = ReferenceEquals(radio, CaptionBottomRadio) ? 1 : 0;
        RefreshCaptions();
    }

    private void CaptionModel_Changed(object? sender, SelectionChangedEventArgs e)
    {
        // The status line says how big the download for the chosen model is.
        if (_ready && !_syncingUi)
            UpdateCaptionPanel();
    }

    private async void GenerateCaptions_Click(object? sender, RoutedEventArgs e)
    {
        if (CancelRunningWork())
            return;

        var composition = _composition.Clone();
        if (composition.CaptionClip is not { } clip)
        {
            Log("ERROR: Put a clip on the canvas first.");
            return;
        }

        if (!clip.Info.HasAudio)
        {
            Log("ERROR: This clip has no sound to listen to.");
            return;
        }

        var model = SelectedSpeechModel;
        var language = SelectedSpeechLanguage;
        var end = clip.Start + composition.Duration;
        Transcript? made = null;

        var completed = await RunWorkAsync(GenerateCaptionsButton, GenerateCaptionsText, "Cancel", async (progress, token) =>
        {
            made = await TranscribeAsync(clip, end, model, language, fraction => progress(fraction * 100d), token);
        });

        if (completed && made != null)
        {
            // Captions someone asked for are captions they want in the short.
            _composition.Captions.Enabled = true;

            var language2 = string.IsNullOrEmpty(made.Language) ? string.Empty : $" in {SpeechLanguage.NameOf(made.Language)}";
            Log(made.Words.Count == 0
                ? "No speech was found in this clip."
                : $"Captions ready: {made.Words.Count} words{language2}. Check the text on the Captions tab.");
        }

        RefreshCaptions();
    }

    /// <summary>Recognises the speech of a clip up to <paramref name="end"/> and remembers the result.</summary>
    /// <param name="progress">Receives progress from 0 to 1.</param>
    private async Task<Transcript> TranscribeAsync(ClipSource clip, TimeSpan end, SpeechModel model, string language, Action<double> progress, CancellationToken token)
    {
        if (!_speech.HasModel(model))
            Log($"Downloading the speech model ({model.SizeMb} MB). This happens once.");

        var lastStep = -1;
        var listening = false;

        // Reported from background threads; Log and the progress callback both cope with that.
        void Report(SpeechProgress update)
        {
            switch (update.Stage)
            {
                case SpeechStage.DownloadingModel:
                {
                    var step = (int)(update.Fraction * 10);
                    if (step > lastStep)
                    {
                        lastStep = step;
                        if (step > 0)
                            Log($"Speech model: {step * 10}% downloaded");
                    }

                    break;
                }

                case SpeechStage.Listening:
                    if (!listening)
                    {
                        listening = true;
                        Log($"Listening to {Path.GetFileName(clip.File)} ({ClipFormat.Time(clip.Start)} - {ClipFormat.Time(end)})");
                    }

                    progress(update.Fraction);
                    break;
            }
        }

        var transcript = await _speech.TranscribeAsync(clip.File, clip.Start, end, model, language, Report, token);
        _transcripts.Add(transcript);
        return transcript;
    }

    /// <summary>True when exporting this composition would first have to listen to its clip.</summary>
    private bool NeedsTranscript(VerticalComposition composition)
        => composition.Captions.Enabled
           && composition.CaptionClip is { Info.HasAudio: true } clip
           && _transcripts.Find(clip.File, clip.Start, clip.Start + composition.Duration) == null;

    /// <summary>
    /// The captions for a composition that is about to be exported, laid out for its frame.
    /// Captions that are wanted but missing are generated first. Returns null when the short gets none.
    /// </summary>
    /// <param name="progress">Receives the progress of listening, from 0 to 1.</param>
    private async Task<CaptionScene?> BuildCaptionSceneAsync(VerticalComposition composition, Action<double> progress, CancellationToken token)
    {
        if (!composition.Captions.Enabled || composition.CaptionClip is not { } clip)
            return null;

        var end = clip.Start + composition.Duration;
        var transcript = _transcripts.Find(clip.File, clip.Start, end);

        if (transcript == null)
        {
            if (!clip.Info.HasAudio)
                return null;

            transcript = await TranscribeAsync(clip, end, SelectedSpeechModel, SelectedSpeechLanguage, progress, token);
        }

        var words = WordsFor(transcript, clip, composition.Duration, out _);
        return words.Count == 0 ? null : BuildScene(words, composition);
    }

    private async void ImportCaptions_Click(object? sender, RoutedEventArgs e)
    {
        if (IsBusy)
            return;

        if (_composition.CaptionClip is not { } clip)
        {
            Log("ERROR: Put a clip on the canvas first.");
            return;
        }

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Subtitles for {Path.GetFileName(clip.File)}",
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("Subtitle files") { Patterns = new[] { "*.srt", "*.vtt" } },
                    FilePickerFileTypes.All
                }
            });

            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
                return;

            var words = SubtitleFile.Read(path);

            // A subtitle file is timed against the whole video, so it covers every clip cut from it.
            var transcript = new Transcript(clip.File, TimeSpan.Zero, clip.Info.Duration, words, $"from {Path.GetFileName(path)}", null);
            _transcripts.Add(transcript);
            _composition.Captions.Enabled = true;

            var inClip = transcript.Range(clip.Start, clip.Start + _composition.Duration).Count;
            Log(inClip > 0
                ? $"Imported {Path.GetFileName(path)}: {inClip} of its {words.Count} words fall inside this clip."
                : $"Imported {Path.GetFileName(path)}, but none of its lines fall inside this clip. Subtitle times are read as times in the source video.");

            RefreshCaptions();
        }
        catch (Exception ex)
        {
            await ReportErrorAsync(ex);
        }
    }

    private void CaptionLineTime_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: CaptionLineItem line } || !_player.HasMedia)
            return;

        // A little way in, so the line is fully on screen rather than still arriving.
        _player.Seek(line.Start + TimeSpan.FromMilliseconds(200));
    }

    private void CaptionLine_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not Control { DataContext: CaptionLineItem line })
            return;

        e.Handled = true;
        CommitCaptionLine(line);
    }

    private void CaptionLine_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CaptionLineItem line })
            CommitCaptionLine(line);
    }

    /// <summary>Writes a corrected line back into the transcript.</summary>
    private void CommitCaptionLine(CaptionLineItem line)
    {
        if (_captionTranscript is not { } transcript || line.Text == line.Original)
            return;

        if (line.FirstWord < 0 || line.FirstWord + line.WordCount > transcript.Words.Count)
            return;

        transcript.ReplaceText(line.FirstWord, line.WordCount, line.Text);

        // The focus may be on its way to another row; the list is refreshed once it has landed.
        if (_captionRefreshQueued)
            return;

        _captionRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _captionRefreshQueued = false;
            RefreshCaptions();
        }, DispatcherPriority.Background);
    }

    // ---------------------------------------------------------------- Style tab

    private void CaptionStyle_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncingUi || CaptionStyleList.SelectedItem is not CaptionStyleTile tile)
            return;

        var settings = _composition.Captions;
        if (settings.StyleId == tile.Style.Id)
            return;

        // A colour or capitals chosen for one style rarely suit the next, so each style starts as designed.
        settings.StyleId = tile.Style.Id;
        settings.Accent = null;
        settings.Uppercase = null;
        RefreshCaptions();
    }

    private void CaptionPosition_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingUi || sender is not RadioButton { IsChecked: true } radio)
            return;

        _composition.Captions.Position = ReferenceEquals(radio, CaptionPositionTopRadio) ? CaptionPosition.Top
            : ReferenceEquals(radio, CaptionPositionMiddleRadio) ? CaptionPosition.Middle
            : CaptionPosition.Bottom;

        RefreshCaptionScene();
    }

    private void CaptionSize_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_ready || _syncingUi)
            return;

        _composition.Captions.Scale = Math.Clamp(e.NewValue / 100d, CaptionSettings.MinScale, CaptionSettings.MaxScale);
        CaptionSizeText.Text = $"{Math.Round(e.NewValue)}%";

        // Bigger text fits fewer words on a line, so the lines of the text list can change too.
        RefreshCaptionScene();
        CaptionLinesList.IsVisible = CaptionLines.Count > 0;
    }

    private void CaptionCaps_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingUi)
            return;

        _composition.Captions.Uppercase = CaptionCapsBox.IsChecked == true;
        RefreshCaptionScene();
    }

    private void CaptionAccent_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingUi || sender is not RadioButton { IsChecked: true } swatch)
            return;

        _composition.Captions.Accent = swatch.Tag as SKColor?;
        RefreshCaptionScene();
    }
}
