namespace Wallfly.Speakers;

/// <summary>
/// Constants of the Teams speaker attribution (port of <c>TeamsAttributionPolicy</c>, SpeakerAttribution.swift).
/// <c>new()</c> yields exactly the macOS defaults; <see cref="Windows"/> differs only in <see cref="DetectionLag"/>.
/// </summary>
public sealed record TeamsAttributionPolicy
{
    /// <summary>macOS: the Teams speaking indicator is backdated by 0.5 s (not calibrated).</summary>
    public const double MacDetectionLag = 0.5;
    /// <summary>Windows start value, uncalibrated; verify in a live call.</summary>
    public const double WindowsTeamsDetectionLag = 0.9;

    /// <summary>The policy for Teams on Windows (detection lag 0.9 s, everything else as on macOS).</summary>
    public static TeamsAttributionPolicy Windows { get; } = new() { DetectionLag = WindowsTeamsDetectionLag };

    /// <summary>How long the speaking indicator lags the audio; observations are backdated by it (platform-specific).</summary>
    public double DetectionLag { get; init; } = MacDetectionLag;
    /// <summary>One observation counts as evidence for at most this long.</summary>
    public double EvidenceLifetime { get; init; } = 1;
    /// <summary>Raw per-word label: one participant must cover at least this share of the word.</summary>
    public double MinimumWordCoverage { get; init; } = 0.75;
    /// <summary>A healthy (no-speaker) stretch up to this long between two observations of the same speaker belongs to them.</summary>
    public double MaximumHealthyGap { get; init; } = 0.5;
    /// <summary>Live: only utterances ending in the last this-many seconds of evidence are relabelled.</summary>
    public double RevisionWindow { get; init; } = 15;
    /// <summary>A word gap at least this long ends an utterance.</summary>
    public double UnitGap { get; init; } = 0.7;
    /// <summary>UI evidence this long before an utterance still counts for its vote (at <see cref="OutsideWeight"/>).</summary>
    public double VoteLead { get; init; } = 0.5;
    /// <summary>UI evidence this long after an utterance still counts for its vote (at <see cref="OutsideWeight"/>).</summary>
    public double VoteTrail { get; init; } = 0.3;
    /// <summary>Weight of UI evidence just outside an utterance relative to evidence inside it.</summary>
    public double OutsideWeight { get; init; } = 0.25;
    /// <summary>Shorter UI speaking intervals are indicator noise and never vote.</summary>
    public double MinUIInterval { get; init; } = 0.2;
    /// <summary>The winner must be shown as speaking for at least this share of the utterance.</summary>
    public double MinUnitCoverage { get; init; } = 0.4;
    /// <summary>A runner-up with at least this share of the winner's score makes the vote close.</summary>
    public double CloseVoteRatio { get; init; } = 0.7;
    /// <summary>Close votes on utterances up to this long go to the interval that started nearest the utterance.</summary>
    public double ShortUnitDuration { get; init; } = 1.2;
    /// <summary>A named run must last this long to split an utterance into a new turn.</summary>
    public double MinTurnDuration { get; init; } = 1.5;
    /// <summary>A run must contain this many words to split an utterance into a new turn.</summary>
    public int MinTurnWords { get; init; } = 3;
    /// <summary>An unnamed run must last this long to split an utterance (shorter UI gaps are bridged).</summary>
    public double MinUnknownRun { get; init; } = 2.5;
    /// <summary>A close vote keeps the previous owner if the utterance starts within this time after them.</summary>
    public double StickyGap { get; init; } = 2;
    /// <summary>How far before the revision window a reconciliation reads words and UI evidence.</summary>
    public double UnitLookback { get; init; } = 30;
    /// <summary>Final pass: an identity with less automatic speech than this is treated as indicator flicker.</summary>
    public double MinIdentitySpeech { get; init; } = 10;
    /// <summary>Final pass: identities below this speech time are checked for a jittery pattern.</summary>
    public double JitterSpeechCeiling { get; init; } = 30;
    /// <summary>Final pass: a jittery identity appears in at least this many separate runs.</summary>
    public int JitterMinRuns { get; init; } = 20;
    /// <summary>Final pass: a jittery identity never holds a run longer than this.</summary>
    public double JitterMaxLongestRun { get; init; } = 3;
    /// <summary>Final pass: a jittery identity wins its votes with less than this mean share.</summary>
    public double JitterMaxMeanShare { get; init; } = 0.6;
    /// <summary>Display: a contribution up to this long inside the other channel's block is a backchannel.</summary>
    public double BackchannelDuration { get; init; } = 1.2;
    /// <summary>Display: a backchannel has at most this many words.</summary>
    public int BackchannelWords { get; init; } = 3;
}
