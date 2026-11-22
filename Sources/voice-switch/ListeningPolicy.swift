public struct ListeningPolicy {
    private enum Meet {
        case idle, blocked, overridden
    }

    private var meet = Meet.idle
    private var userWantsListening = true
    public var microphoneConnected = false

    public init() {}

    public var shouldListen: Bool { userWantsListening && !microphoneConnected && meet != .blocked }
    public var offersPause: Bool { microphoneConnected ? userWantsListening : shouldListen }

    public mutating func configureMeet(inCall: Bool) {
        if !inCall { meet = .idle }
        else if meet == .idle { meet = .blocked }
    }

    public mutating func toggle(inCall: Bool? = nil) {
        let pausing = offersPause
        if let inCall { configureMeet(inCall: inCall) }
        userWantsListening = !pausing
        if userWantsListening && !microphoneConnected && meet == .blocked { meet = .overridden }
    }

    public mutating func pause() {
        userWantsListening = false
    }
}
