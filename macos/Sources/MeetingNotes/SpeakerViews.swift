import SwiftUI

struct TeamsSpeakerSetup: View {
    @ObservedObject var store: MeetingStore
    var body: some View {
        Toggle("Use speaker names from Teams", isOn: Binding(get: { store.settings.teamsSpeakerNames }, set: store.setTeamsSpeakerNames))
            .onAppear { store.refreshTeamsSpeakerStatus() }
        if store.settings.teamsSpeakerNames && store.teamsSpeakerStatus == .needsAccessibility {
            SettingsWarning(text: "Accessibility access is missing", button: "Allow…") { store.requestTeamsAccessibility() }
        }
    }
}

struct TranscriptTurnView: View {
    @ObservedObject var store: MeetingStore
    let meeting: Meeting
    let turn: TranscriptTurn
    var showsSpeaker = true
    @ViewState private var editing: SpeakerEditRequest?

    private var isMe: Bool { turn.speaker == .me }
    private var participant: MeetingParticipant? { meeting.participants?.first(where: { $0.id == turn.participantID }) }
    private var color: Color {
        guard !isMe else { return Palette.accent }
        guard let participant else { return Palette.muted }
        let colors = Palette.speakerColors
        return colors[max(0, participant.colorIndex) % colors.count]
    }
    var body: some View {
        ChatBubbleRow(trailing: isMe) {
            VStack(alignment: isMe ? .trailing : .leading, spacing: 4) {
                if showsSpeaker && !isMe {
                    Group { if store.busy { speakerLabel } else { speakerMenu } }.padding(.horizontal, 4)
                }
                bubble
            }
        }.preference(key: ChildModalPresentedKey.self, value: editing != nil)
        .sheet(item: $editing) { request in
            SpeakerNameEditor(store: store, meetingID: meeting.id, request: request, references: turn.references)
        }
    }
    private var bubble: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(turn.text).font(.system(size: 13)).lineSpacing(3).foregroundStyle(Palette.ink).textSelection(.enabled)
            if !turn.isFinal {
                Text(store.isRecording ? "Transcribing…" : "Segment not finished yet")
                    .font(.system(size: 11)).foregroundStyle(Palette.muted)
            }
        }
        .padding(.horizontal, 13).padding(.vertical, 9)
        .background(isMe ? Palette.transcriptMe : Palette.transcriptOthers, in: bubbleShape)
    }
    private var bubbleShape: UnevenRoundedRectangle {
        isMe ? UnevenRoundedRectangle(topLeadingRadius: 14, bottomLeadingRadius: 14, bottomTrailingRadius: 4, topTrailingRadius: 14)
            : UnevenRoundedRectangle(topLeadingRadius: 4, bottomLeadingRadius: 14, bottomTrailingRadius: 14, topTrailingRadius: 14)
    }
    private var speakerMenu: some View {
        Menu {
            if let participant {
                Button("Rename speaker…") { editing = SpeakerEditRequest(participantID: participant.id, name: participant.name) }
                Divider()
            }
            Menu("Assign this segment") {
                ForEach(meeting.participants ?? []) { participant in
                    Button(participant.name) { store.assignTranscript(meetingID: meeting.id, references: turn.references, participantID: participant.id) }
                }
                Divider()
                Button("New speaker…") { editing = SpeakerEditRequest() }
                Button("Others · unknown") { store.assignTranscript(meetingID: meeting.id, references: turn.references, participantID: nil) }
            }
        } label: { speakerLabel.contentShape(Rectangle()) }
            .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden)
            .help("Correct speaker or assign segment")
            .accessibilityIdentifier("transcript-speaker")
    }
    private var speakerLabel: some View {
        HStack(spacing: 5) {
            Circle().fill(color).frame(width: 6, height: 6)
            Text(turn.participantID == nil ? "Others" : meeting.speakerName(turn.speaker, participantID: turn.participantID))
                .font(.system(size: 11, weight: .semibold)).foregroundStyle(color).lineLimit(1)
        }
    }
}

private struct SpeakerEditRequest: Identifiable {
    let id = UUID()
    var participantID: UUID?
    var name: String = ""
}

private struct SpeakerNameEditor: View {
    @ObservedObject var store: MeetingStore
    @Environment(\.dismiss) private var dismiss
    @ViewState private var name: String
    let meetingID: UUID
    let request: SpeakerEditRequest
    let references: [TranscriptReference]
    init(store: MeetingStore, meetingID: UUID, request: SpeakerEditRequest, references: [TranscriptReference]) {
        self.store = store; self.meetingID = meetingID; self.request = request; self.references = references
        _name = ViewState(initialValue: request.name)
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text(request.participantID == nil ? "Assign speaker" : "Rename speaker").font(.system(size: 22, weight: .semibold))
            TextField("Speaker name", text: $name).textFieldStyle(.roundedBorder)
            Text(request.participantID == nil ? "Assigns this segment to a new speaker in the meeting." : "Changes the speaker name throughout the meeting. Enhanced notes can then be regenerated.")
                .font(.system(size: 12)).foregroundStyle(Palette.muted)
            if let error = store.errorMessage { Text(error).font(.system(size: 12)).foregroundStyle(Palette.red600) }
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Save") {
                    if store.editParticipant(meetingID: meetingID, participantID: request.participantID, name: name, references: references) { dismiss() }
                }.buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
                    .disabled(name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || name.count > 160 || store.busy)
            }
        }.padding(28).frame(width: 400).background(Palette.paper).tint(Palette.red600)
    }
}
