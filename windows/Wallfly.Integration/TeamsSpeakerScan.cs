using System.Diagnostics;
using System.Globalization;
using Wallfly.Speakers;
using Wallfly.Teams;
using Observation = Wallfly.Speakers.TeamsSpeakerObservation;
using ObservedParticipant = Wallfly.Speakers.TeamsObservedParticipant;
using ObservationFailure = Wallfly.Speakers.TeamsObservationFailure;
using SpeakerStatus = Wallfly.Speakers.TeamsSpeakerStatus;

namespace Wallfly.Integration;

/// <summary>Reads one tree of a Teams window (on the UIA thread): <see cref="UiaReader.ReadTree"/> in production.</summary>
public delegate TeamsTreeRead TeamsTreeReader(nint window, TeamsSelectors selectors, UiaTreeOptions options);

/// <summary>
/// One read of the Teams speaker UI (port of <c>TeamsSpeakerWatcher.sample()</c>, macOS 1.13.0): enumerate the visible
/// Teams windows (at most 8, the preferred meeting window first), read each tree within the shared 0.22 s budget
/// (structure, then text inside tiles only), parse a roster per meeting window and elect the scene. The result is one
/// <see cref="Observation"/> whose timestamp is the middle of the read on the meeting clock; the attribution session
/// backdates it by the policy's detection lag. Chat, input fields and the participant list are never read; Teams is
/// never changed. Not thread-safe by itself: one read at a time (a read still stuck on an abandoned UIA thread makes the
/// next one answer "unavailable" instead of running concurrently).
/// </summary>
public sealed class TeamsSpeakerScan(TeamsSelectorSource selectors, ITeamsDesktop desktop)
{
    private readonly object gate = new();
    private TeamsSceneSelector selector = new();
    private readonly HashSet<string> knownWindows = new(StringComparer.Ordinal);
    private int? pid;
    private Guid epoch = Guid.NewGuid();

    /// <summary>The Teams processes and windows seen by the last read (the change signal listens to them).</summary>
    public IReadOnlyCollection<int> LastProcessIds { get; private set; } = [];

    /// <param name="readTree">Reads one window tree with the given remaining budget.</param>
    /// <param name="clock">The meeting clock (seconds) the observation is stamped with.</param>
    public Observation Read(TeamsTreeReader readTree, Func<double> clock)
    {
        if (!Monitor.TryEnter(gate)) return Clear(clock, SpeakerStatus.Unavailable, ObservationFailure.Unreadable);
        try { return ReadLocked(readTree, clock); }
        finally { Monitor.Exit(gate); }
    }

    private Observation ReadLocked(TeamsTreeReader readTree, Func<double> clock)
    {
        var current = selectors.Current;
        var limits = current.Limits;
        var processes = desktop.FindProcesses(current.ProcessNames);
        if (processes.Count == 0)
        {
            pid = null; selector = new(); knownWindows.Clear(); LastProcessIds = [];
            return Clear(clock, SpeakerStatus.TeamsNotRunning, ObservationFailure.NotRunning);
        }
        var root = processes[0].Pid;
        if (pid != root)
        {
            // A new Teams process: a new identity context, no carried-over window preference.
            pid = root; epoch = Guid.NewGuid(); selector = new(); knownWindows.Clear();
        }
        var targets = processes.Take(Math.Max(1, limits.MaxProcesses)).Select(process => process.Pid).ToList();
        LastProcessIds = targets;
        var began = clock();
        var budget = Stopwatch.StartNew();
        var budgetSeconds = limits.BudgetMilliseconds / 1000.0;
        var windows = desktop.FindWindows(targets, current.WindowClasses).Where(window => window.Visible).ToList();
        var ranked = windows.Select((window, index) => (Window: window, Index: index))
            .OrderBy(entry => selector.WindowPriority(Surface(entry.Window), null)).ThenBy(entry => entry.Index)
            .Take(Math.Max(1, limits.MaxWindows)).Select(entry => entry.Window).ToList();
        var visible = ranked.Where(window => !window.Minimized || current.ReadMinimizedWindows).ToList();
        var present = visible.Select(Surface).ToHashSet(StringComparer.Ordinal);
        knownWindows.IntersectWith(present);
        var discovering = selector.PreferredSurface is null || !present.Contains(selector.PreferredSurface);
        var candidates = new List<TeamsMeetingScene>();
        var truncated = false;
        var sawMeeting = false;
        string? lastRead = null;
        foreach (var window in visible)
        {
            var surface = Surface(window);
            // Once a healthy meeting is found, unrelated windows are not walked every poll.
            if (!discovering && candidates.Any(scene => scene.SurfaceId == selector.PreferredSurface) && !knownWindows.Contains(surface)) continue;
            var remaining = budgetSeconds - budget.Elapsed.TotalSeconds;
            if (remaining <= 0) { truncated = true; break; }
            lastRead = surface;
            TeamsTreeRead read;
            try { read = readTree(window.Handle, current, new UiaTreeOptions { BudgetSeconds = remaining, MaxNodes = limits.MaxNodes, MaxDepth = limits.MaxDepth }); }
            catch (Exception error) when (UiaReader.IsUiaFailure(error)) { truncated = true; continue; }
            if (read.Complete && read.Tree is { } tree && TeamsRosterParser.IsMeeting(tree, current) && TeamsRosterParser.Roster(tree, current) is { } roster)
            {
                sawMeeting = true;
                if (roster.Participants.Count > 0 || roster.UnidentifiedActive || roster.LocalUserSpeaking)
                {
                    knownWindows.Add(surface);
                    candidates.Add(new TeamsMeetingScene(surface, roster));
                }
            }
            if (!read.Complete || budget.Elapsed.TotalSeconds >= budgetSeconds)
            {
                truncated = true;
                if (budget.Elapsed.TotalSeconds >= budgetSeconds) break;
            }
        }
        if (selector.Select(candidates) is not { } selected)
        {
            // A large or unreadable chat window must not consume every recovery poll.
            selector.Retry(lastRead, visible.Select(Surface).ToList());
            var status = truncated || sawMeeting || visible.Count == 0 ? SpeakerStatus.Unavailable : SpeakerStatus.MeetingNotFound;
            var failure = truncated ? ObservationFailure.Truncated
                : sawMeeting || visible.Count == 0 ? ObservationFailure.Unreadable : ObservationFailure.MeetingNotFound;
            return Clear(clock, status, failure, began);
        }
        var finished = clock();
        // A failed secondary walk must not discard a complete primary view; failed views are absent from the candidates.
        return new Observation
        {
            Timestamp = (began + finished) / 2,
            SurfaceID = selected.Scene.SurfaceId,
            Participants = selected.Scene.Roster.Participants.Select(Map).ToList(),
            Status = Map(selected.Status),
            ContextID = Context,
            ReadDuration = Math.Max(0, finished - began),
            Failure = selected.Failure is { } reason ? Map(reason) : null
        };
    }

    private string? Context => pid is int value ? $"{value.ToString(CultureInfo.InvariantCulture)}/{epoch:D}" : null;

    private Observation Clear(Func<double> clock, SpeakerStatus status, ObservationFailure failure, double? began = null)
    {
        var now = clock();
        return new Observation
        {
            Timestamp = now, Status = status, ContextID = Context, Failure = failure,
            ReadDuration = began is double start ? Math.Max(0, now - start) : 0
        };
    }

    private static string Surface(TeamsWindowInfo window) => window.Handle.ToString("X", CultureInfo.InvariantCulture);

    internal static ObservedParticipant Map(Wallfly.Teams.TeamsObservedParticipant participant) =>
        new(participant.Key, participant.Name, participant.Active, participant.IsSelf, participant.StableKey);

    internal static SpeakerStatus Map(Wallfly.Teams.TeamsSpeakerStatus status) => status switch
    {
        Wallfly.Teams.TeamsSpeakerStatus.Active => SpeakerStatus.Active,
        Wallfly.Teams.TeamsSpeakerStatus.WaitingForSpeaker => SpeakerStatus.WaitingForSpeaker,
        Wallfly.Teams.TeamsSpeakerStatus.Ambiguous => SpeakerStatus.Ambiguous,
        Wallfly.Teams.TeamsSpeakerStatus.SelfSpeaking => SpeakerStatus.SelfSpeaking,
        Wallfly.Teams.TeamsSpeakerStatus.TeamsNotRunning => SpeakerStatus.TeamsNotRunning,
        Wallfly.Teams.TeamsSpeakerStatus.MeetingNotFound => SpeakerStatus.MeetingNotFound,
        _ => SpeakerStatus.Unavailable
    };

    internal static ObservationFailure Map(Wallfly.Teams.TeamsObservationFailure failure) => failure switch
    {
        Wallfly.Teams.TeamsObservationFailure.NotRunning => ObservationFailure.NotRunning,
        Wallfly.Teams.TeamsObservationFailure.MeetingNotFound => ObservationFailure.MeetingNotFound,
        Wallfly.Teams.TeamsObservationFailure.Truncated => ObservationFailure.Truncated,
        Wallfly.Teams.TeamsObservationFailure.ConflictingViews => ObservationFailure.ConflictingViews,
        Wallfly.Teams.TeamsObservationFailure.UnidentifiedSpeaker => ObservationFailure.UnidentifiedSpeaker,
        Wallfly.Teams.TeamsObservationFailure.LocalUserSpeaking => ObservationFailure.LocalUserSpeaking,
        _ => ObservationFailure.Unreadable
    };
}
