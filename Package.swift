// swift-tools-version: 6.2
import PackageDescription
import Foundation
let aecRoot = "Sources/WebRTCAEC"
func sources(in folder: String) -> [String] {
    FileManager.default.enumerator(atPath: URL(fileURLWithPath: #filePath).deletingLastPathComponent().appendingPathComponent("\(aecRoot)/\(folder)").path)!.allObjects
        .compactMap { $0 as? String }.filter {
            ($0.hasSuffix(".cc") || $0.hasSuffix(".c") || $0.hasSuffix(".cpp")) &&
            !$0.hasSuffix("_avx2.cc") && !$0.hasSuffix("_sse2.cc") && !$0.hasSuffix("_sse.cc")
        }.sorted().map { "\(folder)/\($0)" }
}
let aecSources = sources(in: "vendor") + sources(in: "vendor-ns")
let package = Package(
    name: "MeetingNotes",
    platforms: [.macOS(.v26)],
    products: [
        .executable(name: "wallfly-mcp", targets: ["WallflyMCP"]),
        .executable(name: "MeetingNotes", targets: ["MeetingNotes"]),
        .executable(name: "MeetingNotesDetector", targets: ["MeetingNotesDetector"])
    ],
    dependencies: [
        .package(url: "https://github.com/modelcontextprotocol/swift-sdk.git", exact: "0.12.1"),
        // Offline speaker diarization (Phase 3). FluidAudio 0.17.7 has no "Diarizer" trait; its only
        // trait (default on) links the NeMo text-normalization binary, which diarization does not need.
        .package(url: "https://github.com/FluidInference/FluidAudio.git", exact: "0.17.7", traits: [])
    ],
    targets: [
        .target(name: "WallflyReadCore"),
        .executableTarget(name: "WallflyMCP", dependencies: ["WallflyReadCore", .product(name: "MCP", package: "swift-sdk")]),
        .target(name: "SafeAudio", publicHeadersPath: "include"),
        .target(name: "WebRTCAEC", exclude: ["LICENSE", "JSONCPP-LICENSE", "PROVENANCE.md", "SHA256SUMS", "PROVENANCE-NS.md", "SHA256SUMS-NS",
                    "vendor-ns/LICENSE", "vendor-ns/PATENTS", "vendor-ns/modules/audio_processing/ns/BUILD.gn"],
                sources: ["AECBridge.cpp"] + aecSources, publicHeadersPath: "include",
                cxxSettings: [.define("WEBRTC_MAC"), .define("WEBRTC_POSIX"), .define("WEBRTC_APM_DEBUG_DUMP", to: "0"),
                    .headerSearchPath("vendor"), .headerSearchPath("vendor/base"),
                    .headerSearchPath("vendor/base/rtc_base"), .headerSearchPath("vendor/api"),
                    .headerSearchPath("vendor/audio_processing"), .headerSearchPath("vendor/absl"),
                    .headerSearchPath("vendor/base/system_wrappers/include"),
                    .headerSearchPath("vendor/base/jsoncpp/include"),
                    .headerSearchPath("vendor/common_audio"),
                    // Upstream noise suppressor; its include paths resolve through our forwarding headers.
                    .headerSearchPath("vendor-ns"), .headerSearchPath("ns-shim")]),
        .target(name: "TeamsDetectionKit", swiftSettings: [.swiftLanguageMode(.v5)]),
        .executableTarget(name: "MeetingNotes", dependencies: ["WallflyReadCore", "TeamsDetectionKit", "WebRTCAEC", "SafeAudio", .product(name: "FluidAudio", package: "FluidAudio")], swiftSettings: [.swiftLanguageMode(.v5)]),
        .executableTarget(name: "MeetingNotesDetector", dependencies: ["TeamsDetectionKit"], swiftSettings: [.swiftLanguageMode(.v5)]),
    ],
    cxxLanguageStandard: .cxx20
)
