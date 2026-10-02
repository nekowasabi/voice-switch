# voice-switch build targets
#
# Default `make` follows $PC (see ~/.dotfiles zsh env):
#   PC=wsl | PC=WSL  → Windows voice-switch.exe (Swift for Windows)
#   otherwise        → macOS VoiceSwitch.app (existing flow)
#
# Swift for Windows does not cross-compile from Linux/macOS in this repo;
# on WSL, invoke the Windows toolchain as swift.exe (see BUILD.md).

APP     := VoiceSwitch.app
BUILT   := .build/$(APP)
DEST    ?= $(HOME)/Applications
CONFIG  ?= $(HOME)/.config/voice-switch/config.json
ID      := local.voice-switch
# Earlier versions ran as a LaunchAgent; install removes it so two copies never listen at once.
OLD_PLIST := $(HOME)/Library/LaunchAgents/$(ID).plist

PC_NORM := $(shell printf '%s' "$(PC)" | tr '[:upper:]' '[:lower:]')

ifeq ($(PC_NORM),wsl)
.DEFAULT_GOAL := win
else
.DEFAULT_GOAL := app
endif

# Why: prefer Windows-hosted swift.exe when building the Windows target from WSL.
ifeq ($(PC_NORM),wsl)
SWIFT ?= $(shell command -v swift.exe 2>/dev/null || command -v swift 2>/dev/null || echo swift)
else
SWIFT ?= swift
endif

WIN_CONFIG ?= release
# Why: Run from local disk, not \\wsl.localhost — AV heuristics flag UNC-launched exes.
RELEASE_DIR ?= /mnt/c/takeda/tools/voice-switch

.PHONY: build app install uninstall logs win win-build win-verify help

help: ## List targets
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | \
		awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-14s\033[0m %s\n", $$1, $$2}'

build: ## macOS: swift build -c release
	$(SWIFT) build -c release

app: build ## macOS: wrap .build/release/voice-switch as VoiceSwitch.app (default when PC!=wsl)
	@if [ "$$(uname -s)" != "Darwin" ] && [ "$(PC_NORM)" != "wsl" ]; then \
		echo "error: macOS app target requires Darwin. On WSL run: PC=wsl make" >&2; \
		exit 1; \
	fi
	rm -rf $(BUILT)
	install -d $(BUILT)/Contents/MacOS
	install .build/release/voice-switch $(BUILT)/Contents/MacOS/voice-switch
	cp Info.plist $(BUILT)/Contents/Info.plist
	# Ad-hoc signatures default to a cdhash requirement, so every rebuild looked like a new app to TCC and
	# lost the microphone/Accessibility grants. Pin the requirement to the bundle identifier instead.
	codesign --force --sign - --identifier $(ID) -r='designated => identifier "$(ID)"' $(BUILT)

install: app ## macOS: install into DEST and open
	-launchctl bootout gui/$$(id -u)/$(ID) 2>/dev/null
	rm -f $(OLD_PLIST) $(HOME)/.local/bin/voice-switch
	-osascript -e 'quit app id "$(ID)"' 2>/dev/null
	install -d $(DEST) $(dir $(CONFIG))
	rm -rf $(DEST)/$(APP)
	cp -R $(BUILT) $(DEST)/$(APP)
	test -f $(CONFIG) || cp config.example.json $(CONFIG)
	open $(DEST)/$(APP)

uninstall: ## macOS: remove installed app
	-osascript -e 'quit app id "$(ID)"' 2>/dev/null
	rm -rf $(DEST)/$(APP)

logs: ## macOS: tail the log file
	tail -f $(HOME)/Library/Logs/voice-switch.log

win: win-build ## Windows: build (PC=wsl default)

win-build: ## Windows: swift build → RELEASE_DIR/voice-switch.exe
	@command -v "$(SWIFT)" >/dev/null 2>&1 || { \
		echo "error: Swift toolchain not found ($(SWIFT))." >&2; \
		echo "Install Swift for Windows: https://www.swift.org/install/windows/" >&2; \
		echo "From WSL, ensure swift.exe is on PATH, then: PC=wsl make" >&2; \
		echo "Layout-only check (no SDK): make win-verify" >&2; \
		exit 1; \
	}
	$(SWIFT) build -c $(WIN_CONFIG)
	mkdir -p "$(RELEASE_DIR)"
	# Why: running exe may lock the binary on Windows.
	-taskkill.exe /IM voice-switch.exe /F >/dev/null 2>&1
	bin=$$($(SWIFT) build -c $(WIN_CONFIG) --show-bin-path)/voice-switch.exe; \
	  if [ ! -f "$$bin" ]; then bin=$$($(SWIFT) build -c $(WIN_CONFIG) --show-bin-path)/voice-switch; fi; \
	  cp -f "$$bin" "$(RELEASE_DIR)/voice-switch.exe" 2>/dev/null || cp -f "$$bin" "$(RELEASE_DIR)/voice-switch"
	test -f "$(RELEASE_DIR)/config.json" || cp config.example.windows.json "$(RELEASE_DIR)/config.json"
	@echo "Windows build → $(RELEASE_DIR)"
	@echo "Try: $(RELEASE_DIR)/voice-switch.exe --vad-selftest"
	@echo "     $(RELEASE_DIR)/voice-switch.exe --fire"

win-verify: ## Verify Windows path layout / Makefile routing (no Swift SDK required)
	@test -f Sources/voice-switch/Platform.swift
	@test -f Sources/voice-switch/WindowsApp.swift
	@test -f Sources/voice-switch/Segmenter.swift
	@test -f config.example.windows.json
	@test -f BUILD.md
	@grep -q 'os(Windows)' Sources/voice-switch/Platform.swift
	@grep -q 'defaultSuperwhisperToggle' Sources/voice-switch/Platform.swift
	@grep -q 'vadSelftest' Sources/voice-switch/Segmenter.swift
	@echo "win-verify: ok (PC=$(PC) PC_NORM=$(PC_NORM) default-goal=$(.DEFAULT_GOAL))"
