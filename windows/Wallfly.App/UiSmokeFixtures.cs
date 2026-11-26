using System.IO;
using Wallfly.Ai;
using Wallfly.App.Shell;
using Wallfly.App.ViewModels;
using Wallfly.Audio;
using Wallfly.Core;
using Wallfly.Core.Persistence;
using Wallfly.Core.Services;
using Wallfly.Integration;
using Wallfly.Teams;
using Wallfly.Transcription;

namespace Wallfly.App;

/// <summary>Used only with <c>--smoke-test</c>: the real UI lifecycle without devices or HTTP.</summary>
internal static class UiSmokeAudio
{
    /// <summary>Pushes a short tone on both channels so the meters move.</summary>
    internal static void EmitLevels(this FakeAudioEngine engine)
    {
        engine.PushConstant(AudioSource.Me, .37f, .04);
        engine.PushConstant(AudioSource.Others, -.37f, .04);
    }
}

internal sealed class UiSmokeTranscriptionClient : ITranscriptionClient
{
    public Task<string> TranscribeAsync(byte[] wav, CancellationToken cancellationToken) => Task.FromResult("");
}

internal sealed class UiSmokeProvider : IChatProvider
{
    public int GenerateCount { get; private set; }
    public int DelayMilliseconds { get; set; }
    public bool FailEnhancement { get; set; }

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        GenerateCount++;
        await Task.Delay(DelayMilliseconds, cancellationToken);
        if (request.Instructions.StartsWith(TaskExtractor.Instructions, StringComparison.Ordinal)) return ChatResult.Complete("[]");
        if (FailEnhancement) throw AiError.Transient("Simulated enhancement failure");
        return ChatResult.Complete("{\"title\":null,\"notes_markdown\":\"## Overview\\nThe smaller pilot starts with **ten teams**. [00:12]\\n## Next steps\\n- Review the invite flow on Friday.\\n- Include keyboard and screen reader testing.\",\"tasks\":[]}");
    }
}

/// <summary>Scripted answers for every dialog, so the smoke run never blocks on a modal window or starts Explorer.</summary>
internal sealed class SmokeDialogService(string root) : IDialogService
{
    public List<string> Calls { get; } = [];
    public string Clipboard { get; private set; } = "";
    public bool ConfirmAnswer { get; set; } = true;
    public QuitChoice QuitAnswer { get; set; } = QuitChoice.SaveAndEnd;

    public bool Confirm(string title, string message, string confirm) { Calls.Add("confirm:" + title); return ConfirmAnswer; }
    public string? AskMeetingName(string current) { Calls.Add("rename"); return "Renamed in the smoke test"; }
    public string? SpeakerNameAnswer { get; set; }
    public SpeakerAssignment? AssignmentAnswer { get; set; }

    public QuitChoice AskSaveAndEnd(QuitPrompt prompt = QuitPrompt.SaveAndEnd) { Calls.Add(prompt == QuitPrompt.SaveAndEnd ? "quit" : "quit:" + prompt); return prompt == QuitPrompt.SaveAndEnd ? QuitAnswer : QuitChoice.ReturnToMeeting; }
    public string? AskSpeakerName(string current) { Calls.Add("speaker-name:" + current); return SpeakerNameAnswer; }
    public SpeakerAssignment? AskSpeakerAssignment(string excerpt, IReadOnlyList<SpeakerOption> participants) { Calls.Add("assign:" + participants.Count); return AssignmentAnswer; }
    public void ShowSettings(SettingsArea area) => Calls.Add("settings:" + area);
    public void ShowOnboarding() => Calls.Add("onboarding");
    public bool EditFolder(FolderEditorViewModel editor) { Calls.Add("folder"); return false; }
    public void ShowTaskReview(Guid meetingID) => Calls.Add("review:" + meetingID);
    public string? AskSaveMarkdown(string fileName) { Calls.Add("export"); return Path.Combine(root, "smoke", fileName); }
    public string? AskFolder(string title, string? initial) { Calls.Add("folder-picker"); return null; }
    public string? AskFile(string filter, string? initial) { Calls.Add("file-picker"); return null; }
    public void Inform(string title, string message) => Calls.Add("inform:" + title + ": " + message);
    public void Reveal(string path) => Calls.Add("reveal:" + path);
    public void OpenFolder(string path) => Calls.Add("open:" + path);
    public void OpenUri(string uri) => Calls.Add("uri:" + uri);
    public void CopyText(string text) { Calls.Add("copy"); Clipboard = text; }
}

/// <summary>
/// The helper in the smoke run: never started. "Starting" it writes a fresh status.json into the smoke detection folder,
/// as the real helper does on its first tick.
/// </summary>
internal sealed class SmokeDetectorProcess(DetectionStorage storage) : IDetectorProcess
{
    public const string Version = "1.0.0-smoke";
    public string ExecutablePath => storage.PathOf(WallflyPaths.DetectorExecutableName);
    public bool IsInstalled => true;
    public string? ShippedVersion => Version;
    public bool IsRunning { get; set; }
    public int Starts { get; private set; }

    public bool Start()
    {
        Starts++;
        IsRunning = true;
        WriteStatus();
        return true;
    }

    public bool Stop(TimeSpan timeout)
    {
        IsRunning = false;
        return true;
    }

    public void WriteStatus(string message = "Ready · no Teams meeting running") =>
        storage.Write(new DetectionStatus { Date = DateTimeOffset.UtcNow, Message = message, HelperVersion = Version, State = "absent" }, DetectionStorage.StatusFile);
}

/// <summary>The Run key in memory (the smoke run never writes the registry).</summary>
internal sealed class InMemoryRunKeyStore : IRunKeyStore
{
    private readonly Dictionary<string, string> values = [];
    public string? Get(string name) => values.GetValueOrDefault(name);
    public void Set(string name, string value) => values[name] = value;
    public void Delete(string name) => values.Remove(name);
    public byte[]? GetApproval(string name) => null;
}

/// <summary>Sleep on demand for the smoke run.</summary>
internal sealed class SmokePowerEvents : IPowerEvents
{
    public event Action? Suspending;
    public event Action? Resumed { add { } remove { } }
    public void Suspend() => Suspending?.Invoke();
    public void Dispose() { }
}

/// <summary>Fake devices and audio sessions for the settings screenshots.</summary>
internal sealed class SmokeSystemProbe(IAudioDeviceCatalog devices) : ISystemProbe
{
    public IReadOnlyList<AudioDevice> Devices(AudioFlow flow) => devices.List(flow);
    public IReadOnlyList<AudioSessionEntry> AudioSessions() =>
    [
        new(4242, 4000, AudioSessionActivity.Active, true, false, .12f, "ms-teams.exe", "Microsoft Teams", "{smoke}"),
        new(4343, 4000, AudioSessionActivity.Inactive, true, false, 0, "chrome.exe", "", "{smoke}"),
        new(4, 0, AudioSessionActivity.Active, true, true, 0, "", "System sounds", "{smoke}")
    ];
    public MicrophoneAccess MicrophoneAccess() => ViewModels.MicrophoneAccess.Allowed;
    public bool FileExists(string path) => File.Exists(path);
}

/// <summary>
/// The hooks of the smoke run: the real (pure) speaker editor and a scripted speaker matching step, with the library
/// pointer inside the smoke folder. No Teams call source (whole-system meeting audio), no UI Automation, no models.
/// </summary>
internal static class UiSmokeHooks
{
    public static LibraryServiceHooks Create(string root, SmokeSpeakerMatching matching) => new()
    {
        SpeakerEditor = new SpeakerEditingService(),
        PostRecordingSteps = [matching],
        LibraryPointer = new LibraryPointer(Path.Combine(root, "Pointer"))
    };
}

/// <summary>
/// "Re-detect speakers" in the smoke run: queues only while <see cref="Armed"/> (stops of the run queue nothing), shows
/// "Matching speakers… 40%" and finishes when the smoke test calls <see cref="Finish"/>.
/// </summary>
internal sealed class SmokeSpeakerMatching : IPostRecordingStep
{
    private readonly List<Guid> queue = [];
    private IMeetingHost? host;
    private string progress = "";

    public bool Armed { get; set; }
    public string Progress => progress;
    public bool HasPendingWork => queue.Count > 0;

    public void Attach(IMeetingHost host) => this.host = host;
    public bool IsPending(Guid meetingID) => queue.Contains(meetingID);

    public bool CanRun(Guid meetingID) =>
        host is { IsClosing: false, IsMovingLibrary: false } current && !IsPending(meetingID) && current.RecordingMeetingID != meetingID
        && current.Find(meetingID) is { } meeting && meeting.Transcript.Any(segment => segment.Speaker == Speaker.Others);

    public bool Enqueue(Guid meetingID)
    {
        if (!Armed || !CanRun(meetingID) || host?.Find(meetingID) is not { } meeting) return false;
        queue.Add(meetingID);
        meeting.Diarization = new DiarizationState { Engine = "smoke", Status = DiarizationStatus.Running, StartedAt = host.Now };
        progress = "Matching speakers… 40%";
        host.StateChanged();
        return true;
    }

    /// <summary>Completes the queued run: the meeting records a finished run and the status line goes away.</summary>
    public void Finish()
    {
        if (host is null) return;
        foreach (var meetingID in queue.ToList())
        {
            if (host.Find(meetingID) is { } meeting)
            {
                meeting.Diarization = new DiarizationState { Engine = "smoke", Status = DiarizationStatus.Completed, StartedAt = meeting.Diarization?.StartedAt, FinishedAt = host.Now };
                host.Persist(meeting);
            }
        }
        queue.Clear();
        progress = "";
        host.StateChanged();
    }

    public Task WhenIdle() => Task.CompletedTask;
    public void Cancel() { queue.Clear(); progress = ""; }
    public void Resume() { }
}
