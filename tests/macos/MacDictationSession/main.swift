import Foundation

func checkFollowup() throws {
    var session = MacDictationSession()
    let options = try JSONDecoder().decode(NoWakeWordConfig.self, from: Data(#"{"followup":true,"followupMs":1000}"#.utf8))
    session.armFollowup(isAutomatic: true, hasAction: false, options: options)
    precondition(!session.inFollowupWindow, "automatic dictation must not arm follow-up")
    session.armFollowup(isAutomatic: false, hasAction: true, options: options)
    precondition(!session.inFollowupWindow, "action dictation must not arm follow-up")
    session.armFollowup(isAutomatic: false, hasAction: false, options: options)
    session.advance(sampleCount: Int(rate) - 1)
    precondition(session.inFollowupWindow, "manual dictation must arm follow-up")
    session.advance(sampleCount: 1)
    precondition(!session.inFollowupWindow, "follow-up ends at the configured sample boundary")
}

func checkHeldPhrase() {
    var session = MacDictationSession()
    let preroll = [Float](repeating: 0, count: frameLen)
    let speech = [Float](repeating: 0.25, count: frameLen)
    session.appendRecent(preroll, prerollMs: nil)
    session.noteSpeech(energy: true, hal: true, rms: 0.25)
    session.appendRecent(speech, prerollMs: nil)
    let line = session.phraseLog(outcome: "allowed", threshold: 0.125)
    precondition(line == "voice: phrase voicedMs=30 energyMs=30 halMs=30 peakRms=0.2500 threshold=0.1250 -> allowed",
                 "phrase diagnostics keep the original text and units")
    session.holdPhraseForVoice()
    session.noteGap(rms: 0)
    precondition(!session.phraseGapExpired(hangoverMs: 30), "phrase survives the configured gap")
    session.appendRecent(preroll, prerollMs: nil)
    session.noteGap(rms: 0)
    precondition(session.phraseGapExpired(hangoverMs: 30), "phrase closes after the configured gap")
    session.finishIdlePhrase()
    session.appendRecent(preroll, prerollMs: nil)
    var expired = session
    expired.advance(sampleCount: Int(rate) * 5)
    precondition(expired.takeHeldPhrase().isEmpty, "the held phrase expires at five seconds")
    precondition(session.takeHeldPhrase() == preroll + speech + preroll, "the held phrase excludes the frame that closed it")
    precondition(session.takeHeldPhrase().isEmpty, "the held phrase can only be used once")
}

func checkStopCut() {
    var session = MacDictationSession()
    session.advanceStopCheck(sampleCount: frameLen)
    session.resetStopCheck()
    session.advanceStopCheck(sampleCount: frameLen)
    precondition(session.stopCutCount(100, sampleCount: 1000) == 580, "cut includes audio that arrived while recognizing the stop word")
    precondition(session.stopCutCount(100, sampleCount: 400) == 400, "cut cannot exceed the recording")
}

try checkFollowup()
checkHeldPhrase()
checkStopCut()
print("PASS macOS dictation follow-up, held phrase, and stop-word cut")
