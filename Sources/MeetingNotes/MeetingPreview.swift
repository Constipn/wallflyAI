import SwiftUI

struct MeetingPreview: View {
    @ObservedObject var store: MeetingStore
    let meeting: Meeting
    var query = ""
    var result: MeetingSearchResult?
    @ViewState private var source: MeetingSearchSource = .notes
    @ViewState private var hitIndex = 0
    private var hits: [MeetingSearchHit] { result?.hits ?? [] }
    private var activeHit: MeetingSearchHit? { hits.indices.contains(hitIndex) ? hits[hitIndex] : nil }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if !hits.isEmpty {
                HStack(spacing: 8) {
                    Image(systemName: "magnifyingglass")
                    Text("Match \(hitIndex + 1) of \(hits.count)").lineLimit(1)
                    Spacer(minLength: 0)
                    Button { move(-1) } label: { Image(systemName: "chevron.up") }
                        .help("Previous match").accessibilityLabel("Previous match")
                    Button { move(1) } label: { Image(systemName: "chevron.down") }
                        .help("Next match").accessibilityLabel("Next match")
                }.font(Design.metadata).padding(12).background(Palette.gold100)
            }
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(alignment: .leading, spacing: 22) {
                        let passages = MeetingSearch.passages(meeting, source: source)
                        VStack(alignment: .leading, spacing: 14) {
                            Text((meeting.startedAt ?? meeting.createdAt).formatted(.dateTime.day().month(.wide).year().hour().minute().locale(Locale(identifier: "en_GB"))))
                                .font(Design.metadata).foregroundStyle(Palette.muted)
                            HighlightedText(text: meeting.title.isEmpty ? "Untitled meeting" : meeting.title, query: query)
                                .font(Design.title).fixedSize(horizontal: false, vertical: true).id("title")
                            HStack {
                                MeetingFolderPicker(store: store, meetingID: meeting.id)
                                Spacer()
                                Button("Edit", systemImage: "square.and.pencil") { store.selectMeeting(meeting.id) }
                                    .buttonStyle(.bordered)
                            }.font(Design.metadata)
                        }
                        ViewThatFits(in: .horizontal) {
                            HStack(spacing: 14) { tabs }
                            VStack(alignment: .leading, spacing: 10) { tabs }
                        }
                        if source == .transcript {
                            HStack { Spacer(); TranscriptCopyButton(meeting: meeting) }
                        }
                        Divider()
                        if passages.isEmpty {
                            WorkspaceEmptyState(icon: source == .transcript ? "waveform" : "text.alignleft",
                                title: "No content yet",
                                detail: source == .transcript ? "Start a recording to capture the conversation." : "Open the meeting to edit it or enhance existing notes.")
                        } else {
                            LazyVStack(alignment: .leading, spacing: 12) {
                                ForEach(passages) { passage in
                                    VStack(alignment: .leading, spacing: 6) {
                                        if let offset = passage.offset {
                                            Text(Meeting.timestamp(offset)).font(.system(size: 12, design: .monospaced)).foregroundStyle(Palette.muted)
                                        }
                                        if query.isEmpty && source == .enhanced {
                                            MarkdownLine(text: passage.text)
                                        } else {
                                            HighlightedText(text: passage.text, query: query)
                                                .font(Design.body).lineSpacing(6).fixedSize(horizontal: false, vertical: true)
                                        }
                                    }.frame(maxWidth: .infinity, alignment: .leading)
                                        .padding(8)
                                        .background(activeHit?.id == passage.id ? Palette.gold100.opacity(0.45) : .clear)
                                        .overlay(alignment: .leading) {
                                            if activeHit?.id == passage.id { RoundedRectangle(cornerRadius: 2).fill(Palette.gold600).frame(width: 3) }
                                        }
                                        .id(passage.id)
                                }
                            }.textSelection(.enabled)
                        }
                    }.padding(28).frame(maxWidth: 820, alignment: .leading).frame(maxWidth: .infinity, alignment: .leading)
                }
                .onAppear { reset(); scroll(proxy) }
                .onChange(of: result) { _, _ in reset(); scroll(proxy) }
                .onChange(of: hitIndex) { _, _ in scroll(proxy) }
            }
        }.accessibilityIdentifier("meeting-preview")
    }

    private var tabs: some View {
        ForEach([MeetingSearchSource.notes, .enhanced, .transcript], id: \.self) { tab in
            Button { source = tab } label: {
                Text(tab.label).font(.system(size: 12, weight: source == tab ? .semibold : .regular))
                    .foregroundStyle(source == tab ? Palette.ink : Palette.muted)
                    .padding(.vertical, 7)
                    .overlay(alignment: .bottom) { if source == tab { Rectangle().fill(Palette.accent).frame(height: 2) } }
            }.buttonStyle(.plain)
        }
    }
    private func reset() {
        hitIndex = 0
        let defaultSource: MeetingSearchSource = meeting.enhancedNotes.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? .notes : .enhanced
        source = hits.first.map { $0.passage.source == .title ? defaultSource : $0.passage.source } ?? defaultSource
    }
    private func move(_ delta: Int) {
        guard !hits.isEmpty else { return }
        hitIndex = (hitIndex + delta + hits.count) % hits.count
    }
    private func scroll(_ proxy: ScrollViewProxy) {
        guard let hit = activeHit else { return }
        if hit.passage.source != .title { source = hit.passage.source }
        // Wait for the selected source's anchors to enter the view tree.
        Task { @MainActor in
            await Task.yield()
            proxy.scrollTo(hit.id, anchor: .center)
        }
    }
}

struct MarkdownLine: View {
    let text: String
    var body: some View {
        if text.hasPrefix("# ") { Text(String(text.dropFirst(2))).font(.system(size: 25, weight: .medium, design: .serif)).padding(.top, 8) }
        else if text.hasPrefix("## ") { Text(String(text.dropFirst(3))).font(.system(size: 20, weight: .medium, design: .serif)).padding(.top, 8) }
        else if text.hasPrefix("### ") { Text(String(text.dropFirst(4))).font(.system(size: 16, weight: .semibold)) }
        else if text.hasPrefix("- ") || text.hasPrefix("* ") {
            HStack(alignment: .top, spacing: 10) { Text("•"); Text(.init(String(text.dropFirst(2)))) }.font(Design.body).lineSpacing(6)
        } else { Text(.init(text)).font(Design.body).lineSpacing(6) }
    }
}
