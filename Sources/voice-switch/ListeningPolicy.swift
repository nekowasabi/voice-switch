struct ListeningPolicy {
    private enum Meet {
        case idle, blocked, overridden
    }

    private var meet = Meet.idle
    private var userWantsListening = true
    var microphoneConnected = false

    var shouldListen: Bool { userWantsListening && !microphoneConnected && meet != .blocked }
    var offersPause: Bool { microphoneConnected ? userWantsListening : shouldListen }

    mutating func configureMeet(inCall: Bool) {
        if !inCall { meet = .idle }
        else if meet == .idle { meet = .blocked }
    }

    mutating func toggle(inCall: Bool? = nil) {
        let pausing = offersPause
        if let inCall { configureMeet(inCall: inCall) }
        userWantsListening = !pausing
        if userWantsListening && !microphoneConnected && meet == .blocked { meet = .overridden }
    }

    mutating func pause() {
        userWantsListening = false
    }
}
