import SwiftUI
import AppKit

/// Live transcript list that follows new speech only while the reader stays at the end.
struct TranscriptConversation: View {
    @ObservedObject var store: MeetingStore
    let meeting: Meeting
    let live: Bool
    @ViewState private var following = true

    private struct ScrollPosition: Equatable {
        var offset: CGFloat
        var distanceToEnd: CGFloat
    }

    var body: some View {
        let turns = meeting.conversation
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    ForEach(Array(turns.enumerated()), id: \.element.id) { index, turn in
                        let speakerChanged = index == 0 || turns[index - 1].speaker != turn.speaker || turns[index - 1].participantID != turn.participantID
                        TranscriptTurnView(store: store, meeting: meeting, turn: turn, showsSpeaker: speakerChanged)
                            .padding(.top, index == 0 ? 0 : speakerChanged ? 14 : 4)
                            .background(isRequested(turn) ? Palette.gold100.opacity(0.5) : .clear, in: RoundedRectangle(cornerRadius: 10))
                            .id(turn.id)
                    }
                    Color.clear.frame(height: 1).id("transcript-end")
                }.padding(.horizontal, 23).padding(.vertical, 18)
            }
            .defaultScrollAnchor(live ? .bottom : nil, for: .initialOffset)
            .onScrollGeometryChange(for: ScrollPosition.self) { geometry in
                ScrollPosition(offset: geometry.contentOffset.y, distanceToEnd: geometry.contentSize.height - geometry.visibleRect.maxY)
            } action: { old, new in
                if new.offset < old.offset - 1 && new.distanceToEnd > 2 { following = false } else if new.distanceToEnd <= 40 { following = true }
            }
            .onAppear {
                if let target = turns.first(where: isRequested) {
                    Task { @MainActor in await Task.yield(); proxy.scrollTo(target.id, anchor: .center) }
                }
                else if live { proxy.scrollTo("transcript-end", anchor: .bottom) }
            }
            .onChange(of: store.requestedPersonSource) { _, _ in
                if let target = turns.first(where: isRequested) {
                    Task { @MainActor in await Task.yield(); proxy.scrollTo(target.id, anchor: .center) }
                }
            }
            .onChange(of: meeting.transcript) { _, _ in
                guard following, store.isRecording, store.activeID == meeting.id else { return }
                withAnimation(.easeOut(duration: 0.2)) { proxy.scrollTo("transcript-end", anchor: .bottom) }
            }
            .overlay(alignment: .bottom) {
                if live && !following {
                    Button {
                        following = true
                        withAnimation(.easeInOut(duration: 0.18)) { proxy.scrollTo("transcript-end", anchor: .bottom) }
                    } label: {
                        Label("Latest", systemImage: "arrow.down")
                            .font(.system(size: 12, weight: .semibold)).foregroundStyle(Palette.ink)
                            .padding(.horizontal, 12).padding(.vertical, 6)
                            .background(Palette.paper, in: Capsule())
                            .overlay(Capsule().stroke(Palette.line))
                            .shadow(color: .black.opacity(0.08), radius: 6, y: 2)
                            .contentShape(Capsule())
                    }
                    .buttonStyle(.plain).padding(.bottom, 12)
                    .help("Jump to the latest messages")
                    .accessibilityIdentifier("transcript-latest")
                    .transition(.opacity.combined(with: .move(edge: .bottom)))
                }
            }
            .animation(.easeInOut(duration: 0.18), value: live && !following)
        }
    }
    private func isRequested(_ turn: TranscriptTurn) -> Bool {
        guard let source = store.requestedPersonSource else { return false }
        return turn.references.contains { reference in
            guard reference.segmentID == source.segmentID else { return false }
            guard let range = reference.wordRange, let start = source.wordStart, let end = source.wordEnd else { return true }
            return range.overlaps(start..<end)
        }
    }
}

/// Places one bubble at the leading or trailing edge, limited to a fraction of the row width.
struct ChatBubbleRow: Layout {
    var trailing: Bool
    var fraction: CGFloat = 0.75

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        guard let bubble = subviews.first else { return .zero }
        let size = bubble.sizeThatFits(ProposedViewSize(width: proposal.width.map { $0 * fraction }, height: nil))
        return CGSize(width: proposal.width ?? size.width, height: size.height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        guard let bubble = subviews.first else { return }
        let size = bubble.sizeThatFits(ProposedViewSize(width: bounds.width * fraction, height: nil))
        bubble.place(at: CGPoint(x: trailing ? bounds.maxX - size.width : bounds.minX, y: bounds.minY),
                     proposal: ProposedViewSize(size))
    }
}

/// Closes a panel on clicks elsewhere in its window without consuming the click.
@MainActor
final class OutsideClickMonitor {
    var onOutsideClick: () -> Void = {}
    fileprivate weak var panel: NSView?
    fileprivate weak var exempt: NSView?
    nonisolated(unsafe) private var monitor: Any?

    deinit { if let monitor { NSEvent.removeMonitor(monitor) } }

    fileprivate func panelMoved(_ view: NSView) {
        if view.window != nil {
            panel = view
            guard monitor == nil else { return }
            monitor = NSEvent.addLocalMonitorForEvents(matching: .leftMouseDown) { [weak self] event in
                MainActor.assumeIsolated { self?.handle(event) }
                return event
            }
        } else if panel == nil || panel === view {
            stop()
        }
    }

    func stop() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
    }

    private func handle(_ event: NSEvent) {
        guard let panel, let window = panel.window else { stop(); return }
        guard event.window === window, !contains(panel, event), !(exempt.map { contains($0, event) } ?? false) else { return }
        onOutsideClick()
    }

    private func contains(_ view: NSView, _ event: NSEvent) -> Bool {
        view.window === event.window && view.bounds.contains(view.convert(event.locationInWindow, from: nil))
    }
}

private final class ClickRegionView: NSView {
    var moved: (NSView) -> Void = { _ in }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        moved(self)
    }
}

private struct ClickRegion: NSViewRepresentable {
    let monitor: OutsideClickMonitor
    let isPanel: Bool
    var onOutsideClick: (() -> Void)?

    func makeNSView(context: Context) -> ClickRegionView {
        let view = ClickRegionView()
        let monitor = monitor
        view.moved = isPanel ? { monitor.panelMoved($0) } : { monitor.exempt = $0 }
        return view
    }

    func updateNSView(_ view: ClickRegionView, context: Context) {
        if let onOutsideClick { monitor.onOutsideClick = onOutsideClick }
    }

    static func dismantleNSView(_ view: ClickRegionView, coordinator: ()) {
        view.moved = { _ in }
    }
}

extension View {
    /// Reports clicks in the same window outside this view and any exempt view.
    func closesOnOutsideClick(_ monitor: OutsideClickMonitor, perform action: @escaping () -> Void) -> some View {
        background(ClickRegion(monitor: monitor, isPanel: true, onOutsideClick: action))
    }

    /// Clicks on this view never count as outside clicks for the monitor.
    func exemptFromOutsideClick(_ monitor: OutsideClickMonitor) -> some View {
        background(ClickRegion(monitor: monitor, isPanel: false))
    }
}
