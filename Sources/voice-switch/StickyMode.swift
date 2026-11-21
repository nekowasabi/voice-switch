// Wake-word sticky delivery. Transitions match .claude/skills/verify-sticky-mode/StickyMode.tla.
// One tick is 60 seconds. At maxAge the armed or recording dictation is dropped.

enum StickyPhase: Hashable {
  case idle
  case armed
  case recording
  case delivering
  case delivered
  case fellBack
}

enum StickyKind: Hashable {
  case none
  case field
  case tmux
  case secure
  case noField
}

enum StickyFocus: Hashable {
  case user
  case target
}

enum StickyEvent: Hashable {
  case toggleOn
  case toggleOff
  case start(StickyKind)
  case speech
  case switchApp
  case targetGone
  case tick
  case stop(Bool)
  case pasteOk
  case pasteFail
  case enterOk
  case enterFail
  case reset
}

struct StickyState: Hashable {
  var on: Bool
  var phase: StickyPhase
  var kind: StickyKind
  var alive: Bool
  var switched: Bool
  var deliveries: Int
  var entered: Bool
  var age: Int
  var focus: StickyFocus
  var bad: Bool

  static let maxAge = 3

  static func initial(on: Bool) -> StickyState {
    StickyState(
      on: on, phase: .idle, kind: .none, alive: false, switched: false,
      deliveries: 0, entered: false, age: 0, focus: .user, bad: false)
  }
}

func stickyCapturable(_ kind: StickyKind) -> Bool {
  kind == .field || kind == .tmux
}

func stickyEnabled(configFlag: Bool?, override: Bool?) -> Bool {
  if let override { return override }
  return configFlag == true
}

func stickyMayPaste(_ s: StickyState) -> Bool {
  s.on && s.phase == .delivering && s.deliveries == 0 && !s.entered && s.alive
    && stickyCapturable(s.kind) && s.age < StickyState.maxAge && !s.bad
}

func stickyMayEnter(_ s: StickyState) -> Bool {
  s.on && s.phase == .delivering && s.deliveries == 1 && !s.entered && s.alive
    && stickyCapturable(s.kind)
}

private func stickyActive(_ phase: StickyPhase) -> Bool {
  phase == .armed || phase == .recording || phase == .delivering
}

private func stickyOpen(_ phase: StickyPhase) -> Bool {
  phase == .armed || phase == .recording
}

private func stickyBegin(_ s: StickyState, _ kind: StickyKind) -> StickyState {
  var next = s
  next.kind = kind
  next.alive = kind == .field || kind == .tmux || kind == .secure
  next.switched = false
  next.deliveries = 0
  next.entered = false
  next.age = 0
  next.focus = .user
  return next
}

func stickyStep(_ s: StickyState, _ event: StickyEvent) -> StickyState {
  switch event {
  case .toggleOn:
    guard !s.on, s.phase == .idle || s.phase == .delivered || s.phase == .fellBack else { return s }
    var next = s
    next.on = true
    return next
  case .toggleOff:
    guard s.on else { return s }
    var next = s
    next.on = false
    if stickyActive(s.phase) {
      next.phase = .fellBack
      next.entered = false
    }
    return next
  case .start(let kind):
    guard s.on, s.phase == .idle else { return s }
    guard kind == .field || kind == .tmux || kind == .secure || kind == .noField else { return s }
    var next = stickyBegin(s, kind)
    next.phase = stickyCapturable(kind) ? .armed : .recording
    return next
  case .speech:
    guard s.on, s.phase == .armed else { return s }
    var next = s
    next.phase = .recording
    return next
  case .switchApp:
    guard s.on, stickyActive(s.phase) else { return s }
    var next = s
    next.switched = true
    return next
  case .targetGone:
    guard s.on, s.alive, stickyActive(s.phase) else { return s }
    var next = s
    next.alive = false
    return next
  case .tick:
    guard s.on, stickyOpen(s.phase), s.age < StickyState.maxAge else { return s }
    var next = s
    next.age += 1
    if next.age == StickyState.maxAge { next.phase = .fellBack }
    return next
  case .stop(let hasText):
    guard s.on, stickyOpen(s.phase) else { return s }
    if !hasText {
      var next = StickyState.initial(on: s.on)
      next.bad = s.bad
      return next
    }
    var next = s
    let can = s.alive && stickyCapturable(s.kind) && s.age < StickyState.maxAge
    next.phase = can ? .delivering : .fellBack
    return next
  case .pasteOk:
    guard stickyMayPaste(s) else { return s }
    var next = s
    next.deliveries = 1
    next.focus = .target
    return next
  case .pasteFail:
    guard s.on, s.phase == .delivering, s.deliveries == 0, !s.entered else { return s }
    var next = s
    next.phase = .fellBack
    return next
  case .enterOk:
    guard stickyMayEnter(s) else { return s }
    var next = s
    next.entered = true
    next.phase = .delivered
    next.focus = s.switched ? .user : .target
    return next
  case .enterFail:
    guard s.on, s.phase == .delivering, s.deliveries == 1, !s.entered else { return s }
    var next = s
    next.phase = .fellBack
    return next
  case .reset:
    guard s.phase == .delivered || s.phase == .fellBack else { return s }
    var next = StickyState.initial(on: s.on)
    next.bad = s.bad
    return next
  }
}

func stickyAdvanceAge(_ s: StickyState, _ seconds: Int) -> StickyState {
  let steps = min(StickyState.maxAge, max(0, seconds) / 60)
  var next = s
  for _ in 0..<steps { next = stickyStep(next, .tick) }
  return next
}
