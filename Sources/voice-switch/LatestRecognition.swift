import Foundation
import os

/// Runs one recognition job beside the frame loop. Submitting returns at once, and a newer job supersedes the
/// pending one, so only the newest segment's result can be taken. A superseded result is dropped, not acted on.
final class LatestRecognition<Value: Sendable>: @unchecked Sendable {
    private struct State {
        var seq = 0
        var task: Task<Void, Never>?
        var finished: Value?
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    /// True while the newest job runs or its result has not been taken yet.
    var pending: Bool { state.withLock { $0.task != nil || $0.finished != nil } }

    func submit(_ job: @escaping @Sendable () async -> Value) {
        state.withLock { st in
            st.task?.cancel()
            st.seq += 1
            st.finished = nil
            let seq = st.seq
            st.task = Task {
                let value = await job()
                self.complete(seq: seq, value: value)
            }
        }
    }

    /// The newest job's result, handed over once. Nil while it runs, or after a newer job replaced it.
    func take() -> Value? {
        state.withLock { st in
            defer { st.finished = nil }
            return st.finished
        }
    }

    private func complete(seq: Int, value: Value) {
        state.withLock { st in
            guard st.seq == seq else { return }
            st.task = nil
            st.finished = value
        }
    }
}
