import Foundation

/// Structural metadata only. Never query AXTitle, AXDescription or AXValue here.
public struct TeamsReadDiagnostics: Codable, Equatable, Sendable {
    public struct Node: Codable, Equatable, Sendable {
        public var parent: Int?
        public var role: String
        public var identifier: String
        public var domIdentifier: String
        public var enabled: Bool
        public var pid: Int32? = nil
    }

    public struct Process: Codable, Equatable, Sendable {
        public var pid: Int32
        public var bundleID: String
        public var activationResult: Int32
        public var roots: Int
        public var applicationRoot: Bool
    }

    public var pid: Int32?
    public var windows = 0
    public var minimizedWindows = 0
    public var nodesRead = 0
    public var webAreasRead = 0
    public var initializing = false
    public var emptyContainers = 0
    public var hitLimit = false
    public var traceTruncated = false
    public var errors: [String: Int] = [:]
    public var nodes: [Node] = []
    public var processes: [Process] = []
    public init() { }

    // Reports from earlier helper builds remain readable after an upgrade.
    private enum CodingKeys: String, CodingKey {
        case pid, windows, minimizedWindows, nodesRead, emptyContainers, hitLimit, traceTruncated, errors, nodes
        case webAreasRead, initializing, processes
    }
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        pid = try c.decodeIfPresent(Int32.self, forKey: .pid)
        windows = try c.decodeIfPresent(Int.self, forKey: .windows) ?? 0
        minimizedWindows = try c.decodeIfPresent(Int.self, forKey: .minimizedWindows) ?? 0
        nodesRead = try c.decodeIfPresent(Int.self, forKey: .nodesRead) ?? 0
        webAreasRead = try c.decodeIfPresent(Int.self, forKey: .webAreasRead) ?? 0
        initializing = try c.decodeIfPresent(Bool.self, forKey: .initializing) ?? false
        emptyContainers = try c.decodeIfPresent(Int.self, forKey: .emptyContainers) ?? 0
        hitLimit = try c.decodeIfPresent(Bool.self, forKey: .hitLimit) ?? false
        traceTruncated = try c.decodeIfPresent(Bool.self, forKey: .traceTruncated) ?? false
        errors = try c.decodeIfPresent([String: Int].self, forKey: .errors) ?? [:]
        nodes = try c.decodeIfPresent([Node].self, forKey: .nodes) ?? []
        processes = try c.decodeIfPresent([Process].self, forKey: .processes) ?? []
    }

    func absenceSample(pid: Int32, complete: Bool) -> TeamsCallSample {
        // Readable native window chrome alone says nothing about the web call.
        complete && !initializing && webAreasRead > 0 ? .absent(pid: pid) : .unknown
    }

    mutating func merge(_ other: Self) {
        nodesRead += other.nodesRead
        webAreasRead += other.webAreasRead
        emptyContainers += other.emptyContainers
        hitLimit = hitLimit || other.hitLimit
        traceTruncated = traceTruncated || other.traceTruncated || nodes.count + other.nodes.count > 256
        for (key, count) in other.errors { errors[key, default: 0] += count }
        let offset = nodes.count
        nodes += other.nodes.prefix(max(0, 256 - offset)).map {
            var node = $0
            node.parent = node.parent.map { $0 + offset }
            return node
        }
    }

    static func technicalIdentifier(_ value: String) -> String {
        guard !value.isEmpty else { return "" }
        // Omit dynamic participant/message IDs and strings resembling user text.
        let lower = value.lowercased()
        guard value.count <= 120,
              value.range(of: "^[A-Za-z][A-Za-z0-9_-]*$", options: .regularExpression) != nil,
              !["participant", "displayname", "display-name", "message", "roster", "contact", "member"]
                .contains(where: lower.contains) else { return "<redacted>" }
        return value
    }
}

public struct DetectionDiagnosticRequest: Codable, Equatable, Sendable {
    public var id: UUID
    public var date: Date
    public init(id: UUID = UUID(), date: Date = Date()) { self.id = id; self.date = date }
    public func isFresh(at now: Date) -> Bool { (0..<120).contains(now.timeIntervalSince(date)) }
}

public struct DetectionDiagnosticReport: Codable, Sendable {
    public var requestID: UUID
    public var date: Date
    public var helperVersion: String
    public var accessibilityTrusted: Bool
    public var outcome: String
    public var status: String
    public var structure: TeamsReadDiagnostics

    public func shouldRefresh(helperVersion: String, at now: Date = Date()) -> Bool {
        self.helperVersion != helperVersion && outcome != "active" && (0..<600).contains(now.timeIntervalSince(date))
    }

    public init(request: DetectionDiagnosticRequest, snapshot: TeamsDetectionSnapshot, trusted: Bool,
                helperVersion: String) {
        requestID = request.id; date = Date(); self.helperVersion = helperVersion
        accessibilityTrusted = trusted; status = snapshot.status; structure = snapshot.diagnostics
        switch snapshot.sample {
        case .active: outcome = "active"
        case .absent: outcome = "absent"
        case .unknown: outcome = "unknown"
        case .notRunning: outcome = "notRunning"
        }
    }
}
