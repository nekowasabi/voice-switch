# Validate no-wake-word on native systems

Status: Mac 未検証. Windows native runtime is also unverified. VM builds and logic tests do not establish these results.

1. On each supported macOS version, build the Swift app with the macOS 26 SDK. With Accessibility, Input Monitoring, and Screen Recording denied, verify microphone and Speech permission alone allow the input-idle query. Record the OS version and returned values before and after keyboard and mouse activity.
2. On Windows, verify GetLastInputInfo changes after keyboard and mouse input and handles lock, reconnect, and an API failure safely.
3. Check whether RegisterEventHotKey can register Superwhisper's shortcut. Record conflict failure or whether both applications receive it. Do not assume collision behavior.
4. Inspect the configured Superwhisper transcription model and mode. Record local or cloud, whether auto-paste is disabled, and whether mode restoration survives a crash and overlapping handoffs.
5. Test manual paste in a plain text editor, an active Japanese IME conversion, a password field, a rich clipboard, an empty clipboard, a changed clipboard, and a window that refuses focus. Verify no paste when a safety check fails and no clipboard loss.
6. Test Pause, Resume, Reload, Quit, microphone loss, cancellation, repeated hotkeys, and a second wake while transcription is pending. Automatic results must never change a document, call Jev, or reach tmux.
7. Test history expiry, maximum count, Clear all during transcription, repeated selection, and restart. Pause while the Mac consumer awaits recognition, then resume; old queued frames and old local results must be discarded. Confirm automatic results change the clipboard only after explicit selection.
8. Run paired strict and candidate sessions with reviewed intent and false-wake labels. Use the frozen acceptance gates in `docs/wake-acceptance.md`. Record hours of negative exposure separately from intended attempts.

## Conservative paste support

After a manual start, Windows paste currently accepts only a native `Edit` control with no password style and no active IMM composition. Browsers, custom controls, and rich clipboard formats fall back to history. Mac paste checks focused Accessibility role and subrole without reading the field's contents. It declines input-method sources, including Japanese IME, because the target's composition cannot be proven inactive without a reliable native observation. Select the history entry and paste yourself in those cases.

Ctrl+Shift+F9 opens history on both platforms. A HUD click also opens history. Windows requires selecting an entry and clicking Copy or pressing Enter. Mac has an explicit Copy button per entry. No history selection synthesizes a paste.
