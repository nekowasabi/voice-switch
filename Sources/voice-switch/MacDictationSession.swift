package struct MacDictationSession {
    package private(set) var clock = 0
    private var followupUntil = 0
    private var samplesSinceStopCheck = 0
    package private(set) var recent: [Float] = []
    // Voiced frames are joined across gaps up to the hangover, which the VAD alone splits into short runs.
    package private(set) var speechFrames = 0
    private var gapFrames = 0
    package private(set) var phraseEnergy = 0
    package private(set) var phraseHAL = 0
    package private(set) var phrasePeak: Float = 0

    package init() {}

    package var inFollowupWindow: Bool { clock < followupUntil }

    package mutating func advance(sampleCount: Int) {
        clock += sampleCount
    }

    package mutating func armFollowup(isAutomatic: Bool, hasAction: Bool, options: NoWakeWordConfig?) {
        if !isAutomatic && !hasAction, let options, options.followup == true, options.valid {
            followupUntil = clock + Int(rate) * (options.followupMs ?? 8000) / 1000
        }
    }

    package mutating func clearFollowup() {
        followupUntil = 0
    }

    package mutating func advanceStopCheck(sampleCount: Int) {
        samplesSinceStopCheck += sampleCount
    }

    package mutating func resetStopCheck() {
        samplesSinceStopCheck = 0
    }

    package func stopCutCount(_ cut: Int, sampleCount: Int) -> Int {
        min(cut + samplesSinceStopCheck, sampleCount)
    }

    package var hasSpeech: Bool { speechFrames > 0 }

    package mutating func noteSpeech(energy: Bool, hal: Bool, rms: Float) {
        if !hasSpeech {
            phraseEnergy = 0
            phraseHAL = 0
            phrasePeak = 0
        }
        speechFrames += 1
        gapFrames = 0
        if energy { phraseEnergy += 1 }
        if hal { phraseHAL += 1 }
        phrasePeak = max(phrasePeak, rms)
    }

    package mutating func noteGap(rms: Float) {
        gapFrames += 1
        phrasePeak = max(phrasePeak, rms)
    }

    package func phraseGapExpired(hangoverMs: Int?) -> Bool {
        gapFrames > frames(ms: hangoverMs ?? 300)
    }

    package func hasReachedMinimumSpeech(_ minSpeechMs: Int?) -> Bool {
        speechFrames >= frames(ms: minSpeechMs ?? 300)
    }

    package mutating func clearSpeech() {
        speechFrames = 0
    }

    package mutating func clearRecentSpeech() {
        recent.removeAll()
        clearSpeech()
    }

    package mutating func appendRecent(_ samples: [Float], prerollMs: Int?) {
        recent += samples
        // A live phrase is kept so an automatic session that starts late still records it from the beginning.
        // ponytail: capped at 10 s of audio; a longer phrase keeps only its tail.
        let keep = hasSpeech ? Int(rate) * 10 : frames(ms: prerollMs ?? 300) * frameLen + frameLen
        if recent.count > keep { recent.removeFirst(recent.count - keep) }
    }
}
