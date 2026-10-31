import Foundation

enum RuntimeEnvironment {
    static func parse(_ text: String) -> [String: String] {
        var result: [String: String] = [:]
        for raw in text.components(separatedBy: .newlines) {
            var line = raw.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("export ") { line = String(line.dropFirst(7)) }
            guard !line.isEmpty, !line.hasPrefix("#"), let separator = line.firstIndex(of: "=") else { continue }
            let name = line[..<separator].trimmingCharacters(in: .whitespaces)
            var value = line[line.index(after: separator)...].trimmingCharacters(in: .whitespaces)
            if (value.hasPrefix("\"") && value.hasSuffix("\"")) || (value.hasPrefix("'") && value.hasSuffix("'")) {
                value = String(value.dropFirst().dropLast())
            } else if let comment = value.range(of: " #") { value = String(value[..<comment.lowerBound]).trimmingCharacters(in: .whitespaces) }
            guard !name.isEmpty else { continue }
            result[name] = value
        }
        return result
    }
    static var values: [String: String] {
        var values: [String: String] = [:]
        let cwd = URL(fileURLWithPath: FileManager.default.currentDirectoryPath).appendingPathComponent(".env")
        var paths = [cwd]
        if let path = Bundle.main.object(forInfoDictionaryKey: "MeetingNotesEnvFile") as? String { paths.append(URL(fileURLWithPath: path)) }
        for url in paths {
            if let text = try? String(contentsOf: url, encoding: .utf8) { values.merge(parse(text)) { _, new in new } }
        }
        values.merge(ProcessInfo.processInfo.environment) { _, new in new }
        return values
    }
    static var anthropicAPIKey: String { values["ANTHROPIC_API_KEY"] ?? "" }
    static var apiKey: String { values["OPENAI_API_KEY"] ?? values["OPENAI_KEY"] ?? "" }
    static func deepgramKey(in values: [String: String]) -> String {
        values["DEEPGRAM_API_KEY"] ?? values["DEEPGRAM_KEY"] ?? ""
    }
    static var deepgramAPIKey: String { deepgramKey(in: values) }
}
