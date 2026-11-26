using System.Globalization;
using Wallfly.Core;

namespace Wallfly.Speakers;

/// <summary>
/// Manual speaker corrections (spec 5.5; ports of <c>Meeting.assign</c> from SpeakerAttribution.swift and the pure part of
/// <c>MeetingStore.editParticipant</c>). Store-level rules (busy, selected meeting, save-or-rollback) stay with the caller:
/// edit a copy of the meeting and publish it only after it was saved.
/// </summary>
public static class SpeakerEditing
{
    /// <summary>Longest accepted participant name, in user-perceived characters.</summary>
    public const int MaxNameLength = 160;

    /// <summary>
    /// "Assign this segment": sets <paramref name="participantID"/> (null = Others, on the Me channel: Me) with
    /// <c>Attribution = Manual</c> on exactly the referenced remote words (or whole untimed remote segments) and on referenced
    /// Me words (the microphone pass can name other voices there; untimed Me segments are never named). Manual words are
    /// protected against every later attribution and fusion pass. Ignored when the participant is unknown; invalid
    /// references are skipped.
    /// </summary>
    public static void Assign(this Meeting meeting, IEnumerable<TranscriptReference> references, Guid? participantID)
    {
        if (participantID is not null && meeting.Participants?.Any(person => person.Id == participantID) != true) return;
        foreach (var reference in references)
        {
            var segment = meeting.Transcript.FirstOrDefault(item => item.Id == reference.SegmentID);
            if (segment is null || (segment.Speaker != Speaker.Others && reference.Words is null)) continue;
            if (reference.Words is { } range && segment.Words is { } words)
            {
                if (range.Start < 0 || range.End > words.Count) continue;
                for (var index = range.Start; index < range.End; index++)
                {
                    words[index].ParticipantID = participantID;
                    words[index].Attribution = WordAttribution.Manual;
                }
            }
            else
            {
                segment.ParticipantID = participantID;
                segment.Attribution = WordAttribution.Manual;
            }
        }
    }

    /// <summary>
    /// The edit behind "Rename speaker…" and "Name this passage…". With <paramref name="participantID"/>: renames that
    /// participant in the whole meeting; a synthetic "Speaker k" is instead bound to the name manually
    /// (<see cref="AcousticSpeakerLabels.BindAcousticSpeaker"/>), also for later diarization runs. Without it: creates a
    /// new participant (next colour) and assigns exactly the <paramref name="references"/> to it manually.
    /// Returns false and changes nothing for a blank or too long name, an unknown participant or invalid references.
    /// </summary>
    public static bool EditParticipant(this Meeting meeting, Guid? participantID, string name, IReadOnlyList<TranscriptReference>? references = null)
    {
        if (!TryNormalizeName(name, out var trimmed)) return false;
        if (participantID is Guid id)
        {
            var participant = meeting.Participants?.FirstOrDefault(person => person.Id == id);
            if (participant is null) return false;
            // Naming a "Speaker k" binds its voice to that person, also for later runs.
            if (AcousticSpeakerLabels.AcousticSpeakerId(participant) is not null) return meeting.BindAcousticSpeaker(id, trimmed);
            participant.Name = trimmed;
            return true;
        }
        if (references is not { Count: > 0 } || !references.All(reference => IsValid(meeting, reference))) return false;
        var people = meeting.Participants ??= [];
        var created = new MeetingParticipant { Name = trimmed, ColorIndex = ParticipantPalette.NextColorIndex(people) };
        people.Add(created);
        meeting.Assign(references, created.Id);
        return true;
    }

    /// <summary>Trims the name and checks it is not blank and at most <see cref="MaxNameLength"/> characters long.</summary>
    public static bool TryNormalizeName(string? name, out string trimmed)
    {
        trimmed = (name ?? "").Trim();
        return trimmed.Length > 0 && new StringInfo(trimmed).LengthInTextElements <= MaxNameLength;
    }

    /// <summary>
    /// A reference to a non-empty word range of a segment (remote or Me), or to a whole remote segment without words.
    /// </summary>
    public static bool IsValid(Meeting meeting, TranscriptReference reference)
    {
        var segment = meeting.Transcript.FirstOrDefault(item => item.Id == reference.SegmentID
            && (item.Speaker == Speaker.Others || reference.Words is not null));
        if (segment is null) return false;
        if (reference.Words is { } range) return range.Start >= 0 && range.End <= (segment.Words?.Count ?? 0) && !range.IsEmpty;
        return segment.Words is null || segment.Words.Count == 0;
    }
}
