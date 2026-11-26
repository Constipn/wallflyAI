using Wallfly.Core;
using Wallfly.Core.Json;
using Wallfly.Core.Services;
using Wallfly.Core.Text;
using Wallfly.Speakers;

namespace Wallfly.Integration;

/// <summary>
/// Manual speaker corrections (spec 5.5; macOS <c>editParticipant</c>, <c>assignTranscript</c>,
/// <c>saveParticipantEdit</c>): every edit is applied to a copy of the meeting, written immediately through the meeting
/// writer and only then published into the live meeting; a failed save changes nothing and shows the reason. Manual words
/// are protected from every later attribution and fusion pass. The meeting that is recording cannot be edited (its
/// transcript is live), nor can anything be edited while the library moves or the app quits.
/// </summary>
public sealed class SpeakerEditingService : ISpeakerEditor, IMeetingHostClient
{
    private IMeetingHost? host;

    public void Attach(IMeetingHost host) => this.host = host;

    public bool CanEdit(Guid meetingID) =>
        host is { } current && !current.IsClosing && !current.IsMovingLibrary && current.RecordingMeetingID != meetingID
        && current.Find(meetingID) is not null;

    public bool RenameParticipant(Guid meetingID, Guid participantID, string name) =>
        Edit(meetingID, (meeting, map) => meeting.EditParticipant(map(participantID), name));

    public bool AssignTranscript(Guid meetingID, IReadOnlyList<TranscriptReference> references, Guid? participantID) =>
        Edit(meetingID, (meeting, map) =>
        {
            if (references.Count == 0 || !references.All(reference => SpeakerEditing.IsValid(meeting, reference))) return false;
            var target = participantID is Guid chosen ? map(chosen) : (Guid?)null;
            if (target is Guid id && meeting.Participants?.Any(person => person.Id == id) != true) return false;
            meeting.Assign(references, target);
            return true;
        });

    public bool AddParticipant(Guid meetingID, string name, IReadOnlyList<TranscriptReference> references) =>
        Edit(meetingID, (meeting, _) => meeting.EditParticipant(null, name, references));

    /// <summary>
    /// Applies <paramref name="change"/> to a copy, after merging the duplicate Teams participants of older Windows
    /// recordings (<see cref="TeamsParticipantMerge"/>; the map turns a removed duplicate's id into the kept one), so a
    /// rename reaches the whole person. Saved together with the edit, or not at all.
    /// </summary>
    private bool Edit(Guid meetingID, Func<Meeting, Func<Guid, Guid>, bool> change)
    {
        if (!CanEdit(meetingID) || host is not { } current || current.Find(meetingID) is not { } live) return false;
        current.Flush();
        var edited = WallflyJson.Copy(live);
        var replaced = new Dictionary<Guid, Guid>();
        TeamsParticipantMerge.Merge(edited, replaced);
        return change(edited, id => replaced.TryGetValue(id, out var kept) ? kept : id) && current.SaveEdited(live, edited, CoreMessages.SpeakerEditNotSaved);
    }
}
