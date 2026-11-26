using System.Globalization;
using System.Resources;

namespace Wallfly.App;

/// <summary>
/// Typed access to <c>Strings.resx</c>, the single place that holds the app's user-visible text.
/// Written by hand instead of generated so <c>dotnet build</c> needs no designer tooling; the resx
/// stays the only source of the values. <c>UiStringsTests</c> checks that every member resolves.
/// Members whose value contains <c>{0}</c> are composite formats; fill them with <see cref="Format"/>.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Manager = new("Wallfly.App.Strings", typeof(Strings).Assembly);

    public static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>Fills a composite format of this class with the current culture.</summary>
    public static string Format(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    // App
    public static string AppName => Get(nameof(AppName));
    public static string AlreadyRunning => Get(nameof(AlreadyRunning));
    public static string UnexpectedError => Get(nameof(UnexpectedError));

    // Sidebar
    public static string NewNote => Get(nameof(NewNote));
    public static string NewNoteShortcut => Get(nameof(NewNoteShortcut));
    public static string NavMeetings => Get(nameof(NavMeetings));
    public static string NavTodos => Get(nameof(NavTodos));
    public static string FoldersHeader => Get(nameof(FoldersHeader));
    public static string CreateFolder => Get(nameof(CreateFolder));
    public static string AllMeetings => Get(nameof(AllMeetings));
    public static string NoFolder => Get(nameof(NoFolder));
    public static string GoToCurrentRecording => Get(nameof(GoToCurrentRecording));
    public static string Settings => Get(nameof(Settings));
    public static string EditFolderMenu => Get(nameof(EditFolderMenu));
    public static string DeleteFolderMenu => Get(nameof(DeleteFolderMenu));

    // Library list
    public static string SearchResults => Get(nameof(SearchResults));
    public static string SearchMeetings => Get(nameof(SearchMeetings));
    public static string SearchShortcut => Get(nameof(SearchShortcut));
    public static string ClearSearch => Get(nameof(ClearSearch));
    public static string SearchingIn => Get(nameof(SearchingIn));
    public static string SearchAllMeetings => Get(nameof(SearchAllMeetings));
    public static string SearchCurrentFolderOnly => Get(nameof(SearchCurrentFolderOnly));
    public static string Searching => Get(nameof(Searching));
    public static string NoResults => Get(nameof(NoResults));
    public static string NoResultsDetail => Get(nameof(NoResultsDetail));
    public static string NoMeetingsYet => Get(nameof(NoMeetingsYet));
    public static string NoMeetingsYetDetail => Get(nameof(NoMeetingsYetDetail));
    public static string ResetSearch => Get(nameof(ResetSearch));
    public static string UntitledMeeting => Get(nameof(UntitledMeeting));
    public static string RecordingInProgress => Get(nameof(RecordingInProgress));
    public static string YourMeetings => Get(nameof(YourMeetings));
    public static string RenameMeetingMenu => Get(nameof(RenameMeetingMenu));
    public static string DeleteMeetingMenu => Get(nameof(DeleteMeetingMenu));
    public static string MoveToFolder => Get(nameof(MoveToFolder));

    // Toolbar and detail
    public static string PreviewCaption => Get(nameof(PreviewCaption));
    public static string TodosCaption => Get(nameof(TodosCaption));
    public static string BackToPreview => Get(nameof(BackToPreview));
    public static string BackToMeetings => Get(nameof(BackToMeetings));
    public static string FocusMode => Get(nameof(FocusMode));
    public static string ShowLibrary => Get(nameof(ShowLibrary));
    public static string PresentationMode => Get(nameof(PresentationMode));
    public static string PresentationModeHint => Get(nameof(PresentationModeHint));
    public static string MoreActions => Get(nameof(MoreActions));
    public static string ExportMarkdown => Get(nameof(ExportMarkdown));
    public static string ShowSavedFiles => Get(nameof(ShowSavedFiles));
    public static string RedetectSpeakers => Get(nameof(RedetectSpeakers));
    public static string RedetectSpeakersOnDevice => Get(nameof(RedetectSpeakersOnDevice));
    public static string RenameMenu => Get(nameof(RenameMenu));
    public static string DeleteMenu => Get(nameof(DeleteMenu));
    public static string EmptyTitle => Get(nameof(EmptyTitle));
    public static string EmptyDetail => Get(nameof(EmptyDetail));
    public static string EmptySearchDetail => Get(nameof(EmptySearchDetail));
    public static string DismissMessage => Get(nameof(DismissMessage));
    public static string CopyDetails => Get(nameof(CopyDetails));
    public static string ErrorOpenSettings => Get(nameof(ErrorOpenSettings));
    public static string ErrorTryAgain => Get(nameof(ErrorTryAgain));

    // Preview
    public static string MatchOf => Get(nameof(MatchOf));
    public static string PreviousMatch => Get(nameof(PreviousMatch));
    public static string NextMatch => Get(nameof(NextMatch));
    public static string Edit => Get(nameof(Edit));
    public static string NoContentYet => Get(nameof(NoContentYet));
    public static string NoContentTranscriptDetail => Get(nameof(NoContentTranscriptDetail));
    public static string NoContentNotesDetail => Get(nameof(NoContentNotesDetail));
    public static string TabMyNotes => Get(nameof(TabMyNotes));
    public static string TabEnhancedNotes => Get(nameof(TabEnhancedNotes));
    public static string TabTranscript => Get(nameof(TabTranscript));
    public static string AssignFolder => Get(nameof(AssignFolder));
    public static string CreateFolderEllipsis => Get(nameof(CreateFolderEllipsis));

    // Editor
    public static string MeetingTitleName => Get(nameof(MeetingTitleName));
    public static string NotesPlaceholder => Get(nameof(NotesPlaceholder));
    public static string EnhanceNotes => Get(nameof(EnhanceNotes));
    public static string EnhanceAgain => Get(nameof(EnhanceAgain));
    public static string SetUpEnhancement => Get(nameof(SetUpEnhancement));
    public static string EnhanceNotesHint => Get(nameof(EnhanceNotesHint));
    public static string EnhancingNotes => Get(nameof(EnhancingNotes));
    public static string CaptureWhatMatters => Get(nameof(CaptureWhatMatters));
    public static string CaptureWhatMattersDetail => Get(nameof(CaptureWhatMattersDetail));
    public static string EnhancedWith => Get(nameof(EnhancedWith));

    // Meeting chat
    public static string ChatPlaceholder => Get(nameof(ChatPlaceholder));
    public static string ChatInputName => Get(nameof(ChatInputName));
    public static string ChatNew => Get(nameof(ChatNew));
    public static string ChatCollapse => Get(nameof(ChatCollapse));
    public static string ChatThinking => Get(nameof(ChatThinking));
    public static string ChatFailed => Get(nameof(ChatFailed));
    public static string ChatMessages => Get(nameof(ChatMessages));

    // Transcript
    public static string LivePill => Get(nameof(LivePill));
    public static string CopyTranscript => Get(nameof(CopyTranscript));
    public static string Copied => Get(nameof(Copied));
    public static string CollapseTranscript => Get(nameof(CollapseTranscript));
    public static string ShowTranscript => Get(nameof(ShowTranscript));
    public static string HideTranscript => Get(nameof(HideTranscript));
    public static string TranscriptGaps => Get(nameof(TranscriptGaps));
    public static string TranscriptRecordingTitle => Get(nameof(TranscriptRecordingTitle));
    public static string TranscriptRecordingDetail => Get(nameof(TranscriptRecordingDetail));
    public static string TranscriptEmptyTitle => Get(nameof(TranscriptEmptyTitle));
    public static string TranscriptEmptyDetail => Get(nameof(TranscriptEmptyDetail));
    public static string Transcribing => Get(nameof(Transcribing));
    public static string SegmentNotFinished => Get(nameof(SegmentNotFinished));
    public static string Latest => Get(nameof(Latest));
    public static string LatestHint => Get(nameof(LatestHint));
    public static string SpeakerMe => Get(nameof(SpeakerMe));
    public static string SpeakerOthers => Get(nameof(SpeakerOthers));
    public static string RenameSpeaker => Get(nameof(RenameSpeaker));
    public static string AssignSegment => Get(nameof(AssignSegment));
    public static string RenameSpeakerTitle => Get(nameof(RenameSpeakerTitle));
    public static string RenameSpeakerDetail => Get(nameof(RenameSpeakerDetail));
    public static string SpeakerNameLabel => Get(nameof(SpeakerNameLabel));
    public static string AssignSegmentTitle => Get(nameof(AssignSegmentTitle));
    public static string AssignSegmentDetail => Get(nameof(AssignSegmentDetail));
    public static string AssignOthersUnknown => Get(nameof(AssignOthersUnknown));
    public static string NewParticipant => Get(nameof(NewParticipant));
    public static string Assign => Get(nameof(Assign));
    public static string SpeakerMatchingWaiting => Get(nameof(SpeakerMatchingWaiting));
    public static string SpeakerMenuHint => Get(nameof(SpeakerMenuHint));
    public static string DiarizationRunning => Get(nameof(DiarizationRunning));
    public static string DiarizationFailed => Get(nameof(DiarizationFailed));
    public static string DiarizationSkipped => Get(nameof(DiarizationSkipped));
    public static string DiarizationSkippedNoReason => Get(nameof(DiarizationSkippedNoReason));
    public static string SpeakerSeparationWaiting => Get(nameof(SpeakerSeparationWaiting));
    public static string SpeakerSeparationWaitingForRecording => Get(nameof(SpeakerSeparationWaitingForRecording));
    public static string DiarizationMicrophoneOnly => Get(nameof(DiarizationMicrophoneOnly));
    public static string DiarizationMicrophoneChannel => Get(nameof(DiarizationMicrophoneChannel));
    public static string MeetingAudioWarningHint => Get(nameof(MeetingAudioWarningHint));
    public static string ResizeTranscript => Get(nameof(ResizeTranscript));
    public static string InterimMarker => Get(nameof(InterimMarker));
    public static string TranscriptGapCopy => Get(nameof(TranscriptGapCopy));

    // Recording bar
    public static string StartRecording => Get(nameof(StartRecording));
    public static string StartingRecording => Get(nameof(StartingRecording));
    public static string StopRecording => Get(nameof(StopRecording));
    public static string Finishing => Get(nameof(Finishing));
    public static string StartShortcutHint => Get(nameof(StartShortcutHint));
    public static string StopShortcutHint => Get(nameof(StopShortcutHint));
    public static string GoToRecording => Get(nameof(GoToRecording));
    public static string MeterMicrophone => Get(nameof(MeterMicrophone));
    public static string MeterMeetingAudio => Get(nameof(MeterMeetingAudio));
    public static string RecordingInformation => Get(nameof(RecordingInformation));
    public static string TranscriptToggle => Get(nameof(TranscriptToggle));
    public static string InfoMeetingAudioAutomatic => Get(nameof(InfoMeetingAudioAutomatic));
    public static string InfoMeetingAudioSystem => Get(nameof(InfoMeetingAudioSystem));
    public static string InfoMeetingAudioApp => Get(nameof(InfoMeetingAudioApp));
    public static string InfoChannel => Get(nameof(InfoChannel));
    public static string InfoEchoCancellation => Get(nameof(InfoEchoCancellation));
    public static string InfoEchoCancellationLimited => Get(nameof(InfoEchoCancellationLimited));
    public static string MicrophoneNotice => Get(nameof(MicrophoneNotice));
    public static string InfoRecordingGaps => Get(nameof(InfoRecordingGaps));
    public static string InfoTranscriptionGaps => Get(nameof(InfoTranscriptionGaps));
    public static string InfoGapFrom => Get(nameof(InfoGapFrom));
    public static string InfoLastIssue => Get(nameof(InfoLastIssue));
    public static string SystemDefault => Get(nameof(SystemDefault));
    public static string AudioLevelName => Get(nameof(AudioLevelName));

    // To-dos
    public static string TodosTitle => Get(nameof(TodosTitle));
    public static string OpenCount => Get(nameof(OpenCount));
    public static string GoToRecordingTitle => Get(nameof(GoToRecordingTitle));
    public static string IntroTitle => Get(nameof(IntroTitle));
    public static string IntroDetail => Get(nameof(IntroDetail));
    public static string IntroSetUpEnhancement => Get(nameof(IntroSetUpEnhancement));
    public static string IntroLoadExample => Get(nameof(IntroLoadExample));
    public static string HideIntroduction => Get(nameof(HideIntroduction));
    public static string TasksPending => Get(nameof(TasksPending));
    public static string RetryTaskDetection => Get(nameof(RetryTaskDetection));
    public static string ReviewSuggestions => Get(nameof(ReviewSuggestions));
    public static string NoTodosYet => Get(nameof(NoTodosYet));
    public static string AllDone => Get(nameof(AllDone));
    public static string TodosEmptyDetail => Get(nameof(TodosEmptyDetail));
    public static string DoneCount => Get(nameof(DoneCount));
    public static string OpenSourceMeeting => Get(nameof(OpenSourceMeeting));
    public static string CompleteTask => Get(nameof(CompleteTask));
    public static string ReopenTask => Get(nameof(ReopenTask));
    public static string ExampleTitle => Get(nameof(ExampleTitle));
    public static string ExampleNote1 => Get(nameof(ExampleNote1));
    public static string ExampleNote2 => Get(nameof(ExampleNote2));
    public static string ExampleNote3 => Get(nameof(ExampleNote3));
    public static string ExampleSegment1 => Get(nameof(ExampleSegment1));
    public static string ExampleSegment2 => Get(nameof(ExampleSegment2));
    public static string ExampleSegment3 => Get(nameof(ExampleSegment3));
    public static string ExampleSegment4 => Get(nameof(ExampleSegment4));

    // Task review (kept window)
    public static string TaskReviewTitle => Get(nameof(TaskReviewTitle));
    public static string TaskReviewHint => Get(nameof(TaskReviewHint));
    public static string TaskReviewAccept => Get(nameof(TaskReviewAccept));
    public static string TaskReviewDismiss => Get(nameof(TaskReviewDismiss));
    public static string TaskReviewLaterHint => Get(nameof(TaskReviewLaterHint));
    public static string TaskReviewLater => Get(nameof(TaskReviewLater));

    // Dialogs
    public static string Cancel => Get(nameof(Cancel));
    public static string Rename => Get(nameof(Rename));
    public static string RenameMeetingTitle => Get(nameof(RenameMeetingTitle));
    public static string MeetingName => Get(nameof(MeetingName));
    public static string DeleteMeetingQuestion => Get(nameof(DeleteMeetingQuestion));
    public static string DeleteMeetingDetail => Get(nameof(DeleteMeetingDetail));
    public static string DeleteMeetingConfirm => Get(nameof(DeleteMeetingConfirm));
    public static string DeleteFolderQuestion => Get(nameof(DeleteFolderQuestion));
    public static string DeleteFolderDetail => Get(nameof(DeleteFolderDetail));
    public static string DeleteFolderConfirm => Get(nameof(DeleteFolderConfirm));
    public static string QuitTitle => Get(nameof(QuitTitle));
    public static string QuitDetail => Get(nameof(QuitDetail));
    public static string QuitReturn => Get(nameof(QuitReturn));
    public static string QuitSaveAndEnd => Get(nameof(QuitSaveAndEnd));
    public static string QuitLibraryMovingTitle => Get(nameof(QuitLibraryMovingTitle));
    public static string QuitLibraryMovingDetail => Get(nameof(QuitLibraryMovingDetail));
    public static string QuitLibraryMovingReturn => Get(nameof(QuitLibraryMovingReturn));
    public static string SaveFailedTitle => Get(nameof(SaveFailedTitle));
    public static string SaveFailedDetail => Get(nameof(SaveFailedDetail));
    public static string QuitNotFinished => Get(nameof(QuitNotFinished));
    public static string ExportFailed => Get(nameof(ExportFailed));
    public static string MarkdownFilter => Get(nameof(MarkdownFilter));
    public static string ShutdownReason => Get(nameof(ShutdownReason));

    // Capsule and tray
    public static string OpenRunningMeeting => Get(nameof(OpenRunningMeeting));
    public static string CapsuleTitle => Get(nameof(CapsuleTitle));
    public static string TrayRecording => Get(nameof(TrayRecording));
    public static string TrayFinishing => Get(nameof(TrayFinishing));
    public static string TrayOpenMeeting => Get(nameof(TrayOpenMeeting));
    public static string QuitMenu => Get(nameof(QuitMenu));

    // Settings
    public static string SettingsTitle => Get(nameof(SettingsTitle));
    public static string CloseSettings => Get(nameof(CloseSettings));
    public static string AreaRecording => Get(nameof(AreaRecording));
    public static string AreaAiLanguage => Get(nameof(AreaAiLanguage));
    public static string AreaStorage => Get(nameof(AreaStorage));
    public static string SectionAudio => Get(nameof(SectionAudio));
    public static string Microphone => Get(nameof(Microphone));
    public static string RefreshMicrophones => Get(nameof(RefreshMicrophones));
    public static string SavedMicrophoneUnavailable => Get(nameof(SavedMicrophoneUnavailable));
    public static string DevicesUnreadable => Get(nameof(DevicesUnreadable));
    public static string OutputDevice => Get(nameof(OutputDevice));
    public static string SavedOutputUnavailable => Get(nameof(SavedOutputUnavailable));
    public static string MeetingAudio => Get(nameof(MeetingAudio));
    public static string MeetingAudioAutomatic => Get(nameof(MeetingAudioAutomatic));
    public static string MeetingAudioWholeSystem => Get(nameof(MeetingAudioWholeSystem));
    public static string SavedAppUnavailable => Get(nameof(SavedAppUnavailable));
    public static string RefreshApps => Get(nameof(RefreshApps));
    public static string MeetingAudioDetail => Get(nameof(MeetingAudioDetail));
    public static string AppsUnreadable => Get(nameof(AppsUnreadable));
    public static string SectionSpeakers => Get(nameof(SectionSpeakers));
    public static string UseTeamsNames => Get(nameof(UseTeamsNames));
    public static string MatchVoices => Get(nameof(MatchVoices));
    public static string MatchVoicesDetail => Get(nameof(MatchVoicesDetail));
    public static string SpeakerSeparation => Get(nameof(SpeakerSeparation));
    public static string SpeakerSeparationDeepgram => Get(nameof(SpeakerSeparationDeepgram));
    public static string SpeakerSeparationOnDevice => Get(nameof(SpeakerSeparationOnDevice));
    public static string SpeakerSeparationDeepgramDetail => Get(nameof(SpeakerSeparationDeepgramDetail));
    public static string SpeakerSeparationOnDeviceDetail => Get(nameof(SpeakerSeparationOnDeviceDetail));
    public static string SectionTeams => Get(nameof(SectionTeams));
    public static string DetectTeams => Get(nameof(DetectTeams));
    public static string DetectTeamsDetail => Get(nameof(DetectTeamsDetail));
    public static string AutoStop => Get(nameof(AutoStop));
    public static string HideIndicator => Get(nameof(HideIndicator));
    public static string HideIndicatorDetail => Get(nameof(HideIndicatorDetail));
    public static string SectionTroubleshooting => Get(nameof(SectionTroubleshooting));
    public static string OpenMicrophonePrivacy => Get(nameof(OpenMicrophonePrivacy));
    public static string OpenSoundSettings => Get(nameof(OpenSoundSettings));
    public static string CopyDiagnostics => Get(nameof(CopyDiagnostics));
    public static string DiagnosticsCopied => Get(nameof(DiagnosticsCopied));
    public static string DetectorDisabled => Get(nameof(DetectorDisabled));
    public static string DetectorRunning => Get(nameof(DetectorRunning));
    public static string DetectorNotRunning => Get(nameof(DetectorNotRunning));
    public static string DetectorNotResponding => Get(nameof(DetectorNotResponding));
    public static string DetectorNotInstalled => Get(nameof(DetectorNotInstalled));
    public static string DetectorAutostartEnabled => Get(nameof(DetectorAutostartEnabled));
    public static string DetectorAutostartOff => Get(nameof(DetectorAutostartOff));
    public static string DetectorAutostartDisabledByUser => Get(nameof(DetectorAutostartDisabledByUser));
    public static string DetectorAutostartOtherPath => Get(nameof(DetectorAutostartOtherPath));
    public static string StartHelperNow => Get(nameof(StartHelperNow));
    public static string RestartHelper => Get(nameof(RestartHelper));
    public static string DetectorStartFailed => Get(nameof(DetectorStartFailed));
    public static string DetectorStopFailed => Get(nameof(DetectorStopFailed));
    public static string DetectorSettingFailed => Get(nameof(DetectorSettingFailed));
    public static string OpenDetectionFolder => Get(nameof(OpenDetectionFolder));
    public static string CreateTeamsDiagnostics => Get(nameof(CreateTeamsDiagnostics));
    public static string TeamsDiagnosticsCreating => Get(nameof(TeamsDiagnosticsCreating));
    public static string TeamsDiagnosticsCreated => Get(nameof(TeamsDiagnosticsCreated));
    public static string TeamsDiagnosticsNoResponse => Get(nameof(TeamsDiagnosticsNoResponse));
    public static string TeamsDiagnosticsFailed => Get(nameof(TeamsDiagnosticsFailed));
    public static string MicPrefilter => Get(nameof(MicPrefilter));
    public static string MicPrefilterDetail => Get(nameof(MicPrefilterDetail));
    public static string RecordingEndedSleep => Get(nameof(RecordingEndedSleep));
    public static string DetectedMeetingFailed => Get(nameof(DetectedMeetingFailed));
    public static string SectionTranscription => Get(nameof(SectionTranscription));
    public static string Service => Get(nameof(Service));
    public static string ProviderDeepgram => Get(nameof(ProviderDeepgram));
    public static string ProviderLocalWhisper => Get(nameof(ProviderLocalWhisper));
    public static string ProviderOpenAi => Get(nameof(ProviderOpenAi));
    public static string SpeakerQualityHint => Get(nameof(SpeakerQualityHint));
    public static string DeepgramPrivacy => Get(nameof(DeepgramPrivacy));
    public static string DeepgramKey => Get(nameof(DeepgramKey));
    public static string OpenAiPrivacy => Get(nameof(OpenAiPrivacy));
    public static string OpenAiKey => Get(nameof(OpenAiKey));
    public static string LocalPrivacy => Get(nameof(LocalPrivacy));
    public static string WhisperApp => Get(nameof(WhisperApp));
    public static string WhisperModel => Get(nameof(WhisperModel));
    public static string Choose => Get(nameof(Choose));
    public static string LocalFound => Get(nameof(LocalFound));
    public static string LocalMissing => Get(nameof(LocalMissing));
    public static string WhisperAppFilter => Get(nameof(WhisperAppFilter));
    public static string WhisperModelFilter => Get(nameof(WhisperModelFilter));
    public static string SpokenLanguage => Get(nameof(SpokenLanguage));
    public static string LanguageAutomatic => Get(nameof(LanguageAutomatic));
    public static string LanguageGerman => Get(nameof(LanguageGerman));
    public static string LanguageEnglish => Get(nameof(LanguageEnglish));
    public static string CustomVocabulary => Get(nameof(CustomVocabulary));
    public static string CustomVocabularyDetail => Get(nameof(CustomVocabularyDetail));
    public static string KeyMissing => Get(nameof(KeyMissing));
    public static string KeyEntered => Get(nameof(KeyEntered));
    public static string SectionEnhancement => Get(nameof(SectionEnhancement));
    public static string EnhancementProviderOff => Get(nameof(EnhancementProviderOff));
    public static string EnhancementProviderAnthropic => Get(nameof(EnhancementProviderAnthropic));
    public static string EnhancementProviderOpenAi => Get(nameof(EnhancementProviderOpenAi));
    public static string EnhancementProviderCustom => Get(nameof(EnhancementProviderCustom));
    public static string Model => Get(nameof(Model));
    public static string ModelHint => Get(nameof(ModelHint));
    public static string ApiAddress => Get(nameof(ApiAddress));
    public static string ApiKey => Get(nameof(ApiKey));
    public static string ApiKeyOptional => Get(nameof(ApiKeyOptional));
    public static string SharedOpenAiKey => Get(nameof(SharedOpenAiKey));
    public static string CreateApiKey => Get(nameof(CreateApiKey));
    public static string Advanced => Get(nameof(Advanced));
    public static string WorkspaceId => Get(nameof(WorkspaceId));
    public static string PrivacyAnthropic => Get(nameof(PrivacyAnthropic));
    public static string PrivacyOpenAi => Get(nameof(PrivacyOpenAi));
    public static string PrivacyCustom => Get(nameof(PrivacyCustom));
    public static string SectionMyTodos => Get(nameof(SectionMyTodos));
    public static string YourName => Get(nameof(YourName));
    public static string YourNameHint => Get(nameof(YourNameHint));
    public static string YourNameDetail => Get(nameof(YourNameDetail));
    public static string SectionStorageLocation => Get(nameof(SectionStorageLocation));
    public static string FolderLabel => Get(nameof(FolderLabel));
    public static string Change => Get(nameof(Change));
    public static string ShowInExplorer => Get(nameof(ShowInExplorer));
    public static string StorageDetail => Get(nameof(StorageDetail));
    public static string StorageChangeHint => Get(nameof(StorageChangeHint));
    public static string ChooseLibraryFolder => Get(nameof(ChooseLibraryFolder));
    public static string MoveLibraryQuestion => Get(nameof(MoveLibraryQuestion));
    public static string MoveLibraryDone => Get(nameof(MoveLibraryDone));
    public static string MoveLibraryFailed => Get(nameof(MoveLibraryFailed));
    public static string MoveLibraryConfirm => Get(nameof(MoveLibraryConfirm));
    public static string MovingLibrary => Get(nameof(MovingLibrary));
    public static string MoveLibraryTitle => Get(nameof(MoveLibraryTitle));
    public static string SectionAudioAfter => Get(nameof(SectionAudioAfter));
    public static string KeepAudio => Get(nameof(KeepAudio));
    public static string KeepAudioDetail => Get(nameof(KeepAudioDetail));
    public static string DeleteAutomatically => Get(nameof(DeleteAutomatically));
    public static string DeleteAfterMatching => Get(nameof(DeleteAfterMatching));
    public static string DeleteAfterTranscript => Get(nameof(DeleteAfterTranscript));
    public static string AppliesToNew => Get(nameof(AppliesToNew));
    public static string PendingDeletion => Get(nameof(PendingDeletion));
    public static string MirrorFailedItem => Get(nameof(MirrorFailedItem));
    public static string SectionTranscriptCopy => Get(nameof(SectionTranscriptCopy));
    public static string MirrorToggle => Get(nameof(MirrorToggle));
    public static string ChooseFolder => Get(nameof(ChooseFolder));
    public static string ChooseMirrorFolder => Get(nameof(ChooseMirrorFolder));
    public static string MirrorDetail => Get(nameof(MirrorDetail));
    public static string SwitchMicrophone => Get(nameof(SwitchMicrophone));
    public static string UpdateDeepgramKey => Get(nameof(UpdateDeepgramKey));
    public static string Reconnect => Get(nameof(Reconnect));
    public static string ReconnectHint => Get(nameof(ReconnectHint));
    public static string MoreAfterRecording => Get(nameof(MoreAfterRecording));
    public static string ProcessingInProgress => Get(nameof(ProcessingInProgress));
    public static string OpenSetup => Get(nameof(OpenSetup));
    public static string Done => Get(nameof(Done));
    public static string SettingsSaveFailed => Get(nameof(SettingsSaveFailed));

    // Onboarding
    public static string SetUpTitle => Get(nameof(SetUpTitle));
    public static string SetUpSubtitle => Get(nameof(SetUpSubtitle));
    public static string StepStorage => Get(nameof(StepStorage));
    public static string StepAi => Get(nameof(StepAi));
    public static string StepPermissions => Get(nameof(StepPermissions));
    public static string StepLabel => Get(nameof(StepLabel));
    public static string SetUpLater => Get(nameof(SetUpLater));
    public static string Back => Get(nameof(Back));
    public static string Next => Get(nameof(Next));
    public static string SectionPermissions => Get(nameof(SectionPermissions));
    public static string PermissionMicrophone => Get(nameof(PermissionMicrophone));
    public static string PermissionMicrophoneDetail => Get(nameof(PermissionMicrophoneDetail));
    public static string PermissionMicrophoneDenied => Get(nameof(PermissionMicrophoneDenied));
    public static string Allow => Get(nameof(Allow));
    public static string OpenSettingsButton => Get(nameof(OpenSettingsButton));
    public static string FillLater => Get(nameof(FillLater));
    public static string DeepgramKeyMissing => Get(nameof(DeepgramKeyMissing));
    public static string OpenAiTranscriptionKeyMissing => Get(nameof(OpenAiTranscriptionKeyMissing));
    public static string LocalWhisperMissing => Get(nameof(LocalWhisperMissing));

    // Folder editor
    public static string FolderCreateTitle => Get(nameof(FolderCreateTitle));
    public static string FolderEditTitle => Get(nameof(FolderEditTitle));
    public static string Close => Get(nameof(Close));
    public static string NameAndIcon => Get(nameof(NameAndIcon));
    public static string FolderName => Get(nameof(FolderName));
    public static string ChooseIcon => Get(nameof(ChooseIcon));
    public static string FolderColorLabel => Get(nameof(FolderColorLabel));
    public static string FolderDescription => Get(nameof(FolderDescription));
    public static string FolderDescriptionPlaceholder => Get(nameof(FolderDescriptionPlaceholder));
    public static string Workspace => Get(nameof(Workspace));
    public static string PrivateFolders => Get(nameof(PrivateFolders));
    public static string Create => Get(nameof(Create));
    public static string Save => Get(nameof(Save));
    public static string FolderIconFolder => Get(nameof(FolderIconFolder));
    public static string FolderIconWork => Get(nameof(FolderIconWork));
    public static string FolderIconTeam => Get(nameof(FolderIconTeam));
    public static string FolderIconIdeas => Get(nameof(FolderIconIdeas));
    public static string FolderIconLearning => Get(nameof(FolderIconLearning));
    public static string FolderIconLeaf => Get(nameof(FolderIconLeaf));
    public static string FolderIconStar => Get(nameof(FolderIconStar));
    public static string FolderIconHeart => Get(nameof(FolderIconHeart));
    public static string FolderIconHome => Get(nameof(FolderIconHome));
    public static string FolderIconProjects => Get(nameof(FolderIconProjects));
    public static string ColorBlue => Get(nameof(ColorBlue));
    public static string ColorGreen => Get(nameof(ColorGreen));
    public static string ColorPurple => Get(nameof(ColorPurple));
    public static string ColorOrange => Get(nameof(ColorOrange));
    public static string ColorPink => Get(nameof(ColorPink));
    public static string ColorTeal => Get(nameof(ColorTeal));
    public static string ColorRed => Get(nameof(ColorRed));
    public static string ColorYellow => Get(nameof(ColorYellow));
    public static string ColorBrown => Get(nameof(ColorBrown));
    public static string ColorGray => Get(nameof(ColorGray));
}
