import SwiftUI
import AppKit

/// Native menus tint SF Symbols uniformly; preserve each swatch's own pixels instead.
@MainActor
enum FolderColorSwatch {
    static func image(for color: FolderColor) -> NSImage {
        let fill = NSColor(Palette.folderColors(for: color).foreground)
        let image = NSImage(size: NSSize(width: 14, height: 14), flipped: false) { bounds in
            fill.setFill()
            NSBezierPath(ovalIn: bounds.insetBy(dx: 1, dy: 1)).fill()
            return true
        }
        image.isTemplate = false
        return image
    }
}

struct FolderSidebar: View {
    @ObservedObject var store: MeetingStore
    @ViewState private var editingFolder: MeetingFolder?
    @ViewState private var creating = false
    @ViewState private var deletingFolder: MeetingFolder?
    @ViewState private var confirmDelete = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("FOLDERS").font(.system(size: 12, weight: .semibold)).tracking(1.3).foregroundStyle(Palette.ink)
                Spacer()
                Button {
                    creating = true
                    editingFolder = MeetingFolder()
                } label: { Image(systemName: "folder.badge.plus") }
                    .buttonStyle(.plain).help("Create folder")
                    .accessibilityLabel("Create folder").disabled(!store.folderLibraryAvailable)
            }.padding(.horizontal, 16)
            ScrollView {
                VStack(spacing: 2) {
                    row("All meetings", icon: "square.stack", scope: .all, count: store.meetings.count)
                    row("No folder", icon: "tray", scope: .unfiled,
                        count: store.meetings.filter { store.folderCatalog.folderID(for: $0.id) == nil }.count)
                    ForEach(store.folders) { folder in
                        row(folder.name, icon: folder.icon, scope: .folder(folder.id),
                            count: store.meetings.filter { store.folderCatalog.folderID(for: $0.id) == folder.id }.count)
                            .help(folder.description.isEmpty ? folder.name : folder.description)
                            .contextMenu {
                                Button("Edit folder…", systemImage: "pencil") { creating = false; editingFolder = folder }
                                    .disabled(store.busy)
                                Button("Delete folder…", systemImage: "trash", role: .destructive) {
                                    deletingFolder = folder; confirmDelete = true
                                }.disabled(store.busy)
                            }
                    }
                }.padding(.horizontal, 8)
            }.frame(maxHeight: .infinity)
        }
        .preference(key: ChildModalPresentedKey.self, value: editingFolder != nil || confirmDelete)
        .sheet(item: $editingFolder) { folder in FolderEditor(store: store, folder: folder, creating: creating) }
        .confirmationDialog("Delete “\(deletingFolder?.name ?? "Folder")”?", isPresented: $confirmDelete, titleVisibility: .visible) {
            Button("Delete folder", role: .destructive) {
                if let id = deletingFolder?.id { store.deleteFolder(id) }
                deletingFolder = nil
            }
        } message: { Text("The meetings will be moved to “No folder”. Notes, transcripts and recordings are kept.") }
    }

    private func row(_ name: String, icon: String, scope: MeetingLibraryScope, count: Int) -> some View {
        FolderDropRow(store: store, name: name, icon: icon, scope: scope, count: count)
    }
}

private struct FolderDropRow: View {
    @ObservedObject var store: MeetingStore
    let name: String
    let icon: String
    let scope: MeetingLibraryScope
    let count: Int
    @ViewState private var targeted = false

    private var folderColors: Palette.FolderColors? {
        guard case .folder(let id) = scope,
              let folder = store.folders.first(where: { $0.id == id }) else { return nil }
        return Palette.folderColors(for: folder)
    }

    var body: some View {
        Button { store.selectLibrary(scope) } label: {
            HStack(spacing: 9) {
                Image(systemName: icon).frame(width: 19).foregroundStyle(folderColors?.foreground ?? Palette.muted)
                Text(name).lineLimit(1).foregroundStyle(folderColors?.foreground ?? Palette.ink)
                Spacer(minLength: 4)
                Text("\(count)").font(.system(size: 12)).foregroundStyle(Palette.muted)
            }.font(.system(size: 14)).padding(.horizontal, 10).frame(height: 34)
                .contentShape(Rectangle())
                .background(targeted ? Palette.selection : store.libraryScope == scope ? folderColors?.background ?? Palette.gray100 : Color.clear)
                .clipShape(RoundedRectangle(cornerRadius: 7))
        }.buttonStyle(.plain).modifier(HoverSurface())
        .onDrop(of: scope == .all || !store.folderLibraryAvailable ? [] : [MeetingDrag.typeIdentifier], isTargeted: $targeted) { providers in
            guard scope != .all, store.folderLibraryAvailable,
                  let provider = providers.first else { return false }
            let folderID: UUID?
            if case .folder(let id) = scope { folderID = id } else { folderID = nil }
            provider.loadDataRepresentation(forTypeIdentifier: MeetingDrag.typeIdentifier) { data, _ in
                guard let id = MeetingDrag.decode(data) else { return }
                Task { @MainActor in store.moveMeeting(id, to: folderID) }
            }
            return true
        }
    }
}

struct FolderEditor: View {
    @ObservedObject var store: MeetingStore
    @ViewState private var folder: MeetingFolder
    @Environment(\.dismiss) private var dismiss
    let creating: Bool
    var onSave: ((UUID) -> Void)?

    init(store: MeetingStore, folder: MeetingFolder, creating: Bool, onSave: ((UUID) -> Void)? = nil) {
        self.store = store
        var draft = folder
        draft.color = draft.color ?? store.folderCatalog.nextColor
        _folder = ViewState(initialValue: draft)
        self.creating = creating
        self.onSave = onSave
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            HStack {
                Text(creating ? "Create folder" : "Edit folder").font(.system(size: 22, weight: .semibold))
                Spacer()
                Button { dismiss() } label: { Image(systemName: "xmark") }.buttonStyle(.plain).help("Close").accessibilityLabel("Close")
            }.padding(.bottom, 6)
            VStack(alignment: .leading, spacing: 10) {
                Text("Name and icon").font(.system(size: 13, weight: .medium))
                HStack(spacing: 12) {
                    Menu {
                        ForEach(MeetingFolder.icons, id: \.self) { icon in
                            Button { folder.icon = icon } label: { Label(iconName(icon), systemImage: icon) }
                        }
                    } label: {
                        Image(systemName: folder.icon).font(.system(size: 20)).frame(width: 35, height: 35)
                            .foregroundStyle(Palette.folderColors(for: folder).foreground)
                    }
                        .menuStyle(.borderlessButton).tint(Palette.folderColors(for: folder).foreground)
                        .frame(width: 42).help("Choose folder icon").accessibilityLabel("Choose folder icon")
                    TextField("Name", text: $folder.name).textFieldStyle(.plain).font(.system(size: 16))
                }.padding(9).background(Palette.sidebar).clipShape(RoundedRectangle(cornerRadius: 9))
                    .overlay(RoundedRectangle(cornerRadius: 9).stroke(Palette.line))
            }
            Picker("Color", selection: Binding(get: { folder.color ?? .blue }, set: { folder.color = $0 })) {
                ForEach(FolderColor.allCases, id: \.self) { color in
                    Label {
                        Text(color.name)
                    } icon: {
                        Image(nsImage: FolderColorSwatch.image(for: color)).renderingMode(.original)
                    }.tag(color)
                }
            }.pickerStyle(.menu).tint(Palette.folderColors(for: folder).foreground).accessibilityLabel("Folder color")
            VStack(alignment: .leading, spacing: 10) {
                Text("Description").font(.system(size: 13, weight: .medium))
                ZStack(alignment: .topLeading) {
                    if folder.description.isEmpty {
                        Text("What is this folder for?").foregroundStyle(Palette.muted).padding(10).allowsHitTesting(false)
                    }
                    TextEditor(text: $folder.description).scrollContentBackground(.hidden).padding(5)
                }.font(.system(size: 13)).frame(height: 92).background(Palette.sidebar)
                    .clipShape(RoundedRectangle(cornerRadius: 9))
                    .overlay(RoundedRectangle(cornerRadius: 9).stroke(Palette.line))
            }
            VStack(alignment: .leading, spacing: 10) {
                Text("Workspace").font(.system(size: 13, weight: .medium))
                Label("My notes", systemImage: "lock.fill").font(.system(size: 14))
                    .frame(maxWidth: .infinity, alignment: .leading).padding(14)
                    .background(Palette.sidebar).clipShape(RoundedRectangle(cornerRadius: 9))
                    .overlay(RoundedRectangle(cornerRadius: 9).stroke(Palette.line))
                Text("Private folders, stored on this Mac.").font(.system(size: 12)).foregroundStyle(Palette.muted)
            }
            if let error = store.errorMessage {
                Text(error).font(.system(size: 12)).foregroundStyle(Palette.red600).textSelection(.enabled)
            }
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Button(creating ? "Create" : "Save") {
                    if store.saveFolder(folder) { onSave?(folder.id); dismiss() }
                }.buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
                    .disabled(folder.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }.padding(.top, 12)
        }.padding(28).frame(width: 500).background(Palette.paper).foregroundStyle(Palette.ink).tint(Palette.red600)
    }

    private func iconName(_ icon: String) -> String {
        switch icon {
        case "briefcase.fill": return "Work"
        case "person.2.fill": return "Team"
        case "lightbulb.fill": return "Ideas"
        case "graduationcap.fill": return "Learning"
        case "leaf.fill": return "Leaf"
        case "star.fill": return "Star"
        case "heart.fill": return "Heart"
        case "house.fill": return "Home"
        case "wrench.and.screwdriver.fill": return "Projects"
        default: return "Folder"
        }
    }
}
