import Foundation

struct MeetingFolder: Identifiable, Codable, Equatable {
    var id = UUID()
    var name: String = ""
    var icon: String = "folder.fill"
    var description: String = ""

    static let icons = ["folder.fill", "briefcase.fill", "person.2.fill", "lightbulb.fill",
        "graduationcap.fill", "leaf.fill", "star.fill", "heart.fill", "house.fill", "wrench.and.screwdriver.fill"]
}

enum MeetingLibraryScope: Equatable {
    case all
    case unfiled
    case folder(UUID)
}

/// Folder edits and membership changes commit together in one atomic file.
/// Original meeting JSON/audio files are independent of this organization catalog.
struct FolderCatalog: Codable, Equatable {
    var folders: [MeetingFolder] = []
    var assignments: [String: UUID] = [:]

    func folderID(for meetingID: UUID) -> UUID? {
        guard let id = assignments[meetingID.uuidString], folders.contains(where: { $0.id == id }) else { return nil }
        return id
    }
}
