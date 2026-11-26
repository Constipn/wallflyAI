import SwiftUI
import AppKit
import AVFoundation
import CoreGraphics
import ApplicationServices

struct MeetingOnboardingView: View {
    @ObservedObject var store: MeetingStore
    @Environment(\.dismiss) private var dismiss
    @ViewState private var step: Int
    private static let titles = ["Storage", "AI services", "Permissions"]
    private var titles: [String] { Self.titles }
    private var isLastStep: Bool { step >= titles.count - 1 }

    init(store: MeetingStore, initialStep: Int = 0) {
        self.store = store
        _step = ViewState(initialValue: min(max(initialStep, 0), Self.titles.count - 1))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 10) {
                Text("Set up Wallfly").font(.system(size: 27, design: .serif))
                Text("Three short steps. You can change everything later.")
                    .font(.system(size: 13)).foregroundStyle(Palette.muted)
                HStack(spacing: 8) {
                    ForEach(titles.indices, id: \.self) { index in
                        if index > 0 { Text("·").foregroundStyle(Palette.muted) }
                        Text("\(index + 1) \(titles[index])")
                            .font(.system(size: 11, weight: index == step ? .semibold : .regular))
                            .foregroundStyle(index == step ? Palette.red600 : Palette.muted)
                    }
                }.font(.system(size: 11)).padding(.top, 6)
            }.padding(24)
            Divider()
            Form {
                switch step {
                case 0:
                    StorageLocationSection(store: store)
                    AudioRetentionSection(store: store)
                case 1:
                    TranscriptionSection(store: store)
                    SummarySection(store: store)
                    TaskOwnerSection(store: store)
                default:
                    RecordingPermissionsView(store: store)
                    Section {
                        ForEach(openIssues, id: \.self) { SettingsWarning(text: $0) }
                        settingsDetail("You can fill in anything missing at any time in Settings.")
                    }
                }
            }.formStyle(.grouped).scrollContentBackground(.hidden).disabled(store.busy)
            if let error = store.errorMessage {
                Text(error).font(Design.metadata).foregroundStyle(Palette.red600)
                    .textSelection(.enabled).padding(.horizontal, 24).padding(.bottom, 10)
            }
            Divider()
            HStack(spacing: 14) {
                Button("Set up later") { finish() }.disabled(store.busy)
                Spacer()
                if store.isMovingLibrary {
                    ProgressView().controlSize(.small)
                    Text("Moving library…").font(Design.metadata)
                }
                if step > 0 { Button("Back") { step -= 1 }.disabled(store.busy) }
                Button(isLastStep ? "Done" : "Next") {
                    if isLastStep { finish() } else if store.saveSettings() { step += 1 }
                }.buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction).disabled(store.busy)
            }.padding(20)
        }
        .frame(width: 690)
        .frame(height: min(750, (NSScreen.main?.visibleFrame.height ?? 850) - 100))
        .background(Palette.paper).foregroundStyle(Palette.ink).tint(Palette.red600)
        .interactiveDismissDisabled()
        .onAppear { store.isSettingUp = true; store.refreshMicrophones(); store.refreshTeamsSpeakerStatus() }
        .onDisappear { store.isSettingUp = false }
        .onChange(of: store.settings.transcriptionProvider) { _, _ in store.refreshTeamsSpeakerStatus() }
        .onChange(of: store.settings.microphoneUID) { _, _ in store.applyMicrophoneSelection() }
    }

    /// Only what still blocks transcription or summaries; a deliberately disabled summary is not an issue.
    private var openIssues: [String] {
        [store.transcriptionConfigurationIssue,
         store.settings.provider == .disabled ? nil : store.enhancementConfigurationIssue].compactMap { $0 }
    }

    private func finish() {
        if store.completeOnboarding() { dismiss() }
    }
}

struct RecordingPermissionsView: View {
    @ObservedObject var store: MeetingStore
    @ViewState private var microphoneAllowed = AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
    @ViewState private var screenAllowed = CGPreflightScreenCaptureAccess()
    @ViewState private var accessibilityAllowed = AXIsProcessTrusted()
    @ViewState private var message: String?

    var body: some View {
        Section("macOS permissions") {
            permission("Microphone", allowed: microphoneAllowed) {
                if AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined {
                    Task { microphoneAllowed = await AVCaptureDevice.requestAccess(for: .audio) }
                } else { openPrivacy("Privacy_Microphone") }
            }
            permission("Screen & system audio", detail: "Audio only – no screen images are saved.", allowed: screenAllowed) {
                if screenAllowed { openPrivacy("Privacy_ScreenCapture"); return }
                _ = CGRequestScreenCaptureAccess()
                refresh()
                message = "After a change, the app may need to restart."
                if !screenAllowed { openPrivacy("Privacy_ScreenCapture") }
            }
            permission("Teams speaker names · optional", allowed: accessibilityAllowed) {
                if accessibilityAllowed { openPrivacy("Privacy_Accessibility"); return }
                store.requestTeamsAccessibility()
                refresh()
                if !accessibilityAllowed { openPrivacy("Privacy_Accessibility") }
            }
            if let message { settingsDetail(message) }
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in refresh() }
    }

    private func permission(_ name: String, detail: String? = nil, allowed: Bool, action: @escaping () -> Void) -> some View {
        HStack {
            Label {
                VStack(alignment: .leading, spacing: 2) {
                    Text(name)
                    if let detail { settingsDetail(detail) }
                }
            } icon: { Image(systemName: allowed ? "checkmark.circle.fill" : "circle") }
            Spacer()
            Button(allowed ? "Settings" : "Allow…", action: action)
        }
    }
    private func refresh() {
        microphoneAllowed = AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
        screenAllowed = CGPreflightScreenCaptureAccess()
        accessibilityAllowed = AXIsProcessTrusted()
    }
    private func openPrivacy(_ pane: String) {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(pane)") { NSWorkspace.shared.open(url) }
    }
}
