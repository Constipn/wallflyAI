using System.Globalization;
using System.Text;
using Wallfly.Core;
using Wallfly.Speakers;

namespace Wallfly.Diarization;

/// <summary>Thresholds of <see cref="DeepgramSelfCheck"/> (start values; check in Phase B).</summary>
public sealed record SelfCheckOptions
{
    /// <summary>
    /// The plan's limit: a median offset of more than 0.25 s between the live and the pre-recorded word times means the time
    /// mapping is broken (a 0.25-s shift already moves short words and interjections into the neighbouring turn).
    /// </summary>
    public double MaxMedianOffset { get; init; } = 0.25;
    /// <summary>
    /// A pre-recorded word is compared with live words of the same token at most this far away. Wider than any plausible
    /// recogniser difference (≤ 0.5 s), narrow enough that a broken mapping finds no partner and fails on the match count.
    /// </summary>
    public double MatchWindow { get; init; } = 3.0;
    /// <summary>Shorter tokens ("ja", "so", "i") repeat too often to identify one word.</summary>
    public int MinTokenLength { get; init; } = 3;
    /// <summary>
    /// Fewer comparable LIVE words and the check cannot judge: it accepts, since the fusion only labels live words and with so
    /// few of them a wrong time map could do little harm. Only the live side decides: a cloud answer with few or no words is
    /// judged by <see cref="MinPrerecordedShare"/>, never accepted for lack of comparison (an empty result would complete the
    /// pass, and "delete audio after processing" would then remove the audio).
    /// </summary>
    public int MinComparableWords { get; init; } = 10;
    /// <summary>
    /// With enough live words, the pre-recorded answer must have at least this share of their number in comparable words, or it
    /// is rejected (<see cref="SelfCheckVerdict.TooFewWords"/>). Both are Nova-3 transcripts of the same audio and usually differ
    /// by a few per cent (the pre-recorded one tends to have more words); a quarter is far below any such difference and is
    /// only reached when Deepgram heard little of the upload (an empty, truncated or wrongly decoded body, silence).
    /// </summary>
    public double MinPrerecordedShare { get; init; } = 0.25;
    /// <summary>Matches needed in a long meeting (with <see cref="MinMatchShare"/>).</summary>
    public int MinMatches { get; init; } = 20;
    /// <summary>
    /// Matches needed as a share of the comparable words of the smaller side. Two recognisers on the same audio agree on most
    /// longer tokens and the uniqueness rule keeps roughly half of those; 10 % leaves a wide margin and still fails when the
    /// mapping is off by more than <see cref="MatchWindow"/>.
    /// </summary>
    public double MinMatchShare { get; init; } = 0.10;
    /// <summary>Short meetings (where <see cref="MinMatches"/> would exceed what they can give): this share of the comparable words suffices.</summary>
    public double SmallMeetingShare { get; init; } = 0.25;
}

public enum SelfCheckVerdict
{
    /// <summary>Enough matches, median offset within the limit.</summary>
    Trustworthy,
    /// <summary>Too few comparable live words to judge (accepted).</summary>
    NothingToCompare,
    /// <summary>Too few words could be matched (rejected).</summary>
    TooFewMatches,
    /// <summary>The median offset exceeds the limit (rejected).</summary>
    TimeOffset,
    /// <summary>The pre-recorded answer has far fewer words than the live transcript (<see cref="SelfCheckOptions.MinPrerecordedShare"/>; rejected).</summary>
    TooFewWords
}

/// <summary>The outcome of one self-check (numbers only, loggable).</summary>
/// <param name="Attempts">Recording attempts (archive files) checked one by one; 0 for a check of two plain word lists.</param>
/// <param name="FailedAttempt">The first attempt (file index) whose check rejected the result; null when none did.</param>
public sealed record SelfCheckResult(SelfCheckVerdict Verdict, int LiveWords, int PrerecordedWords, int Matches, int RequiredMatches, double? MedianOffset,
    int Attempts = 0, int? FailedAttempt = null)
{
    public bool IsTrustworthy => Verdict is SelfCheckVerdict.Trustworthy or SelfCheckVerdict.NothingToCompare;

    /// <summary>The permanent "result not trustworthy" failure for a rejected check; null when it passed.</summary>
    public CloudDiarizationException? ToException() => Verdict switch
    {
        SelfCheckVerdict.TooFewMatches => CloudDiarizationException.Untrustworthy(CloudFailureReason.TooFewMatches,
            string.Create(CultureInfo.InvariantCulture, $"{Matches} of {RequiredMatches} required words matched the live transcript{AttemptText}")),
        SelfCheckVerdict.TimeOffset => CloudDiarizationException.Untrustworthy(CloudFailureReason.TimeOffset,
            string.Create(CultureInfo.InvariantCulture, $"word times differ from the live transcript by {MedianOffset:0.00} s{AttemptText}")),
        SelfCheckVerdict.TooFewWords => CloudDiarizationException.Untrustworthy(CloudFailureReason.TooFewWords,
            string.Create(CultureInfo.InvariantCulture, $"{PrerecordedWords} words instead of about {LiveWords} in the live transcript{AttemptText}")),
        _ => null
    };

    private string AttemptText => FailedAttempt is int index && Attempts > 1
        ? string.Create(CultureInfo.InvariantCulture, $" (recording attempt {index + 1} of {Attempts})") : "";

    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"verdict={Verdict} live={LiveWords} prerecorded={PrerecordedWords} matches={Matches} required={RequiredMatches} medianOffset={(MedianOffset is double offset ? offset.ToString("0.000", CultureInfo.InvariantCulture) : "-")}s attempts={Attempts} failedAttempt={(FailedAttempt is int failed ? failed.ToString(CultureInfo.InvariantCulture) : "-")}");
}

/// <summary>A word reduced to its time (seconds on the common timeline) and normalised token.</summary>
public readonly record struct TimedToken(double Start, string Token);

/// <summary>
/// Plan step 3, self-check: do the pre-recorded words line up with the live words on the same timeline? Identical
/// normalised tokens are paired (each pair unique within <see cref="SelfCheckOptions.MatchWindow"/> on both sides), the
/// median of pre-recorded minus live start time is the offset. Too few pairs or an offset above the limit: the result is
/// not trustworthy (<see cref="CloudFailureKind.Untrustworthy"/>). Pure.
/// </summary>
public static class DeepgramSelfCheck
{
    /// <summary>Lower case, letters and digits only (punctuation, apostrophes and spaces removed), NFC.</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormC))
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }

    /// <summary>The check on two word lists on the same timeline (tokens are normalised here as well).</summary>
    public static SelfCheckResult Check(IReadOnlyList<TimedToken> live, IReadOnlyList<TimedToken> prerecorded, SelfCheckOptions? options = null)
    {
        options ??= new SelfCheckOptions();
        var liveWords = Comparable(live, options);
        var preWords = Comparable(prerecorded, options);
        // Only too few LIVE words make the check unable to judge; a short or empty cloud answer is judged (and rejected).
        if (liveWords.Count < options.MinComparableWords)
            return new SelfCheckResult(SelfCheckVerdict.NothingToCompare, liveWords.Count, preWords.Count, 0, 0, null);
        if (preWords.Count < options.MinPrerecordedShare * liveWords.Count - 1e-9)
            return new SelfCheckResult(SelfCheckVerdict.TooFewWords, liveWords.Count, preWords.Count, 0, 0, null);
        var comparable = Math.Min(liveWords.Count, preWords.Count);
        var required = Math.Max(Math.Min(options.MinMatches, (int)Math.Ceiling(options.SmallMeetingShare * comparable)),
            (int)Math.Ceiling(options.MinMatchShare * comparable));
        var liveIndex = Index(liveWords);
        var preIndex = Index(preWords);
        var offsets = new List<double>();
        foreach (var word in preWords)
        {
            if (Within(preIndex[word.Token], word.Start, options.MatchWindow) is not (_, 1)) continue;
            if (!liveIndex.TryGetValue(word.Token, out var candidates) || Within(candidates, word.Start, options.MatchWindow) is not (var partner, 1)) continue;
            offsets.Add(word.Start - partner);
        }
        if (offsets.Count < required)
            return new SelfCheckResult(SelfCheckVerdict.TooFewMatches, liveWords.Count, preWords.Count, offsets.Count, required, offsets.Count > 0 ? Median(offsets) : null);
        var median = Median(offsets);
        var verdict = Math.Abs(median) > options.MaxMedianOffset + 1e-9 ? SelfCheckVerdict.TimeOffset : SelfCheckVerdict.Trustworthy;
        return new SelfCheckResult(verdict, liveWords.Count, preWords.Count, offsets.Count, required, median);
    }

    /// <summary>
    /// The check for a finished run: the meeting's timed Others words (live) against the run's words (<see cref="DeepgramPrerecordedDiarizer.LastWords"/>),
    /// both in meeting time (archive frames mapped with the same time maps the fusion uses), judged per recording attempt
    /// (archive file): each attempt has its own time map and its own place on the uploaded timeline (behind a 1-s separator),
    /// so one broken attempt must not hide behind a well-aligned longer one. A live word belongs to the attempt whose file
    /// recorded its start time. The result is rejected when any attempt with enough comparable live words fails (the first
    /// such attempt is reported); otherwise it is trustworthy when at least one attempt could be judged.
    /// </summary>
    public static SelfCheckResult Check(Meeting meeting, DiarizationResult result, IReadOnlyList<ArchiveWord> words, SelfCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(words);
        var maps = DiarizationTimeline.TimeMaps(meeting, result.Files);
        var live = maps.Select(_ => new List<TimedToken>()).ToList();
        foreach (var segment in meeting.Transcript.Where(segment => segment.Speaker == Speaker.Others && segment.Words is not null))
        {
            foreach (var word in segment.Words!.Where(word => double.IsFinite(word.Start)))
            {
                var time = segment.Offset + word.Start;
                var file = maps.FindIndex(map => map.FrameForOffset(time) is not null);
                if (file >= 0) live[file].Add(new TimedToken(time, word.Text));
            }
        }
        var prerecorded = maps.Select(_ => new List<TimedToken>()).ToList();
        foreach (var word in words.Where(word => word.File >= 0 && word.File < maps.Count))
            prerecorded[word.File].Add(new TimedToken(maps[word.File].OffsetForFrame(word.StartFrame), word.Token));
        var results = Enumerable.Range(0, maps.Count).Select(file => Check(live[file], prerecorded[file], options)).ToList();
        var attempts = results.Count;
        var failed = results.FindIndex(check => !check.IsTrustworthy);
        if (failed >= 0) return results[failed] with { Attempts = attempts, FailedAttempt = failed };
        var judged = results.Where(check => check.Verdict == SelfCheckVerdict.Trustworthy).ToList();
        var offsets = judged.Where(check => check.MedianOffset is not null).Select(check => check.MedianOffset!.Value).ToList();
        return new SelfCheckResult(judged.Count > 0 ? SelfCheckVerdict.Trustworthy : SelfCheckVerdict.NothingToCompare, results.Sum(check => check.LiveWords),
            results.Sum(check => check.PrerecordedWords), results.Sum(check => check.Matches), results.Sum(check => check.RequiredMatches),
            offsets.Count > 0 ? offsets.MaxBy(Math.Abs) : null, attempts);
    }

    private static List<TimedToken> Comparable(IReadOnlyList<TimedToken> words, SelfCheckOptions options) =>
        words.Where(word => double.IsFinite(word.Start)).Select(word => word with { Token = Normalize(word.Token) })
            .Where(word => word.Token.Length >= options.MinTokenLength).ToList();

    private static Dictionary<string, List<double>> Index(List<TimedToken> words)
    {
        var index = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (var word in words) (index.TryGetValue(word.Token, out var list) ? list : index[word.Token] = []).Add(word.Start);
        foreach (var list in index.Values) list.Sort();
        return index;
    }

    /// <summary>The first start within ±window of <paramref name="time"/> and how many there are (sorted list).</summary>
    private static (double First, int Count) Within(List<double> sorted, double time, double window)
    {
        var low = sorted.BinarySearch(time - window - 1e-9);
        if (low < 0) low = ~low;
        var count = 0;
        var first = double.NaN;
        for (var index = low; index < sorted.Count && sorted[index] <= time + window + 1e-9; index++)
        {
            if (count == 0) first = sorted[index];
            count++;
        }
        return (first, count);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
