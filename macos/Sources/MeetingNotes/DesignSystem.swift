import SwiftUI

enum Design {
    static let body = Font.system(size: 14)
    static let metadata = Font.system(size: 12)
    static let title = Font.system(size: 30, weight: .medium, design: .serif)
    static let radius: CGFloat = 10
}

struct HighlightedText: View {
    let text: String
    var query = ""
    var body: some View { Text(attributed) }
    private var attributed: AttributedString {
        var value = AttributedString(text)
        for match in MeetingSearch.ranges(in: text, terms: MeetingSearch.terms(query)) {
            if let range = Range(match, in: text),
               let lower = AttributedString.Index(range.lowerBound, within: value),
               let upper = AttributedString.Index(range.upperBound, within: value) {
                value[lower..<upper].backgroundColor = Palette.gold100
                value[lower..<upper].foregroundColor = Palette.ink
            }
        }
        return value
    }
}

struct FolderBadge: View {
    let folder: MeetingFolder
    var body: some View {
        Label(folder.name, systemImage: folder.icon)
            .font(Design.metadata).foregroundStyle(Palette.folderColors(for: folder).foreground).lineLimit(1)
            .padding(.horizontal, 7).padding(.vertical, 3)
            .background(Palette.folderColors(for: folder).background).clipShape(RoundedRectangle(cornerRadius: 5))
            .help(folder.name)
    }
}

struct WorkspaceEmptyState: View {
    let icon: String
    let title: String
    let detail: String
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Image(systemName: icon).font(.system(size: 25, weight: .light)).foregroundStyle(Palette.muted)
            Text(title).font(.system(size: 22, weight: .medium, design: .serif))
            Text(detail).font(Design.body).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
        }.padding(28).frame(maxWidth: .infinity, alignment: .leading)
    }
}

extension EnhancementProvider {
    var displayName: String {
        switch self {
        case .disabled: return "Off"
        case .anthropic: return "Anthropic"
        case .openai: return "OpenAI"
        case .compatible: return "Custom API (OpenAI-compatible)"
        }
    }
}
extension TranscriptionProvider {
    var displayName: String {
        switch self {
        case .deepgram: return "Deepgram · Nova-3 (Streaming)"
        case .openWhispr: return "OpenWhispr · Whisper Turbo (local)"
        case .openai: return "OpenAI"
        }
    }
}

struct HoverSurface: ViewModifier {
    @ViewState private var hovering = false
    func body(content: Content) -> some View {
        content.background(hovering ? Palette.gray200.opacity(0.35) : .clear)
            .clipShape(RoundedRectangle(cornerRadius: Design.radius))
            .onHover { hovering = $0 }
    }
}
