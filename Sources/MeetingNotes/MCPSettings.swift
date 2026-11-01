import SwiftUI
import AppKit
import WallflyReadCore

struct MCPSettingsSection: View {
    private var executable: URL { Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS/wallfly-mcp") }
    private var configuration: String {
        let value: [String: Any] = ["mcpServers": ["wallfly": ["command": executable.path, "args": [String]()]]]
        return (try? JSONSerialization.data(withJSONObject: value, options: [.prettyPrinted, .sortedKeys])).map { String(decoding: $0, as: UTF8.self) } ?? ""
    }
    var body: some View {
        Section("Local MCP access") {
            Text("Connect a client that supports local stdio MCP servers to search and read all saved Wallfly meetings, notes and tasks. The Wallfly window can stay closed.")
            LabeledContent("Program", value: FileManager.default.isExecutableFile(atPath: executable.path) ? "Available" : "Not installed in this build")
            LabeledContent("Library", value: FileManager.default.isReadableFile(atPath: ReadLibrary.selectedPath) ? "Available" : "Unavailable")
            Text(executable.path).font(.caption).textSelection(.enabled)
            Text(ReadLibrary.selectedPath).font(.caption).textSelection(.enabled)
            Button("Copy MCP configuration") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(configuration, forType: .string)
            }.disabled(!FileManager.default.isExecutableFile(atPath: executable.path))
            Text(configuration).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
            Text("Add this configuration to your client's MCP settings and restart its connection. Configuration formats vary by client. Moving Wallfly requires copying a new program path. Changing the meeting library does not. Availability above does not indicate a connected client. Your client may send retrieved text to its AI provider.").font(.caption)
        }
    }
}
