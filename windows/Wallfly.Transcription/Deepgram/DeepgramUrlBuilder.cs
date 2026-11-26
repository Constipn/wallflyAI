namespace Wallfly.Transcription.Deepgram;

/// <summary>
/// The fixed Nova-3 streaming contract (port of the macOS <c>DeepgramConfiguration</c>): mono 16 kHz
/// linear16, interim results, smart format, 100 ms endpointing, language <c>multi</c> unless German
/// or English is chosen. <c>utterance_end_ms</c>, <c>diarize</c>, <c>punctuate</c> and <c>vad_events</c>
/// are deliberately not sent. Windows additions: with <c>multi</c> the request biases detection towards
/// German and English (repeated <c>language_hint</c>), and optional keyterms (repeated <c>keyterm</c>,
/// Nova-3 keyterm prompting) are sent in every language mode. The key travels only in the header, never in the URL.
/// </summary>
public static class DeepgramUrlBuilder
{
    public const string Endpoint = "wss://api.deepgram.com/v1/listen";
    public const string Model = "nova-3";

    /// <summary>Sent as repeated <c>language_hint</c> only with <c>language=multi</c> (Deepgram ignores them otherwise).</summary>
    public static readonly IReadOnlyList<string> LanguageHints = ["de", "en"];

    /// <summary>
    /// At most this many keyterms per request. Deepgram allows 500 tokens across all keyterms and recommends
    /// focusing on the most important 20–50 terms.
    /// </summary>
    public const int MaxKeyterms = 50;

    /// <summary>A longer term is dropped (not cut): a truncated name or phrase would boost the wrong words.</summary>
    public const int MaxKeytermLength = 50;

    /// <summary>
    /// Character budget across all keyterms. At two or more characters per token this stays within Deepgram's
    /// 500-token limit; names and words usually need fewer tokens than that.
    /// </summary>
    public const int MaxKeytermCharacters = 1000;

    /// <summary>"de-DE"/"de" → de, "en-US"/"en" → en, everything else ("auto", "und", other languages) → multi.</summary>
    public static string Language(string? locale)
    {
        var code = (locale ?? "").Split('-', '_')[0].Trim().ToLowerInvariant();
        return code switch { "de" => "de", "en" => "en", _ => "multi" };
    }

    /// <summary>
    /// The keyterms a request carries, in the given priority order: whitespace collapsed and trimmed, empty and
    /// over-long terms dropped, duplicates removed case-insensitively (the first spelling wins), and capped at
    /// <see cref="MaxKeyterms"/> terms and <see cref="MaxKeytermCharacters"/> characters.
    /// </summary>
    public static IReadOnlyList<string> Keyterms(IEnumerable<string?>? terms)
    {
        var result = new List<string>();
        if (terms == null) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var characters = 0;
        foreach (var raw in terms)
        {
            if (result.Count >= MaxKeyterms) break;
            var term = string.Join(' ', (raw ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (term.Length == 0 || term.Length > MaxKeytermLength || characters + term.Length > MaxKeytermCharacters || !seen.Add(term)) continue;
            result.Add(term);
            characters += term.Length;
        }
        return result;
    }

    /// <summary>
    /// Query items sorted by name, exactly as the macOS build sends them; repeated items keep their order.
    /// </summary>
    /// <param name="keyterms">Normalized through <see cref="Keyterms"/>; null or empty sends none.</param>
    /// <param name="languageHints">False sends the bare macOS contract with <c>multi</c> (used after Deepgram rejected an enriched request).</param>
    public static IReadOnlyList<KeyValuePair<string, string>> Parameters(string? locale, IEnumerable<string?>? keyterms = null, bool languageHints = true)
    {
        var language = Language(locale);
        var items = new Dictionary<string, string>
        {
            ["model"] = Model,
            ["encoding"] = "linear16",
            ["sample_rate"] = "16000",
            ["channels"] = "1",
            ["interim_results"] = "true",
            ["smart_format"] = "true",
            ["language"] = language,
            ["endpointing"] = "100"
        }.ToList();
        if (languageHints && language == "multi") items.AddRange(LanguageHints.Select(hint => KeyValuePair.Create("language_hint", hint)));
        items.AddRange(Keyterms(keyterms).Select(term => KeyValuePair.Create("keyterm", term)));
        // OrderBy is stable: repeated parameters keep their priority order.
        return items.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
    }

    public static Uri Build(string? locale, IEnumerable<string?>? keyterms = null, bool languageHints = true) =>
        new(Endpoint + "?" + string.Join('&', Parameters(locale, keyterms, languageHints)
            .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));

    public static WebSocketRequest Request(string key, string? locale, IEnumerable<string?>? keyterms = null, bool languageHints = true) =>
        new(Build(locale, keyterms, languageHints), "Token " + key.Trim());
}
