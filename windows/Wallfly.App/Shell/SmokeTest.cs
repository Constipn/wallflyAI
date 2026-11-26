using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wallfly.App.Controls;
using Wallfly.App.Dialogs;
using Wallfly.App.ViewModels;
using Wallfly.Audio;
using Wallfly.Core;
using Wallfly.Core.Json;
using Wallfly.Core.Services;
using Wallfly.Teams;

namespace Wallfly.App.Shell;

/// <summary>
/// <c>Wallfly.exe --smoke-test --data-dir &lt;dir&gt;</c>: drives the real windows through the view models with a fake
/// audio engine, transcription and AI provider (no devices, no network), opens every window and view once and saves
/// PNG screenshots into <c>&lt;dir&gt;\smoke\</c>. Writes <c>ui-smoke-result.txt</c> and exits 0 on success, 1 on failure.
/// </summary>
internal sealed class SmokeTest(AppShell shell)
{
    private readonly string shots = Path.Combine(shell.Root, "smoke");
    private readonly List<string> log = [];

    public async Task RunAsync()
    {
        var resultPath = Path.Combine(shell.Root, "ui-smoke-result.txt");
        Directory.CreateDirectory(shots);
        var app = shell.ViewModel;
        var store = shell.Store;
        var window = shell.Window;
        var dialogs = (SmokeDialogService)shell.Dialogs;
        try
        {
            await Idle();
            WriteIconReport();

            // Empty library: "My to-dos" with the introduction card.
            app.ShowTodosCommand.Execute(null);
            await Idle();
            Check(app.DetailMode == DetailMode.Todos && app.Todos.ShowIntroduction && !app.ListVisible, "To-dos with introduction card");
            await Shot("01-todos-introduction");

            // "Load example" opens the editor in focus mode; without a key the action reads "Set up enhancement…".
            app.Todos.LoadExampleCommand.Execute(null);
            await Idle();
            var example = app.SelectedMeeting ?? throw new InvalidOperationException("Load example selected nothing");
            Check(app.IsEditor && app.FocusMode && !app.SidebarVisible && !app.ListVisible, "Load example opens the focus-mode editor");
            Check(app.Current!.EnhanceButtonText == Strings.SetUpEnhancement, "Unconfigured enhancement reads 'Set up enhancement…'");
            store.SaveSettings(new AppSettings { TranscriptionProvider = TranscriptionProvider.OpenAi, OnboardingCompleted = true }, "ui-smoke-test-key", "", "");
            await Idle();
            Check(app.Current!.EnhanceButtonText == Strings.EnhanceNotes, "Configured enhancement reads 'Enhance notes'");
            await Shot("02-editor-my-notes");

            // Folders, a finished meeting with enhanced notes, speakers, tasks and gaps, and a plain one.
            var product = AddFolder(store, "Product decisions", "briefcase.fill", FolderColor.Orange);
            var research = AddFolder(store, "Research", "lightbulb.fill", FolderColor.Teal);
            app.MoveMeeting(example.Id, product.Id);
            var review = AddReviewMeeting(store);
            app.MoveMeeting(review.Id, research.Id);
            var weekly = store.CreateMeeting();
            weekly.Title = "Weekly sync";
            weekly.Notes = "Short round of updates.\nNo decisions.";
            store.Flush();
            app.Refresh(contentChanged: true);

            // Single click: the preview keeps sidebar and list.
            app.FocusMode = false;
            app.SelectScope(LibraryScope.All);
            app.PreviewMeeting(example.Id);
            await Idle();
            Check(app.DetailMode == DetailMode.Preview && app.SidebarVisible && app.ListVisible && !app.IsEditor && !app.FocusMode, "Single click previews with sidebar and list");
            Check(app.Library.Rows.Count == 3 && app.Library.Rows.Any(row => row.IsRecording == false && row.Folder != null), "Rows with folder badges");
            await Shot("03-library-preview");
            app.BackToListCommand.Execute(null);
            await Idle();
            Check(app.DetailMode == DetailMode.Empty, "Without a selection the detail shows 'Room for good thoughts'");
            await Shot("03b-empty-detail");
            app.PreviewMeeting(example.Id);
            await Idle();
            Check(window.ListView.List.SelectedItem is MeetingRowViewModel selectedRow && selectedRow.Id == example.Id && window.ListView.List.SelectedIndex >= 0, "The previewed meeting is selected in the list");

            // Search: excerpts, highlight, "Match i of n".
            app.Library.SearchText = "pilot";
            await WaitFor(() => !app.Library.IsSearching && app.Library.Results.Count > 0, "search results");
            await Idle();
            Check(app.Preview.HasHits && app.Preview.MatchText.StartsWith(Strings.Format(Strings.MatchOf, 1, "").Trim(), StringComparison.Ordinal), "Preview shows 'Match 1 of n'");
            Check(app.Library.Rows.Any(row => row.Snippets.Count > 0 && row.Height > MeetingRowViewModel.BaseHeight), "Search excerpts add 70 px per row");
            await Shot("04-search-results");
            var firstHit = app.Preview.HitIndex;
            app.Preview.NextMatchCommand.Execute(null);
            Check(app.Preview.HitCount() < 2 || app.Preview.HitIndex != firstHit, "Next match moves");
            app.Library.SearchText = "";
            await Idle();

            // Editor tabs of a finished meeting.
            app.OpenEditor(review.Id);
            await Idle();
            Check(app.Current!.CurrentTab == NoteTab.Enhanced, "Meetings with enhanced notes open on 'Enhanced notes'");
            await Shot("05-editor-enhanced-notes");
            app.SelectTabCommand.Execute("Transcript");
            await Idle();
            Check(app.Current!.CurrentTab == NoteTab.Transcript && app.Current.Transcript.Turns.Count >= 3, "Transcript tab with the chat");
            await Shot("06-editor-transcript-tab");
            dialogs.Calls.Clear();
            app.Current.Transcript.CopyCommand.Execute(null);
            Check(dialogs.Clipboard.Contains(Strings.InterimMarker, StringComparison.Ordinal) && dialogs.Clipboard.Contains("Anna", StringComparison.Ordinal), "Copy transcript");
            await RunSpeakersAsync(review);

            // Enhancement: progress, locked notes, automatic switch to "Enhanced notes".
            app.OpenEditor(example.Id);
            await Idle();
            Check(app.Current!.CurrentTab == NoteTab.Notes, "Meetings without enhanced notes open on 'My notes'");
            shell.SmokeProvider!.DelayMilliseconds = 600;
            app.Current.EnhanceCommand.Execute(null);
            await WaitFor(() => app.Current.IsEnhancing, "enhancement start");
            await Idle();
            Check(app.Current.IsLocked && window.EditorView.Notes.IsReadOnly, "Notes are locked while enhancing");
            await Shot("07-editor-enhancing");
            await WaitFor(() => !store.Busy, "enhancement end");
            await Idle();
            Check(app.Current.CurrentTab == NoteTab.Enhanced && example.EnhancedNotes.Length > 0 && app.Current.EnhanceButtonText == Strings.EnhanceAgain, "After enhancing the editor shows 'Enhanced notes'");
            shell.SmokeProvider.DelayMilliseconds = 0;

            // A failed enhancement keeps the result and shows the banner with exactly one action ("Try again").
            shell.SmokeProvider.FailEnhancement = true;
            var previous = example.EnhancedNotes;
            await app.Current.EnhanceAgainCommand.ExecuteAsync(null);
            await WaitFor(() => !store.Busy, "failed enhancement");
            Check(app.HasError && app.ErrorActionText == Strings.ErrorTryAgain && example.EnhancedNotes == previous, "Error banner with 'Try again', result kept");
            await Shot("07b-error-banner");
            shell.SmokeProvider.FailEnhancement = false;
            app.DismissErrorCommand.Execute(null);

            // Recording: live box, meters, (i) popover.
            app.SelectTabCommand.Execute("Notes");
            await app.StartRecordingCommand.ExecuteAsync(null);
            await WaitFor(() => store.IsRecording, "recording start");
            Check(!app.Current.IsTranscriptTabAvailable && app.Recording.ShowStop && app.Recording.ShowActivity, "Live: no transcript tab, stop and meters");
            app.ToggleTranscriptCommand.Execute(null);
            shell.SmokeEngine!.EmitLevels();
            await WaitFor(() => app.Recording.MeLevel > 0 && app.Recording.OthersLevel > 0, "level meters");
            var session = Guid.NewGuid();
            example.Upsert(new TranscriptSegment { Speaker = Speaker.Me, Offset = 190, Duration = 3, Text = "Let's also check the onboarding numbers before Friday.", IsFinal = true, SessionID = session });
            example.Upsert(new TranscriptSegment { Speaker = Speaker.Others, Offset = 195, Duration = 2, Text = "I can pull them together this afternoon", IsFinal = false, SessionID = session });
            store.MarkDirty(example);
            app.Refresh(contentChanged: true);
            await Idle();
            Check(app.Current.IsTranscriptBoxVisible && window.TranscriptBoxView.Panel.ActualHeight is >= 119 and <= 301, $"Live box 120–300 px (is {window.TranscriptBoxView.Panel.ActualHeight:0})");
            Check(app.Current.Transcript.IsLive && app.Current.Transcript.Turns.Any(turn => turn.ShowsInterim), "Live transcript with 'Transcribing…'");
            await Shot("08-recording-live-box");
            // Echo-canceller health from the engine metrics (WfMetrics.erle_db / delay_ms) in the popover.
            shell.SmokeEngine!.SetMetrics(new AudioMetrics { State = AudioEngineState.Running, ErleDb = 18.4, DelayMs = 42 });
            app.Recording.ToggleInfoCommand.Execute(null);
            await Idle();
            Check(app.Recording.InfoOpen && app.Recording.InfoLines.Count >= 2, "(i) popover lines");
            Check(app.Recording.InfoLines.Contains(Strings.Format(Strings.InfoEchoCancellation, 18.4, 42)), "(i) popover shows ERLE and the estimated delay");
            await Shot("09-recording-info-popover", window.RecordingBarView.InfoContent);
            app.Recording.InfoOpen = false;
            Check(app.Recording.ShowAudioNotice && app.Recording.AudioNotice == store.RecordingAudioDescription
                && app.Recording.InfoLines.Any(line => line.StartsWith(store.RecordingAudioDescription, StringComparison.Ordinal)), "Recording bar notice and (i) popover show the meeting audio Core chose");
            log.Add("meeting audio: " + store.RecordingAudioDescription);
            await SwitchMicrophoneLiveAsync();

            // Capsule while the app is inactive, presentation mode, close to the tray, capsule click.
            var foreground = new Window { Width = 220, Height = 120, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = new TextBlock { Text = "Smoke test foreground window" } };
            foreground.Show();
            foreground.Activate();
            await WaitFor(() => !window.IsActive && shell.Indicator.CapsuleVisible, "capsule while the app is inactive");
            Check(shell.Indicator.TrayVisible, "Tray icon while recording");
            log.Add("capsule excluded from capture: " + shell.Indicator.CaptureExcluded);
            await Shot("10-capsule", (FrameworkElement)shell.Indicator.Capsule.Content);
            shell.Indicator.TogglePresentationFromTray();
            await Idle();
            Check(!shell.Indicator.CapsuleVisible && store.PresentationMode && shell.Indicator.TrayVisible, "Presentation mode hides only the capsule");
            shell.Indicator.TogglePresentationFromTray();
            await WaitFor(() => shell.Indicator.CapsuleVisible, "capsule after presentation mode");
            foreground.Close();
            window.Activate();
            await WaitFor(() => window.IsActive && !shell.Indicator.CapsuleVisible, "capsule hides when the app is active");
            window.Close();
            await WaitFor(() => !window.IsVisible && store.IsRecording && shell.Indicator.TrayVisible && shell.Indicator.CapsuleVisible, "closing hides to the tray while recording");
            shell.Indicator.Open();
            await WaitFor(() => window.IsVisible && app.IsEditor && app.SelectedID == example.Id, "capsule click opens the running note");

            // "Save and end meeting?": the dialog, and "Return to meeting" keeps recording.
            var quitDialog = new QuitDialog { Owner = window, ShowActivated = false };
            quitDialog.Show();
            await Shot("11-quit-dialog", window: quitDialog);
            quitDialog.Close();
            var movingDialog = new QuitDialog(QuitPrompt.LibraryMoving) { Owner = window, ShowActivated = false };
            movingDialog.Show();
            await Shot("11b-quit-library-moving", window: movingDialog);
            movingDialog.Close();
            dialogs.QuitAnswer = QuitChoice.ReturnToMeeting;
            Check(!await app.RequestQuitAsync() && store.IsRecording && dialogs.Calls.Contains("quit"), "'Return to meeting' keeps the recording");
            dialogs.QuitAnswer = QuitChoice.SaveAndEnd;

            // Stop: presentation mode resets, the transcript tab appears.
            store.PresentationMode = true;
            await app.StopRecordingCommand.ExecuteAsync(null);
            await WaitFor(() => !store.Busy, "recording stop");
            await Idle();
            Check(!store.PresentationMode && app.Current!.IsTranscriptTabAvailable && !shell.Indicator.CapsuleVisible, "After stopping: transcript tab, no capsule, no presentation mode");

            // My to-dos with groups, "Done · n" collapsed, review suggestions.
            app.ShowTodosCommand.Execute(null);
            await Idle();
            Check(app.Todos.OpenCount >= 1 && app.Todos.DoneCount >= 1 && app.Todos.Reviews.Count >= 1 && !app.Todos.IsDoneExpanded && app.Todos.OpenGroups.Any(group => group.Name == "Research"),
                "To-dos grouped by folder, Done collapsed, Review suggestions");
            await Shot("12-todos");

            // Settings: three areas.
            var settings = new SettingsWindow(new SettingsViewModel(store, dialogs, shell.System, new LibraryLocation(false)), store) { Owner = window, ShowActivated = false };
            settings.Show();
            Check(settings.ViewModel.Microphones.Count == 3 && settings.ViewModel.MeetingAudioOptions.Any(option => option.Label == "Microsoft Teams"), "Settings lists microphones and running apps");
            foreach (var (area, name) in new[] { (SettingsArea.Recording, "13-settings-recording"), (SettingsArea.AiLanguage, "14-settings-ai-language"), (SettingsArea.Storage, "15-settings-storage") })
            {
                settings.ViewModel.Area = area;
                await Shot(name, window: settings);
            }
            settings.ViewModel.Area = SettingsArea.AiLanguage;
            settings.ViewModel.Enhancement.SelectedProvider = settings.ViewModel.Enhancement.Providers.First(choice => choice.Value == EnhancementProvider.Anthropic);
            await Shot("15b-settings-ai-anthropic", window: settings);
            settings.Close();
            await RunStorageRetriesAsync(weekly);

            // Onboarding: three steps.
            var onboarding = new OnboardingWindow(new OnboardingViewModel(store, new SettingsViewModel(store, dialogs, shell.System, new LibraryLocation(false)), shell.System, dialogs), store) { Owner = window, ShowActivated = false };
            onboarding.Show();
            foreach (var (step, name) in new[] { (0, "16-onboarding-1-storage"), (1, "17-onboarding-2-ai-services"), (2, "18-onboarding-3-permissions") })
            {
                onboarding.ViewModel.Step = step;
                await Shot(name, window: onboarding);
            }
            Check(onboarding.ViewModel.IsLastStep && onboarding.ViewModel.PrimaryText == Strings.Done, "Last setup step reads 'Done'");
            onboarding.CloseForTest();

            // Folder editor (new folder: the least used colour).
            var creating = new FolderEditorViewModel(store, null);
            Check(creating.Creating && creating.Color == store.Catalog.NextColor && creating.Icons.Count == 10 && creating.Colors.Count == 10, "Folder editor: 10 icons, 10 colours, next colour");
            var editor = new FolderEditorWindow(new FolderEditorViewModel(store, product)) { Owner = window, ShowActivated = false };
            editor.Show();
            await Shot("19-folder-editor", window: editor);
            editor.Close();

            // Task review (kept window), confirmation and rename dialogs.
            var taskReview = new TaskReviewWindow(store, review.Id) { Owner = window, ShowActivated = false };
            taskReview.Show();
            await Shot("20-task-review", window: taskReview);
            taskReview.Close();
            var confirm = new ConfirmDialog(Strings.DeleteMeetingQuestion, Strings.DeleteMeetingDetail, Strings.DeleteMeetingConfirm) { Owner = window, ShowActivated = false };
            confirm.Show();
            await Shot("21-confirm-delete", window: confirm);
            confirm.Close();
            var rename = new RenameDialog(weekly.Title) { Owner = window, ShowActivated = false };
            rename.Show();
            await Shot("22-rename-dialog", window: rename);
            rename.Close();

            // Window sizes: 1030 × 680 library, 400 × 760 focus editor.
            window.Width = 1030; window.Height = 680;
            app.ShowMeetingsCommand.Execute(null);
            app.PreviewMeeting(review.Id);
            await Idle();
            Check(app.SidebarVisible && app.ListVisible, "1030 px keeps three columns");
            await Shot("23-library-1030x680");
            window.Width = 400; window.Height = 760;
            app.OpenEditor(example.Id);
            app.ToggleTranscriptCommand.Execute(null);
            await Idle();
            Check(window.EditorArea.ActualWidth <= 400 && app.Current!.IsTranscriptBoxVisible, "400 px focus editor fits");
            Check(window.EditorView.ActualHeight >= window.EditorView.HeaderHeight + 40, "The live box leaves the editor header and some writing space");
            await Shot("24-editor-400x760");
            app.Current.TranscriptBoxOpen = false;
            window.Width = 1340; window.Height = 850;

            // Menu actions through the view model with scripted answers.
            app.RenameMeeting(weekly.Id);
            Check(weekly.Title == "Renamed in the smoke test", "Rename meeting");
            app.OpenEditor(example.Id);
            app.ExportMarkdownCommand.Execute(null);
            Check(File.Exists(Path.Combine(shots, Core.Export.MarkdownExporter.FileName(example))), "Export Markdown");
            app.ShowSavedFiles(example.Id);
            Check(dialogs.Calls.Any(call => call.StartsWith("reveal:", StringComparison.Ordinal)), "Show saved files selects the meeting file");
            app.DeleteMeeting(weekly.Id);
            Check(store.Meetings.All(meeting => meeting.Id != weekly.Id), "Delete meeting");
            Check(app.DeleteFolder(product.Id) && store.Catalog.FolderID(example.Id) == null, "Deleting a folder moves its meetings to 'No folder'");

            // Wallfly Detector with a fake helper, its own pipe name and detection folder.
            await RunDetectionAsync(review, example);
            app.OpenEditor(example.Id);
            await Idle();

            // Session end while recording: WM_QUERYENDSESSION saves and ends the recording, with a block reason.
            await app.StartRecordingCommand.ExecuteAsync(null);
            await WaitFor(() => store.IsRecording, "recording before session end");
            var handle = new WindowInteropHelper(window).Handle;
            var answer = SendMessage(handle, SessionEndGuard.QueryEndSession, IntPtr.Zero, IntPtr.Zero);
            Check(answer == new IntPtr(1) && !store.IsRecording && shell.Guard!.LastSaveSucceeded, "WM_QUERYENDSESSION saves and ends the recording");
            log.Add("shutdown block reason registered: " + shell.Guard?.ReasonWasRegistered);
            // Another app calls the session end off (WM_ENDSESSION with FALSE): the service accepts work again.
            _ = SendMessage(handle, SessionEndGuard.EndSession, IntPtr.Zero, IntPtr.Zero);
            await app.StartRecordingCommand.ExecuteAsync(null);
            await WaitFor(() => store.IsRecording, "recording after a called-off session end");
            await app.StopRecordingCommand.ExecuteAsync(null);
            await WaitFor(() => !store.Busy, "stop after a called-off session end");

            var bindingErrors = Path.Combine(shots, "binding-errors.txt");
            Check(!File.Exists(bindingErrors) || new FileInfo(bindingErrors).Length == 0, "No data-binding errors (see smoke\binding-errors.txt)");
            File.WriteAllText(resultPath, "PASS\n" + string.Join("\n", log) + "\n");
            shell.Shutdown(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(resultPath, "FAIL: " + error.GetType().Name + " " + error.Message + "\n" + error.StackTrace + "\n" + string.Join("\n", log) + "\n");
            try { await Shot("failure"); } catch (Exception) { /* the failure text is already written */ }
            shell.Shutdown(1);
        }
    }

    /// <summary>
    /// "Take notes" over the activation pipe: a new note records; while recording the running note is shown; a used
    /// request is refused; during AI processing nothing happens and the request stays. The heartbeat follows, sleep ends
    /// the recording with its banner, and the settings show the helper status line with "Start helper now".
    /// </summary>
    private async Task RunDetectionAsync(Meeting other, Meeting enhanceable)
    {
        var app = shell.ViewModel;
        var store = shell.Store;
        var dialogs = (SmokeDialogService)shell.Dialogs;
        var detection = shell.Detection;
        var storage = detection.Storage;
        var helper = (SmokeDetectorProcess)detection.Detector;
        Check(detection.PipeListening && helper.Starts >= 1 && storage.ReadConfiguration().Enabled, "Detection on: configuration.json written, helper started, pipe listening");
        Check(storage.Read<DetectionHeartbeat>(DetectionStorage.HeartbeatFile) is { Busy: false }, "Idle heartbeat");
        var before = store.Meetings.Count;
        app.ShowMeetingsCommand.Execute(null);
        await Idle();

        var first = Guid.NewGuid();
        storage.Write(new DetectionRequest(first, DateTimeOffset.UtcNow), DetectionStorage.RequestFile);
        Check(await Send(first) == ActivationDelivery.Accepted, "'Take notes' over the pipe is accepted");
        await WaitFor(() => store.IsRecording, "recording from 'Take notes'");
        var detected = store.Meetings.First(meeting => meeting.Id == store.ActiveID);
        Check(store.Meetings.Count == before + 1 && app.IsEditor && app.SelectedID == detected.Id && !storage.Exists(DetectionStorage.RequestFile),
            "'Take notes' opens a new note that records and consumes the request");
        await WaitFor(() => storage.Read<DetectionHeartbeat>(DetectionStorage.HeartbeatFile) is { Busy: true }, "busy heartbeat while recording");
        await Shot("25-take-notes-recording");

        app.PreviewMeeting(other.Id);
        await Idle();
        var second = Guid.NewGuid();
        storage.Write(new DetectionRequest(second, DateTimeOffset.UtcNow), DetectionStorage.RequestFile);
        Check(await Send(second) == ActivationDelivery.Accepted, "'Take notes' while recording is accepted");
        await Idle();
        Check(app.IsEditor && app.SelectedID == detected.Id && store.Meetings.Count == before + 1 && store.IsRecording, "'Take notes' while recording shows the running note");
        Check(await Send(first) == ActivationDelivery.Rejected, "A used request is refused");

        ((SmokePowerEvents)shell.Power!).Suspend();
        await WaitFor(() => !store.Busy, "recording end on sleep");
        await detection.SleepStop;
        await Idle();
        Check(app.ErrorText == Strings.RecordingEndedSleep && store.Meetings.Count == before + 1, "Sleep ends the recording with its banner (is '" + app.ErrorText + "')");
        await WaitFor(() => storage.Read<DetectionHeartbeat>(DetectionStorage.HeartbeatFile) is { Busy: false }, "idle heartbeat after the recording");
        await Shot("26-sleep-banner");
        app.DismissErrorCommand.Execute(null);

        shell.SmokeProvider!.DelayMilliseconds = 1500;
        var enhancing = store.EnhanceAsync(enhanceable.Id);
        await WaitFor(() => store.ProcessingID != null, "enhancement start");
        var third = Guid.NewGuid();
        storage.Write(new DetectionRequest(third, DateTimeOffset.UtcNow), DetectionStorage.RequestFile);
        Check(await Send(third) == ActivationDelivery.Rejected && storage.Exists(DetectionStorage.RequestFile) && store.Meetings.Count == before + 1,
            "'Take notes' during AI processing does nothing and keeps the request");
        await enhancing;
        shell.SmokeProvider.DelayMilliseconds = 0;
        storage.Remove(DetectionStorage.RequestFile);

        helper.WriteStatus();
        var settings = new SettingsWindow(new SettingsViewModel(store, dialogs, shell.System, new LibraryLocation(false), SettingsArea.Recording, detection), store)
            { Owner = shell.Window, ShowActivated = false };
        settings.Show();
        var model = settings.ViewModel;
        Check(model.HasDetection && model.DetectionLine == Strings.DetectorRunning && model.DetectionActionText == null
            && model.DetectionAutostart == Strings.DetectorAutostartEnabled, "Settings: 'Meeting detection is on', starts at sign-in");
        log.Add("helper status: " + model.DetectionLine + " | " + model.DetectionDetail + " | " + model.DetectionAutostart);
        settings.RecordingArea.HelperStatus.BringIntoView();
        await Shot("27-settings-helper-running", window: settings);
        helper.IsRunning = false;
        detection.Refresh();
        Check(model.DetectionLine == Strings.DetectorNotRunning && model.DetectionActionText == Strings.StartHelperNow && model.StartHelperCommand.CanExecute(null),
            "Settings: 'Helper: not running' with 'Start helper now'");
        await Shot("28-settings-helper-stopped", window: settings);
        model.StartHelperCommand.Execute(null);
        Check(helper.IsRunning && model.DetectionActionText == null, "'Start helper now' starts the helper");
        model.CreateTeamsDiagnosticsCommand.Execute(null);
        Check(storage.Exists(DetectionStorage.DiagnosticRequestFile) && model.DetectionDiagnosticStatus == Strings.TeamsDiagnosticsCreating, "'Create Teams diagnostics' writes the request");
        model.OpenDetectionFolderCommand.Execute(null);
        Check(dialogs.Calls.Contains("open:" + storage.Root), "'Open detection folder'");
        model.DetectTeams = false;
        Check(!storage.ReadConfiguration().Enabled && model.DetectionLine == Strings.DetectorDisabled, "Turning detection off writes configuration.json at once");
        model.DetectTeams = true;
        Check(storage.ReadConfiguration().Enabled && helper.IsRunning, "Turning detection on again");
        settings.Close();
    }

    /// <summary>
    /// The transcript tab of a finished meeting with Core's speaker editor: the speaker menu, "Rename speaker…" and
    /// "Assign this segment…" (dialogs and saved results), then "Re-detect speakers" in the "…" menu with its status line.
    /// </summary>
    private async Task RunSpeakersAsync(Meeting review)
    {
        var app = shell.ViewModel;
        var store = shell.Store;
        var window = shell.Window;
        var dialogs = (SmokeDialogService)shell.Dialogs;
        var transcript = app.Current!.Transcript;
        var anna = review.Participants!.First(person => person.Name.StartsWith("Anna", StringComparison.Ordinal));
        var ben = review.Participants!.First(person => person.Name.StartsWith("Ben", StringComparison.Ordinal));
        var annaTurn = transcript.Turns.First(turn => turn.ParticipantID == anna.Id);
        Check(transcript.CanEditSpeakers && transcript.RenameSpeakerCommand.CanExecute(annaTurn) && transcript.AssignSegmentCommand.CanExecute(annaTurn),
            "Speaker menu: 'Rename speaker…' and 'Assign this segment…' are enabled for a finished meeting");

        // The menu opens at Anna's label in the chat.
        var label = Find<Button>(window.EditorView, button => button.DataContext == annaTurn && button.IsVisible)
            ?? throw new InvalidOperationException("Speaker label of Anna not found");
        var menu = Views.TranscriptChatView.SpeakerMenu(transcript, annaTurn);
        menu.PlacementTarget = label;
        menu.IsOpen = true;
        await Idle();
        Check(menu.Items.OfType<MenuItem>().All(item => item.IsEnabled), "Speaker menu entries enabled");
        await Shot("06b-transcript-speaker-menu", overlay: menu);
        menu.IsOpen = false;

        var renameDialog = new SpeakerNameDialog(anna.Name) { Owner = window, ShowActivated = false };
        renameDialog.Show();
        await Shot("06c-rename-speaker-dialog", window: renameDialog);
        renameDialog.Close();
        dialogs.SpeakerNameAnswer = "Anna Müller-Lang";
        transcript.RenameSpeakerCommand.Execute(annaTurn);
        await Idle();
        Check(review.Participants!.Single(person => person.Id == anna.Id).Name == "Anna Müller-Lang"
            && store.Library.Load().Single(meeting => meeting.Id == review.Id).Participants!.Any(person => person.Name == "Anna Müller-Lang"),
            "'Rename speaker…' renames the participant and saves it");

        var unknownTurn = transcript.Turns.First(turn => !turn.IsMe && turn.ParticipantID == null);
        var options = review.Participants!.Select(person => new SpeakerOption(person.Id, person.Name, Palette.SpeakerColor(person.ColorIndex))).ToList();
        var assignDialog = new AssignSpeakerDialog(unknownTurn.Text, options) { Owner = window, ShowActivated = false };
        assignDialog.Show();
        assignDialog.Select(options.Count + 1, "Chris Doe");
        await Idle();
        Check(assignDialog.Result is { NewName: "Chris Doe" }, "'New participant…' with a name is a valid choice");
        await Shot("06d-assign-segment-dialog", window: assignDialog);
        assignDialog.Close();
        dialogs.AssignmentAnswer = SpeakerAssignment.Existing(ben.Id);
        transcript.AssignSegmentCommand.Execute(unknownTurn);
        await Idle();
        Check(review.Transcript.Single(segment => segment.Offset == 34).ParticipantID == ben.Id
            && store.Library.Load().Single(meeting => meeting.Id == review.Id).Transcript.Single(segment => segment.Offset == 34).Attribution == WordAttribution.Manual,
            "'Assign this segment…' assigns exactly that passage manually and saves it");

        // "Re-detect speakers" in the "…" menu, then the status line under the transcript while it runs.
        var matching = shell.SmokeMatching!;
        matching.Armed = true;
        Check(app.ShowRedetectSpeakers(review.Id) && app.CanRedetectSpeakers(review.Id), "'Re-detect speakers' is offered and enabled");
        var more = window.MoreMenu() ?? throw new InvalidOperationException("No '…' menu");
        more.IsOpen = true;
        await Idle();
        var redetect = more.Items.OfType<MenuItem>().First(item => Equals(item.Header, Strings.RedetectSpeakers));
        Check(redetect.IsEnabled, "'Re-detect speakers' menu entry enabled");
        await Shot("06e-more-menu-redetect", overlay: more);
        redetect.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        more.IsOpen = false;
        await Idle();
        Check(transcript.IsMatchingSpeakers && transcript.DiarizationStatus == matching.Progress && store.SpeakerMatchingProgress == matching.Progress,
            "Re-detect queues the matching and shows 'Matching speakers… 40%'");
        await Shot("06f-transcript-matching-speakers");
        matching.Finish();
        matching.Armed = false;
        await Idle();
        Check(!transcript.IsMatchingSpeakers && transcript.DiarizationStatus == null && review.Diarization?.Status == DiarizationStatus.Completed,
            "The status line goes away when the matching has finished");
    }

    /// <summary>"Switch microphone" in the live controls of the settings while recording: the fake engine switches, Core saves.</summary>
    private async Task SwitchMicrophoneLiveAsync()
    {
        var store = shell.Store;
        var live = new SettingsWindow(new SettingsViewModel(store, shell.Dialogs, shell.System, new LibraryLocation(false)), store) { Owner = shell.Window, ShowActivated = false };
        live.Show();
        Check(live.ViewModel.ShowLiveControls && live.ViewModel.SwitchMicrophoneCommand.CanExecute(null), "Live controls with 'Switch microphone' while recording");
        var headset = live.ViewModel.Microphones.First(choice => choice.Value.Contains("smoke-headset", StringComparison.Ordinal));
        live.ViewModel.LiveMicrophone = headset;
        await WaitFor(() => store.Settings.MicrophoneUID == headset.Value, "live microphone switch");
        Check(shell.SmokeEngine!.MicrophoneDeviceId == headset.Value && live.ViewModel.LiveMicrophone?.Value == headset.Value && live.ViewModel.LiveMicrophoneError == null,
            "'Switch microphone' switches the running engine and saves the choice");
        await Shot("09b-settings-live-microphone", window: live);
        live.Close();
    }

    /// <summary>
    /// Storage with "Try again": a failed audio deletion (Core's cleanup) and a failed transcript copy (Core's mirror) are
    /// listed, shown, and cleared by their buttons. Mirroring is switched off again afterwards.
    /// </summary>
    private async Task RunStorageRetriesAsync(Meeting processed)
    {
        var store = shell.Store;
        var directory = store.Library.AudioDirectory(processed.Id);
        Directory.CreateDirectory(directory);
        var audio = Path.Combine(directory, Guid.NewGuid().ToString("D") + "-others.wav");
        File.WriteAllBytes(audio, new byte[64]);
        processed.AudioFiles.Add(audio);
        processed.AudioDeletionPending = [audio];
        processed.AudioDeletionReady = true;
        processed.AudioDeletionError = "The process cannot access the file because it is being used by another process.";
        store.MarkDirty(processed);
        store.Flush();

        var mirrorFolder = Path.Combine(shell.Root, "transcript-copies");
        var settingsBefore = WallflyJson.Copy(store.Settings);
        var mirroring = WallflyJson.Copy(store.Settings);
        mirroring.TranscriptMirrorEnabled = true;
        mirroring.TranscriptMirrorFolder = new TranscriptMirrorInfo { Path = mirrorFolder };
        store.SaveSettings(mirroring, store.ApiKey);
        var mirrored = store.CreateMeeting();
        mirrored.Title = "Customer call";
        mirrored.Upsert(new TranscriptSegment { Speaker = Speaker.Others, Offset = 2, Duration = 2, Text = "Thanks for joining.", IsFinal = true });
        store.MarkDirty(mirrored);
        store.Flush();
        await WaitFor(() => store.TranscriptMirrorErrors.ContainsKey(mirrored.Id), "transcript copy failure");

        var settings = new SettingsWindow(new SettingsViewModel(store, shell.Dialogs, shell.System, new LibraryLocation(false), SettingsArea.Storage), store)
            { Owner = shell.Window, ShowActivated = false };
        settings.Show();
        var model = settings.ViewModel;
        Check(model.PendingDeletions.Count == 1 && model.MirrorErrors.Count == 1, "Storage lists the pending audio deletion and the failed transcript copy");
        await Shot("15c-settings-storage-retry", window: settings);
        settings.Scroller.ScrollToEnd();
        await Shot("15d-settings-storage-transcript-copy", window: settings);
        model.PendingDeletions[0].TryAgainCommand.Execute(null);
        await Idle();
        Check(!File.Exists(audio) && processed.AudioDeletionError == null && model.PendingDeletions.Count == 0, "'Try again' deletes the processed audio");
        Directory.CreateDirectory(mirrorFolder);
        model.MirrorErrors[0].TryAgainCommand.Execute(null);
        await WaitFor(() => !store.TranscriptMirrorErrors.ContainsKey(mirrored.Id) && model.MirrorErrors.Count == 0, "transcript copy after 'Try again'");
        Check(Directory.GetFiles(mirrorFolder, "*.transcript.json").Length == 1, "'Try again' writes the transcript copy");
        settings.Close();

        store.SaveSettings(settingsBefore, store.ApiKey);
        store.DeleteMeeting(mirrored.Id);
        shell.ViewModel.Refresh(contentChanged: true);
    }

    /// <summary>The first visual descendant of <paramref name="root"/> of type <typeparamref name="T"/> that matches.</summary>
    private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate && match(candidate)) return candidate;
            if (Find(child, match) is { } found) return found;
        }
        return null;
    }

    private Task<ActivationDelivery> Send(Guid id) => ActivationPipeClient.SendAsync(id, TimeSpan.FromSeconds(5), shell.Detection.PipeName);

    private static MeetingFolder AddFolder(MeetingLibraryService store, string name, string icon, FolderColor color)
    {
        var folder = new MeetingFolder { Name = name, Icon = icon, Color = color };
        var catalog = WallflyJson.Copy(store.Catalog);
        catalog.Folders.Add(folder);
        if (!store.CommitCatalog(catalog)) throw new InvalidOperationException("Folder could not be saved");
        return folder;
    }

    private static Meeting AddReviewMeeting(MeetingLibraryService store)
    {
        var meeting = store.CreateMeeting();
        meeting.Title = "Design review: navigation, search and the pilot";
        meeting.StartedAt = DateTimeOffset.Now.AddHours(-3);
        meeting.Duration = 1240;
        meeting.Notes = "Navigation feels heavy.\nSearch should show where a match is.\nPilot with ten teams.";
        meeting.EnhancedNotes = "# Navigation and search\nThe team agreed to simplify the sidebar and to show search matches **in context**.\n## Decisions\n- The pilot starts with ten teams in November.\n- Search results show two excerpts per meeting.\n### Open questions\n- Who runs the screen reader tests?";
        meeting.EnhancementEngine = "OpenAI · gpt-4.1-mini";
        var anna = new MeetingParticipant { Name = "Anna Müller", ColorIndex = 0 };
        var ben = new MeetingParticipant { Name = "Ben Okafor", ColorIndex = 1 };
        meeting.Participants = [anna, ben];
        meeting.Upsert(new TranscriptSegment { Speaker = Speaker.Me, Offset = 4, Duration = 5, Text = "Let's start with the navigation. It feels heavy with all the folders open.", IsFinal = true });
        meeting.Upsert(new TranscriptSegment { Speaker = Speaker.Others, Offset = 11, Duration = 6, Text = "Agreed. Most people only use two or three folders anyway.", IsFinal = true, ParticipantID = anna.Id });
        meeting.Upsert(new TranscriptSegment { Speaker = Speaker.Others, Offset = 18, Duration = 4, Text = "And search should show where the match is, not just the meeting.", IsFinal = true, ParticipantID = anna.Id });
        meeting.Upsert(new TranscriptSegment { Speaker = Speaker.Others, Offset = 25, Duration = 5, Text = "I can prototype the excerpts for the pilot this week.", IsFinal = true, ParticipantID = ben.Id });
        meeting.Upsert(new TranscriptSegment { Speaker = Speaker.Me, Offset = 31, Duration = 2, Text = "Great, thank you.", IsFinal = true });
        meeting.Upsert(new TranscriptSegment { Speaker = Speaker.Others, Offset = 34, Duration = 3, Text = "One more thing about the pilot timeline", IsFinal = false });
        meeting.AppendTranscriptionGap(new TranscriptionGap { Start = 40, Duration = 2.5, Speaker = Speaker.Others, Reason = "Network interrupted" });
        meeting.MergeTasks([new("Prepare the pilot with ten teams", "Me"), new("Review the invite flow with design", "Me"), new("Confirm the pilot budget", "Me")]);
        meeting.Tasks![0].Review = TaskReview.Accepted;
        meeting.Tasks[1].Review = TaskReview.Accepted;
        meeting.Tasks[1].IsCompleted = true;
        meeting.TaskExtraction = TaskExtractionState.Complete;
        store.MarkDirty(meeting);
        store.Flush();
        return meeting;
    }

    private void WriteIconReport()
    {
        var report = new StringBuilder();
        foreach (var kind in Enum.GetValues<IconKind>().Where(kind => kind != IconKind.None))
            report.AppendLine($"{kind}: {(Icon.GlyphFont(kind) is { } face ? "glyph " + face.FamilyNames.Values.FirstOrDefault() : "path fallback")}");
        File.WriteAllText(Path.Combine(shots, "icons.txt"), report.ToString());
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("Check failed: " + what);
    }

    private static async Task Idle()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(40);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// Renders a window's content (or one element) on white at 96 dpi. <paramref name="overlay"/> (an open context menu,
    /// which lives in its own popup window) is drawn on top at its position on screen.
    /// </summary>
    private async Task Shot(string name, FrameworkElement? element = null, Window? window = null, FrameworkElement? overlay = null)
    {
        await Idle();
        // A window renders from its template root, so margins of the content stay in the picture.
        element ??= (FrameworkElement)VisualTreeHelper.GetChild(window ?? shell.Window, 0);
        element.UpdateLayout();
        var width = Math.Max(1, element.ActualWidth);
        var height = Math.Max(1, element.ActualHeight);
        // Render at the monitor's scale, so 1-pixel lines snapped by layout rounding appear as on screen.
        var scale = VisualTreeHelper.GetDpi(element).DpiScaleX;
        Rect? popup = null;
        if (overlay is { IsVisible: true } && PresentationSource.FromVisual(overlay) != null && PresentationSource.FromVisual(element) != null)
        {
            // A popup may reach past the window edge (a menu at the right border): the picture grows to include it.
            var offset = overlay.PointToScreen(new Point(0, 0)) - element.PointToScreen(new Point(0, 0));
            popup = new Rect(new Point(Math.Max(0, offset.X / scale), Math.Max(0, offset.Y / scale)), new Size(Math.Max(1, overlay.ActualWidth), Math.Max(1, overlay.ActualHeight)));
        }
        var canvasWidth = popup is { } extent ? Math.Max(width, extent.Right + 8) : width;
        var canvasHeight = popup is { } extentY ? Math.Max(height, extentY.Bottom + 8) : height;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, canvasWidth, canvasHeight));
            context.DrawRectangle(new VisualBrush(element)
            {
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                Viewbox = new Rect(0, 0, width, height), ViewboxUnits = BrushMappingMode.Absolute
            }, null, new Rect(0, 0, width, height));
            if (popup is { } bounds)
            {
                context.DrawRectangle(new VisualBrush(overlay!)
                {
                    Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                    Viewbox = new Rect(bounds.Size), ViewboxUnits = BrushMappingMode.Absolute
                }, null, bounds);
            }
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(canvasWidth * scale), (int)Math.Ceiling(canvasHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(shots, name + ".png"));
        encoder.Save(output);
        log.Add("screenshot " + name + $" {bitmap.PixelWidth}x{bitmap.PixelHeight} at {scale * 100:0} %");
    }

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}

internal static class SmokePreviewExtensions
{
    public static int HitCount(this MeetingPreviewViewModel preview) => preview.Hits.Count;
}
