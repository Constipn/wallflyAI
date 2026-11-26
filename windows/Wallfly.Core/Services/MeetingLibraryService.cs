using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Wallfly.Ai;
using Wallfly.Audio;
using Wallfly.Core.Credentials;
using Wallfly.Core.Json;
using Wallfly.Core.Persistence;
using Wallfly.Core.Text;
using Wallfly.Transcription;

namespace Wallfly.Core.Services;

/// <summary>
/// The one piece of app state, owned by the UI thread: library, folders, settings, keys, autosave, and
/// the hand-off to <see cref="RecordingSession"/> and <see cref="PostProcessingService"/>. Producers
/// never touch this class directly; they fill the recording inboxes, which a 100 ms timer drains here.
/// Replaces the first port's <c>MeetingStore</c>. The optional <see cref="LibraryServiceHooks"/> add the Teams
/// call source (automatic meeting audio, auto-stop), live speaker names, speaker matching and speaker editing.
/// </summary>
/// <remarks>
/// Split by topic: this file holds the library, settings, AI and quit; <c>MeetingLibraryService.Recording.cs</c> the
/// recording lifecycle; <c>MeetingLibraryService.Storage.cs</c> audio retention, the transcript mirror, moving the library
/// and the speaker hooks.
/// </remarks>
public sealed partial class MeetingLibraryService : Observable, IDisposable, IMeetingHost
{
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan QuitBudget = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DrainInterval = TimeSpan.FromMilliseconds(100);

    public ObservableCollection<Meeting> Meetings { get; } = [];
    /// <summary>The current library; replaced by <see cref="MoveLibraryAsync"/>.</summary>
    public MeetingLibrary Library { get; private set; }
    /// <summary>The writer of <see cref="Library"/>; replaced together with it.</summary>
    public MeetingWriter Writer { get; private set; }
    public ICredentialStore Credentials { get; }
    public IAudioDeviceCatalog Devices { get; }
    public AppSettings Settings { get; private set; } = new();
    public FolderCatalog Catalog { get; private set; } = new();
    public bool FoldersAvailable { get; private set; } = true;
    public string ApiKey { get; private set; } = "";
    /// <summary>Deepgram transcription key (Credential Manager account <c>deepgram-transcription</c>, else DEEPGRAM_API_KEY / DEEPGRAM_KEY).</summary>
    public string DeepgramKey { get; private set; } = "";
    /// <summary>Anthropic's own key slot; OpenAI and the custom API use <see cref="ApiKey"/>, as on the Mac.</summary>
    public string AnthropicApiKey { get; private set; } = "";
    public string EnhancementApiKey => Settings.Provider == EnhancementProvider.Anthropic ? AnthropicApiKey : ApiKey;
    /// <summary>Null when "Enhance notes" can run; otherwise the reason shown next to the setup action.</summary>
    public string? EnhancementConfigurationIssue => PostProcessingService.ConfigurationIssue(Settings, EnhancementApiKey);
    /// <summary>The banner action for the current <see cref="Error"/>; null once the message changed.</summary>
    public EnhancementRecovery? Recovery => recovery != null && recovery.Message == Error ? recovery : null;
    public Guid? ActiveID { get; private set; }
    public Guid? ProcessingID { get; private set; }
    public bool IsRecording { get; private set; }
    public bool IsStarting { get; private set; }
    public bool IsStopping { get; private set; }
    /// <summary><see cref="MoveLibraryAsync"/> is copying and verifying the library.</summary>
    public bool IsMovingLibrary { get; private set; }
    public bool Busy => IsRecording || IsStarting || IsStopping || ProcessingID != null || IsMovingLibrary;

    private string status = CoreMessages.Ready;
    private string error = "";
    private double meLevel;
    private double othersLevel;
    private bool presentationMode;
    public string Status { get => status; private set => Set(ref status, value); }
    public string Error { get => error; set => Set(ref error, value); }
    public double MeLevel { get => meLevel; private set => Set(ref meLevel, value); }
    public double OthersLevel { get => othersLevel; private set => Set(ref othersLevel, value); }
    public bool PresentationMode { get => presentationMode; set => Set(ref presentationMode, value && IsRecording && !IsStopping); }

    public event Action? Updated;
    public event Action<Guid>? EnhancementCompleted;
    /// <summary>Raised after AI processing when the meeting has personal suggestions awaiting "Accept" / "Not my task".</summary>
    public event Action<Guid>? TaskReviewRequested;
    private EnhancementRecovery? recovery;

    private readonly Func<Action, Task> dispatch;
    private readonly Func<string, AppSettings, IChatProvider> providerFactory;
    /// <summary>Only set by tests and the smoke mode: an injected chunk client replaces the factory-built OpenAI client.</summary>
    private readonly Func<string, AppSettings, ITranscriptionClient>? customTranscription;
    private readonly Func<AppSettings, IAudioEngine> engineFactory;
    private readonly Func<AudioSource, ITranscriptionClient, Guid, ITranscriptionPipeline>? customPipeline;
    private readonly TranscriptionPipelineFactory pipelines;
    private readonly PostProcessingService postProcessing;
    private readonly LibraryServiceHooks hooks;
    private readonly MeetingAudioRouting routing;
    private readonly Func<string, int?> resolveProcess;
    private readonly Func<double> monotonic;
    private readonly TimeProvider time;
    private readonly WriteCadence? cadence;
    private readonly LibraryPointer pointer;
    private CancellationTokenSource? intelligenceCancellation;
    /// <summary>The waiting meeting chat questions, by meeting.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> chatRequests = new();
    private bool closing;

    public MeetingLibraryService(
        MeetingLibrary library,
        Func<Action, Task> dispatch,
        Func<string, AppSettings, IChatProvider>? providerFactory = null,
        Func<string, AppSettings, ITranscriptionClient>? transcriptionFactory = null,
        Func<AppSettings, IAudioEngine>? engineFactory = null,
        Func<AudioSource, ITranscriptionClient, Guid, ITranscriptionPipeline>? pipelineFactory = null,
        IAudioDeviceCatalog? devices = null,
        ICredentialStore? credentials = null,
        bool loadCredential = true,
        TimeProvider? time = null,
        WriteCadence? cadence = null,
        TranscriptionPipelineFactory? pipelines = null,
        LibraryServiceHooks? hooks = null)
    {
        Library = library;
        this.dispatch = dispatch;
        this.hooks = hooks ?? new LibraryServiceHooks();
        this.providerFactory = providerFactory ?? ((key, settings) => PostProcessingService.CreateProvider(settings, key));
        customTranscription = transcriptionFactory;
        // WallflyAudio.dll is the only capture path: a missing or incompatible DLL makes every start fail
        // visibly (NotSupportedException -> RecordingEngineUnavailable), never fall back to another engine.
        this.engineFactory = engineFactory ?? (_ => NativeAudioEngine.Create());
        this.pipelines = pipelines ?? new();
        customPipeline = pipelineFactory;
        Devices = devices ?? new NativeAudioDeviceCatalog();
        Credentials = credentials ?? new CredentialStore();
        this.time = time ?? TimeProvider.System;
        this.cadence = cadence;
        pointer = this.hooks.LibraryPointer ?? new LibraryPointer();
        resolveProcess = this.hooks.ResolveProcess ?? ProcessLookup.Resolve;
        monotonic = this.hooks.MonotonicClock ?? ProcessLookup.MonotonicSeconds;
        routing = new MeetingAudioRouting(this.hooks.TeamsCalls, resolveProcess, monotonic, this.hooks.Timings);
        mirror.Reported += (id, message) => _ = this.dispatch(() => SetMirrorError(id, message));
        Writer = CreateWriter(library);
        postProcessing = new(() => this.providerFactory(EnhancementApiKey, Settings), () => Settings);

        foreach (var meeting in Library.Load()) Attach(meeting);
        Error = string.Join('\n', Library.LoadFailures.Select(failure => failure.Message));
        try { Settings = Library.LoadSettings(); } catch (Exception) { Error += "\n" + CoreMessages.SettingsUnreadable; }
        try { Catalog = Library.LoadFolders(); } catch (Exception) { FoldersAvailable = false; Error += "\n" + CoreMessages.FoldersUnreadable; }
        mirror.Enabled = Settings.TranscriptMirrorEnabled;
        if (loadCredential)
        {
            try { ApiKey = Credentials.Read(CredentialAccount.Enhancement); DeepgramKey = Credentials.Read(CredentialAccount.DeepgramTranscription); AnthropicApiKey = Credentials.Read(CredentialAccount.AnthropicEnhancement); }
            catch (Exception) { Error += "\n" + CoreMessages.CredentialUnreadable; }
        }
        foreach (var client in this.hooks.Clients())
        {
            try { client.Attach(this); }
            catch (Exception failure) { Trace.TraceError("Wallfly: attaching a hook failed: " + failure.Message); }
        }
        // Spec 8: deletion decisions saved before a crash or a failed final write are carried out now.
        foreach (var meeting in Meetings.Where(meeting => meeting.AudioDeletionReady == true).ToList()) CleanupProcessedAudio(meeting.Id);
    }

    private MeetingWriter CreateWriter(MeetingLibrary library)
    {
        var writer = new MeetingWriter(library, time, cadence);
        writer.WriteFailed += (_, _) => _ = dispatch(() => Error = CoreMessages.SaveFailed);
        var root = library.Root;
        // The transcript mirror runs after each successful internal save; its failures never fail that save.
        writer.Saved += (id, bytes) => mirror.OnSaved(id, bytes, root);
        return writer;
    }

    private void Attach(Meeting meeting)
    {
        meeting.PropertyChanged += MeetingChanged;
        Meetings.Add(meeting);
    }

    private void MeetingChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is Meeting meeting && args.PropertyName is nameof(Meeting.Title) or nameof(Meeting.Notes)
            or nameof(Meeting.EnhancedNotes) or nameof(Meeting.TranscriptText) or nameof(Meeting.Tasks)
            or nameof(Meeting.AudioTranscriptionComplete))
            MarkDirty(meeting);
    }

    /// <summary>Takes a snapshot of the meeting and leaves the write cadence to <see cref="MeetingWriter"/>.</summary>
    public void MarkDirty(Meeting meeting) => Writer.Mark(meeting);

    private Meeting? Find(Guid id) => Meetings.FirstOrDefault(meeting => meeting.Id == id);

    private void Notify()
    {
        Changed(nameof(Busy)); Changed(nameof(IsRecording)); Changed(nameof(IsStarting));
        Changed(nameof(IsStopping)); Changed(nameof(ActiveID)); Changed(nameof(ProcessingID));
        Changed(nameof(IsMovingLibrary));
        Updated?.Invoke();
    }

    /// <summary>Barrier: blocks until everything dirty is on disk. Used before navigation, create, stop, delete, enhance and quit.</summary>
    public bool Flush() => Flush(FlushTimeout);

    public bool Flush(TimeSpan timeout)
    {
        Tick();
        if (Writer.Flush(timeout)) return true;
        Error = CoreMessages.SaveFailed;
        return false;
    }

    public Meeting CreateMeeting(Guid? folderID = null)
    {
        if (Busy) throw new InvalidOperationException(CoreMessages.FinishCurrentMeeting);
        if (!Flush()) throw new IOException(CoreMessages.UnsavedChanges);
        var meeting = new Meeting();
        // Spec 8: only meetings created while mirroring is on are mirrored, always into the folder chosen then.
        if (Settings.TranscriptMirrorEnabled && Settings.TranscriptMirrorFolder is { Path.Length: > 0 } folder)
            meeting.TranscriptMirror = new() { Path = folder.Path, Bookmark = folder.Bookmark };
        Attach(meeting);
        Persist(meeting);
        if (folderID != null) MoveMeeting(meeting.Id, folderID);
        Updated?.Invoke();
        return meeting;
    }

    private bool Persist(Meeting meeting) { MarkDirty(meeting); return Flush(); }

    /// <param name="deepgramKey">Null leaves the stored Deepgram key unchanged; empty removes it.</param>
    /// <param name="anthropicKey">Null leaves the stored Anthropic key unchanged.</param>
    public void SaveSettings(AppSettings settings, string key, string? deepgramKey = null, string? anthropicKey = null)
    {
        if (Busy) throw new InvalidOperationException(CoreMessages.SettingsWhileBusy);
        if (settings.Provider != EnhancementProvider.Disabled && string.IsNullOrWhiteSpace(settings.Model)) throw new InvalidOperationException(CoreMessages.EmptyModelNames);
        Credentials.Write(CredentialAccount.Enhancement, key);
        if (deepgramKey != null)
        {
            Credentials.Write(CredentialAccount.DeepgramTranscription, deepgramKey);
            DeepgramKey = deepgramKey.Trim();
        }
        if (anthropicKey != null) { Credentials.Write(CredentialAccount.AnthropicEnhancement, anthropicKey); AnthropicApiKey = anthropicKey.Trim(); }
        Library.SaveSettings(settings);
        ApiKey = key.Trim();
        Settings = settings;
        mirror.Enabled = settings.TranscriptMirrorEnabled;
        if (!settings.TranscriptMirrorEnabled) ClearMirrorErrors();
        Updated?.Invoke();
    }

    /// <summary>
    /// "Enhance notes" (one request for notes, personal to-do suggestions and a title) or, with
    /// <paramref name="tasksOnly"/>, "Retry to-do recognition" for a meeting whose recognition is pending or failed.
    /// Missing keys or models surface as configuration errors with the "Open settings" action.
    /// </summary>
    public async Task EnhanceAsync(Guid meetingID, bool tasksOnly = false)
    {
        if (Settings.Provider == EnhancementProvider.Disabled) { Status = AiMessages.EnhancementTurnedOffStatus; return; }
        if (Busy || closing) return;
        var meeting = Meetings.First(item => item.Id == meetingID);
        if (string.IsNullOrWhiteSpace(meeting.TranscriptText) && !tasksOnly) { Error = CoreMessages.NoTranscriptText; return; }
        if (tasksOnly && meeting.TaskExtraction is not (TaskExtractionState.Pending or TaskExtractionState.Failed)) return;
        if (!Flush()) return;
        ProcessingID = meetingID;
        intelligenceCancellation = new();
        var token = intelligenceCancellation.Token;
        var enhancedSaved = false;
        Status = CoreMessages.LookingForTasks;
        Notify();
        var callbacks = new PostProcessingCallbacks(
            message => { if (!closing && !token.IsCancellationRequested) Status = message; },
            Persist,
            message => Error = message);
        try
        {
            enhancedSaved = await postProcessing.EnhanceAsync(meeting, tasksOnly, callbacks, token).ConfigureAwait(true);
            if (!tasksOnly) Status = CoreMessages.EnhancementDone;
        }
        catch (OperationCanceledException) { Status = CoreMessages.EnhancementCancelled; }
        catch (Exception failure)
        {
            var message = failure is InvalidOperationException or InvalidDataException ? failure.Message : CoreMessages.EnhancementFailed;
            // The banner reads Recovery when Error changes, so the action is set first.
            recovery = PostProcessingService.Recovery(failure, message, meetingID, tasksOnly);
            Error = message;
            Status = CoreMessages.EnhancementNeedsAttention;
        }
        finally
        {
            intelligenceCancellation.Dispose(); intelligenceCancellation = null; ProcessingID = null;
            Notify();
            if (enhancedSaved && !closing) EnhancementCompleted?.Invoke(meetingID);
            RequestTaskReview(meetingID);
        }
    }

    /// <summary>A meeting chat question is waiting for its answer (at most one per meeting).</summary>
    public bool IsAskingAboutMeeting(Guid meetingID) => chatRequests.ContainsKey(meetingID);

    /// <summary>
    /// Windows addition: answers <paramref name="question"/> about a finished meeting from its typed notes, transcript and
    /// earlier chat, with the provider and model of the enhancement. Only a complete answer is stored: question and answer
    /// are appended to <see cref="Meeting.Chat"/> together and saved; a failure or cancellation stores nothing. It does not
    /// use <see cref="Busy"/> or <see cref="ProcessingID"/> (the notes are never changed, so it may run beside an
    /// enhancement); one question per meeting runs at a time. Configuration problems throw an <see cref="AiError"/> of kind
    /// <see cref="AiErrorKind.Configuration"/> ("Open settings"); every other failure is rethrown for the chat to show.
    /// </summary>
    public async Task<string> AskAboutMeetingAsync(Guid meetingID, string question, CancellationToken cancellationToken = default)
    {
        var meeting = Find(meetingID) ?? throw new InvalidOperationException(CoreMessages.ChatMeetingMissing);
        var text = (question ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("The question is empty.", nameof(question));
        if (closing) throw new OperationCanceledException(cancellationToken);
        if (ActiveID == meetingID && (IsRecording || IsStarting || IsStopping)) throw new InvalidOperationException(CoreMessages.ChatWhileRecording);
        if (EnhancementConfigurationIssue is { } issue) throw AiError.Configuration(issue);
        var history = (meeting.Chat ?? []).Select(message => new MeetingChatTurn(message.FromUser, message.Text)).ToList();
        var input = new MeetingChatInput(meeting.Title, meeting.Notes, meeting.TranscriptText, history, text);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!chatRequests.TryAdd(meetingID, cancellation)) throw new InvalidOperationException(CoreMessages.ChatAlreadyAsking);
        var asked = time.GetUtcNow();
        IChatProvider? provider = null;
        try
        {
            provider = providerFactory(EnhancementApiKey, Settings);
            var answer = await MeetingChatAnswerer.AskAsync(provider, input, cancellation.Token).ConfigureAwait(true);
            cancellation.Token.ThrowIfCancellationRequested();
            // The meeting may have been deleted while the model answered.
            if (Find(meetingID) is not { } current) throw new OperationCanceledException(cancellation.Token);
            var chat = current.Chat ??= [];
            chat.Add(new() { Role = ChatMessage.UserRole, Text = text, At = asked });
            chat.Add(new() { Role = ChatMessage.AssistantRole, Text = answer, At = time.GetUtcNow() });
            MarkDirty(current);
            return answer;
        }
        finally
        {
            chatRequests.TryRemove(meetingID, out _);
            (provider as IDisposable)?.Dispose();
        }
    }

    /// <summary>"New chat": removes the stored chat of the meeting and cancels a question that is still waiting.</summary>
    public void ClearChat(Guid meetingID)
    {
        if (chatRequests.TryGetValue(meetingID, out var pending)) CancelChatRequest(pending);
        if (Find(meetingID) is not { Chat: not null } meeting) return;
        meeting.Chat = null;
        MarkDirty(meeting);
    }

    private static void CancelChatRequest(CancellationTokenSource request)
    {
        try { request.Cancel(); }
        catch (ObjectDisposedException) { }   // the answer arrived meanwhile
    }

    /// <summary>Personal to-do suggestions of the meeting that still need "Accept" or "Not my task".</summary>
    public IReadOnlyList<MeetingTask> PendingTasks(Meeting meeting) => PostProcessingService.PendingTasks(meeting, Settings.TaskOwnerName);

    /// <summary>Asks the UI for "Add to my to-dos?" when the meeting has pending suggestions.</summary>
    public void RequestTaskReview(Guid meetingID)
    {
        if (closing || Meetings.FirstOrDefault(item => item.Id == meetingID) is not { } meeting || PendingTasks(meeting).Count == 0) return;
        TaskReviewRequested?.Invoke(meetingID);
    }

    /// <summary>"Accept" or "Not my task": saved at once; a failed save leaves the suggestion pending.</summary>
    public void ReviewTask(Guid meetingID, Guid taskID, bool accept)
    {
        if (Busy || Meetings.FirstOrDefault(item => item.Id == meetingID) is not { } meeting) return;
        if (PostProcessingService.Review(meeting, taskID, accept) is not { } task) return;
        try { Writer.WriteNow(meeting); Updated?.Invoke(); }
        catch (Exception failure) { task.Review = null; Error = AiMessages.TaskDecisionNotSavedPrefix + failure.Message; }
    }

    public bool CommitCatalog(FolderCatalog updated)
    {
        // The folder file is copied by a library move; a change made meanwhile would stay behind in the old library.
        if (!FoldersAvailable || IsMovingLibrary) return false;
        try { Library.SaveFolders(updated); Catalog = updated; Updated?.Invoke(); return true; }
        catch (Exception) { Error = CoreMessages.FolderSaveFailed; return false; }
    }

    public void MoveMeeting(Guid meetingID, Guid? folderID)
    {
        if (!Meetings.Any(meeting => meeting.Id == meetingID) || folderID != null && !Catalog.Folders.Any(folder => folder.Id == folderID)) return;
        var updated = WallflyJson.Copy(Catalog);
        foreach (var key in updated.Assignments.Keys.Where(key => string.Equals(key, meetingID.ToString(), StringComparison.OrdinalIgnoreCase)).ToArray())
            updated.Assignments.Remove(key);
        if (folderID != null) updated.Assignments[meetingID.ToString("D").ToUpperInvariant()] = folderID.Value;
        CommitCatalog(updated);
    }

    public void SetTaskCompleted(Guid meetingID, Guid taskID, bool completed)
    {
        var meeting = Meetings.First(item => item.Id == meetingID);
        var task = meeting.Tasks!.First(item => item.Id == taskID);
        var previous = task.CompletedAt;
        task.IsCompleted = completed;
        try { Writer.WriteNow(meeting); Updated?.Invoke(); }
        catch (Exception) { task.CompletedAt = previous; Error = CoreMessages.TaskSaveFailed; }
    }

    public void DeleteMeeting(Guid id)
    {
        if (Busy || !Flush()) return;
        var meeting = Meetings.First(item => item.Id == id);
        try
        {
            Writer.Forget(id);
            Library.Delete(meeting);
            meeting.PropertyChanged -= MeetingChanged;
            Meetings.Remove(meeting);
            MoveAssignmentAfterDelete(id);
            SetMirrorError(id, null);
            AudioRemoved(id);
            Updated?.Invoke();
        }
        catch (Exception) { Error = CoreMessages.DeleteFailed; }
    }

    private void MoveAssignmentAfterDelete(Guid id)
    {
        var updated = WallflyJson.Copy(Catalog);
        foreach (var key in updated.Assignments.Keys.Where(key => string.Equals(key, id.ToString(), StringComparison.OrdinalIgnoreCase)).ToArray())
            updated.Assignments.Remove(key);
        CommitCatalog(updated);
    }

    /// <summary>
    /// Save and quit: at most three seconds; pending AI requests and speaker matching are dropped, a starting recording is
    /// abandoned, a running one is stopped within the budget. False keeps the app open (a save failed, the library is
    /// moving, or a stop did not finish in time); the service then works as before.
    /// </summary>
    public async Task<bool> CloseAsync()
    {
        if (IsMovingLibrary) { Error = CoreMessages.WaitForLibraryMove; return false; }
        if (!Flush()) return false;
        closing = true;
        intelligenceCancellation?.Cancel();
        foreach (var request in chatRequests.Values) CancelChatRequest(request);
        StopWatches();
        foreach (var step in hooks.PostRecordingSteps) step.Cancel();
        if (IsStarting && startFinished is { } starting)
        {
            startCancellation?.Cancel();
            try { await starting.Task.WaitAsync(QuitBudget).ConfigureAwait(true); }
            catch (TimeoutException) { CancelClose(); return false; }
        }
        if (IsStopping)
        {
            stopCancellation?.Cancel();
            try { if (stopFinished != null) await stopFinished.Task.WaitAsync(QuitBudget).ConfigureAwait(true); }
            catch (TimeoutException) { CancelClose(); return false; }
        }
        else if (IsRecording) await StopRecordingAsync(QuitBudget, RecordingStopReasons.Quit).ConfigureAwait(true);
        if (!Flush()) { CancelClose(); return false; }
        return true;
    }

    /// <summary>
    /// The quit was called off after <see cref="CloseAsync"/> (the user cancelled the session end, or the shutdown was
    /// vetoed): the service accepts work again, abandoned speaker matching is queued again, and meetings stopped during the
    /// quit get their post-recording steps.
    /// </summary>
    public void CancelClose()
    {
        if (!closing) return;
        closing = false;
        foreach (var step in hooks.PostRecordingSteps) step.Resume();
        var deferred = deferredAfterStop.ToList();
        deferredAfterStop.Clear();
        foreach (var (meetingID, complete) in deferred) AfterStop(meetingID, complete);
    }

    public void Dispose()
    {
        StopWatches();
        drainTimer?.Dispose();
        levelTimer?.Dispose();
        healthTimer?.Dispose();
        Writer.Dispose();
        hooks.Dispose();
    }

    // ---- IMeetingHost (the hooks' view of the service) ------------------------------------------------------------

    IReadOnlyList<Meeting> IMeetingHost.Meetings => Meetings;
    Meeting? IMeetingHost.Find(Guid meetingID) => Find(meetingID);
    Guid? IMeetingHost.RecordingMeetingID => IsRecording || IsStarting || IsStopping ? ActiveID : null;
    bool IMeetingHost.IsBusy => Busy;
    bool IMeetingHost.IsClosing => closing;
    DateTimeOffset IMeetingHost.Now => time.GetUtcNow();
    string IMeetingHost.ResolveAudioPath(string stored) => Library.ResolveAudioPath(stored);
    bool IMeetingHost.Persist(Meeting meeting) => Persist(meeting);
    bool IMeetingHost.SaveEdited(Meeting live, Meeting edited, string failurePrefix) => SaveEdited(live, edited, failurePrefix);
    void IMeetingHost.ReportError(string message) => Error = message;
    Task IMeetingHost.DispatchAsync(Action action) => dispatch(action);
    void IMeetingHost.AudioProcessed(Guid meetingID) => MarkAudioReadyForDeletion(meetingID);
    void IMeetingHost.StateChanged()
    {
        Changed(nameof(SpeakerMatchingProgress));
        Updated?.Invoke();
    }
}
