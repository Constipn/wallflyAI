import SwiftUI
import AppKit

enum MeetingSettingsCategory: String, CaseIterable {
    case recording = "Recording", intelligence = "AI & language", storage = "Storage", mcp = "MCP"
}

struct MeetingSettingsView: View {
    @ObservedObject var store: MeetingStore
    @Environment(\.dismiss) private var dismiss
    @ViewState private var showSetup = false
    @ViewState private var category: MeetingSettingsCategory

    init(store: MeetingStore, category: MeetingSettingsCategory = .recording) {
        self.store = store
        _category = ViewState(initialValue: category)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("Settings").font(.system(size: 25, design: .serif))
                Spacer()
                Button { if store.saveSettings() { dismiss() } } label: {
                    Image(systemName: "xmark.circle.fill").font(.system(size: 21)).foregroundStyle(Palette.muted)
                }.buttonStyle(.plain).help("Close settings").accessibilityLabel("Close settings")
            }.padding(24)
            Picker("Section", selection: $category) {
                ForEach(MeetingSettingsCategory.allCases, id: \.self) { Text($0.rawValue).tag($0) }
            }.pickerStyle(.segmented).labelsHidden().padding(.horizontal, 24).padding(.bottom, 16)
            Divider()
            Form {
                switch category {
                case .recording:
                    AudioSourcesSection(store: store)
                    SpeakerSection(store: store)
                    TeamsSection(store: store)
                case .intelligence:
                    TranscriptionSection(store: store)
                    SummarySection(store: store)
                    TaskOwnerSection(store: store)
                case .mcp:
                    MCPSettingsSection()
                case .storage:
                    StorageLocationSection(store: store)
                    AudioRetentionSection(store: store)
                    TranscriptMirrorSection(store: store)
                }
            }.formStyle(.grouped).scrollContentBackground(.hidden).disabled(store.busy)
            if store.isRecording && !store.isStopping { liveControls }
            Divider()
            HStack {
                if store.isMovingLibrary {
                    ProgressView().controlSize(.small)
                    Text("Moving library…").font(Design.metadata)
                } else if store.busy {
                    Text(store.isRecording ? "More settings can be edited after the recording." : "Processing in progress. Settings can be edited afterwards.")
                        .font(Design.metadata).foregroundStyle(Palette.muted)
                }
                Button("Open setup…") { showSetup = true }.disabled(store.busy)
                Spacer()
                Button("Done") { if store.saveSettings() { dismiss() } }
                    .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
            }.padding(20)
        }
        .frame(width: 650)
        .frame(minHeight: 480, idealHeight: 640, maxHeight: min(780, (NSScreen.main?.visibleFrame.height ?? 850) - 100))
        .background(Palette.paper).foregroundStyle(Palette.ink).tint(Palette.red600)
        .onAppear { store.refreshMicrophones(); store.refreshTeamsSpeakerStatus() }
        .onChange(of: store.settings.microphoneUID) { _, _ in store.applyMicrophoneSelection() }
        .onChange(of: store.settings.audioSourceBundleID) { _, _ in store.refreshTeamsSpeakerStatus() }
        .onChange(of: store.settings.automaticMeetingAudioSource) { _, _ in store.refreshTeamsSpeakerStatus() }
        .onChange(of: store.settings.transcriptionProvider) { _, _ in store.refreshTeamsSpeakerStatus() }
        .onChange(of: store.settings.transcriptMirrorEnabled) { _, _ in store.saveSettings() }
        .onDisappear { store.saveSettings() }
        .sheet(isPresented: $showSetup) { MeetingOnboardingView(store: store) }
    }

    private var liveControls: some View {
        VStack(spacing: 12) {
            MicrophonePicker(store: store, title: "Switch microphone")
            if store.settings.transcriptionProvider == .deepgram {
                HStack {
                    SecureField("Update Deepgram key", text: $store.deepgramAPIKey).textFieldStyle(.roundedBorder)
                    Button("Reconnect") { store.saveSettings(); store.restartDeepgramTranscription() }
                        .disabled(store.deepgramAPIKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }
        }.padding(20)
    }
}

// MARK: - Shared helpers

func settingsDetail(_ text: String) -> some View {
    Text(text).font(Design.metadata).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
}

func credentialStatus(_ value: String) -> some View {
    let missing = value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    return Label(missing ? "No key entered" : "Key entered · not yet verified",
                 systemImage: missing ? "exclamationmark.triangle" : "checkmark.circle")
        .font(Design.metadata).foregroundStyle(missing ? Palette.red600 : Palette.muted)
}

/// A missing permission or setup step, with the one button that fixes it.
struct SettingsWarning: View {
    let text: String
    var button: String?
    var action: () -> Void = { }
    var body: some View {
        HStack {
            Label(text, systemImage: "exclamationmark.triangle").font(Design.metadata).foregroundStyle(Palette.red600)
            Spacer()
            if let button { Button(button, action: action) }
        }
    }
}

private func refreshButton(_ title: String, action: @escaping () -> Void) -> some View {
    Button(action: action) { Image(systemName: "arrow.clockwise") }
        .buttonStyle(.plain).help(title).accessibilityLabel(title)
}

struct MicrophonePicker: View {
    @ObservedObject var store: MeetingStore
    var title = "Microphone"
    var body: some View {
        Picker(title, selection: $store.settings.microphoneUID) {
            Text("System default").tag("")
            ForEach(store.microphones) { Text($0.name).tag($0.id) }
            if !store.settings.microphoneUID.isEmpty && !store.microphones.contains(where: { $0.id == store.settings.microphoneUID }) {
                Text("Saved microphone · unavailable").tag(store.settings.microphoneUID)
            }
        }
    }
}

// MARK: - Aufnahme

struct AudioSourcesSection: View {
    @ObservedObject var store: MeetingStore
    var body: some View {
        Section("Audio") {
            HStack {
                MicrophonePicker(store: store)
                refreshButton("Refresh microphones") { store.refreshMicrophones() }
            }
            HStack {
                Picker("Meeting audio", selection: Binding(get: { store.meetingAudioChoice }, set: { store.meetingAudioChoice = $0 })) {
                    Text("Automatic (Teams or whole Mac)").tag(MeetingAudioChoice.automatic)
                    Text("Whole Mac").tag(MeetingAudioChoice.manual(""))
                    ForEach(store.audioSources) { source in Text(source.name).tag(MeetingAudioChoice.manual(source.id)) }
                    if !store.settings.audioSourceBundleID.isEmpty && !store.audioSources.contains(where: { $0.id == store.settings.audioSourceBundleID }) {
                        Text("Saved app · unavailable").tag(MeetingAudioChoice.manual(store.settings.audioSourceBundleID))
                    }
                }
                refreshButton("Refresh apps") { Task { await store.refreshAudioSources() } }
            }
            settingsDetail(store.recordingAudioDescription.isEmpty
                ? "Automatic records a running Teams meeting, otherwise the audio of the whole Mac."
                : store.recordingAudioDescription)
        }
    }
}

struct SpeakerSection: View {
    @ObservedObject var store: MeetingStore
    var body: some View {
        Section("Speakers") {
            TeamsSpeakerSetup(store: store)
            Toggle("Match voices after the meeting", isOn: $store.settings.acousticSpeakerRecognition)
            settingsDetail("Improves speaker attribution in the transcript. Runs locally.")
        }
    }
}

struct TeamsSection: View {
    @ObservedObject var store: MeetingStore
    var body: some View {
        Section("Microsoft Teams") {
            MeetingDetectionSetup()
            Toggle("Stop recording when the meeting ends", isOn: $store.settings.autoStopWhenMeetingEnds)
            DisclosureGroup("Troubleshooting") {
                MeetingDetectionDiagnostics(speakerStatus: store.teamsSpeakerStatusText) { store.refreshTeamsSpeakerStatus() }
            }
        }
    }
}

// MARK: - KI & Sprache

struct TranscriptionSection: View {
    @ObservedObject var store: MeetingStore
    var body: some View {
        Section("Transcription") {
            Picker("Service", selection: $store.settings.transcriptionProvider) {
                ForEach(TranscriptionProvider.allCases, id: \.self) { Text($0.displayName).tag($0) }
            }
            let weakSpeakers = store.settings.transcriptionProvider == .openai
            Label("Best speaker recognition with Deepgram or OpenWhispr (local Whisper). OpenAI tells speakers apart less reliably.",
                  systemImage: weakSpeakers ? "exclamationmark.triangle" : "person.2")
                .font(Design.metadata).foregroundStyle(weakSpeakers ? Palette.red600 : Palette.muted)
                .fixedSize(horizontal: false, vertical: true)
            switch store.settings.transcriptionProvider {
            case .deepgram:
                settingsDetail("Audio is sent to Deepgram for transcription.")
                SecureField("Deepgram API key", text: $store.deepgramAPIKey).textFieldStyle(.roundedBorder)
                credentialStatus(store.deepgramAPIKey)
            case .openai:
                settingsDetail("Audio is sent to OpenAI for transcription.")
                SecureField("OpenAI API key", text: $store.apiKey).textFieldStyle(.roundedBorder)
                credentialStatus(store.apiKey)
            case .openWhispr:
                settingsDetail("Runs on this Mac.")
                LabeledContent("OpenWhispr app") {
                    HStack {
                        TextField("OpenWhispr app", text: $store.settings.openWhisprAppPath).textFieldStyle(.roundedBorder).labelsHidden()
                            .frame(minWidth: 120, idealWidth: 260, maxWidth: .infinity)
                        Button("Choose…") { chooseLocalFile(application: true) }.fixedSize()
                    }
                }
                LabeledContent("Turbo model") {
                    HStack {
                        TextField("Turbo model", text: $store.settings.whisperModelPath).textFieldStyle(.roundedBorder).labelsHidden()
                            .frame(minWidth: 120, idealWidth: 260, maxWidth: .infinity)
                        Button("Choose…") { chooseLocalFile(application: false) }.fixedSize()
                    }
                }
                let installed = LocalWhisperInstallation(appPath: store.settings.openWhisprAppPath, modelPath: store.settings.whisperModelPath).isAvailable
                Label(installed ? "Turbo model found" : "App or model not found", systemImage: installed ? "checkmark.circle" : "exclamationmark.triangle")
                    .font(Design.metadata).foregroundStyle(installed ? Palette.muted : Palette.red600)
            }
            Picker("Spoken language", selection: $store.settings.locale) {
                Text("Automatic").tag("auto"); Text("German").tag("de-DE"); Text("English").tag("en-US")
            }
        }
    }

    private func chooseLocalFile(application: Bool) {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = false; panel.allowsMultipleSelection = false
        panel.allowedContentTypes = [.init(filenameExtension: application ? "app" : "bin")!]
        panel.prompt = "Choose"
        if panel.runModal() == .OK, let url = panel.url {
            if application { store.settings.openWhisprAppPath = url.path }
            else { store.settings.whisperModelPath = url.path }
        }
    }
}

struct SummarySection: View {
    @ObservedObject var store: MeetingStore
    private var provider: EnhancementProvider { store.settings.provider }
    var body: some View {
        Section("Enhancement") {
            Picker("Service", selection: Binding(get: { provider }, set: { store.selectEnhancementProvider($0) })) {
                ForEach(EnhancementProvider.allCases, id: \.self) { Text($0.displayName).tag($0) }
            }
            if provider == .disabled {
                if let issue = store.enhancementConfigurationIssue { settingsDetail(issue) }
            } else {
                settingsDetail(privacy)
                let models = SummaryModels.choices(for: provider)
                if models.isEmpty {
                    TextField("Model ID", text: $store.settings.model).textFieldStyle(.roundedBorder)
                    TextField("API address", text: $store.settings.endpoint, prompt: Text("https://…/v1/chat/completions")).textFieldStyle(.roundedBorder)
                } else {
                    Picker("Model", selection: $store.settings.model) {
                        ForEach(models) { Text($0.name).tag($0.id) }
                        if !models.contains(where: { $0.id == store.settings.model }) {
                            Text(store.settings.model.isEmpty ? "Please choose" : "Saved: " + store.settings.model).tag(store.settings.model)
                        }
                    }
                }
                switch provider {
                case .anthropic:
                    SecureField("API key", text: $store.anthropicAPIKey).textFieldStyle(.roundedBorder)
                    credentialStatus(store.anthropicAPIKey)
                    Link("Create API key", destination: URL(string: "https://platform.claude.com/settings/keys")!)
                case .openai:
                    SecureField("API key", text: $store.apiKey).textFieldStyle(.roundedBorder)
                    credentialStatus(store.apiKey)
                    Link("Create API key", destination: URL(string: "https://platform.openai.com/api-keys")!)
                default:
                    SecureField("API key", text: $store.apiKey, prompt: Text("optional")).textFieldStyle(.roundedBorder)
                }
                if let issue = store.enhancementConfigurationIssue, !keyMissing {
                    Label(issue, systemImage: "exclamationmark.triangle").font(Design.metadata).foregroundStyle(Palette.red600)
                }
                if !models.isEmpty {
                    DisclosureGroup("Advanced") {
                        TextField("Custom model ID", text: $store.settings.model).textFieldStyle(.roundedBorder)
                        if provider == .anthropic {
                            TextField("Workspace-ID", text: $store.settings.anthropicWorkspaceID, prompt: Text("optional")).textFieldStyle(.roundedBorder)
                        }
                    }
                }
            }
        }
    }

    /// The key status above already says so; don't repeat it as a configuration issue.
    private var keyMissing: Bool {
        let key = provider == .anthropic ? store.anthropicAPIKey : store.apiKey
        return [.openai, .anthropic].contains(provider) && key.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
    private var privacy: String {
        switch provider {
        case .anthropic: "Notes and transcript are sent to Anthropic. A Claude subscription is not enough; you need an API key."
        case .openai: "Notes and transcript are sent to OpenAI. A ChatGPT subscription is not enough; you need an API key."
        default: "Notes and transcript are sent to the configured API address."
        }
    }
}

struct TaskOwnerSection: View {
    @ObservedObject var store: MeetingStore
    var body: some View {
        Section("My to-dos") {
            TextField("Your name", text: $store.settings.taskOwnerName, prompt: Text("How others refer to you in the meeting · optional"))
                .textFieldStyle(.roundedBorder)
            settingsDetail("The AI suggests tasks assigned to this name. You confirm each one yourself.")
        }
    }
}

// MARK: - Speicherung

struct StorageLocationSection: View {
    @ObservedObject var store: MeetingStore
    var body: some View {
        Section("Storage location") {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Folder")
                    Text(store.repository.root.path).font(Design.metadata).foregroundStyle(Palette.muted)
                        .lineLimit(1).truncationMode(.middle).help(store.repository.root.path)
                }
                Spacer()
                Button("Change…") { store.chooseLibraryLocation() }.disabled(!store.diarizationQueue.isEmpty)
                    .help("An external drive must be connected when the app starts. Cloud folders sync your meeting data.")
                    .fixedSize()
                Button("Show in Finder") { store.revealStorage() }.fixedSize()
            }
            settingsDetail("All meetings, notes and audio files are stored here. Changing the location moves everything.")
        }
    }
}

struct AudioRetentionSection: View {
    @ObservedObject var store: MeetingStore
    var body: some View {
        Section("Audio after the meeting") {
            option(.keep, "Keep", "You can have speakers re-detected later. This improves the transcript.")
            option(.deleteAfterProcessing, "Delete automatically", store.settings.acousticSpeakerRecognition
                ? "Deleted once speakers have been matched. Transcript, notes and speaker names are kept."
                : "Deleted as soon as the transcript is ready. Transcript, notes and speaker names are kept.")
            settingsDetail("Applies to new recordings.")
            ForEach(store.meetings.filter { $0.audioDeletionError != nil }) { meeting in
                HStack {
                    Text("Audio deletion pending: \(meeting.title)").font(Design.metadata).lineLimit(1).truncationMode(.middle)
                    Spacer()
                    Button("Try again") { store.cleanupProcessedAudio(meeting.id) }
                }
            }
        }
    }

    private func option(_ value: AudioRetention, _ title: String, _ detail: String) -> some View {
        let selected = store.settings.audioRetention == value
        return Button { store.settings.audioRetention = value } label: {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Image(systemName: selected ? "largecircle.fill.circle" : "circle")
                    .foregroundStyle(selected ? Palette.red600 : Palette.muted)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title)
                    settingsDetail(detail)
                }
                Spacer(minLength: 0)
            }.contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

struct TranscriptMirrorSection: View {
    @ObservedObject var store: MeetingStore
    var body: some View {
        Section("Transcript copy") {
            Toggle("Also save the transcript to a folder", isOn: $store.settings.transcriptMirrorEnabled)
                .disabled(store.settings.transcriptMirrorFolder == nil)
            HStack {
                if let folder = store.settings.transcriptMirrorFolder {
                    Text(folder.path).font(Design.metadata).foregroundStyle(Palette.muted)
                        .lineLimit(1).truncationMode(.middle).help(folder.path)
                }
                Spacer()
                Button(store.settings.transcriptMirrorFolder == nil ? "Choose folder…" : "Change…") { store.chooseTranscriptMirrorFolder() }.fixedSize()
                if store.settings.transcriptMirrorFolder != nil {
                    Button("Show in Finder") { store.revealTranscriptMirrorFolder() }.fixedSize()
                }
            }
            settingsDetail("For new meetings, as JSON, updated continuously.")
        }
    }
}
