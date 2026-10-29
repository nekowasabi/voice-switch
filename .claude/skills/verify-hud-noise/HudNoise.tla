---- MODULE HudNoise ----
\* The mic HUD after the user stops talking (macOS). One Frame step is one pass of the frame loop in
\* Listener.consume (Sources/voice-switch/MacApp.swift): Segmenter.push, then either the dictation branch
\* or the idle path, then the phase `defer` that drives HUD.show. Names in comments are the Swift ones.
EXTENDS Naturals

CONSTANTS
    MinSpeech,  \* cfg.minSpeechMs in frames: voiced frames before an automatic session opens
    Cap,        \* Segmenter maxFrames + hangover: an open utterance longer than this emits .head and skips
    Hang,       \* cfg.hangoverMs in frames: non-speech frames that close an open utterance
    EndSilence, \* dictation.noWakeWord.endSilenceMs in frames
    MaxAge      \* dictation.maxSeconds in frames

Sounds == {"quiet", "noise", "voice"}
Min(a, b) == IF a < b THEN a ELSE b

VARIABLES
    sound,        \* what the mic hears this frame (the world)
    floorHigh,    \* Segmenter.floor * vadRatio is above the room noise, so noise reads as non-speech
    uttLen,       \* Segmenter.utt.count in frames, 0 when no utterance is open (capped at Cap + 1)
    skipping,     \* Segmenter.skipping: the open utterance passed Cap and its .head was emitted
    silent,       \* Segmenter.silent: non-speech frames at the tail of the open utterance
    speechFrames, \* consume's speechFrames (capped at MinSpeech)
    dict,         \* consume's `dictation`: "none" or "auto" (an automatic no-wake session)
    age,          \* Dictation.samples.count in frames
    dsilent,      \* Dictation.silentFrames
    confirmed     \* Dictation.confirmed: Apple Speech and Whisper agreed on words (only a voice can do that)

vars == <<sound, floorHigh, uttLen, skipping, silent, speechFrames, dict, age, dsilent, confirmed>>

TypeOK ==
    /\ sound \in Sounds /\ floorHigh \in BOOLEAN /\ skipping \in BOOLEAN
    /\ uttLen \in 0..(Cap + 1) /\ silent \in 0..Hang /\ speechFrames \in 0..MinSpeech
    /\ dict \in {"none", "auto"} /\ age \in 0..MaxAge /\ dsilent \in 0..EndSilence /\ confirmed \in BOOLEAN

\* Every Listener start and restart builds a new Segmenter(cfg:), whose floor (0.001) is below any room noise.
Init ==
    /\ sound \in Sounds
    /\ floorHigh = FALSE /\ uttLen = 0 /\ skipping = FALSE /\ silent = 0
    /\ speechFrames = 0 /\ dict = "none" /\ age = 0 /\ dsilent = 0 /\ confirmed = FALSE

Frame(s) ==
    LET idle   == dict = "none"
        \* Segmenter.isSpeech: energy above the threshold, or the HAL voice detector (voice only).
        speech == s = "voice" \/ (s = "noise" /\ ~floorHigh)
        \* The floor follows non-speech frames only while seg.adaptFloor (= dictation == nil).
        fh1    == IF ~speech /\ idle THEN s = "noise" ELSE floorHigh
        open0  == uttLen > 0
        len1   == IF open0 THEN uttLen + 1 ELSE IF speech THEN 1 ELSE 0
        sil1   == IF open0 /\ ~speech THEN silent + 1 ELSE 0
        close  == open0 /\ sil1 >= Hang                 \* .utterance or .tail
        head   == open0 /\ ~skipping /\ len1 > Cap /\ ~close
        skip1  == IF close THEN FALSE ELSE skipping \/ head
    IN
    /\ sound' = s
    /\ uttLen' = IF close THEN 0 ELSE Min(len1, Cap + 1)
    /\ skipping' = skip1
    /\ silent' = IF close THEN 0 ELSE sil1
    /\ IF ~idle
       \* Dictation branch: ends on noWakeWord.endSilenceMs of non-speech or on maxSeconds. It never looks at .head.
       \* The agreement check (wordCheck) can confirm the session only on a voice; modeled as confirming at once.
       THEN LET a  == age + 1
                ds == IF speech THEN 0 ELSE dsilent + 1
                c  == confirmed \/ s = "voice"
            IN IF ds >= EndSilence \/ a >= MaxAge
               \* A maxSeconds end with no agreed words calls rebaseFloorAfterNoWake (Segmenter.rebaseFloor acts
               \* only while skipping).
               THEN /\ dict' = "none" /\ speechFrames' = 0 /\ age' = 0 /\ dsilent' = 0 /\ confirmed' = FALSE
                    /\ floorHigh' = (fh1 \/ (a >= MaxAge /\ ~c /\ skip1))
               ELSE /\ dict' = "auto" /\ age' = a /\ dsilent' = ds /\ speechFrames' = speechFrames
                    /\ confirmed' = c /\ floorHigh' = fh1
       \* Idle path: a long enough phrase opens an automatic session and `continue`s, so a .head on that frame
       \* is never looked at. Only a .head the idle path reaches calls rebaseFloorAfterNoWake.
       ELSE LET sf == IF speech THEN Min(speechFrames + 1, MinSpeech) ELSE 0
            IN /\ confirmed' = FALSE
               /\ IF speech /\ sf >= MinSpeech
                  THEN dict' = "auto" /\ age' = 0 /\ dsilent' = 0 /\ speechFrames' = sf /\ floorHigh' = fh1
                  ELSE /\ dict' = "none" /\ age' = 0 /\ dsilent' = 0 /\ speechFrames' = sf
                       /\ floorHigh' = (head \/ fh1)

Next == \E s \in Sounds : Frame(s)

Spec == Init /\ [][Next]_vars /\ WF_vars(Next)

\* The phase `defer` with dictation.echoCancellation false: a dictation shows .waiting / .hearing / .recording,
\* and with none a voiced phrase shows .hearing. HUD.render draws all of them as the mic glyph.
HudVisible == dict = "auto" \/ speechFrames > 0

\* Once nobody talks and the room settles (silence, or steady noise such as a fan on a raw mic, from some frame
\* on), the mic HUD eventually goes away and stays away. Bursts of noise between silences are not covered.
HudHidesWhenNobodyTalks == (<>[](sound = "quiet") \/ <>[](sound = "noise")) => <>[](~HudVisible)
====
