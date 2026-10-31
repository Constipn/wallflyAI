import SwiftUI
import AppKit

typealias ViewState<Value> = SwiftUI.State<Value>

struct MeetingWorkspace: View {
    @ObservedObject var store: MeetingStore
    @ViewState private var search = ""
    @ViewState private var showTranscript = false
    @ViewState private var transcriptClicks = OutsideClickMonitor()
    @ViewState private var showRecordingInfo = false
    @ViewState private var childModalOpen = false
    @ViewState private var noteTab = NoteTab.notes
    @ViewState private var transcriptDragStart: CGFloat?
    @ViewState private var transcriptBoxHeight: CGFloat = 0
    @ViewState private var editorHeaderHeight: CGFloat = 0
    /// 0 = automatic height; otherwise the height the user dragged the transcript box to.
    @AppStorage("transcriptHeight") private var storedTranscriptHeight: Double = 0
    @ViewState private var confirmDelete = false
    @ViewState private var deleteTarget: UUID?
    @ViewState private var renameTarget: UUID?
    @ViewState private var renameTitle = ""
    @ViewState private var showRename = false
    @ViewState private var showSettings = false
    @ViewState private var settingsCategory = MeetingSettingsCategory.recording
    @ViewState private var tasksSelected = false
    @ViewState private var focusMode = false
    @ViewState private var searchEverywhere = false
    @ViewState private var searchResults: [MeetingSearchResult] = []
    @ViewState private var searching = false
    @FocusState private var searchFocused: Bool

    private var hasQuery: Bool { !MeetingSearch.terms(search).isEmpty }
    private var searchMeetings: [Meeting] { searchEverywhere ? store.meetings : store.visibleMeetings }
    private var scopeName: String { hasQuery && searchEverywhere ? "All meetings" : store.selectedFolder?.name ?? (store.libraryScope == .unfiled ? "No folder" : "All meetings") }
    private var showingTasks: Bool { tasksSelected && store.workspacePresentation == .overview }
    private struct SearchInput: Equatable {
        let query: String
        let meetings: [Meeting]
    }

    enum NoteTab { case notes, enhanced, transcript }

    init(store: MeetingStore, showTranscript: Bool = false, showEnhanced: Bool? = nil, noteTab: NoteTab? = nil, searchQuery: String = "", tasksSelected: Bool = false, focusMode: Bool? = nil) {
        self.store = store
        _focusMode = ViewState(initialValue: focusMode ?? (store.workspacePresentation == .editor))
        _tasksSelected = ViewState(initialValue: tasksSelected)
        _search = ViewState(initialValue: searchQuery)
        _searchResults = ViewState(initialValue: searchQuery.isEmpty ? [] : MeetingSearch.search(store.visibleMeetings, query: searchQuery))
        _showTranscript = ViewState(initialValue: showTranscript)
        _noteTab = ViewState(initialValue: store.requestedPersonSource != nil ? .transcript : noteTab ?? showEnhanced.map { $0 ? .enhanced : .notes } ?? Self.openingTab(for: store.selectedMeeting, live: store.selectedMeeting.map { Self.isLive($0, in: store) } ?? false))
    }

    private var busy: Bool { store.busy }
    private var filteredMeetings: [Meeting] {
        guard hasQuery else { return store.visibleMeetings }
        let byID = Dictionary(uniqueKeysWithValues: searchMeetings.map { ($0.id, $0) })
        return searchResults.compactMap { byID[$0.id] }
    }

    /// Narrow windows trade width for height: the transcript share grows and the 300 pt cap fades out.
    static func transcriptHeight(for size: CGSize) -> CGFloat {
        let t = min(max((800 - size.width) / 360, 0), 1)
        let share = 0.42 + 0.16 * t
        let cap = 300 + t * size.height
        return min(cap, size.height * share)
    }

    /// Below the editor header a dragged box still leaves room for the note actions, some writing space, the gap and the recording bar.
    static let editorChrome: CGFloat = 160

    /// A dragged height wins over the automatic one, clamped so the editor header (at most 240 pt) stays unclipped.
    static func transcriptHeight(for size: CGSize, preferred: CGFloat?, header: CGFloat = 240) -> CGFloat {
        guard let preferred else { return max(120, transcriptHeight(for: size)) }
        return min(max(preferred, 120), max(120, size.height - header - editorChrome))
    }
    /// Measured header height; until it is known, assume the tallest header.
    private var measuredHeader: CGFloat { editorHeaderHeight > 0 ? editorHeaderHeight : 240 }

    /// Finished meetings with enhanced notes open on them; live meetings and everything else open on the raw notes.
    static func openingTab(for meeting: Meeting?, live: Bool = false) -> NoteTab { live || (meeting?.enhancedNotes.isEmpty ?? true) ? .notes : .enhanced }
    private static func isLive(_ meeting: Meeting, in store: MeetingStore) -> Bool {
        store.activeID == meeting.id && (store.isRecording || store.isStarting || store.isStopping)
    }
    private var selectedOpeningTab: NoteTab { Self.openingTab(for: store.selectedMeeting, live: store.selectedMeeting.map { Self.isLive($0, in: store) } ?? false) }

    private var preferredTranscriptHeight: CGFloat? { storedTranscriptHeight > 0 ? CGFloat(storedTranscriptHeight) : nil }

    /// The transcript tab exists only for finished meetings; live meetings keep the box below.
    private func transcriptTabAvailable(_ meeting: Meeting) -> Bool {
        !meeting.transcript.isEmpty && !Self.isLive(meeting, in: store)
    }
    private func currentTab(_ meeting: Meeting) -> NoteTab { noteTab == .transcript && !transcriptTabAvailable(meeting) ? .notes : noteTab }

    var body: some View {
        HSplitView {
            navigation
                .frame(minWidth: focusMode ? 0 : 180, idealWidth: focusMode ? 0 : 200, maxWidth: focusMode ? 0 : 260)
                .clipped().accessibilityHidden(focusMode)
            library
                .frame(minWidth: focusMode || showingTasks ? 0 : 280, idealWidth: focusMode || showingTasks ? 0 : 320, maxWidth: focusMode || showingTasks ? 0 : 440)
                .clipped().accessibilityHidden(focusMode || showingTasks)
            VStack(spacing: 0) {
                toolbar
                Rectangle().fill(Palette.line).frame(height: 1)
                if !store.showingOverview, let meeting = store.selectedMeeting {
                    GeometryReader { geometry in
                      VStack(spacing: 0) {
                        editor(meeting, compact: geometry.size.width < 600).frame(maxWidth: .infinity, maxHeight: .infinity)
                        if showTranscript && currentTab(meeting) != .transcript {
                            // Automatic: flexible as before. Dragged: fixed (min == max), clamped below the measured header.
                            let dragged = preferredTranscriptHeight.map { Self.transcriptHeight(for: geometry.size, preferred: $0, header: measuredHeader) }
                            transcript(meeting).frame(minHeight: dragged ?? 120, maxHeight: dragged ?? Self.transcriptHeight(for: geometry.size))
                                .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { transcriptBoxHeight = $0 }
                                .overlay(alignment: .top) { transcriptResizeHandle(height: transcriptBoxHeight > 0 ? transcriptBoxHeight : dragged ?? Self.transcriptHeight(for: geometry.size, preferred: nil), size: geometry.size) }
                                .closesOnOutsideClick(transcriptClicks) { withAnimation(.easeInOut(duration: 0.18)) { showTranscript = false } }
                                .background(.white)
                                .clipShape(RoundedRectangle(cornerRadius: 22))
                                .overlay(RoundedRectangle(cornerRadius: 22).stroke(Palette.line).allowsHitTesting(false))
                                .padding(.horizontal, 25).padding(.top, 10)
                        }
                        recordingBar
                      }
                    }.frame(maxWidth: 980).frame(maxWidth: .infinity)
                } else if showingTasks {
                    TaskOverviewView(store: store, openSettings: { openSettings(.intelligence) })
                } else if let meeting = store.selectedMeeting, !hasQuery || searchResults.contains(where: { $0.id == meeting.id }) {
                    MeetingPreview(store: store, meeting: meeting, query: hasQuery ? search : "",
                        result: hasQuery ? searchResults.first(where: { $0.id == meeting.id }) : nil).id(meeting.id)
                } else {
                    WorkspaceEmptyState(icon: "doc.text.magnifyingglass", title: "Room for good thoughts",
                        detail: hasQuery ? "Select a search result to read the match in context." : "Select a meeting or create a new note.")
                    Spacer()
                }
                if let message = store.errorMessage {
                    HStack(alignment: .top, spacing: 10) {
                        Image(systemName: "exclamationmark.circle.fill").foregroundStyle(Palette.red600)
                        Text(message).font(.system(size: 12)).textSelection(.enabled)
                        Spacer(minLength: 8)
                        switch store.currentErrorRecovery {
                        case .openSettings:
                            Button("Open settings") { openSettings(.intelligence) }.buttonStyle(.link).font(.system(size: 12))
                        case .retryEnhancement(let id):
                            Button("Try again") { store.errorMessage = nil; Task { await store.enhanceNotes(meetingID: id) } }
                                .buttonStyle(.link).font(.system(size: 12)).disabled(busy || store.enhancementConfigurationIssue != nil)
                        case .retryTaskExtraction(let id):
                            Button("Try again") { store.errorMessage = nil; Task { await store.retryTaskExtraction(meetingID: id) } }
                                .buttonStyle(.link).font(.system(size: 12)).disabled(busy)
                        case nil: EmptyView()
                        }
                        Button { store.errorMessage = nil } label: { Image(systemName: "xmark") }.buttonStyle(.plain)
                            .help("Dismiss message").accessibilityLabel("Dismiss message")
                    }
                    .padding(14).background(Palette.gray100)
                }
                if let entry = store.transcriptMirrorErrors.sorted(by: { $0.key.uuidString < $1.key.uuidString }).first {
                    HStack(alignment: .top, spacing: 10) {
                        Image(systemName: "exclamationmark.triangle").foregroundStyle(Palette.red600)
                        Text(entry.value).font(Design.metadata).fixedSize(horizontal: false, vertical: true)
                        Spacer(minLength: 4)
                        Button("Try again") { store.retryTranscriptMirror(entry.key) }.buttonStyle(.link)
                    }.padding(14).background(Palette.gray100)
                }
            }.frame(minWidth: 360).background(Palette.paper)
        }
        .environment(\.locale, Locale(identifier: "en_GB"))
        .font(Design.body)
        .task(id: SearchInput(query: search, meetings: hasQuery ? searchMeetings : [])) {
            guard hasQuery else { searchResults = []; searching = false; return }
            searching = true
            do { try await Task.sleep(for: .milliseconds(180)) } catch { return }
            let meetings = searchMeetings
            let query = search
            let worker = Task.detached(priority: .userInitiated) { MeetingSearch.search(meetings, query: query) }
            let results = await withTaskCancellationHandler { await worker.value } onCancel: { worker.cancel() }
            guard !Task.isCancelled else { return }
            searchResults = results
            searching = false
            if store.workspacePresentation != .editor, let first = results.first,
               !results.contains(where: { $0.id == store.selectedID }) { store.previewMeeting(first.id) }
        }
        .background {
            Button("Search meetings") { focusMode = false; tasksSelected = false; searchFocused = true }
                .keyboardShortcut("f").hidden()
        }
        .foregroundStyle(Palette.ink)
        .tint(Palette.red600)
        .sheet(isPresented: $showSettings) { MeetingSettingsView(store: store, category: settingsCategory) }
        .focusedSceneValue(\.meetingMenu, menuActions)
        .onPreferenceChange(ChildModalPresentedKey.self) { childModalOpen = $0 }
        .sheet(item: $store.taskReviewMeeting) { meeting in PersonalTaskReviewView(store: store, meetingID: meeting.id) }
        .confirmationDialog("Delete this meeting?", isPresented: $confirmDelete, titleVisibility: .visible) {
            Button("Delete meeting", role: .destructive) {
                if let id = deleteTarget { store.deleteMeeting(id) }
                deleteTarget = nil
            }
        } message: { Text("Notes, transcript, recordings and tasks of this meeting will be deleted.") }
        .alert("Rename meeting", isPresented: $showRename) {
            TextField("Meeting name", text: $renameTitle)
            Button("Cancel", role: .cancel) { renameTarget = nil }
            Button("Rename") {
                if let id = renameTarget { store.renameMeeting(id, title: renameTitle) }
                renameTarget = nil
            }.disabled(renameTitle.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        }
        .onChange(of: store.workspacePresentation) { _, presentation in
            if presentation != .overview { tasksSelected = false }
            if presentation == .editor { focusMode = true }
        }
        .onChange(of: store.libraryScope) { _, _ in searchEverywhere = false }
        .onChange(of: store.requestedPersonSource) { _, source in if source != nil { noteTab = .transcript; showTranscript = false } }
        .onChange(of: store.selectedID) { _, _ in noteTab = store.requestedPersonSource == nil ? selectedOpeningTab : .transcript; showTranscript = false; showRecordingInfo = false }
        .onChange(of: store.notePresentationID) { _, _ in noteTab = store.requestedPersonSource == nil ? selectedOpeningTab : .transcript; showTranscript = false; focusMode = true }
        .onChange(of: store.selectedMeeting.map(transcriptTabAvailable) ?? false) { _, available in if !available && noteTab == .transcript { noteTab = .notes } }
        .onChange(of: store.showingOverview) { _, overview in if !overview { showTranscript = false } }
        .onChange(of: store.enhancingID) { previous, current in
            if current == nil, previous == store.selectedID, !(store.selectedMeeting?.enhancedNotes.isEmpty ?? true) { noteTab = .enhanced }
        }
    }

    private var navigation: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack(spacing: 8) {
                Image(nsImage: NSApplication.shared.applicationIconImage).resizable().interpolation(.high).frame(width: 22, height: 22)
                Text("Wallfly").font(.system(size: 17, weight: .semibold, design: .serif))
            }.padding(.horizontal, 16).padding(.top, 34)
            Button { tasksSelected = false; Task { await store.newNote() } } label: {
                HStack { Image(systemName: "plus"); Text("New note"); Spacer(); Text("⌘N").font(Design.metadata).foregroundStyle(Palette.muted) }
                    .padding(10).background(Palette.paper).clipShape(RoundedRectangle(cornerRadius: Design.radius))
                    .overlay(RoundedRectangle(cornerRadius: Design.radius).stroke(Palette.line))
            }.buttonStyle(.plain).disabled(busy).padding(.horizontal, 12)
            VStack(spacing: 4) {
                navigationButton("Meetings", icon: "rectangle.stack", selected: !showingTasks) {
                    tasksSelected = false; store.showOverview()
                }
                navigationButton("My to-dos", icon: "checklist", selected: showingTasks) {
                    tasksSelected = true; store.showOverview()
                }
            }.padding(.horizontal, 8)
            FolderSidebar(store: store)
            Spacer(minLength: 0)
            if store.activeMeeting != nil {
                Button { tasksSelected = false; store.openActiveMeeting() } label: {
                    Label("Go to current recording", systemImage: "waveform.circle.fill")
                        .font(Design.metadata).foregroundStyle(Palette.red600).padding(10)
                }.buttonStyle(.plain).padding(.horizontal, 8)
            }
            VStack(alignment: .leading, spacing: 14) {
                Button { openSettings(.recording) } label: { Label("Settings", systemImage: "slider.horizontal.3") }.buttonStyle(.plain)
            }.padding(16)
        }.background(Palette.sidebar)
    }

    private func navigationButton(_ title: String, icon: String, selected: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(title, systemImage: icon).font(.system(size: 14, weight: selected ? .semibold : .regular))
                .frame(maxWidth: .infinity, alignment: .leading).padding(10)
                .background(selected ? Palette.paper : .clear).clipShape(RoundedRectangle(cornerRadius: Design.radius))
        }.buttonStyle(.plain).modifier(HoverSurface())
    }

    private var library: some View {
        let filteredMeetings = self.filteredMeetings
        return VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text(hasQuery ? "Search results" : scopeName).font(.system(size: 23, weight: .medium, design: .serif)).lineLimit(2)
                Spacer(minLength: 4)
                Text("\(filteredMeetings.count)").font(Design.metadata).foregroundStyle(Palette.muted)
            }.padding(.horizontal, 20).padding(.top, 32).padding(.bottom, 18)
            HStack(spacing: 8) {
                Image(systemName: "magnifyingglass").foregroundStyle(Palette.muted)
                TextField("Search meetings", text: $search).textFieldStyle(.plain).focused($searchFocused)
                    .onChange(of: search) { _, _ in tasksSelected = false; searchResults = [] }
                    .onExitCommand { search = "" }
                if !search.isEmpty {
                    Button { search = "" } label: { Image(systemName: "xmark.circle.fill") }
                        .buttonStyle(.plain).foregroundStyle(Palette.muted).help("Clear search").accessibilityLabel("Clear search")
                } else { Text("⌘F").foregroundStyle(Palette.muted).font(Design.metadata) }
            }.font(Design.body).padding(10).background(Palette.sidebar)
                .clipShape(RoundedRectangle(cornerRadius: Design.radius))
                .overlay(RoundedRectangle(cornerRadius: Design.radius).stroke(searchFocused ? Palette.gray700 : Palette.line, lineWidth: searchFocused ? 1.5 : 0.5))
                .padding(.horizontal, 16)
            if hasQuery {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Searching in: \(scopeName)").font(Design.metadata).foregroundStyle(Palette.muted)
                    if store.libraryScope != .all {
                        Button(searchEverywhere ? "Search current folder only" : "Search all meetings") { searchEverywhere.toggle() }
                            .buttonStyle(.link).font(Design.metadata)
                    }
                    if searching { ProgressView().controlSize(.small).accessibilityLabel("Searching") }
                }.padding(.horizontal, 20).padding(.top, 12)
            }
            MeetingLibraryList(meetings: filteredMeetings, selectedID: store.selectedID,
                busy: busy, recordingID: store.isRecording ? store.activeID : nil, catalog: store.folderCatalog, canMove: store.folderLibraryAvailable,
                onSelect: store.previewMeeting, onOpen: store.selectMeeting, onRename: requestRename, onDelete: requestDelete,
                onMove: store.moveMeeting, showFolderBadges: store.libraryScope == .all || (hasQuery && searchEverywhere), query: hasQuery ? search : "", results: searchResults)
                .overlay(alignment: .topLeading) {
                    if filteredMeetings.isEmpty && !searching {
                        VStack(alignment: .leading, spacing: 4) {
                            WorkspaceEmptyState(icon: hasQuery ? "magnifyingglass" : "tray", title: hasQuery ? "No results" : "No meetings yet",
                                detail: hasQuery ? "No results for “\(search)” in “\(scopeName)”." : "Create a note or move a meeting into this folder.")
                            if hasQuery { Button("Reset search") { search = "" }.padding(.horizontal, 28) }
                        }
                    }
                }.padding(.horizontal, 8).padding(.top, 16)
        }.background(Palette.paper)
    }

    private var toolbar: some View {
        HStack(spacing: 14) {
            if store.workspacePresentation == .editor {
                Button { focusMode = false; if let id = store.selectedID { store.previewMeeting(id) } } label: {
                    ViewThatFits(in: .horizontal) {
                        Label("Back to preview", systemImage: "chevron.left")
                        Image(systemName: "chevron.left")
                    }
                }.buttonStyle(.plain).help("Back to preview").accessibilityLabel("Back to preview")
                Button { focusMode.toggle() } label: {
                    Image(systemName: focusMode ? "sidebar.left" : "arrow.up.left.and.arrow.down.right")
                }.buttonStyle(.plain).help(focusMode ? "Show library" : "Focus mode")
                    .accessibilityLabel(focusMode ? "Show library" : "Focus mode")
            } else {
                Text(showingTasks ? "My to-dos" : "Preview").font(Design.metadata).foregroundStyle(Palette.muted)
            }
            Spacer()
            if store.isStarting || store.isRecording || store.isStopping {
                Toggle(isOn: $store.presentationMode) {
                    ViewThatFits(in: .horizontal) {
                        Label("Presentation mode", systemImage: "rectangle.slash")
                        Image(systemName: "rectangle.slash")
                    }
                }.toggleStyle(.button).help("Hide floating recording indicator").accessibilityLabel("Presentation mode")
            }
            if !store.showingOverview, store.selectedMeeting != nil {
                Menu {
                    Button("Export Markdown…", systemImage: "square.and.arrow.up") { store.exportSelected() }
                    Button("Show saved files", systemImage: "folder") { store.revealStorage() }
                    Menu("Move to folder") {
                        Button("No folder") { if let id = store.selectedID { store.moveMeeting(id, to: nil) } }
                        ForEach(store.folders) { folder in
                            Button { if let id = store.selectedID { store.moveMeeting(id, to: folder.id) } } label: {
                                Label(folder.name, systemImage: folder.icon)
                            }
                        }
                    }.disabled(!store.folderLibraryAvailable)
                    Divider()
                    Button("Rename meeting…", systemImage: "pencil") { if let id = store.selectedID { requestRename(id) } }.disabled(busy)
                    Button("Delete meeting…", systemImage: "trash", role: .destructive) { if let id = store.selectedID { requestDelete(id) } }.disabled(busy)
                } label: { Image(systemName: "ellipsis") }.menuStyle(.borderlessButton).frame(width: 22)
                    .help("More actions").accessibilityLabel("More actions")
            }
        }.padding(.horizontal, 25).frame(height: 62)
    }

    private func requestRename(_ id: UUID) {
        guard !busy, let meeting = store.meetings.first(where: { $0.id == id }) else { return }
        renameTarget = id
        renameTitle = meeting.title
        showRename = true
    }

    private func requestDelete(_ id: UUID) {
        guard !busy else { return }
        deleteTarget = id
        confirmDelete = true
    }

    private func openSettings(_ category: MeetingSettingsCategory) {
        settingsCategory = category
        showSettings = true
    }

    /// The "Meeting" menu mirrors the editor's buttons: an action is nil exactly when its button is hidden or disabled.
    private var menuActions: MeetingMenuActions? {
        guard !store.showingOverview, let meeting = store.selectedMeeting, !showSettings, !showRename, !confirmDelete,
              store.taskReviewMeeting == nil, !store.showOnboarding, !childModalOpen else { return nil }
        var actions = MeetingMenuActions()
        actions.tabs[.notes] = { noteTab = .notes }
        actions.tabs[.enhanced] = { noteTab = .enhanced }
        if transcriptTabAvailable(meeting) {
            actions.tabs[.transcript] = { noteTab = .transcript; withAnimation(.easeInOut(duration: 0.18)) { showTranscript = false } }
        }
        if !(busy && store.activeID != nil && store.activeID != store.selectedID) {
            if store.isRecording || store.isStopping {
                if !store.isStopping { actions.stopRecording = { Task { await store.stopRecording() } } }
            } else if !busy {
                actions.startRecording = { Task { noteTab = .notes; await store.startRecording() } }
            }
        }
        if currentTab(meeting) != .transcript {
            actions.transcriptShown = showTranscript
            actions.toggleTranscript = { withAnimation(.easeInOut(duration: 0.18)) { showTranscript.toggle() } }
        }
        if meeting.hasTranscriptText, !busy, store.enhancementConfigurationIssue == nil {
            actions.enhanceTitle = meeting.enhancedNotes.isEmpty ? "Enhance notes" : "Enhance again"
            actions.enhance = { Task { await store.enhanceNotes(meetingID: meeting.id) } }
        }
        return actions
    }

    private func editor(_ meeting: Meeting, compact: Bool) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            MeetingHeaderContainer {
                editorHeader(meeting)
            }.onGeometryChange(for: CGFloat.self) { $0.size.height } action: { editorHeaderHeight = $0 }
            Rectangle().fill(Palette.line).frame(height: 1)
            switch currentTab(meeting) {
            case .transcript:
                // TranscriptConversation insets by 23 pt; pull it back so the text lines up with the title.
                transcriptBody(meeting, live: false, menu: true).padding(.top, 18).padding(.horizontal, -23)
            case .enhanced:
                if store.enhancingID == meeting.id {
                    VStack(spacing: 16) {
                        ProgressView().controlSize(.small)
                        Text("Enhancing notes").font(.system(size: 20, design: .serif))
                        Text(store.extractingTasksID == meeting.id ? store.taskProgress : store.enhancementProgress).font(.system(size: 12)).foregroundStyle(Palette.muted).multilineTextAlignment(.center)
                    }.frame(maxWidth: .infinity, maxHeight: .infinity)
                } else if meeting.enhancedNotes.isEmpty {
                    VStack(spacing: 14) {
                        Image(systemName: "sparkles").font(.system(size: 28)).foregroundStyle(Palette.accent)
                        enhancementSetupNotice
                        Text("Capture what matters").font(.system(size: 23, design: .serif))
                        Text("After the meeting, turn your notes and the transcript into a clear summary.")
                            .font(.system(size: 13)).foregroundStyle(Palette.muted).multilineTextAlignment(.center).lineSpacing(5)
                        if meeting.hasTranscriptText {
                            Button("Enhance notes") { Task { await store.enhanceNotes(meetingID: meeting.id) } }.buttonStyle(.borderedProminent).disabled(busy || store.enhancementConfigurationIssue != nil).padding(.top, 6)
                        }
                    }.frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 14) {
                            enhancementSetupNotice
                            markdown(meeting.enhancedNotes)
                            Text("Enhanced with \(meeting.enhancementEngine)").font(.system(size: 12)).foregroundStyle(Palette.muted).padding(.top, 20)
                            Button("Enhance again") { Task { await store.enhanceNotes(meetingID: meeting.id) } }.font(.system(size: 12)).disabled(busy || store.enhancementConfigurationIssue != nil)
                        }.frame(maxWidth: .infinity, alignment: .leading).padding(.vertical, 25)
                    }
                }
            case .notes:
                VStack(alignment: .leading, spacing: 0) {
                    ZStack(alignment: .topLeading) {
                        if meeting.notes.isEmpty {
                            Text("Capture thoughts and notes…").font(.system(size: 16)).foregroundStyle(Palette.muted.opacity(0.8)).padding(.top, 25).padding(.leading, 5).allowsHitTesting(false)
                        }
                        TextEditor(text: Binding(get: { store.meetings.first { $0.id == meeting.id }?.notes ?? "" }, set: { store.updateNotes($0, meetingID: meeting.id) }))
                            .font(.system(size: 14)).lineSpacing(7).scrollContentBackground(.hidden).padding(.top, 18)
                            .opacity(meeting.notes.isEmpty ? 0.72 : 1).disabled(store.enhancingID == meeting.id || store.extractingTasksID == meeting.id)
                    }
                    if meeting.hasTranscriptText {
                        HStack(spacing: 12) {
                            Button {
                                if store.enhancementConfigurationIssue != nil { openSettings(.intelligence) }
                                else { Task { await store.enhanceNotes(meetingID: meeting.id) } }
                            } label: {
                                Label(store.enhancementConfigurationIssue != nil ? "Set up enhancement…" : meeting.enhancedNotes.isEmpty ? "Enhance notes" : "Enhance again", systemImage: "sparkles")
                            }.buttonStyle(.borderedProminent).disabled(busy)
                                .help(store.enhancementConfigurationIssue ?? "Enhance notes and transcript")
                            if store.enhancingID == meeting.id || store.extractingTasksID == meeting.id {
                                ProgressView().controlSize(.small)
                                Text(store.extractingTasksID == meeting.id ? store.taskProgress : store.enhancementProgress)
                                    .font(.system(size: 12)).foregroundStyle(Palette.muted)
                            }
                        }.padding(.vertical, 16)
                    }
                }
            }
        }.padding(.horizontal, compact ? 20 : 38)
    }

    @ViewBuilder private var enhancementSetupNotice: some View {
        if let issue = store.enhancementConfigurationIssue {
            VStack(alignment: .leading, spacing: 6) {
                Text(issue).font(Design.metadata).foregroundStyle(Palette.muted)
                Button("Set up enhancement…") { openSettings(.intelligence) }.buttonStyle(.link)
            }.padding(.vertical, 8)
        }
    }

    private func editorHeader(_ meeting: Meeting) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 7) {
                Image(systemName: "clock")
                Text((meeting.startedAt ?? meeting.createdAt).formatted(.dateTime.day().month(.wide).year().hour().minute().locale(Locale(identifier: "en_GB")))).lineLimit(1)
                if meeting.duration > 0 { Text("·"); Text(Meeting.timestamp(meeting.duration)) }
                MeetingFolderPicker(store: store, meetingID: meeting.id)
            }.font(.system(size: 12)).foregroundStyle(Palette.muted).padding(.top, 28)
            TextField("Untitled meeting", text: Binding(get: { store.meetings.first { $0.id == meeting.id }?.title ?? "" }, set: { store.updateTitle($0, meetingID: meeting.id) }), axis: .vertical)
                .textFieldStyle(.plain).fixedSize(horizontal: false, vertical: true).font(.system(size: 31, weight: .medium, design: .serif)).padding(.top, 14).padding(.bottom, 26).disabled(store.enhancingID == meeting.id || store.extractingTasksID == meeting.id)
            HStack(spacing: 23) {
                let selected = currentTab(meeting)
                tab("My notes", selected: selected == .notes) { noteTab = .notes }
                tab("Enhanced notes", selected: selected == .enhanced) { noteTab = .enhanced }
                if transcriptTabAvailable(meeting) {
                    tab("Transcript", selected: selected == .transcript) {
                        noteTab = .transcript
                        withAnimation(.easeInOut(duration: 0.18)) { showTranscript = false }
                    }
                }
                Spacer()
                if !meeting.enhancedNotes.isEmpty && selected == .enhanced {
                    Image(systemName: "sparkles").foregroundStyle(Palette.accent)
                }
            }
        }.fixedSize(horizontal: false, vertical: true)
    }

    private func tab(_ title: String, selected: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title).font(.system(size: 12, weight: selected ? .semibold : .regular))
                .foregroundStyle(selected ? Palette.ink : Palette.muted)
                .padding(.bottom, 13).fixedSize()
                .overlay(alignment: .bottom) { Rectangle().fill(selected ? Palette.accent : .clear).frame(height: 2) }
        }.buttonStyle(.plain)
    }

    private func transcript(_ meeting: Meeting) -> some View {
        let live = store.activeID == meeting.id && (store.isRecording || store.isStarting)
        return VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("Transcript").font(.system(size: 17, weight: .medium, design: .serif))
                Spacer()
                if live { Text("LIVE").font(.system(size: 12, weight: .bold)).tracking(1).foregroundStyle(Palette.accent).padding(.horizontal, 7).padding(.vertical, 4).background(Palette.gray100).clipShape(Capsule()) }
                else { speakerMenu(meeting) }
                TranscriptCopyButton(meeting: meeting)
                Button { withAnimation(.easeInOut(duration: 0.18)) { showTranscript = false } } label: {
                    Image(systemName: "minus").frame(width: 32, height: 32).contentShape(Rectangle())
                }
                    .buttonStyle(.plain).help("Collapse transcript")
                    .accessibilityLabel("Hide transcript")
                    .accessibilityIdentifier("collapse-transcript")
            }.padding(.horizontal, 23).padding(.vertical, 14)
            transcriptBody(meeting, live: live, menu: false)
        }.background(Color.white.opacity(0.25))
    }

    @ViewBuilder private func speakerMenu(_ meeting: Meeting) -> some View {
        if store.settings.acousticSpeakerRecognition && meeting.transcript.contains(where: { $0.speaker == .others }) {
            Menu {
                Button("Re-detect speakers", systemImage: "person.wave.2") { store.rediarize(meetingID: meeting.id) }
                    .disabled(!store.canRediarize(meeting.id))
            } label: { Image(systemName: "person.wave.2") }
                .menuStyle(.borderlessButton).menuIndicator(.hidden).frame(width: 28)
                .help("Match speakers by their voices")
                .accessibilityLabel("Speaker options")
        }
    }

    /// Speaker legend, diarization status and the conversation, shared by the box and the transcript tab.
    private func transcriptBody(_ meeting: Meeting, live: Bool, menu: Bool) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            if menu {
                HStack {
                    Spacer()
                    TranscriptCopyButton(meeting: meeting)
                    speakerMenu(meeting)
                }.padding(.horizontal, 23).padding(.bottom, 10)
            }
            if !(meeting.transcriptionGaps ?? []).isEmpty {
                Label("The transcript has gaps or unconfirmed audio.", systemImage: "exclamationmark.triangle")
                    .font(Design.metadata).foregroundStyle(Palette.red600)
                    .padding(.horizontal, 23).padding(.bottom, 12)
            }
            if store.settings.teamsSpeakerNames && store.activeID == meeting.id && (store.isRecording || store.isStarting || store.isStopping)
                && [.needsAccessibility, .unavailable, .missingWordTimes].contains(store.teamsSpeakerStatus) {
                Text(store.teamsSpeakerStatusText).font(.system(size: 12)).foregroundStyle(Palette.muted)
                    .padding(.horizontal, 23).padding(.bottom, 12)
            }
            if store.isDiarizing(meeting.id) {
                HStack(spacing: 7) {
                    ProgressView().controlSize(.mini)
                    Text(store.diarizationQueue.first == meeting.id ? store.diarizationProgress : "Waiting for speaker matching…")
                }.font(.system(size: 12)).foregroundStyle(Palette.muted).padding(.horizontal, 23).padding(.bottom, 12)
            } else if meeting.diarization?.status == .failed, let message = meeting.diarization?.message {
                Text("Speaker matching failed · \(message)").font(.system(size: 12)).foregroundStyle(Palette.muted)
                    .lineLimit(2).padding(.horizontal, 23).padding(.bottom, 12)
            }
            Rectangle().fill(Palette.line.opacity(0.7)).frame(height: 1)
            if meeting.transcript.isEmpty {
                ScrollView {
                    VStack(spacing: 12) {
                        Image(systemName: "waveform").font(.system(size: 26, weight: .light)).foregroundStyle(Palette.accent.opacity(0.65))
                        Text(live ? "Recording in progress" : "The conversation, ready to read").font(.system(size: 17, design: .serif))
                        Text(live ? "Detected speech appears here as it comes in." : "Start a recording to capture microphone and meeting audio.")
                            .font(.system(size: 12)).lineSpacing(4).foregroundStyle(Palette.muted).multilineTextAlignment(.center)
                    }.padding(22).frame(maxWidth: .infinity)
                }
            } else {
                TranscriptConversation(store: store, meeting: meeting, live: live).id(meeting.id)
            }
        }
    }

    /// A slim grip along the top edge, inside the box so clicks on it never count as outside clicks.
    private func transcriptResizeHandle(height: CGFloat, size: CGSize) -> some View {
        let resize = { (target: CGFloat) in storedTranscriptHeight = Double(Self.transcriptHeight(for: size, preferred: target, header: measuredHeader)) }
        return Capsule().fill(Palette.gray300).frame(width: 36, height: 4)
            .frame(maxWidth: .infinity).frame(height: 10).contentShape(Rectangle())
            .pointerStyle(.rowResize)
            // Global coordinates: the grip moves with the box edge, so local translations would feed back.
            .gesture(DragGesture(minimumDistance: 1, coordinateSpace: .global)
                .onChanged { value in
                    let start = transcriptDragStart ?? height
                    if transcriptDragStart == nil { transcriptDragStart = start }
                    resize(start - value.translation.height)
                }
                .onEnded { _ in transcriptDragStart = nil })
            .onTapGesture(count: 2) { storedTranscriptHeight = 0 }
            .onDisappear { transcriptDragStart = nil }
            .help("Drag to change the height · Double-click: automatic")
            .accessibilityElement().accessibilityLabel("Adjust transcript height").accessibilityValue("\(Int(height)) pt")
            .accessibilityAdjustableAction { direction in
                switch direction {
                case .increment: resize(height + 40)
                case .decrement: resize(height - 40)
                @unknown default: break
                }
            }
    }

    private var recordingInfo: String {
        var lines = [store.extractingTasksID != nil ? store.taskProgress : store.isEnhancing ? store.enhancementProgress : store.status]
        if !store.diarizationProgress.isEmpty { lines.append(store.diarizationProgress) }
        if store.activeID == store.selectedID && (store.isRecording || store.isStarting || store.isStopping) {
            lines.append([store.recordingAudioDescription, store.actualMicrophoneName, store.captureStatus].filter { !$0.isEmpty }.joined(separator: " · "))
            for speaker in [Speaker.me, .others] {
                if let state = store.transcriptionStates[speaker] {
                    lines.append("\(speaker == .me ? "Me" : "Others"): \(state)")
                }
            }
        }
        if let gaps = store.selectedMeeting?.recordingGaps, !gaps.isEmpty {
            lines.append("Recording gaps: \(gaps.count) · \(String(format: "%.1f", store.selectedMeeting?.recordingGapDuration ?? 0)) s (per source)")
        }
        if let gaps = store.selectedMeeting?.transcriptionGaps, !gaps.isEmpty {
            lines.append("Transcription gaps / unconfirmed audio: \(gaps.count) · \(String(format: "%.1f", gaps.reduce(0) { $0 + $1.duration })) s (per source)")
            if let last = gaps.last { lines.append("From \(Meeting.timestamp(last.start)): \(last.reason)") }
        }
        return lines.filter { !$0.isEmpty }.joined(separator: "\n")
    }

    private var recordingBar: some View {
        VStack(alignment: .leading, spacing: 10) {
            Divider()
            HStack(spacing: 12) {
                if let meeting = store.selectedMeeting, currentTab(meeting) != .transcript {
                    Button { withAnimation(.easeInOut(duration: 0.18)) { showTranscript.toggle() } } label: {
                        Label("Transcript", systemImage: showTranscript ? "chevron.down" : "waveform")
                    }.buttonStyle(.bordered).help(showTranscript ? "Hide transcript" : "Show transcript")
                        .exemptFromOutsideClick(transcriptClicks)
                }
                Button { showRecordingInfo.toggle() } label: {
                    Image(systemName: "info.circle")
                        .font(.system(size: 14)).foregroundStyle(Palette.muted)
                        .frame(width: 24, height: 24).contentShape(Rectangle())
                }.buttonStyle(.plain).help("Recording information")
                    .popover(isPresented: $showRecordingInfo) {
                        Text(recordingInfo)
                            .font(Design.metadata).foregroundStyle(Palette.ink).lineSpacing(4)
                            .fixedSize(horizontal: false, vertical: true)
                            .textSelection(.enabled)
                            .padding(14).frame(width: 340, alignment: .leading)
                            .accessibilityIdentifier("recording-info-popover")
                    }
                    .onDisappear { showRecordingInfo = false }
                    .accessibilityLabel("Recording information")
                    .accessibilityValue(recordingInfo)
                    .accessibilityIdentifier("recording-info")
                Spacer(minLength: 4)
                if busy && store.activeID != nil && store.activeID != store.selectedID {
                    Button("Go to recording") { store.openActiveMeeting() }.buttonStyle(.borderedProminent)
                } else if store.isRecording || store.isStopping {
                    ViewThatFits(in: .horizontal) {
                        HStack(spacing: 12) {
                            if focusMode { RecordingActivityView(activity: store.recordingActivity) }
                            stopButton(iconOnly: false)
                        }
                        HStack(spacing: 12) {
                            if focusMode { RecordingActivityView(activity: store.recordingActivity, compact: true) }
                            stopButton(iconOnly: true)
                        }
                        stopButton(iconOnly: true)
                    }
                } else {
                    ViewThatFits(in: .horizontal) {
                        HStack(spacing: 12) { startButton(iconOnly: false) }
                        HStack(spacing: 12) { startButton(iconOnly: true) }
                    }
                }
            }.font(Design.metadata).controlSize(.regular).padding(.horizontal, 24).padding(.bottom, 16)
        }
    }

    private func stopButton(iconOnly: Bool) -> some View {
        let title = store.isStopping ? "Finishing…" : "Stop recording"
        return Button { Task { await store.stopRecording() } } label: {
            if iconOnly { Image(systemName: "stop.fill") } else { Label(title, systemImage: "stop.fill") }
        }.buttonStyle(.borderedProminent).disabled(store.isStopping).help(title).accessibilityLabel(title)
    }

    private func startButton(iconOnly: Bool) -> some View {
        let title = store.isStarting ? "Starting…" : "Start recording"
        return Button { Task { noteTab = .notes; await store.startRecording() } } label: {
            if iconOnly { Image(systemName: "record.circle") } else { Label(title, systemImage: "record.circle") }
        }.buttonStyle(.borderedProminent).disabled(busy).help(title).accessibilityLabel(title)
    }

    private func markdown(_ text: String) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            ForEach(Array(text.components(separatedBy: "\n").enumerated()), id: \.offset) { _, line in
                if line.hasPrefix("# ") { Text(String(line.dropFirst(2))).font(.system(size: 25, weight: .medium, design: .serif)).padding(.top, 8) }
                else if line.hasPrefix("## ") { Text(String(line.dropFirst(3))).font(.system(size: 20, weight: .medium, design: .serif)).padding(.top, 13) }
                else if line.hasPrefix("### ") { Text(String(line.dropFirst(4))).font(.system(size: 16, weight: .semibold)).padding(.top, 8) }
                else if line.hasPrefix("- ") || line.hasPrefix("* ") {
                    HStack(alignment: .top, spacing: 10) { Text("•"); Text(.init(String(line.dropFirst(2)))).frame(maxWidth: .infinity, alignment: .leading) }.font(.system(size: 13)).lineSpacing(5)
                } else if !line.trimmingCharacters(in: .whitespaces).isEmpty { Text(.init(line)).font(.system(size: 13)).lineSpacing(6).frame(maxWidth: .infinity, alignment: .leading) }
            }
        }.textSelection(.enabled)
    }
}

/// A native list keeps selection on the first click, including when the editor or
/// another application previously had keyboard focus.
struct MeetingLibraryList: NSViewRepresentable {
    var meetings: [Meeting]
    var selectedID: UUID?
    var busy: Bool
    var recordingID: UUID?
    var catalog: FolderCatalog
    var canMove: Bool
    var onSelect: (UUID) -> Void
    var onOpen: (UUID) -> Void
    var onRename: (UUID) -> Void
    var onDelete: (UUID) -> Void
    var onMove: (UUID, UUID?) -> Void
    var showFolderBadges = true
    var query: String = ""
    var results: [MeetingSearchResult] = []

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> NSScrollView {
        let table = MeetingTableView()
        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("meeting"))
        table.addTableColumn(column)
        table.headerView = nil
        table.rowHeight = 94
        table.setDraggingSourceOperationMask(.move, forLocal: true)
        table.intercellSpacing = NSSize(width: 0, height: 0)
        table.backgroundColor = .clear
        table.focusRingType = .exterior
        table.selectionHighlightStyle = .regular
        table.allowsEmptySelection = true
        table.allowsMultipleSelection = false
        table.columnAutoresizingStyle = .lastColumnOnlyAutoresizingStyle
        table.delegate = context.coordinator
        table.dataSource = context.coordinator
        table.target = context.coordinator
        table.action = #selector(Coordinator.previewClicked(_:))
        table.doubleAction = #selector(Coordinator.openDoubleClicked(_:))
        table.openSelection = { [weak coordinator = context.coordinator] row in
            guard let coordinator, coordinator.parent.meetings.indices.contains(row) else { return }
            coordinator.parent.onOpen(coordinator.parent.meetings[row].id)
        }
        table.contextMenu = { [weak coordinator = context.coordinator] row in coordinator?.menu(for: row) }
        table.setAccessibilityLabel("Your meetings")
        let scroll = NSScrollView()
        scroll.drawsBackground = false
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.documentView = table
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let table = scroll.documentView as? NSTableView else { return }
        let coordinator = context.coordinator
        let oldRows = coordinator.parent.meetings.map { RowSnapshot($0, recordingID: coordinator.parent.recordingID) }
        let newRows = meetings.map { RowSnapshot($0, recordingID: recordingID) }
        let searchChanged = coordinator.parent.query != query || coordinator.parent.results != results || coordinator.parent.showFolderBadges != showFolderBadges
        coordinator.parent = self
        coordinator.synchronizing = true
        defer { coordinator.synchronizing = false }
        if oldRows != newRows || searchChanged || coordinator.lastCatalog != catalog || table.numberOfRows != meetings.count {
            table.reloadData()
            table.noteHeightOfRows(withIndexesChanged: IndexSet(integersIn: 0..<meetings.count))
        }
        coordinator.lastCatalog = catalog
        let row = meetings.firstIndex(where: { $0.id == selectedID })
        if let row, table.selectedRow != row {
            table.selectRowIndexes(IndexSet(integer: row), byExtendingSelection: false)
            table.scrollRowToVisible(row)
        } else if row == nil, table.selectedRow != -1 { table.deselectAll(nil) }
    }

    private struct RowSnapshot: Equatable {
        let id: UUID
        let title: String
        let date: Date
        let recording: Bool
        init(_ meeting: Meeting, recordingID: UUID?) {
            id = meeting.id; title = meeting.title; date = meeting.startedAt ?? meeting.createdAt
            recording = meeting.id == recordingID
        }
    }

    final class Coordinator: NSObject, NSTableViewDataSource, NSTableViewDelegate {
        var parent: MeetingLibraryList
        var synchronizing = false
        var lastCatalog = FolderCatalog()
        init(_ parent: MeetingLibraryList) { self.parent = parent }
        func numberOfRows(in tableView: NSTableView) -> Int { parent.meetings.count }
        func selectionShouldChange(in tableView: NSTableView) -> Bool { true }
        func tableView(_ tableView: NSTableView, heightOfRow row: Int) -> CGFloat {
            guard parent.meetings.indices.contains(row) else { return 94 }
            let count = parent.query.isEmpty ? 0 : min(2, parent.results.first { $0.id == parent.meetings[row].id }?.hits.count ?? 0)
            return 94 + CGFloat(count) * 70
        }
        func tableViewSelectionDidChange(_ notification: Notification) {
            // Mouse clicks preview through the table's action after mouse-up,
            // keeping selection changes separate from drag initiation.
            guard NSApp.currentEvent?.type == .keyDown, !synchronizing,
                  let table = notification.object as? NSTableView else { return }
            previewClicked(table)
        }
        @objc func previewClicked(_ table: NSTableView) {
            guard !synchronizing, parent.meetings.indices.contains(table.selectedRow) else { return }
            parent.onSelect(parent.meetings[table.selectedRow].id)
        }
        func tableView(_ tableView: NSTableView, rowViewForRow row: Int) -> NSTableRowView? { MeetingTableRowView() }
        func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
            guard parent.meetings.indices.contains(row) else { return nil }
            let identifier = NSUserInterfaceItemIdentifier("meeting-cell")
            let cell = tableView.makeView(withIdentifier: identifier, owner: nil) as? MeetingTableCell ?? MeetingTableCell()
            cell.identifier = identifier
            let meeting = parent.meetings[row]
            let folderID = parent.catalog.folderID(for: meeting.id)
            let folder = parent.catalog.folders.first { $0.id == folderID }
            let result = parent.results.first { $0.id == meeting.id }
            cell.host.rootView = MeetingLibraryRow(meeting: meeting, folder: parent.showFolderBadges ? folder : nil,
                recording: parent.recordingID == meeting.id, query: parent.query, result: result)
            cell.toolTip = meeting.title + (folder.map { " · " + $0.name } ?? "")
            cell.setAccessibilityLabel("\(meeting.title), \(folder?.name ?? "No folder")")
            return cell
        }
        func tableView(_ tableView: NSTableView, pasteboardWriterForRow row: Int) -> NSPasteboardWriting? {
            guard parent.canMove, parent.meetings.indices.contains(row) else { return nil }
            let item = NSPasteboardItem()
            item.setString(parent.meetings[row].id.uuidString, forType: MeetingDrag.pasteboardType)
            return item
        }
        func menu(for row: Int) -> NSMenu? {
            guard parent.meetings.indices.contains(row) else { return nil }
            let id = parent.meetings[row].id
            let menu = NSMenu()
            menu.autoenablesItems = false
            for (title, action) in [("Rename meeting…", #selector(renameFromMenu(_:))), ("Delete meeting…", #selector(deleteFromMenu(_:)))] {
                let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
                item.target = self
                item.representedObject = id
                item.isEnabled = !parent.busy
                menu.addItem(item)
            }
            let move = NSMenuItem(title: "Move to folder", action: nil, keyEquivalent: "")
            let submenu = NSMenu()
            submenu.autoenablesItems = false
            let destinations: [(UUID?, String, String)] = [(nil, "No folder", "tray")] + parent.catalog.folders.map { ($0.id, $0.name, $0.icon) }
            for (folderID, name, icon) in destinations {
                let item = NSMenuItem(title: name, action: #selector(moveFromMenu(_:)), keyEquivalent: "")
                item.target = self
                item.representedObject = MoveDestination(meetingID: id, folderID: folderID)
                item.image = NSImage(systemSymbolName: icon, accessibilityDescription: nil)
                item.state = parent.catalog.folderID(for: id) == folderID ? .on : .off
                item.isEnabled = parent.canMove
                submenu.addItem(item)
            }
            move.submenu = submenu
            move.isEnabled = parent.canMove
            menu.addItem(.separator())
            menu.addItem(move)
            return menu
        }
        @objc func openDoubleClicked(_ table: NSTableView) {
            guard !synchronizing, parent.meetings.indices.contains(table.clickedRow) else { return }
            parent.onOpen(parent.meetings[table.clickedRow].id)
        }
        @objc private func renameFromMenu(_ sender: NSMenuItem) {
            guard !parent.busy, let id = sender.representedObject as? UUID else { return }
            parent.onRename(id)
        }
        @objc private func deleteFromMenu(_ sender: NSMenuItem) {
            guard !parent.busy, let id = sender.representedObject as? UUID else { return }
            parent.onDelete(id)
        }
        @objc private func moveFromMenu(_ sender: NSMenuItem) {
            guard parent.canMove, let destination = sender.representedObject as? MoveDestination else { return }
            parent.onMove(destination.meetingID, destination.folderID)
        }
    }
}

private final class MoveDestination: NSObject {
    let meetingID: UUID
    let folderID: UUID?
    init(meetingID: UUID, folderID: UUID?) { self.meetingID = meetingID; self.folderID = folderID }
}

private final class MeetingTableView: NSTableView {
    var contextMenu: ((Int) -> NSMenu?)?
    var openSelection: ((Int) -> Void)?
    override func keyDown(with event: NSEvent) {
        if event.keyCode == 36 || event.keyCode == 76 { openSelection?(selectedRow) }
        else { super.keyDown(with: event) }
    }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with event: NSEvent) {
        // Commit any title field / notes editor before selecting the next meeting.
        window?.makeFirstResponder(self)
        super.mouseDown(with: event)
    }
    override func menu(for event: NSEvent) -> NSMenu? {
        contextMenu?(row(at: convert(event.locationInWindow, from: nil)))
    }
}

private final class MeetingTableRowView: NSTableRowView {
    override func drawBackground(in dirtyRect: NSRect) {
        super.drawBackground(in: dirtyRect)
        NSColor(Palette.gray100).setFill()
        NSRect(x: 10, y: bounds.maxY - 0.75, width: max(0, bounds.width - 20), height: 0.75).fill()
    }
    override func drawSelection(in dirtyRect: NSRect) {
        NSColor(Palette.selection).setFill()
        NSBezierPath(roundedRect: bounds.insetBy(dx: 1, dy: 0.75), xRadius: 8, yRadius: 8).fill()
    }
}

private final class MeetingTableCell: NSTableCellView {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { super.hitTest(point) == nil ? nil : self }
    private var table: NSTableView? {
        var ancestor = superview
        while let view = ancestor {
            if let table = view as? NSTableView { return table }
            ancestor = view.superview
        }
        return nil
    }
    override func mouseDown(with event: NSEvent) { table?.mouseDown(with: event) }
    override func menu(for event: NSEvent) -> NSMenu? { table?.menu(for: event) }
    let host = NSHostingView(rootView: MeetingLibraryRow(meeting: Meeting(), folder: nil, recording: false))
    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        host.translatesAutoresizingMaskIntoConstraints = false
        addSubview(host)
        NSLayoutConstraint.activate([
            host.leadingAnchor.constraint(equalTo: leadingAnchor),
            host.trailingAnchor.constraint(equalTo: trailingAnchor),
            host.topAnchor.constraint(equalTo: topAnchor),
            host.bottomAnchor.constraint(equalTo: bottomAnchor)
        ])
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}

private struct MeetingLibraryRow: View {
    let meeting: Meeting
    let folder: MeetingFolder?
    let recording: Bool
    var query = ""
    var result: MeetingSearchResult?
    var body: some View {
        VStack(alignment: .leading, spacing: 7) {
            HStack(alignment: .top, spacing: 6) {
                HighlightedText(text: meeting.title.isEmpty ? "Untitled meeting" : meeting.title, query: query)
                    .font(.system(size: 14, weight: .medium)).lineLimit(2)
                    .frame(maxWidth: .infinity, alignment: .leading)
                if recording { Circle().fill(Palette.accent).frame(width: 7, height: 7).padding(.top, 5).accessibilityLabel("Recording in progress") }
            }
            HStack(spacing: 8) {
                let date = meeting.startedAt ?? meeting.createdAt
                let locale = Locale(identifier: "en_GB")
                Text("\(date.formatted(.dateTime.day().month(.abbreviated).locale(locale))) · \(date.formatted(.dateTime.hour().minute().locale(locale)))")
                    .font(Design.metadata).foregroundStyle(Palette.muted).fixedSize()
                if let folder { FolderBadge(folder: folder) }
            }
            if !query.isEmpty, let result {
                ForEach(Array(result.hits.prefix(2))) { hit in
                    VStack(alignment: .leading, spacing: 4) {
                        Text(hit.passage.label).font(.system(size: 12, weight: .medium)).foregroundStyle(Palette.muted)
                        HighlightedText(text: hit.snippet, query: query).font(Design.metadata).lineLimit(2)
                    }.frame(height: 63, alignment: .topLeading)
                }
            }
        }.padding(12).frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .foregroundStyle(Palette.ink)
    }
}

/// Fit the complete header first; only exceptionally long titles need their own scroll area.
struct MeetingHeaderContainer<Content: View>: View {
    @ViewBuilder var content: Content
    var body: some View {
        ViewThatFits(in: .vertical) {
            content.fixedSize(horizontal: false, vertical: true)
            ScrollView { content }
                .frame(height: 240)
        }
        .frame(maxHeight: 240, alignment: .top)
        .fixedSize(horizontal: false, vertical: true)
        .layoutPriority(1)
    }
}

private struct RecordingActivityView: View {
    @ObservedObject var activity: RecordingActivity
    var compact = false
    var body: some View {
        HStack(spacing: 16) {
            HStack(spacing: 8) {
                Circle().fill(Palette.accent).frame(width: 7, height: 7)
                Text(Meeting.timestamp(activity.elapsed)).font(.system(size: 13, weight: .medium, design: .monospaced))
            }
            if !compact {
                meter("Microphone", icon: "mic", level: activity.meLevel)
                meter("Meeting audio", icon: "waveform", level: activity.othersLevel)
            }
        }
    }

    private func meter(_ title: String, icon: String, level: Double) -> some View {
        HStack(spacing: 6) {
            Image(systemName: icon).font(.system(size: 12)).foregroundStyle(Palette.muted)
            HStack(spacing: 2) {
                ForEach(0..<8) { i in
                    RoundedRectangle(cornerRadius: 1).fill(level > Double(i) / 8 ? Palette.accent : Palette.line).frame(width: 3, height: 5 + CGFloat(i % 3) * 3)
                }
            }.frame(height: 14)
        }.help(title).accessibilityLabel("\(title), audio level \(Int(level * 100)) percent")
    }

}

/// Actions of the open meeting for the "Meeting" menu; nil means unavailable.
struct MeetingMenuActions {
    var startRecording: (() -> Void)?
    var stopRecording: (() -> Void)?
    var enhanceTitle = "Enhance notes"
    var enhance: (() -> Void)?
    var transcriptShown = false
    var toggleTranscript: (() -> Void)?
    var tabs: [MeetingWorkspace.NoteTab: () -> Void] = [:]
}

private struct MeetingMenuKey: FocusedValueKey { typealias Value = MeetingMenuActions }

/// Child views report an open sheet/dialog so the Meeting menu stays disabled behind it.
struct ChildModalPresentedKey: PreferenceKey {
    static let defaultValue = false
    static func reduce(value: inout Bool, nextValue: () -> Bool) { value = value || nextValue() }
}
extension FocusedValues {
    var meetingMenu: MeetingMenuActions? {
        get { self[MeetingMenuKey.self] }
        set { self[MeetingMenuKey.self] = newValue }
    }
}

struct MeetingMenuCommands: View {
    @FocusedValue(\.meetingMenu) private var actions

    var body: some View {
        Button("Start recording") { actions?.startRecording?() }
            .keyboardShortcut("r", modifiers: [.command, .shift]).disabled(actions?.startRecording == nil)
        Button("Stop recording") { actions?.stopRecording?() }
            .keyboardShortcut("e", modifiers: [.command, .shift]).disabled(actions?.stopRecording == nil)
        Divider()
        Button(actions?.enhanceTitle ?? "Enhance notes") { actions?.enhance?() }
            .keyboardShortcut("a", modifiers: [.command, .shift]).disabled(actions?.enhance == nil)
        Button(actions?.transcriptShown == true ? "Hide transcript" : "Show transcript") { actions?.toggleTranscript?() }
            .keyboardShortcut("t", modifiers: [.command, .shift]).disabled(actions?.toggleTranscript == nil)
        Divider()
        tab("My notes", .notes, key: "1")
        tab("Enhanced notes", .enhanced, key: "2")
        tab("Transcript", .transcript, key: "3")
    }

    private func tab(_ title: String, _ tab: MeetingWorkspace.NoteTab, key: KeyEquivalent) -> some View {
        Button(title) { actions?.tabs[tab]?() }.keyboardShortcut(key).disabled(actions?.tabs[tab] == nil)
    }
}
