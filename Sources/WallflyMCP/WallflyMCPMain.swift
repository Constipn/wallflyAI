import Foundation
import MCP
import WallflyReadCore

@main struct WallflyMCPMain {
    static func main() async {
        do {
            let args = Array(CommandLine.arguments.dropFirst())
            if args == ["--help"] { print("wallfly-mcp [--library <path>] — local read-only MCP over stdio"); return }
            guard args.isEmpty || (args.count == 2 && args[0] == "--library") else { throw ReadFailure.invalidArguments }
            let service = MeetingTools(library: ReadLibrary(path: args.last.flatMap { args.count == 2 ? $0 : nil }))
            let server = Server(name: "Wallfly", version: "1.0.0", capabilities: .init(tools: .init(listChanged: false)))
            await server.withMethodHandler(ListTools.self) { _ in .init(tools: MeetingTools.definitions) }
            await server.withMethodHandler(CallTool.self) { params in
                do {
                    let data = try JSONEncoder().encode(params.arguments ?? [:])
                    let response = try service.call(params.name, arguments: data)
                    let value = try JSONDecoder().decode(Value.self, from: response)
                    return .init(content: [.text(text: String(decoding: response, as: UTF8.self), annotations: nil, _meta: nil)], structuredContent: Optional.some(value), isError: false)
                } catch {
                    let message: String
                    switch error {
                    case ReadFailure.unavailable: message = "The meeting library is unavailable. Connect the drive or select a library in Wallfly."
                    case ReadFailure.notFound: message = "Meeting not found. It may have been deleted."
                    case ReadFailure.damagedMeeting: message = "The requested meeting is damaged or unreadable."
                    case ReadFailure.staleCursor: message = "The library or query changed. Restart without a cursor."
                    default: message = "Invalid tool or arguments. Check IDs, dates, limits and time range."
                    }
                    return .init(content: [.text(text: message, annotations: nil, _meta: nil)], isError: true)
                }
            }
            try await server.start(transport: StdioTransport())
            await server.waitUntilCompleted()
        } catch { FileHandle.standardError.write(Data("Wallfly MCP could not start. Use --help to check arguments.\n".utf8)); exit(1) }
    }
}
