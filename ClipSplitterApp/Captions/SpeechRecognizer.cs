using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ClipSplitterApp.Services;
using Whisper.net;
using Whisper.net.Ggml;

namespace ClipSplitterApp.Captions;

/// <summary>A speech recognition model the user can choose. Bigger models hear better and take longer.</summary>
/// <param name="Heads">Tells the engine which parts of this model line words up with the sound.</param>
public sealed record SpeechModel(string Id, string Name, string Summary, GgmlType Type, WhisperAlignmentHeadsPreset Heads, int SizeMb)
{
    public static readonly SpeechModel Fast = new("base", "Fast", "Quick, and fine for clear English", GgmlType.Base, WhisperAlignmentHeadsPreset.Base, 60);
    public static readonly SpeechModel Accurate = new("small", "Accurate", "Fewer mistakes, better with accents and other languages", GgmlType.Small, WhisperAlignmentHeadsPreset.Small, 190);
    public static readonly SpeechModel Best = new("large-v3-turbo", "Best", "The most accurate, and much slower", GgmlType.LargeV3Turbo, WhisperAlignmentHeadsPreset.LargeV3Turbo, 624);

    public static IReadOnlyList<SpeechModel> All { get; } = new[] { Fast, Accurate, Best };

    public const QuantizationType Quantization = QuantizationType.Q5_1;

    public string FileName => $"ggml-{Id}-q5_1.bin";

    public string Title => $"{Name}  ·  {SizeMb} MB";

    public static SpeechModel Find(string? id) => All.FirstOrDefault(model => model.Id == id) ?? Accurate;
}

/// <summary>A language the user can tell the recognizer to expect.</summary>
public sealed record SpeechLanguage(string Code, string Name)
{
    public const string AutoCode = "auto";

    public static IReadOnlyList<SpeechLanguage> All { get; } = new[]
    {
        new SpeechLanguage(AutoCode, "Auto-detect"),
        new SpeechLanguage("en", "English"),
        new SpeechLanguage("es", "Spanish"),
        new SpeechLanguage("pt", "Portuguese"),
        new SpeechLanguage("fr", "French"),
        new SpeechLanguage("de", "German"),
        new SpeechLanguage("it", "Italian"),
        new SpeechLanguage("ro", "Romanian"),
        new SpeechLanguage("nl", "Dutch"),
        new SpeechLanguage("pl", "Polish"),
        new SpeechLanguage("ru", "Russian"),
        new SpeechLanguage("uk", "Ukrainian"),
        new SpeechLanguage("tr", "Turkish"),
        new SpeechLanguage("el", "Greek"),
        new SpeechLanguage("cs", "Czech"),
        new SpeechLanguage("hu", "Hungarian"),
        new SpeechLanguage("bg", "Bulgarian"),
        new SpeechLanguage("sv", "Swedish"),
        new SpeechLanguage("no", "Norwegian"),
        new SpeechLanguage("da", "Danish"),
        new SpeechLanguage("fi", "Finnish"),
        new SpeechLanguage("ar", "Arabic"),
        new SpeechLanguage("he", "Hebrew"),
        new SpeechLanguage("hi", "Hindi"),
        new SpeechLanguage("id", "Indonesian"),
        new SpeechLanguage("vi", "Vietnamese"),
        new SpeechLanguage("th", "Thai"),
        new SpeechLanguage("ja", "Japanese"),
        new SpeechLanguage("ko", "Korean"),
        new SpeechLanguage("zh", "Chinese")
    };

    public static string NameOf(string? code)
        => All.FirstOrDefault(language => language.Code == code)?.Name ?? code ?? string.Empty;
}

public enum SpeechStage
{
    DownloadingModel,
    LoadingModel,
    ReadingSound,
    Listening
}

public readonly record struct SpeechProgress(SpeechStage Stage, double Fraction);

/// <summary>
/// One piece of text from the recognizer, usually a word or part of one, starting with a space
/// when it begins a new word.
/// </summary>
/// <param name="Text">The text of the piece.</param>
/// <param name="Start">Where the recognizer guesses the piece starts. Often late at the end of a sentence.</param>
/// <param name="End">Where the recognizer guesses the piece ends.</param>
/// <param name="AlignedEnd">Where the piece ends according to the alignment with the sound, which is far more exact, when that is available.</param>
public readonly record struct SpeechToken(string Text, TimeSpan Start, TimeSpan End, TimeSpan? AlignedEnd);

/// <summary>Raised when speech recognition cannot run on this computer at all.</summary>
public sealed class SpeechUnavailableException : InvalidOperationException
{
    public SpeechUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Turns the speech in a clip into timed words. Recognition runs on this computer with Whisper;
/// the only thing fetched from the internet is the model, once.
/// </summary>
public sealed class SpeechRecognizer : IDisposable
{
    /// <summary>
    /// The longest stretch that can be captioned in one go. Shorts are far shorter; the limit
    /// only stops a whole film being read into memory by mistake.
    /// </summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(20);

    private const int SampleRate = 16000;
    private const int MinModelBytes = 1_000_000;

    /// <summary>A word shorter than this has been squeezed against its neighbour by the alignment.</summary>
    private static readonly TimeSpan CrushedWord = TimeSpan.FromMilliseconds(60);

    /// <summary>Words closer together than this count as following straight on from each other.</summary>
    private static readonly TimeSpan Touching = TimeSpan.FromMilliseconds(40);

    private readonly string _modelFolder;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;
    private string? _factoryModel;
    private bool _disposed;

    public SpeechRecognizer(string? modelFolder = null)
    {
        _modelFolder = modelFolder ?? DefaultModelFolder;
    }

    /// <summary>Where downloaded models are kept, so they are fetched only once.</summary>
    public static string DefaultModelFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipSplitter", "models");

    public bool HasModel(SpeechModel model)
    {
        var info = new FileInfo(PathOf(model));
        return info.Exists && info.Length > MinModelBytes;
    }

    /// <summary>Recognises the speech in a stretch of a video file.</summary>
    public async Task<Transcript> TranscribeAsync(
        string file,
        TimeSpan start,
        TimeSpan end,
        SpeechModel model,
        string language,
        Action<SpeechProgress>? progress,
        CancellationToken token)
    {
        if (end - start > MaxLength)
            throw new InvalidOperationException($"Captions can be generated for up to {MaxLength.TotalMinutes:0} minutes at a time. Split the video into shorter clips first.");

        // The model comes first: there is no point reading the sound if it cannot be fetched.
        await _gate.WaitAsync(token);
        try
        {
            await EnsureModelAsync(model, progress, token);
        }
        finally
        {
            _gate.Release();
        }

        progress?.Invoke(new SpeechProgress(SpeechStage.ReadingSound, 0));
        var samples = await ReadSoundAsync(file, start, end - start, token);

        var (words, detected) = await RecognizeAsync(samples, model, language, progress, token);
        var shifted = words.Select(word => new CaptionWord(word.Text, word.Start + start, word.End + start));
        return new Transcript(file, start, end, shifted, "speech recognition", detected);
    }

    /// <summary>Recognises speech in 16 kHz mono samples. Word times are measured from the first sample.</summary>
    public async Task<(IReadOnlyList<CaptionWord> Words, string? Language)> RecognizeAsync(
        float[] samples,
        SpeechModel model,
        string language,
        Action<SpeechProgress>? progress,
        CancellationToken token)
    {
        // Whisper cannot make anything of a fraction of a second.
        if (samples.Length < SampleRate / 4)
            return (Array.Empty<CaptionWord>(), null);

        await _gate.WaitAsync(token);
        try
        {
            await EnsureModelAsync(model, progress, token);
            progress?.Invoke(new SpeechProgress(SpeechStage.LoadingModel, 0));

            return await Task.Run(async () =>
            {
                var factory = GetFactory(model);
                token.ThrowIfCancellationRequested();

                var builder = factory.CreateBuilder()
                    .WithThreads(ThreadCount)
                    .WithLanguage(string.IsNullOrWhiteSpace(language) ? SpeechLanguage.AutoCode : language)
                    .WithTokenTimestamps()
                    .WithProgressHandler(percent => progress?.Invoke(new SpeechProgress(SpeechStage.Listening, Math.Clamp(percent / 100d, 0, 1))));

                await using var processor = builder.Build();
                progress?.Invoke(new SpeechProgress(SpeechStage.Listening, 0));

                var tokens = new List<SpeechToken>();
                string? detected = null;

                await foreach (var segment in processor.ProcessAsync(samples, token))
                {
                    if (!string.IsNullOrWhiteSpace(segment.Language))
                        detected ??= segment.Language;

                    if (segment.Tokens is { Length: > 0 } pieces)
                    {
                        foreach (var piece in pieces)
                        {
                            tokens.Add(new SpeechToken(
                                piece.Text ?? string.Empty,
                                FromHundredths(piece.Start),
                                FromHundredths(piece.End),
                                piece.DtwTimestamp >= 0 ? FromHundredths(piece.DtwTimestamp) : null));
                        }
                    }
                    else
                    {
                        // Without pieces there is only the sentence and when it was said, so its
                        // words share that time between them.
                        var said = segment.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        foreach (var word in Transcript.Spread(said, segment.Start, segment.End))
                            tokens.Add(new SpeechToken(" " + word.Text, word.Start, word.End, null));
                    }
                }

                var length = TimeSpan.FromSeconds((double)samples.Length / SampleRate);
                return ((IReadOnlyList<CaptionWord>)BuildWords(tokens, length), detected);
            }, token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // The engine must not be freed while it is still listening. A job that has been
        // cancelled stops within moments; one that has not is left for the process to clean up.
        if (!_gate.Wait(TimeSpan.FromSeconds(3)))
            return;

        try
        {
            _factory?.Dispose();
            _factory = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Joins the recognizer's pieces into words and times each one. With alignment a word runs
    /// from where the one before it ended to where its own last letter ends, which leaves the
    /// pause after a full stop as a gap instead of stretching a word across it.
    /// </summary>
    public static List<CaptionWord> BuildWords(IReadOnlyList<SpeechToken> tokens, TimeSpan length)
    {
        var raw = new List<CaptionWord>();
        var text = new System.Text.StringBuilder();
        var start = TimeSpan.Zero;
        var spokenEnd = TimeSpan.Zero;
        var cursor = TimeSpan.Zero;
        var open = false;
        var first = true;

        void Close()
        {
            if (open)
                raw.Add(new CaptionWord(text.ToString(), start, spokenEnd < start ? start : spokenEnd));

            text.Clear();
            open = false;
        }

        foreach (var token in tokens)
        {
            // Markers such as "[_BEG_]" and "[_TT_200]" belong to the engine, not to the speech.
            if (token.Text.Length == 0 || token.Text.StartsWith("[_", StringComparison.Ordinal))
                continue;

            var end = token.AlignedEnd ?? token.End;
            if (end < cursor)
                end = cursor;

            if (!open || token.Text[0] == ' ')
            {
                Close();
                open = true;

                if (token.AlignedEnd == null || first)
                    start = token.Start > end ? end : token.Start < cursor ? cursor : token.Start;
                else
                    start = cursor;

                spokenEnd = start;
            }

            text.Append(token.Text);

            // Punctuation carries the pause that follows it, so it moves the next word's start
            // but not this word's end.
            if (token.Text.Any(char.IsLetterOrDigit))
                spokenEnd = end;

            cursor = end;
            first = false;
        }

        Close();
        return CleanWords(raw, length);
    }

    /// <summary>
    /// Tidies recognised words: drops the recognizer's notes about noises, such as "[MUSIC]",
    /// keeps the words in order and inside the clip, and repairs timings that cannot be right.
    /// </summary>
    public static List<CaptionWord> CleanWords(IEnumerable<CaptionWord> raw, TimeSpan length)
    {
        var words = new List<CaptionWord>();
        var insideNote = false;

        foreach (var word in raw)
        {
            var text = word.Text.Trim();
            if (text.Length == 0)
                continue;

            // Notes can span several words: "(upbeat" "music)".
            if (insideNote)
            {
                insideNote = !EndsNote(text);
                continue;
            }

            if (text[0] is '[' or '(' or '*' or '♪')
            {
                insideNote = text[0] != '♪' && !EndsNote(text);
                continue;
            }

            if (!text.Any(char.IsLetterOrDigit))
                continue;

            // A word timed past the end was still spoken inside the clip, so it is kept and
            // pulled back in rather than dropped.
            var start = Clamp(word.Start, TimeSpan.Zero, length);
            if (words.Count > 0 && start < words[^1].Start)
                start = words[^1].Start;

            words.Add(new CaptionWord(text, start, Clamp(word.End, start, length)));
        }

        for (var index = 1; index < words.Count; index++)
        {
            if (words[index - 1].End > words[index].Start)
                words[index - 1] = words[index - 1] with { End = words[index].Start };
        }

        EvenOutCrushedWords(words);

        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];

            // A word that seems to last far too long has swallowed a silence before it. The word
            // itself is at the end of that stretch; where it began is not known, so it gets the
            // time a word of its length usually takes.
            if (word.End - word.Start > LongestPlausible(word.Text))
                word = word with { Start = word.End - UsualLength(word.Text) };

            // A word left too short takes what free time follows it.
            if (word.End - word.Start < CrushedWord)
            {
                var limit = index + 1 < words.Count ? words[index + 1].Start : length;
                var wanted = word.Start + CrushedWord;
                word = word with { End = wanted < limit ? wanted : limit > word.Start ? limit : word.Start };
            }

            words[index] = word;
        }

        var result = new List<CaptionWord>(words.Count);
        foreach (var word in words)
            result.AddRange(SplitUnspaced(word.Text, word.Start, word.End));

        return result;
    }

    /// <summary>
    /// The alignment sometimes gives several words in a row the same instant, having given all
    /// their time to the word before them. The run then shares that time by the words' lengths.
    /// </summary>
    private static void EvenOutCrushedWords(List<CaptionWord> words)
    {
        var index = 0;
        while (index < words.Count)
        {
            if (!IsCrushed(words[index]))
            {
                index++;
                continue;
            }

            var first = index;
            if (first > 0 && words[first].Start - words[first - 1].End <= Touching)
                first--;

            var last = index;
            while (last + 1 < words.Count && IsCrushed(words[last + 1]) && words[last + 1].Start - words[last].End <= Touching)
                last++;

            // With no word before it to share with, the word after it shares instead.
            if (first == index && last + 1 < words.Count && words[last + 1].Start - words[last].End <= Touching)
                last++;

            if (last > first)
            {
                var texts = words.GetRange(first, last - first + 1).Select(word => word.Text).ToList();
                var shared = Transcript.Spread(texts, words[first].Start, words[last].End).ToList();
                for (var offset = 0; offset < shared.Count; offset++)
                    words[first + offset] = shared[offset];
            }

            index = last + 1;
        }
    }

    private static bool IsCrushed(CaptionWord word) => word.End - word.Start < CrushedWord;

    /// <summary>Generous even for slow, drawn-out speech: a third of a second plus an eighth for each letter.</summary>
    private static TimeSpan LongestPlausible(string text)
        => TimeSpan.FromSeconds(0.3 + 0.12 * text.Count(char.IsLetterOrDigit));

    /// <summary>How long a word of this length takes at an ordinary speaking pace.</summary>
    private static TimeSpan UsualLength(string text)
        => TimeSpan.FromSeconds(0.12 + 0.07 * text.Count(char.IsLetterOrDigit));

    private static TimeSpan Clamp(TimeSpan value, TimeSpan low, TimeSpan high)
        => value < low ? low : value > high ? high : value;

    private static TimeSpan FromHundredths(long hundredths) => TimeSpan.FromMilliseconds(hundredths * 10);

    /// <summary>Most of the processor, leaving a little so the app stays responsive while it listens.</summary>
    private static int ThreadCount
    {
        get
        {
            var processors = Environment.ProcessorCount;
            return Math.Clamp(processors - Math.Max(1, processors / 4), 1, 8);
        }
    }

    private static bool EndsNote(string text) => text[^1] is ']' or ')' or '*';

    /// <summary>
    /// Languages written without spaces come back as whole phrases. They are cut into short
    /// pieces so captions can still follow the speech.
    /// </summary>
    private static IEnumerable<CaptionWord> SplitUnspaced(string text, TimeSpan start, TimeSpan end)
    {
        const int pieceLength = 4;
        if (text.Length <= pieceLength + 2 || !text.Any(IsUnspacedScript))
        {
            yield return new CaptionWord(text, start, end);
            yield break;
        }

        var pieces = new List<string>();
        for (var index = 0; index < text.Length; index += pieceLength)
            pieces.Add(text.Substring(index, Math.Min(pieceLength, text.Length - index)));

        // A trailing piece of only punctuation belongs with the piece before it.
        if (pieces.Count > 1 && !pieces[^1].Any(char.IsLetterOrDigit))
        {
            pieces[^2] += pieces[^1];
            pieces.RemoveAt(pieces.Count - 1);
        }

        foreach (var piece in Transcript.Spread(pieces, start, end))
            yield return piece;
    }

    private static bool IsUnspacedScript(char character)
        => character is >= '぀' and <= 'ヿ'   // Japanese kana
            or >= '一' and <= '鿿'            // Chinese characters
            or >= '฀' and <= '๿';           // Thai

    private static async Task<float[]> ReadSoundAsync(string file, TimeSpan start, TimeSpan length, CancellationToken token)
    {
        // first_pts=0 pads a late-starting audio track, so word times line up with the picture
        // exactly as they do in the exported short.
        var bytes = await FfmpegTools.RunFfmpegToBytesAsync(
            new[]
            {
                "-ss", FfmpegTools.ArgTime(start),
                "-t", FfmpegTools.ArgTime(length),
                "-i", file,
                "-vn", "-sn", "-dn",
                "-map", "0:a:0",
                "-af", "aresample=16000:async=1:first_pts=0",
                "-ac", "1",
                "-ar", "16000",
                "-f", "f32le",
                "pipe:1"
            },
            token);

        var samples = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * sizeof(float));
        return samples;
    }

    private string PathOf(SpeechModel model) => Path.Combine(_modelFolder, model.FileName);

    private async Task EnsureModelAsync(SpeechModel model, Action<SpeechProgress>? progress, CancellationToken token)
    {
        if (HasModel(model))
            return;

        var path = PathOf(model);
        var partial = path + ".part";
        Directory.CreateDirectory(_modelFolder);
        progress?.Invoke(new SpeechProgress(SpeechStage.DownloadingModel, 0));

        try
        {
            var expected = model.SizeMb * 1024L * 1024L;
            long received = 0;

            await using (var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(model.Type, SpeechModel.Quantization, token))
            await using (var target = File.Create(partial))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), token);
                    received += read;
                    progress?.Invoke(new SpeechProgress(SpeechStage.DownloadingModel, Math.Min(0.99, (double)received / expected)));
                }
            }

            if (received < MinModelBytes)
                throw new IOException("The download ended early.");

            // Renamed only once complete, so an interrupted download is never mistaken for a model.
            File.Move(partial, path, overwrite: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            FfmpegClipSplitter.TryDelete(partial);
            throw new InvalidOperationException(
                $"The speech model could not be downloaded ({ex.Message}). Check the internet connection and try again. You can also import captions from an .srt file.",
                ex);
        }
        catch
        {
            FfmpegClipSplitter.TryDelete(partial);
            throw;
        }
    }

    private WhisperFactory GetFactory(SpeechModel model)
    {
        if (_factory != null && _factoryModel == model.Id)
            return _factory;

        _factory?.Dispose();
        _factory = null;
        _factoryModel = null;

        try
        {
            // Alignment with the sound is what makes each word's timing exact enough to light
            // it up as it is spoken.
            var options = new WhisperFactoryOptions { UseDtwTimeStamps = true, HeadsPreset = model.Heads };
            _factory = WhisperFactory.FromPath(PathOf(model), options);
            _factoryModel = model.Id;
            return _factory;
        }
        catch (WhisperModelLoadException ex)
        {
            // A damaged file would fail the same way every time, so it is removed and fetched again next time.
            FfmpegClipSplitter.TryDelete(PathOf(model));
            throw new InvalidOperationException("The speech model on this computer was damaged and has been removed. Generate the captions again to download it afresh.", ex);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or PlatformNotSupportedException or TypeInitializationException or FileNotFoundException)
        {
            throw new SpeechUnavailableException(
                "Speech recognition cannot run on this computer. It needs a 64-bit processor with AVX2 and, on Windows, the Microsoft Visual C++ runtime. You can still import captions from an .srt file.",
                ex);
        }
    }
}
