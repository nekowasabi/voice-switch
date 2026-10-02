APP     := VoiceSwitch.app
BUILT   := .build/$(APP)
DEST    ?= $(HOME)/Applications
CONFIG  ?= $(HOME)/.config/voice-switch/config.json
ID      := local.voice-switch
# Earlier versions ran as a LaunchAgent; install removes it so two copies never listen at once.
OLD_PLIST := $(HOME)/Library/LaunchAgents/$(ID).plist

.PHONY: build app install uninstall logs

build:
	swift build -c release

app: build
	rm -rf $(BUILT)
	install -d $(BUILT)/Contents/MacOS
	install .build/release/voice-switch $(BUILT)/Contents/MacOS/voice-switch
	cp Info.plist $(BUILT)/Contents/Info.plist
	codesign --force --sign - --identifier $(ID) $(BUILT)

install: app
	-launchctl bootout gui/$$(id -u)/$(ID) 2>/dev/null
	rm -f $(OLD_PLIST) $(HOME)/.local/bin/voice-switch
	-osascript -e 'quit app id "$(ID)"' 2>/dev/null
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
