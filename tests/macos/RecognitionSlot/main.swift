// Build and run (on macOS):
//   swiftc Sources/voice-switch/LatestRecognition.swift tests/macos/RecognitionSlot/main.swift -o /tmp/recognition-slot
//   /tmp/recognition-slot; echo "exit=$?"
import Foundation

func check(_ ok: Bool, _ what: String) {
    print((ok ? "PASS " : "FAIL ") + what)
    if !ok { exit(1) }
}

func sleepMs(_ ms: UInt64) async {
    do { try await Task.sleep(nanoseconds: ms * 1_000_000) } catch {}
}

// Feeds frames every 5 ms, as the mic does. Each frame takes the result and, every 40 frames, submits a
// recognition job. Only the frame's own work is timed; the pacing sleep is not counted.
func feedFrames(jobMs: UInt64) async -> (worstWorkMs: Double, wallSeconds: Double) {
    let slot = LatestRecognition<Int>()
    var worst = 0.0
    let wallStart = Date()
    for frame in 0 ..< 200 {
        let workStart = Date()
        if frame % 40 == 0 {
            slot.submit { await sleepMs(jobMs); return frame }
        }
        _ = slot.take()
        worst = max(worst, Date().timeIntervalSince(workStart) * 1000)
        await sleepMs(5)
    }
    return (worst, Date().timeIntervalSince(wallStart))
}

// The recognition wait may grow; the frame loop's own work and its pace must not.
for jobMs in [UInt64(10), 2000] {
    let r = await feedFrames(jobMs: jobMs)
    check(r.worstWorkMs < 20, "frame work stays under 20 ms with a \(jobMs) ms recognition (worst \(String(format: "%.2f", r.worstWorkMs)) ms)")
    check(r.wallSeconds < 2.0, "200 frames finish in \(String(format: "%.2f", r.wallSeconds)) s with a \(jobMs) ms recognition")
}

// Only the newest job's result survives; superseded results are dropped.
do {
    let slot = LatestRecognition<Int>()
    for i in 1 ... 5 {
        slot.submit { await sleepMs(50); return i }
    }
    await sleepMs(300)
    check(slot.take() == 5, "after five quick submits, only the newest result is taken")
    check(slot.take() == nil, "a result is taken once")
    check(!slot.pending, "nothing pending after the take")
}

do {
    let slot = LatestRecognition<Int>()
    slot.submit { await sleepMs(200); return 1 }
    slot.submit { 2 }
    await sleepMs(400)
    check(slot.take() == 2, "a slow superseded job never surfaces its result")
}

do {
    let slot = LatestRecognition<Int>()
    slot.submit { await sleepMs(100); return 7 }
    check(slot.pending, "pending while the job runs")
    await sleepMs(250)
    check(slot.pending, "pending while the finished result waits to be taken")
    check(slot.take() == 7, "the finished result is taken")
    check(!slot.pending, "not pending after the take")
}

print("all recognition slot checks passed")
