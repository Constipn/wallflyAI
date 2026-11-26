using System.Diagnostics;
using Wallfly.Teams;
using Observation = Wallfly.Speakers.TeamsSpeakerObservation;
using ObservationFailure = Wallfly.Speakers.TeamsObservationFailure;
using SpeakerStatus = Wallfly.Speakers.TeamsSpeakerStatus;

namespace Wallfly.Integration;

/// <summary>Where the speaker watcher gets its observations from: one read per call, plus change signals.</summary>
public interface ITeamsSpeakerSource
{
    /// <summary>One read; the observation is stamped with <paramref name="clock"/> at the middle of the read.</summary>
    Task<Observation> ReadAsync(Func<double> clock, CancellationToken cancellationToken = default);

    /// <summary>Raised on any thread when the Teams UI signalled a change worth an early read.</summary>
    event Action? Changed;
}

/// <summary>The lifecycle the attribution hook needs from a watcher.</summary>
public interface ITeamsSpeakerWatcher
{
    void Start();
    /// <summary>One last read, then no further observations (stop stage "stopping speaker detection").</summary>
    Task StopAsync();
}

/// <summary>
/// Production <see cref="ITeamsSpeakerSource"/>: <see cref="TeamsSpeakerScan"/> on the shared, watchdog-protected Teams UIA
/// thread (the same thread as the call sampling of the auto-stop). A stalled or paused UIA thread answers "unavailable".
/// </summary>
public sealed class UiaTeamsSpeakerSource : ITeamsSpeakerSource
{
    private readonly UiaThread<TeamsReaderSession> thread;
    private readonly TeamsSpeakerScan scan;
    private readonly TeamsSelectorSource selectors;
    private readonly ITeamsChangeSignal? signal;

    public UiaTeamsSpeakerSource(UiaThread<TeamsReaderSession> thread, TeamsSpeakerScan scan, TeamsSelectorSource selectors, ITeamsChangeSignal? signal = null)
    {
        this.thread = thread; this.scan = scan; this.selectors = selectors; this.signal = signal;
        if (signal != null) signal.Changed += () => Changed?.Invoke();
    }

    public event Action? Changed;

    public async Task<Observation> ReadAsync(Func<double> clock, CancellationToken cancellationToken = default)
    {
        var budget = TimeSpan.FromMilliseconds(selectors.Current.Limits.BudgetMilliseconds);
        var result = await thread.RunAsync(session => scan.Read(session.Uia.ReadTree, clock), budget, cancellationToken).ConfigureAwait(false);
        signal?.Watch(scan.LastProcessIds);
        return result.Status == UiaRunStatus.Completed && result.Value is { } observation
            ? observation
            : new Observation { Timestamp = clock(), Status = SpeakerStatus.Unavailable, Failure = ObservationFailure.Unreadable };
    }
}

/// <summary>
/// The Teams speaker watcher (port of <c>TeamsSpeakerWatcher</c>): reads every 500 ms and, on a change signal of the
/// Teams UI, early — but never sooner than 150 ms after the previous read started. Reads never overlap. Each observation
/// goes to <c>onObservation</c> (any thread; the hook queues it for the UI thread). <see cref="StopAsync"/> makes one last
/// read and ends the watcher.
/// </summary>
public sealed class TeamsSpeakerWatcher : ITeamsSpeakerWatcher
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan MinimumEventInterval = TimeSpan.FromMilliseconds(150);

    private readonly ITeamsSpeakerSource source;
    private readonly Func<double> clock;
    private readonly Action<Observation> onObservation;
    private readonly TimeSpan interval;
    private readonly TimeSpan minimumEventInterval;
    private readonly SemaphoreSlim reading = new(1, 1);
    private readonly SemaphoreSlim changed = new(0, 1);
    private readonly CancellationTokenSource stop = new();
    private Task? loop;
    private int started;
    private int stopped;
    private long lastReadStarted;

    public TeamsSpeakerWatcher(ITeamsSpeakerSource source, Func<double> clock, Action<Observation> onObservation,
        TimeSpan? interval = null, TimeSpan? minimumEventInterval = null)
    {
        this.source = source; this.clock = clock; this.onObservation = onObservation;
        this.interval = interval ?? Interval;
        this.minimumEventInterval = minimumEventInterval ?? MinimumEventInterval;
        source.Changed += OnChanged;
    }

    /// <summary>Reads made so far (diagnostics, tests).</summary>
    public int Reads { get; private set; }

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0 || Volatile.Read(ref stopped) != 0) return;
        loop = Task.Run(RunAsync);
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        source.Changed -= OnChanged;
        stop.Cancel();
        if (loop != null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (Exception error) { Trace.TraceWarning("Wallfly: the Teams speaker watcher ended with an error: " + error.Message); }
            // macOS stop(): one last read, so the final words of the call still have evidence.
            await ReadOnceAsync(CancellationToken.None).ConfigureAwait(false);
        }
        stop.Dispose();
    }

    private void OnChanged()
    {
        if (Volatile.Read(ref stopped) != 0) return;
        try { if (changed.CurrentCount == 0) changed.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync()
    {
        var token = stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await ReadOnceAsync(token).ConfigureAwait(false);
                var sinceRead = Stopwatch.GetElapsedTime(Interlocked.Read(ref lastReadStarted));
                var untilTick = interval - sinceRead;
                if (untilTick <= TimeSpan.Zero) continue;
                bool signalled;
                using (var round = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    var tick = Task.Delay(untilTick, round.Token);
                    var signal = changed.WaitAsync(round.Token);
                    await Task.WhenAny(tick, signal).ConfigureAwait(false);
                    // Leave no waiter behind: a pending wait would swallow the next signal.
                    round.Cancel();
                    signalled = signal.IsCompletedSuccessfully;
                }
                token.ThrowIfCancellationRequested();
                if (!signalled) continue;
                // Event-driven read, at most every 150 ms.
                var wait = minimumEventInterval - Stopwatch.GetElapsedTime(Interlocked.Read(ref lastReadStarted));
                if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReadOnceAsync(CancellationToken token)
    {
        try { await reading.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            Interlocked.Exchange(ref lastReadStarted, Stopwatch.GetTimestamp());
            Observation observation;
            try { observation = await source.ReadAsync(clock, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception error)
            {
                Trace.TraceWarning("Wallfly: a Teams speaker read failed: " + error.Message);
                observation = new Observation { Timestamp = clock(), Status = SpeakerStatus.Unavailable, Failure = ObservationFailure.Unreadable };
            }
            Reads++;
            onObservation(observation);
        }
        finally { reading.Release(); }
    }
}
