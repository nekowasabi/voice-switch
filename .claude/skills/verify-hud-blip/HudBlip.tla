---- MODULE HudBlip ----
\* Whether a short sound that is not a voice (a cough, a keyboard, a door, speaker playback that blips the
\* detector) can show the mic HUD. One Frame step is one pass of the frame loop: Listener.consume in
\* Sources/voice-switch/MacApp.swift, DictationRuntime in dotnet/VoiceSwitch.Windows/DictationRuntime.cs.
\* Only the HUD rule differs between the platforms (HudMac / HudWindows).
EXTENDS Naturals

CONSTANTS
    Platform,   \* "mac" or "windows"
    MinSpeech,  \* cfg.minSpeechMs in frames: voiced frames before an automatic session may open
    MinVoice,   \* Listener.minVoiceSeconds (1.5 s) in frames: HAL voice run that opens the voice activity gate
    BlipMax,    \* longest HAL run a non-voice sound produces (725-1236 ms measured 2026-10-28), < MinVoice
    Recent,     \* the 1.5 s after a MinVoice run ends during which voiceRecentlyActive() stays true
    EndSilence, \* dictation.noWakeWord.endSilenceMs in frames
    MaxAge      \* dictation.maxSeconds in frames

ASSUME MinSpeech >= 1 /\ BlipMax < MinVoice /\ Platform \in {"mac", "windows"}

Sounds == {"quiet", "noise", "voice"}
Min(a, b) == IF a < b THEN a ELSE b

VARIABLES
    sound,          \* what the mic hears this frame (the world)
    halRun,         \* frames the HAL voice detector has been on (voiceActivity.onSince), capped at MinVoice
    sinceVoice,     \* frames since a run of MinVoice or more ended (voiceActivity.lastVoiceEnd), capped at Recent
    speechFrames,   \* consume's speechFrames, capped at MinSpeech
    phraseHAL,      \* consume's phraseHAL > 0: the detector was on for some frame of the open phrase
    dict,           \* consume's `dictation`: "none", "auto" (no-wake session) or "wake" (wake word or action)
    confirmed,      \* Dictation.confirmed / confirmedSession: Apple Speech and Whisper agreed on words
    age,            \* Dictation.samples.count in frames
    dsilent,        \* Dictation.silentFrames
    voiceInEpisode  \* ghost: a voice frame was heard since the loop was last idle with no open phrase

vars == <<sound, halRun, sinceVoice, speechFrames, phraseHAL, dict, confirmed, age, dsilent, voiceInEpisode>>

TypeOK ==
    /\ sound \in Sounds /\ halRun \in 0..MinVoice /\ sinceVoice \in 0..Recent
    /\ speechFrames \in 0..MinSpeech /\ phraseHAL \in BOOLEAN /\ dict \in {"none", "auto", "wake"}
    /\ confirmed \in BOOLEAN /\ age \in 0..MaxAge /\ dsilent \in 0..EndSilence /\ voiceInEpisode \in BOOLEAN

\* lastVoiceEnd starts at .distantPast, so the gate starts closed.
Init ==
    /\ sound = "quiet" /\ halRun = 0 /\ sinceVoice = Recent /\ speechFrames = 0 /\ phraseHAL = FALSE
    /\ dict = "none" /\ confirmed = FALSE /\ age = 0 /\ dsilent = 0 /\ voiceInEpisode = FALSE

\* The detector is on for a voice, off in a quiet room, and may blip on other sound for at most BlipMax frames.
HalOK(s, h) ==
    /\ s = "voice" => h
    /\ s = "quiet" => ~h
    /\ (s = "noise" /\ h) => halRun < BlipMax

\* s: the sound; h: the HAL detector this frame (voiceBeingHeard()); w: the recognizer matched a wake word.
Frame(s, h, w) ==
    LET speech == s # "quiet"                       \* Segmenter.isSpeech: energy over threshold, or the HAL flag
        hr1    == IF h THEN Min(halRun + 1, MinVoice) ELSE 0
        sv1    == IF ~h /\ halRun >= MinVoice THEN 0 ELSE Min(sinceVoice + 1, Recent)
        \* automaticWakeAllowed() -> voiceRecentlyActive() on the Mac. The Windows opening gate (SpeechGate) is
        \* not modeled: it may open on any long-enough phrase, which only adds behaviors.
        gate   == Platform = "windows" \/ hr1 >= MinVoice \/ sv1 < Recent
    IN
    /\ HalOK(s, h)
    /\ sound' = s /\ halRun' = hr1 /\ sinceVoice' = sv1
    /\ IF dict # "none"
       \* Dictation branch: ends on endSilenceMs of non-speech or on maxSeconds. The agreement check (wordCheck /
       \* CheckWords) confirms only on a voice, modeled as confirming on the next voice frame.
       THEN LET ds == IF speech THEN 0 ELSE dsilent + 1
                a  == age + 1
            IN IF ds >= EndSilence \/ a >= MaxAge
               THEN /\ dict' = "none" /\ age' = 0 /\ dsilent' = 0 /\ confirmed' = FALSE
                    /\ speechFrames' = 0 /\ phraseHAL' = FALSE
               ELSE /\ dict' = dict /\ age' = a /\ dsilent' = ds /\ confirmed' = (confirmed \/ s = "voice")
                    /\ speechFrames' = speechFrames /\ phraseHAL' = phraseHAL
       \* Idle path: speechFrames and phraseHAL follow the phrase; a long-enough phrase opens an automatic session
       \* when the gate allows; a recognized wake word opens a wake dictation.
       ELSE LET sf == IF speech THEN Min(speechFrames + 1, MinSpeech) ELSE 0
                ph == IF ~speech THEN FALSE ELSE IF speechFrames = 0 THEN h ELSE phraseHAL \/ h
            IN /\ age' = 0 /\ dsilent' = 0 /\ confirmed' = FALSE
               /\ speechFrames' = sf /\ phraseHAL' = ph
               /\ dict' = IF w /\ s = "voice" THEN "wake"
                          ELSE IF speech /\ sf >= MinSpeech /\ gate THEN "auto"
                          ELSE "none"
    /\ voiceInEpisode' = IF dict' = "none" /\ speechFrames' = 0 THEN FALSE ELSE (voiceInEpisode \/ s = "voice")

Next == \E s \in Sounds, h \in BOOLEAN, w \in BOOLEAN : Frame(s, h, w)

Spec == Init /\ [][Next]_vars /\ WF_vars(Next)

\* Mac: the phase `defer` in Listener.consume keeps wake-word waiting visible, but hides idle phrases and
\* unconfirmed automatic sessions after body speech. Confirmed sessions show .recording / .stopping.
\* relaxedD2 waiting is represented by "wake"; heardSpeech and stopHeard are outside this model.
HudMac == dict = "wake" \/ (dict = "auto" /\ confirmed)

\* Windows: PublishPhase(session, ...). An active session shows only once Confirmed; an unconfirmed automatic
\* session and an idle phrase are Idle.
HudWindows == dict = "wake" \/ (dict = "auto" /\ confirmed)

HudVisible == IF Platform = "mac" THEN HudMac ELSE HudWindows

\* A sound that is not a voice never shows the mic HUD on its own.
NoiseNeverShowsHud == HudVisible => voiceInEpisode

\* Someone who keeps talking eventually sees the mic HUD (a fix may not just hide it).
VoiceShowsHud == <>[](sound = "voice") => <>HudVisible
====
