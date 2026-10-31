import AppKit
import Combine
import ServiceManagement
import SwiftUI
import TeamsDetectionKit

@MainActor
final class MeetingDetectionService: ObservableObject {
    static let shared = MeetingDetectionService()
    @Published private(set) var enabled = true
    @Published private(set) var status = "Setting up Teams detection…"
    @Published private(set) var needsLoginApproval = false
    @Published private(set) var accessibilityTrusted = false
    @Published private(set) var helperResponding = false
    @Published private(set) var error: String?
    @Published private(set) var diagnosticStatus: String?
    @Published private(set) var diagnosticReportAvailable = false
    private let storage = DetectionStorage()
    private let service = SMAppService.loginItem(identifier: "io.github.constipn.wallfly.detector")
    private weak var store: MeetingStore?
    private var timer: Timer?
    private var subscriptions = Set<AnyCancellable>()
    private var pendingDiagnostic: DetectionDiagnosticRequest?
    private var helperLaunchPending = false

    private init() { }

    func start(store: MeetingStore) {
        guard timer == nil else { return }
        self.store = store
        enabled = (storage.read("configuration.json", as: DetectionConfiguration.self) ?? .init()).enabled
        // Register once on first launch. Later OS disablement requires an explicit user action.
        let defaults = UserDefaults.standard
        if enabled && !defaults.bool(forKey: "meetingDetectionRegistrationAttempted") {
            defaults.set(true, forKey: "meetingDetectionRegistrationAttempted")
            register()
        } else if enabled && service.status == .enabled { launchHelper() }
        Publishers.CombineLatest4(store.$isStarting, store.$isRecording, store.$isStopping, store.$presentationMode)
            .sink { [weak self] _ in Task { @MainActor in self?.publishHeartbeat() } }.store(in: &subscriptions)
        store.$isEnhancing.sink { [weak self] _ in Task { @MainActor in self?.publishHeartbeat() } }.store(in: &subscriptions)
        store.$extractingTasksID.sink { [weak self] _ in Task { @MainActor in self?.publishHeartbeat() } }.store(in: &subscriptions)
        timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refresh(); self?.publishHeartbeat() }
        }
        refresh(); publishHeartbeat()
    }

    func setEnabled(_ value: Bool) {
        do { try storage.write(DetectionConfiguration(enabled: value), to: "configuration.json") }
        catch { self.error = "Setting could not be saved: \(error.localizedDescription)"; return }
        enabled = value; error = nil
        if value { register() }
        else {
            // The helper observes configuration even if unregistering fails.
            do { try service.unregister() }
            catch { self.error = "Launch at login could not be disabled: \(error.localizedDescription)" }
        }
        refresh()
    }

    private func register() {
        do {
            if service.status != .enabled { try service.register() }
            if service.status == .enabled { launchHelper() }
        } catch { self.error = "The background helper could not be registered: \(error.localizedDescription)" }
    }

    private func launchHelper() {
        guard !helperLaunchPending else { return }
        let helper = Bundle.main.bundleURL.appendingPathComponent("Contents/Library/LoginItems/Wallfly Detector.app")
        guard FileManager.default.fileExists(atPath: helper.path) else {
            error = "Background helper is missing. Please open the built Wallfly.app."; return
        }
        helperLaunchPending = true
        Task { @MainActor [weak self] in
            guard let self else { return }
            defer { self.helperLaunchPending = false }
            let expected = Bundle(url: helper)?.object(forInfoDictionaryKey: "CFBundleVersion") as? String
            let running = NSRunningApplication.runningApplications(withBundleIdentifier: "io.github.constipn.wallfly.detector")
            // LaunchServices otherwise keeps the old process alive after a bundle update.
            if !running.isEmpty, self.storage.read("status.json", as: DetectionStatus.self)?.helperVersion != expected {
                for process in running { process.terminate() }
                let deadline = ProcessInfo.processInfo.systemUptime + 5
                while running.contains(where: { !$0.isTerminated }), ProcessInfo.processInfo.systemUptime < deadline {
                    try? await Task.sleep(for: .milliseconds(100))
                }
                guard running.allSatisfy(\.isTerminated) else {
                    self.error = "The previous background helper could not be stopped."; return
                }
            }
            let configuration = NSWorkspace.OpenConfiguration()
            configuration.activates = false
            do { _ = try await NSWorkspace.shared.openApplication(at: helper, configuration: configuration) }
            catch { self.error = "The background helper could not be started: \(error.localizedDescription)" }
        }
    }

    func requestDiagnostic() {
        guard enabled, service.status == .enabled else {
            error = "Enable the background helper first."; return
        }
        let request = DetectionDiagnosticRequest()
        do { try storage.write(request, to: "diagnostic-request.json") }
        catch { self.error = "Diagnostics could not be requested: \(error.localizedDescription)"; return }
        pendingDiagnostic = request
        diagnosticStatus = "Creating Teams diagnostics…"
        launchHelper()
    }

    func showDiagnostic() {
        NSWorkspace.shared.open(storage.root.appendingPathComponent("diagnostic-report.json"))
    }

    func requestAccessibility() {
        guard enabled else { return }
        do { try storage.write(true, to: "accessibility-request.json") }
        catch { self.error = error.localizedDescription; return }
        if service.status == .enabled { launchHelper() }
        else { status = "Enable the background helper first." }
    }

    func openLoginSettings() { SMAppService.openSystemSettingsLoginItems() }

    func refresh() {
        let report = storage.read("diagnostic-report.json", as: DetectionDiagnosticReport.self)
        diagnosticReportAvailable = report != nil
        if let pendingDiagnostic {
            if report?.requestID == pendingDiagnostic.id {
                diagnosticStatus = "Teams diagnostics created. They contain only technical structure data."
                self.pendingDiagnostic = nil
            } else if Date().timeIntervalSince(pendingDiagnostic.date) > 15 {
                diagnosticStatus = "No diagnostics response. Please re-enable the background helper."
                self.pendingDiagnostic = nil
            }
        }
        needsLoginApproval = enabled && service.status == .requiresApproval
        accessibilityTrusted = false; helperResponding = false
        if !enabled { status = "Teams detection is off"; return }
        switch service.status {
        case .requiresApproval:
            status = "Allow Wallfly in System Settings → General → Login Items."
        case .enabled:
            if let heartbeat = storage.read("status.json", as: DetectionStatus.self),
               Date().timeIntervalSince(heartbeat.date) < 8,
               !NSRunningApplication.runningApplications(withBundleIdentifier: "io.github.constipn.wallfly.detector").isEmpty {
                status = heartbeat.message; accessibilityTrusted = heartbeat.accessibilityTrusted; helperResponding = true
            } else { status = "Background helper is not responding. Re-enable detection." }
        case .notRegistered:
            status = "Background helper not registered. Re-enable detection."
        case .notFound:
            status = "Background helper is missing from the app bundle."
        @unknown default:
            status = "Background helper status unknown."
        }
    }

    private func publishHeartbeat() {
        guard let store else { return }
        try? storage.write(DetectionHeartbeat(busy: store.busy, presenting: store.presentationMode), to: "heartbeat.json")
    }
}

struct MeetingDetectionSetup: View {
    @ObservedObject private var detection = MeetingDetectionService.shared
    var body: some View {
        Toggle("Detect Teams meetings automatically", isOn: Binding(
            get: { detection.enabled }, set: { detection.setEnabled($0) }))
        if detection.enabled && detection.needsLoginApproval {
            SettingsWarning(text: "Background operation not allowed", button: "Allow…") { detection.openLoginSettings() }
        } else if detection.enabled && detection.helperResponding && !detection.accessibilityTrusted {
            SettingsWarning(text: "Accessibility access for “Wallfly Detector” is missing", button: "Allow…") { detection.requestAccessibility() }
        }
        if let error = detection.error { SettingsWarning(text: error) }
    }
}

struct MeetingDetectionDiagnostics: View {
    @ObservedObject private var detection = MeetingDetectionService.shared
    let speakerStatus: String
    let refreshSpeakers: () -> Void
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Detection: " + detection.status)
            Text("Speaker names: " + speakerStatus)
            HStack {
                Button("Check again") { detection.refresh(); refreshSpeakers() }
                if detection.enabled {
                    Button("Restart background helper") { detection.setEnabled(true) }
                    Button("Login Items…") { detection.openLoginSettings() }
                }
            }
            if detection.enabled {
                HStack {
                    Button("Create Teams diagnostics") { detection.requestDiagnostic() }
                    if detection.diagnosticReportAvailable { Button("Show diagnostics") { detection.showDiagnostic() } }
                }
            }
            if let status = detection.diagnosticStatus { Text(status) }
        }.font(Design.metadata).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
    }
}
