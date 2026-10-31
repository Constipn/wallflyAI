import Foundation

/// Callback boundary keeps socket operations off the audio/archival queue and is replaceable in tests.
protocol DeepgramTransport: AnyObject, Sendable {
    func open(_ completion: @escaping @Sendable (Result<Void, Error>) -> Void)
    func send(_ message: URLSessionWebSocketTask.Message, completion: @escaping @Sendable (Error?) -> Void)
    func receive(_ completion: @escaping @Sendable (Result<URLSessionWebSocketTask.Message, Error>) -> Void)
    func close()
}

struct DeepgramTransportError: Error { var status: Int }

final class DeepgramWebSocket: NSObject, DeepgramTransport, URLSessionWebSocketDelegate, @unchecked Sendable {
    private let request: URLRequest
    private let lock = NSLock()
    private var session: URLSession?
    private var socket: URLSessionWebSocketTask?
    private var opening: (@Sendable (Result<Void, Error>) -> Void)?

    init(request: URLRequest) { self.request = request }

    func open(_ completion: @escaping @Sendable (Result<Void, Error>) -> Void) {
        lock.lock()
        let configuration = URLSessionConfiguration.ephemeral
        configuration.urlCache = nil
        configuration.httpShouldSetCookies = false
        configuration.timeoutIntervalForRequest = 10
        let session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
        self.session = session
        let socket = session.webSocketTask(with: request)
        self.socket = socket
        opening = completion
        lock.unlock()
        socket.resume()
    }

    func send(_ message: URLSessionWebSocketTask.Message, completion: @escaping @Sendable (Error?) -> Void) {
        lock.lock(); let socket = socket; lock.unlock()
        guard let socket else { completion(URLError(.cancelled)); return }
        socket.send(message, completionHandler: completion)
    }

    func receive(_ completion: @escaping @Sendable (Result<URLSessionWebSocketTask.Message, Error>) -> Void) {
        lock.lock(); let socket = socket; lock.unlock()
        guard let socket else { completion(.failure(URLError(.cancelled))); return }
        socket.receive(completionHandler: completion)
    }

    func close() {
        lock.lock()
        let socket = socket, session = session
        self.socket = nil; self.session = nil; opening = nil
        lock.unlock()
        socket?.cancel(with: .goingAway, reason: nil)
        session?.invalidateAndCancel()
    }

    private func opened(_ result: Result<Void, Error>) {
        lock.lock(); let callback = opening; opening = nil; lock.unlock()
        callback?(result)
    }

    func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didOpenWithProtocol protocol: String?) {
        opened(.success(()))
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        let status = (task.response as? HTTPURLResponse)?.statusCode ?? 0
        opened(.failure(status >= 400 ? DeepgramTransportError(status: status) : error ?? URLError(.networkConnectionLost)))
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        // Never forward the API credential to a redirected host.
        completionHandler(nil)
    }
}

enum DeepgramConfiguration {
    static func request(key: String, locale: Locale) -> URLRequest {
        let code = locale.language.languageCode?.identifier
        let language = code == "de" ? "de" : code == "en" ? "en" : "multi"
        var url = URLComponents(string: "wss://api.deepgram.com/v1/listen")!
        url.queryItems = ["model": "nova-3", "encoding": "linear16", "sample_rate": "16000", "channels": "1",
                          "interim_results": "true", "smart_format": "true", "language": language,
                          "endpointing": "100"].sorted { $0.key < $1.key }.map { URLQueryItem(name: $0.key, value: $0.value) }
        var request = URLRequest(url: url.url!)
        request.setValue("Token " + key.trimmingCharacters(in: .whitespacesAndNewlines), forHTTPHeaderField: "Authorization")
        return request
    }
}
