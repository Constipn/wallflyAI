import AppKit
import ApplicationServices
import TeamsDetectionKit

@MainActor
enum TeamsAccessibility {
    nonisolated static let bundleID = "com.microsoft.teams2"
    private static var requested = false
    static var trusted: Bool { AXIsProcessTrusted() }
    static func request() {
        if !requested && !trusted {
            requested = true
            _ = AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary)
        }
        if !trusted, let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
            NSWorkspace.shared.open(url)
        }
    }
}

typealias TeamsAXNode = TeamsDetectionKit.TeamsAXNode

/// Read-only selector policy; a participant name never constitutes evidence of speech.
enum TeamsAXParser {
    struct Roster {
        var participants: [TeamsObservedParticipant]
        var unidentifiedActive = false
        var localUserSpeaking = false
    }
    static func excluded(identifier: String, role: String) -> Bool {
        TeamsStructure.excluded(identifier: identifier, role: role)
    }

    static func participants(in root: TeamsAXNode) -> [TeamsObservedParticipant]? {
        roster(in: root)?.participants
    }

    static func roster(in root: TeamsAXNode) -> Roster? {
        guard isMeeting(root) else { return nil }
        var result = Roster(participants: [])
        func visit(_ node: TeamsAXNode) {
            guard !excluded(identifier: node.identifier, role: node.role) else { return }
            let id = node.identifier.lowercased()
            let vdiTile = ["AXMenuItem", "AXImage"].contains(node.role) && hasVDIMarker(node)
            if isTile(node) {
                let descendants = safeNodes(node)
                let nameNode = descendants.first { element in
                    ["participant-name", "participantname", "display-name", "displayname", "name-text", "nametext"].contains(where: element.identifier.lowercased().contains)
                }
                let vdiName = descendants.filter { $0.classes.contains("vdi-occlusion") }
                    .flatMap(safeNodes).filter { $0.role == "AXStaticText" }.map(firstText)
                let nameText = ([nameNode.map(firstText)].compactMap { $0 } + vdiName + [labelName(node)])
                    .first { !cleanName($0).isEmpty } ?? ""
                let selfText = [nameText, node.description, node.title].joined(separator: " ").lowercased()
                let isSelf = (vdiTile && node.role == "AXImage") ||
                    ["myself video, ", "mein video, "].contains(where: node.description.lowercased().hasPrefix) ||
                    id.contains("self-preview") || id.contains("self-tile") || id.contains("selfpreview") ||
                    ["(you)", "(sie)", "(ich)", "you are speaking", "sie sprechen", "du sprichst"].contains(where: selfText.contains)
                let name = cleanName(nameText)
                let active = descendants.contains { speaking($0, isTile: $0.identifier == node.identifier && $0.key == node.key) }
                result.localUserSpeaking = result.localUserSpeaking || (isSelf && active)
                result.unidentifiedActive = result.unidentifiedActive || (!isSelf && active && name.isEmpty)
                let stableKey = !node.domIdentifier.isEmpty ? "dom/\(node.domIdentifier)" : (!node.identifier.isEmpty ? "ax/\(node.identifier)" : nil)
                if !name.isEmpty { result.participants.append(TeamsObservedParticipant(key: node.key ?? node.identifier,
                    name: name, active: active, isSelf: isSelf, stableKey: stableKey)) }
                return
            }
            for child in node.children { visit(child) }
        }
        visit(root)
        let grouped = Dictionary(grouping: result.participants, by: \.key)
        let stableCounts = Dictionary(grouping: result.participants.compactMap(\.stableKey), by: { $0 })
        // A repeated DOM/AX identifier is not a stable participant identifier.
        result.participants = result.participants.filter { grouped[$0.key]?.count == 1 }.map {
            var participant = $0
            if let key = participant.stableKey, stableCounts[key]?.count != 1 { participant.stableKey = nil }
            return participant
        }.sorted { $0.key < $1.key }
        if grouped.values.contains(where: { $0.count > 1 && $0.contains(where: \.active) }) { result.unidentifiedActive = true }
        return result
    }
    static func isMeeting(_ root: TeamsAXNode) -> Bool {
        let nodes = safeNodes(root)
        let meetingMarkers = ["hangup", "hang-up", "leave-call", "calling-screen", "meeting-stage", "call-screen", "meeting-window", "callingbuttons"]
        return nodes.contains { node in meetingMarkers.contains(where: node.identifier.lowercased().contains) ||
            node.classes.contains("vdi-occlusion") || node.classes.contains("vdi-frame-occlusion") }
    }
    static func isTile(_ node: TeamsAXNode) -> Bool {
        // Both native VDI classes and AX roles are recognized.
        let vdiTile = ["AXMenuItem", "AXImage"].contains(node.role) && hasVDIMarker(node)
        return vdiTile || ["participant-tile", "participanttile", "video-tile", "videotile", "self-preview"].contains(where: node.identifier.lowercased().contains)
    }
    private static func hasVDIMarker(_ node: TeamsAXNode) -> Bool {
        safeNodes(node).contains { $0.classes.contains("vdi-occlusion") || $0.classes.contains("vdi-frame-occlusion") }
    }

    private static func safeNodes(_ root: TeamsAXNode) -> [TeamsAXNode] {
        guard !excluded(identifier: root.identifier, role: root.role) else { return [] }
        return [root] + root.children.flatMap(safeNodes)
    }
    private static func firstText(_ node: TeamsAXNode) -> String {
        for value in [node.value, node.title, node.description] where !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return value }
        for child in node.children { let text = firstText(child); if !text.isEmpty { return text } }
        return ""
    }
    private static func speaking(_ node: TeamsAXNode, isTile: Bool) -> Bool {
        if node.classes.contains("vdi-frame-occlusion") { return true }
        guard (node.role != "AXButton" || isTile), node.role != "AXMenuItem" else { return false }
        let markers: Set<String> = ["speaking", "is-speaking", "isspeaking", "active-speaker", "speaker-active"]
        if node.classes.contains(where: { markers.contains($0.lowercased()) }) { return true }
        if node.identifier.lowercased().hasSuffix("-speaking-indicator") { return true }
        for raw in [node.description, node.title, node.value] {
            let text = raw.lowercased().trimmingCharacters(in: .whitespacesAndNewlines)
            if ["speaking", "is speaking", "you are speaking", "spricht", "spricht gerade", "sie sprechen", "du sprichst", "aktiver sprecher", "aktive sprecherin"].contains(text) { return true }
            if [" is speaking", ", speaking", " spricht", " spricht gerade", ", aktiver sprecher", ", aktive sprecherin"].contains(where: text.hasSuffix) { return true }
        }
        return false
    }
    private static func labelName(_ node: TeamsAXNode) -> String {
        let text = [node.title, node.description].first { !$0.isEmpty } ?? ""
        if text.hasPrefix("Myself video, ") || text.hasPrefix("Mein Video, ") {
            return text.components(separatedBy: ", ").dropFirst().first ?? ""
        }
        // A tile label is a fallback only; controls such as mute/menu do not supply names.
        return text.components(separatedBy: ",").first ?? ""
    }
    private static func cleanName(_ raw: String) -> String {
        var value = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        for suffix in [" (You)", " (you)", " (Sie)", " (Ich)", " is speaking", " spricht gerade", " spricht"] {
            if value.hasSuffix(suffix) { value = String(value.dropLast(suffix.count)) }
        }
        if ["context menu is available", "has context menu", "kontextmenü ist verfügbar", "hat ein kontextmenü", "myself video", "mein video", "you are speaking", "sie sprechen", "du sprichst", "speaking", "spricht", "aktiver sprecher", "aktive sprecherin"].contains(value.lowercased()) { return "" }
        return String(value.prefix(160)).trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

struct TeamsMeetingScene {
    var surfaceID: String
    var roster: TeamsAXParser.Roster
}

/// Pure scene election, also used by synthetic multi-window fixtures.
struct TeamsSceneSelector {
    private(set) var preferredSurface: String?
    private var recoverySurface: String?

    func windowPriority(_ surface: String, focused: String?) -> Int {
        if surface == recoverySurface { return 0 }
        if surface == preferredSurface { return 1 }
        return surface == focused ? 2 : 3
    }

    mutating func retry(after surface: String?, windows: [String]) {
        guard let surface, let index = windows.firstIndex(of: surface), !windows.isEmpty else { return }
        recoverySurface = windows[(index + 1) % windows.count]
    }

    mutating func select(_ scenes: [TeamsMeetingScene]) -> (TeamsMeetingScene, TeamsSpeakerStatus, TeamsObservationFailure?)? {
        guard var best = scenes.first(where: { $0.surfaceID == preferredSurface }) ??
                scenes.max(by: { $0.roster.participants.count < $1.roster.participants.count }) else { return nil }
        preferredSurface = best.surfaceID
        recoverySurface = nil
        let speakingNames = Set(scenes.flatMap { $0.roster.participants.filter { $0.active && !$0.isSelf }
            .map { $0.name.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() } })
        if speakingNames.count > 1 || scenes.contains(where: { $0.roster.participants.filter(\.active).count > 1 }) {
            return (best, .ambiguous, .conflictingViews)
        }
        if scenes.contains(where: { $0.roster.unidentifiedActive }) { return (best, .ambiguous, .unidentifiedSpeaker) }
        if scenes.contains(where: { $0.roster.localUserSpeaking }) { return (best, .selfSpeaking, .localUserSpeaking) }
        if !best.roster.participants.contains(where: \.active),
           let active = scenes.first(where: { $0.roster.participants.contains(where: \.active) }) {
            best = active; preferredSurface = active.surfaceID
        }
        return (best, best.roster.participants.contains(where: \.active) ? .active : .waitingForSpeaker, nil)
    }
}

/// All AX reads are confined to one background queue with a bounded traversal budget.
/// The monitor never raises windows, clicks controls, sets Teams attributes, or starts a call.
final class TeamsSpeakerWatcher: @unchecked Sendable {
    private let queue = DispatchQueue(label: "meetingnotes.teams-accessibility", qos: .utility)
    private let onObservation: @Sendable (TeamsSpeakerObservation) -> Void
    private var timer: DispatchSourceTimer?
    private var application: AXUIElement?
    private var observer: AXObserver?
    private var pid: pid_t?
    private var epoch = UUID()
    private var selector = TeamsSceneSelector()
    private var knownWindows = Set<String>()
    private var stopped = true
    private var lastSample: TimeInterval = 0
    private var walkIncomplete = false
    private var walkElements: [String: AXUIElement] = [:]

    init(onObservation: @escaping @Sendable (TeamsSpeakerObservation) -> Void) { self.onObservation = onObservation }
    func start() {
        queue.async { [self] in
            guard stopped else { return }
            stopped = false
            let timer = DispatchSource.makeTimerSource(queue: queue)
            timer.schedule(deadline: .now(), repeating: .milliseconds(500))
            timer.setEventHandler { [weak self] in self?.sample() }
            self.timer = timer
            timer.resume()
        }
    }
    func stop() async {
        await withCheckedContinuation { (done: CheckedContinuation<Void, Never>) in
            queue.async { [self] in
                if !stopped { sample() }
                stopped = true; timer?.cancel(); timer = nil
                detachObserver(); application = nil; pid = nil
                done.resume()
            }
        }
    }
    private func requestSample() {
        queue.async { [weak self] in
            guard let self, !self.stopped, ProcessInfo.processInfo.systemUptime - self.lastSample >= 0.15 else { return }
            self.sample()
        }
    }
    private func detachObserver() {
        if let observer { CFRunLoopRemoveSource(CFRunLoopGetMain(), AXObserverGetRunLoopSource(observer), .commonModes) }
        observer = nil
    }
    private func attachObserver(_ app: AXUIElement, pid: pid_t) {
        detachObserver()
        var created: AXObserver?
        let callback: AXObserverCallback = { _, _, _, context in
            guard let context else { return }
            Unmanaged<TeamsSpeakerWatcher>.fromOpaque(context).takeUnretainedValue().requestSample()
        }
        guard AXObserverCreate(pid, callback, &created) == .success, let created else { return }
        observer = created
        let context = Unmanaged.passUnretained(self).toOpaque()
        for name in [kAXWindowCreatedNotification, kAXUIElementDestroyedNotification, kAXLayoutChangedNotification, kAXValueChangedNotification, kAXTitleChangedNotification] {
            _ = AXObserverAddNotification(created, app, name as CFString, context)
        }
        CFRunLoopAddSource(CFRunLoopGetMain(), AXObserverGetRunLoopSource(created), .commonModes)
    }
    private func sample() {
        guard !stopped else { return }
        autoreleasepool {
            lastSample = ProcessInfo.processInfo.systemUptime
            guard AXIsProcessTrusted() else { clear(.needsAccessibility, failure: .accessibility); return }
            guard let running = NSRunningApplication.runningApplications(withBundleIdentifier: "com.microsoft.teams2").first else {
                detachObserver(); application = nil; pid = nil; clear(.teamsNotRunning, failure: .notRunning); return
            }
            if pid != running.processIdentifier {
                pid = running.processIdentifier; epoch = UUID()
                selector = TeamsSceneSelector(); knownWindows.removeAll()
                let app = AXUIElementCreateApplication(running.processIdentifier)
                application = app
                _ = AXUIElementSetMessagingTimeout(app, 0.1)
                attachObserver(app, pid: running.processIdentifier)
            }
            guard let app = application, let windows = attribute(app, kAXWindowsAttribute) as? [AXUIElement] else {
                clear(.unavailable, failure: .unreadable); return
            }
            let began = ProcessInfo.processInfo.systemUptime
            let deadline = began + 0.22
            walkElements.removeAll(keepingCapacity: true)
            defer { walkElements.removeAll(keepingCapacity: true) }
            var candidates: [TeamsMeetingScene] = []
            var truncated = false
            var sawMeeting = false
            let focused = attribute(app, kAXFocusedWindowAttribute).map { String(CFHash($0)) }
            let ranked = windows.enumerated().sorted {
                let left = selector.windowPriority(String(CFHash($0.element)), focused: focused)
                let right = selector.windowPriority(String(CFHash($1.element)), focused: focused)
                return left == right ? $0.offset < $1.offset : left < right
            }.prefix(8).map(\.element)
            let visible = ranked.filter { attribute($0, kAXMinimizedAttribute) as? Bool != true }
            let ordered = visible
            let present = Set(ordered.map { String(CFHash($0)) })
            knownWindows.formIntersection(present)
            let discovering = selector.preferredSurface == nil || !present.contains(selector.preferredSurface ?? "")
            var lastReadSurface: String?
            for window in ordered {
                let surface = String(CFHash(window))
                // Once a healthy meeting is found, unrelated windows are not walked every poll.
                if !discovering && candidates.contains(where: { $0.surfaceID == selector.preferredSurface }) &&
                    !knownWindows.contains(surface) { continue }
                if attribute(window, kAXMinimizedAttribute) as? Bool == true { continue }
                lastReadSurface = surface
                walkIncomplete = false
                var remaining = 2200
                var visited: Set<CFHashCode> = []
                if let structure = read(window, remaining: &remaining, visited: &visited, deadline: deadline, depth: 0),
                   TeamsAXParser.isMeeting(structure),
                   let roster = TeamsAXParser.roster(in: enrich(structure, inTile: false, deadline: deadline)),
                   !walkIncomplete {
                    sawMeeting = true
                    if !roster.participants.isEmpty || roster.unidentifiedActive || roster.localUserSpeaking {
                        knownWindows.insert(surface)
                        candidates.append(TeamsMeetingScene(surfaceID: surface, roster: roster))
                    }
                }
                if walkIncomplete || remaining <= 0 || ProcessInfo.processInfo.systemUptime >= deadline {
                    truncated = true
                    if ProcessInfo.processInfo.systemUptime >= deadline { break }
                }
            }
            guard let (best, status, failure) = selector.select(candidates) else {
                // A large/unreadable chat window must not consume every recovery poll.
                selector.retry(after: lastReadSurface, windows: ordered.map { String(CFHash($0)) })
                clear(truncated || sawMeeting || visible.isEmpty ? .unavailable : .meetingNotFound,
                      failure: truncated ? .truncated : (sawMeeting || visible.isEmpty ? .unreadable : .meetingNotFound), began: began)
                return
            }
            // A failed secondary walk must not discard a complete primary view. Failed
            // preferred views are absent from candidates, so stale activity cannot survive.
            let finished = ProcessInfo.processInfo.systemUptime
            onObservation(TeamsSpeakerObservation(timestamp: (began + finished) / 2,
                surfaceID: best.surfaceID, participants: best.roster.participants, status: status,
                contextID: "\(running.processIdentifier)/\(epoch.uuidString)", readDuration: finished - began, failure: failure))
        }
    }
    private func clear(_ status: TeamsSpeakerStatus, failure: TeamsObservationFailure, began: TimeInterval? = nil) {
        let now = ProcessInfo.processInfo.systemUptime
        onObservation(TeamsSpeakerObservation(timestamp: now, status: status,
            contextID: pid.map { "\($0)/\(epoch.uuidString)" }, readDuration: began.map { now - $0 } ?? 0, failure: failure))
    }
    private func attribute(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
        var result: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, name as CFString, &result) == .success else { return nil }
        return result
    }
    private func read(_ element: AXUIElement, remaining: inout Int, visited: inout Set<CFHashCode>, deadline: TimeInterval,
                      depth: Int) -> TeamsAXNode? {
        guard remaining > 0, depth < 36, ProcessInfo.processInfo.systemUptime < deadline else { walkIncomplete = true; return nil }
        guard visited.insert(CFHash(element)).inserted else { return nil }
        remaining -= 1
        guard let role = attribute(element, kAXRoleAttribute) as? String else { walkIncomplete = true; return nil }
        let axIdentifier = attribute(element, kAXIdentifierAttribute) as? String ?? ""
        let domIdentifier = attribute(element, "AXDOMIdentifier") as? String ?? ""
        let identifier = axIdentifier.isEmpty ? domIdentifier : axIdentifier
        guard !TeamsAXParser.excluded(identifier: identifier, role: role) else { return nil }
        var node = TeamsAXNode(role: role, identifier: identifier)
        node.domIdentifier = domIdentifier
        let key = "\(identifier)/\(domIdentifier)/\(CFHash(element))"
        node.key = key
        walkElements[key] = element
        node.classes = attribute(element, "AXDOMClassList") as? [String] ?? []
        if let children = attribute(element, kAXChildrenAttribute) as? [AXUIElement] {
            for child in children {
                if let child = read(child, remaining: &remaining, visited: &visited, deadline: deadline, depth: depth + 1) { node.children.append(child) }
                if remaining <= 0 || ProcessInfo.processInfo.systemUptime >= deadline { break }
            }
        }
        return node
    }

    /// A second pass reads text only after structure proves it belongs to a meeting tile.
    private func enrich(_ original: TeamsAXNode, inTile: Bool, deadline: TimeInterval) -> TeamsAXNode {
        guard ProcessInfo.processInfo.systemUptime < deadline else { walkIncomplete = true; return original }
        var node = original
        let tile = inTile || TeamsAXParser.isTile(node)
        if tile, let key = node.key, let element = walkElements[key] {
            node.title = attribute(element, kAXTitleAttribute) as? String ?? ""
            node.description = attribute(element, kAXDescriptionAttribute) as? String ?? ""
            if node.role == "AXStaticText" || node.classes.contains("vdi-occlusion") {
                node.value = attribute(element, kAXValueAttribute) as? String ?? ""
            }
        }
        node.children = node.children.map { enrich($0, inTile: tile, deadline: deadline) }
        return node
    }
}
