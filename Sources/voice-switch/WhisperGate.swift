#if os(macOS)
import Foundation
import whisper

/// Whisper large-v3-turbo, the second recognizer an automatic recording must agree with before it reaches Superwhisper.
/// The model is not bundled (550 MB); `make model` puts it where `modelPath` points.
enum WhisperGate {
    static let modelPath = NSHomeDirectory() + "/Library/Application Support/voice-switch/models/ggml-large-v3-turbo-q5_0.bin"

    private static let context: Result<OpaquePointer, Error> = Result {
        guard FileManager.default.fileExists(atPath: modelPath) else {
            throw NSError(domain: "voice-switch.whisper", code: 1, userInfo: [NSLocalizedDescriptionKey: "Whisper model missing at \(modelPath); run make model"])
        }
        var params = whisper_context_default_params()
        params.use_gpu = true
        guard let ctx = whisper_init_from_file_with_params(modelPath, params) else {
            throw NSError(domain: "voice-switch.whisper", code: 2, userInfo: [NSLocalizedDescriptionKey: "Whisper model failed to load"])
        }
        // ggml's Metal device teardown at exit aborts while a context still holds its buffers, so every exit()
        // crashed. Registered after init, this runs before that static destructor.
        liveContext = ctx
        atexit { if let ctx = liveContext { whisper_free(ctx) } }
        return ctx
    }
    private nonisolated(unsafe) static var liveContext: OpaquePointer?
    private static let lock = NSLock()

    /// Japanese transcript of 16 kHz mono samples. One whisper context is shared, so calls run one at a time.
    static func transcribe(_ samples: [Float]) throws -> String {
        lock.lock()
        defer { lock.unlock() }
        let ctx = try context.get()
        var params = whisper_full_default_params(WHISPER_SAMPLING_GREEDY)
        params.print_progress = false
        params.print_realtime = false
        params.print_timestamps = false
        params.print_special = false
        params.n_threads = Int32(max(1, ProcessInfo.processInfo.activeProcessorCount / 2))
        return try "ja".withCString { language in
            params.language = language
            guard whisper_full(ctx, params, samples, Int32(samples.count)) == 0 else {
                throw NSError(domain: "voice-switch.whisper", code: 3, userInfo: [NSLocalizedDescriptionKey: "Whisper inference failed"])
            }
            return (0 ..< whisper_full_n_segments(ctx)).map { String(cString: whisper_full_get_segment_text(ctx, $0)) }.joined()
        }
    }
}
#endif
