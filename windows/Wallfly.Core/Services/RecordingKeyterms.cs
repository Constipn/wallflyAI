namespace Wallfly.Core.Services;

/// <summary>
/// The keyterms of a live Deepgram recording, in priority order (the URL builder caps the list): the user's own name
/// (<see cref="AppSettings.TaskOwnerName"/>), the meeting's participants known so far (Teams names, names from an earlier
/// attempt), the user's <see cref="AppSettings.CustomVocabulary"/>, then significant words of the meeting title.
/// Duplicates are removed later by <c>DeepgramUrlBuilder.Keyterms</c>. The terms are names: never log them, only their count.
/// </summary>
public static class RecordingKeyterms
{
    /// <summary>Title words shorter than this are dropped.</summary>
    public const int MinimumTitleWordLength = 4;

    /// <summary>Frequent German and English words and generic meeting words that would only waste the keyterm budget.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // English
        "about", "after", "also", "before", "from", "have", "into", "just", "like", "more", "only", "other", "over", "some",
        "than", "that", "their", "them", "then", "there", "these", "they", "this", "what", "when", "where", "which", "while",
        "will", "with", "your", "meeting", "call", "sync", "weekly", "daily", "monthly", "update", "updates", "review",
        "check", "catch", "intro", "follow", "discussion", "session", "team", "teams", "online", "today", "tomorrow", "next",
        // German
        "aber", "alle", "auch", "auf", "aus", "beim", "bitte", "dass", "dein", "deine", "denn", "dies", "diese", "dieser",
        "doch", "eine", "einen", "einem", "einer", "noch", "nach", "oder", "ohne", "sein", "seine", "sich", "sind", "über",
        "unser", "unsere", "unter", "viel", "vom", "von", "wegen", "weil", "wenn", "wird", "wir", "zum", "zur", "zwischen",
        "besprechung", "termin", "abstimmung", "runde", "gespräch", "treffen", "jour", "fixe", "kurz", "kurze", "kurzes",
        "wöchentlich", "wöchentliche", "täglich", "tägliche", "heute", "morgen", "nächste", "nächster"
    };

    /// <summary>
    /// The names among the meeting's participants: synthetic "Speaker k" participants (<see cref="TranscriptDisplay.AcousticSourceKeyPrefix"/>)
    /// are unnamed voices, not names, and are left out. The one filter of the live stream and the cloud pass.
    /// </summary>
    public static List<string> ParticipantNames(IEnumerable<MeetingParticipant>? participants) =>
        (participants ?? []).Where(person => person.SourceKey?.StartsWith(TranscriptDisplay.AcousticSourceKeyPrefix, StringComparison.Ordinal) != true)
            .Select(person => person.Name).ToList();

    /// <param name="participantNames">Names of the meeting's participants known at this moment (<see cref="ParticipantNames"/>).</param>
    /// <param name="title">The meeting title; null or a placeholder adds nothing.</param>
    public static IReadOnlyList<string> For(AppSettings settings, IEnumerable<string?> participantNames, string? title)
    {
        var terms = new List<string>();
        if (settings.TaskOwnerName is { Length: > 0 } owner) terms.Add(owner);
        terms.AddRange(participantNames.Where(name => !string.IsNullOrWhiteSpace(name))!);
        terms.AddRange(settings.CustomVocabulary ?? []);
        terms.AddRange(TitleWords(title));
        return terms;
    }

    /// <summary>
    /// The significant words of a meeting title: split at whitespace and punctuation (hyphenated words stay whole), without
    /// words shorter than <see cref="MinimumTitleWordLength"/>, numbers and stop words. Placeholder titles add nothing.
    /// </summary>
    public static IReadOnlyList<string> TitleWords(string? title)
    {
        if (Wallfly.Ai.NotesEnhancer.IsPlaceholderTitle(title)) return [];
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        void Flush()
        {
            var word = current.ToString().Trim('-', '\'');
            current.Clear();
            if (word.Length >= MinimumTitleWordLength && word.Any(char.IsLetter) && !StopWords.Contains(word)) words.Add(word);
        }
        foreach (var character in title!)
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '\'') current.Append(character);
            else Flush();
        }
        Flush();
        return words;
    }
}
