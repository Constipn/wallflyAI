import AppKit
import Combine
import AVFoundation
import TeamsDetectionKit

typealias MeetingTaskExtraction = (Meeting, AppSettings, String, @escaping @Sendable (String) async -> Void) async throws -> [TaskDraft]
typealias MeetingFullEnhancement = (Meeting, AppSettings, String, @escaping @Sendable (String) async -> Void) async throws -> MeetingEnhancementResult
typealias MeetingPersonSummary = (Meeting, AppSettings, String) async throws -> PersonSummaryRun

enum MeetingWorkspacePresentation {
    case overview, preview, editor
}

/// The one action the error banner offers next to a message.
struct ErrorRecovery: Equatable {
    enum Action: Equatable { case openSettings, retryEnhancement(UUID), retryTaskExtraction(UUID) }
    let message: String
    let action: Action
}

@MainActor
final class MeetingStore: ObservableObject {
    @Published var meetings: [Meeting] = []
    @Published var selectedID: UUID?
    @Published private(set) var workspacePresentation: MeetingWorkspacePresentation = .overview
    /// Both library presentations retain the sidebar; only the full editor hides it.
    var showingOverview: Bool { workspacePresentation != .editor }
    @Published private(set) var notePresentationID = UUID()
    @Published var presentationMode = false
    @Published private(set) var enhancingID: UUID?
    @Published private(set) var extractingTasksID: UUID?
    @Published private(set) var taskProgress = ""
    @Published var taskReviewMeeting: Meeting?
    /// Meetings whose acoustic speaker run is waiting or running (in start order; the first one runs).
    @Published private(set) var diarizationQueue: [UUID] = []
    @Published private(set) var diarizationProgress = ""
    @Published private(set) var folderCatalog = FolderCatalog()
    @Published private(set) var libraryScope: MeetingLibraryScope = .all
    @Published private(set) var folderLibraryAvailable = true
    @Published private(set) var personCatalog = PersonCatalog()
    @Published private(set) var personCatalogAvailable = true
    @Published private(set) var personSummaryMeetingID: UUID?
    @Published private(set) var personSummaryErrors: [UUID: String] = [:]
    @Published private(set) var transcriptMirrorErrors: [UUID: String] = [:]
    @Published var requestedPersonSource: PersonSource?
    @Published var settings = AppSettings()
    @Published var apiKey = ""
    @Published var anthropicAPIKey = ""
    @Published var showOnboarding = false
    @Published var isSettingUp = false
    @Published var deepgramAPIKey = ""
    @Published private(set) var transcriptionStates: [Speaker: String] = [:]
    private var transcriptionEvents = TranscriptionEventInbox()
    @Published var isRecording = false
    @Published var isStarting = false
    @Published var isStopping = false
    @Published var isEnhancing = false
    @Published private(set) var isMovingLibrary = false
    @Published var status = "Ready to record"
    @Published var errorMessage: String?
    /// What the error banner can offer for the current `errorMessage`; ignored once another message replaces it.
    @Published private(set) var errorRecovery: ErrorRecovery?
    var currentErrorRecovery: ErrorRecovery.Action? { errorRecovery?.message == errorMessage ? errorRecovery?.action : nil }
    let recordingActivity = RecordingActivity()
    var elapsed: TimeInterval {
        get { recordingActivity.elapsed }
        set { if recordingActivity.elapsed != newValue { recordingActivity.elapsed = newValue } }
    }
    @Published var microphones: [MicrophoneDevice] = CoreAudioDevices.microphones()
    @Published private(set) var actualMicrophoneName = ""
    @Published private(set) var captureStatus = ""
    private var captureEvents = CaptureEventInbox()
    private var captureState: CaptureState?
    private var captureSourceStates: [CaptureSource: CaptureState] = [:]
    @Published var audioSources: [AudioSource] = []
    @Published private(set) var recordingAudioSelection: RecordingAudioSelection?
    @Published var enhancementProgress = ""
    @Published private(set) var teamsSpeakerStatus: TeamsSpeakerStatus = .disabled
    @Published private(set) var teamsSpeakerStatusText = TeamsSpeakerStatus.disabled.displayName
    private(set) var teamsSpeakerDiagnostics = TeamsSpeakerDiagnostics()
    private let extractMeetingTasks: MeetingTaskExtraction
    private let enhanceMeetingAll: MeetingFullEnhancement
    private let summarizePeople: MeetingPersonSummary
    private let transcriptMirror: TranscriptMirror
    private let acousticDiarizer: any AcousticDiarizer
    /// Tail of the serial chain of acoustic speaker runs; cancelled on quit.
    private var diarizationTask: Task<Void, Never>?
    /// The diarizer call of the running acoustic run (off the main actor); cancelled on quit.
    private var diarizationWork: Task<[AcousticSegment], Error>?
    @Published private(set) var repository: MeetingRepository
    private var localWhisperServer: LocalWhisperServer?
    private var capture: AudioCapture?
    private var mePipeline: (any AudioTranscribing)?
    private var othersPipeline: (any AudioTranscribing)?
    private var timer: Timer?
    private var saveWork: Task<Void, Never>?
    private var saveFirstPending: Date?
    private var dirtyMeetingIDs = Set<UUID>()
    /// Bumped on every change and every successful write, so a background write only clears what it actually saved.
    private var revisions: [UUID: Int] = [:]
    private var writer: MeetingWriter
    /// Seconds between background writes of the recording meeting; nil writes only at stop/quit.
    private let checkpointInterval: TimeInterval?
    private var lastCheckpoint: Date?
    private let saveDebounce: TimeInterval
    private let saveDebounceCap: TimeInterval
    private var recordingOrigin: Date?
    private var recordingHostOrigin: Double?
    private var baseDuration: TimeInterval = 0
    @Published private(set) var activeID: UUID?
    private var audioFailureHandled = false
    private let transcriptInbox = TranscriptInbox()
    private var interruptionObserver: NSObjectProtocol?
    private var activeSessionID: UUID?
    private var isQuitting = false
    private var teamsWatcher: TeamsSpeakerWatcher?
    private var attributionSession: TeamsAttributionSession?
    private let teamsInbox = TeamsObservationInbox()
    private let audioRouting: MeetingAudioRouting
    private let recordingAttempt: ((RecordingAudioSelection) async throws -> Void)?
    private var activeCaptureAttemptID: UUID?
    private let meetingEndWatch: MeetingEndWatch?
    private var meetingEndTask: Task<Void, Never>?

    var selectedMeeting: Meeting? { meetingSnapshot(for: selectedID) }
    var activeMeeting: Meeting? { meetingSnapshot(for: activeID) }
    private func meetingSnapshot(for id: UUID?) -> Meeting? {
        guard var meeting = meetings.first(where: { $0.id == id }) else { return nil }
        if id == activeID, recordingOrigin != nil { meeting.duration = elapsed }
        return meeting
    }
    var busy: Bool { isMovingLibrary || isQuitting || isStarting || isRecording || isStopping || isEnhancing || extractingTasksID != nil || personSummaryMeetingID != nil }
    var people: [PersonContact] { PersonDirectory.contacts(meetings: meetings, catalog: personCatalog) }
    var folders: [MeetingFolder] { folderCatalog.folders }
    var selectedFolder: MeetingFolder? {
        guard case .folder(let id) = libraryScope else { return nil }
        return folders.first { $0.id == id }
    }
    var visibleMeetings: [Meeting] {
        meetings.filter { meeting in
            switch libraryScope {
            case .all: return true
            case .unfiled: return folderCatalog.folderID(for: meeting.id) == nil
            case .folder(let id): return folderCatalog.folderID(for: meeting.id) == id
            }
        }.sorted { $0.createdAt > $1.createdAt }
    }

    // Isolated stores used for previews/tests must never alter the user's Keychain.
    private let managesCredentials: Bool

    init(repository: MeetingRepository = MeetingRepository(), loadCredentials: Bool = true,
         localWhisperServer: LocalWhisperServer? = nil,
         mePipeline: (any AudioTranscribing)? = nil, othersPipeline: (any AudioTranscribing)? = nil,
         audioRouting: MeetingAudioRouting? = nil,
         recordingAttempt: ((RecordingAudioSelection) async throws -> Void)? = nil,
         meetingEndWatch: MeetingEndWatch? = nil,
         extractMeetingTasks: @escaping MeetingTaskExtraction = { meeting, settings, key, progress in
             try await TaskExtractor().extract(meeting, settings: settings, apiKey: key, progress: progress)
         },
         enhanceMeetingAll: @escaping MeetingFullEnhancement = { meeting, settings, key, progress in
             try await NotesEnhancer().enhanceAll(meeting, settings: settings, apiKey: key, progress: progress)
         },
         summarizePeople: @escaping MeetingPersonSummary = { meeting, settings, key in
             try await PersonSummarizer().summarize(meeting, settings: settings, apiKey: key)
         },
         acousticDiarizer: (any AcousticDiarizer)? = nil,
         writer: MeetingWriter? = nil, checkpointInterval: TimeInterval? = 30,
         saveDebounce: TimeInterval = 1.5, saveDebounceCap: TimeInterval = 5) {
        self.repository = repository
        let mirror = TranscriptMirror()
        self.transcriptMirror = mirror
        self.writer = writer ?? MeetingWriter(repository: repository, mirror: mirror)
        self.checkpointInterval = checkpointInterval
        self.saveDebounce = saveDebounce
        self.saveDebounceCap = saveDebounceCap
        self.acousticDiarizer = acousticDiarizer ?? FluidAudioDiarizer()
        self.managesCredentials = loadCredentials
        self.localWhisperServer = localWhisperServer
        self.mePipeline = mePipeline
        self.othersPipeline = othersPipeline
        self.audioRouting = audioRouting ?? MeetingAudioRouting()
        self.recordingAttempt = recordingAttempt
        // Tests that stub the capture must not drive the real Accessibility probe.
        if let meetingEndWatch { self.meetingEndWatch = meetingEndWatch }
        else if recordingAttempt == nil { let probe = TeamsAudioProbe(); self.meetingEndWatch = MeetingEndWatch(sample: { await probe.sample() }) }
        else { self.meetingEndWatch = nil }
        self.extractMeetingTasks = extractMeetingTasks
        self.enhanceMeetingAll = enhanceMeetingAll
        self.summarizePeople = summarizePeople
        let hadSavedSettings = FileManager.default.fileExists(atPath: repository.root.appendingPathComponent("settings.json").path)
        do {
            meetings = try repository.load()
            personSummaryErrors = Dictionary(uniqueKeysWithValues: meetings.compactMap { meeting in
                meeting.personSummaryError.map { (meeting.id, $0) }
            })
            // A run cut off by quitting the app never finished; show it as interrupted instead of running.
            for index in meetings.indices where meetings[index].diarization?.status == .running {
                meetings[index].diarization?.status = .failed
                meetings[index].diarization?.message = "Interrupted because the app was quit."
                // Saved once, so the repair does not repeat at every launch; a failed write only repeats it.
                try? repository.save(meetings[index])
            }
            settings = try repository.loadSettings()
            // Persist the one-time provider migration before any later explicit choice.
            try repository.saveSettings(settings)
            selectedID = meetings.first?.id
        } catch { errorMessage = "Saved meetings could not be loaded: \(error.localizedDescription). The files are kept." }
        do {
            let saved = try repository.loadFolderCatalog()
            var updated = saved
            updated.assignMissingColors()
            if updated != saved { try repository.saveFolderCatalog(updated) }
            folderCatalog = updated
        }
        catch {
            folderLibraryAvailable = false
            errorMessage = "Folders could not be loaded: \(error.localizedDescription). Meetings and the folder file are kept."
        }
        do { personCatalog = try repository.loadPersonCatalog() }
        catch {
            personCatalogAvailable = false
            errorMessage = "People assignments could not be loaded. The file is kept: \(error.localizedDescription)"
        }
        configureTranscriptMirror()
        for meeting in meetings where meeting.audioDeletionReady == true { cleanupProcessedAudio(meeting.id) }
        if loadCredentials {
            anthropicAPIKey = SecureCredential.load(account: "anthropic-enhancement")
            if anthropicAPIKey.isEmpty { anthropicAPIKey = RuntimeEnvironment.anthropicAPIKey }
            apiKey = SecureCredential.load()
            if apiKey.isEmpty { apiKey = RuntimeEnvironment.apiKey }
            deepgramAPIKey = SecureCredential.load(account: "deepgram-transcription")
            if deepgramAPIKey.isEmpty { deepgramAPIKey = RuntimeEnvironment.deepgramAPIKey }
        }
        let environment = loadCredentials ? RuntimeEnvironment.values : [:]
        if !hadSavedSettings, settings.provider == .openai, let model = environment["OPENAI_NOTES_MODEL"] { settings.model = model }
        if !hadSavedSettings, settings.provider == .anthropic, let model = environment["ANTHROPIC_NOTES_MODEL"] { settings.model = model }
        showOnboarding = loadCredentials && !settings.onboardingCompleted
        interruptionObserver = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.willSleepNotification,
            object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in
                guard let self, self.isRecording else { return }
                self.errorMessage = "Recording ended because the Mac went to sleep. The meeting was saved."
                await self.stopRecording()
            }
        }
    }

    func createMeeting() {
        guard !busy else { return }
        flush()
        var meeting = Meeting(title: "New note")
        if settings.transcriptMirrorEnabled { meeting.transcriptMirror = settings.transcriptMirrorFolder }
        meetings.insert(meeting, at: 0)
        selectedID = meeting.id
        requestedPersonSource = nil
        workspacePresentation = .editor
        notePresentationID = UUID()
        elapsed = 0
        persist(meeting)
        if let folder = selectedFolder { moveMeeting(meeting.id, to: folder.id) }
    }
    func selectMeeting(_ id: UUID) {
        guard meetings.contains(where: { $0.id == id }) else { return }
        flush()
        selectedID = id
        requestedPersonSource = nil
        workspacePresentation = .editor
        notePresentationID = UUID()
        if !busy { elapsed = selectedMeeting?.duration ?? 0 }
    }
    func previewMeeting(_ id: UUID) {
        guard meetings.contains(where: { $0.id == id }) else { return }
        flush()
        selectedID = id
        requestedPersonSource = nil
        workspacePresentation = .preview
        if !busy { elapsed = selectedMeeting?.duration ?? 0 }
    }
    func showOverview() { flush(); workspacePresentation = .overview }
    func openActiveMeeting() { if let id = activeID { selectMeeting(id) } }
    func newNote() async {
        guard !showOnboarding, !isSettingUp, !busy else { return }
        createMeeting()
        await startRecording()
    }
    /// Use the same capture path as New note; a URL alone never authorizes recording.
    func openDetectedTeamsMeeting(_ url: URL, storage: DetectionStorage = .init(),
                                  start: (() async -> Void)? = nil) async {
        do {
            guard try storage.consume(url) != nil else { return }
        } catch { errorMessage = "The detected meeting could not be opened: \(error.localizedDescription)"; return }
        if busy { openActiveMeeting(); return }
        if let start { await start() }
        else { await newNote() }
    }
    func renameMeeting(_ id: UUID, title: String) {
        let title = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !busy, !title.isEmpty, let index = meetings.firstIndex(where: { $0.id == id }) else { return }
        // Commit the editor before mutating a different row, so its pending save is preserved.
        flush()
        meetings[index].title = title
        persist(meetings[index])
    }
    func selectLibrary(_ scope: MeetingLibraryScope) {
        if case .folder(let id) = scope, !folders.contains(where: { $0.id == id }) { return }
        flush()
        libraryScope = scope
        workspacePresentation = .overview
        selectVisibleMeetingIfNeeded()
    }
    private func selectVisibleMeetingIfNeeded() {
        if !visibleMeetings.contains(where: { $0.id == selectedID }) { selectedID = visibleMeetings.first?.id }
        if selectedID == nil, workspacePresentation == .preview { workspacePresentation = .overview }
        if !busy { elapsed = selectedMeeting?.duration ?? 0 }
    }
    @discardableResult
    func saveFolder(_ folder: MeetingFolder) -> Bool {
        guard folderLibraryAvailable else { return false }
        var folder = folder
        folder.name = folder.name.trimmingCharacters(in: .whitespacesAndNewlines)
        folder.description = folder.description.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !folder.name.isEmpty else { return false }
        if !MeetingFolder.icons.contains(folder.icon) { folder.icon = "folder.fill" }
        var updated = folderCatalog
        if let index = updated.folders.firstIndex(where: { $0.id == folder.id }) {
            folder.color = folder.color ?? updated.folders[index].color ?? updated.nextColor
            updated.folders[index] = folder
        } else {
            folder.color = folder.color ?? updated.nextColor
            updated.folders.append(folder)
        }
        return commitFolderCatalog(updated)
    }
    func moveMeeting(_ id: UUID, to folderID: UUID?) {
        guard folderLibraryAvailable, meetings.contains(where: { $0.id == id }) else { return }
        if let folderID, !folders.contains(where: { $0.id == folderID }) { return }
        flush()
        var updated = folderCatalog
        updated.assignments[id.uuidString] = folderID
        if commitFolderCatalog(updated), showingOverview { selectVisibleMeetingIfNeeded() }
    }
    func deleteFolder(_ id: UUID) {
        guard !busy, folderLibraryAvailable, folders.contains(where: { $0.id == id }) else { return }
        flush()
        var updated = folderCatalog
        updated.folders.removeAll { $0.id == id }
        updated.assignments = updated.assignments.filter { $0.value != id }
        guard commitFolderCatalog(updated) else { return }
        if libraryScope == .folder(id) { libraryScope = .unfiled }
        selectVisibleMeetingIfNeeded()
    }
    private func commitFolderCatalog(_ catalog: FolderCatalog) -> Bool {
        guard !isMovingLibrary else { return false }
        do { try repository.saveFolderCatalog(catalog); folderCatalog = catalog; return true }
        catch { errorMessage = "Folders could not be saved: \(error.localizedDescription)"; return false }
    }
    func updateTitle(_ text: String, meetingID: UUID? = nil) {
        mutateMeeting(meetingID) { $0.title = text }
    }
    func updateNotes(_ text: String, meetingID: UUID? = nil) {
        mutateMeeting(meetingID) { $0.notes = text }
    }
    private func mutateMeeting(_ meetingID: UUID?, _ update: (inout Meeting) -> Void) {
        guard !isMovingLibrary else { return }
        guard let id = meetingID ?? selectedID, id != enhancingID, id != extractingTasksID,
              let index = meetings.firstIndex(where: { $0.id == id }) else { return }
        update(&meetings[index])
        markDirty(id)
        // The recording meeting is written by the checkpoint and the final save.
        if id != activeID { scheduleSave() }
    }
    private func markDirty(_ id: UUID) {
        dirtyMeetingIDs.insert(id)
        revisions[id, default: 0] += 1
    }
    /// Trailing debounce, capped since the first pending change; the write itself runs off the main actor.
    private func scheduleSave() {
        let now = Date()
        let first = saveFirstPending ?? now
        saveFirstPending = first
        let delay = max(0, min(saveDebounce, saveDebounceCap - now.timeIntervalSince(first)))
        saveWork?.cancel()
        saveWork = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled, let self else { return }
            saveWork = nil; saveFirstPending = nil
            for meeting in meetings where dirtyMeetingIDs.contains(meeting.id) && meeting.id != activeID { enqueueSnapshot(meeting) }
        }
    }
    private func enqueueSnapshot(_ meeting: Meeting) {
        var snapshot = meeting
        if meeting.id == activeID, recordingOrigin != nil { snapshot.duration = elapsed }
        let id = meeting.id, revision = revisions[id, default: 0]
        writer.enqueue(snapshot, revision: revision) { [weak self] result in self?.backgroundWriteFinished(id, revision: revision, result) }
    }
    private func backgroundWriteFinished(_ id: UUID, revision: Int, _ result: Result<Void, Error>) {
        guard meetings.contains(where: { $0.id == id }) else { return }
        switch result {
        case .success: if revisions[id, default: 0] == revision { dirtyMeetingIDs.remove(id) }
        // The recording meeting changes every second, so a failure is reported whatever the revision.
        case .failure(let error): dirtyMeetingIDs.insert(id); errorMessage = Self.saveFailureMessage(error)
        }
    }
    private static func saveFailureMessage(_ error: Error) -> String {
        "The meeting could not be saved: \(error.localizedDescription). Export a copy before closing."
    }
    /// Synchronous write of every changed meeting; true when nothing is left unsaved.
    @discardableResult
    func flush() -> Bool {
        saveWork?.cancel(); saveWork = nil; saveFirstPending = nil
        for meeting in meetings where dirtyMeetingIDs.contains(meeting.id) { persist(meeting) }
        return dirtyMeetingIDs.isEmpty
    }
    private func saveNow(_ meeting: Meeting) throws {
        try writer.write(meeting)
        revisions[meeting.id, default: 0] += 1
    }
    private func persist(_ meeting: Meeting) {
        // Checkpoint live duration without publishing a copy of the entire library each tick.
        var meeting = meeting
        if meeting.id == activeID, recordingOrigin != nil { meeting.duration = elapsed }
        do { try saveNow(meeting); dirtyMeetingIDs.remove(meeting.id) }
        catch {
            markDirty(meeting.id)
            errorMessage = Self.saveFailureMessage(error)
        }
    }
    @discardableResult
    func saveSettings() -> Bool {
        guard !isMovingLibrary else { return false }
        do {
            if managesCredentials {
                try SecureCredential.save(apiKey)
                try SecureCredential.save(anthropicAPIKey, account: "anthropic-enhancement")
                try SecureCredential.save(deepgramAPIKey, account: "deepgram-transcription")
            }
            try repository.saveSettings(settings)
            configureTranscriptMirror()
            return true
        } catch { errorMessage = error.localizedDescription; return false }
    }

    @discardableResult
    func completeOnboarding() -> Bool {
        guard !busy else { return false }
        let previous = settings.onboardingCompleted
        settings.onboardingCompleted = true
        guard saveSettings() else { settings.onboardingCompleted = previous; return false }
        showOnboarding = false
        return true
    }

    var enhancementAPIKey: String { settings.provider == .anthropic ? anthropicAPIKey : apiKey }

    func selectEnhancementProvider(_ provider: EnhancementProvider) {
        guard provider != settings.provider else { return }
        settings.enhancementModels[settings.provider.rawValue] = settings.model
        settings.provider = provider
        settings.model = settings.enhancementModels[provider.rawValue] ?? SummaryModels.defaultModel(for: provider)
    }

    var enhancementConfigurationIssue: String? {
        if settings.provider == .disabled { return "Enhancement is turned off. Transcript and your own notes remain available." }
        if settings.model.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return "Choose a model for enhancement." }
        if [.openai, .anthropic].contains(settings.provider), enhancementAPIKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return "\(settings.provider.displayName) API key is missing. Transcript and your own notes are kept."
        }
        if settings.provider == .compatible, let issue = NotesEnhancer.endpointIssue(settings.endpoint) { return issue }
        return nil
    }

    var transcriptionConfigurationIssue: String? {
        switch settings.transcriptionProvider {
        case .deepgram: return deepgramAPIKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "Deepgram API key for transcription is missing." : nil
        case .openai: return apiKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "OpenAI API key for transcription is missing." : nil
        case .openWhispr: return LocalWhisperInstallation(appPath: settings.openWhisprAppPath, modelPath: settings.whisperModelPath).isAvailable ? nil : "OpenWhispr app or local Whisper model is missing."
        }
    }

    private func configureTranscriptMirror() {
        transcriptMirror.configure(enabled: settings.transcriptMirrorEnabled) { [weak self] id, message in
            guard let self, self.meetings.contains(where: { $0.id == id }) else { return }
            self.transcriptMirrorErrors[id] = message
        }
        if !settings.transcriptMirrorEnabled { transcriptMirrorErrors.removeAll() }
    }
    func chooseTranscriptMirrorFolder() {
        guard !busy else { return }
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true; panel.canChooseFiles = false; panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = "Choose folder"
        panel.message = "Also mirror transcripts of new meetings into this folder. Audio and notes stay where they are."
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let path = url.resolvingSymlinksInPath().standardizedFileURL.path
        let internalPath = repository.root.resolvingSymlinksInPath().standardizedFileURL.path
        guard path != internalPath, !path.hasPrefix(internalPath + "/") else {
            errorMessage = "Choose a folder outside the internal meeting library for mirroring."
            return
        }
        settings.transcriptMirrorFolder = TranscriptMirrorDestination(url: url)
        settings.transcriptMirrorEnabled = true
        saveSettings()
    }
    func revealTranscriptMirrorFolder() {
        guard let folder = settings.transcriptMirrorFolder else { return }
        do { NSWorkspace.shared.open(try folder.resolve()) }
        catch { errorMessage = "The transcript folder could not be opened: \(error.localizedDescription)" }
    }
    func retryTranscriptMirror(_ id: UUID) {
        guard settings.transcriptMirrorEnabled, let meeting = meetings.first(where: { $0.id == id }),
              meeting.transcriptMirror != nil else { return }
        enqueueSnapshot(meeting)
    }

    private func commitPersonCatalog(_ catalog: PersonCatalog) {
        guard !isMovingLibrary else { return }
        guard personCatalogAvailable else { return }
        do { try repository.savePersonCatalog(catalog); personCatalog = catalog }
        catch { errorMessage = "People assignments could not be saved: \(error.localizedDescription)" }
    }
    func renamePerson(_ id: UUID, name: String) {
        let name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty, name.count <= 160, people.contains(where: { $0.id == id }) else { return }
        var catalog = personCatalog; catalog.names[id.uuidString] = name
        commitPersonCatalog(catalog)
    }
    func mergePerson(_ source: UUID, into target: UUID) {
        guard !busy, source != target, let person = people.first(where: { $0.id == source }),
              let targetPerson = people.first(where: { $0.id == target }) else { return }
        var catalog = personCatalog
        for membership in person.memberships { catalog.assignments[membership.id] = target }
        catalog.names.removeValue(forKey: source.uuidString)
        catalog.names[target.uuidString] = targetPerson.name
        commitPersonCatalog(catalog)
    }
    func splitPersonMembership(_ membership: PersonMembership) {
        guard !busy else { return }
        var catalog = personCatalog
        // A fresh ID also allows detaching the original member of a merged contact.
        catalog.assignments[membership.id] = UUID()
        commitPersonCatalog(catalog)
    }
    func showPersonSource(_ source: PersonSource, in meetingID: UUID) {
        guard meetings.first(where: { $0.id == meetingID })?.personSources.contains(source) == true else { return }
        selectMeeting(meetingID)
        requestedPersonSource = source
    }
    /// Also used by the explicit retry action. Never called by loading or stopping a recording.
    func retryPersonSummary(_ id: UUID) async {
        guard !busy, settings.personSummariesEnabled else { return }
        await updatePersonSummary(id)
    }
    private func updatePersonSummary(_ id: UUID) async {
        guard settings.personSummariesEnabled, !isQuitting, !Task.isCancelled,
              let meeting = meetings.first(where: { $0.id == id }) else { return }
        guard !meeting.personSources.isEmpty else {
            if let index = meetings.firstIndex(where: { $0.id == id }), meeting.personSummary != nil || meeting.personSummaryError != nil {
                meetings[index].personSummary = nil; meetings[index].personSummaryError = nil
                personSummaryErrors[id] = nil
                persist(meetings[index])
            }
            return
        }
        personSummaryMeetingID = id
        if isEnhancing { enhancementProgress = "Creating person summaries…" }
        defer { personSummaryMeetingID = nil }
        do {
            let result = try await summarizePeople(meeting, settings, enhancementAPIKey)
            guard !isQuitting, !Task.isCancelled, let index = meetings.firstIndex(where: { $0.id == id }) else { return }
            guard meetings[index].personSourceFingerprint == meeting.personSourceFingerprint else {
                throw MeetingError.message("Speakers or transcript changed. Please enhance the person summary again.")
            }
            meetings[index].personSummary = result
            meetings[index].personSummaryError = nil
            persist(meetings[index])
            personSummaryErrors[id] = nil
        } catch {
            guard !isQuitting, !Task.isCancelled else { return }
            personSummaryErrors[id] = "The person summary could not be created: \(error.localizedDescription)"
            if let index = meetings.firstIndex(where: { $0.id == id }) {
                meetings[index].personSummaryError = personSummaryErrors[id]
                persist(meetings[index])
            }
        }
    }
    func refreshMicrophones() { microphones = CoreAudioDevices.microphones() }
    func restartDeepgramTranscription() {
        guard isRecording, !isStopping, !deepgramAPIKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        (mePipeline as? DeepgramTranscriptionPipeline)?.restart(apiKey: deepgramAPIKey)
        (othersPipeline as? DeepgramTranscriptionPipeline)?.restart(apiKey: deepgramAPIKey)
    }

    private func drainTranscriptionEvents(sessionID: UUID, meetingID: UUID) {
        guard activeSessionID == sessionID, activeID == meetingID,
              let index = meetings.firstIndex(where: { $0.id == meetingID }) else { return }
        for event in transcriptionEvents.drain() {
            switch event {
            case .state(let speaker, let state): transcriptionStates[speaker] = state
            case .gap(var gap):
                gap.start += baseDuration
                meetings[index].appendTranscriptionGap(gap)
                markDirty(meetingID)
            case .anchor(let anchor):
                // Same session base that receive(_:) adds to segment offsets.
                meetings[index].appendAudioAnchor(AudioAnchor(file: anchor.file, frame: anchor.frame, offset: anchor.offset + baseDuration))
                markDirty(meetingID)
            }
        }
    }
    func applyMicrophoneSelection() {
        capture?.selectMicrophone(uid: settings.microphoneUID)
        saveSettings()
    }
    private func drainCaptureEvents(sessionID: UUID, meetingID: UUID) {
        guard activeSessionID == sessionID, let index = meetings.firstIndex(where: { $0.id == meetingID }) else { return }
        let events = captureEvents.drain()
        for event in events {
            switch event {
            case .microphone(let name, let fallback, let reason):
                actualMicrophoneName = fallback ? "\(name) · Fallback (\(reason ?? "Microphone unavailable"))" : name
            case .state(let state):
                captureState = state; updateCaptureStatus()
            case .source(let source, let state):
                captureSourceStates[source] = state; updateCaptureStatus()
            case .gap(var gap):
                guard gap.duration > 0 else { continue }
                gap.start += baseDuration
                meetings[index].appendRecordingGap(gap)
                markDirty(meetingID)
            }
        }
    }
    private func updateCaptureStatus() {
        guard let captureState else { return }
        switch captureState {
        case .starting: captureStatus = "Preparing audio sources…"
        case .running: captureStatus = "Echo cancellation active"
        case .recovering: captureStatus = "Restoring audio connection…"
        case .degraded:
            // The others track never degrades; a degraded reference is the Teams-mode echo reference.
            var parts: [String] = []
            if captureSourceStates[.microphone] == .degraded { parts.append("Microphone disconnected – recording Others only") }
            if captureSourceStates[.reference] == .degraded { parts.append("Echo reference missing – microphone without echo cancellation") }
            captureStatus = parts.isEmpty ? "Audio connection limited" : parts.joined(separator: " · ")
        case .stopping: captureStatus = "Finishing audio buffers…"
        case .failed: captureStatus = "Audio connection lost"
        }
    }
    func refreshAudioSources() async {
        refreshMicrophones()
        do { audioSources = try await AudioCapture.sources() }
        catch { errorMessage = error.localizedDescription }
    }

    func setTeamsSpeakerNames(_ enabled: Bool) {
        guard !busy else { return }
        settings.teamsSpeakerNames = enabled
        if enabled && !settings.automaticMeetingAudioSource { settings.audioSourceBundleID = TeamsAccessibility.bundleID }
        refreshTeamsSpeakerStatus()
    }
    func refreshTeamsSpeakerStatus() {
        guard !isRecording && !isStarting && !isStopping else { return }
        if !settings.teamsSpeakerNames { teamsSpeakerStatus = .disabled }
        else if settings.automaticMeetingAudioSource { teamsSpeakerStatus = TeamsAccessibility.trusted ? .automatic : .needsAccessibility }
        else if settings.audioSourceBundleID != TeamsAccessibility.bundleID { teamsSpeakerStatus = .wrongAudioSource }
        else if !TeamsAccessibility.trusted { teamsSpeakerStatus = .needsAccessibility }
        else if NSRunningApplication.runningApplications(withBundleIdentifier: TeamsAccessibility.bundleID).isEmpty { teamsSpeakerStatus = .teamsNotRunning }
        else { teamsSpeakerStatus = .ready }
        if teamsSpeakerStatusText != teamsSpeakerStatus.displayName { teamsSpeakerStatusText = teamsSpeakerStatus.displayName }
    }
    func requestTeamsAccessibility() { TeamsAccessibility.request(); refreshTeamsSpeakerStatus() }

    var meetingAudioChoice: MeetingAudioChoice {
        get { settings.automaticMeetingAudioSource ? .automatic : .manual(settings.audioSourceBundleID) }
        set {
            guard !busy else { return }
            switch newValue {
            case .automatic: settings.automaticMeetingAudioSource = true
            case .manual(let bundleID):
                settings.audioSourceBundleID = bundleID
                settings.automaticMeetingAudioSource = false
            }
            refreshTeamsSpeakerStatus()
        }
    }

    var recordingAudioDescription: String {
        guard let selection = recordingAudioSelection else { return "" }
        return (["Meeting audio: " + selection.displayName, selection.explanation]).filter { !$0.isEmpty }.joined(separator: " · ")
    }

    func beginTeamsAttribution(origin: TimeInterval, sessionID: UUID, meetingID: UUID, enabled: Bool,
                               startMonitor: Bool = true, policy: TeamsAttributionPolicy = .init()) {
        guard enabled, activeSessionID == sessionID, activeID == meetingID else { return }
        attributionSession = TeamsAttributionSession(id: sessionID, origin: origin, baseDuration: baseDuration, policy: policy)
        teamsSpeakerDiagnostics = TeamsSpeakerDiagnostics()
        guard startMonitor else { return }
        let inbox = teamsInbox
        let monitor = TeamsSpeakerWatcher { [weak self] observation in
            inbox.append(sessionID: sessionID, observation: observation)
            Task { @MainActor in self?.drainTeamsObservations() }
        }
        teamsWatcher = monitor
        monitor.start()
    }
    private func drainTeamsObservations() {
        let pending = teamsInbox.drain()
        guard var session = attributionSession, activeSessionID == session.id,
              let index = meetings.firstIndex(where: { $0.id == activeID }) else { return }
        var participants = meetings[index].participants ?? []
        var changed = false
        for (sessionID, observation) in pending where sessionID == session.id {
            session.ingest(observation, participants: &participants)
        }
        if meetings[index].participants ?? [] != participants { meetings[index].participants = participants; changed = true }
        attributionSession = session
        if changed { markDirty(meetings[index].id) }
        reconcileTeamsTranscript()
    }

    /// The same inbox path is used by the native monitor and controlled integration tests.
    func receiveTeamsObservation(_ observation: TeamsSpeakerObservation, sessionID: UUID) {
        teamsInbox.append(sessionID: sessionID, observation: observation)
        drainTeamsObservations()
    }

    /// `arrived` is a segment that was just received: it is labeled even when it arrives after the revision window.
    func reconcileTeamsTranscript(all: Bool = false, arrived: UUID? = nil) {
        guard let session = attributionSession, session.id == activeSessionID,
              let index = meetings.firstIndex(where: { $0.id == activeID }) else { return }
        let cutoff = session.latestOffset - session.policy.revisionWindow
        // Utterances span several recognition results, so the batch also reads words just before the window
        // (and around a late result); only utterances ending inside the window, containing the arrived result,
        // or all of them in the final pass are relabeled.
        let transcript = meetings[index].transcript
        let lookback = session.policy.unitLookback
        let late = transcript.first { $0.id == arrived && $0.speaker == .others }.map { ($0.offset - lookback)...($0.offset + $0.duration + lookback) }
        let batch = transcript.indices.filter { i in
            let segment = transcript[i], end = segment.offset + segment.duration
            guard segment.speaker == .others, segment.sessionID == session.id else { return false }
            return all || end >= cutoff - lookback || late.map { end >= $0.lowerBound && segment.offset <= $0.upperBound } == true
        }
        let annotated = session.annotate(batch.map { transcript[$0] }, revisableAfter: all ? nil : cutoff,
                                         alwaysRevising: arrived.map { [$0] } ?? [], finalPass: all)
        var changed = false
        for (i, updated) in zip(batch, annotated) where updated != transcript[i] {
            meetings[index].transcript[i] = updated; changed = true
        }
        if changed { markDirty(meetings[index].id) }
        refreshTeamsSpeakerPresentation(all: all)
    }

    private func refreshTeamsSpeakerPresentation(all: Bool = false) {
        guard let session = attributionSession, session.id == activeSessionID,
              let meeting = meetings.first(where: { $0.id == activeID }) else { return }
        let cutoff = session.latestOffset - session.policy.revisionWindow
        let recent = all ? meeting.transcript : meeting.transcript.filter { $0.offset + $0.duration >= cutoff }
        teamsSpeakerDiagnostics = session.diagnostics(for: recent, now: ProcessInfo.processInfo.systemUptime)
        var status = session.lastObservation?.status ?? .waitingForSpeaker
        if teamsSpeakerDiagnostics.observationAge > session.policy.evidenceLifetime { status = .unavailable }
        else if (status == .active || status == .waitingForSpeaker), teamsSpeakerDiagnostics.latestRemoteHasWordTimes == false {
            status = .missingWordTimes
        }
        if teamsSpeakerStatus != status { teamsSpeakerStatus = status }
        let count = teamsSpeakerDiagnostics.participantCount
        let text = status.displayName + (count > 0 ? " · \(count) participant\(count == 1 ? "" : "s")" : "")
        if teamsSpeakerStatusText != text { teamsSpeakerStatusText = text }
    }

    @discardableResult
    func editParticipant(meetingID: UUID, participantID: UUID?, name: String, references: [TranscriptReference] = []) -> Bool {
        let name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !busy, selectedID == meetingID, !name.isEmpty, name.count <= 160,
              let index = meetings.firstIndex(where: { $0.id == meetingID }) else { return false }
        flush()
        var meeting = meetings[index]
        var participants = meeting.participants ?? []
        if let participantID {
            guard let participantIndex = participants.firstIndex(where: { $0.id == participantID }) else { return false }
            // Naming a "Speaker k" binds its voice to that person, also for later runs.
            if Meeting.acousticSpeakerID(of: participants[participantIndex]) != nil {
                guard meeting.bindAcousticSpeaker(participantID: participantID, name: name) else { return false }
                return saveParticipantEdit(meeting, index: index)
            }
            participants[participantIndex].name = name
        } else {
            guard !references.isEmpty, references.allSatisfy({ reference in
                guard let segment = meeting.transcript.first(where: { $0.id == reference.segmentID && $0.speaker == .others }) else { return false }
                if let range = reference.wordRange { return range.lowerBound >= 0 && range.upperBound <= (segment.words?.count ?? 0) && !range.isEmpty }
                return segment.words?.isEmpty ?? true
            }) else { return false }
            let participant = MeetingParticipant(name: name, colorIndex: participants.count)
            participants.append(participant)
            meeting.participants = participants
            meeting.assign(references, to: participant.id)
        }
        meeting.participants = participants
        return saveParticipantEdit(meeting, index: index)
    }
    func assignTranscript(meetingID: UUID, references: [TranscriptReference], participantID: UUID?) {
        guard !busy, selectedID == meetingID, let index = meetings.firstIndex(where: { $0.id == meetingID }) else { return }
        flush()
        var meeting = meetings[index]
        meeting.assign(references, to: participantID)
        _ = saveParticipantEdit(meeting, index: index)
    }
    private func saveParticipantEdit(_ meeting: Meeting, index: Int) -> Bool {
        guard !isMovingLibrary else { return false }
        do { try saveNow(meeting); meetings[index] = meeting; return true }
        catch { errorMessage = "Speaker corrections could not be saved: \(error.localizedDescription)"; return false }
    }

    func startRecording() async {
        guard !busy else { return }
        refreshTeamsSpeakerStatus()
        if selectedMeeting == nil { createMeeting() }
        guard let id = selectedID, let sessionID = beginRecordingSession(for: id) else { return }
        let requestedSettings = settings
        do {
            if requestedSettings.automaticMeetingAudioSource { status = "Choosing meeting audio automatically…" }
            try await audioRouting.start(settings: requestedSettings) { selection in
                try self.ensureRecordingSession(sessionID)
                self.recordingAudioSelection = selection
                if !selection.settings.usesTeamsNames {
                    self.teamsSpeakerStatus = !selection.settings.teamsSpeakerNames ? .disabled :
                        (requestedSettings.automaticMeetingAudioSource ? .meetingNotFound : .wrongAudioSource)
                    self.teamsSpeakerStatusText = self.teamsSpeakerStatus.displayName
                }
                do {
                    if let recordingAttempt = self.recordingAttempt { try await recordingAttempt(selection) }
                    else { try await self.startRecordingAttempt(selection, sessionID: sessionID, meetingID: id) }
                } catch {
                    // An app disappearing before the clock starts may be retried with
                    // system audio. Fence callbacks and discard that attempt's resources.
                    if error is MeetingAudioSourceUnavailable, self.recordingHostOrigin == nil {
                        await self.discardUnstartedRecordingAttempt(meetingID: id)
                    }
                    throw error
                }
            }
            try ensureRecordingSession(sessionID)
            if audioFailureHandled { throw MeetingError.message(errorMessage ?? "Transcription could not be started.") }
            captureDidStart(at: Date().addingTimeInterval((recordingHostOrigin ?? ProcessInfo.processInfo.systemUptime) - ProcessInfo.processInfo.systemUptime))
            status = "Recording · " + (recordingAudioSelection?.settings.transcriptionStatus ?? requestedSettings.transcriptionStatus)
            timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
                Task { @MainActor in self?.tick() }
            }
            timer?.tolerance = 0.1
            startMeetingEndWatch(sessionID: sessionID)
        } catch {
            guard !isQuitting, activeSessionID == sessionID else { return }
            activeCaptureAttemptID = nil
            await capture?.stop(); capture = nil
            await mePipeline?.cancel(); mePipeline = nil
            await othersPipeline?.cancel(); othersPipeline = nil
            await teamsWatcher?.stop(); teamsWatcher = nil
            drainCaptureEvents(sessionID: sessionID, meetingID: id)
            drainTranscript(meetingID: id)
            drainTranscriptionEvents(sessionID: sessionID, meetingID: id)
            _ = teamsInbox.drain(); attributionSession = nil; activeSessionID = nil
            recordingStartFailed(error.localizedDescription)
            refreshTeamsSpeakerStatus()
        }
    }

    private func startMeetingEndWatch(sessionID: UUID) {
        meetingEndTask?.cancel(); meetingEndTask = nil
        guard settings.autoStopWhenMeetingEnds, let watch = meetingEndWatch else { return }
        meetingEndTask = Task { [weak self] in
            var policy = MeetingEndPolicy()
            while !Task.isCancelled {
                let sample = await watch.sample()
                guard !Task.isCancelled, let self, self.activeSessionID == sessionID, self.isRecording, !self.isStopping,
                      self.settings.autoStopWhenMeetingEnds else { return }
                if policy.observe(sample, at: watch.now()) {
                    // Detach first: stopRecording cancels the watch and must not cancel this task mid-stop.
                    self.meetingEndTask = nil
                    await self.stopRecording()
                    if !self.isRecording, self.activeSessionID == nil { self.status = "Meeting ended · Recording stopped automatically" }
                    return
                }
                try? await watch.pause()
            }
        }
    }

    private func ensureRecordingSession(_ sessionID: UUID) throws {
        try Task.checkCancellation()
        guard !isQuitting, activeSessionID == sessionID else { throw CancellationError() }
    }

    private func discardUnstartedRecordingAttempt(meetingID: UUID) async {
        let attemptID = activeCaptureAttemptID
        activeCaptureAttemptID = nil
        await capture?.stop(); capture = nil
        await mePipeline?.cancel(); mePipeline = nil
        await othersPipeline?.cancel(); othersPipeline = nil
        shutdownTranscription()
        transcriptionStates = [:]
        transcriptionEvents = TranscriptionEventInbox()
        captureEvents = CaptureEventInbox()
        captureStatus = ""; actualMicrophoneName = ""; captureState = nil; captureSourceStates = [:]
        audioFailureHandled = false
        _ = transcriptInbox.drain()
        if let attemptID, let index = meetings.firstIndex(where: { $0.id == meetingID }) {
            let paths = meetings[index].audioFiles.filter { URL(fileURLWithPath: $0).lastPathComponent.hasPrefix(attemptID.uuidString + "-") }
            for path in paths { try? FileManager.default.removeItem(atPath: path) }
            meetings[index].audioFiles.removeAll { paths.contains($0) }
            meetings[index].audioDeletionPending?.removeAll { paths.contains($0) }
            meetings[index].audioAnchors?.removeAll { $0.file.hasPrefix(attemptID.uuidString + "-") }
            persist(meetings[index])
        }
    }

    private func startRecordingAttempt(_ selection: RecordingAudioSelection, sessionID: UUID, meetingID id: UUID) async throws {
        let sessionSettings = selection.settings
        let useTeamsNames = sessionSettings.usesTeamsNames
        let improvedCapture = CapturePath.improved
        let attemptID = UUID()
        activeCaptureAttemptID = attemptID
        // Check the selected app before permissions, model loading or audio files.
        try MeetingAudioSource.requireRunning(bundleID: sessionSettings.audioSourceBundleID)
        try AudioCapture.authorizeSystemAudio()
        guard sessionSettings.transcriptionProvider != .openai || !apiKey.isEmpty else { throw MeetingError.message("No OpenAI API key found. Set OPENAI_API_KEY in .env or enter it in Settings.") }
        guard sessionSettings.transcriptionProvider != .deepgram || !deepgramAPIKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw MeetingError.message("No Deepgram API key found. Set DEEPGRAM_API_KEY in .env or enter it in Settings.")
        }
        transcriptionStates = [:]
        transcriptionEvents = TranscriptionEventInbox()
        let locale = Locale(identifier: sessionSettings.locale == "auto" ? "und" : sessionSettings.locale)
        try ensureRecordingSession(sessionID)
        if sessionSettings.transcriptionProvider == .openWhispr {
            status = "Preparing local Whisper Turbo…"
            let server = localWhisperServer ?? LocalWhisperServer()
            localWhisperServer = server
            try await server.prepare(LocalWhisperInstallation(appPath: sessionSettings.openWhisprAppPath, modelPath: sessionSettings.whisperModelPath), wordTimestamps: useTeamsNames)
        }
        try ensureRecordingSession(sessionID)
        try await AudioCapture.requestMicrophone()
        try ensureRecordingSession(sessionID)
        let recordingSuffix = attemptID.uuidString
        var paths: [String] = []
        let directory = sessionSettings.keepAudio ? try repository.audioDirectory(for: id) : nil
        let inbox = transcriptInbox
        let onSegment: @Sendable (TranscriptSegment) -> Void = { [weak self] segment in
            var segment = segment
            segment.sessionID = sessionID
            inbox.append(segment)
            Task { @MainActor in self?.drainTranscript(meetingID: id) }
        }
        let onError: @Sendable (String) -> Void = { [weak self] text in
            Task { @MainActor in
                guard let self, self.activeSessionID == sessionID, self.activeCaptureAttemptID == attemptID, self.activeID == id else { return }
                await self.handleCaptureFailure(text)
            }
        }
        let meURL = directory?.appendingPathComponent("\(recordingSuffix)-me.caf")
        let othersURL = directory?.appendingPathComponent("\(recordingSuffix)-others.caf")
        // State, gap and audio-anchor events from every provider share one inbox.
        let events = transcriptionEvents
        let onEvent: @Sendable (TranscriptionEvent) -> Void = { [weak self] event in
            if events.append(event) {
                Task { @MainActor in guard let self, self.activeCaptureAttemptID == attemptID else { return }; self.drainTranscriptionEvents(sessionID: sessionID, meetingID: id) }
            }
        }
        if sessionSettings.transcriptionProvider == .deepgram {
            mePipeline = try DeepgramTranscriptionPipeline(speaker: .me, locale: locale, recordingURL: meURL,
                apiKey: deepgramAPIKey, onSegment: onSegment, onEvent: onEvent, onError: onError,
                sampleAccurateTimestamps: improvedCapture)
            othersPipeline = try DeepgramTranscriptionPipeline(speaker: .others, locale: locale, recordingURL: othersURL,
                apiKey: deepgramAPIKey, onSegment: onSegment, onEvent: onEvent, onError: onError,
                sampleAccurateTimestamps: improvedCapture)
        } else if sessionSettings.transcriptionProvider == .openWhispr {
            let server = localWhisperServer!
            mePipeline = try await ChunkedTranscriptionPipeline(speaker: .me, locale: locale, recordingURL: meURL, apiKey: "", model: "large-v3-turbo", onSegment: onSegment, onError: onError, onEvent: onEvent, localServer: server, wordTimestamps: useTeamsNames, sampleAccurateTimestamps: improvedCapture)
            othersPipeline = try await ChunkedTranscriptionPipeline(speaker: .others, locale: locale, recordingURL: othersURL, apiKey: "", model: "large-v3-turbo", onSegment: onSegment, onError: onError, onEvent: onEvent, localServer: server, wordTimestamps: useTeamsNames, sampleAccurateTimestamps: improvedCapture)
        } else {
            mePipeline = try await ChunkedTranscriptionPipeline(speaker: .me, locale: locale, recordingURL: meURL, apiKey: apiKey, model: sessionSettings.openAIModel(for: .me), onSegment: onSegment, onError: onError, onEvent: onEvent, sampleAccurateTimestamps: improvedCapture)
            othersPipeline = try await ChunkedTranscriptionPipeline(speaker: .others, locale: locale, recordingURL: othersURL, apiKey: apiKey, model: sessionSettings.openAIModel(for: .others), onSegment: onSegment, onError: onError, onEvent: onEvent, wordTimestamps: useTeamsNames, sampleAccurateTimestamps: improvedCapture)
        }
        if let meURL, let othersURL { paths = [meURL.path, othersURL.path] }
        try ensureRecordingSession(sessionID)
        let me = mePipeline!
        let others = othersPipeline!
        let captureEvents = captureEvents
        let capture = AudioCapture(onAudio: { speaker, buffer, time in
            (speaker == .me ? me : others).append(buffer, at: time)
        }, onMeter: { [weak self] speaker, level in
            Task { @MainActor in
                guard let self, self.activeSessionID == sessionID, self.activeCaptureAttemptID == attemptID, !self.isStopping else { return }
                self.recordingActivity.receive(level, from: speaker)
            }
        }, onError: onError, onEvent: { [weak self] event in
            if captureEvents.append(event) {
                Task { @MainActor in guard let self, self.activeCaptureAttemptID == attemptID else { return }; self.drainCaptureEvents(sessionID: sessionID, meetingID: id) }
            }
        }, improved: improvedCapture)
        self.capture = capture
        if let index = meetings.firstIndex(where: { $0.id == id }) {
            meetings[index].audioFiles += paths
            meetings[index].audioDeletionReady = false
            meetings[index].audioTranscriptionComplete = false
            if sessionSettings.deleteAudioAfterProcessing {
                meetings[index].audioDeletionPending = (meetings[index].audioDeletionPending ?? []) + paths
            }
            persist(meetings[index])
        }
        try await capture.start(bundleID: sessionSettings.audioSourceBundleID, microphoneUID: sessionSettings.microphoneUID) { [weak self] origin in
            self?.recordingHostOrigin = origin
            self?.beginTeamsAttribution(origin: origin, sessionID: sessionID, meetingID: id, enabled: useTeamsNames)
        }
        try ensureRecordingSession(sessionID)
    }

    /// Session state is independent of the editor and can be exercised without audio hardware.
    func beginRecordingSession(for id: UUID) -> UUID? {
        guard !showOnboarding, !isSettingUp, !busy, meetings.contains(where: { $0.id == id }) else { return nil }
        flush()
        isStarting = true
        errorMessage = nil
        audioFailureHandled = false
        recordingHostOrigin = nil
        recordingAudioSelection = nil; activeCaptureAttemptID = nil
        actualMicrophoneName = ""; captureStatus = ""; captureEvents = CaptureEventInbox(); captureState = nil; captureSourceStates = [:]
        activeID = id
        let sessionID = UUID()
        activeSessionID = sessionID
        teamsSpeakerDiagnostics = TeamsSpeakerDiagnostics()
        baseDuration = activeMeeting?.duration ?? 0
        elapsed = baseDuration
        status = "Preparing recording · " + settings.transcriptionStatus
        return sessionID
    }
    func captureDidStart(at date: Date = Date()) {
        guard isStarting, let index = meetings.firstIndex(where: { $0.id == activeID }) else { return }
        recordingOrigin = date
        if meetings[index].startedAt == nil { meetings[index].startedAt = date }
        lastCheckpoint = date
        persist(meetings[index])
        isRecording = true
        isStarting = false
    }
    func recordingStartFailed(_ message: String) {
        meetingEndTask?.cancel(); meetingEndTask = nil
        shutdownTranscription()
        recordingActivity.resetLevels()
        flush()
        isStarting = false
        activeID = nil
        activeSessionID = nil
        recordingOrigin = nil
        presentationMode = false
        recordingAudioSelection = nil; activeCaptureAttemptID = nil
        status = "Ready to record"
        errorMessage = message
    }
    func tick(at date: Date = Date()) {
        guard let origin = recordingOrigin else { return }
        elapsed = baseDuration + max(0, date.timeIntervalSince(origin))
        if let id = activeID { markDirty(id) }
        recordingActivity.expireSignals(at: date)
        refreshTeamsSpeakerPresentation()
        // Checkpoint even in a silent meeting, so a crash loses at most one interval and doesn't erase elapsed duration.
        if let checkpointInterval, let id = activeID, date.timeIntervalSince(lastCheckpoint ?? origin) >= checkpointInterval {
            lastCheckpoint = date
            if let meeting = meetings.first(where: { $0.id == id }) { enqueueSnapshot(meeting) }
        }
    }
    private func drainTranscript(meetingID: UUID) {
        // A callback scheduled by an old recording must not consume the next
        // meeting's pending results from the shared inbox.
        guard activeID == meetingID else { return }
        for segment in transcriptInbox.drain() { receive(segment, meetingID: meetingID) }
    }
    func receive(_ newSegment: TranscriptSegment, meetingID: UUID) {
        guard activeID == meetingID, newSegment.sessionID == activeSessionID,
              let index = meetings.firstIndex(where: { $0.id == meetingID }) else { return }
        var segment = newSegment
        segment.offset += baseDuration
        Self.merge(segment, into: &meetings[index].transcript)
        // Speaker names are decided per utterance across neighbouring results, so label after inserting.
        if segment.speaker == .others { reconcileTeamsTranscript(arrived: segment.id) } else { refreshTeamsSpeakerPresentation() }
        markDirty(meetingID)
    }
    static func merge(_ segment: TranscriptSegment, into transcript: inout [TranscriptSegment]) {
        // Final results may still be revised by another final result, but an
        // out-of-order preview must never downgrade already finalized text.
        if !segment.isFinal, transcript.contains(where: {
            $0.id == segment.id && $0.speaker == segment.speaker && $0.isFinal
        }) { return }
        guard !segment.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            // A final empty recognition can reject an earlier provisional guess (e.g. music).
            if segment.isFinal { transcript.removeAll { $0.id == segment.id && !$0.isFinal } }
            return
        }
        // A recognizer can resize its volatile range before finalizing. Replace only overlapping
        // provisional text on the same source; preserve simultaneous speech on the other source.
        let end = segment.offset + max(0.01, segment.duration)
        transcript.removeAll { old in
            guard old.speaker == segment.speaker else { return false }
            if segment.connectionID != nil || old.connectionID != nil {
                guard old.connectionID == segment.connectionID, old.sessionID == segment.sessionID else { return false }
            }
            if old.id == segment.id { return true }
            if abs(old.offset - segment.offset) < 0.005 { return !old.isFinal }
            return !old.isFinal && old.offset < end && old.offset + max(0.01, old.duration) > segment.offset
        }
        transcript.append(segment)
    }
    private func handleCaptureFailure(_ text: String) async {
        guard !audioFailureHandled else { return }
        audioFailureHandled = true
        errorMessage = text
        if isRecording { await stopRecording() }
    }
    func stopRecording() async {
        guard isRecording, !isStopping else { return }
        meetingEndTask?.cancel(); meetingEndTask = nil
        let finishingSessionID = activeSessionID
        isStopping = true
        status = "Finishing transcript…"
        tick()
        timer?.invalidate(); timer = nil
        // Save before waiting on capture/recognition APIs, which may never reply.
        flush()
        let capture = self.capture
        let monitor = teamsWatcher
        let me = mePipeline
        let others = othersPipeline
        var finishingStage = "stopping the audio recording"
        let completed = await BoundedWork.run(for: .seconds(60)) { [self] in
            await capture?.stop()
            if let sessionID = activeSessionID, let meetingID = activeID { drainCaptureEvents(sessionID: sessionID, meetingID: meetingID) }
            guard !Task.isCancelled else { return }
            finishingStage = "stopping speaker detection"
            await monitor?.stop()
            guard !Task.isCancelled else { return }
            drainTeamsObservations()
            finishingStage = "finalizing the transcription"
            // Capture is drained before the analyzers receive end-of-input.
            async let meFinish: Void = me?.finish() ?? ()
            async let othersFinish: Void = others?.finish() ?? ()
            _ = await (meFinish, othersFinish)
        }
        guard !isQuitting, activeSessionID == finishingSessionID else { return }
        self.capture = nil; teamsWatcher = nil
        if !completed {
            capture?.stopInput()
            shutdownTranscription()
            // Do not await an unresponsive provider again on the recovery path.
            Task { await me?.cancel() }
            Task { await others?.cancel() }
            Task { await monitor?.stop() }
            let reason = Task.isCancelled ? "was cancelled" : "exceeded the 60-second time limit"
            errorMessage = "Finishing the transcript \(reason) while \(finishingStage). Notes and the received transcript were saved; the last section may be incomplete. Kept audio stays on this Mac."
        }
        // Drain synchronously after both pipelines finish so the last utterance is in the summary.
        if let id = activeID { drainTranscript(meetingID: id) }
        if let sessionID = activeSessionID, let id = activeID { drainTranscriptionEvents(sessionID: sessionID, meetingID: id) }
        drainTeamsObservations()
        reconcileTeamsTranscript(all: true)
        mePipeline = nil; othersPipeline = nil
        // Both channels have drained; release model RAM before running note/task models.
        shutdownTranscription()
        let stoppedID = activeID
        if let index = meetings.firstIndex(where: { $0.id == activeID }) {
            meetings[index].duration = elapsed
            meetings[index].audioTranscriptionComplete = completed && !audioFailureHandled && (meetings[index].transcriptionGaps ?? []).isEmpty
            if meetings[index].audioTranscriptionComplete != true { meetings[index].audioRecoveryNeeded = true }
            markDirty(meetings[index].id)
            // Persist the session's UI evidence so attribution variants can be replayed offline.
            if let session = attributionSession, session.id == activeSessionID {
                let entries = session.exportTimeline(until: elapsed)
                if !entries.isEmpty { meetings[index].speakerTimeline = (meetings[index].speakerTimeline ?? []) + entries }
            }
        }
        flush()
        activeID = nil; recordingOrigin = nil
        activeSessionID = nil; attributionSession = nil
        recordingAudioSelection = nil; activeCaptureAttemptID = nil
        isRecording = false; isStopping = false
        presentationMode = false
        recordingActivity.resetLevels()
        status = completed ? "Meeting saved" : "Meeting saved · Transcript incomplete"
        flush()
        refreshTeamsSpeakerStatus()
        if let stoppedID {
            if completed && !settings.acousticSpeakerRecognition {
                markAudioReadyForDeletion(stoppedID)
            } else { rediarize(meetingID: stoppedID) }
        }
    }

    func retryTaskExtraction(meetingID: UUID) async {
        guard settings.provider != .disabled else { return }
        guard !busy, let meeting = meetings.first(where: { $0.id == meetingID }),
              meeting.taskExtraction == .pending || meeting.taskExtraction == .failed else { return }
        await extractTasks(for: meeting)
        requestTaskReview(meeting.id)
    }

    // MARK: Acoustic speaker recognition (Phase 3)

    func isDiarizing(_ meetingID: UUID) -> Bool { diarizationQueue.contains(meetingID) }

    /// Whether "Re-detect speakers" can start for the meeting now.
    func canRediarize(_ meetingID: UUID) -> Bool {
        guard settings.acousticSpeakerRecognition, !isQuitting, !isDiarizing(meetingID), activeID != meetingID,
              let meeting = meetings.first(where: { $0.id == meetingID }) else { return false }
        return meeting.hasTimedRemoteWords && !existingOthersFiles(meeting).isEmpty
    }

    private func existingOthersFiles(_ meeting: Meeting) -> [URL] {
        AcousticDiarization.othersFiles(of: meeting).filter { FileManager.default.fileExists(atPath: $0.path) }
    }

    /// Offline acoustic speaker recognition of a stopped meeting, then fusion with the Teams labels (after each stop
    /// and via "Re-detect speakers"). Runs are serial; the returned task ends when this meeting's run has ended.
    /// Skips (status `.skipped`) when the setting is off, the transcript has no remote word times, no remote-channel
    /// audio was kept, or the models cannot be loaded. On failure the transcript stays unchanged.
    @discardableResult
    func rediarize(meetingID: UUID) -> Task<Void, Never>? {
        guard !isMovingLibrary else { return nil }
        // A run already queued for this meeting checks at its end whether the recordings changed meanwhile
        // (resumed recording) and then starts a fresh run itself.
        guard !isQuitting, !isDiarizing(meetingID), activeID != meetingID,
              let meeting = meetings.first(where: { $0.id == meetingID }) else { return nil }
        // Switched off: nothing runs and nothing is recorded; earlier results stay as they are.
        guard settings.acousticSpeakerRecognition else { return nil }
        let reason: String?
        if !meeting.hasTimedRemoteWords { reason = "The transcript of “Others” has no word timings." }
        else if existingOthersFiles(meeting).isEmpty { reason = "No audio recording of “Others” available (keeping audio files is off or the files are missing)." }
        else { reason = nil }
        if let reason {
            finishDiarization(meetingID, status: .skipped, message: reason)
            return nil
        }
        diarizationQueue.append(meetingID)
        let previous = diarizationTask
        let task = Task { [weak self] in
            await previous?.value
            await self?.runDiarization(meetingID: meetingID)
        }
        diarizationTask = task
        return task
    }

    /// Waits until every queued acoustic run has ended, including runs queued meanwhile (tests, eval harness).
    func waitForDiarization() async {
        var awaited: Task<Void, Never>?
        while let task = diarizationTask, task != awaited {
            awaited = task
            await task.value
        }
    }

    private func runDiarization(meetingID: UUID) async {
        var requeue = false
        defer {
            diarizationQueue.removeAll { $0 == meetingID }
            if diarizationQueue.isEmpty { diarizationProgress = "" }
            if requeue, rediarize(meetingID: meetingID) == nil,
               meetings.first(where: { $0.id == meetingID })?.diarization?.status == .running {
                finishDiarization(meetingID, status: .skipped,
                                  message: "The recordings changed during matching. Please choose “Re-detect speakers”.")
            }
        }
        guard !isQuitting, !Task.isCancelled, activeID != meetingID,
              let index = meetings.firstIndex(where: { $0.id == meetingID }) else { return }
        let meeting = meetings[index]
        let files = existingOthersFiles(meeting)
        let diarizer = acousticDiarizer
        meetings[index].diarization = DiarizationRun(engine: diarizer.engine, modelVersion: diarizer.modelVersion,
            threshold: diarizer.threshold, status: .running, startedAt: Date(), finishedAt: nil, message: nil,
            segments: meeting.diarization?.segments)
        persist(meetings[index])
        diarizationProgress = "Matching speakers…"
        let progress: @Sendable (Double) -> Void = { [weak self] value in
            Task { @MainActor in self?.setDiarizationProgress(value, meetingID: meetingID) }
        }
        // File reading, model loading and clustering stay off the main actor.
        let work = Task.detached(priority: .utility) { () throws -> [AcousticSegment] in
            let maps = try AcousticDiarization.timeMaps(for: meeting, files: files)
            return try await diarizer.diarize(audioFiles: files, timeMap: { maps[$0] }, progress: progress)
        }
        diarizationWork = work
        let outcome = await withTaskCancellationHandler { await work.result } onCancel: { work.cancel() }
        if diarizationWork == work { diarizationWork = nil }
        guard !isQuitting else { return }
        switch outcome {
        case .success(let segments):
            guard !Task.isCancelled else { return finishDiarization(meetingID, status: .failed, message: "Cancelled.") }
            guard activeID != meetingID, let index = meetings.firstIndex(where: { $0.id == meetingID }) else {
                return finishDiarization(meetingID, status: .skipped, message: "Recording was resumed; matching will follow after it stops.")
            }
            // Recording resumed and stopped during the run: its result misses the new audio. Discard it and run again.
            guard existingOthersFiles(meetings[index]) == files else {
                requeue = true
                return
            }
            var updated = meetings[index]
            updated.applyAcousticFusion(segments: segments)
            updated.diarization = DiarizationRun(engine: diarizer.engine, modelVersion: diarizer.modelVersion,
                threshold: diarizer.threshold, status: .completed, startedAt: meetings[index].diarization?.startedAt,
                finishedAt: Date(), message: nil, segments: segments)
            meetings[index] = updated
            persist(updated)
            markAudioReadyForDeletion(meetingID)
        case .failure(let error):
            if case .modelsUnavailable(let reason)? = error as? AcousticDiarizationError {
                finishDiarization(meetingID, status: .skipped, message: "Speaker models unavailable: \(reason)")
            } else if case .audioTooLong? = error as? AcousticDiarizationError {
                // Over the 4 h cap: not an error of the pipeline, so the run is skipped with the reason shown.
                finishDiarization(meetingID, status: .skipped, message: error.localizedDescription)
            } else if error is CancellationError {
                finishDiarization(meetingID, status: .failed, message: "Cancelled.")
            } else {
                finishDiarization(meetingID, status: .failed, message: error.localizedDescription)
            }
        }
    }

    /// Records a run that ended without new labels; earlier segments and labels stay as they are.
    private func finishDiarization(_ meetingID: UUID, status: DiarizationStatus, message: String) {
        guard let index = meetings.firstIndex(where: { $0.id == meetingID }) else { return }
        let previous = meetings[index].diarization
        meetings[index].diarization = DiarizationRun(engine: acousticDiarizer.engine, modelVersion: acousticDiarizer.modelVersion,
            threshold: acousticDiarizer.threshold, status: status,
            startedAt: previous?.status == .running ? previous?.startedAt : nil, finishedAt: Date(),
            message: message, segments: previous?.segments)
        persist(meetings[index])
    }

    private func setDiarizationProgress(_ value: Double, meetingID: UUID) {
        guard !isQuitting, diarizationQueue.first == meetingID else { return }
        let text = "Matching speakers… \(Int((min(1, max(0, value)) * 100).rounded()))%"
        if diarizationProgress != text { diarizationProgress = text }
    }

    private func extractTasks(for meeting: Meeting) async {
        flush()
        extractingTasksID = meeting.id
        taskProgress = "Looking for agreed tasks…"
        defer { extractingTasksID = nil; taskProgress = "" }
        do {
            let drafts = try await extractMeetingTasks(meeting, settings, enhancementAPIKey) { [weak self] message in
                await self?.setTaskProgress(message)
            }
            try Task.checkCancellation()
            guard !isQuitting, let index = meetings.firstIndex(where: { $0.id == meeting.id }) else { return }
            meetings[index].mergeTasks(drafts.filter { PersonalTasks.isMine($0.owner, name: settings.taskOwnerName) })
            meetings[index].taskExtraction = .complete
            persist(meetings[index])
        } catch {
            guard !isQuitting, let index = meetings.firstIndex(where: { $0.id == meeting.id }) else { return }
            meetings[index].taskExtraction = .failed
            persist(meetings[index])
            errorMessage = "Tasks for “\(meeting.title)” could not be detected: \(Self.sentence(error.localizedDescription)) Repeat detection under “My to-dos”. Existing tasks are kept."
            setErrorRecovery(for: error, retry: .retryTaskExtraction(meeting.id))
        }
    }

    func pendingTasks(for meeting: Meeting) -> [MeetingTask] {
        (meeting.tasks ?? []).filter { $0.review == nil && PersonalTasks.isMine($0.owner, name: settings.taskOwnerName) }
    }

    func requestTaskReview(_ id: UUID) {
        guard !isQuitting else { return }
        guard let meeting = meetings.first(where: { $0.id == id }), !pendingTasks(for: meeting).isEmpty else { return }
        taskReviewMeeting = meeting
    }

    func reviewTask(meetingID: UUID, taskID: UUID, accept: Bool) {
        guard !busy, let index = meetings.firstIndex(where: { $0.id == meetingID }),
              let taskIndex = meetings[index].tasks?.firstIndex(where: { $0.id == taskID }),
              meetings[index].tasks?[taskIndex].review == nil else { return }
        var updated = meetings[index]
        updated.tasks?[taskIndex].review = accept ? .accepted : .dismissed
        do {
            try saveNow(updated)
            meetings[index] = updated
            dirtyMeetingIDs.remove(meetingID)
            if pendingTasks(for: updated).isEmpty { taskReviewMeeting = nil }
        } catch { errorMessage = "The task decision could not be saved: \(error.localizedDescription)" }
    }

    private func markAudioReadyForDeletion(_ id: UUID) {
        guard let index = meetings.firstIndex(where: { $0.id == id }),
              meetings[index].audioTranscriptionComplete == true, meetings[index].audioRecoveryNeeded != true,
              !(meetings[index].audioDeletionPending ?? []).isEmpty else { return }
        meetings[index].audioDeletionReady = true
        cleanupProcessedAudio(id)
    }

    /// Save processing results before unlinking any recording. Persist the deletion journal so
    /// a crash or failed final write can safely retry already-missing files at the next launch.
    func cleanupProcessedAudio(_ id: UUID) {
        guard !isMovingLibrary else { return }
        guard activeID != id, let index = meetings.firstIndex(where: { $0.id == id }),
              meetings[index].audioDeletionReady == true else { return }
        do {
            try saveNow(meetings[index])
            for path in meetings[index].audioDeletionPending ?? [] {
                try repository.removeAudioFile(path, meetingID: id)
                meetings[index].audioFiles.removeAll { $0 == path }
                meetings[index].audioDeletionPending?.removeAll { $0 == path }
            }
            meetings[index].audioDeletionError = nil
            try saveNow(meetings[index])
            dirtyMeetingIDs.remove(id)
        } catch {
            meetings[index].audioDeletionError = error.localizedDescription
            markDirty(id)
            persist(meetings[index])
            errorMessage = "Audio could not be deleted automatically: \(error.localizedDescription). Try again under Storage."
        }
    }

    private func setTaskProgress(_ message: String) { if !isQuitting { taskProgress = message } }

    func setTaskCompleted(meetingID: UUID, taskID: UUID, completed: Bool) {
        guard !isMovingLibrary else { return }
        guard !isQuitting, let index = meetings.firstIndex(where: { $0.id == meetingID }),
              let taskIndex = meetings[index].tasks?.firstIndex(where: { $0.id == taskID }) else { return }
        var updated = meetings[index]
        updated.tasks?[taskIndex].completedAt = completed ? Date() : nil
        do {
            try saveNow(updated)
            meetings[index] = updated
            dirtyMeetingIDs.remove(meetingID)
        } catch {
            errorMessage = "The to-do could not be saved: \(error.localizedDescription). Its status was not changed."
        }
    }

    func taskGroups(completed: Bool) -> [MeetingTaskGroup] {
        let rows = visibleMeetings.flatMap { meeting in
            (meeting.tasks ?? []).filter { $0.review == .accepted && ($0.completedAt != nil) == completed }
                .map { MeetingTaskRow(meeting: meeting, task: $0) }
        }
        return (folders.map { Optional($0.id) } + [nil]).compactMap { id in
            var matching = rows.filter { folderCatalog.folderID(for: $0.meeting.id) == id }
            guard !matching.isEmpty else { return nil }
            if completed { matching.sort { ($0.task.completedAt ?? .distantPast) > ($1.task.completedAt ?? .distantPast) } }
            let folder = folders.first { $0.id == id }
            return MeetingTaskGroup(id: id, name: folder?.name ?? "No folder", icon: folder?.icon ?? "tray", rows: matching)
        }
    }

    func enhanceNotes(meetingID: UUID? = nil) async {
        guard settings.provider != .disabled else {
            status = "Enhancement turned off · Transcript and notes are kept"
            return
        }
        guard !busy, !Task.isCancelled, let meeting = meetings.first(where: { $0.id == (meetingID ?? selectedID) }),
              !meeting.transcriptText.isEmpty else { return }
        flush()
        enhancingID = meeting.id
        isEnhancing = true
        defer {
            isEnhancing = false
            enhancingID = nil
            enhancementProgress = ""
            requestTaskReview(meeting.id)
        }
        await enhanceInOnePass(meetingID: meeting.id)
    }

    private func enhanceInOnePass(meetingID: UUID) async {
        guard let index = meetings.firstIndex(where: { $0.id == meetingID }) else { return }
        meetings[index].taskExtraction = .pending
        persist(meetings[index])
        let meeting = meetings[index]
        enhancementProgress = "Creating notes, tasks and title…"
        let configuration = settings
        do {
            let result = try await enhanceMeetingAll(meeting, configuration, enhancementAPIKey) { [self] message in
                await setEnhancementProgress(message)
            }
            guard !isQuitting, !Task.isCancelled, let index = meetings.firstIndex(where: { $0.id == meetingID }) else { return }
            meetings[index].enhancedNotes = result.notes
            meetings[index].enhancementEngine = configuration.provider.rawValue
            meetings[index].mergeTasks(result.tasks.filter { PersonalTasks.isMine($0.owner, name: settings.taskOwnerName) })
            meetings[index].taskExtraction = .complete
            if let title = result.title, meetings[index].needsSuggestedTitle { meetings[index].title = title }
            persist(meetings[index])
            status = "Notes enhanced"

        } catch {
            guard !isQuitting, let index = meetings.firstIndex(where: { $0.id == meetingID }) else { return }
            meetings[index].taskExtraction = .failed
            persist(meetings[index])
            errorMessage = error.localizedDescription; status = "Meeting saved · Enhancement failed"
            setErrorRecovery(for: error, retry: .retryEnhancement(meetingID))
        }
    }

    private static func sentence(_ text: String) -> String { text.hasSuffix(".") ? text : text + "." }

    /// Settings for configuration failures; otherwise the same retry the meeting's own button offers.
    private func setErrorRecovery(for error: Error, retry: ErrorRecovery.Action) {
        guard let message = errorMessage else { return }
        let configuration = (error as? EnhancementServiceError)?.kind == .configuration
        errorRecovery = ErrorRecovery(message: message, action: configuration ? .openSettings : retry)
    }

    private func setEnhancementProgress(_ message: String) { if !isQuitting, enhancementProgress != message { enhancementProgress = message } }
    func exportSelected() {
        guard let meeting = selectedMeeting else { return }
        flush()
        let panel = NSSavePanel()
        panel.nameFieldStringValue = meeting.title.replacingOccurrences(of: "/", with: "-") + ".md"
        panel.allowedContentTypes = [.init(filenameExtension: "md")!]
        if panel.runModal() == .OK, let url = panel.url {
            do { try meeting.markdown.write(to: url, atomically: true, encoding: .utf8) }
            catch { errorMessage = error.localizedDescription }
        }
    }
    func deleteSelected() {
        guard let id = selectedID else { return }
        deleteMeeting(id)
    }
    func deleteMeeting(_ id: UUID) {
        guard !busy, let index = meetings.firstIndex(where: { $0.id == id }) else { return }
        flush()
        let visibleIndex = visibleMeetings.firstIndex(where: { $0.id == id }) ?? 0
        let meeting = meetings[index]
        do {
            try writer.delete(meeting)
            meetings.remove(at: index)
            personSummaryErrors[id] = nil; transcriptMirrorErrors[id] = nil
            dirtyMeetingIDs.remove(id); revisions.removeValue(forKey: id)
            if selectedID == id {
                requestedPersonSource = nil
                let visible = visibleMeetings
                selectedID = visible.isEmpty ? nil : visible[min(visibleIndex, visible.count - 1)].id
                elapsed = selectedMeeting?.duration ?? 0
                if selectedID == nil { workspacePresentation = .overview }
            }
            if folderLibraryAvailable, folderCatalog.assignments[id.uuidString] != nil {
                var updated = folderCatalog
                updated.assignments.removeValue(forKey: id.uuidString)
                _ = commitFolderCatalog(updated)
            }
        } catch { errorMessage = "The meeting could not be deleted: \(error.localizedDescription)" }
    }
    /// A bounded quit path saves received text, then abandons outstanding work.
    /// Returning false keeps the app open when the save itself failed.
    func prepareToQuit() async -> Bool {
        guard !isMovingLibrary else {
            errorMessage = "Please wait until the meeting library has been moved."
            return false
        }
        if isRecording, !isStopping { tick() }
        if let sessionID = activeSessionID, let id = activeID { drainTranscriptionEvents(sessionID: sessionID, meetingID: id) }
        if let sessionID = activeSessionID, let id = activeID { drainCaptureEvents(sessionID: sessionID, meetingID: id) }
        drainTeamsObservations()
        if let id = activeID { drainTranscript(meetingID: id) }
        guard flush() else { return false }
        isQuitting = true
        // The acoustic run is local CPU work on saved audio: abandon it, never wait for it.
        diarizationTask?.cancel(); diarizationTask = nil
        diarizationWork?.cancel(); diarizationWork = nil
        timer?.invalidate(); timer = nil
        meetingEndTask?.cancel(); meetingEndTask = nil
        activeSessionID = nil
        capture?.stopInput()
        shutdownTranscription()
        let capture = self.capture
        let monitor = teamsWatcher
        let me = mePipeline
        let others = othersPipeline
        _ = await BoundedWork.run(for: .seconds(3)) {
            async let stopCapture: Void = capture?.stop() ?? ()
            async let stopMonitor: Void = monitor?.stop() ?? ()
            async let cancelMe: Void = me?.cancel() ?? ()
            async let cancelOthers: Void = others?.cancel() ?? ()
            _ = await (stopCapture, stopMonitor, cancelMe, cancelOthers)
        }
        return true
    }
    func shutdownTranscription() { localWhisperServer?.shutdown(); localWhisperServer = nil }
    func chooseLibraryLocation() {
        guard !busy, diarizationQueue.isEmpty else { return }
        let panel = NSOpenPanel()
        panel.canChooseFiles = false; panel.canChooseDirectories = true; panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true
        panel.prompt = "Move library here"
        panel.message = "A Wallfly folder will be created in the chosen folder. Your existing library will be moved there."
        guard panel.runModal() == .OK, let parent = panel.url else { return }
        Task { await moveLibrary(to: parent.appendingPathComponent(AppDataFolder.name, isDirectory: true)) }
    }

    func moveLibrary(to destination: URL, remember: @escaping (URL) throws -> Void = LibraryLocation.remember) async {
        guard !busy, diarizationQueue.isEmpty, flush() else { return }
        isMovingLibrary = true
        defer { isMovingLibrary = false }
        do {
            try repository.saveSettings(settings)
            let source = repository
            let migration = try await Task.detached(priority: .utility) {
                try LibraryMigration.prepare(source: source, destinationURL: destination)
            }.value
            try remember(migration.destination.root)
            repository = MeetingRepository(root: migration.destination.root, requiresExistingRoot: true)
            writer = MeetingWriter(repository: repository, mirror: transcriptMirror)
            meetings = migration.meetings
            status = "Meeting library moved"
            do { try await Task.detached(priority: .utility) { try migration.removeOriginals() }.value }
            catch {
                errorMessage = "The new library is active. Some old copies could not be deleted: \(source.root.path). \(error.localizedDescription)"
            }
        } catch { errorMessage = "The library was not moved: \(error.localizedDescription)" }
    }

    func revealStorage() { try? repository.prepare(); NSWorkspace.shared.open(repository.root) }
    func importDemo() {
        guard !busy else { return }
        flush()
        var meeting = Meeting(title: "Example · Product check-in", duration: 184,
            notes: "Launch the smaller pilot first\nAsk about the onboarding drop-off\nFollow up on accessibility testing")
        meeting.transcript = [
            TranscriptSegment(speaker: .me, offset: 12, text: "Let's start with a smaller pilot so we can learn from the first ten teams.", isFinal: true),
            TranscriptSegment(speaker: .others, offset: 38, text: "Agreed. The biggest drop-off is on the invite screen. We should simplify that before we expand.", isFinal: true),
            TranscriptSegment(speaker: .me, offset: 74, text: "I'll review the invite flow on Friday. Can we include keyboard and screen reader testing?", isFinal: true),
            TranscriptSegment(speaker: .others, offset: 106, text: "Yes. We still need to decide who will run those tests.", isFinal: true)
        ]
        meetings.insert(meeting, at: 0); selectedID = meeting.id; persist(meeting)
        workspacePresentation = .editor
        notePresentationID = UUID()
        if let folder = selectedFolder { moveMeeting(meeting.id, to: folder.id) }
    }
}


/// The producer appends synchronously; the main actor drains before ending a meeting.
private final class TranscriptInbox: @unchecked Sendable {
    private let lock = NSLock()
    private var pending: [TranscriptSegment] = []
    func append(_ segment: TranscriptSegment) {
        lock.lock(); defer { lock.unlock() }
        pending.append(segment)
    }
    func drain() -> [TranscriptSegment] {
        lock.lock(); defer { lock.unlock() }
        let result = pending
        pending.removeAll(keepingCapacity: true)
        return result
    }
}

private final class TeamsObservationInbox: @unchecked Sendable {
    private let lock = NSLock()
    private var pending: [(UUID, TeamsSpeakerObservation)] = []
    func append(sessionID: UUID, observation: TeamsSpeakerObservation) {
        lock.lock(); defer { lock.unlock() }
        pending.append((sessionID, observation))
    }
    func drain() -> [(UUID, TeamsSpeakerObservation)] {
        lock.lock(); defer { lock.unlock() }
        let result = pending; pending.removeAll(keepingCapacity: true); return result
    }
}
