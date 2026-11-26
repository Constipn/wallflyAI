using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wallfly.Ai;
using Wallfly.Core.Json;
using Wallfly.Core.Text;

namespace Wallfly.Core;

/// <summary>
/// One meeting file. The field set and the JSON shape follow the macOS 1.13.0 <c>Meeting</c> struct
/// (camelCase, optional fields omitted when null, Apple reference dates, uppercase UUIDs); Windows
/// adds <see cref="SchemaVersion"/>, <see cref="ProcessingErrors"/>, <see cref="RecordingSessions"/> and <see cref="Chat"/>. Unknown fields of other
/// writers survive a load/save cycle through <see cref="AdditionalData"/>.
/// </summary>
public sealed class Meeting : Observable
{
    public const int CurrentSchemaVersion = 2;

    private string title = "New note";
    private string notes = "";
    private string enhancedNotes = "";
    private DateTimeOffset? startedAt;
    private bool? audioTranscriptionComplete;

    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Windows addition: 2 is the first Wallfly-for-Windows shape. macOS files have none.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Title { get => title; set => Set(ref title, value); }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get => startedAt; set { if (Set(ref startedAt, value)) Changed(nameof(DateLabel)); } }
    public double Duration { get; set; }
    public string Notes { get => notes; set => Set(ref notes, value); }
    public string EnhancedNotes { get => enhancedNotes; set { if (Set(ref enhancedNotes, value)) Changed(nameof(DisplayedEnhancedNotes)); } }
    public string EnhancementEngine { get; set; } = "";
    public List<TranscriptSegment> Transcript { get; set; } = [];

    /// <summary>Written relative to the library root (<c>Audio\&lt;MeetingID&gt;\…</c>); absolute values are accepted on read.</summary>
    [JsonConverter(typeof(AudioPathListConverter))]
    public List<string> AudioFiles { get; set; } = [];
    /// <summary>Only recordings explicitly enrolled in automatic cleanup; legacy recordings stay untouched.</summary>
    [JsonConverter(typeof(NullableAudioPathListConverter))]
    public List<string>? AudioDeletionPending { get; set; }
    /// <summary>null = never recorded, false = a session is running or ended incomplete, true = finished cleanly.</summary>
    public bool? AudioTranscriptionComplete { get => audioTranscriptionComplete; set { if (Set(ref audioTranscriptionComplete, value)) Changed(nameof(TranscriptIncomplete)); } }
    public bool? AudioRecoveryNeeded { get; set; }
    public bool? AudioDeletionReady { get; set; }
    public string? AudioDeletionError { get; set; }
    public List<RecordingGap>? RecordingGaps { get; set; }
    public List<AudioAnchor>? AudioAnchors { get; set; }
    public List<TranscriptionGap>? TranscriptionGaps { get; set; }
    public List<MeetingParticipant>? Participants { get; set; }
    public ObservableCollection<MeetingTask>? Tasks { get; set; }
    public TaskExtractionState? TaskExtraction { get; set; }
    public List<SpeakerTimelineEntry>? SpeakerTimeline { get; set; }
    public List<AcousticSpeaker>? AcousticSpeakers { get; set; }
    public DiarizationState? Diarization { get; set; }
    public TranscriptMirrorInfo? TranscriptMirror { get; set; }
    public PersonSummaryInfo? PersonSummary { get; set; }
    public string? PersonSummaryError { get; set; }
    /// <summary>Windows addition: the user-visible reasons why a session did not finish cleanly.</summary>
    public List<string> ProcessingErrors { get; set; } = [];
    /// <summary>
    /// Windows addition: one entry per recording attempt (audio source, devices, transcription, source switches); absent
    /// until Wallfly for Windows records, so a Mac-written meeting gains no key on save.
    /// </summary>
    public List<RecordingSessionInfo>? RecordingSessions { get; set; }
    /// <summary>
    /// Windows addition: the questions and answers of the meeting chat, oldest first; absent until the first answer, so a
    /// Mac-written meeting gains no key on save.
    /// </summary>
    public List<ChatMessage>? Chat { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    /// <summary>Convenience view of <see cref="AudioTranscriptionComplete"/>; a fresh note is never incomplete.</summary>
    [JsonIgnore]
    public bool TranscriptIncomplete
    {
        get => AudioTranscriptionComplete == false;
        set => AudioTranscriptionComplete = !value;
    }

    [JsonIgnore] public bool NeedsSuggestedTitle => NotesEnhancer.IsPlaceholderTitle(Title);
    [JsonIgnore] public string DateLabel => (StartedAt ?? CreatedAt).ToLocalTime().ToString("dd.MM.yyyy · HH:mm");
    /// <summary>Older notes may still carry [mm:ss] citations; they are hidden in views, search and export.</summary>
    [JsonIgnore] public string DisplayedEnhancedNotes => TimestampCitations.Strip(EnhancedNotes);
    [JsonIgnore] public IEnumerable<TranscriptSegment> OrderedTranscript =>
        Transcript.OrderBy(segment => segment.Offset).ThenBy(segment => segment.Id.ToString("D"), StringComparer.Ordinal);
    /// <summary>
    /// <c>[mm:ss] Name: Text</c> per segment: the transcript the notes enhancer and the task extractor read. Like macOS, a
    /// segment carries its channel's label (<see cref="SpeakerName(TranscriptSegment)"/>). Windows addition: a Me segment
    /// whose timed words belong for more than half to one other voice of the microphone pass ("Speaker k", or a manual
    /// assignment) is that voice's line, so the AI never credits another person in the room to the user.
    /// </summary>
    [JsonIgnore] public string TranscriptText =>
        string.Join('\n', OrderedTranscript.Select(segment => $"[{Timestamp(segment.Offset)}] {PromptSpeakerName(segment)}: {segment.Text}"));

    /// <summary>The label of a segment in <see cref="TranscriptText"/> (see there).</summary>
    private string PromptSpeakerName(TranscriptSegment segment)
    {
        if (segment.Speaker != Speaker.Me || segment.Words is not { Count: > 0 } words || Participants is not { Count: > 0 } participants)
            return SpeakerName(segment);
        var timed = words.Where(word => !string.IsNullOrWhiteSpace(word.Text)).ToList();
        var voice = timed
            .Where(word => TranscriptDisplay.NamesMeWord(word) && word.ParticipantID is not null)
            .GroupBy(word => word.ParticipantID!.Value)
            .Select(group => (Id: group.Key, Count: group.Count()))
            .OrderByDescending(group => group.Count)
            .FirstOrDefault();
        return voice.Count * 2 > timed.Count && participants.FirstOrDefault(person => person.Id == voice.Id)?.Name is { } name
            ? name
            : SpeakerName(segment);
    }

    /// <summary>Consecutive passages of the same channel and participant, joined into turns.</summary>
    [JsonIgnore]
    public IEnumerable<(Speaker Speaker, Guid? ParticipantID, double Offset, string Text)> Conversation
    {
        get
        {
            var turns = new List<(Speaker Speaker, Guid? ParticipantID, double Offset, string Text)>();
            foreach (var segment in OrderedTranscript)
            {
                if (turns.Count > 0 && turns[^1].Speaker == segment.Speaker && turns[^1].ParticipantID == segment.ParticipantID)
                    turns[^1] = turns[^1] with { Text = turns[^1].Text + " " + segment.Text };
                else turns.Add((segment.Speaker, segment.ParticipantID, segment.Offset, segment.Text));
            }
            return turns;
        }
    }

    [JsonIgnore] public string ConversationText =>
        string.Join("\n\n", Conversation.Select(turn => $"[{Timestamp(turn.Offset)}] {SpeakerName(turn.Speaker, turn.ParticipantID)}: {turn.Text}"));

    public string SpeakerName(TranscriptSegment segment) => SpeakerName(segment.Speaker, segment.ParticipantID);

    /// <summary>
    /// The segment-level label (macOS <c>speakerName</c>): "Me" for the Me channel whatever participant a segment carries,
    /// else the participant's name or "Others". Word-level labels of the display (a microphone voice "Speaker k" on Me
    /// words) are <see cref="TranscriptDisplay.SpeakerName"/>.
    /// </summary>
    public string SpeakerName(Speaker speaker, Guid? participantID) =>
        speaker == Speaker.Me ? "Me" : Participants?.FirstOrDefault(person => person.Id == participantID)?.Name ?? "Others";

    public static string Timestamp(double value)
    {
        var seconds = Math.Max(0, (int)value);
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    /// <summary>
    /// Inserts or replaces a passage (port of the macOS <c>MeetingStore.merge</c>, spec 4.4). A preview never
    /// replaces a final of the same id; an empty text is ignored, except that an empty final removes the
    /// preview of its id. A passage replaces passages of the same channel with the same id, previews
    /// within 5 ms of its offset and overlapping previews; passages of another streaming connection or
    /// session are left alone, and simultaneous speech on the other channel is always kept.
    /// </summary>
    public void Upsert(TranscriptSegment segment)
    {
        // Final results may still be revised by another final, but an out-of-order preview must never
        // downgrade already finalized text.
        if (!segment.IsFinal && Transcript.Any(old => old.Id == segment.Id && old.Speaker == segment.Speaker && old.IsFinal)) return;
        if (string.IsNullOrWhiteSpace(segment.Text))
        {
            // A final empty recognition rejects an earlier provisional guess (e.g. music).
            if (segment.IsFinal && Transcript.RemoveAll(old => old.Id == segment.Id && !old.IsFinal) > 0)
            { Changed(nameof(TranscriptText)); Changed(nameof(ConversationText)); }
            return;
        }
        // A recognizer can resize its volatile range before finalizing. Replace only overlapping
        // provisional text on the same source; preserve simultaneous speech on the other source.
        var end = segment.Offset + Math.Max(.01, segment.Duration);
        Transcript.RemoveAll(old =>
        {
            if (old.Speaker != segment.Speaker) return false;
            if ((segment.ConnectionID != null || old.ConnectionID != null)
                && (old.ConnectionID != segment.ConnectionID || old.SessionID != segment.SessionID)) return false;
            if (old.Id == segment.Id) return true;
            if (Math.Abs(old.Offset - segment.Offset) < .005) return !old.IsFinal;
            return !old.IsFinal && old.Offset < end && old.Offset + Math.Max(.01, old.Duration) > segment.Offset;
        });
        Transcript.Add(segment);
        Changed(nameof(TranscriptText)); Changed(nameof(ConversationText));
    }

    /// <summary>
    /// Publishes the speaker state of <paramref name="edited"/> (a saved, edited copy of this meeting) into this live
    /// instance: transcript, participants, acoustic speakers, diarization run and Teams timeline. Used after a speaker
    /// correction or a diarization run was written, so the UI never shows an unsaved edit.
    /// </summary>
    public void AdoptSpeakerState(Meeting edited)
    {
        ArgumentNullException.ThrowIfNull(edited);
        if (edited.Id != Id) throw new ArgumentException("The edited copy belongs to another meeting.", nameof(edited));
        Transcript = edited.Transcript;
        Participants = edited.Participants;
        AcousticSpeakers = edited.AcousticSpeakers;
        Diarization = edited.Diarization;
        SpeakerTimeline = edited.SpeakerTimeline;
        Changed(nameof(Transcript)); Changed(nameof(Participants)); Changed(nameof(AcousticSpeakers)); Changed(nameof(Diarization));
        Changed(nameof(TranscriptText)); Changed(nameof(ConversationText));
    }

    /// <summary>Replaces the segment at <paramref name="index"/> with a relabelled copy (live Teams attribution).</summary>
    public void ReviseSegment(int index, TranscriptSegment segment) => ReviseSegments([(index, segment)]);

    /// <summary>
    /// Replaces relabelled copies at their indexes (one attribution pass), raising one change notification. A revision
    /// whose index no longer holds the same segment id is skipped. Returns the number applied.
    /// </summary>
    public int ReviseSegments(IReadOnlyList<(int Index, TranscriptSegment Segment)> revisions)
    {
        var applied = 0;
        foreach (var (index, segment) in revisions)
        {
            if (index < 0 || index >= Transcript.Count || Transcript[index].Id != segment.Id) continue;
            Transcript[index] = segment;
            applied++;
        }
        if (applied > 0) { Changed(nameof(TranscriptText)); Changed(nameof(ConversationText)); }
        return applied;
    }

    public void MergeTasks(IEnumerable<TaskDraft> drafts)
    {
        Tasks ??= [];
        foreach (var draft in drafts)
        {
            if (string.IsNullOrWhiteSpace(draft.Text)) continue;
            if (draft.ExistingTaskId is Guid id && Tasks.Any(task => task.Id == id)) continue;
            if (Tasks.Any(task => MeetingTask.Key(task.Text) == MeetingTask.Key(draft.Text) && MeetingTask.Key(task.Owner) == MeetingTask.Key(draft.Owner))) continue;
            Tasks.Add(new() { Text = draft.Text.Trim(), Owner = draft.Owner });
        }
        Changed(nameof(Tasks));
    }

    /// <summary>Overlapping reasons for the same source describe one missing interval (port of macOS behaviour).</summary>
    public void AppendRecordingGap(RecordingGap gap)
    {
        var gaps = RecordingGaps ??= [];
        var index = gaps.FindLastIndex(existing => existing.Source == gap.Source && existing.Reason == gap.Reason);
        if (index >= 0 && gap.Start >= gaps[index].Start && gap.Start <= gaps[index].Start + gaps[index].Duration + .011)
            gaps[index].Duration = Math.Max(gaps[index].Start + gaps[index].Duration, gap.Start + gap.Duration) - gaps[index].Start;
        else gaps.Add(gap);
    }

    /// <summary>
    /// A gap that starts inside (or within 1 ms after) the last gap of the same channel and reason extends
    /// it; port of the macOS <c>Meeting.appendTranscriptionGap</c>. Streaming pipelines report one gap per
    /// audio block while paused, so this keeps the stored list short.
    /// </summary>
    public void AppendTranscriptionGap(TranscriptionGap gap)
    {
        var gaps = TranscriptionGaps ??= [];
        var index = gaps.FindLastIndex(existing => existing.Speaker == gap.Speaker && existing.Reason == gap.Reason
            && gap.Start >= existing.Start && gap.Start <= existing.Start + existing.Duration + .001);
        if (index >= 0)
            gaps[index].Duration = Math.Max(gaps[index].Start + gaps[index].Duration, gap.Start + gap.Duration) - gaps[index].Start;
        else gaps.Add(gap);
    }

    /// <summary>Total missing capture time; overlapping gaps of one source count once.</summary>
    [JsonIgnore]
    public double RecordingGapDuration => Enum.GetValues<CaptureSource>().Sum(source =>
    {
        double end = 0, duration = 0;
        foreach (var gap in (RecordingGaps ?? []).Where(gap => gap.Source == source).OrderBy(gap => gap.Start))
        {
            duration += Math.Max(0, gap.Start + gap.Duration - Math.Max(end, gap.Start));
            end = Math.Max(end, gap.Start + gap.Duration);
        }
        return duration;
    });
}
