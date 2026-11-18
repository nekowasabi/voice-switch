func expect(_ condition: @autoclosure () -> Bool, _ message: String) {
    precondition(condition(), message)
}

var policy = ListeningPolicy()
expect(policy.shouldListen, "Listening is on by default")
policy.microphoneConnected = true
expect(!policy.shouldListen, "Connecting the selected microphone pauses listening")
policy.microphoneConnected = false
expect(policy.shouldListen, "Disconnecting the selected microphone resumes listening")

policy.toggle()
policy.microphoneConnected = true
policy.microphoneConnected = false
expect(!policy.shouldListen, "Disconnecting keeps a manual pause")
policy.microphoneConnected = true
policy.toggle()
expect(!policy.shouldListen, "Manually enabling listening while connected waits for disconnection")
policy.microphoneConnected = false
expect(policy.shouldListen, "Disconnection applies the manual choice made while connected")

policy.microphoneConnected = true
policy.toggle()
policy.microphoneConnected = false
expect(!policy.shouldListen, "Pausing while connected cancels automatic resume")
policy.toggle()

policy.configureMeet(inCall: true)
expect(!policy.shouldListen, "A call pauses listening")
policy.microphoneConnected = true
policy.configureMeet(inCall: false)
expect(!policy.shouldListen, "Ending a call does not override the connected microphone")
policy.configureMeet(inCall: true)
policy.microphoneConnected = false
expect(!policy.shouldListen, "Disconnecting does not override a call")
policy.toggle()
expect(policy.shouldListen, "Manually resuming overrides the current call")
policy.configureMeet(inCall: true)
expect(policy.shouldListen, "The current call stays overridden at the next observation")
policy.microphoneConnected = true
expect(!policy.shouldListen, "A call override cannot override the connected microphone")
policy.configureMeet(inCall: false)
policy.microphoneConnected = false
policy.configureMeet(inCall: true)
expect(!policy.shouldListen, "The next call pauses again")
policy.configureMeet(inCall: false)
expect(policy.shouldListen, "Ending the call resumes with the microphone disconnected")

print("ListeningPolicy: ok")
