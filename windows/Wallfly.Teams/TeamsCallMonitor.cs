namespace Wallfly.Teams;

/// <summary>Everything one UIA thread owns: the UIA reader, the call reader on top of it, and the mic probe.</summary>
public sealed class TeamsReaderSession : IDisposable
{
    public const string StatusMicIdle = "Ready · Teams is not using the microphone";
    public const string StatusCallAudio = "Teams call audio active · meeting view not recognised";

    private readonly Lazy<UiaReader> uia;
    private readonly Func<bool?> teamsAudioActive;

    /// <param name="teamsAudioActive">Tests: whether Teams holds an active audio session; null: <see cref="MicSessionProbe.TeamsAudioActive"/>.</param>
    public TeamsReaderSession(TeamsSelectorSource selectors, TeamsReaderState state, ITeamsDesktop? desktop = null, ITeamsWindowReader? windowReader = null,
        Func<double>? clock = null, Func<bool?>? teamsAudioActive = null)
    {
        uia = new Lazy<UiaReader>(() => new UiaReader(selectors.Current.Limits, clock));
        CallReader = new TeamsCallReader(desktop ?? new Win32TeamsDesktop(), windowReader ?? new LazyWindowReader(uia), selectors, state, clock);
        Mic = new MicSessionProbe(selectors);
        this.teamsAudioActive = teamsAudioActive ?? Mic.TeamsAudioActive;
    }

    public TeamsCallReader CallReader { get; }
    public MicSessionProbe Mic { get; }
    public UiaReader Uia => uia.Value;

    /// <summary>
    /// One detection sample. With <paramref name="micPrefilter"/>, Teams' UI is only read while a Teams process
    /// holds the microphone or a detected call is still open (<paramref name="callOpen"/>, so its end can be
    /// seen); otherwise the sample is <c>unknown</c> without touching Teams. Probe errors fail open.
    /// <para>
    /// While a detected call is open, an <c>absent</c> read is checked against the audio sessions: a Teams process with
    /// an ACTIVE render or capture session means the call still runs (the meeting view is in a layout the selectors do
    /// not know, e.g. screen sharing), so the sample becomes <c>unknown</c>, which never ends the detected call
    /// (2026-11-24: a screen share re-announced the running call). Unreadable sessions keep <c>absent</c>.
    /// </para>
    /// </summary>
    public TeamsDetectionSnapshot Sample(bool captureDiagnostics, bool micPrefilter = false, bool callOpen = false)
    {
        if (micPrefilter && !callOpen && !captureDiagnostics && !Mic.Probe().ShouldReadTeams)
            return TeamsDetectionSnapshot.Of(TeamsCallSample.Unknown, StatusMicIdle);
        var snapshot = CallReader.Sample(captureDiagnostics);
        if (callOpen && snapshot.Sample.State == TeamsCallState.Absent && teamsAudioActive() == true)
            return snapshot with { Sample = new TeamsCallSample(TeamsCallState.Unknown, snapshot.Sample.Pid), Status = StatusCallAudio };
        return snapshot;
    }

    public void Dispose()
    {
        if (uia.IsValueCreated) uia.Value.Dispose();
    }

    private sealed class LazyWindowReader(Lazy<UiaReader> reader) : ITeamsWindowReader
    {
        public TeamsWindowRead ReadWindow(TeamsWindowInfo window, TeamsSelectors selectors, double deadline, bool captureDiagnostics) =>
            reader.Value.ReadWindow(window, selectors, deadline, captureDiagnostics);
    }
}

/// <summary>
/// Teams call sampling on a watchdog-protected UIA thread, for the helper's 2-s loop and the app's
/// auto-stop watch. A stalled read answers <c>unknown</c> and its window is skipped for 30 s; after three
/// stalls in 60 s the monitor answers <c>unavailable</c> for 30 s.
/// </summary>
public sealed class TeamsCallMonitor : IDisposable
{
    public const string StatusStalled = "Teams view is not responding";
    public const string StatusPaused = "Teams reading paused after repeated stalls";

    private readonly UiaThread<TeamsReaderSession> thread;
    private readonly TeamsSelectorSource selectors;
    private readonly Func<double> clock;

    public TeamsCallMonitor(TeamsSelectorSource selectors, TeamsReaderState? state = null, Func<TeamsReaderSession>? sessionFactory = null,
        UiaThreadOptions? options = null, Func<double>? clock = null)
    {
        this.selectors = selectors;
        this.clock = clock ?? MonotonicClock.Now;
        State = state ?? new TeamsReaderState();
        var limits = selectors.Current.Limits;
        thread = new UiaThread<TeamsReaderSession>(sessionFactory ?? (() => new TeamsReaderSession(selectors, State, clock: this.clock)),
            options ?? new UiaThreadOptions { Budget = TimeSpan.FromMilliseconds(limits.BudgetMilliseconds), Name = "Wallfly Teams UIA" });
    }

    public TeamsReaderState State { get; }
    public UiaThread<TeamsReaderSession> Thread => thread;

    public async Task<TeamsDetectionSnapshot> SampleAsync(bool captureDiagnostics = false, bool micPrefilter = false, bool callOpen = false)
    {
        var limits = selectors.Current.Limits;
        var budget = TimeSpan.FromMilliseconds(captureDiagnostics ? limits.DiagnosticBudgetMilliseconds : limits.BudgetMilliseconds);
        var result = await thread.RunAsync(session => session.Sample(captureDiagnostics, micPrefilter, callOpen), budget).ConfigureAwait(false);
        switch (result.Status)
        {
            case UiaRunStatus.Completed:
                return result.Value!;
            case UiaRunStatus.Abandoned:
                State.MarkStalled(State.CurrentWindow, clock() + limits.StalledWindowSeconds);
                State.CurrentWindow = 0;
                return TeamsDetectionSnapshot.Of(TeamsCallSample.Unknown, StatusStalled);
            case UiaRunStatus.Unavailable:
                return TeamsDetectionSnapshot.Of(TeamsCallSample.Unavailable, StatusPaused);
            default:
                var faulted = new UiaReadDiagnostics();
                faulted.AddError("fault:" + (result.Error?.GetType().Name ?? "unknown"));
                return new TeamsDetectionSnapshot(TeamsCallSample.Unknown, null, StatusNotReadable, faulted);
        }
    }

    private const string StatusNotReadable = TeamsCallReader.StatusNotReadable;

    public void Dispose() => thread.Dispose();
}
