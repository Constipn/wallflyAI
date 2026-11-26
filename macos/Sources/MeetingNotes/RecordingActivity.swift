import Combine
import Foundation

/// Frequently changing presentation data. Deliberately not forwarded to MeetingStore.
@MainActor
final class RecordingActivity: ObservableObject {
    @Published private(set) var meLevel: Double = 0
    @Published private(set) var othersLevel: Double = 0
    @Published var elapsed: TimeInterval = 0
    private var lastMeSignal = Date.distantPast
    private var lastOthersSignal = Date.distantPast

    func receive(_ level: Double, from speaker: Speaker, at date: Date = Date()) {
        let level = level.isFinite ? min(1, max(0, level)) : 0
        if speaker == .me {
            lastMeSignal = date
            if meLevel != level { meLevel = level }
        } else {
            lastOthersSignal = date
            if othersLevel != level { othersLevel = level }
        }
    }

    func expireSignals(at date: Date = Date()) {
        if date.timeIntervalSince(lastMeSignal) > 0.35, meLevel != 0 { meLevel = 0 }
        if date.timeIntervalSince(lastOthersSignal) > 0.35, othersLevel != 0 { othersLevel = 0 }
    }

    func resetLevels() {
        if meLevel != 0 { meLevel = 0 }
        if othersLevel != 0 { othersLevel = 0 }
        lastMeSignal = .distantPast
        lastOthersSignal = .distantPast
    }
}
