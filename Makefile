# voice-switch build targets
#
# Default `make` follows $PC:
#   PC=wsl | PC=WSL  → Windows voice-switch.exe (.NET)
#   otherwise        → macOS: quit, rebuild and relaunch VoiceSwitch.app (same as focusbm)
#
# Windows follows the WSL -> .NET Windows-targeting path.

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
.DEFAULT_GOAL := relaunch
endif

SWIFT ?= swift

WIN_CONFIG ?= Release
DOTNET ?= $(shell command -v dotnet 2>/dev/null || printf '%s' "$(HOME)/.local/share/mise/shims/dotnet")
WIN_RID ?= win-x64
WIN_APP := dotnet/VoiceSwitch.Windows/VoiceSwitch.Windows.csproj
WIN_TESTS := dotnet/VoiceSwitch.Windows.Tests/VoiceSwitch.Windows.Tests.csproj
DOTNET_RESTORE_FLAGS ?= --ignore-failed-sources --disable-parallel
# Why: Run from local disk, not \\wsl.localhost, as focusbm does; AV heuristics flag UNC-launched exes.
RELEASE_DIR ?= /mnt/c/takeda/tools/voice-switch

.PHONY: build app install relaunch uninstall logs win win-restore win-build win-test parity-test win-publish win-verify help

help: ## List targets
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | \
		awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-14s\033[0m %s\n", $$1, $$2}'

build: ## macOS: swift build -c release
	$(SWIFT) build -c release

app: build ## macOS: wrap .build/release/voice-switch as VoiceSwitch.app
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
	# open fails with -600 if it races the old instance still shutting down, so wait for it to exit.
	@for i in $$(seq 50); do pgrep -x voice-switch >/dev/null || break; sleep 0.1; done
	install -d $(DEST) $(dir $(CONFIG))
	rm -rf $(DEST)/$(APP)
	cp -R $(BUILT) $(DEST)/$(APP)
	test -f $(CONFIG) || cp config.example.json $(CONFIG)
	open $(DEST)/$(APP)

relaunch: install ## macOS: quit, rebuild and relaunch (default when PC!=wsl)

uninstall: ## macOS: remove installed app
	-osascript -e 'quit app id "$(ID)"' 2>/dev/null
	rm -rf $(DEST)/$(APP)

logs: ## macOS: tail the log file
	tail -f $(HOME)/Library/Logs/voice-switch.log

win: win-publish ## Windows: build, test, and publish voice-switch.exe (PC=wsl default)

win-restore: ## Windows: restore .NET projects
	$(DOTNET) restore $(WIN_APP) -r $(WIN_RID) $(DOTNET_RESTORE_FLAGS)
	$(DOTNET) restore $(WIN_TESTS) $(DOTNET_RESTORE_FLAGS)

win-build: win-restore win-test ## Windows: build .NET app and behavior tests
	$(DOTNET) build $(WIN_APP) -c $(WIN_CONFIG) -r $(WIN_RID) --no-restore

win-test: win-restore ## Windows: run package-free behavior tests
	$(DOTNET) run --project $(WIN_TESTS) -c $(WIN_CONFIG) --no-restore
	$(MAKE) parity-test

parity-test: ## Run macOS/Windows parity contract checks
	python3 tests/parity/run_parity.py

win-publish: win-build ## Windows: publish voice-switch.exe to RELEASE_DIR
	mkdir -p "$(RELEASE_DIR)"
	@# A running voice-switch.exe locks its DLLs and publish fails with Access denied.
	-taskkill.exe /IM voice-switch.exe /F >/dev/null 2>&1
	@# The tray used to ship as a second exe; drop its leftovers so release/ holds one app.
	rm -f "$(RELEASE_DIR)"/voice-switch-tray.*
	$(DOTNET) publish $(WIN_APP) -c $(WIN_CONFIG) -r $(WIN_RID) --self-contained false \
		-p:PublishSingleFile=false \
		-p:DebugType=None \
		-p:CopyOutputSymbolsToPublishDirectory=false \
		-o "$(RELEASE_DIR)"
	@# An existing config.json in RELEASE_DIR is never overwritten. The old in-repo release/config.json seeds it once.
	[ -f "$(RELEASE_DIR)/config.json" ] || [ ! -f release/config.json ] || cp release/config.json "$(RELEASE_DIR)/config.json"
	[ -f "$(RELEASE_DIR)/config.json" ] || cp config.example.windows.json "$(RELEASE_DIR)/config.json"
	@# cwd is RELEASE_DIR so the exe starts from local disk, not a UNC path.
	cd "$(RELEASE_DIR)" && powershell.exe -NoProfile -Command "Start-Process -FilePath .\voice-switch.exe"
	@echo "Windows build → $(RELEASE_DIR)"
	@echo "Run:  $(RELEASE_DIR)/voice-switch.exe            (tray app, listens on launch)"
	@echo "Try:  $(RELEASE_DIR)/voice-switch.exe --self-test"
	@echo "      $(RELEASE_DIR)/voice-switch.exe --recognizers"
	@echo "      $(RELEASE_DIR)/voice-switch.exe --check-device"
	@echo "      $(RELEASE_DIR)/voice-switch.exe --tray-command quit"

win-verify: ## Verify Windows path layout / Makefile routing (no Swift SDK required)
	@test -f Sources/voice-switch/Platform.swift
	@test -f Sources/voice-switch/WindowsApp.swift
	@test -f Sources/voice-switch/Segmenter.swift
	@test -f dotnet/VoiceSwitch.Windows/VoiceSwitch.Windows.csproj
	@test -f dotnet/VoiceSwitch.Windows/Tray/TrayHost.cs
	@test ! -e dotnet/VoiceSwitch.Windows.Tray
	@test -f dotnet/VoiceSwitch.Windows.Core/VoiceSwitchConfig.cs
	@test -f dotnet/VoiceSwitch.Windows/SyntheticIngress.cs
	@test -f scripts/windows-say.ps1
	@test -f tests/windows/SyntheticDictationRuntimeHarness/SyntheticDictationRuntimeHarness.csproj
	@test -f tests/windows/run-tray-host.ps1
	@test -f config.example.windows.json
	@test -f BUILD.md
	@grep -q 'os(Windows)' Sources/voice-switch/Platform.swift
	@grep -q 'defaultSuperwhisperToggle' Sources/voice-switch/Platform.swift
	@grep -q 'SpeechPowerShell' dotnet/VoiceSwitch.Windows/Program.cs
	@echo "win-verify: ok (PC=$(PC) PC_NORM=$(PC_NORM) default-goal=$(.DEFAULT_GOAL))"
