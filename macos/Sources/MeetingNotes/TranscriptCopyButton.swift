import SwiftUI
import AppKit

struct TranscriptCopyButton: View {
    let meeting: Meeting
    @ViewState private var copied = false

    var body: some View {
        Button {
            NSPasteboard.general.clearContents()
            copied = NSPasteboard.general.setString(meeting.copyableTranscript, forType: .string)
        } label: {
            HStack(spacing: 5) {
                Image(systemName: copied ? "checkmark" : "doc.on.doc")
                if copied { Text("Copied").font(Design.metadata) }
            }.frame(minWidth: 28, minHeight: 28)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(meeting.transcript.isEmpty)
        .help("Copy entire transcript")
        .accessibilityLabel(copied ? "Transcript copied" : "Copy transcript")
        .accessibilityIdentifier("copy-transcript")
        .task(id: copied) {
            guard copied else { return }
            try? await Task.sleep(for: .seconds(1.5))
            if !Task.isCancelled { copied = false }
        }
    }
}
