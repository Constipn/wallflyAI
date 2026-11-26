using Wallfly.Core;

namespace Wallfly.App.ViewModels;

/// <summary>One displayed turn of the chat view (macOS <c>TranscriptTurn</c>).</summary>
public sealed class ChatTurn
{
    public required string Key { get; init; }
    public required Speaker Speaker { get; init; }
    public required double Offset { get; init; }
    public required string Text { get; set; }
    public required bool IsFinal { get; set; }
    /// <summary>Null for the Me channel unless the microphone pass named another voice on it.</summary>
    public Guid? ParticipantID { get; init; }
    /// <summary>The exact source words of the turn: what "Assign this segment…" hands to Core's speaker editor.</summary>
    public required IReadOnlyList<TranscriptReference> References { get; init; }
}

/// <summary>
/// The chat view's turns (spec 4.4). The grouping itself is Core's <see cref="TranscriptDisplay"/> (utterance blocks with
/// gaps below 0.7 s, short backchannels after the other channel's block, consecutive blocks of one speaker as one turn);
/// this class only maps its turns to view keys and references.
/// </summary>
public static class TranscriptGrouping
{
    /// <summary>Equivalent to a non-empty transcript text without building it (macOS <c>hasTranscriptText</c>).</summary>
    public static bool HasTranscriptText(Meeting meeting) => meeting.Transcript.Any(segment =>
        segment.Words is { Count: > 0 } words ? words.Any(word => word.Text.Trim().Length > 0) : segment.Text.Trim().Length > 0);

    /// <summary>Per segment, consecutive words with the same participant, ordered by utterance block.</summary>
    public static List<ChatTurn> Runs(Meeting meeting) => TranscriptDisplay.Runs(meeting).Select(ToChat).ToList();

    /// <summary>Consecutive runs of the same channel and participant, joined into turns (macOS <c>conversation</c>).</summary>
    public static List<ChatTurn> Conversation(Meeting meeting) => TranscriptDisplay.Conversation(meeting).Select(ToChat).ToList();

    /// <summary>The "Copy" button's text (macOS <c>copyableTranscript</c>): turns, interim markers, then the gaps.</summary>
    public static string CopyableTranscript(Meeting meeting)
    {
        var text = string.Join("\n\n", Conversation(meeting).Select(turn =>
        {
            // A Me turn carries a participant only when the microphone pass named another voice on it.
            var name = meeting.Participants?.FirstOrDefault(person => turn.ParticipantID is not null && person.Id == turn.ParticipantID)?.Name
                ?? (turn.Speaker == Speaker.Me ? Strings.SpeakerMe : Strings.SpeakerOthers);
            return $"[{Meeting.Timestamp(turn.Offset)}] {name}: {turn.Text}" + (turn.IsFinal ? "" : Strings.InterimMarker);
        }));
        var gaps = (meeting.TranscriptionGaps ?? []).Select(gap => $"[{Meeting.Timestamp(gap.Start)}] " + Strings.Format(Strings.TranscriptGapCopy, gap.Reason));
        return string.Join("\n\n", new[] { text }.Concat(gaps).Where(part => part.Length > 0));
    }

    /// <summary>The view key is the turn's first word (segment ID plus word index) or its untimed segment; stable while the turn grows.</summary>
    private static ChatTurn ToChat(TranscriptTurn turn) => new()
    {
        Key = turn.References[0].Words is { } first ? turn.Id.SegmentID.ToString("N") + ":" + first.Start : turn.Id.SegmentID.ToString("N"),
        Speaker = turn.Speaker,
        Offset = turn.Offset,
        Text = turn.Text,
        IsFinal = turn.IsFinal,
        ParticipantID = turn.ParticipantID,
        References = turn.References.ToList()
    };
}
