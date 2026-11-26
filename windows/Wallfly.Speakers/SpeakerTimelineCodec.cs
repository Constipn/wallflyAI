using System.Text.Json;
using Wallfly.Core;
using Wallfly.Core.Json;

namespace Wallfly.Speakers;

/// <summary>
/// The persisted Teams evidence (<c>Meeting.SpeakerTimeline</c>, macOS <c>speakerTimeline</c>): compressed intervals,
/// about 30 KB per hour, from which every attribution decision can be replayed offline.
/// </summary>
public static class SpeakerTimelineCodec
{
    private static readonly JsonSerializerOptions Compact = new(WallflyJson.Options) { WriteIndented = false };

    /// <summary>The session's evidence as intervals in meeting offsets (rounded to ms), up to <paramref name="until"/>.</summary>
    public static List<SpeakerTimelineEntry> Encode(TeamsAttributionSession session, double? until = null) =>
        session.ExportTimeline(until);

    /// <summary>A replay session for <paramref name="sessionId"/>. The meeting timeline of all sessions may be passed as a whole.</summary>
    public static TeamsAttributionSession Decode(Guid sessionId, IEnumerable<SpeakerTimelineEntry> timeline, TeamsAttributionPolicy? policy = null) =>
        TeamsAttributionSession.FromTimeline(sessionId, timeline, policy);

    /// <summary>
    /// What the macOS stop does: appends the session's entries to the meeting timeline (sessions occupy disjoint
    /// offset ranges). Nothing is stored for a session without evidence.
    /// </summary>
    public static void AppendTo(Meeting meeting, TeamsAttributionSession session, double until)
    {
        var entries = session.ExportTimeline(until);
        if (entries.Count == 0) return;
        (meeting.SpeakerTimeline ??= []).AddRange(entries);
    }

    /// <summary>One replay session per remote recording session of the meeting (offline evaluation, Swift <c>SpeakerEval</c>).</summary>
    public static Dictionary<Guid, TeamsAttributionSession> ReplaySessions(Meeting meeting, TeamsAttributionPolicy? policy = null)
    {
        var timeline = meeting.SpeakerTimeline ?? [];
        return meeting.Transcript.Where(segment => segment.Speaker == Speaker.Others && segment.SessionID is not null)
            .Select(segment => segment.SessionID!.Value).Distinct()
            .ToDictionary(id => id, id => TeamsAttributionSession.FromTimeline(id, timeline, policy));
    }

    /// <summary>Compact JSON in the macOS shape (<c>kind</c>, <c>start</c>, <c>end</c>, <c>participantID</c>, <c>barrier</c>).</summary>
    public static string Serialize(IEnumerable<SpeakerTimelineEntry> timeline) => JsonSerializer.Serialize(timeline.ToList(), Compact);

    public static List<SpeakerTimelineEntry> Deserialize(string json) =>
        JsonSerializer.Deserialize<List<SpeakerTimelineEntry>>(json, WallflyJson.Options) ?? [];
}
