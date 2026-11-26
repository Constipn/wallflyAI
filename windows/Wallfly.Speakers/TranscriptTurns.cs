using Wallfly.Core;

namespace Wallfly.Speakers;

/// <summary>
/// The transcript view over word-level speaker labels with the constants of a <see cref="TeamsAttributionPolicy"/>. The
/// grouping itself lives in <see cref="TranscriptDisplay"/> (Wallfly.Core), so the app, the speaker editor and this
/// project share one implementation; <see cref="TranscriptTurn"/>, <see cref="TranscriptReference"/>,
/// <see cref="TranscriptTurnId"/> and <see cref="WordRange"/> are Core types.
/// </summary>
public static class TranscriptTurns
{
    /// <summary>Runs: per segment, consecutive words with the same participant (Swift <c>transcriptRuns</c>).</summary>
    public static List<TranscriptTurn> Runs(Meeting meeting, TeamsAttributionPolicy? policy = null) =>
        TranscriptDisplay.Runs(meeting, Options(policy));

    /// <summary>Consecutive runs of the same channel and participant, joined into turns (Swift <c>conversation</c>).</summary>
    public static List<TranscriptTurn> Conversation(Meeting meeting, TeamsAttributionPolicy? policy = null) =>
        TranscriptDisplay.Conversation(meeting, Options(policy));

    /// <summary><c>[mm:ss] Name: text</c> per run, joined by newlines (Swift <c>transcriptText</c>).</summary>
    public static string TranscriptText(Meeting meeting) => TranscriptDisplay.TranscriptText(meeting);

    /// <summary><c>[mm:ss] Name: text</c> per turn, joined by blank lines (Swift <c>conversationText</c>).</summary>
    public static string ConversationText(Meeting meeting) => TranscriptDisplay.ConversationText(meeting);

    /// <summary>The display constants of <paramref name="policy"/> (null: the macOS defaults).</summary>
    public static TranscriptDisplayOptions Options(TeamsAttributionPolicy? policy) =>
        policy is null ? TranscriptDisplayOptions.Default : new(policy.UnitGap, policy.BackchannelDuration, policy.BackchannelWords);
}
