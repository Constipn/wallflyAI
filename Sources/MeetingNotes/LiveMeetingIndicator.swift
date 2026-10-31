import AppKit
import SwiftUI
import Combine

struct MeetingWindowAccessor: NSViewRepresentable {
    let attach: (NSWindow) -> Void
    func makeNSView(context: Context) -> WindowProbe { WindowProbe(attach: attach) }
    func updateNSView(_ nsView: WindowProbe, context: Context) { }

    final class WindowProbe: NSView {
        let attach: (NSWindow) -> Void
        init(attach: @escaping (NSWindow) -> Void) { self.attach = attach; super.init(frame: .zero) }
        required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            if let window { DispatchQueue.main.async { self.attach(window) } }
        }
    }
}

/// Pure visibility policy, shared by the window controller and its tests.
enum LiveIndicatorPolicy {
    static func visible(recording: Bool, presenting: Bool, appActive: Bool, windowVisible: Bool, minimized: Bool) -> Bool {
        recording && !presenting && (!appActive || !windowVisible || minimized)
    }
}

@MainActor
final class LiveMeetingIndicatorController: NSObject, NSMenuDelegate {
    private let store: MeetingStore
    private weak var mainWindow: NSWindow?
    private let panel: AudioIndicatorPanel
    private let statusItem: NSStatusItem
    private let presentationItem = NSMenuItem(title: "Presentation mode", action: #selector(togglePresentation), keyEquivalent: "")
    private var subscriptions = Set<AnyCancellable>()
    private var anchorScreen: NSScreen?

    init(store: MeetingStore, window: NSWindow) {
        self.store = store
        mainWindow = window
        anchorScreen = window.screen
        panel = AudioIndicatorPanel(contentRect: NSRect(origin: .zero, size: LiveMeetingCapsule.size),
            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        super.init()
        panel.isReleasedWhenClosed = false
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = true
        panel.hidesOnDeactivate = false
        panel.canHide = false
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.contentView = NSHostingView(rootView: LiveMeetingCapsule(activity: store.recordingActivity) { [weak self] in self?.openMeeting() })
        statusItem.button?.image = NSImage(systemSymbolName: "waveform.circle", accessibilityDescription: "Recording in progress")
        statusItem.button?.toolTip = "Recording in progress"
        let menu = NSMenu()
        menu.delegate = self
        let open = NSMenuItem(title: "Open current meeting", action: #selector(openMeeting), keyEquivalent: "")
        open.target = self
        presentationItem.target = self
        menu.addItem(open)
        menu.addItem(presentationItem)
        statusItem.menu = menu

        Publishers.CombineLatest4(store.$isStarting, store.$isRecording, store.$isStopping, store.$presentationMode)
            .removeDuplicates { previous, current in
                previous.0 == current.0 && previous.1 == current.1 && previous.2 == current.2 && previous.3 == current.3
            }.sink { [weak self] _ in
            Task { @MainActor in self?.refresh() }
        }.store(in: &subscriptions)
        let center = NotificationCenter.default
        for name in [NSApplication.didBecomeActiveNotification, NSApplication.didResignActiveNotification,
                     NSApplication.didHideNotification, NSApplication.didUnhideNotification,
                     NSApplication.didChangeScreenParametersNotification] {
            center.publisher(for: name).sink { [weak self] _ in
                Task { @MainActor in self?.refresh() }
            }.store(in: &subscriptions)
        }
        for name in [NSWindow.didMiniaturizeNotification, NSWindow.didDeminiaturizeNotification,
                     NSWindow.willCloseNotification, NSWindow.didChangeScreenNotification,
                     NSWindow.didBecomeKeyNotification, NSWindow.didResignKeyNotification] {
            center.publisher(for: name, object: window).sink { [weak self] _ in
                Task { @MainActor in self?.refresh() }
            }.store(in: &subscriptions)
        }
        refresh()
    }

    func menuWillOpen(_ menu: NSMenu) { presentationItem.state = store.presentationMode ? .on : .off }

    @objc private func togglePresentation() { store.presentationMode.toggle(); refresh() }

    @objc private func openMeeting() {
        store.openActiveMeeting()
        NSApp.unhide(nil)
        NSApp.activate(ignoringOtherApps: true)
        if mainWindow?.isMiniaturized == true { mainWindow?.deminiaturize(nil) }
        mainWindow?.makeKeyAndOrderFront(nil)
        panel.orderOut(nil)
    }

    private func refresh() {
        statusItem.isVisible = store.isStarting || store.isRecording || store.isStopping
        if let screen = mainWindow?.screen { anchorScreen = screen }
        let show = LiveIndicatorPolicy.visible(recording: store.isRecording && !store.isStopping,
            presenting: store.presentationMode, appActive: NSApp.isActive,
            windowVisible: mainWindow?.isVisible == true, minimized: mainWindow?.isMiniaturized == true)
        guard show else { if panel.isVisible { panel.orderOut(nil) }; return }
        let screen = NSScreen.screens.first(where: { $0 == anchorScreen }) ?? NSScreen.main ?? NSScreen.screens.first
        if let frame = screen?.visibleFrame {
            panel.setFrameOrigin(NSPoint(x: frame.maxX - panel.frame.width - 24, y: frame.midY - panel.frame.height / 2))
        }
        if !panel.isVisible { panel.orderFrontRegardless() }
    }
}

private final class AudioIndicatorPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

struct LiveMeetingCapsule: View {
    static let size = CGSize(width: 34, height: 101)

    @ObservedObject var activity: RecordingActivity
    let open: () -> Void
    var body: some View {
        Button(action: open) {
            VStack(spacing: 7) {
                Image(systemName: "waveform").font(.system(size: 9.8, weight: .bold))
                    .frame(width: 18.2, height: 18.2)
                    .overlay(Circle().stroke(Palette.accent, lineWidth: 1.4))
                VStack(spacing: 2.1) {
                    ForEach(0..<11) { index in
                        Capsule().frame(width: barWidth(index), height: 2.1)
                    }
                }.frame(width: 14)
            }.foregroundStyle(Palette.accent).padding(.vertical, 8.4)
                .frame(width: Self.size.width - 1.4, height: Self.size.height - 1.4)
                .background(Palette.paper, in: Capsule())
                .overlay(Capsule().stroke(Palette.hairline, lineWidth: 0.525))
                .contentShape(Capsule())
        }.buttonStyle(.plain)
            .frame(width: Self.size.width, height: Self.size.height)
            .help("Open notes for the current recording").accessibilityLabel("Open notes for the current recording")
            .preferredColorScheme(.light)
    }
    private func barWidth(_ index: Int) -> CGFloat {
        let level = max(activity.meLevel, activity.othersLevel)
        let shape = 1 - Double(index) / 16
        return 4.2 + CGFloat(min(1, max(0, level)) * 9.1 * shape)
    }
}
