#if os(macOS)
import Foundation
import OnnxRuntimeBindings

/// Silero VAD v5 over a whole 16 kHz recording. Windows runs the same model and rule in SpeechGate.cs.
enum SpeechGate {
    static let threshold: Float = 0.5
    static let window = 512
    static let context = 64

    private static let session: Result<ORTSession, Error> = Result {
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
    private static let lock = NSLock()

    /// Number of 32 ms windows Silero scores at or above `threshold`; a recording with none holds no voice.
    static func voicedWindows(_ samples: [Float]) throws -> Int {
        lock.lock()
        defer { lock.unlock() }
        let session = try session.get()
        var state = [Float](repeating: 0, count: 2 * 128)
        var input = [Float](repeating: 0, count: context + window)
        var sr = Int64(rate)
        let srValue = try ORTValue(tensorData: NSMutableData(bytes: &sr, length: 8), elementType: .int64, shape: [1])
        var voiced = 0
        for start in stride(from: 0, to: samples.count, by: window) {
            let chunk = samples[start ..< min(start + window, samples.count)]
            // Silero expects each window prefixed with the previous window's last 64 samples; a short tail is zero-padded.
            input.replaceSubrange(0 ..< context, with: input[window ..< window + context])
            input.replaceSubrange(context ..< context + window, with: chunk + [Float](repeating: 0, count: window - chunk.count))
            let out = try session.run(
                withInputs: [
                    "input": ORTValue(tensorData: NSMutableData(bytes: input, length: input.count * 4), elementType: .float, shape: [1, NSNumber(value: context + window)]),
                    "state": ORTValue(tensorData: NSMutableData(bytes: state, length: state.count * 4), elementType: .float, shape: [2, 1, 128]),
                    "sr": srValue,
                ],
                outputNames: ["output", "stateN"], runOptions: nil)
            guard let prob = out["output"], let next = out["stateN"] else { throw NSError(domain: "voice-switch.speech-gate", code: 2) }
            let probData = try prob.tensorData() as Data
            if probData.withUnsafeBytes({ $0.load(as: Float.self) }) >= threshold { voiced += 1 }
            let nextData = try next.tensorData() as Data
            state = nextData.withUnsafeBytes { Array($0.bindMemory(to: Float.self)) }
        }
        return voiced
    }
}
#endif
