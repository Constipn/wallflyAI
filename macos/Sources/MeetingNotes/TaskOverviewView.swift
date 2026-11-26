import SwiftUI

struct TaskOverviewView: View {
    @ObservedObject var store: MeetingStore
    var openSettings: () -> Void
    @AppStorage("overviewIntroductionDismissed") private var introductionDismissed = false
    @ViewState private var showCompleted = false

    private var completedGroups: [MeetingTaskGroup] { store.taskGroups(completed: true) }

    var body: some View {
        let visibleMeetings = store.visibleMeetings
        let openGroups = store.taskGroups(completed: false)
        let openCount = openGroups.reduce(0) { $0 + $1.rows.count }
        let completedCount = completedGroups.reduce(0) { $0 + $1.rows.count }
        let needsRetry = visibleMeetings.filter {
            ($0.taskExtraction == .failed || $0.taskExtraction == .pending)
                && $0.id != store.extractingTasksID && $0.id != store.enhancingID
        }
        ScrollView {
            VStack(alignment: .leading, spacing: 28) {
                if let active = store.activeMeeting {
                    Button { store.openActiveMeeting() } label: {
                        Label("Go to recording: \(active.title)", systemImage: "waveform.circle.fill")
                    }.buttonStyle(.borderedProminent)
                }
                if !introductionDismissed && store.meetings.isEmpty { introduction }
                HStack(alignment: .firstTextBaseline, spacing: 12) {
                    Text("My to-dos").font(.system(size: 34, weight: .medium, design: .serif)).foregroundStyle(Palette.strong)
                    Text("\(openCount) open").font(.system(size: 13)).foregroundStyle(Palette.muted)
                    Spacer()
                }
                if store.extractingTasksID != nil {
                    HStack(spacing: 10) {
                        ProgressView().controlSize(.small)
                        Text(store.taskProgress).font(.system(size: 12)).foregroundStyle(Palette.muted)
                    }.accessibilityElement(children: .combine)
                }
                if !needsRetry.isEmpty {
                    VStack(alignment: .leading, spacing: 12) {
                        Text("Task detection is still pending for some meetings.").font(.system(size: 12, weight: .medium))
                        ForEach(needsRetry) { meeting in
                            HStack(alignment: .firstTextBaseline, spacing: 16) {
                                Button { store.selectMeeting(meeting.id) } label: {
                                    Text(meeting.title).multilineTextAlignment(.leading)
                                }.buttonStyle(.link)
                                Spacer(minLength: 8)
                                Button("Retry task detection") {
                                    Task { await store.retryTaskExtraction(meetingID: meeting.id) }
                                }.disabled(store.busy)
                            }.font(.system(size: 12))
                        }
                    }.padding(16).background(Palette.sidebar).clipShape(RoundedRectangle(cornerRadius: 10))
                }
                ForEach(visibleMeetings.filter { !store.pendingTasks(for: $0).isEmpty }) { meeting in
                    Button { store.requestTaskReview(meeting.id) } label: {
                        Label("Review suggestions: \(meeting.title)", systemImage: "checklist")
                    }.disabled(store.busy)
                }
                if openGroups.isEmpty {
                    VStack(alignment: .leading, spacing: 12) {
                        Image(systemName: "checkmark.circle").font(.system(size: 30, weight: .light)).foregroundStyle(Palette.accent)
                        Text(completedCount == 0 ? "No to-dos yet" : "All done").font(.system(size: 19, weight: .medium, design: .serif))
                        Text("Only your confirmed to-dos appear here. You can review suggestions after enhancement.")
                            .font(.system(size: 13)).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
                    }.padding(.vertical, 30)
                } else {
                    groups(openGroups)
                }
                if completedCount > 0 {
                    Divider()
                    DisclosureGroup(isExpanded: $showCompleted) {
                        groups(completedGroups).padding(.top, 20)
                    } label: {
                        Text("Done · \(completedCount)").font(.system(size: 13, weight: .medium)).foregroundStyle(Palette.muted)
                    }
                }
            }.padding(38).frame(maxWidth: 920, alignment: .leading).frame(maxWidth: .infinity, alignment: .topLeading)
        }
        .onChange(of: store.libraryScope) { _, _ in showCompleted = false }
    }

    private var introduction: some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: "leaf").font(.system(size: 25, weight: .light)).foregroundStyle(Palette.accent)
            VStack(alignment: .leading, spacing: 8) {
                Text("Your place for better meetings").font(.system(size: 15, weight: .semibold, design: .serif))
                Text("Capture conversations and thoughts, and keep track of agreed tasks.")
                    .font(.system(size: 12)).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
                HStack(spacing: 18) {
                    Button("Set up enhancement", action: openSettings)
                    Button("Load example") { store.importDemo() }.disabled(store.busy)
                }.buttonStyle(.link).font(.system(size: 12))
            }
            Spacer(minLength: 4)
            Button { introductionDismissed = true } label: {
                Image(systemName: "xmark").font(.system(size: 12, weight: .medium)).padding(6)
            }.buttonStyle(.plain).foregroundStyle(Palette.muted)
                .accessibilityLabel("Hide introduction").help("Hide introduction")
        }.padding(18).background(Palette.sidebar).clipShape(RoundedRectangle(cornerRadius: 12))
    }

    private func groups(_ groups: [MeetingTaskGroup]) -> some View {
        VStack(alignment: .leading, spacing: 28) {
            ForEach(groups) { group in
                VStack(alignment: .leading, spacing: 4) {
                    HStack(spacing: 8) {
                        Image(systemName: group.icon).foregroundStyle(Palette.muted)
                        Text(group.name).foregroundStyle(Palette.ink)
                        Text("\(group.rows.count)").foregroundStyle(Palette.muted)
                    }.font(.system(size: 14, weight: .semibold)).padding(.bottom, 9)
                    ForEach(group.rows) { row in
                        taskRow(row)
                        if row.id != group.rows.last?.id { Divider().padding(.leading, 30) }
                    }
                }
            }
        }
    }

    private func taskRow(_ row: MeetingTaskRow) -> some View {
        let completed = row.task.completedAt != nil
        return HStack(alignment: .top, spacing: 12) {
            Toggle("", isOn: Binding(get: { completed }, set: { value in
                store.setTaskCompleted(meetingID: row.meeting.id, taskID: row.task.id, completed: value)
            }))
            .toggleStyle(.checkbox).labelsHidden().padding(.top, 1)
            .accessibilityLabel("\(completed ? "Reopen" : "Complete"): \(row.task.text)")
            VStack(alignment: .leading, spacing: 7) {
                Text(row.task.text).font(.system(size: 14)).strikethrough(completed)
                    .foregroundStyle(completed ? Palette.muted : Palette.ink)
                    .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                if let owner = row.task.owner, !owner.isEmpty {
                    Text(owner).font(.system(size: 12)).foregroundStyle(Palette.muted)
                }
                Button { store.selectMeeting(row.meeting.id) } label: {
                    HStack(alignment: .firstTextBaseline, spacing: 5) {
                        Image(systemName: "arrow.up.right")
                        Text("\(row.meeting.title) · \(row.meeting.createdAt.formatted(date: .abbreviated, time: .omitted))")
                            .multilineTextAlignment(.leading)
                    }.font(.system(size: 12))
                }.buttonStyle(.plain).foregroundStyle(Palette.link)
                    .help("Open source meeting")
            }.frame(maxWidth: .infinity, alignment: .leading)
        }.padding(.vertical, 10)
    }
}


struct PersonalTaskReviewView: View {
    @ObservedObject var store: MeetingStore
    let meetingID: UUID
    @Environment(\.dismiss) private var dismiss
    private var meeting: Meeting? { store.meetings.first { $0.id == meetingID } }

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            Text("Add to my to-dos?").font(.title2)
            Text(meeting?.title ?? "Meeting").foregroundStyle(Palette.muted)
            Text("These tasks may be for you. Only accept what you want to do yourself.")
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    ForEach(meeting.map { store.pendingTasks(for: $0) } ?? []) { task in
                        VStack(alignment: .leading, spacing: 8) {
                            Text(task.text).textSelection(.enabled)
                            HStack {
                                Button("Accept") { store.reviewTask(meetingID: meetingID, taskID: task.id, accept: true) }
                                    .buttonStyle(.borderedProminent)
                                Button("Not my task") { store.reviewTask(meetingID: meetingID, taskID: task.id, accept: false) }
                            }.disabled(store.busy)
                        }
                        Divider()
                    }
                }
            }
            HStack {
                Text("You can review these later under My to-dos.").font(.caption).foregroundStyle(Palette.muted)
                Spacer()
                Button("Later") { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }.padding(28).frame(width: 560, height: 460)
    }
}
