#if os(macOS)
import Foundation
import OnnxRuntimeBindings

enum SpeakerGate {
    static let modelName = "3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx"
    static let modelPath = NSHomeDirectory() + "/Library/Application Support/voice-switch/models/" + modelName
    // Measured 2026-10-16 on the user's superwhisper recordings vs FLEURS ja, CAM++ with a 5-clip mean enrollment:
    // ~1 s does not separate (EER 14-18 %), 3 s gives EER 2.9 % at 0.295, 6 s 0 % at 0.300. Shorter audio is not judged.
    static let defaultThreshold = 0.30
    static let minSamples = 3 * Int(rate)
    private static let lock = NSLock()
    private static let session: Result<ORTSession, Error> = Result {
        let options = try ORTSessionOptions()
        try options.setIntraOpNumThreads(1)
        return try ORTSession(env: ORTEnv(loggingLevel: .warning), modelPath: modelPath, sessionOptions: options)
    }
    struct Enrollment: Codable {
        let model: String
        let sampleRate: Int
        let sources: [String]
        let embedding: [Float]
    }
    private static func invalid(_ message: String) -> NSError {
        NSError(domain: "voice-switch.speaker-gate", code: 1, userInfo: [NSLocalizedDescriptionKey: message])
    }

    static func accepts(_ samples: [Float], cfg: Config) -> Bool {
        guard let path = cfg.speakerEnrollment else { return true }
        guard samples.count >= minSamples else {
            log("speaker gate: skipped, \(samples.count * 1000 / Int(rate)) ms is under 3000 ms")
            return true
        }
        var stage = "enrollment"
        do {
            let (enrolled, threshold) = try load(path, cfg: cfg, stage: &stage)
            let began = DispatchTime.now().uptimeNanoseconds
            let vector = try embedding(samples)
            let ms = (DispatchTime.now().uptimeNanoseconds - began) / 1_000_000
            let score = try cosine(vector, enrolled)
            log(String(format: "speaker gate: score=%.3f ms=%llu", locale: Locale(identifier: "en_US_POSIX"), score, ms))
            return score >= threshold
        } catch {
            log("speaker gate: \(stage) unavailable, passing through: \(error.localizedDescription)")
            return true
        }
    }

    /// The samples without each pause-delimited segment of 2 s+ that scores as another speaker; everything kept is untouched.
    /// Measured 2026-10-16 (30 own+FLEURS+own mixes, 30 own-only clips): pauses of 400 ms, 2 s segments and 0.30 removed
    /// a mean 86 % of the other voice, 0.1 % of own speech in mixes and none of the own-only clips.
    static func withoutOtherSpeakers(_ samples: [Float], cfg: Config) -> [Float] {
        guard let path = cfg.speakerEnrollment else { return samples }
        var stage = "enrollment"
        let cuts: [Range<Int>]
        do {
            let (enrolled, threshold) = try load(path, cfg: cfg, stage: &stage)
            cuts = try segments(samples).filter { segment in
                guard segment.count >= 2 * Int(rate) else { return false }
                return try cosine(embedding(Array(samples[segment])), enrolled) < threshold
            }
        } catch {
            log("speaker gate: \(stage) unavailable, passing through: \(error.localizedDescription)")
            return samples
        }
        guard !cuts.isEmpty else { return samples }
        let removed = cuts.reduce(0) { $0 + $1.count }, ms = { $0 * 1000 / Int(rate) }
        // The user asked for this recording, so a cut that leaves almost nothing keeps all of it instead.
        guard samples.count - removed >= Int(rate) else {
            log("speaker gate: kept all \(ms(samples.count)) ms, cutting \(cuts.count) segments would leave \(ms(samples.count - removed)) ms")
            return samples
        }
        log("speaker gate: cut \(ms(removed)) ms of \(ms(samples.count)) ms in \(cuts.count) segments")
        var kept = [Float](), at = 0
        kept.reserveCapacity(samples.count - removed)
        for cut in cuts { kept += samples[at..<cut.lowerBound]; at = cut.upperBound }
        return kept + samples[at...]
    }

    /// 30 ms frames are voiced above a tenth of the 95th-percentile frame RMS; 400 ms unvoiced ends a segment.
    /// Keep in step with SpeakerGate.cs; the measurement ran this rule in numpy (linear percentile).
    static func segments(_ samples: [Float]) -> [Range<Int>] {
        let f = 480, n = samples.count / f
        guard n > 0 else { return [] }
        let rms = (0..<n).map { i in sqrt(samples[i * f..<(i + 1) * f].reduce(0.0) { $0 + Double($1) * Double($1) } / Double(f)) }
        let sorted = rms.sorted(), h = 0.95 * Double(n - 1), lo = Int(h), t = h - Double(lo)
        let a = sorted[lo], b = sorted[min(lo + 1, n - 1)]
        let p95 = t < 0.5 ? a + (b - a) * t : b - (b - a) * (1 - t)
        var segments = [Range<Int>](), start: Int?, quiet = 0
        for i in 0..<n {
            if rms[i] > 0.1 * p95 {
                if start == nil { start = i }
                quiet = 0
            } else if let s = start {
                quiet += 1
                if quiet * 30 >= 400 { segments.append(s * f..<(i - quiet + 1) * f); start = nil }
            }
        }
        if let s = start { segments.append(s * f..<n * f) }
        return segments
    }

    private static func load(_ path: String, cfg: Config, stage: inout String) throws -> (embedding: [Float], threshold: Double) {
        let enrolled = try JSONDecoder().decode(Enrollment.self, from: Data(contentsOf: URL(fileURLWithPath: (path as NSString).expandingTildeInPath)))
        guard enrolled.model == modelName, enrolled.sampleRate == 16000 else { throw invalid("model or sample rate mismatch") }
        stage = "model"
        guard FileManager.default.fileExists(atPath: modelPath) else { throw invalid("run make speaker-model") }
        _ = try session.get()
        stage = "inference"
        let threshold = cfg.speakerThreshold ?? defaultThreshold
        guard threshold.isFinite, (-1...1).contains(threshold) else { throw invalid("invalid threshold") }
        return (enrolled.embedding, threshold)
    }

    /// Mean of the unit per-file embeddings, itself scaled to unit length.
    static func enroll(_ output: String, _ inputs: [String]) throws {
        guard !inputs.isEmpty else { throw invalid("no input WAV") }
        var sum = [Double]()
        for input in inputs {
            let vector = try embedding(try readWave(input))
            _ = try cosine(vector, vector)
            let norm = sqrt(vector.reduce(0.0) { $0 + Double($1) * Double($1) })
            if sum.isEmpty { sum = [Double](repeating: 0, count: vector.count) }
            guard sum.count == vector.count else { throw invalid("embedding dimension mismatch") }
            for i in vector.indices { sum[i] += Double(vector[i]) / norm }
        }
        let norm = sqrt(sum.reduce(0) { $0 + $1 * $1 })
        let mean = sum.map { Float($0 / norm) }
        let data = try JSONEncoder().encode(Enrollment(model: modelName, sampleRate: 16000, sources: inputs, embedding: mean))
        try data.write(to: URL(fileURLWithPath: output), options: .atomic)
        print("speaker enrollment: \(output)")
    }

    static func cosine(_ a: [Float], _ b: [Float]) throws -> Double {
        guard !a.isEmpty, a.count == b.count else { throw invalid("embedding dimension mismatch") }
        var dot = 0.0, aa = 0.0, bb = 0.0
        for (x, y) in zip(a, b) {
            guard x.isFinite, y.isFinite else { throw invalid("nonfinite embedding") }
            dot += Double(x) * Double(y); aa += Double(x) * Double(x); bb += Double(y) * Double(y)
        }
        guard aa > 0, bb > 0 else { throw invalid("zero embedding") }
        let score = dot / sqrt(aa * bb)
        guard score.isFinite else { throw invalid("nonfinite score") }
        return score
    }

    static func embedding(_ samples: [Float]) throws -> [Float] {
        let features = try fbank(samples)
        lock.lock()
        defer { lock.unlock() }
        let session = try session.get()
        let input = try ORTValue(tensorData: NSMutableData(bytes: features, length: features.count * 4), elementType: .float,
                                 shape: [1, NSNumber(value: features.count / 80), 80])
        let outputs = try session.run(withInputs: ["x": input], outputNames: ["embedding"], runOptions: nil)
        guard let value = outputs["embedding"] else { throw invalid("missing embedding output") }
        let data = try value.tensorData() as Data
        return data.withUnsafeBytes { Array($0.bindMemory(to: Float.self)) }
    }

    // Upstream defaults: reflected edges, 25/10 ms, DC removal, preemphasis .97, Povey, mel80 (20–7600 Hz), CMN.
    // Keep this algorithm in step with SpeakerGate.cs.
    static func fbank(_ samples: [Float]) throws -> [Float] {
        guard samples.count >= 400, samples.allSatisfy(\.isFinite) else { throw invalid("audio shorter than 25 ms or nonfinite") }
        let frames = (samples.count + 80) / 160
        let low = 1127 * log(1 + 20.0 / 700), high = 1127 * log(1 + 7600.0 / 700)
        let points = (0..<82).map { low + Double($0) * (high - low) / 81 }
        var weights = [[(Int, Double)]](repeating: [], count: 80)
        for bin in 1..<256 {
            let mel = 1127 * log(1 + Double(bin) * 16000 / 512 / 700)
            for m in 0..<80 where mel > points[m] && mel < points[m + 2] {
                let weight = mel <= points[m + 1] ? (mel - points[m]) / (points[m + 1] - points[m]) : (points[m + 2] - mel) / (points[m + 2] - points[m + 1])
                weights[m].append((bin, weight))
            }
        }
        let window = (0..<400).map { pow(0.5 - 0.5 * cos(2 * .pi * Double($0) / 399), 0.85) }
        var features = [Float](repeating: 0, count: frames * 80)
        for frame in 0..<frames {
            var real = [Double](repeating: 0, count: 512), imag = real
            let offset = frame * 160 - 120
            let input = (0..<400).map { i -> Double in
                var index = offset + i
                while index < 0 || index >= samples.count {
                    index = index < 0 ? -index - 1 : 2 * samples.count - 1 - index
                }
                return Double(samples[index])
            }
            let mean = input.reduce(0, +) / 400
            for i in 0..<400 {
                let x = input[i] - mean
                let previous = input[max(0, i - 1)] - mean
                real[i] = (x - 0.97 * previous) * window[i]
            }
            var j = 0
            for i in 1..<512 {
                var bit = 256
                while j & bit != 0 { j ^= bit; bit >>= 1 }
                j ^= bit
                if i < j { real.swapAt(i, j) }
            }
            var size = 2
            while size <= 512 {
                let angle = -2 * Double.pi / Double(size)
                for start in stride(from: 0, to: 512, by: size) {
                    for k in 0..<(size / 2) {
                        let c = cos(angle * Double(k)), s = sin(angle * Double(k))
                        let a = start + k, b = a + size / 2
                        let re = real[b] * c - imag[b] * s, im = real[b] * s + imag[b] * c
                        real[b] = real[a] - re; imag[b] = imag[a] - im
                        real[a] += re; imag[a] += im
                    }
                }
                size *= 2
            }
            for m in 0..<80 {
                let energy = weights[m].reduce(0.0) { $0 + (real[$1.0] * real[$1.0] + imag[$1.0] * imag[$1.0]) * $1.1 }
                features[frame * 80 + m] = Float(log(max(Double(Float.ulpOfOne), energy)))
            }
        }
        for m in 0..<80 {
            let mean = (0..<frames).reduce(0.0) { $0 + Double(features[$1 * 80 + m]) } / Double(frames)
            for frame in 0..<frames { features[frame * 80 + m] -= Float(mean) }
        }
        return features
    }

    static func readWave(_ path: String) throws -> [Float] {
        let data = [UInt8](try Data(contentsOf: URL(fileURLWithPath: path)))
        func u16(_ i: Int) -> Int { Int(data[i]) | Int(data[i + 1]) << 8 }
        func u32(_ i: Int) -> Int { u16(i) | u16(i + 2) << 16 }
        func tag(_ i: Int) -> String { String(bytes: data[i..<i + 4], encoding: .ascii) ?? "" }
        guard data.count >= 12, tag(0) == "RIFF", tag(8) == "WAVE", u32(4) + 8 == data.count else { throw invalid("invalid RIFF WAV") }
        var offset = 12, validFormat = false, pcm: Range<Int>?
        let end = u32(4) + 8
        while offset + 8 <= end {
            let length = u32(offset + 4), start = offset + 8
            guard length <= end - start else { throw invalid("truncated WAV") }
            if tag(offset) == "fmt " {
                guard length >= 16 else { throw invalid("invalid WAV format") }
                validFormat = u16(start) == 1 && u16(start + 2) == 1 && u32(start + 4) == 16000 && u32(start + 8) == 32000 && u16(start + 12) == 2 && u16(start + 14) == 16
            } else if tag(offset) == "data" {
                guard pcm == nil else { throw invalid("duplicate WAV data chunk") }
                pcm = start..<start + length
            }
            offset = start + length + length % 2
        }
        guard validFormat, let pcm, !pcm.isEmpty, pcm.count % 2 == 0 else { throw invalid("requires 16 kHz mono 16-bit PCM WAV") }
        return stride(from: pcm.lowerBound, to: pcm.upperBound, by: 2).map { Float(Int16(bitPattern: UInt16(u16($0)))) / 32768 }
    }
}
#endif
