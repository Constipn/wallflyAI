using System.Collections.Concurrent;
using System.Diagnostics;
using Wallfly.Core;
using Wallfly.Core.Services;
using Wallfly.Speakers;

namespace Wallfly.Integration;

/// <summary>
/// Live Teams speaker names (spec 5.2; macOS <c>beginTeamsAttribution</c>, <c>drainTeamsObservations</c>,
/// <c>reconcileTeamsTranscript</c>, <c>refreshTeamsSpeakerPresentation</c> and the timeline export of
/// <c>stopRecording</c>). The watcher's observations are queued from its thread; the service ingests them on the UI thread
/// into a <see cref="TeamsAttributionSession"/> (Windows policy: detection lag 0.9 s, evidence valid 1 s) and reconciles
/// the live window (15 s, 30 s context) whenever remote text arrives or the evidence changes. At stop the final pass
/// labels every remote word of the session and the evidence is exported as <c>speakerTimeline</c>.
/// </summary>
public sealed class SpeakerAttributionHook : ISpeakerAttributionHook, IDisposable
{
    /// <summary>Creates the watcher of one recording: meeting clock, Teams root PID, observation sink.</summary>
    public delegate ITeamsSpeakerWatcher WatcherFactory(Func<double> clock, int teamsProcessId, Action<TeamsSpeakerObservation> onObservation);

    private readonly WatcherFactory? watcherFactory;
    private readonly TeamsAttributionPolicy policy;
    private readonly ConcurrentQueue<(Guid Session, TeamsSpeakerObservation Observation)> inbox = new();
    private TeamsAttributionSession? session;
    private ITeamsSpeakerWatcher? watcher;
    private Func<double> clock = () => 0;
    private TeamsSpeakerStatus baseStatus = TeamsSpeakerStatus.WaitingForSpeaker;
    private int participantCount;

    /// <param name="watcherFactory">Null: no UI reads (tests feed observations through <see cref="Receive"/>).</param>
    /// <param name="policy">Null: <see cref="TeamsAttributionPolicy.Windows"/>.</param>
    public SpeakerAttributionHook(WatcherFactory? watcherFactory, TeamsAttributionPolicy? policy = null)
    {
        this.watcherFactory = watcherFactory;
        this.policy = policy ?? TeamsAttributionPolicy.Windows;
    }

    public bool IsActive => session != null;

    /// <summary>The running session (diagnostics, tests).</summary>
    public TeamsAttributionSession? Session => session;

    public string Status
    {
        get
        {
            if (session is not { } current) return "";
            var status = baseStatus;
            // No fresh evidence: the reads stopped or stall (macOS: observation age above the evidence lifetime).
            if (current.LastObservation is { } last && SafeClock() - last.Timestamp > current.Policy.EvidenceLifetime) status = TeamsSpeakerStatus.Unavailable;
            return status.DisplayName() + (participantCount > 0 ? $" · {participantCount} participant{(participantCount == 1 ? "" : "s")}" : "");
        }
    }

    public void OnRecordingStarted(SpeakerAttributionStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        Cancel();
        while (inbox.TryDequeue(out _)) { }
        session = new TeamsAttributionSession(start.SessionID, start.Origin, start.BaseDuration, policy);
        clock = start.Clock;
        baseStatus = TeamsSpeakerStatus.WaitingForSpeaker;
        participantCount = 0;
        var sessionID = start.SessionID;
        if (watcherFactory is null) return;
        watcher = watcherFactory(start.Clock, start.TeamsProcessId, observation => Receive(sessionID, observation));
        watcher.Start();
    }

    /// <summary>The observation inbox (macOS <c>receiveTeamsObservation</c>); any thread.</summary>
    public void Receive(Guid sessionID, TeamsSpeakerObservation observation) => inbox.Enqueue((sessionID, observation));

    public bool IngestObservations(List<MeetingParticipant> participants)
    {
        var any = false;
        while (inbox.TryDequeue(out var item))
        {
            if (session is not { } current || item.Session != current.Id) continue;
            current.Ingest(item.Observation, participants);
            any = true;
        }
        if (any && session?.LastObservation is { } last)
        {
            baseStatus = last.Status;
            participantCount = last.Participants.Count(person => !person.IsSelf);
        }
        return any;
    }

    public IReadOnlyList<TranscriptRevision> OnTranscriptChanged(IReadOnlyList<TranscriptSegment> transcript, Guid? arrived = null)
    {
        if (session is not { } current) return [];
        var revisions = current.Reconcile(transcript, all: false, arrived).Select(change => new TranscriptRevision(change.Index, change.Segment)).ToList();
        Refresh(current, transcript, all: false);
        return revisions;
    }

    public Task StopObservingAsync()
    {
        var running = Interlocked.Exchange(ref watcher, null);
        return running?.StopAsync() ?? Task.CompletedTask;
    }

    public SpeakerAttributionResult OnStopped(IReadOnlyList<TranscriptSegment> transcript, double until)
    {
        if (session is not { } current) return SpeakerAttributionResult.Empty;
        try
        {
            var revisions = current.Reconcile(transcript, all: true).Select(change => new TranscriptRevision(change.Index, change.Segment)).ToList();
            // The persisted evidence (spec 5.2: about 30 KB per hour) from which every decision can be replayed.
            return new SpeakerAttributionResult(revisions, SpeakerTimelineCodec.Encode(current, until));
        }
        finally { End(); }
    }

    public void Cancel()
    {
        var running = Interlocked.Exchange(ref watcher, null);
        if (running != null) _ = StopQuietlyAsync(running);
        End();
    }

    public void Dispose() => Cancel();

    private void End()
    {
        session = null;
        while (inbox.TryDequeue(out _)) { }
    }

    private static async Task StopQuietlyAsync(ITeamsSpeakerWatcher running)
    {
        try { await running.StopAsync().ConfigureAwait(false); }
        catch (Exception error) { Trace.TraceWarning("Wallfly: stopping the Teams speaker watcher failed: " + error.Message); }
    }

    /// <summary>macOS <c>refreshTeamsSpeakerPresentation</c>: status and participant count over the live window.</summary>
    private void Refresh(TeamsAttributionSession current, IReadOnlyList<TranscriptSegment> transcript, bool all)
    {
        var cutoff = current.LatestOffset - current.Policy.RevisionWindow;
        var recent = all ? transcript : transcript.Where(segment => segment.Offset + segment.Duration >= cutoff).ToList();
        var diagnostics = current.Diagnostics(recent, SafeClock());
        var status = current.LastObservation?.Status ?? TeamsSpeakerStatus.WaitingForSpeaker;
        if (status is TeamsSpeakerStatus.Active or TeamsSpeakerStatus.WaitingForSpeaker && diagnostics.LatestRemoteHasWordTimes == false)
            status = TeamsSpeakerStatus.MissingWordTimes;
        baseStatus = status;
        participantCount = diagnostics.ParticipantCount;
    }

    private double SafeClock()
    {
        try { return clock(); }
        catch (Exception) { return 0; }
    }
}
