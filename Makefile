APP     := VoiceSwitch.app
BUILT   := .build/$(APP)
DEST    ?= $(HOME)/Applications
CONFIG  ?= $(HOME)/.config/voice-switch/config.json
ID      := local.voice-switch
# Earlier versions ran as a LaunchAgent; install removes it so two copies never listen at once.
OLD_PLIST := $(HOME)/Library/LaunchAgents/$(ID).plist

# Default `make` quits, rebuilds and relaunches the app (same as focusbm).
.DEFAULT_GOAL := relaunch

.PHONY: build app install relaunch uninstall logs

relaunch: install

build:
	swift build -c release

app: build
	rm -rf $(BUILT)
	install -d $(BUILT)/Contents/MacOS
	install .build/release/voice-switch $(BUILT)/Contents/MacOS/voice-switch
	cp Info.plist $(BUILT)/Contents/Info.plist
	# Ad-hoc signatures default to a cdhash requirement, so every rebuild looked like a new app to TCC and
	# lost the microphone/Accessibility grants. Pin the requirement to the bundle identifier instead.
	codesign --force --sign - --identifier $(ID) -r='designated => identifier "$(ID)"' $(BUILT)

install: app
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

uninstall:
	-osascript -e 'quit app id "$(ID)"' 2>/dev/null
	rm -rf $(DEST)/$(APP)

logs:
	tail -f $(HOME)/Library/Logs/voice-switch.log
