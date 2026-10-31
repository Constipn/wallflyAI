import Foundation

enum Speaker: String, Codable, CaseIterable {
    case me = "Me"
    case others = "Others"
}

struct TranscriptSegment: Identifiable, Codable, Equatable {
    var id: UUID = UUID()
    var speaker: Speaker
    var offset: TimeInterval
    var text: String
    var duration: TimeInterval = 0
    var isFinal: Bool = false
    var sessionID: UUID?
    var words: [TranscriptWord]?
    var participantID: UUID?
    var attribution: SpeakerAttribution?
    var connectionID: UUID?
}

/// A presentation of consecutive recognition chunks, preserving their source data.
struct TranscriptTurn: Identifiable, Equatable {
    var id: TranscriptTurnID
    var speaker: Speaker
    var offset: TimeInterval
    var text: String
    var isFinal: Bool
    var participantID: UUID?
    var references: [TranscriptReference]
}

struct Meeting: Identifiable, Codable, Equatable {
    var id: UUID = UUID()
    var title: String = "Untitled meeting"
    var createdAt: Date = Date()
    var startedAt: Date?
    var duration: TimeInterval = 0
    var notes: String = ""
    var enhancedNotes: String = ""
    var enhancementEngine: String = ""
    var transcript: [TranscriptSegment] = []
    var audioFiles: [String] = []
    /// Only recordings explicitly enrolled in automatic cleanup. Legacy recordings stay untouched.
    var audioDeletionPending: [String]?
    var audioTranscriptionComplete: Bool?
    var audioRecoveryNeeded: Bool?
    var audioDeletionReady: Bool?
    var audioDeletionError: String?
    var recordingGaps: [RecordingGap]?
    /// Optional so meetings saved before anchors existed decode unchanged.
    var audioAnchors: [AudioAnchor]?
    var transcriptionGaps: [TranscriptionGap]?
    var participants: [MeetingParticipant]?
    var tasks: [MeetingTask]?
    var taskExtraction: TaskExtractionState?
    /// Compressed Teams speaker evidence of all recording sessions, appended at each stop (offline replay/eval).
    /// Optional so meetings saved before it existed decode unchanged.
    var speakerTimeline: [TeamsTimelineEntry]?
    /// Clusters of the offline acoustic diarization (Phase 3). Optional so older meetings decode unchanged.
    var acousticSpeakers: [AcousticSpeaker]?
    /// Last acoustic diarization run (status, engine, segments). Optional so older meetings decode unchanged.
    var diarization: DiarizationRun?
    /// Assigned only when a new meeting is created with mirroring enabled. Never inferred for old files.
    var transcriptMirror: TranscriptMirrorDestination?
    var personSummary: PersonSummaryRun?
    var personSummaryError: String?

    var needsSuggestedTitle: Bool {
        let value = title.trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty || ["New note", "Untitled meeting", "Neue Notiz", "Unbenanntes Meeting"].contains(value)
    }

    var orderedTranscript: [TranscriptSegment] {
        transcript.sorted { $0.offset == $1.offset ? $0.id.uuidString < $1.id.uuidString : $0.offset < $1.offset }
    }
    /// Equivalent to `!transcriptText.isEmpty` without building it: `transcriptRuns` keeps every non-blank word, or the non-blank text of a segment without words.
    var hasTranscriptText: Bool {
        transcript.contains { segment in
            if let words = segment.words, !words.isEmpty { return words.contains { !$0.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty } }
            return !segment.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        }
    }
    var transcriptText: String {
        transcriptRuns.map { "[\(Self.timestamp($0.offset))] \(speakerName($0.speaker, participantID: $0.participantID)): \($0.text)" }.joined(separator: "\n")
    }
    var conversation: [TranscriptTurn] {
        var turns: [TranscriptTurn] = []
        for run in transcriptRuns {
            if let last = turns.indices.last, turns[last].speaker == run.speaker,
               turns[last].participantID == run.participantID {
                turns[last].text += " " + run.text
                turns[last].isFinal = turns[last].isFinal && run.isFinal
                turns[last].references += run.references
            } else {
                turns.append(run)
            }
        }
        return turns
    }
    var conversationText: String {
        conversation.map { "[\(Self.timestamp($0.offset))] \(speakerName($0.speaker, participantID: $0.participantID)): \($0.text)" }.joined(separator: "\n\n")
    }
    var copyableTranscript: String {
        let text = conversation.map { turn in
            let name = turn.speaker == .me ? "Me" : (participants?.first(where: { $0.id == turn.participantID })?.name ?? "Others")
            return "[\(Self.timestamp(turn.offset))] \(name): \(turn.text)" + (turn.isFinal ? "" : " [interim]")
        }.joined(separator: "\n\n")
        let gaps = (transcriptionGaps ?? []).map { "[\(Self.timestamp($0.start))] Transcription gap / unconfirmed audio: \($0.reason)" }
        return ([text] + gaps).filter { !$0.isEmpty }.joined(separator: "\n\n")
    }
    static func timestamp(_ value: TimeInterval) -> String {
        let seconds = max(0, Int(value))
        return String(format: "%02d:%02d", seconds / 60, seconds % 60)
    }
    mutating func upsert(_ segment: TranscriptSegment) {
        guard !segment.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        if let i = transcript.firstIndex(where: { $0.id == segment.id }) {
            transcript[i] = segment
        } else { transcript.append(segment) }
    }
    var markdown: String {
        let gaps = (transcriptionGaps ?? []).map {
            "- \(Self.timestamp($0.start)) · \($0.speaker.rawValue) · \(String(format: "%.2f", $0.duration)) s: \($0.reason)"
        }
        let warning = gaps.isEmpty ? "" : "\n## Transcription gaps / unconfirmed audio\n\n" + gaps.joined(separator: "\n") + "\n"
        return "# \(title)\n\n\(createdAt.formatted()) · \(Self.timestamp(duration))\n\n## My notes\n\n\(notes)\n\n## Enhanced notes\n\n\(enhancedNotes)\n\n## Transcript\n\n\(conversationText)\n" + warning
    }
}

enum EnhancementProvider: String, Codable, CaseIterable {
    case disabled = "Disabled"
    case anthropic = "Anthropic"
    case openai = "OpenAI"
    case compatible = "Custom API"
}

enum TranscriptionProvider: String, Codable, CaseIterable {
    case deepgram = "Deepgram"
    case openWhispr = "OpenWhispr · Whisper Turbo (local)"
    case openai = "OpenAI"

    var supportedModel: String {
        switch self {
        case .deepgram: return "nova-3"
        case .openai: return "gpt-transcribe"
        case .openWhispr: return "large-v3-turbo"
        }
    }
}

struct AppSettings: Codable, Equatable {
    static let openAITranscriptionModel = "gpt-transcribe"
    var transcriptionProvider: TranscriptionProvider = .deepgram {
        didSet { transcriptionModel = transcriptionProvider.supportedModel }
    }
    var openWhisprAppPath: String = "/Applications/OpenWhispr.app"
    var whisperModelPath: String = LocalWhisperInstallation.defaultModelPath
    var transcriptionConfigurationVersion: Int = 3
    var transcriptionModel: String = "nova-3"
    var locale: String = "auto"
    var provider: EnhancementProvider = .openai
    var endpoint: String = ""
    var model: String = SummaryModels.defaultModel(for: .openai)
    var enhancementModels: [String: String] = [:]
    var anthropicWorkspaceID = ""
    var onboardingCompleted = false
    var keepAudio: Bool = true
    var deleteAudioAfterProcessing = true
    var taskOwnerName = ""
    var audioSourceBundleID: String = ""
    var automaticMeetingAudioSource = true
    var microphoneUID = ""
    var teamsSpeakerNames = true
    /// Offline acoustic speaker separation after a recording stops (FluidAudio).
    var acousticSpeakerRecognition = true
    /// Stops the recording once the Teams call it has seen is over.
    var autoStopWhenMeetingEnds = true
    var transcriptMirrorEnabled = false
    var transcriptMirrorFolder: TranscriptMirrorDestination?
    var personSummariesEnabled = true
    var usesTeamsNames: Bool { teamsSpeakerNames && !automaticMeetingAudioSource && audioSourceBundleID == TeamsAccessibility.bundleID }
    func openAIModel(for speaker: Speaker) -> String {
        usesTeamsNames && speaker == .others ? "whisper-1" : Self.openAITranscriptionModel
    }
    var transcriptionStatus: String {
        if transcriptionProvider == .deepgram { return "Deepgram · Nova-3 · Live streaming" }
        if transcriptionProvider == .openWhispr { return "Whisper Turbo on this Mac" }
        return usesTeamsNames ? "OpenAI · Teams: Whisper · Me: GPT-Transcribe" : "Transcription with OpenAI · GPT-Transcribe"
    }


    init() { }
    enum CodingKeys: String, CodingKey {
        case transcriptionProvider, transcriptionModel, locale, provider, endpoint, model, keepAudio, audioSourceBundleID
        case openWhisprAppPath, whisperModelPath, transcriptionConfigurationVersion
        case teamsSpeakerNames, microphoneUID, automaticMeetingAudioSource, acousticSpeakerRecognition
        case autoStopWhenMeetingEnds, deleteAudioAfterProcessing, taskOwnerName
        case transcriptMirrorEnabled, transcriptMirrorFolder, personSummariesEnabled
        case onboardingCompleted, enhancementModels, anthropicWorkspaceID
    }
    init(from decoder: Decoder) throws {
        self.init()
        let values = try decoder.container(keyedBy: CodingKeys.self)
        // Version 3 selects Deepgram once. Later explicit provider choices round-trip unchanged.
        let version = try values.decodeIfPresent(Int.self, forKey: .transcriptionConfigurationVersion) ?? 1
        if version >= 3 {
            let savedProvider = try values.decodeIfPresent(String.self, forKey: .transcriptionProvider)
            // The removed Apple Speech option uses the current default.
            if let savedProvider, savedProvider != "On this Mac" {
                guard let provider = TranscriptionProvider(rawValue: savedProvider) else {
                    throw DecodingError.dataCorruptedError(forKey: .transcriptionProvider, in: values, debugDescription: "Unknown transcription provider: \(savedProvider)")
                }
                transcriptionProvider = provider
            }
        }
        openWhisprAppPath = try values.decodeIfPresent(String.self, forKey: .openWhisprAppPath) ?? openWhisprAppPath
        whisperModelPath = try values.decodeIfPresent(String.self, forKey: .whisperModelPath) ?? whisperModelPath
        // Normalize within the selected provider, never force Nova-3 back to an OpenAI model.
        transcriptionModel = transcriptionProvider.supportedModel
        let savedLocale = try values.decodeIfPresent(String.self, forKey: .locale) ?? "auto"
        switch Locale(identifier: savedLocale).language.languageCode?.identifier {
        case "de": locale = "de-DE"
        case "en": locale = "en-US"
        default: locale = "auto"
        }
        // Removed providers (Local Ollama, Apple Intelligence) and unknown values fall back to Off without a model.
        let savedEnhancement = try values.decodeIfPresent(String.self, forKey: .provider)
        let removedProvider = savedEnhancement != nil && EnhancementProvider(rawValue: savedEnhancement!) == nil
        provider = removedProvider ? .disabled : (savedEnhancement.flatMap(EnhancementProvider.init(rawValue:)) ?? provider)
        endpoint = try values.decodeIfPresent(String.self, forKey: .endpoint) ?? endpoint
        if endpoint == "http://localhost:11434/v1/chat/completions", provider != .compatible { endpoint = "" }
        model = removedProvider ? "" : (try values.decodeIfPresent(String.self, forKey: .model) ?? SummaryModels.defaultModel(for: provider))
        enhancementModels = try values.decodeIfPresent([String: String].self, forKey: .enhancementModels) ?? [:]
        anthropicWorkspaceID = try values.decodeIfPresent(String.self, forKey: .anthropicWorkspaceID) ?? ""
        // Existing installations have already been configured; new settings explicitly store false.
        onboardingCompleted = try values.decodeIfPresent(Bool.self, forKey: .onboardingCompleted) ?? true
        keepAudio = try values.decodeIfPresent(Bool.self, forKey: .keepAudio) ?? keepAudio
        deleteAudioAfterProcessing = try values.decodeIfPresent(Bool.self, forKey: .deleteAudioAfterProcessing) ?? false
        // The removed "don't store audio" choice becomes automatic deletion after processing.
        if !keepAudio { keepAudio = true; deleteAudioAfterProcessing = true }
        taskOwnerName = try values.decodeIfPresent(String.self, forKey: .taskOwnerName) ?? ""
        audioSourceBundleID = try values.decodeIfPresent(String.self, forKey: .audioSourceBundleID) ?? audioSourceBundleID
        // Adopt automatic routing once for the two common sources; preserve other apps
        // and every explicit choice saved by a version with this setting.
        automaticMeetingAudioSource = try values.decodeIfPresent(Bool.self, forKey: .automaticMeetingAudioSource)
            ?? (audioSourceBundleID.isEmpty || audioSourceBundleID == TeamsAccessibility.bundleID)
        microphoneUID = try values.decodeIfPresent(String.self, forKey: .microphoneUID) ?? ""
        teamsSpeakerNames = version < 3 ? true : (try values.decodeIfPresent(Bool.self, forKey: .teamsSpeakerNames) ?? true)
        acousticSpeakerRecognition = try values.decodeIfPresent(Bool.self, forKey: .acousticSpeakerRecognition) ?? true
        autoStopWhenMeetingEnds = try values.decodeIfPresent(Bool.self, forKey: .autoStopWhenMeetingEnds) ?? true
        transcriptMirrorEnabled = try values.decodeIfPresent(Bool.self, forKey: .transcriptMirrorEnabled) ?? false
        transcriptMirrorFolder = try values.decodeIfPresent(TranscriptMirrorDestination.self, forKey: .transcriptMirrorFolder)
        personSummariesEnabled = try values.decodeIfPresent(Bool.self, forKey: .personSummariesEnabled) ?? true
        transcriptionConfigurationVersion = 3
    }
}

enum AudioRetention: Hashable { case keep, deleteAfterProcessing }

extension AppSettings {
    /// The two user-facing audio choices, stored in the existing `keepAudio`/`deleteAudioAfterProcessing` flags.
    var audioRetention: AudioRetention {
        get { keepAudio && !deleteAudioAfterProcessing ? .keep : .deleteAfterProcessing }
        set { keepAudio = true; deleteAudioAfterProcessing = newValue == .deleteAfterProcessing }
    }
}

struct AudioSource: Identifiable, Hashable {
    var id: String
    var name: String
}

enum MeetingError: LocalizedError {
    case message(String)
    var errorDescription: String? {
        switch self { case .message(let text): return text }
    }
}
