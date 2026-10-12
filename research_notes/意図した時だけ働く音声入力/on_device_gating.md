# On-device gating of unintended audio for a Windows dictation tool

Scope: on-device techniques to keep non-dictation audio (backchannels, laughter, noise, the PC's own video playback) away from the transcriber. Research budget was about 15 tool calls, so coverage is partial. Most numbers are from model cards or self-reports and are flagged as such. Items not found are listed under Gaps and were not filled in from memory.

## 1. Personal VAD and target-speaker VAD

### Takeaway
Personal VAD is a published, small (about 130K parameters) frame-level model that is conditioned on a target-speaker embedding. It is a credible design, but the sources found do not give accuracy on 0.3 to 1.5 s clips, and it needs enrollment. A Silero-style generic VAD only answers "is there speech", not "whose speech".

### Cited Findings
- Personal VAD labels each frame as non-speech, target speaker, or non-target speaker. It is conditioned on a target speaker embedding or speaker verification score, and the best setup reaches about 130K parameters while beating a cascade of separate VAD and speaker-recognition networks. Source: [Personal VAD, arXiv 1908.04284](https://arxiv.org/pdf/1908.04284v4)
- Personal VAD 2.0 targets on-device ASR, covering enrollment and enrollment-less operation, streaming, and small size. Source: [Personal VAD 2.0, arXiv 2204.03793](https://arxiv.org/pdf/2204.03793v1)
- A 2024 PVAD paper argues that a cascade of VAD plus speaker verification is a poor fit for seamless integration and stresses low-latency designs. Its title and results were not retrieved. Source: [arXiv 2406.09443](https://www.arxiv.org/pdf/2406.09443)
- HyWA (Oct 2025) generates personalized weights for a few layers of a fixed VAD backbone with a hypernetwork. It is outside the 2022 to 2026 focus only in the sense of being very recent; the authors say code will be released. Source: [arXiv 2510.12947](https://arxiv.org/pdf/2510.12947v2)
- Silero VAD: about 2 MB JIT model, about 309K parameters per a third-party page. Vendor claim: under 1 ms per 30 ms chunk on one CPU thread. Source: [silero-vad on PyPI](https://pypi.org/project/silero-vad)
- Third-party throughput numbers for Silero ONNX with a single thread (self-reported, not verified): roughly 390 to 457 RTFx for the official Python build at 1024 ms chunks. Source: [fast-silero-vad on PyPI](https://pypi.org/project/fast-silero-vad/)
- A Rust benchmark on Apple Silicon found Silero about 60x slower per frame than the lighter Earshot VAD, but with better recall under noise. Source: [rlx-vad on docs.rs](https://docs.rs/crate/rlx-vad/0.2.5)

### Inferences
- Silero (ONNX, Apache-licensed, runs as a .NET ONNX Runtime call or a small sidecar) is a cheap first gate for "speech present for at least N ms". It does not separate the user from the video voice.
- Personal VAD would need a trained or fine-tuned model and enrollment data from the user. Using it for a single user is plausible, but the effort is large.

### Gaps
- No EER or accuracy for Personal VAD or Silero on 0.3 to 1.5 s clips was found.
- No license or ONNX export status for Personal VAD was verified.

## 2. Speaker verification for short utterances

### Takeaway
Speaker verification degrades as clips get shorter. Published results show roughly 7% EER at 1 s on VoxCeleb1 for one 2023 system, and no source found gives a reliable EER for 0.3 s clips. Verification of the user's voice against the video voice is possible in principle, but the sources do not show it works at this clip length without enrollment work.

### Cited Findings
- VoiceExtender (2023) reports a 1.0 s baseline EER of 6.98% on VoxCeleb1, with relative EER gains at 0.5, 1.0, 1.5 and 2.0 s. Source: [VoiceExtender, arXiv 2310.04681](https://arxiv.org/pdf/2310.04681)
- Kim et al. (Interspeech 2020) report a relative improvement of about 45% at 1 s test utterances through segment aggregation. Source: [ISCA Interspeech 2020](https://www.isca-archive.org/interspeech_2020/kim20b_interspeech.html)
- Cosine teacher-student compensation (2018) reports relative EER rising by 46% when evaluation duration fell from 3.59 s to 2.05 s on VoxCeleb1. Source: [arXiv 1810.10884](https://arxiv.org/pdf/1810.10884)
- ERes2NetV2 (June 2024) targets short-duration verification. Source: [arXiv 2406.02167](https://arxiv.org/abs/2406.02167)
- WeSpeaker reports 0.447% EER on vox1-O-clean for its deep r-vector, 0.723% for ResNet34 and 0.728% for ECAPA-TDNN with LM fine-tuning and AS-Norm. These are full-length results, not short-clip results. Pretrained ONNX (voxceleb_resnet34_LM.onnx) is available, with `infer_onnx.py` for 16 kHz wav. The pretrained VoxCeleb models are listed as CC BY 4.0. Sources: [WeSpeaker paper](https://arxiv.org/pdf/2210.17016), [pretrained models](https://github.com/wenet-e2e/wespeaker/blob/master/docs/pretrained.md), [project](https://github.com/wenet-e2e/wespeaker/)
- SpeechBrain ECAPA-TDNN (spkrec-ecapa-voxceleb), trained on VoxCeleb1+2, cosine scoring. Sources disagree on EER: an older card lists 0.69% and a later card lists 0.80% on VoxCeleb1 cleaned. The live card was not retrieved. Sources: [model card](https://huggingface.co/speechbrain/spkrec-ecapa-voxceleb), [SpeechBrain paper](https://arxiv.org/pdf/2106.04624)
- NVIDIA TitaNet-Large: about 23M parameters per the Hugging Face card, 25.3M per NGC. EER 0.66% (HF card, VoxCeleb1 cleaned trial list) and 0.68% (NGC, clean test). Embedding size 192. Input is 16 kHz mono wav. Loading uses NeMo `EncDecSpeakerLabelModel`. Sources: [HF card](https://huggingface.co/nvidia/speakerverification_en_titanet_large), [NGC](https://catalog.ngc.nvidia.com/orgs/nvidia/nemo/models/titanet_large/v1)

### Inferences
- All headline EERs are for clips of several seconds. At 0.3 s the practical EER will be much worse, so a verification score on such clips is a soft signal at best.
- For a single user, the cheapest form is a personal enrollment centroid with a cosine threshold, computed over a sliding window of at least 1 s when available. Enrollment needs several minutes of the user's speech in the same mic and room. The video voice is a different speaker, so the signal is useful in principle, but the sources do not quantify the margin on short clips.
- The Windows PC's own video audio is a different speaker, but it also has its own room colouring and codec, so the embedding may pick up channel rather than identity. This is unverified.

### Gaps
- No published EER at 0.3 to 0.5 s for ECAPA, WeSpeaker, or TitaNet was found.
- No CPU latency figures for these models were found.
- Pyannote was not searched; its speaker-embedding and EER numbers are not in this file.

## 3. Nonverbal vocalization classification (laughter, backchannels, coughs, keyboard, noise)

### Takeaway
Off-the-shelf AudioSet taggers (PANNs, YAMNet) include laughter-related and noise classes, and PANNs has a MobileNet variant for CPU use. No precision or recall figure for laughter or backchannels was found, so claims about reliable rejection cannot be made from these sources. Backchannels such as "un" and "hai" are not an AudioSet class, and no source was found for them.

### Cited Findings
- PANNs: CNN14 and related models trained on AudioSet (527 classes). Overall mAP 0.439 for Wavegram-Logmel-CNN and 0.431 for the 14-layer CNN. The MobileNet variant uses 6.7% of CNN14's FLOPs and 5.0% of its parameters, with 0.383 mAP. Code and weights are public. Sources: [PANNs, arXiv 1912.10211](https://arxiv.org/pdf/1912.10211), [audioset_tagging_cnn](https://github.com/qiuqiangkong/audioset_tagging_cnn)
- YAMNet predicts 521 AudioSet classes, including Laughter, Baby laughter, Giggle, Snicker, Belly laugh, and Chuckle. Frame scores are aggregated per clip. Source: [TensorFlow transfer learning tutorial](https://www.tensorflow.org/tutorials/audio/transfer_learning_audio)
- A 2023 vocalization study reports high precision and recall for YAMNet transfer learning on animal vocalizations. It is not a laughter benchmark. Source: [arXiv 2302.07640](https://arxiv.org/pdf/2302.07640)
- Laughter-type research (happy versus evil laughter) reports an unweighted average recall of about 0.7 for its best models, using non-YAMNet features. It is a laughter-subtype task, not a laughter-versus-speech gate. Source: [arXiv 2305.14023](https://arxiv.org/pdf/2305.14023)

### Inferences
- A tagger gate would reject a clip if its laughter, cough, keyboard or noise scores exceed the speech score. The sources do not show how often that is correct on 0.3 to 1.5 s clips, and AudioSet clips are typically 10 s, so short-clip behaviour is untested.
- Backchannels ("un", "hai", "aa") are short, voiced and speech-like. Tagger scores for "Speech" will be high, so a tagger alone will likely miss them. Length and a personal enrollment signal are the more likely discriminators. This is an inference, not a finding.

### Gaps
- No per-class AP, precision or recall for laughter or speech on AudioSet was found for PANNs or YAMNet.
- No CPU timings for PANNs or YAMNet were found.
- No published results for backchannel rejection were found.

## 4. Acoustic echo cancellation and removal of the PC's own playback

### Takeaway
WASAPI loopback gives a sample-accurate reference of what the PC is playing, which is the ground truth the spectral features lacked. It is usable in shared mode without drivers. Its limits are timing (peak meters lag one device period) and that the reference is the full mix unless the per-process loopback is used. Windows Voice Capture DSP exists but is a legacy DMO with mono-only echo cancellation.

### Cited Findings
- WASAPI loopback: open the render endpoint with eRender and set AUDCLNT_STREAMFLAGS_LOOPBACK. Loopback works only in shared mode. The mix includes everything played on the device, and behaviour depends on the hardware. Sources: [Loopback Recording](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording) (archived MSDN text was also returned)
- Application loopback (Windows 10+) restricts captured audio to a specific process and its children, using ActivateAudioInterfaceAsync. This is the direct fit for "audio from the video browser only". Source: [Application loopback audio capture sample](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/)
- Hardware "Stereo Mix" loopback is not present on all systems and is less dependable. Source: same loopback page as above.
- Voice Capture DMO: bundles AEC, microphone array processing, noise suppression, AGC and VAD, each toggleable. Echo cancellation is single-channel only, so the reference must be mono. Source mode is recommended over filter mode. The page is older documentation. Source: [Voice Capture DSP](https://learn.microsoft.com/en-us/previous-versions/bb288683(v=vs.85))
- The Voice Capture pages say DirectSound capture-buffer AEC is legacy and superseded by WASAPI and Audio Graphs. Sources: same page, and [Capture Buffer Effects](https://msdn.microsoft.com/en-us/library/ee416300(v=vs.85))
- Per a Microsoft Lync/Skype note, default-mode streams apply APO and driver DSP effects, including device-tuned AEC, while raw-mode streams bypass them. Source: [Lync and Skype audio offloading PDF](https://download.microsoft.com/download/7/C/1/7C1C33EC-748A-477D-B250-6D90A0E0AA08/Lync%20and%20Skype%20Audio%20Offloading%20of%20Digital%20Signal%20Processing%20Effects%20in%20Windows%208-1.pdf)
- IAudioMeterInformation::GetPeakValue returns a normalized 0.0 to 1.0 peak. The value is the previous device period's peak, so it lags. In exclusive mode the software meter always reports 0.0. Sources: [Peak Meters](https://learn.microsoft.com/en-ie/Windows/Win32/coreaudio/peak-meters), [GetChannelsPeakValues](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nf-endpointvolume-iaudiometerinformation-getchannelspeakvalues)
- WebRTC AEC3 has an echo path delay estimator that works from the downsampled render buffer and the capture signal. It is the reference used for removing render (speaker) audio from the microphone. Source: [echo_path_delay_estimator.h](https://webrtc.googlesource.com/src/+/main/modules/audio_processing/aec3/echo_path_delay_estimator.h)

### Inferences
- The simplest effective gate is probably not AEC. Record the WASAPI loopback (or per-process loopback of the video app) in parallel with the mic, and gate on the loopback level and on a time-aligned correlation between the two. If the mic is dominated by a copy of the loopback, the speech is playback. This needs only delay estimation, not echo modelling. This is a design inference, not a published result.
- The per-app meter (IAudioMeterInformation on a session) is coarser: it tells you a player is making sound, not whether the mic picked it up. It is a cheap extra condition.
- With headphones there is no acoustic leak, and the video voice is not in the mic at all. The problem as described implies speakers, so the loopback path is the one that matches.
- WebRTC AEC3 would need a C++ build or a port (the aec3 Rust crate exists) and reference alignment. This is more work than the correlation gate.

### Gaps
- No developer reports on the reliability of Windows communications AEC for removing media playback were found. Not searched in depth.
- No per-session IAudioMeterInformation reference was found in the sources retrieved. The Learn results covered endpoint meters only. Verify with IAudioSessionControl documentation before relying on it.
- SpeexDSP was not searched.
- Reference synchronization (latency of the render path, buffer offset, drift) was not quantified from sources.

## Key questions

### Q1. Which personal VAD or speaker-verification models give usable accuracy on 0.3 to 1.5 s clips, and what EER?
- Takeaway: No source found reports a usable EER at 0.3 s. The best dated data point is about 7% EER at 1 s on VoxCeleb1 (VoiceExtender). Full-length models are in the 0.4 to 0.8% EER range.
- Gaps: No 0.3 s results. Enrollment needs and the margin between user and video voice are not quantified.

### Q2. Can laughter, backchannels, and noise be rejected reliably with an off-the-shelf tagging model? Precision and recall?
- Takeaway: Not shown. The tagger classes exist (PANNs, YAMNet), but no laughter or backchannel precision or recall was found.
- Gaps: All of Q2's numbers.

### Q3. Most practical way to remove or detect the PC's own playback?
- Takeaway: WASAPI loopback (or per-process application loopback) as a time-aligned reference, with a correlation or energy-ratio gate, is the most direct method. AEC via Voice Capture DSP is mono and legacy. Per-session meters are lagged and coarse.
- Developer reliability reports: not found.

### Q4. Single technique with the best return for a solo developer?
- Inference, not a sourced finding: the loopback reference gate. It directly targets the failure that spectral features could not separate (the video voice), it needs no enrollment, and it uses documented WinMM/WASAPI APIs. Its cost is one extra capture stream and a delay estimate.
- Second: a minimum speech-duration gate using a Silero-class VAD, which removes most backchannels and coughs at low cost. Its accuracy on these short events is not sourced.
- Personal speaker verification is the most work for the least sourced evidence at the clip lengths involved.

## Recommended next verification steps
1. Log, for each record start, the loopback RMS and the mic RMS with a time offset, and label the existing 44 to 62% bad recordings against them. This tests Q3 on the user's own data.
2. Check current EER on the live SpeechBrain ECAPA card and the WeSpeaker pretrained table before choosing a model.
3. Benchmark Silero and a 1 s enrollment-based ECAPA or WeSpeaker ONNX on the user's own clips, with a CPU latency timer.
