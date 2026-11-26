using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wallfly.Core;

namespace Wallfly.App.ViewModels;

/// <summary>One chat bubble: Me on the right (#FDECEF), Others on the left (#EFF1F3), at most 75 % wide.</summary>
public sealed class ChatTurnViewModel : ObservableObject
{
    private string text = "";
    private bool final;
    private bool showsSpeaker;
    private string speakerName = "";
    private string speakerColor = Palette.Muted;
    private double topSpacing;
    private string interimText = "";

    public ChatTurnViewModel(string key, Speaker speaker, Guid? participantID)
    {
        Key = key;
        IsMe = IsMeTurn(speaker, participantID);
        ParticipantID = participantID;
    }

    /// <summary>A Me-channel turn that the microphone pass gave another voice ("Speaker k") shows like a remote turn.</summary>
    internal static bool IsMeTurn(Speaker speaker, Guid? participantID) => speaker == Speaker.Me && participantID is null;

    public string Key { get; }
    public bool IsMe { get; }
    public Guid? ParticipantID { get; }
    /// <summary>The exact source words of the turn (Core <see cref="TranscriptDisplay"/>), for "Assign this segment…".</summary>
    public IReadOnlyList<TranscriptReference> References { get; private set; } = [];
    public string Text { get => text; private set => SetProperty(ref text, value); }
    public bool IsFinal { get => final; private set { if (SetProperty(ref final, value)) OnPropertyChanged(nameof(ShowsInterim)); } }
    public bool ShowsInterim => !IsFinal;
    public string InterimText { get => interimText; private set => SetProperty(ref interimText, value); }
    /// <summary>The label with its colour dot appears at a speaker change, for Others only (as on macOS).</summary>
    public bool ShowsSpeaker { get => showsSpeaker; private set => SetProperty(ref showsSpeaker, value); }
    public string SpeakerName { get => speakerName; private set => SetProperty(ref speakerName, value); }
    public string SpeakerColor { get => speakerColor; private set => SetProperty(ref speakerColor, value); }
    public double TopSpacing { get => topSpacing; private set => SetProperty(ref topSpacing, value); }
    public string Background => IsMe ? Palette.TranscriptMe : Palette.TranscriptOthers;

    internal void Update(ChatTurn turn, Meeting meeting, bool speakerChanged, bool first, bool recording)
    {
        Text = turn.Text;
        IsFinal = turn.IsFinal;
        References = turn.References;
        InterimText = recording ? Strings.Transcribing : Strings.SegmentNotFinished;
        ShowsSpeaker = speakerChanged && !IsMe;
        TopSpacing = first ? 0 : speakerChanged ? 14 : 4;
        var participant = meeting.Participants?.FirstOrDefault(person => person.Id == turn.ParticipantID);
        // Word-level label: a Me turn the microphone pass gave another voice shows that voice ("Speaker k").
        SpeakerName = turn.ParticipantID == null ? Strings.SpeakerOthers : TranscriptDisplay.SpeakerName(meeting, turn.Speaker, turn.ParticipantID);
        SpeakerColor = IsMe ? Palette.Accent : participant == null ? Palette.Muted : Palette.SpeakerColor(participant.ColorIndex);
    }
}

/// <summary>
/// The conversation of one meeting (chat view of the transcript tab and of the live box): grouped turns, live following
/// with "Latest ↓", copy, gap warning, the speaker matching status and the speaker corrections ("Rename speaker…",
/// "Assign this segment…") through Core's <see cref="Core.Services.ISpeakerEditor"/>.
/// </summary>
public sealed class TranscriptViewModel : ObservableObject
{
    /// <summary>macOS <c>SpeakerNameEditor</c>: an empty name or one longer than this cannot be saved.</summary>
    public const int MaxSpeakerNameLength = 160;
    private static readonly TimeSpan CopiedDuration = TimeSpan.FromSeconds(1.5);
    private readonly AppViewModel app;
    private bool following = true;
    private bool copied;
    private IDisposable? copiedReset;
    private int lastCount = -1;

    public TranscriptViewModel(AppViewModel app, Meeting meeting)
    {
        this.app = app;
        Meeting = meeting;
        CopyCommand = new RelayCommand(Copy, () => Meeting.Transcript.Count > 0);
        LatestCommand = new RelayCommand(() => { Following = true; ScrollToEndRequested?.Invoke(); });
        RenameSpeakerCommand = new RelayCommand<ChatTurnViewModel>(RenameSpeaker, CanRenameSpeaker);
        AssignSegmentCommand = new RelayCommand<ChatTurnViewModel>(AssignSegment, CanAssignSegment);
        Refresh();
    }

    public Meeting Meeting { get; }
    public ObservableCollection<ChatTurnViewModel> Turns { get; } = [];
    public bool IsLive => app.IsLive(Meeting.Id) && !app.Store.IsStopping;
    public bool IsEmpty => Turns.Count == 0;
    public string EmptyTitle => IsLive ? Strings.TranscriptRecordingTitle : Strings.TranscriptEmptyTitle;
    public string EmptyDetail => IsLive ? Strings.TranscriptRecordingDetail : Strings.TranscriptEmptyDetail;
    public bool HasGaps => (Meeting.TranscriptionGaps ?? []).Count > 0;

    /// <summary>A speaker matching run is queued or running for this meeting (the status line shows a spinner).</summary>
    public bool IsMatchingSpeakers => app.Store.IsMatchingSpeakers(Meeting.Id);

    /// <summary>
    /// The diarization status line under the transcript (macOS <c>transcriptBody</c>): "Matching speakers… 40%" for the
    /// running run, "Waiting for speaker matching…" while queued, "Speaker matching failed · …" after a failure and
    /// "Speakers not detected: …" with the stored reason when the run was skipped. A completed run shows nothing.
    /// </summary>
    public string? DiarizationStatus
    {
        get
        {
            if (IsMatchingSpeakers)
            {
                if (Meeting.Diarization?.Status != Core.DiarizationStatus.Running) return Strings.SpeakerMatchingWaiting;
                return app.Store.SpeakerMatchingProgress is { Length: > 0 } progress ? progress : Strings.DiarizationRunning;
            }
            // The top level describes the Others pass; a failed microphone pass gets a line of its own.
            var others = Meeting.Diarization switch
            {
                // A run held back by a recording (after a restart, before it is queued again), or a cloud pass waiting for the
                // network (the top level says failed, so a Mac reading the file never meets "waiting").
                { Channels.Others: { Status: Core.DiarizationStatus.Waiting, WaitingFor: global::Wallfly.Speakers.DiarizationRunStates.WaitingForRecording } } =>
                    Strings.SpeakerSeparationWaitingForRecording,
                { Channels.Others.Status: Core.DiarizationStatus.Waiting } => Strings.SpeakerSeparationWaiting,
                { Status: Core.DiarizationStatus.Failed, Message: { } message } when !string.IsNullOrWhiteSpace(message) => Strings.Format(Strings.DiarizationFailed, message),
                // The Others pass was skipped, but the microphone pass separated several voices: speakers were detected.
                { Status: Core.DiarizationStatus.Skipped, Channels.Me: { Status: Core.DiarizationStatus.Completed, Retained: > 1 }, Message: { } reason }
                    when !string.IsNullOrWhiteSpace(reason) => Strings.Format(Strings.DiarizationMicrophoneOnly, reason.Trim()),
                { Status: Core.DiarizationStatus.Skipped, Message: { } reason } when !string.IsNullOrWhiteSpace(reason) => Strings.Format(Strings.DiarizationSkipped, reason.Trim()),
                { Status: Core.DiarizationStatus.Skipped } => Strings.DiarizationSkippedNoReason,
                _ => null
            };
            var microphone = Meeting.Diarization?.Channels?.Me is { Status: Core.DiarizationStatus.Failed } failed
                ? Strings.Format(Strings.DiarizationFailed, string.IsNullOrWhiteSpace(failed.Message)
                    ? Strings.Microphone
                    : Strings.Format(Strings.DiarizationMicrophoneChannel, failed.Message.Trim()))
                : null;
            return others is null ? microphone : microphone is null ? others : others + "\n" + microphone;
        }
    }

    /// <summary>"Teams speaker detection active · 3 participants" while this meeting records with Teams names.</summary>
    public string? SpeakerStatus => IsLive && app.Store.TeamsSpeakerStatus is { Length: > 0 } status ? status : null;

    /// <summary>
    /// Speaker corrections are possible: the app runs with Core's speaker editor, nothing records or processes (macOS shows
    /// the speaker menu only when not busy) and Core allows editing this meeting (not live, no library move, no quit).
    /// </summary>
    public bool CanEditSpeakers => app.Store.SpeakerEditor is { } editor && !app.Store.Busy && editor.CanEdit(Meeting.Id);

    /// <summary>Set by the view: false once the reader scrolled up, true again near the end.</summary>
    public bool Following { get => following; set { if (SetProperty(ref following, value)) OnPropertyChanged(nameof(ShowLatest)); } }
    public bool ShowLatest => IsLive && !Following;
    public bool Copied { get => copied; private set => SetProperty(ref copied, value); }

    public IRelayCommand CopyCommand { get; }
    public IRelayCommand LatestCommand { get; }
    /// <summary>"Rename speaker…" on an Others turn with a named participant: renames it in the whole meeting.</summary>
    public IRelayCommand<ChatTurnViewModel> RenameSpeakerCommand { get; }
    /// <summary>"Assign this segment…" on an Others turn: an existing participant, Others (unknown) or a new participant.</summary>
    public IRelayCommand<ChatTurnViewModel> AssignSegmentCommand { get; }

    /// <summary>New speech arrived while following a live meeting.</summary>
    public event Action? ScrollToEndRequested;

    /// <summary>A usable speaker name: trimmed, 1 to <see cref="MaxSpeakerNameLength"/> characters; otherwise null.</summary>
    public static string? SpeakerName(string? text) => text?.Trim() is { Length: > 0 and <= MaxSpeakerNameLength } name ? name : null;

    private MeetingParticipant? Participant(ChatTurnViewModel? turn) =>
        turn?.ParticipantID is Guid id ? Meeting.Participants?.FirstOrDefault(person => person.Id == id) : null;

    private bool CanRenameSpeaker(ChatTurnViewModel? turn) => turn is { IsMe: false } && Participant(turn) != null && CanEditSpeakers;

    private bool CanAssignSegment(ChatTurnViewModel? turn) => turn is { IsMe: false, References.Count: > 0 } && CanEditSpeakers;

    private void RenameSpeaker(ChatTurnViewModel? turn)
    {
        if (!CanRenameSpeaker(turn) || Participant(turn) is not { } participant) return;
        var name = SpeakerName(app.Dialogs.AskSpeakerName(participant.Name));
        // The dialog is modal: a recording or a quit may have started meanwhile, so the editor is asked again.
        if (name == null || name == participant.Name || !CanEditSpeakers) return;
        // Core saves first and only then updates the live meeting; a failed save shows the banner and changes nothing.
        app.Store.SpeakerEditor!.RenameParticipant(Meeting.Id, participant.Id, name);
    }

    private void AssignSegment(ChatTurnViewModel? turn)
    {
        if (turn == null || !CanAssignSegment(turn)) return;
        var participants = (Meeting.Participants ?? []).Select(person => new SpeakerOption(person.Id, person.Name, Palette.SpeakerColor(person.ColorIndex))).ToList();
        if (app.Dialogs.AskSpeakerAssignment(turn.Text, participants) is not { } choice || !CanEditSpeakers) return;
        var editor = app.Store.SpeakerEditor!;
        if (choice.NewName != null)
        {
            if (SpeakerName(choice.NewName) is { } name) editor.AddParticipant(Meeting.Id, name, turn.References);
        }
        else editor.AssignTranscript(Meeting.Id, turn.References, choice.ParticipantID);
    }

    private void Copy()
    {
        app.Dialogs.CopyText(TranscriptGrouping.CopyableTranscript(Meeting));
        Copied = true;
        copiedReset?.Dispose();
        copiedReset = app.Scheduler.Schedule(CopiedDuration, () => Copied = false);
    }

    /// <summary>Re-groups the transcript and updates the bubbles in place, so the list keeps its scroll position.</summary>
    public void Refresh()
    {
        var turns = TranscriptGrouping.Conversation(Meeting);
        var recording = app.Store.IsRecording && app.Store.ActiveID == Meeting.Id;
        for (var index = 0; index < turns.Count; index++)
        {
            var turn = turns[index];
            var changed = index == 0 || turns[index - 1].Speaker != turn.Speaker || turns[index - 1].ParticipantID != turn.ParticipantID;
            if (index < Turns.Count && (Turns[index].Key != turn.Key || Turns[index].IsMe != ChatTurnViewModel.IsMeTurn(turn.Speaker, turn.ParticipantID) || Turns[index].ParticipantID != turn.ParticipantID))
                Turns[index] = new ChatTurnViewModel(turn.Key, turn.Speaker, turn.ParticipantID);
            else if (index >= Turns.Count) Turns.Add(new ChatTurnViewModel(turn.Key, turn.Speaker, turn.ParticipantID));
            Turns[index].Update(turn, Meeting, changed, index == 0, recording);
        }
        while (Turns.Count > turns.Count) Turns.RemoveAt(Turns.Count - 1);
        var grew = lastCount >= 0 && Meeting.Transcript.Count != lastCount;
        lastCount = Meeting.Transcript.Count;
        OnPropertyChanged(nameof(IsLive)); OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDetail)); OnPropertyChanged(nameof(HasGaps)); OnPropertyChanged(nameof(DiarizationStatus));
        OnPropertyChanged(nameof(IsMatchingSpeakers)); OnPropertyChanged(nameof(SpeakerStatus)); OnPropertyChanged(nameof(CanEditSpeakers));
        OnPropertyChanged(nameof(ShowLatest));
        CopyCommand.NotifyCanExecuteChanged();
        RenameSpeakerCommand.NotifyCanExecuteChanged();
        AssignSegmentCommand.NotifyCanExecuteChanged();
        if (grew && Following && recording) ScrollToEndRequested?.Invoke();
    }

    /// <summary>The Teams speaker status of the running recording changed (it does not raise the store's Updated).</summary>
    internal void SpeakerStatusChanged() => OnPropertyChanged(nameof(SpeakerStatus));
}
