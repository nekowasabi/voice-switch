#if os(macOS)
import Foundation
import OnnxRuntimeBindings

/// Silero VAD v5 over a whole 16 kHz recording. Windows runs the same model and rule in SpeechGate.cs.
enum SpeechGate {
    static let threshold: Float = 0.5
    static let window = 512
    static let context = 64

    static func makeSession() -> Result<ORTSession, Error> {
        Result {
            // Not Bundle.module: it calls fatalError when the bundle is missing, and a broken install must still dictate.
            let bundle = [Bundle.main.resourceURL, Bundle.main.bundleURL].lazy
                .compactMap { $0.flatMap { Bundle(url: $0.appendingPathComponent("voice-switch_voice-switch.bundle")) } }.first
            guard let path = bundle?.path(forResource: "silero_vad", ofType: "onnx") else {
                throw NSError(domain: "voice-switch.speech-gate", code: 1, userInfo: [NSLocalizedDescriptionKey: "silero_vad.onnx missing from bundle"])
            }
            let options = try ORTSessionOptions()
            try options.setIntraOpNumThreads(1)
            return try ORTSession(env: ORTEnv(loggingLevel: .warning), modelPath: path, sessionOptions: options)
        }
    }
    private static let session = makeSession()
    private static let lock = NSLock()

    /// Number of 32 ms windows Silero scores at or above `threshold`; a recording with none holds no voice.
    static func voicedWindows(_ samples: [Float]) throws -> Int {
        try probabilities(samples).filter { $0 >= threshold }.count
    }

    /// Silero's speech probability for each 32 ms window, in order.
    static func probabilities(_ samples: [Float]) throws -> [Float] {
        lock.lock()
        defer { lock.unlock() }
        let session = try session.get()
        var state = [Float](repeating: 0, count: 2 * 128)
        var input = [Float](repeating: 0, count: context + window)
        var probs: [Float] = []
        for start in stride(from: 0, to: samples.count, by: window) {
            let chunk = samples[start ..< min(start + window, samples.count)]
            probs.append(try score(chunk, input: &input, state: &state, session: session))
        }
        return probs
    }

    /// One window through the model; a short chunk is zero-padded.
    static func score(_ chunk: ArraySlice<Float>, input: inout [Float], state: inout [Float], session: ORTSession) throws -> Float {
        // Silero expects each window prefixed with the previous window's last 64 samples.
        input.replaceSubrange(0 ..< context, with: input[window ..< window + context])
        input.replaceSubrange(context ..< context + window, with: chunk + [Float](repeating: 0, count: window - chunk.count))
        var sr = Int64(rate)
        let out = try session.run(
            withInputs: [
                "input": ORTValue(tensorData: NSMutableData(bytes: input, length: input.count * 4), elementType: .float, shape: [1, NSNumber(value: context + window)]),
                "state": ORTValue(tensorData: NSMutableData(bytes: state, length: state.count * 4), elementType: .float, shape: [2, 1, 128]),
                "sr": ORTValue(tensorData: NSMutableData(bytes: &sr, length: 8), elementType: .int64, shape: [1]),
            ],
            outputNames: ["output", "stateN"], runOptions: nil)
        guard let prob = out["output"], let next = out["stateN"] else { throw NSError(domain: "voice-switch.speech-gate", code: 2) }
        let nextData = try next.tensorData() as Data
        state = nextData.withUnsafeBytes { Array($0.bindMemory(to: Float.self)) }
        return (try prob.tensorData() as Data).withUnsafeBytes { $0.load(as: Float.self) }
    }
}

/// Silero fed live 30 ms frames: scores each 512-sample window as it fills, carrying the model state between windows.
/// Its own session, so the frame loop never waits on the gate scoring a whole recording.
struct SileroStream {
    private static let session = SpeechGate.makeSession()
    private var state = [Float](repeating: 0, count: 2 * 128)
    private var input = [Float](repeating: 0, count: SpeechGate.context + SpeechGate.window)
    private var pending: [Float] = []

    /// Probabilities of the windows this frame completed (usually one, sometimes none); empty when the model is missing.
    mutating func push(_ f: [Float]) -> [Float] {
        guard let session = try? Self.session.get() else { return [] }
        pending += f
        var probs: [Float] = []
        while pending.count >= SpeechGate.window {
            guard let p = try? SpeechGate.score(pending.prefix(SpeechGate.window), input: &input, state: &state, session: session) else { return probs }
            probs.append(p)
            pending.removeFirst(SpeechGate.window)
        }
        return probs
    }
}
#endif
