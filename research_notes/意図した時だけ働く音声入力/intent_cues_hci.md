# Non-acoustic intent cues and HCI evidence for hands-free voice input on a desktop PC

Scope note: this run used about 15 tool calls. Search snippets were used for most claims. Only two pages were fetched (Look and Talk arXiv 2504.09296 succeeded; arXiv 1808.02504 PDF failed to parse, so its numbers are snippet-level). Items marked **[unverified]** were not checked against the primary text. Posture, typing/mouse patterns, focused-app and caret detection were **not searched** in this run; they appear under Gaps with only unsourced engineering notes.

## Q1. Is webcam gaze or lip-activity detection accurate and light enough to gate dictation?

### Takeaway
Webcam gaze is usable as a coarse "is the user looking at this window" gate, and a sustained dwell (about 2 s in the one study found) is the only evidence-backed way to reduce glance false positives. Precise gaze targeting is not supported by the sources found. Lip or visual VAD detects lip motion well, but lip motion is not speech, so it is weak as a sole dictation gate. No study found reports false-activation rates for a gaze- or lip-gated dictation trigger.

### Cited Findings
- MediaPipe Iris tracks iris, pupil, and eye-contour landmarks from one RGB camera in real time, and does not infer where the user is looking. — [Google Research blog](https://research.google/blog/mediapipe-iris-real-time-iris-tracking-depth-estimation/)
- A 2024 IEEE Access study on MediaPipe-based gaze (about 13 participants) needed user-specific 5-point calibration plus regression to produce gaze points. — [IEEE Access 2024 via DOAJ](https://doaj.org/article/21ad3ac72299486a9497a31f1eb60713)
- WebGazer (regression on MediaPipe-style face and iris features after calibration) was significantly less accurate than an IR commercial eye tracker and judged unsuitable for most high-precision tasks. — [CMS conference paper](https://openaccess-api.cms-conferences.org/articles/download/978-1-7923-8989-4_87)
- Valtakari et al. 2024 field test (nine screen targets): OpenFace was judged not accurate enough for screen-target gaze; OpenGaze's stated limit was targets at least about 11 degrees apart; the authors advised against OpenFace for dwell-duration measures. — [PMC10990994](https://www.ncbi.nlm.nih.gov/pmc/articles/PMC10990994/)
- The OpenFace 2.0 paper claims real-time operation from an ordinary webcam, but the search did not surface a degree-level gaze error figure from it. — [OpenFace 2.0, IEEE Xplore](https://ieeexplore.ieee.org/document/8373812)
- Hobbyist testing reports that iris-based gaze degrades with glare and dim light and suits coarse aiming. (Anecdotal.) — [HackerNoon](https://hackernoon.com/i-turned-my-eyes-into-a-game-controller-with-nothing-but-a-webcam)
- Look and Talk (AR headset, 2025 arXiv): sustained fixation of about 2 s on an avatar starts the assistant without a wake word. Reported as "effectively minimizes accidental activations", but no false-activation rate is measured. Study: 2 participants, Wizard-of-Oz, so no latency measured. Participants found it intuitive but felt 2 s was slightly long, and one asked for clearer "activation confirmed" feedback. Privacy handling is not discussed. — [arXiv 2504.09296](https://arxiv.org/html/2504.09296v1) (fetched)
- Google Look and Talk (Nest Hub Max, 2022 beta): camera-based glance activation, off by default, combined with Face Match and Voice Match to limit accidental use. Current availability not confirmed. — [Voicebot 2022](https://voicebot.ai/2022/05/12/google-nest-hub-max-debuts-look-and-talk-and-quick-phrases-features-for-skipping-wake-words)
- Google Look to Speak (Android, 2020): front-camera gaze selection with off-screen gaze gestures to pick phrases; eye detection runs on-device for privacy. This is a gaze-gesture input, not a dictation gate. — [Google blog](https://blog.google/outreach-initiatives/accessibility/look-to-speak/)
- Roider, Reisig, Gross (2018, classic, car context, 25 participants): gaze used to start speech input during a primary task; results showed strong task dependence. — [AutomotiveUI 2018 WIP PDF](https://cml.hci.uni-bamberg.de/~gross/publ/automotiveui18wip_roider_et_al_gaze_autoui18b-sub1073-cam-i19.pdf)
- Visual VAD (lip shape and LSTM, Aung and Ritthipravat 2016, classic): about 98% speech/non-speech frame classification on lip-shape features, reported as real-time capable. Evaluation is frame labelling on a small setup, not in-the-wild. — [Mahidol repository](https://repository.li.mahidol.ac.th/handle/123456789/43477)
- VVAD-LRS3 (2023): people move their lips without speaking, so lip motion does not equal voice activity; the dataset paper evaluates facial, lip, and landmark baselines. — [SciTePress 2023](https://scitepress.org/publishedPapers/2023/116129/pdf/index.html)
- Guy et al. (WildVVAD, 2020): landmark-based and optical-flow V-VAD models, with an in-the-wild dataset auto-labelled by audio VAD plus face tracking. — [arXiv 2009.11204](https://ar5iv.labs.arxiv.org/html/2009.11204)
- S-VVAD (WACV 2021): whole-frame motion segmentation, no face or lip detector needed. — [CVF WACV 2021](https://openaccess.thecvf.com/content/WACV2021/html/Shahid_S-VVAD_Visual_Voice_Activity_Detection_by_Motion_Segmentation_WACV_2021_paper.html)

### Inferences
- Gaze as a gate: the best-supported design is a dwell gate (about 1.5 to 2 s, per Look and Talk) on a binary "looking at the target window" signal. Precise point-of-gaze is not needed; window-level or half-screen resolution is the realistic target. Cost: one webcam, a landmark model at real-time rates (MediaPipe-class models run on CPU; no per-frame cloud call), and camera privacy (frames are processed locally, but the camera light and a live "watching" feel are still a cost).
- Lip activity: useful as a second signal (mouth open and moving while the user is facing the screen) to reject backchannels and video audio, not as a standalone trigger. Lip motion without speech is common, so a single visual signal will produce false positives during mouthing, chewing, and talking to people in the room.
- Accuracy numbers for dictation-gate false alarms are not in the literature found. Any threshold must be measured on the user's own recordings.

### Gaps
- No peer-reviewed study found with a false-positive or false-negative rate for a webcam gaze or lip gate on dictation. Look and Talk has only 2 participants and no rates.
- OpenFace 2.0 degree-level webcam gaze error: not found in this run. Consult the OpenFace 2.0 paper's gaze section directly.
- MediaPipe Face Mesh per-frame CPU latency on Windows: not measured in sources found (unverified).
- Lip-reading and VVAD models trained for speech-vs-silence on video datasets (LRS3, WildVVAD) were not checked for transfer to a seated desktop webcam.

## Q2. What HCI evidence exists on how false activations and unexpected sounds affect users?

### Takeaway
Interruptions have measurable cost (speed-up with more stress and frustration, Mark et al. 2008). Survey data suggest frequent false wake-ups are common and frustrating, though these are commercial surveys. Unexpected sounds risk distraction, and the literature favours restraint in auditory feedback. No academic study was found that measures the annoyance of dictation-start sound effects specifically.

### Cited Findings
- Mark, Gudith, Klocke (CHI 2008, classic): interrupted work finished faster with no quality change, but people paid with more stress, frustration, time pressure, and effort. Personality (openness, need for structure) predicts disruption cost. — [CHI 2008 paper](https://www.ics.uci.edu/~gmark/chi08-mark.pdf)
- The commonly quoted "about 23 minutes to resume" figure is not in the CHI 2008 abstract; treat it as unverified. — [CHI 2008 abstract context](https://www.interruptions.net/literature/Mark-CHI08.pdf)
- Siegert (Interspeech 2021): prosodic variation affects accidental triggers of a commercial assistant; the wake-word trade-off is false acceptance vs. false rejection. — [ISCA archive](https://www.isca-archive.org/interspeech_2021/siegert21_interspeech.pdf)
- Controlled tests report accidental activations as often as 19 times a day, mostly from words rhyming with the wake word (press coverage of an academic study; secondary). — [TBS News](https://www.tbsnews.net/node/47709)
- Ruhr-Bochum / Max Planck work identified more than 1,000 phrases that can trigger assistants, and notes that systems are tuned to fail on the side of activating. (Press coverage.) — [Voicebot](https://voicebot.ai/?p=18781)
- The Manifest survey (501 US users, commercial, not peer-reviewed): nearly two-thirds of users had accidentally triggered their assistant within a month; only 5% said they were never frustrated. — [Voicebot 2020](https://voicebot.ai/2020/01/09/voice-assistants-accidentally-awakened-by-64-of-users-a-month-survey)
- Earcons vs. auditory icons (Amer and Johnson, classic): auditory icons were learned faster, earcons were preferred, but auditory icons were judged more irritating after repeated hearing. — [NAU record](https://experts.nau.edu/en/publications/earcons-versus-auditory-icons-in-communicating-computing-events-l/)
- A CHI 2019 study with deaf and hard-of-hearing users: 92% wanted visual and haptic feedback alongside sound; they did not want sound-only alerts. — [Findlater et al. CHI 2019 PDF](https://soundability.eecs.umich.edu/img/portfolio/Findlater_WerableSurveyForDHH-CHI2019.pdf)
- Look and Talk (2025, n=2): participant asked for more prominent "activation confirmed" feedback; one reported the no-wake-word approach valued in loud or public settings. — [arXiv 2504.09296](https://arxiv.org/html/2504.09296v1)

### Inferences
- A start sound on every automatic capture repeats the interruption cost that the user already reports. A silent visual indicator (tray or overlay colour, no sound) fits the finding that feedback sounds, if repeated, become irritating, and that DHH users want non-audio channels.
- Review-later designs (draft, then promote) move the cost of a false activation from the user's attention at the moment of speech to a later cheap decision. This is consistent with the Mark et al. finding that the cost of interruption is in the resumption, not the event itself. Inference only; no study tested this.
- Frequency of false activations (survey: about 2/3 of users per month) suggests a trust cost. No study found quantifies trust loss per false activation.

### Gaps
- No CHI, UIST, CSCW, or IUI paper found on annoyance from dictation start-sounds specifically.
- Li and Hwang (extra auditory feedback can hurt usability; auditory feedback becomes noise when unnecessary): source found only as a search-snippet summary; no URL retained. Verify before citing.
- Auditory icons outperforming earcons in longitudinal mobile notifications: a claim seen in search results, but the source link was not confirmed. Treat as unverified.
- Quantitative trust-loss measures per false activation: not found.

## Q3. Are there studies or systems using retroactive selection or "always record, act on command" for voice?

### Takeaway
No CHI or UIST study of a lookback or retroactive-nomination voice UI was found. The closest evidence is (a) an ASR paper that uses later utterances to revise earlier output (technical, not a user study), (b) a patent for re-utterance correction, and (c) user studies of always-listening devices that ask for control over what is processed. The retroactive pattern is therefore untested in HCI as far as this search could show.

### Cited Findings
- Takeda and Komatani, Retrospective Speech Recognition (IWSDS 2026): later utterances revise earlier recognition output; reported character-error-rate gain is small (about 0.10 points, snippet only). Technical ASR evaluation, no user study. — [IWSDS 2026 page](https://preview.aclanthology.org/credits/2026.iwsds-1.20)
- US patent 7444286 (re-utterance recognition): the user selects part of the output and re-speaks it to correct it. Patent, no study. — [USPTO](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/7444286)
- Gaze-initiated voice control patent (US 11423896): drops the wake word and uses gaze at a target to start command capture. Patent, no evaluation. — [USPTO](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/11423896)
- Investigating Users' Preferences and Expectations for Always-Listening Voice Assistants (IMWUT 2019, n=178 survey): willingness to share depends on content sensitivity, perceived benefit, and prior ownership of assistants. — [UC Berkeley BLUES](https://blues.cs.berkeley.edu/?p=701)
- An NSF-listed study of runtime permission prompts for always-listening apps (n=214): participants wanted control over actions and data, but prioritized an interruption-free experience over fine-grained control of what the device hears. Paper identity from the NSF record; not opened. — [NSF PAR 10175454](https://par.nsf.gov/biblio/10175454)
- Wizard-of-Oz CSCW 2022 study (n=200) on whether users can detect unauthorised listening: detection ranged from 7.7% to 75% by attack type, and groups did better than individuals. — [CSCW 2022 PDF](https://people.eecs.berkeley.edu/%7Edaw/papers/listen-cscw22.pdf)

### Inferences
- Lookback (buffer, then pick) avoids the start-latency and false-start cost, and the user supplies the intent signal at selection time. Strongest analogue is the always-listening preference finding: users accept continuous capture when they control use of the data. The privacy cost is that continuous audio is retained, even if only in a local ring buffer; the CSCW 2022 finding suggests users may not notice unauthorised listening.
- "Leave Superwhisper and judge with an LLM": the device-directed speech literature suggests text and prosody features are informative for "addressed to device" decisions (see Q4). An LLM judge on ASR text may be a workable filter, but it adds a network call (latency and sending transcripts off-device). No source measured this combination for dictation.

### Gaps
- No CHI/UIST/CSCW/IUI evaluation of lookback or retroactive voice nomination found.
- No user-study data on "promote a draft with a key" workflows found.
- The exact Interspeech or IWSDS improvement numbers were not checked in full text.

## Q4. What feedback design do studies recommend for ambient or always-listening systems?

### Takeaway
Recommendations converge on three points: keep auditory feedback sparse, prefer visual or haptic indicators for frequent events, and give explicit confirmation for the rare costly action. Device-directed speech detection research shows that prosody and ASR-side features are complementary and can reduce false acceptance, which argues for a multimodal gate rather than sound feedback as a substitute.

### Cited Findings
- Device-directed utterance detection (Amazon-era, arXiv 1808.02504): two LSTMs on acoustic and ASR-hypothesis features plus a DNN reached an equal-error rate of 5.2%, a 44% relative improvement over the best single source. Snippet-level figures; full PDF did not parse. **[unverified]** — [arXiv 1808.02504](https://arxiv.org/pdf/1808.02504)
- Apple modality dropout (Krishna et al.): prosody improved device-directed speech detection by up to 8.5% in false-acceptance rate at a fixed operating point, with non-linear intermediate fusion. Snippet-level; **[unverified]**. — [Apple ML Research](https://machinelearning.apple.com/research/modality-dropout)
- A 2026 device-addressed speech paper reports that users speak to devices louder, slower, with higher mean F0 ("hyperarticulation"), and that rhythm and vocal-effort cues work without ASR context. Attribution to the specific arXiv record (2604.08412) is likely but **[unverified]**. — [arXiv 2604.08412](https://arxiv.org/pdf/2604.08412)
- Look and Talk: a fixed dwell time separates intentional gaze from glances; too short risks accidental activations, too long slows interaction. — [arXiv 2504.09296](https://arxiv.org/html/2504.09296v1)
- Google Look and Talk relied on a per-user voice match to cut accidental triggers from other people. — [Voicebot 2022](https://voicebot.ai/2022/05/12/google-nest-hub-max-debuts-look-and-talk-and-quick-phrases-features-for-skipping-wake-words)
- DHH wearable study: visual plus haptic preferred over sound-only alerts. — [CHI 2019 PDF](https://soundability.eecs.umich.edu/img/portfolio/Findlater_WerableSurveyForDHH-CHI2019.pdf)
- Mark et al. 2008: costs appear as stress and frustration when interrupted, so frequent alerts should be avoided. — [CHI 2008 PDF](https://www.ics.uci.edu/~gmark/chi08-mark.pdf)

### Inferences
- Recommended design for the user's tool, based on the above (inference, not a tested finding): (1) silent visual state for the automatic mode, (2) a sound only on explicit commits, not on captures, (3) a visible "confirm" affordance (look-and-hotkey or a single key) before any injection into the focused app, (4) a lookback buffer with a visible list of recent captures.
- Prosody features (loudness, rate, F0) may help separate directed dictation from backchannels and laughter, but they were built for devices with ASR and dialogue context. The 44 to 62% unintended-recording rate is in the same regime as the false-acceptance problem these papers solve, so a learned gate may need labelled examples from the user.
- Mixed-initiative: no study found on "confirm before act" timing for voice input. The permission study suggests users prefer few interruptions, so confirmation should be a single, cheap action rather than a dialogue.

### Gaps
- Not found: direct comparisons of sound vs. silent visual indicators for always-listening dictation.
- Not found: a published confirm-before-act study for voice dictation (mixed-initiative, CHI/IUI).
- Not searched in this run: posture (chair or seat cues), keyboard and mouse activity as intent predictors, focused-application and text-caret state as signals, and their accuracy or privacy cost. Engineering notes only, unsourced: Windows UI Automation and caret APIs could expose focused-control type; key-activity idle timers already exist in the current tool. These need separate research before any accuracy claim.
- Miniotas et al. (gaze plus speech for small targets, about 93% pointing performance): attribution to a specific ETRA 2006 record not confirmed. Source: [ETRA 2006 page](http://www.yorku.ca/mack/etra2006.html), **[unverified]**.
