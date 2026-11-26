import AppKit
import Darwin
import Foundation

/// A borrowed endpoint carries no authority to stop the process that owns it.
struct OpenWhisprEndpoint: Equatable {
    let pid: pid_t
    let baseURL: URL

    static func verified(pid: pid_t, executable: String, arguments: [String],
                         installation: LocalWhisperInstallation) throws -> Self {
        func canonical(_ path: String) -> String {
            URL(fileURLWithPath: path).standardizedFileURL.resolvingSymlinksInPath().path
        }
        func option(_ name: String) -> String? {
            guard let index = arguments.firstIndex(of: name), index + 1 < arguments.count else { return nil }
            return arguments[index + 1]
        }
        guard pid > 0, canonical(executable) == canonical(installation.executableURL.path),
              let model = option("--model"), canonical(model) == canonical(installation.modelURL.path),
              option("--host") == "127.0.0.1", let rawPort = option("--port"),
              let port = UInt16(rawPort), port > 0 else {
            throw MeetingError.message("OpenWhispr is running but is not using the selected local Turbo model. Select it in OpenWhispr before recording. No second model was started.")
        }
        let prefix = option("--request-path") ?? ""
        guard prefix.isEmpty || (prefix.hasPrefix("/") && !prefix.contains("?") && !prefix.contains("#") && !prefix.contains("..")),
              let url = URL(string: "http://127.0.0.1:\(port)\(prefix.hasSuffix("/") ? prefix : prefix + "/")"),
              url.host == "127.0.0.1", url.port == Int(port) else {
            throw MeetingError.message("Could not verify OpenWhispr’s local server address. No second model was started.")
        }
        return Self(pid: pid, baseURL: url)
    }
}

protocol OwnedWhisperProcess: AnyObject {
    var isRunning: Bool { get }
    var terminationStatus: Int32 { get }
    func terminate()
}

extension Process: OwnedWhisperProcess {}

@MainActor
protocol OpenWhisprRuntime {
    func appIsRunning(_ installation: LocalWhisperInstallation) -> Bool
    func runningServer(_ installation: LocalWhisperInstallation) throws -> OpenWhisprEndpoint?
    func availablePort() throws -> UInt16
    func launch(_ installation: LocalWhisperInstallation, port: UInt16, prefix: String,
                wordTimestamps: Bool) throws -> any OwnedWhisperProcess
}

@MainActor
struct SystemOpenWhisprRuntime: OpenWhisprRuntime {
    func appIsRunning(_ installation: LocalWhisperInstallation) -> Bool {
        let appURL = URL(fileURLWithPath: (installation.appPath as NSString).expandingTildeInPath)
            .standardizedFileURL.resolvingSymlinksInPath()
        let bundleID = Bundle(url: appURL)?.bundleIdentifier
        return NSWorkspace.shared.runningApplications.contains {
            (bundleID != nil && $0.bundleIdentifier == bundleID) ||
                $0.bundleURL?.standardizedFileURL.resolvingSymlinksInPath() == appURL
        }
    }

    func runningServer(_ installation: LocalWhisperInstallation) throws -> OpenWhisprEndpoint? {
        let pidFile = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/open-whispr/sidecar-pids/whisper.pid")
        guard let raw = try? String(contentsOf: pidFile, encoding: .utf8),
              let pid = pid_t(raw.trimmingCharacters(in: .whitespacesAndNewlines)), pid > 0 else { return nil }
        if kill(pid, 0) != 0 {
            if errno == ESRCH { return nil }
            throw MeetingError.message("macOS prevented checking the running OpenWhispr model. No second model was started.")
        }
        // PROC_PIDPATHINFO_MAXSIZE is a C expression macro unavailable to Swift.
        var path = [CChar](repeating: 0, count: 4 * Int(MAXPATHLEN))
        guard proc_pidpath(pid, &path, UInt32(path.count)) > 0 else {
            throw MeetingError.message("Could not verify OpenWhispr’s server process. No second model was started.")
        }
        let executable = String(cString: path)
        var mib: [Int32] = [CTL_KERN, KERN_PROCARGS2, pid]
        var size = 0
        guard sysctl(&mib, UInt32(mib.count), nil, &size, nil, 0) == 0, size > MemoryLayout<Int32>.size else {
            throw MeetingError.message("Could not verify the loaded OpenWhispr model. No second model was started.")
        }
        var data = [UInt8](repeating: 0, count: size)
        let result = data.withUnsafeMutableBytes { sysctl(&mib, UInt32(mib.count), $0.baseAddress, &size, nil, 0) }
        guard result == 0 else {
            throw MeetingError.message("Could not verify the loaded OpenWhispr model. No second model was started.")
        }
        let arguments = try Self.arguments(from: Array(data.prefix(size)))
        return try OpenWhisprEndpoint.verified(pid: pid, executable: executable, arguments: arguments, installation: installation)
    }

    /// KERN_PROCARGS2: argc, executable path, padding, then exactly argc strings.
    /// Environment entries following argv are never interpreted as arguments.
    static func arguments(from bytes: [UInt8]) throws -> [String] {
        guard bytes.count > 4 else { throw MeetingError.message("Invalid OpenWhispr process information.") }
        let count = bytes.withUnsafeBytes { $0.loadUnaligned(as: Int32.self) }
        guard count > 0, count < 4096, let executableEnd = bytes[4...].firstIndex(of: 0) else {
            throw MeetingError.message("Invalid OpenWhispr process arguments.")
        }
        var index = executableEnd + 1
        while index < bytes.count && bytes[index] == 0 { index += 1 }
        var arguments: [String] = []
        for _ in 0..<count {
            guard index < bytes.count, let end = bytes[index...].firstIndex(of: 0),
                  let argument = String(bytes: bytes[index..<end], encoding: .utf8) else {
                throw MeetingError.message("Incomplete OpenWhispr process arguments.")
            }
            arguments.append(argument)
            index = end + 1
        }
        return arguments
    }

    func availablePort() throws -> UInt16 { try LocalWhisperServer.availableLoopbackPort() }

    func launch(_ installation: LocalWhisperInstallation, port: UInt16, prefix: String,
                wordTimestamps: Bool) throws -> any OwnedWhisperProcess {
        let process = Process()
        process.executableURL = installation.executableURL
        process.arguments = installation.arguments(port: port, prefix: prefix, wordTimestamps: wordTimestamps)
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        process.environment = ["PATH": "/usr/bin:/bin:/usr/sbin:/sbin", "HOME": NSHomeDirectory(),
                               "TMPDIR": NSTemporaryDirectory()]
        try process.run()
        return process
    }
}
