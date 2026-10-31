import Foundation
import Darwin

struct LocalWhisperInstallation: Equatable {
    var appPath: String
    var modelPath: String
    var executableURL: URL {
        URL(fileURLWithPath: (appPath as NSString).expandingTildeInPath)
            .appendingPathComponent("Contents/Resources/bin/whisper-server-darwin-arm64")
    }
    var modelURL: URL { URL(fileURLWithPath: (modelPath as NSString).expandingTildeInPath) }
    static var defaultModelPath: String { "~/.cache/openwhispr/whisper-models/ggml-large-v3-turbo.bin" }
    var isAvailable: Bool {
        FileManager.default.isExecutableFile(atPath: executableURL.path) &&
            FileManager.default.fileExists(atPath: modelURL.path)
    }
    func validate() throws {
        guard FileManager.default.isExecutableFile(atPath: executableURL.path) else {
            throw MeetingError.message("The local Whisper server was not found. Choose your OpenWhispr.app in Settings.")
        }
        guard FileManager.default.fileExists(atPath: modelURL.path) else {
            throw MeetingError.message("The local Whisper Turbo model was not found. Choose the downloaded file ggml-large-v3-turbo.bin in Settings.")
        }
    }
    func arguments(port: UInt16, prefix: String, wordTimestamps: Bool = false) -> [String] {
        var arguments = ["--model", modelURL.path, "--host", "127.0.0.1", "--port", String(port),
         "--language", "auto", "--threads", String(min(8, max(2, ProcessInfo.processInfo.activeProcessorCount / 2))),
         "--best-of", "1", "--no-fallback", "--max-len", "4096", "--request-path", prefix]
        let vad = URL(fileURLWithPath: (appPath as NSString).expandingTildeInPath)
            .appendingPathComponent("Contents/Resources/bin/whisper-vad/ggml-silero-v5.1.2.bin")
        // VAD can compress silence and shift token timestamps in bundled whisper.cpp builds.
        // Timed Teams mode uses the original chunk timeline; RMS chunking still skips silence.
        if !wordTimestamps, FileManager.default.fileExists(atPath: vad.path) { arguments += ["--vad", "--vad-model", vad.path] }
        return arguments
    }
}

/// Shares OpenWhispr's verified server when open, otherwise owns a background helper.
/// Only a helper launched here may be terminated here; audio always stays on loopback.
@MainActor
final class LocalWhisperServer {
    private var process: (any OwnedWhisperProcess)?
    private var borrowedEndpoint: OpenWhisprEndpoint?
    private let runtime: any OpenWhisprRuntime
    private let preparationTimeout: TimeInterval
    private var installation: LocalWhisperInstallation?
    private var inferenceURL: URL?
    private var healthURL: URL?
    private let session: URLSession
    private var testTransport = false
    private var timedMode = false

    var isPrepared: Bool { inferenceURL != nil && (testTransport || borrowedEndpoint != nil || process?.isRunning == true) }

    init(sessionConfiguration: URLSessionConfiguration = .ephemeral, testEndpoint: URL? = nil,
         runtime: (any OpenWhisprRuntime)? = nil, preparationTimeout: TimeInterval = 60) {
        self.runtime = runtime ?? SystemOpenWhisprRuntime()
        self.preparationTimeout = preparationTimeout
        sessionConfiguration.timeoutIntervalForRequest = 30
        sessionConfiguration.timeoutIntervalForResource = 45
        sessionConfiguration.httpShouldSetCookies = false
        sessionConfiguration.urlCache = nil
        session = URLSession(configuration: sessionConfiguration, delegate: LocalWhisperNoRedirects(), delegateQueue: nil)
        inferenceURL = testEndpoint
        testTransport = testEndpoint != nil
    }

    func prepare(_ installation: LocalWhisperInstallation, wordTimestamps: Bool = false) async throws {
        if self.installation == installation, timedMode == wordTimestamps, isPrepared { return }
        stop()
        timedMode = wordTimestamps
        try Task.checkCancellation()
        if runtime.appIsRunning(installation) {
            try await prepareBorrowed(installation)
            return
        }
        try installation.validate()
        let port = try runtime.availablePort()
        let prefix = "/meetingnotes-" + UUID().uuidString.lowercased()
        let base = URL(string: "http://127.0.0.1:\(port)\(prefix)/")!
        // Recheck immediately before launch in case OpenWhispr opened during preparation.
        if runtime.appIsRunning(installation) {
            try await prepareBorrowed(installation)
            return
        }
        let process = try runtime.launch(installation, port: port, prefix: prefix, wordTimestamps: wordTimestamps)
        self.process = process
        self.installation = installation
        healthURL = base
        do {
            let deadline = Date().addingTimeInterval(preparationTimeout)
            while Date() < deadline {
                try Task.checkCancellation()
                guard process.isRunning else {
                    throw MeetingError.message("The Whisper server exited while loading Turbo (status \(process.terminationStatus)). No audio was recorded. Check the app and model paths and available memory.")
                }
                var request = URLRequest(url: base)
                request.timeoutInterval = 0.4
                if let (_, response) = try? await session.data(for: request),
                   let response = response as? HTTPURLResponse, response.statusCode == 200 {
                    inferenceURL = base.appendingPathComponent("inference")
                    return
                }
                try await Task.sleep(for: .milliseconds(100))
            }
            throw MeetingError.message("Loading the local Turbo model took too long. Check that OpenWhispr and the model are available.")
        } catch {
            stop()
            throw error
        }
    }

    private func prepareBorrowed(_ installation: LocalWhisperInstallation) async throws {
        let deadline = Date().addingTimeInterval(preparationTimeout)
        do {
            repeat {
                try Task.checkCancellation()
                guard runtime.appIsRunning(installation) else {
                    throw MeetingError.message("OpenWhispr was closed while preparing the model. Start the recording again to use Turbo in the background.")
                }
                if let endpoint = try runtime.runningServer(installation) {
                    var request = URLRequest(url: endpoint.baseURL)
                    request.timeoutInterval = 0.4
                    if let (_, response) = try? await session.data(for: request),
                       (response as? HTTPURLResponse)?.statusCode == 200,
                       try runtime.runningServer(installation) == endpoint {
                        try Task.checkCancellation()
                        self.installation = installation
                        borrowedEndpoint = endpoint
                        healthURL = endpoint.baseURL
                        inferenceURL = endpoint.baseURL.appendingPathComponent("inference")
                        return
                    }
                }
                if Date() >= deadline { break }
                try await Task.sleep(for: .milliseconds(100))
            } while Date() < deadline
            throw MeetingError.message("OpenWhispr is open, but the local Turbo model is not ready. Select Turbo in OpenWhispr, or quit OpenWhispr and start the recording again. No second model was started.")
        } catch {
            stop()
            throw error
        }
    }

    func transcribe(_ wav: Data, language: String?) async throws -> String {
        try await transcribeResult(wav, language: language, duration: 0, includeWords: false).text
    }

    func transcribeResult(_ wav: Data, language: String?, duration: TimeInterval, includeWords: Bool) async throws -> LocalTranscriptionResult {
        guard let url = inferenceURL, isPrepared else {
            throw MeetingError.message("The local Whisper server is no longer running. Start the recording again.")
        }
        if let borrowedEndpoint, let installation {
            guard try runtime.runningServer(installation) == borrowedEndpoint else {
                throw MeetingError.message("The shared Turbo server is no longer available. The transcript received so far is kept; start the recording again. No second model was started.")
            }
        }
        guard url.host == "127.0.0.1", url.scheme == "http" else {
            throw MeetingError.message("Audio for local Whisper must stay on this Mac.")
        }
        let boundary = "MeetingNotes-" + UUID().uuidString
        var body = Data()
        func field(_ name: String, _ value: String) {
            body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
        }
        field("response_format", includeWords ? "verbose_json" : "json")
        field("token_timestamps", includeWords ? "true" : "false")
        if includeWords { field("no_timestamps", "false"); field("vad", "false") }
        field("language", language ?? "auto")
        // A borrowed server may have been launched for translation, language detection,
        // or a different dictation prompt. Each request must retain this meeting's
        // original language and keep its two source channels independent.
        field("translate", "false")
        field("detect_language", "false")
        field("diarize", "false")
        field("tinydiarize", "false")
        field("prompt", "")
        // Avoid multi-pass temperature fallback on short, continuously arriving chunks.
        field("temperature", "0.0")
        field("temperature_inc", "0.0")
        field("no_context", "true")
        body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"meeting.wav\"\r\nContent-Type: audio/wav\r\n\r\n".utf8))
        body.append(wav)
        body.append(Data("\r\n--\(boundary)--\r\n".utf8))
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.httpBody = body
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        let (data, response) = try await session.data(for: request)
        guard let response = response as? HTTPURLResponse, response.statusCode == 200 else {
            throw MeetingError.message("The local Whisper model could not transcribe this segment (HTTP \((response as? HTTPURLResponse)?.statusCode ?? 0)).")
        }
        return try LocalTranscriptionResult.decode(data, duration: duration, includeWords: includeWords)
    }

    func stop() {
        if let process, process.isRunning { process.terminate() }
        process = nil
        borrowedEndpoint = nil
        installation = nil
        inferenceURL = nil
        healthURL = nil
    }

    func shutdown() {
        stop()
        session.invalidateAndCancel()
    }

    deinit {
        if let process, process.isRunning { process.terminate() }
        session.invalidateAndCancel()
    }

    static func availableLoopbackPort() throws -> UInt16 {
        let descriptor = socket(AF_INET, SOCK_STREAM, 0)
        guard descriptor >= 0 else { throw MeetingError.message("Could not create the local Whisper connection.") }
        defer { close(descriptor) }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        address.sin_port = 0
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bound == 0 else { throw MeetingError.message("macOS blocked the local Whisper connection. Start Wallfly from your normal desktop session.") }
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let result = withUnsafeMutablePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(descriptor, $0, &length) }
        }
        guard result == 0 else { throw MeetingError.message("Could not choose a local Whisper port.") }
        return UInt16(bigEndian: address.sin_port)
    }
}

private final class LocalWhisperNoRedirects: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
}
