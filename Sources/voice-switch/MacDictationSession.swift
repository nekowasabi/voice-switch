package struct MacDictationSession {
    package private(set) var clock = 0
    private var followupUntil = 0

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
}
