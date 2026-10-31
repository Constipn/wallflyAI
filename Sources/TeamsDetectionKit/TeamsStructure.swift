import AppKit
import ApplicationServices

/// Reduced Accessibility tree shared by detection and speaker attribution.
public struct TeamsAXNode: Equatable {
    public var role: String
    public var identifier: String
    public var key: String?
    public var title: String
    public var description: String
    public var value: String
    public var classes: [String]
    public var children: [TeamsAXNode]
    public var domIdentifier: String
    public var enabled: Bool
    public var hidden: Bool

    public init(role: String = "AXGroup", identifier: String = "", key: String? = nil,
                title: String = "", description: String = "", value: String = "",
                classes: [String] = [], children: [TeamsAXNode] = [], domIdentifier: String = "",
                enabled: Bool = true, hidden: Bool = false) {
        self.role = role; self.identifier = identifier; self.key = key
        self.title = title; self.description = description; self.value = value
        self.classes = classes; self.children = children; self.domIdentifier = domIdentifier
        self.enabled = enabled; self.hidden = hidden
    }
    public var nodes: [TeamsAXNode] { [self] + children.flatMap(\.nodes) }
}

public enum TeamsStructure {
    public static let bundleID = "com.microsoft.teams2"

    public static func excluded(identifier: String, role: String) -> Bool {
        let id = identifier.lowercased()
        return ["AXTextField", "AXTextArea", "AXSearchField"].contains(role) ||
            ["chat-pane", "chatpane", "message-pane", "messagepane", "chat-message", "conversation-message"].contains(where: id.contains)
    }

    /// A stage/tile alone can also occur in a preview. Require an actual leave control.
    public static func hasLeaveControl(_ root: TeamsAXNode) -> Bool {
        guard !root.hidden, !excluded(identifier: root.identifier, role: root.role),
              !excluded(identifier: root.domIdentifier, role: root.role) else { return false }
        let identifiers = [root.identifier, root.domIdentifier].map { $0.lowercased() }
        let markers = ["hangup", "hang-up", "hang_up", "leave-call", "leavecall", "leave-meeting", "leavemeeting"]
        if root.enabled, ["AXButton", "AXMenuButton"].contains(root.role),
           identifiers.contains(where: { id in markers.contains(where: id.contains) }) { return true }
        return root.children.contains(where: hasLeaveControl)
    }
}

public enum TeamsCallSample: Equatable, Sendable {
    case active(pid: Int32)
    case absent(pid: Int32)
    case unknown
    case notRunning
}

public struct TeamsDetectionSnapshot: Sendable {
    public var sample: TeamsCallSample
    public var windowFrame: CGRect?
    public var status: String
    public var diagnostics = TeamsReadDiagnostics()
}

/// AX handles can share a CFHash. Equality must still distinguish their elements.
struct TeamsAXIdentity: Hashable {
    let element: AXUIElement
    static func == (lhs: Self, rhs: Self) -> Bool { CFEqual(lhs.element, rhs.element) }
    func hash(into hasher: inout Hasher) { hasher.combine(CFHash(element)) }
}

struct TeamsAXAttribute {
    var error: AXError = .success
    var value: Any?
}

/// The walker is shared with fixture tests; only the transport touches macOS AX.
struct TeamsAXWalk<Element: Hashable> {
    let attribute: (Element, String) -> TeamsAXAttribute
    let deadline: TimeInterval
    var remaining = 2200
    var visited = Set<Element>()
    var complete = true
    var diagnostic = TeamsReadDiagnostics()
    var captureNodes = false
    var processID: ((Element) -> Int32?)? = nil

    mutating func value<T>(_ element: Element, _ name: String, as type: T.Type, required: Bool = false) -> T? {
        let result = attribute(element, name)
        if result.error == .success, let value = result.value as? T { return value }
        let expectedAbsence = result.error == .noValue || result.error == .attributeUnsupported
        if required || !expectedAbsence {
            complete = false
            let key = "\(name):\(result.error.rawValue)"
            diagnostic.errors[key, default: 0] += 1
        }
        return nil
    }

    mutating func read(_ element: Element, depth: Int = 0, parent: Int? = nil) -> TeamsAXNode? {
        guard remaining > 0, depth < 36, ProcessInfo.processInfo.systemUptime < deadline else {
            complete = false; diagnostic.hitLimit = true; return nil
        }
        guard visited.insert(element).inserted else { return nil }
        remaining -= 1
        diagnostic.nodesRead += 1
        let errorsBefore = diagnostic.errors
        guard let role = value(element, kAXRoleAttribute, as: String.self, required: true) else { return nil }
        guard value(element, "AXHidden", as: Bool.self) != true else { return nil }
        // Never inspect identifiers or children of editable text.
        guard !TeamsStructure.excluded(identifier: "", role: role) else { return nil }
        let axID = value(element, kAXIdentifierAttribute, as: String.self) ?? ""
        let domID = value(element, "AXDOMIdentifier", as: String.self) ?? ""
        guard !TeamsStructure.excluded(identifier: axID, role: role),
              !TeamsStructure.excluded(identifier: domID, role: role) else { return nil }
        var node = TeamsAXNode(role: role, identifier: axID.isEmpty ? domID : axID, domIdentifier: domID)
        node.enabled = value(element, kAXEnabledAttribute, as: Bool.self) ?? false
        guard diagnostic.errors == errorsBefore else { return nil }
        if role == "AXWebArea" { diagnostic.webAreasRead += 1 }
        let index = diagnostic.nodes.count
        if captureNodes && index < 256 {
            diagnostic.nodes.append(.init(parent: parent, role: role,
                identifier: TeamsReadDiagnostics.technicalIdentifier(axID),
                domIdentifier: TeamsReadDiagnostics.technicalIdentifier(domID), enabled: node.enabled,
                pid: processID?(element)))
        } else if captureNodes { diagnostic.traceTruncated = true }
        if TeamsStructure.hasLeaveControl(node) { return node }
        let children = value(element, kAXChildrenAttribute, as: [Element].self) ?? []
        // An empty window/web area is not evidence that a call ended: WebView
        // accessibility may not have exposed the document yet.
        if children.isEmpty && ["AXWindow", "AXWebArea"].contains(role) {
            complete = false; diagnostic.emptyContainers += 1
        }
        for child in children {
            if let result = read(child, depth: depth + 1, parent: index < 256 ? index : nil) {
                node.children.append(result)
                if TeamsStructure.hasLeaveControl(result) { return node }
            }
            if remaining <= 0 || ProcessInfo.processInfo.systemUptime >= deadline {
                complete = false; diagnostic.hitLimit = true; break
            }
        }
        return node
    }
}

/// Caller confines this reader to one utility queue. It reads structure, never text/chat.
public final class TeamsCallReader: @unchecked Sendable {
    private var cursor = 0
    private var preferredWindow: TeamsAXIdentity?
    private var webActivation = TeamsWebActivation()
    public init() { }

    public func sample(captureDiagnostics: Bool = false) -> TeamsDetectionSnapshot {
        var diagnostics = TeamsReadDiagnostics()
        guard AXIsProcessTrusted() else {
            return .init(sample: .unknown, status: "Accessibility access for Wallfly Detector is missing", diagnostics: diagnostics)
        }
        guard let app = NSRunningApplication.runningApplications(withBundleIdentifier: TeamsStructure.bundleID).first else {
            preferredWindow = nil
            return .init(sample: .notRunning, status: "Ready · waiting for Microsoft Teams", diagnostics: diagnostics)
        }
        diagnostics.pid = app.processIdentifier
        let webProcesses = NSRunningApplication.runningApplications(withBundleIdentifier: TeamsWebProcess.bundleID)
            .filter { TeamsWebProcess.belongsToTeams(bundleID: $0.bundleIdentifier, url: $0.bundleURL, owner: app.bundleURL) }
            .sorted { $0.processIdentifier < $1.processIdentifier }
        let targets = [app] + webProcesses
        webActivation.retain(Set(targets.map(\.processIdentifier)))
        var roots: [TeamsAXIdentity] = []
        var seenRoots = Set<TeamsAXIdentity>()
        var complete = targets.count <= 4
        for target in targets.prefix(4) {
            let element = AXUIElementCreateApplication(target.processIdentifier)
            AXUIElementSetMessagingTimeout(element, 0.05)
            let now = ProcessInfo.processInfo.systemUptime
            let activation = webActivation.request(pid: target.processIdentifier, at: now) { [self] in
                // Enable accessibility before traversal.
                // Query the current state first so existing assistive clients are
                // respected, and never turn accessibility off on their behalf.
                if attribute(element, "AXEnhancedUserInterface").value as? Bool == true { return .success }
                return AXUIElementSetAttributeValue(element, "AXEnhancedUserInterface" as CFString, kCFBooleanTrue)
            }
            diagnostics.initializing = diagnostics.initializing || activation.isInitializing(at: now)
            let result = attribute(element, kAXWindowsAttribute)
            let windows = result.error == .success ? result.value as? [AXUIElement] ?? [] : []
            diagnostics.windows += windows.count
            let applicationRoot = windows.isEmpty && target.processIdentifier != app.processIdentifier
            if windows.isEmpty && (!applicationRoot ||
                ![AXError.success, .noValue, .attributeUnsupported].contains(result.error)) {
                diagnostics.errors["\(target.processIdentifier)/AXWindows:\(result.error.rawValue)", default: 0] += 1
                complete = false
            }
            // Embedded Chromium content may be exposed by its own application
            // root instead of windows belonging to the native Teams process.
            let candidates = applicationRoot ? [element] : windows
            diagnostics.processes.append(.init(pid: target.processIdentifier,
                bundleID: target.bundleIdentifier ?? "", activationResult: activation.result.rawValue,
                roots: candidates.count, applicationRoot: applicationRoot))
            for candidate in candidates {
                let identity = TeamsAXIdentity(element: candidate)
                if seenRoots.insert(identity).inserted { roots.append(identity) }
            }
        }
        // A previously enabled web tree can already be readable during the local
        // activation grace period. Positive leave-control evidence is safe now;
        // absenceSample still refuses negative evidence while initializing.
        guard !roots.isEmpty else {
            return .init(sample: .unknown, status: "Teams window currently not readable", diagnostics: diagnostics)
        }
        let deadline = ProcessInfo.processInfo.systemUptime + (captureDiagnostics ? 1 : 0.22)
        complete = complete && roots.count <= 8
        let offset = cursor % roots.count
        var ordered = Array(roots[offset...]) + Array(roots[..<offset])
        if let preferredWindow, let index = ordered.firstIndex(of: preferredWindow) {
            ordered.insert(ordered.remove(at: index), at: 0)
        }
        for window in ordered.prefix(8) {
            // Advance from the actual window, even after preferred-window reordering.
            if let index = roots.firstIndex(of: window) {
                cursor = (index + 1) % roots.count
            }
            var walk = TeamsAXWalk<TeamsAXIdentity>(attribute: { [self] element, name in
                let remainingTime = deadline - ProcessInfo.processInfo.systemUptime
                guard remainingTime > 0 else { return .init(error: .cannotComplete) }
                // AX messaging timeouts are per element. Setting one only on
                // the application does not bound reads of descendant elements.
                AXUIElementSetMessagingTimeout(element.element, Float(min(0.05, remainingTime)))
                var result = attribute(element.element, name)
                if name == kAXChildrenAttribute, let children = result.value as? [AXUIElement] {
                    result.value = children.map { TeamsAXIdentity(element: $0) }
                }
                return result
            }, deadline: deadline, captureNodes: captureDiagnostics, processID: { element in
                var pid: pid_t = 0
                return AXUIElementGetPid(element.element, &pid) == .success ? pid : nil
            })
            if walk.value(window, kAXMinimizedAttribute, as: Bool.self) == true {
                diagnostics.minimizedWindows += 1; complete = false; continue
            }
            let tree = walk.read(window)
            diagnostics.merge(walk.diagnostic)
            if let tree, TeamsStructure.hasLeaveControl(tree) {
                preferredWindow = window
                return .init(sample: .active(pid: app.processIdentifier), windowFrame: frame(window.element),
                             status: "Teams meeting detected", diagnostics: diagnostics)
            }
            complete = complete && walk.complete
            if ProcessInfo.processInfo.systemUptime >= deadline { complete = false; diagnostics.hitLimit = true; break }
        }
        preferredWindow = nil
        let sample = diagnostics.absenceSample(pid: app.processIdentifier, complete: complete)
        let status = sample == .absent(pid: app.processIdentifier) ? "Ready · no Teams meeting running" :
            (diagnostics.webAreasRead == 0 ? "Teams web interface currently not accessible" : "Teams view currently not readable")
        return .init(sample: sample, status: status, diagnostics: diagnostics)
    }

    private func attribute(_ element: AXUIElement, _ name: String) -> TeamsAXAttribute {
        var result: CFTypeRef?
        let error = AXUIElementCopyAttributeValue(element, name as CFString, &result)
        return .init(error: error, value: result)
    }

    private func frame(_ window: AXUIElement) -> CGRect? {
        guard let position = attribute(window, kAXPositionAttribute).value as CFTypeRef?,
              let size = attribute(window, kAXSizeAttribute).value as CFTypeRef?,
              CFGetTypeID(position) == AXValueGetTypeID(), CFGetTypeID(size) == AXValueGetTypeID() else { return nil }
        var point = CGPoint.zero, dimensions = CGSize.zero
        guard AXValueGetValue(position as! AXValue, .cgPoint, &point), AXValueGetValue(size as! AXValue, .cgSize, &dimensions) else { return nil }
        return CGRect(origin: point, size: dimensions)
    }
}
