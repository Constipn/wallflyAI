import Foundation

enum FolderColor: String, Codable, CaseIterable {
    case blue, green, purple, orange, pink, teal, red, yellow, brown, gray

    var name: String {
        switch self {
        case .blue: "Blue"
        case .green: "Green"
        case .purple: "Purple"
        case .orange: "Orange"
        case .pink: "Pink"
        case .teal: "Teal"
        case .red: "Red"
        case .yellow: "Yellow"
        case .brown: "Brown"
        case .gray: "Gray"
        }
    }
}

struct MeetingFolder: Identifiable, Codable, Equatable {
    var id = UUID()
    var name: String = ""
    var icon: String = "folder.fill"
    var description: String = ""
    var color: FolderColor?

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

    var nextColor: FolderColor {
        FolderColor.allCases.min { lhs, rhs in
            folders.filter { $0.color == lhs }.count < folders.filter { $0.color == rhs }.count
        } ?? .blue
    }

    /// Fill legacy entries in catalog order, retaining every already assigned color.
    mutating func assignMissingColors() {
        for index in folders.indices where folders[index].color == nil {
            folders[index].color = nextColor
        }
    }

    func folderID(for meetingID: UUID) -> UUID? {
        guard let id = assignments[meetingID.uuidString], folders.contains(where: { $0.id == id }) else { return nil }
        return id
    }
}
