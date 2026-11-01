import SwiftUI
import AppKit
import TeamsDetectionKit

@main
struct MeetingNotesApp: App {
    @StateObject private var store = MeetingStore()
    @NSApplicationDelegateAdaptor(MeetingAppDelegate.self) private var appDelegate
    init() {
        // Rename the legacy data folder before the store or the detection service touch it.
        DataFolderStartup.prepare()
    }
    var body: some Scene {
        Window("Wallfly", id: "main") {
            MeetingWorkspace(store: store)
                .sheet(isPresented: $store.showOnboarding) { MeetingOnboardingView(store: store) }
                .onChange(of: store.showOnboarding) { _, showing in
                    if !showing && store.settings.onboardingCompleted { MeetingDetectionService.shared.start(store: store) }
                }
                .background(MeetingWindowAccessor { window in appDelegate.attach(store: store, window: window) })
                .frame(minWidth: 360, minHeight: 520)
                .preferredColorScheme(.light)
        }
        .windowStyle(.hiddenTitleBar)
        .defaultSize(width: 1340, height: 850)
        .commands {
            CommandGroup(replacing: .newItem) {
                Button("New note") { Task { await store.newNote() } }
                    .keyboardShortcut("n")
                    .disabled(store.busy || store.showOnboarding || store.isSettingUp)
            }
            CommandMenu("Meeting") { MeetingMenuCommands() }
        }
        Settings {
            MeetingSettingsView(store: store)
                .preferredColorScheme(.light)
        }
    }
}

@MainActor
final class MeetingAppDelegate: NSObject, NSApplicationDelegate {
    weak var store: MeetingStore?
    private var liveIndicator: LiveMeetingIndicatorController?
    private var mainWindow: NSWindow?
    private var quitPending = false
    private var pendingDetectionURLs: [URL] = []

    func attach(store: MeetingStore, window: NSWindow) {
        self.store = store
        mainWindow = window
        window.isReleasedWhenClosed = false
        if liveIndicator == nil { liveIndicator = LiveMeetingIndicatorController(store: store, window: window) }
        if !store.showOnboarding { MeetingDetectionService.shared.start(store: store) }
        let pending = pendingDetectionURLs
        pendingDetectionURLs.removeAll()
        for url in pending { handleDetectionURL(url) }
    }
    func application(_ application: NSApplication, open urls: [URL]) {
        for url in urls where DetectionRequest.id(from: url) != nil {
            if store == nil || mainWindow == nil { pendingDetectionURLs.append(url) }
            else { handleDetectionURL(url) }
        }
    }
    private func handleDetectionURL(_ url: URL) {
        guard let store else { return }
        NSApp.unhide(nil)
        NSApp.activate(ignoringOtherApps: true)
        if mainWindow?.isMiniaturized == true { mainWindow?.deminiaturize(nil) }
        mainWindow?.makeKeyAndOrderFront(nil)
        Task { @MainActor in await store.openDetectedTeamsMeeting(url) }
    }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard let store else { return .terminateNow }
        if quitPending { return .terminateLater }
        if store.isMovingLibrary {
            let alert = NSAlert()
            alert.messageText = "Library is being moved"
            alert.informativeText = "Please wait until the move has finished."
            alert.runModal()
            return .terminateCancel
        }
        guard store.busy else {
            guard store.flush() else { showSaveFailure(store); return .terminateCancel }
            store.shutdownTranscription()
            return .terminateNow
        }
        let alert = NSAlert()
        alert.messageText = "Save and end meeting?"
        alert.informativeText = "Notes and the transcript received so far will be saved. Recording and processing will stop; the last segment may be incomplete."
        alert.addButton(withTitle: "Return to meeting")
        alert.addButton(withTitle: "Save and end")
        if alert.runModal() == .alertSecondButtonReturn {
            quitPending = true
            Task { @MainActor in
                let saved = await store.prepareToQuit()
                quitPending = false
                if !saved { showSaveFailure(store) }
                sender.reply(toApplicationShouldTerminate: saved)
            }
            return .terminateLater
        }
        store.openActiveMeeting()
        mainWindow?.makeKeyAndOrderFront(nil)
        return .terminateCancel
    }
    private func showSaveFailure(_ store: MeetingStore) {
        let alert = NSAlert()
        alert.messageText = "Notes could not be saved"
        alert.informativeText = store.errorMessage ?? "Export a copy before closing."
        alert.addButton(withTitle: "Return to meeting")
        alert.runModal()
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        // The floating capsule may count as a visible window when the main window is closed.
        if mainWindow?.isMiniaturized == true { mainWindow?.deminiaturize(nil) }
        mainWindow?.makeKeyAndOrderFront(nil)
        return true
    }
}
