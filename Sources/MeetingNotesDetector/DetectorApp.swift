import AppKit
import ApplicationServices
import SwiftUI
import TeamsDetectionKit

@main
enum DetectorApp {
    @MainActor static func main() {
        let app = NSApplication.shared
        let delegate = DetectorDelegate()
        app.setActivationPolicy(.accessory)
        app.delegate = delegate
        withExtendedLifetime(delegate) { app.run() }
    }
}

@MainActor
final class DetectorDelegate: NSObject, NSApplicationDelegate {
    private let storage = DetectionStorage()
    private let reader = TeamsCallReader()
    private let queue = DispatchQueue(label: "meetingnotes.call-detection", qos: .utility)
    private var policy = DetectionPolicy()
    private var timer: Timer?
    private var sampling = false
    private var sleeping = false
    private var panel: MeetingDetectedPanel?
    private var displayedEvent: UUID?
    private var launchPending = false
    private var popupError: String?
    private var lastFrame: CGRect?
    private var observers: [NSObjectProtocol] = []
    private var pendingDiagnostic: DetectionDiagnosticRequest?
    private var helperVersion: String { Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "development" }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // LaunchServices normally reuses the helper; also reject a direct duplicate launch.
        if NSRunningApplication.runningApplications(withBundleIdentifier: "io.github.constipn.wallfly.detector")
            .contains(where: { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier }) {
            NSApp.terminate(nil); return
        }
        policy = storage.read("session.json", as: DetectionPolicy.self) ?? .init()
        policy.resume()
        // Continue a recently requested, unsuccessful diagnostic across this
        // update. Ordinary launches without that prior request collect no trace.
        if let report = storage.read("diagnostic-report.json", as: DetectionDiagnosticReport.self),
           report.shouldRefresh(helperVersion: helperVersion),
           !FileManager.default.fileExists(atPath: storage.root.appendingPathComponent("diagnostic-request.json").path) {
            try? storage.write(DetectionDiagnosticRequest(), to: "diagnostic-request.json")
        }
        timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
        for name in [NSApplication.didChangeScreenParametersNotification] {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in self?.positionPanel() }
            })
        }
        observers.append(NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in
                self?.sleeping = true; self?.policy.resume(); self?.hidePopup()
            }
        })
        observers.append(NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in
                self?.sleeping = false; self?.policy.resume(); self?.tick()
            }
        })
        tick()
    }

    func applicationWillTerminate(_ notification: Notification) {
        timer?.invalidate()
        try? storage.remove("status.json")
    }

    private func tick() {
        guard !sleeping else { return }
        let config = storage.read("configuration.json", as: DetectionConfiguration.self) ?? .init()
        guard config.enabled else { hidePopup(); NSApp.terminate(nil); return }
        if storage.read("accessibility-request.json", as: Bool.self) == true {
            try? storage.remove("accessibility-request.json")
            if !AXIsProcessTrusted() {
                _ = AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary)
                if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
                    NSWorkspace.shared.open(url)
                }
            }
        }
        let suppress = mainAppSuppressesPopup()
        if suppress { hidePopup() }
        guard !sampling else { return }
        sampling = true
        if let request = try? storage.takeDiagnosticRequest() { pendingDiagnostic = request }
        if pendingDiagnostic?.isFresh(at: Date()) == false { pendingDiagnostic = nil }
        let diagnosticRequest = pendingDiagnostic
        queue.async { [reader, weak self] in
            let snapshot = reader.sample(captureDiagnostics: diagnosticRequest != nil)
            Task { @MainActor in self?.receive(snapshot, diagnosticRequest: diagnosticRequest) }
        }
    }

    private func mainAppSuppressesPopup() -> Bool {
        let running = !NSRunningApplication.runningApplications(withBundleIdentifier: "io.github.constipn.wallfly").isEmpty
        return running && (storage.read("heartbeat.json", as: DetectionHeartbeat.self)?.suppressesPopup(at: Date()) == true)
    }

    private func receive(_ snapshot: TeamsDetectionSnapshot, diagnosticRequest: DetectionDiagnosticRequest?) {
        sampling = false
        guard !sleeping else { return }
        guard (storage.read("configuration.json", as: DetectionConfiguration.self) ?? .init()).enabled else {
            hidePopup(); return
        }
        let trusted = AXIsProcessTrusted()
        var message = snapshot.status
        if let diagnosticRequest, !snapshot.diagnostics.initializing {
            pendingDiagnostic = nil
            do {
                try storage.write(DetectionDiagnosticReport(request: diagnosticRequest, snapshot: snapshot,
                    trusted: trusted, helperVersion: helperVersion), to: "diagnostic-report.json")
            } catch { message += " · Diagnostics could not be saved" }
        }
        try? storage.write(DetectionStatus(message: message, accessibilityTrusted: trusted,
                                           helperVersion: helperVersion), to: "status.json")
        let previous = policy
        let event = policy.observe(snapshot.sample, at: Date().timeIntervalSince1970)
        if policy != previous {
            do { try storage.write(policy, to: "session.json") }
            catch { hidePopup(); return } // No persistent suppression means no repeated pop-ups.
        }
        switch snapshot.sample {
        case .active:
            lastFrame = snapshot.windowFrame
            if let event, !mainAppSuppressesPopup() { showPopup(event) }
            else if displayedEvent == policy.eventID, displayedEvent != nil, !mainAppSuppressesPopup() {
                positionPanel(); panel?.orderFrontRegardless()
            }
        case .notRunning:
            hidePopup()
        case .absent, .unknown:
            // Temporarily unreadable/minimized views do not dismiss the current offer.
            panel?.orderOut(nil)
            if policy.eventID == nil { hidePopup() }
        }
    }

    private func showPopup(_ event: UUID) {
        displayedEvent = event
        popupError = nil
        if panel == nil {
            let panel = MeetingDetectedPanel(contentRect: NSRect(origin: .zero, size: MeetingDetectedPopup.size),
                styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
            panel.isReleasedWhenClosed = false
            panel.isOpaque = false; panel.backgroundColor = .clear
            panel.hasShadow = true; panel.hidesOnDeactivate = false; panel.canHide = false
            panel.level = .floating
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
            self.panel = panel
        }
        updateContent()
        positionPanel()
        panel?.orderFrontRegardless()
    }

    private func updateContent() {
        panel?.contentView = NSHostingView(rootView: MeetingDetectedPopup(error: popupError, launching: launchPending,
            open: { [weak self] in self?.openMeeting() }, close: { [weak self] in self?.hidePopup() }))
    }

    private func positionPanel() {
        guard let panel else { return }
        let screen = lastFrame == nil ? NSScreen.main : NSScreen.screens.max { left, right in
            intersectionArea(left) < intersectionArea(right)
        }
        if let frame = screen?.visibleFrame {
            panel.setFrameOrigin(NSPoint(x: frame.maxX - panel.frame.width - 20, y: frame.maxY - panel.frame.height - 14))
        }
    }

    private func intersectionArea(_ screen: NSScreen) -> CGFloat {
        guard let lastFrame, let primary = NSScreen.screens.first else { return 0 }
        // AX coordinates start at the top left of the primary display.
        let converted = CGRect(x: lastFrame.minX, y: primary.frame.maxY - lastFrame.maxY,
                               width: lastFrame.width, height: lastFrame.height)
        let intersection = converted.intersection(screen.frame)
        return intersection.isNull ? 0 : intersection.width * intersection.height
    }

    private func hidePopup() { panel?.orderOut(nil); displayedEvent = nil }

    private func openMeeting() {
        guard !launchPending, let event = displayedEvent else { return }
        let request = DetectionRequest(id: event)
        do { try storage.write(request, to: "request.json") }
        catch { popupError = "Could not prepare launch. Try again."; updateContent(); return }
        // Resolve the owning bundle, avoiding a stale or unrelated LaunchServices copy.
        let parent = Bundle.main.bundleURL.deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        guard Bundle(url: parent)?.bundleIdentifier == "io.github.constipn.wallfly" else {
            popupError = "Wallfly was not found."; updateContent(); return
        }
        launchPending = true; updateContent()
        NSWorkspace.shared.open([request.url], withApplicationAt: parent, configuration: .init()) { [weak self] _, error in
            Task { @MainActor in
                guard let self else { return }
                self.launchPending = false
                if error != nil {
                    self.popupError = "Could not open. Try again."; self.updateContent()
                } else { self.hidePopup() }
            }
        }
    }
}

private final class MeetingDetectedPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

private struct MeetingDetectedPopup: View {
    static let size = CGSize(width: 420, height: 90)

    let error: String?
    let launching: Bool
    let open: () -> Void
    let close: () -> Void
    private let accent = Color(red: 244/255, green: 54/255, blue: 76/255)

    var body: some View {
        ZStack(alignment: .topLeading) {
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 15) {
                    RoundedRectangle(cornerRadius: 1.5).fill(accent).frame(width: 3, height: 33)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Meeting detected").font(.system(size: 14, weight: .medium))
                        Text("Microsoft Teams").font(.system(size: 13)).foregroundStyle(.secondary)
                    }.lineLimit(1).fixedSize()
                    Spacer(minLength: 0)
                    Button(action: open) {
                        HStack(spacing: 7) {
                            Image(systemName: "waveform.circle.fill").font(.system(size: 18)).foregroundStyle(accent)
                            Text(launching ? "Opening…" : "Take notes").font(.system(size: 13, weight: .medium))
                        }.padding(.horizontal, 12).frame(height: 45)
                            .background(.white, in: RoundedRectangle(cornerRadius: 14))
                            .overlay(RoundedRectangle(cornerRadius: 14).stroke(Color.gray.opacity(0.25)))
                            .contentShape(RoundedRectangle(cornerRadius: 14))
                    }.buttonStyle(.plain).fixedSize().disabled(launching)
                }
                if let error {
                    Text(error).font(.system(size: 10)).foregroundStyle(accent)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }.padding(.horizontal, 15).frame(height: 72)
                .background(.white, in: RoundedRectangle(cornerRadius: 18))
                .overlay(RoundedRectangle(cornerRadius: 18).stroke(Color.gray.opacity(0.18)))
                .padding(.top, 12).padding(.leading, 10)
            Button(action: close) {
                Image(systemName: "xmark").font(.system(size: 9, weight: .bold))
                    .frame(width: 24, height: 24).background(Color(white: 0.94), in: Circle())
                    .contentShape(Circle())
            }.buttonStyle(.plain).accessibilityLabel("Dismiss for this meeting")
        }.foregroundStyle(Color(white: 0.16)).frame(width: Self.size.width, height: Self.size.height)
            .preferredColorScheme(.light)
    }
}
