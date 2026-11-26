import SwiftUI

/// Approved neutral palette; native component geometry remains defined by the views.
enum Palette {
    static let paper = color(0xFFFFFF)
    static let sidebar = color(0xF5F6F7)
    static let strong = color(0x202328)
    static let ink = color(0x202328)

    static let gray100 = color(0xE8EBEF)
    static let gray200 = color(0xD7DBE0)
    static let gray300 = color(0xBFC0C1)
    static let gray400 = color(0xAAABAD)
    static let gray500 = color(0x97999B)
    static let gray600 = color(0x797A7C)
    static let gray700 = color(0x565D66)

    static let red100 = color(0xFDDBDF)
    static let red200 = color(0xFBB3BB)
    static let red300 = color(0xF88290)
    static let red400 = color(0xF65A6C)
    static let red500 = color(0xF4364C)
    static let red600 = color(0xC32B3D)
    static let red700 = color(0x92202E)

    static let blue100 = color(0xD7EDF7)
    static let blue200 = color(0xAAD9EE)
    static let blue300 = color(0x75C1E4)
    static let blue400 = color(0x48ADDB)
    static let blue500 = color(0x209BD3)
    static let blue600 = color(0x1A7CA9)
    static let blue700 = color(0x135D7F)

    static let teal100 = color(0xD1DEE3)
    static let teal200 = color(0x9EB9C3)
    static let teal300 = color(0x618C9D)
    static let teal400 = color(0x2E677D)
    static let teal500 = color(0x004661)
    static let teal600 = color(0x00384E)
    static let teal700 = color(0x002A3A)

    static let green100 = color(0xD9F6EF)
    static let green200 = color(0xAEECDD)
    static let green300 = color(0x7BDFC7)
    static let green400 = color(0x50D5B5)
    static let green500 = color(0x2ACCA5)
    static let green600 = color(0x22A384)
    static let green700 = color(0x197A63)

    static let gold100 = color(0xF7F2DD)
    static let gold200 = color(0xEEE4B6)
    static let gold300 = color(0xE3D489)
    static let gold400 = color(0xDAC662)
    static let gold500 = color(0xD2B940)
    static let gold600 = color(0xA89433)
    static let gold700 = color(0x7E6F26)

    static let vineyard100 = color(0xEFD2D9)
    static let vineyard200 = color(0xDEA0AF)
    static let vineyard300 = color(0xC8647C)
    static let vineyard400 = color(0xB73252)
    static let vineyard500 = color(0xA7052C)
    static let vineyard600 = color(0x860423)
    static let vineyard700 = color(0x64031A)

    static let muted = gray700
    static let accent = red600
    static let line = gray200
    static let hairline = gray300
    static let rule = gray500
    static let link = color(0x225D88)
    static let linkVisited = teal500
    static let selection = color(0xE7F0F8)
    static let transcriptMe = color(0xFDECEF)
    static let transcriptOthers = color(0xEFF1F3)
    static let speakerColors = [color(0x225D88), color(0x356851), color(0x146A59), color(0x6B588A), red600, gray700]

    struct FolderColors {
        let foreground: Color
        let background: Color
    }

    static func folderColors(for folder: MeetingFolder) -> FolderColors {
        folderColors(for: folder.color ?? .blue)
    }

    static func folderColors(for folderColor: FolderColor) -> FolderColors {
        switch folderColor {
        case .blue: FolderColors(foreground: color(0x1D5AA6), background: color(0xDAE9FB))
        case .green: FolderColors(foreground: color(0x356851), background: color(0xE0EDDA))
        case .purple: FolderColors(foreground: color(0x6940A4), background: color(0xE9DDFA))
        case .orange: FolderColors(foreground: color(0x934B12), background: color(0xFCE6CF))
        case .pink: FolderColors(foreground: color(0x9B3563), background: color(0xF8DEEA))
        case .teal: FolderColors(foreground: color(0x146A70), background: color(0xD5EEF0))
        case .red: FolderColors(foreground: color(0xA12C32), background: color(0xFADDDD))
        case .yellow: FolderColors(foreground: color(0x7A610C), background: color(0xFAF0C8))
        case .brown: FolderColors(foreground: color(0x795039), background: color(0xEEDFD4))
        case .gray: FolderColors(foreground: color(0x555D68), background: color(0xE5E8EC))
        }
    }

    private static func color(_ hex: UInt32) -> Color {
        Color(.sRGB, red: Double((hex >> 16) & 255) / 255,
              green: Double((hex >> 8) & 255) / 255, blue: Double(hex & 255) / 255, opacity: 1)
    }
}
