import Foundation

/// Unlike a task-group race, this returns at the deadline even if an SDK ignores
/// cancellation. Callers must invalidate the session before accepting new work.
@MainActor
enum BoundedWork {
    static func run(for timeout: Duration, operation: @escaping @MainActor () async -> Void) async -> Bool {
        let race = CompletionRace()
        return await withTaskCancellationHandler {
            guard !Task.isCancelled else { return false }
            return await withCheckedContinuation { continuation in
                race.continuation = continuation
                race.work = Task {
                    await operation()
                    race.resolve(true)
                }
                race.deadline = Task {
                    do { try await Task.sleep(for: timeout) }
                    catch { return }
                    race.resolve(false)
                }
            }
        } onCancel: {
            Task { @MainActor in race.resolve(false) }
        }
    }

    private final class CompletionRace {
        var continuation: CheckedContinuation<Bool, Never>?
        var work: Task<Void, Never>?
        var deadline: Task<Void, Never>?

        func resolve(_ completed: Bool) {
            guard let continuation else { return }
            self.continuation = nil
            if !completed { work?.cancel() }
            deadline?.cancel()
            work = nil
            deadline = nil
            continuation.resume(returning: completed)
        }
    }
}
