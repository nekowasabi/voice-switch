#if os(macOS)
import AppKit

private extension TriageKind {
    var title: String {
        switch self {
        case .selfTask: return "自分の作業"
        case .lookUp: return "調べもの"
        case .writeText: return "下書き"
        case .computerChange: return "PC への変更"
        }
    }
}

@MainActor
final class TriagePanel {
    private let service: TriageService
    private var panel: NSPanel?
    private var countdowns: [(NSTextField, Date)] = []
    private var countdownTimer: Timer?
    private var lastDay = Calendar.current.startOfDay(for: Date())

    init(service: TriageService) { self.service = service }

    func show() {
        let window = panel ?? NSPanel(contentRect: NSRect(x: 0, y: 0, width: 640, height: 460),
                                      styleMask: [.titled, .closable], backing: .buffered, defer: false)
        panel = window
        window.title = "voice-switch 依頼"
        window.isReleasedWhenClosed = false
        render(window)
        window.center()
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
        if countdownTimer == nil {
            countdownTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] timer in
                MainActor.assumeIsolated {
                    guard let self, let panel = self.panel, panel.isVisible else {
                        timer.invalidate()
                        self?.countdownTimer = nil
                        return
                    }
                    let day = Calendar.current.startOfDay(for: Date())
                    if self.lastDay != day {
                        self.lastDay = day
                        self.render(panel)
                    }
                    self.tick()
                }
            }
        }
    }

    func refresh() {
        if let panel, panel.isVisible { render(panel) }
    }

    private func render(_ window: NSPanel) {
        let previousOffset = (window.contentView as? NSScrollView)?.contentView.bounds.origin ?? .zero
        countdowns.removeAll(keepingCapacity: true)
        let stack = NSStackView(views: [])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 10
        stack.edgeInsets = NSEdgeInsets(top: 12, left: 12, bottom: 12, right: 12)
        stack.addArrangedSubview(button(service.paused ? "自動送信を再開" : "自動送信を一時停止") { [weak service] in
            service?.togglePause()
        })
        if service.storeError { stack.addArrangedSubview(label("依頼ファイルを読み書きできません。", secondary: true)) }

        let review = service.review
        section("確認が要るもの", count: review.count, in: stack)
        for proposal in review {
            stack.addArrangedSubview(label(proposal.text))
            if service.stage(of: proposal.id) == .unknown {
                stack.addArrangedSubview(label("送ったか不明", secondary: true))
                stack.addArrangedSubview(NSStackView(views: [
                    button("送ったことにする") { [weak service] in service?.confirmDone(proposal.id) },
                    button("やり直す", enabled: service.canRetry(proposal)) { [weak service] in service?.retry(proposal.id) },
                    button("送らない") { [weak service] in service?.dismiss(proposal.id) },
                ]))
                continue
            }
            let reasons = service.stage(of: proposal.id) == .pending ? ["送り先の設定が変わりました"] : proposal.reasons
            stack.addArrangedSubview(label((reasons + [service.routeName(proposal.kind)].compactMap { $0 }).joined(separator: " · "),
                                           secondary: true))
            let kinds = service.stage(of: proposal.id) == .review ? TriageKind.allCases : []
            let buttons = kinds.map { kind in
                button(kind.title, enabled: service.routeName(kind) != nil) { [weak service] in
                    service?.send(proposal.id, as: kind)
                }
            } + [
                button("送る", enabled: service.routeName(proposal.kind) != nil) { [weak service] in service?.send(proposal.id) },
                button("送らない") { [weak service] in service?.dismiss(proposal.id) },
            ]
            stack.addArrangedSubview(NSStackView(views: buttons))
        }

        let pending = service.pending
        section("まもなく送る", count: pending.count, in: stack)
        for proposal in pending {
            stack.addArrangedSubview(label(proposal.text))
            let countdown = label("", secondary: true)
            if let deadline = proposal.deadline { countdowns.append((countdown, deadline)) }
            stack.addArrangedSubview(NSStackView(views: [
                countdown, label(service.routeName(proposal.kind) ?? "", secondary: true),
                button("取消") { [weak service] in service?.dismiss(proposal.id) },
                button("すぐ送る") { [weak service] in service?.send(proposal.id) },
            ]))
        }

        let today = service.today
        section("送った（今日）", count: today.filter { $0.outcome != .failed }.count, in: stack)
        for proposal in today {
            stack.addArrangedSubview(label(proposal.text))
            let result = proposal.outcome == .failed ? "送信に失敗" : proposal.kind?.title ?? "送信済み"
            let correction = NSPopUpButton()
            correction.addItem(withTitle: "種類を直す")
            for kind in TriageKind.allCases {
                correction.addItem(withTitle: kind.title)
                let item = correction.lastItem
                let action = TriageButtonAction { [weak service, weak correction] in
                    service?.correct(proposal.id, to: kind)
                    correction?.selectItem(at: 0)
                }
                item?.target = action
                item?.action = #selector(TriageButtonAction.press)
                item?.representedObject = action
            }
            stack.addArrangedSubview(NSStackView(views: [label(result, secondary: true), correction]))
        }

        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        let document = TriageDocumentView()
        document.addSubview(stack)
        stack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: document.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: document.trailingAnchor),
            stack.topAnchor.constraint(equalTo: document.topAnchor),
        ])
        tick()
        document.frame = NSRect(x: 0, y: 0, width: 620, height: max(460, stack.fittingSize.height))
        scroll.documentView = document
        window.contentView = scroll
        scroll.contentView.scroll(to: scroll.contentView.constrainBoundsRect(
            NSRect(origin: previousOffset, size: scroll.contentView.bounds.size)).origin)
    }

    private func section(_ title: String, count: Int, in stack: NSStackView) {
        let heading = label("\(title) (\(count))")
        heading.font = .boldSystemFont(ofSize: NSFont.systemFontSize)
        stack.addArrangedSubview(heading)
    }

    private func label(_ text: String, secondary: Bool = false) -> NSTextField {
        let view = NSTextField(wrappingLabelWithString: text)
        view.preferredMaxLayoutWidth = 590
        view.isSelectable = !secondary
        if secondary { view.textColor = .secondaryLabelColor }
        return view
    }

    private func button(_ title: String, enabled: Bool = true, action: @escaping () -> Void) -> NSButton {
        let button = TriageButton(title: title, target: nil, action: #selector(TriageButtonAction.press))
        button.handler = TriageButtonAction(action)
        button.target = button.handler
        button.isEnabled = enabled && !service.storeError
        return button
    }

    private func tick() {
        for (label, deadline) in countdowns {
            let seconds = max(0, min(359999, Int(min(359999, ceil(deadline.timeIntervalSinceNow)))))
            label.stringValue = service.paused ? "一時停止中" : String(format: "残り %d:%02d", seconds / 60, seconds % 60)
        }
    }
}

private final class TriageDocumentView: NSView {
    override var isFlipped: Bool { true }
}

private final class TriageButton: NSButton {
    var handler: TriageButtonAction?
}

private final class TriageButtonAction: NSObject {
    let action: () -> Void
    init(_ action: @escaping () -> Void) { self.action = action }
    @objc func press() { action() }
}
#endif
