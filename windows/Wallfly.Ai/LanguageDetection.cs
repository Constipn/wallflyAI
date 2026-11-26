using System.Globalization;
using System.Text;
using Lingua;

namespace Wallfly.Ai;

/// <summary>The best language hypothesis of one text: an ISO 639-1 code and a probability between 0 and 1.</summary>
public readonly record struct LanguageHypothesis(string Code, double Confidence);

/// <summary>The seam that replaces <c>NLLanguageRecognizer</c>; the thresholds live in <see cref="MeetingOutputLanguage"/>.</summary>
public interface ILanguageDetector
{
    /// <summary>The most likely language, or null when undetermined.</summary>
    LanguageHypothesis? Best(string text);
}

/// <summary>
/// Offline detection with Lingua (75 languages). Low-accuracy mode reads only the trigram models, which ship with
/// the app; its confidence values are probabilities that sum to 1, like the <c>NLLanguageRecognizer</c> hypotheses.
/// </summary>
public sealed class LinguaLanguageDetector : ILanguageDetector
{
    public static LinguaLanguageDetector Shared { get; } = new();

    private static readonly Lazy<LanguageDetector> Detector = new(
        () => LanguageDetectorBuilder.FromAllLanguages().WithLowAccuracyMode().Build(), LazyThreadSafetyMode.ExecutionAndPublication);

    public LanguageHypothesis? Best(string text)
    {
        foreach (var (language, confidence) in Detector.Value.ComputeLanguageConfidenceValues(text))
        {
            if (language == Language.Unknown || confidence <= 0) return null;
            return new(language.IsoCode6391().ToString().ToLowerInvariant(), confidence);
        }
        return null;
    }
}

/// <summary>
/// Port of <c>MeetingOutputLanguage</c>: the output language is determined once from the spoken content, so app
/// locale, titles, speaker labels and typed templates cannot turn a German meeting into an English summary.
/// </summary>
public sealed class MeetingOutputLanguage
{
    public const int DetectionChunkBytes = 2400;
    public const int MinimumLetters = 24;
    public const double MinimumConfidence = .65;
    public const double MinimumShare = .6;
    public const int CorrectionMinimumLetters = 80;
    public const double CorrectionMinimumConfidence = .85;
    public const int ReferenceChunkBytes = 400;

    private readonly ILanguageDetector detector;
    private readonly string sourceDescription;
    private readonly string reference;

    /// <param name="spokenText">The ordered transcript passages joined by newlines, without timestamps or speaker names.</param>
    /// <param name="notes">The typed notes; used only when nothing was spoken.</param>
    public MeetingOutputLanguage(string spokenText, string notes, ILanguageDetector? detector = null)
    {
        this.detector = detector ?? LinguaLanguageDetector.Shared;
        var hasTranscript = !string.IsNullOrWhiteSpace(spokenText);
        var source = hasTranscript ? spokenText : notes ?? "";
        Code = Detect(source, this.detector);
        sourceDescription = hasTranscript ? "the predominant spoken language of the original meeting transcript" : "the language of the original typed notes";
        reference = Code == null ? LanguageSample(source) : "";
    }

    /// <summary>The detected ISO 639-1 code, or null when no language is clear enough.</summary>
    public string? Code { get; }

    public string Instruction
    {
        get
        {
            var language = Code == null ? sourceDescription : $"{EnglishName(Code)} ({Code})";
            return Prompts.Fill("LanguageInstruction", ("LANGUAGE", language));
        }
    }

    /// <summary>Appends the language reference to a prompt (never to the system instructions) when detection was unsure.</summary>
    public string Contextualize(string prompt) =>
        reference.Length == 0 ? prompt : prompt + "\n" + Prompts.Fill("LanguageReference", ("REFERENCE", reference));

    /// <summary>True only when the text is confidently (80 letters, 0.85) in another language than the meeting.</summary>
    public bool ConfidentlyDisagrees(string text) =>
        Code != null && Detect(text, detector, CorrectionMinimumLetters, CorrectionMinimumConfidence) is { } detected && detected != Code;

    /// <summary>
    /// Covers the complete meeting in 2 400-byte chunks, weighted by letter count so a short foreign aside cannot
    /// select the output language. A language wins with at least 60 % of all counted letters.
    /// </summary>
    public static string? Detect(string text, ILanguageDetector detector, int minimumLetters = MinimumLetters, double minimumConfidence = MinimumConfidence)
    {
        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        var total = 0.0;
        foreach (var chunk in NotesEnhancer.Chunks(text, DetectionChunkBytes))
        {
            var letters = Letters(chunk);
            if (letters < minimumLetters) continue;
            total += letters;
            if (detector.Best(chunk) is not { } best || best.Confidence < minimumConfidence || string.IsNullOrEmpty(best.Code)) continue;
            weights[best.Code] = weights.GetValueOrDefault(best.Code) + letters;
        }
        if (weights.Count == 0) return null;
        var winner = weights.MaxBy(pair => pair.Value);
        return winner.Value / Math.Max(1, total) >= MinimumShare ? winner.Key : null;
    }

    /// <summary>Unicode scalars in <c>CharacterSet.letters</c> (categories L* and M*).</summary>
    public static int Letters(string text)
    {
        var count = 0;
        foreach (var rune in text.EnumerateRunes()) if (TaskTextKey.IsLetter(Rune.GetUnicodeCategory(rune))) count++;
        return count;
    }

    /// <summary>Opening, middle and ending (400 bytes each) bound the extra model context.</summary>
    public static string LanguageSample(string source)
    {
        var parts = NotesEnhancer.Chunks(source, ReferenceChunkBytes);
        if (parts.Count == 0) return "";
        return string.Join("\n[…]\n", new SortedSet<int> { 0, parts.Count / 2, parts.Count - 1 }.Select(index => parts[index]));
    }

    /// <summary><c>Locale(identifier: "en").localizedString(forLanguageCode:)</c>.</summary>
    public static string EnglishName(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            return culture.EnglishName.StartsWith("Unknown", StringComparison.Ordinal) ? code : culture.EnglishName;
        }
        catch (CultureNotFoundException) { return code; }
    }
}
