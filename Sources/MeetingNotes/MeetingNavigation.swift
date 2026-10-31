import SwiftUI
import AppKit
import UniformTypeIdentifiers

enum MeetingDrag {
    static let typeIdentifier = UTType(exportedAs: "io.github.constipn.wallfly.meeting-id", conformingTo: .data).identifier
    static let pasteboardType = NSPasteboard.PasteboardType(typeIdentifier)
    static func decode(_ data: Data?) -> UUID? {
        guard let data, data.count <= 64, let text = String(data: data, encoding: .utf8) else { return nil }
        return UUID(uuidString: text)
    }
}

struct MeetingFolderPicker: View {
    @ObservedObject var store: MeetingStore
    let meetingID: UUID
    @ViewState private var showPicker = false
    @ViewState private var creatingFolder = false

    private var assigned: MeetingFolder? {
        store.folders.first { $0.id == store.folderCatalog.folderID(for: meetingID) }
    }

    private var folderColors: Palette.FolderColors? {
        assigned.map { Palette.folderColors(for: $0.id) }
    }

    var body: some View {
        Button { showPicker.toggle() } label: {
            HStack(spacing: 7) {
                Image(systemName: assigned?.icon ?? "folder.badge.plus").font(.system(size: 16))
                Text(assigned?.name ?? "Assign folder").lineLimit(1)
            }.padding(9).foregroundStyle(folderColors?.foreground ?? Palette.ink)
                .background(folderColors?.background ?? Palette.sidebar).clipShape(Capsule())
                .overlay(Capsule().stroke(Palette.line))
        }.buttonStyle(.plain).disabled(!store.folderLibraryAvailable)
            .help("Assign folder").accessibilityLabel("Assign folder")
            .preference(key: ChildModalPresentedKey.self, value: showPicker || creatingFolder)
            .popover(isPresented: $showPicker, arrowEdge: .bottom) {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Move to folder").font(.headline).foregroundStyle(Palette.strong).padding(.bottom, 4)
                    destination("No folder", icon: "tray", id: nil)
                    ScrollView {
                        VStack(alignment: .leading, spacing: 4) {
                            ForEach(store.folders) { folder in
                                destination(folder.name, icon: folder.icon, id: folder.id)
                            }
                        }
                    }.frame(maxHeight: 240)
                    Divider()
                    Button { showPicker = false; creatingFolder = true } label: {
                        Label("Create folder…", systemImage: "folder.badge.plus")
                    }.buttonStyle(.plain).padding(.vertical, 6)
                }.padding(18).frame(width: 260)
            }
            .sheet(isPresented: $creatingFolder) {
                FolderEditor(store: store, folder: MeetingFolder(), creating: true) { id in
                    store.moveMeeting(meetingID, to: id)
                }
            }
    }

    private func destination(_ name: String, icon: String, id: UUID?) -> some View {
        Button {
            store.moveMeeting(meetingID, to: id)
            showPicker = false
        } label: {
            HStack {
                Label(name, systemImage: icon).lineLimit(1)
                    .foregroundStyle(id.map { Palette.folderColors(for: $0).foreground } ?? Palette.ink)
                Spacer()
                if store.folderCatalog.folderID(for: meetingID) == id { Image(systemName: "checkmark") }
            }.padding(.vertical, 7).contentShape(Rectangle())
        }.buttonStyle(.plain)
    }
}
