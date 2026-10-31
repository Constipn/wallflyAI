import Foundation

/// Persist only the announced session, never AX data or participant information.
public struct DetectionPolicy: Codable, Equatable {
    public private(set) var pid: Int32?
    public private(set) var eventID: UUID?
    private var confirmations = 0
    private var absentSince: TimeInterval?

    public init() { }

    public mutating func observe(_ sample: TeamsCallSample, at time: TimeInterval) -> UUID? {
        switch sample {
        case .notRunning:
            self = .init()
        case .unknown:
            confirmations = 0
            absentSince = nil
        case .absent(let process):
            if pid != process { self = .init(); pid = process }
            confirmations = 0
            if absentSince == nil { absentSince = time }
            if time - (absentSince ?? time) >= 10 { eventID = nil }
        case .active(let process):
            if pid != process { self = .init(); pid = process }
            absentSince = nil
            confirmations = min(2, confirmations + 1)
            if confirmations >= 2 && eventID == nil {
                let id = UUID(); eventID = id
                return id
            }
        }
        return nil
    }

    /// A restarted helper must collect fresh confirmations/end evidence.
    public mutating func resume() { confirmations = 0; absentSince = nil }
}

public struct DetectionRequest: Codable, Equatable {
    public var id: UUID
    public var createdAt: Date
    public init(id: UUID, createdAt: Date = Date()) { self.id = id; self.createdAt = createdAt }
    public var url: URL { URL(string: "meetingnotes://detected-teams/\(id.uuidString)")! }

    public static func id(from url: URL) -> UUID? {
        guard url.scheme == "meetingnotes", url.host == "detected-teams", url.query == nil, url.fragment == nil,
              url.pathComponents.count == 2 else { return nil }
        return UUID(uuidString: url.lastPathComponent)
    }
}

public struct DetectionConfiguration: Codable {
    public var enabled = true
    public init(enabled: Bool = true) { self.enabled = enabled }
}

public struct DetectionHeartbeat: Codable {
    public var date: Date
    public var busy: Bool
    public var presenting: Bool
    public init(date: Date = Date(), busy: Bool, presenting: Bool) {
        self.date = date; self.busy = busy; self.presenting = presenting
    }
    public func suppressesPopup(at now: Date) -> Bool {
        now.timeIntervalSince(date) >= 0 && now.timeIntervalSince(date) < 8 && (busy || presenting)
    }
}

public struct DetectionStatus: Codable {
    public var date: Date
    public var message: String
    public var accessibilityTrusted: Bool
    public var helperVersion: String?
    public init(message: String, accessibilityTrusted: Bool, date: Date = Date(), helperVersion: String? = nil) {
        self.date = date; self.message = message; self.accessibilityTrusted = accessibilityTrusted
        self.helperVersion = helperVersion
    }
}

/// Separate subdirectory: the existing meeting loader treats root-level JSON as meetings.
public struct DetectionStorage {
    public let root: URL
    public init(root: URL? = nil) {
        self.root = root ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("MeetingNotes/Detection", isDirectory: true)
    }
    public func read<T: Decodable>(_ name: String, as type: T.Type) -> T? {
        guard let data = try? Data(contentsOf: root.appendingPathComponent(name)) else { return nil }
        return try? JSONDecoder().decode(type, from: data)
    }
    public func write<T: Encodable>(_ value: T, to name: String) throws {
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let path = root.appendingPathComponent(name)
        try JSONEncoder().encode(value).write(to: path, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: path.path)
    }
    public func remove(_ name: String) throws {
        let path = root.appendingPathComponent(name)
        if FileManager.default.fileExists(atPath: path.path) { try FileManager.default.removeItem(at: path) }
    }

    public func takeDiagnosticRequest(at now: Date = Date()) throws -> DetectionDiagnosticRequest? {
        let request = read("diagnostic-request.json", as: DetectionDiagnosticRequest.self)
        try remove("diagnostic-request.json")
        return request?.isFresh(at: now) == true ? request : nil
    }

    /// Only a recent request written by the local helper can start a recording.
    public func consume(_ url: URL, at now: Date = Date()) throws -> UUID? {
        guard let id = DetectionRequest.id(from: url),
              let request = read("request.json", as: DetectionRequest.self), request.id == id,
              now.timeIntervalSince(request.createdAt) >= 0, now.timeIntervalSince(request.createdAt) < 120 else { return nil }
        var consumed = read("consumed.json", as: [UUID].self) ?? []
        guard !consumed.contains(id) else { return nil }
        consumed.append(id)
        try write(Array(consumed.suffix(64)), to: "consumed.json")
        try remove("request.json")
        return id
    }
}
