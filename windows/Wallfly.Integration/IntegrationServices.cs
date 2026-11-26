using Wallfly.Core.Persistence;
using Wallfly.Core.Services;
using Wallfly.Diarization;
using Wallfly.Speakers;
using Wallfly.Teams;

namespace Wallfly.Integration;

/// <summary>What <see cref="IntegrationServices.CreateDefault"/> wires; every switch defaults to the production setup.</summary>
public sealed record IntegrationOptions
{
    /// <summary>Teams call sampling (live names, the automatic source's endpoint, the silent-meeting-audio guard, auto-stop) and live speaker names.</summary>
    public bool Teams { get; init; } = true;
    /// <summary>Early speaker reads on Teams UI change notifications (WinEvents), at most every 150 ms.</summary>
    public bool EventDrivenSpeakerReads { get; init; } = true;
    /// <summary>The selector source; null: the shipped defaults plus <c>%LOCALAPPDATA%\Wallfly\teams-selectors.json</c>.</summary>
    public TeamsSelectorSource? Selectors { get; init; }
    /// <summary>
    /// The diarizer of the speaker matching (both passes); null: <see cref="SherpaOnnxDiarizer"/> with
    /// <see cref="SherpaOnnxDiarizerOptions.Others"/> and <see cref="SherpaOnnxDiarizerOptions.Microphone"/>.
    /// </summary>
    public Func<IDiarizer>? Diarizer { get; init; }
    /// <summary>
    /// The microphone pass; default <see cref="Integration.MicrophonePass.Never"/> (decision of 25.11.2026: the microphone
    /// track is always the user, "Me", and is never split).
    /// </summary>
    public MicrophonePass MicrophonePass { get; init; } = MicrophonePass.Never;
    /// <summary>
    /// The cloud engine and retry policy of the speaker matching; null: <see cref="DeepgramPrerecordedDiarizer"/> with the defaults
    /// and the upload cache in <see cref="DiarizationStepOptions.DefaultUploadCacheDirectory"/>.
    /// </summary>
    public DiarizationStepOptions? Speakers { get; init; }
    /// <summary>Null: <c>%LOCALAPPDATA%\Wallfly\library.json</c>.</summary>
    public LibraryPointer? LibraryPointer { get; init; }
    public RecordingTimings Timings { get; init; } = RecordingTimings.Default;
}

/// <summary>The composition root of the Core hooks (pass the result to <c>MeetingLibraryService(…, hooks:)</c>).</summary>
public static class IntegrationServices
{
    /// <summary>
    /// The production hooks: <see cref="TeamsCallSource"/> over one <see cref="TeamsCallMonitor"/>, a
    /// <see cref="SpeakerAttributionHook"/> whose <see cref="TeamsSpeakerWatcher"/> reads on that monitor's UIA thread,
    /// a <see cref="DiarizationStep"/> (sherpa-onnx) and the <see cref="SpeakerEditingService"/>. The service disposes them.
    /// </summary>
    public static LibraryServiceHooks CreateDefault(IntegrationOptions? options = null)
    {
        options ??= new IntegrationOptions();
        var resources = new List<IDisposable>();
        ITeamsCallSource? calls = null;
        SpeakerAttributionHook? attribution = null;
        if (options.Teams)
        {
            var selectors = options.Selectors ?? TeamsSelectorSource.CreateDefault();
            if (options.Selectors is null) resources.Add(selectors);
            var monitor = new TeamsCallMonitor(selectors);
            resources.Add(monitor);
            calls = new TeamsCallSource(monitor);
            var scan = new TeamsSpeakerScan(selectors, new Win32TeamsDesktop());
            ITeamsChangeSignal? signal = options.EventDrivenSpeakerReads ? new WinEventTeamsChangeSignal() : null;
            if (signal != null) resources.Insert(0, signal);
            var source = new UiaTeamsSpeakerSource(monitor.Thread, scan, selectors, signal);
            attribution = new SpeakerAttributionHook((clock, _, onObservation) => new TeamsSpeakerWatcher(source, clock, onObservation));
        }
        return new LibraryServiceHooks
        {
            TeamsCalls = calls,
            SpeakerAttribution = attribution,
            // The local engine merges split voices by their fingerprints, each pass with its own merge settings: the microphone
            // pass (off by default, MicrophonePass.Never) clusters far-field speech with its own threshold
            // (SherpaOnnxDiarizerOptions.Microphone), the Others pass keeps 0.6 (SherpaOnnxDiarizerOptions.Others; DiarizationStep
            // undoes merges that contradict the Teams names). A supplied diarizer serves both. The engine of the Others pass
            // (Teams alone, Deepgram or this device) is chosen per run from the settings.
            PostRecordingSteps = [new DiarizationStep(options.Diarizer ?? (() => new SherpaOnnxDiarizer(SherpaOnnxDiarizerOptions.Others)),
                microphonePass: options.MicrophonePass,
                microphoneDiarizerFactory: options.Diarizer is null ? () => new SherpaOnnxDiarizer(SherpaOnnxDiarizerOptions.Microphone) : null,
                options: options.Speakers ?? new DiarizationStepOptions { UploadCacheDirectory = DiarizationStepOptions.DefaultUploadCacheDirectory })],
            SpeakerEditor = new SpeakerEditingService(),
            LibraryPointer = options.LibraryPointer,
            Timings = options.Timings,
            Resources = resources
        };
    }
}
