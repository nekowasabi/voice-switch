func check(_ ok: Bool, _ what: String) {
  if ok {
    print("PASS \(what)")
  } else {
    print("FAIL \(what)")
    fatalError(what)
  }
}

func run(_ s: StickyState, _ events: StickyEvent...) -> StickyState {
  events.reduce(s) { stickyStep($0, $1) }
}

let off = StickyState.initial(on: false)
let on = StickyState.initial(on: true)

check(stickyEnabled(configFlag: nil, override: nil) == false, "absent config is off")
check(stickyEnabled(configFlag: false, override: nil) == false, "false config is off")
check(stickyEnabled(configFlag: true, override: nil) == true, "config turns sticky on")
check(stickyEnabled(configFlag: true, override: false) == false, "menu override turns sticky off")
check(stickyEnabled(configFlag: false, override: true) == true, "menu override turns sticky on")

let ignored = stickyStep(off, .start(.field))
check(ignored == off, "off ignores dictation start")

let armed = stickyStep(on, .start(.field))
check(
  armed.phase == .armed && armed.kind == .field && armed.alive && armed.age == 0,
  "a text field arms")

let recording = stickyStep(armed, .speech)
check(
  recording.phase == .recording && recording.kind == .field, "speech records with the field kept")

let away = stickyStep(recording, .switchApp)
check(
  away.switched && away.phase == .recording && away.kind == .field, "switching apps keeps the field"
)

let delivering = stickyStep(away, .stop(true))
check(
  delivering.phase == .delivering && stickyMayPaste(delivering) && !stickyMayEnter(delivering),
  "text goes to the captured field")

let pasted = stickyStep(delivering, .pasteOk)
check(
  pasted.deliveries == 1 && pasted.focus == .target && stickyMayEnter(pasted)
    && !stickyMayPaste(pasted),
  "one paste, then Enter is allowed")

let again = stickyStep(pasted, .pasteOk)
check(again == pasted, "a second paste is ignored")

let delivered = stickyStep(pasted, .enterOk)
check(
  delivered.phase == .delivered && delivered.entered && delivered.focus == .user,
  "after a switch, focus returns to the app in front at delivery")

let stayed = run(on, .start(.tmux), .speech, .stop(true), .pasteOk, .enterOk)
check(
  stayed.phase == .delivered && stayed.focus == .target && stayed.kind == .tmux,
  "without a switch, focus stays on the target")

check(run(on, .start(.field), .speech, .stop(false)).phase == .idle, "empty text does not deliver")

let secure = run(on, .start(.secure), .stop(true))
check(
  secure.phase == .fellBack && secure.deliveries == 0 && !secure.entered && !stickyMayPaste(secure),
  "a secure field never pastes")

let missing = run(on, .start(.noField), .stop(true))
check(missing.phase == .fellBack && !stickyMayPaste(missing), "no field never pastes")

let gone = run(on, .start(.field), .speech, .targetGone, .stop(true))
check(gone.phase == .fellBack && !gone.alive, "a dead target falls back")

let stale = stickyAdvanceAge(run(on, .start(.field), .speech), 180)
check(stale.phase == .fellBack && stale.age == 3, "three minutes drops the target")

let fresh = stickyAdvanceAge(run(on, .start(.field), .speech), 179)
check(fresh.phase == .recording && fresh.age == 2, "just under three minutes still records")

let failedPaste = run(on, .start(.field), .speech, .stop(true), .pasteFail)
check(
  failedPaste.phase == .fellBack && failedPaste.deliveries == 0 && !failedPaste.entered,
  "a failed paste does not press Enter")

let failedEnter = run(on, .start(.field), .stop(true), .pasteOk, .enterFail)
check(
  failedEnter.phase == .fellBack && failedEnter.deliveries == 1 && !failedEnter.entered,
  "a failed Enter is not a delivery")

let earlyEnter = stickyStep(run(on, .start(.field), .stop(true)), .enterOk)
check(earlyEnter.phase == .delivering && !earlyEnter.entered, "Enter before paste is ignored")

let cancelled = stickyStep(run(on, .start(.field), .speech), .toggleOff)
check(
  !cancelled.on && cancelled.phase == .fellBack && !cancelled.entered,
  "turning sticky off drops the target")

var secureDelivering = StickyState.initial(on: true)
secureDelivering.phase = .delivering
secureDelivering.kind = .secure
secureDelivering.alive = true
check(!stickyMayPaste(secureDelivering), "paste refuses a secure kind even if asked")

var offDelivering = secureDelivering
offDelivering.kind = .field
offDelivering.on = false
check(
  !stickyMayPaste(offDelivering) && !stickyMayEnter(offDelivering), "off refuses paste and Enter")

let events: [StickyEvent] = [
  .toggleOn, .toggleOff,
  .start(.field), .start(.tmux), .start(.secure), .start(.noField), .start(.none),
  .speech, .switchApp, .targetGone, .tick,
  .stop(false), .stop(true),
  .pasteOk, .pasteFail, .enterOk, .enterFail, .reset,
]

var seen: Set<StickyState> = [off, on]
var queue = [off, on]
var q = 0
while q < queue.count {
  let current = queue[q]
  q += 1
  if current.bad { fatalError("good steps set bad") }
  if current.entered,
    !(current.deliveries == 1 && stickyCapturable(current.kind) && current.phase == .delivered)
  {
    fatalError("Enter only after one paste into the target")
  }
  if current.kind == .secure, current.deliveries != 0 || current.entered {
    fatalError("secure kind is never delivered")
  }
  if current.phase == .armed || current.phase == .recording || current.phase == .delivering,
    !current.on
  {
    fatalError("sticky phases require the flag")
  }
  if current.deliveries > 1 { fatalError("more than one delivery") }
  if current.phase == .delivered, current.entered {
    let focusOK = current.switched ? current.focus == .user : current.focus == .target
    if !focusOK { fatalError("focus matches whether the user had switched") }
  }
  for event in events {
    let next = stickyStep(current, event)
    if seen.insert(next).inserted { queue.append(next) }
  }
}

print("states \(seen.count)")
print("PASS sticky logic \(seen.count) states")
