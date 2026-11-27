using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wallfly.Audio;
using Wallfly.Core;
using Wallfly.Core.Services;

namespace Wallfly.App.ViewModels;

/// <summary>
/// The recording bar (spec 9.2): start/stop with their shortcuts, the timer, two 8-bar meters ("Microphone",
/// "Meeting audio"), the meeting-audio notice and the (i) popover with the source, the state per channel, the microphone
/// and the gap counts.
/// </summary>
public sealed class RecordingViewModel : ObservableObject
{
    /// <summary>The meters follow the service's level timer (<see cref="RecordingSession.LevelInterval"/>, 50 ms), not the 100 ms inbox drain.</summary>
    public static readonly TimeSpan MeterInterval = RecordingSession.LevelInterval;

    private readonly AppViewModel app;
    private double meLevel;
    private double othersLevel;
    private bool infoOpen;
    private IReadOnlyList<string> infoLines = [];

    public RecordingViewModel(AppViewModel app)
    {
        this.app = app;
        StartCommand = new AsyncRelayCommand(() => app.StartRecordingAsync(), () => app.CanStartRecording);
        StopCommand = new AsyncRelayCommand(() => app.StopRecordingAsync(), () => app.Store.IsRecording && !app.Store.IsStopping);
        GoToRecordingCommand = new RelayCommand(app.OpenActiveMeeting);
        ToggleInfoCommand = new RelayCommand(() => { if (!InfoOpen) RefreshInfo(); InfoOpen = !InfoOpen; });
    }

    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }
    public IRelayCommand GoToRecordingCommand { get; }
    public IRelayCommand ToggleInfoCommand { get; }

    public bool IsRecording => app.Store.IsRecording;
    public bool IsStarting => app.Store.IsStarting;
    public bool IsStopping => app.Store.IsStopping;
    /// <summary>"Go to recording" replaces the controls while another meeting records or processes.</summary>
    public bool ShowGoToRecording => app.Store.Busy && app.Store.ActiveID != null && app.Store.ActiveID != app.SelectedID;
    public bool ShowStop => !ShowGoToRecording && (IsRecording || IsStopping);
    public bool ShowStart => !ShowGoToRecording && !ShowStop;
    /// <summary>Timer and meters appear in focus mode, next to "Stop recording".</summary>
    public bool ShowActivity => ShowStop && app.FocusMode;
    public string StartText => IsStarting ? Strings.StartingRecording : Strings.StartRecording;
    public string StopText => IsStopping ? Strings.Finishing : Strings.StopRecording;
    public string ElapsedText => Meeting.Timestamp(app.ActiveMeeting?.Duration ?? 0);
    public double MeLevel { get => meLevel; private set => SetProperty(ref meLevel, value); }
    public double OthersLevel { get => othersLevel; private set => SetProperty(ref othersLevel, value); }
    public string MeLevelName => Strings.Format(Strings.AudioLevelName, Strings.MeterMicrophone, (int)(MeLevel * 100));
    public string OthersLevelName => Strings.Format(Strings.AudioLevelName, Strings.MeterMeetingAudio, (int)(OthersLevel * 100));
    public bool InfoOpen { get => infoOpen; set => SetProperty(ref infoOpen, value); }
    public IReadOnlyList<string> InfoLines { get => infoLines; private set { if (SetProperty(ref infoLines, value)) OnPropertyChanged(nameof(InfoText)); } }
    public string InfoText => string.Join("\n", InfoLines);

    /// <summary>
    /// The notice next to the controls while this meeting records: the meeting-audio source Core chose
    /// ("Meeting audio: system output · Headset", "Meeting audio: whole system", the guard's switch) and, when the
    /// system default replaced the configured microphone (spec 3.1), "Microphone: Name · Fallback (reason)".
    /// </summary>
    public string AudioNotice
    {
        get
        {
            if (!ShowStop) return "";
            var parts = new List<string> { app.Store.RecordingAudioDescription };
            if (app.Store.MicrophoneStatus is { Fallback: true } microphone)
                parts.Add(Strings.Format(Strings.MicrophoneNotice, microphone.Description));
            // A device change of the running recording ("Output device changed · …", "Microphone disconnected · …"), for a few seconds.
            parts.Add(app.Store.AudioDeviceNotice);
            return string.Join(" · ", parts.Where(part => part.Length > 0));
        }
    }
    public bool ShowAudioNotice => AudioNotice.Length > 0;

    /// <summary>
    /// "No meeting audio from Microsoft Teams. …" / "No meeting audio from your call apps. …" while this meeting records and its meeting audio
    /// stays silent although something plays (Core's <see cref="MeetingLibraryService.MeetingAudioWarning"/>); empty otherwise.
    /// </summary>
    public string MeetingAudioWarning => ShowStop ? app.Store.MeetingAudioWarning : "";
    public bool ShowMeetingAudioWarning => MeetingAudioWarning.Length > 0;
    /// <summary>Tooltip of the warning: what to check.</summary>
    public string MeetingAudioWarningHint => Strings.MeetingAudioWarningHint;

    /// <summary>Levels arrive every <see cref="MeterInterval"/> (and with every inbox drain) from the service.</summary>
    internal void LevelsChanged()
    {
        MeLevel = Clamp(app.Store.MeLevel);
        OthersLevel = Clamp(app.Store.OthersLevel);
        OnPropertyChanged(nameof(MeLevelName)); OnPropertyChanged(nameof(OthersLevelName));
    }

    private static double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    internal void Tick() => OnPropertyChanged(nameof(ElapsedText));

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        if (InfoOpen) RefreshInfo();
    }

    /// <summary>The popover text (port of <c>recordingInfo</c>), built when it opens.</summary>
    public void RefreshInfo() => InfoLines = BuildInfo();

    /// <summary>Echo reduction from this many dB on counts as "working well" in the popover; the raw values stay out of sight.</summary>
    private const double GoodEchoCancellationDb = 10;

    internal IReadOnlyList<string> BuildInfo()
    {
        var store = app.Store;
        var lines = new List<string> { store.Status, store.SpeakerMatchingProgress };
        var selected = app.SelectedMeeting;
        if (selected != null && store.ActiveID == selected.Id && (store.IsRecording || store.IsStarting || store.IsStopping))
        {
            var settings = store.Settings;
            // Core's choice of this recording; while the start still chooses, the configured source.
            var source = store.RecordingAudioDescription.Length > 0 ? store.RecordingAudioDescription
                : settings.AutomaticMeetingAudioSource ? Strings.InfoMeetingAudioAutomatic
                : settings.AudioSourceBundleID.Length == 0 ? Strings.InfoMeetingAudioSystem
                : Strings.Format(Strings.InfoMeetingAudioApp, settings.AudioSourceBundleID);
            // The microphone that runs (Core, from the engine: "Name · Fallback (reason)"); until it is acquired, the configured one.
            var microphone = store.MicrophoneDescription.Length > 0 ? store.MicrophoneDescription : MicrophoneName(settings.MicrophoneUID);
            lines.Add(source + " · " + microphone);
            if (store.RecordingMetrics is { } metrics && metrics.State is not (AudioEngineState.Idle or AudioEngineState.Stopped))
                lines.Add(metrics.ErleDb >= GoodEchoCancellationDb ? Strings.InfoEchoCancellation : Strings.InfoEchoCancellationLimited);
            lines.Add(store.TeamsSpeakerStatus);
            foreach (var (channel, state) in ChannelStates(store.TranscriptionStates))
                lines.Add(Strings.Format(Strings.InfoChannel, channel, state));
        }
        if (selected?.RecordingGaps is { Count: > 0 } recordingGaps)
            lines.Add(Strings.Format(Strings.InfoRecordingGaps, recordingGaps.Count, Formats.Seconds(selected.RecordingGapDuration)));
        if (selected?.TranscriptionGaps is { Count: > 0 } transcriptionGaps)
        {
            lines.Add(Strings.Format(Strings.InfoTranscriptionGaps, transcriptionGaps.Count, Formats.Seconds(transcriptionGaps.Sum(gap => gap.Duration))));
            var last = transcriptionGaps[^1];
            lines.Add(Strings.Format(Strings.InfoGapFrom, Meeting.Timestamp(last.Start), last.Reason));
        }
        if (selected?.ProcessingErrors is { Count: > 0 } errors) lines.Add(Strings.Format(Strings.InfoLastIssue, errors[^1]));
        return lines.Where(line => line.Trim().Length > 0).ToList();
    }

    /// <summary>
    /// Me / Others states from Core's per-channel <see cref="MeetingLibraryService.TranscriptionStates"/> (macOS
    /// <c>transcriptionStates</c>): Me first, a channel without a state yet is left out.
    /// </summary>
    public static IReadOnlyList<(string Channel, string State)> ChannelStates(IReadOnlyDictionary<AudioSource, string> states)
    {
        var result = new List<(string Channel, string State)>();
        foreach (var (source, label) in new[] { (AudioSource.Me, Strings.SpeakerMe), (AudioSource.Others, Strings.SpeakerOthers) })
            if (states.TryGetValue(source, out var state) && !string.IsNullOrWhiteSpace(state)) result.Add((label, state));
        return result;
    }

    private string MicrophoneName(string id)
    {
        if (id.Length == 0) return Strings.SystemDefault;
        try { return app.System.Devices(AudioFlow.Capture).FirstOrDefault(device => device.Id == id)?.Name ?? Strings.SavedMicrophoneUnavailable; }
        catch (Exception) { return Strings.SavedMicrophoneUnavailable; }
    }
}
