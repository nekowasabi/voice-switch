# Device-directed speech detection (DDSD) and addressee detection without a wake word

Scope: how a system decides, with no wake word, that an utterance is meant for the device. Priority: 2023 onward. Older work is labeled "(older)". Numbers are as reported by each paper on its own data. Papers are not comparable across rows. Where a number was taken from a search snippet and not from the fetched text, that is noted.

## 1. Apple: DDSD, acoustic and LLM approaches

### Key question 1: Apple's published approaches

### Takeaway
Apple has the most published DDSD work. It moved from acoustic models with ASR-distilled teachers (2022), to LLMs fed with ASR n-best text and decoder signals (2023 to 2024), to context-aware follow-up detection (2024), to a 2025 adapter-based distillation method. Its models target Siri-class devices and are reported as on-device for the trigger system, but the DDSD papers do not state a per-model latency.

### Cited Findings
- Touch-invoked DDSD (Garg et al., Interspeech 2022). Targets speech to a device without a wake word, after button-press activation. Uses weakly labeled data and distills an ASR-based LatticeRNN teacher into an acoustics-only student. Reports a 66% gain in EER over the base acoustics-only model, and a further 20% from ensembling the two. Gain type (relative or absolute) and absolute EER values are not stated in the abstract. Model size not stated. — [arXiv 2203.15975](https://arxiv.org/pdf/2203.15975); [Interspeech 2022 page](https://www.isca-archive.org/interspeech_2022/garg22_interspeech.html); [Apple ML research page](https://machinelearning.apple.com/research/device-directed-speech)
- Multimodal LLM for DDSD (Wagner, Churchill, Sigtia; Apple with TH Nürnberg, arXiv Dec 2023). Inputs: ASR 1-best text, ASR decoder signals, and audio-encoder features (Whisper or a UAD encoder). A 7B decoder-only LLM (Falcon-7B or RedPajama-7B) is frozen; mapping networks and LoRA (rank 8) are trained with prefix tuning. Best system (Multi 4, Falcon + UAD backbone, 80k training examples): EER 8.23%. With Whisper as the audio encoder: EER 8.80%. The paper says these are ~12% and ~16% relative improvements over the audio-only models. Decoder-signal-only EER is 35 to 37%. The text-only baseline is worse than audio-only in the paper. The authors also say a small training set (down to 1k examples in one ablation) still works. Whisper tiny is ~39M params; Whisper large is ~1.5B (Appendix D). — [arXiv 2312.03632 (HTML)](https://arxiv.org/html/2312.03632v1)
- Multimodal LLM DDSD, follow-on (Wagner et al., arXiv Mar 2024). Abstract: relative EER improvements of "up to 39% and 61%" over text-only and audio-only models, and up to a further 18% from scaling the LLM and LoRA. LLM and encoder names are not in the abstract; read the full paper before quoting them. — [arXiv 2403.14438](https://arxiv.org/abs/2403.14438); [PDF](https://arxiv.org/pdf/2403.14438v1)
- LLM DDSD for follow-up conversations (Rudovic, Dighe, Su et al., Apple; arXiv Nov 2024; NeurIPS 2024 AFM workshop). Input: ASR 1-best and n-best (n=8) hypotheses from a Conformer/CTC/attention ASR with FST-LM rescoring, plus the first query as context. Two variants: (a) prompting a Vicuna-7B-v1.3 LLM; (b) a classification head on the frozen LLM, optionally with LoRA (rank 8, 4.1M params, 0.06% of the 7B). Head adds ~8k params. Reported: 20 to 40% reduction in false accepts at 10% fixed false rejects when modeling the initial query plus ASR uncertainty, compared with modeling the follow-up alone. Dataset: ~19k recordings, 1.3k participants, ~245k utterance pairs, device-directed:undirected ratio ~1:4, average duration 3.5 s. Inference ran on 4 A100 GPUs, so this is server-class. The paper says the approach is text-only. — [arXiv 2411.00023 (HTML)](https://arxiv.org/html/2411.00023v2); [Apple research page](https://pr-mlr-shield-prod.apple.com/research/device-directed)
- Adaptive knowledge distillation for DDSD (Chi, Pesce, Chang, Rudovic, Garg et al., Apple; arXiv Aug 2025). Frozen large ASR acoustic encoder as teacher, with task-specific adapters; student trained jointly. Works on transformer and conformer students. Reported: +26% EER improvement on keyword invocations, +19% on keyword-free follow-ups. Absolute EER values, adapter counts, and dataset not stated in the fetched abstract. — [arXiv 2508.02801](https://arxiv.org/pdf/2508.02801)
- Apple trigger system (Aug 2023). Describes Siri directed-speech detection that analyzes the full utterance, including the trigger phrase, to cut false triggers. States it runs on-device across iPhone, HomePod, AirPods, Watch. — [Apple ML research](https://machinelearning.apple.com/research/voice-trigger)

### Inferences
- Apple's text-only follow-up result (2411) is the closest published match to "ASR text plus context, no audio model", but its gain is relative to a follow-up-only baseline, not an absolute EER. No absolute EER was extracted from the fetched text.
- The 2312 paper suggests audio features beat text alone for this task, and the best results combine text and audio. Their 7B LLM is not a realistic on-device model on a typical Windows PC without a GPU (inference).

### Gaps
- No Apple absolute FAR/FRR figures for on-device models were found in the fetched text.
- No published on-device latency for the 2023 to 2025 Apple DDSD models.

## 2. Amazon (Alexa)

### Key question 1 (continued): Amazon's published approaches

### Takeaway
Amazon's published DDSD work is mostly pre-2023 and uses ASR-derived lexical and decoder features with acoustic models. The 2020 ICASSP paper reports a 14% EER gain. Follow-Up Mode (about 5 s listening window after a response) is the shipped product behavior.

### Cited Findings
- ICASSP 2020 (Improving Device Directedness Classification of Utterances with Semantic Lexical Features). Adds semantic and syntactic lexical features to a model that already uses ASR components. Reports a 14% EER improvement over the best baseline. Amazon Science blog page: [How Alexa knows when you're talking to her](https://www.amazon.science/blog/how-alexa-knows-when-youre-talking-to-her). The blog says Follow-Up Mode lets users ask a follow-up without repeating "Alexa", and that the device must separate device-directed speech from TV and children's voices. (older, 2020)
- Device-directed utterance detection (Mallidi et al., Interspeech 2018). Two LSTMs (one on acoustic features, one on ASR 1-best text) plus a DNN that also takes ASR decoder features. EER: decoder features alone 9.3%, acoustic embeddings 10.9%, 1-best text 20.1%, combined 5.2% (about 44% relative gain over the best single input). Dataset size and on-device status not stated in the fetched text. Small models (LSTM-scale), so it is the most realistic architecture for a PC. (older, 2018) — [arXiv 1808.02504](https://arxiv.org/pdf/1808.02504)
- Patents: "Device-directed utterance detection" (US 11551685 and US 12236950) describe an on-device interrupt detector using ~300 to 500 ms audio windows to decide whether speech is device-directed, with no wake word. Cited from search snippets, not read in full. — [USPTO 11551685](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/11551685)

### Inferences
- Amazon's 2018 combined lexical-plus-acoustic design is an architecture a developer could reimplement with small models. Its numbers come from Amazon's data and are not transferable.

### Gaps
- No Amazon LLM-based DDSD paper was found in search. An attempted search for Amazon follow-up LLM work returned only Apple papers.
- No Amazon 2023 to 2026 DDSD publication was confirmed.

## 3. Google

### Key question 1 (continued): Google's published approaches

### Takeaway
Google's best public description is the Look and Talk feature (2022). It is multimodal (face and gaze from camera, then on-device audio and text), and runs on-device. It shows the design pattern (cheap visual gate, then audio and intent) but publishes no accuracy numbers in the fetched text. Continued Conversation is a shipped feature with a ~10 s window, and a patent covers its multi-device form.

### Cited Findings
- Look and Talk (Google Research blog, July 2022). "First multimodal, on-device Assistant feature" for Nest Hub Max. Phase one uses face match and gaze to decide the user is looking at the device. Phase two, still on device, runs ASR, Voice Match, an intent model on the non-lexical audio (pitch, speed, hesitation), and a text intent model, plus visual context. Forgoes streaming audio to the server. Intent models work on partial utterances, so end-to-end latency is said to be comparable with hotword systems. Personalization uses hotword-interaction data, on device. No EER/FAR numbers in the fetched text. — [Google Research blog](https://research.google/blog/look-and-talk-natural-conversations-with-google-assistant/)
- Continued Conversation: support documentation says Assistant listens for follow-ups for ~10 s and uses voice and sensor signals. Date and details: [Google support](https://support.google.com/assistant/answer/10004407) (found in search; not fetched). Patent US 12347420 "Processing continued conversations over multiple devices" (Carbune, Sharifi). — [Google Patents](https://patents.google.com/patent/US12347420)

### Inferences
- Google's approach fuses a non-audio gate (vision or sensors) with an on-device audio intent check. This matches the user's situation only if a camera or other gate is available. The user's keyboard and mouse idle signal plays the same role as a gate.

### Gaps
- No Google research paper on DDSD classification with metrics was found.
- Look and Talk accuracy and the eight-model count were not confirmed in fetched text.

## 4. Meta

### Key question 1 (continued): Meta's published approaches

### Takeaway
No Meta paper on wake-word-free DDSD was found in search. Treat Meta as unanswered.

### Gaps
- Search for Meta DDSD returned only Amazon (2018) and Apple (2022) work. No Meta publication was verified.

## 5. Academic addressee detection: human vs device speech

### Key question 2: Academic results, especially short utterances and backchannels

### Takeaway
Classic addressee-detection work (2008 to 2020) shows prosody and lexical features each separate computer-directed from human-directed speech, and that a combined model is best. The corpora used in older work have confounds. Recent (2025 to 2026) LLM results on addressee recognition are weak or moderate. No 2023 to 2026 paper on backchannels or laughter as device-directed negatives was found.

### Cited Findings
- Shriberg et al. (Interspeech; older, year not verified, cited via search snippet). Lexical features EER 23.1%, prosodic 12.6%, combined 11.1% for telling computer-directed from human-directed speech in human-human-computer dialog. Snippet came from a search summary; the original paper was not fetched. Search source: [Microsoft Research](https://www.microsoft.com/en-us/research/?p=163170)
- Akhtiamov et al. (Sensors, 2020). Argues earlier acoustic corpora used role-swapped dialogues that confound human-vs-machine speech. Built the Restaurant Booking Corpus. — [DOAJ listing](https://doaj.org/article/d860f100bc5e47e590b7f2d15577d865) (older, found via search, not fetched)
- Raveh et al. (ESSV 2019; older). VACC corpus with an Amazon Echo Dot. Intensity differences between human-directed and device-directed speech were significant in 74% and 89% of cases respectively. — [PDF](https://amlap.coli.uni-saarland.de/~steiner/publications/ESSV2019Raveh.pdf) (older, search snippet only)
- Inoue, Lala et al. (arXiv 2501.16643, Jan 2025). GPT-4o on addressee recognition in triadic conversation: only slightly better than chance; explicit addressee in ~20% of turns. Search snippet only. — [arXiv 2501.16643](https://export.arxiv.org/pdf/2501.16643)
- Fukuda et al. (arXiv 2606.17542, 2026). Text LLMs, multimodal LLMs, and humans on addressee detection, turn-change, and next-speaker prediction using the AMI meeting corpus. Multimodal LLM beats text-only on addressee detection but stays below humans. Search snippet only. — [papers.cool](https://papers.cool/arxiv/2606.17542)
- Gaze and backchannel study (arXiv 2607.15648, 2026). Reports gaze and backchannels both associate with addressee; continuous address levels fit better than discrete labels. Search snippet only. — [papers.cool](https://papers.cool/arxiv/2607.15648)

### Inferences
- Human-directed backchannels ("uh-huh", laughter) are not well covered by device-directed datasets. The published DDSD work treats undirected speech as a class but does not report per-class accuracy on short backchannels, so this is an open gap for the user's case.
- Text-only addressee signal on meeting-style data is weak. The device-directed task is easier because the device has a conversation context, but no source reports text-only accuracy on short isolated utterances.

### Gaps
- No 2023 to 2026 paper reports per-class (backchannel, laughter, noise, TV) results for DDSD.
- Shriberg et al. and Raveh et al. numbers were not verified in the original PDFs.

## 6. Recent on-device and small-system work

### Key question 3 (partial): Lightweight and on-device systems

### Takeaway
The only recent published on-device system found is SAS (arXiv 2604.08412, 2026). It reports <20 MB memory, <150 ms, on ARM Cortex-A, F1 0.86 (audio only) and 0.95 (audio+video). It reports no EER, FAR or FRR. Its evaluation is internal and proprietary.

### Cited Findings
- Selective Attention System (SAS), Sequential Device-Addressed Routing (SDAR). Decides before transcription whether to forward audio, using recent interaction history. Audio is primary; optional camera. Under 150 ms, runs on ARM Cortex-A. Memory under 20 MB. Held-out 60 h English multi-speaker test set (internal, proprietary). Audio-only F1 0.86 (precision 0.89, recall 0.83). Audio+video F1 0.95 (precision 0.97, recall 0.93). Removing causal interaction history drops audio+video F1 to 0.57 ± 0.03. Open-source status not stated. — [arXiv 2604.08412](https://arxiv.org/pdf/2604.08412)
- attenlabs SAA (commercial, Apache-2.0 client SDK only). Hosted classifier that runs before STT, no wake word, per-utterance decision from audio (optionally low-rate video). Model is a hosted service and not open source. Accuracy: the page cites the SAS report's F1 numbers. Says it "fails closed under distribution shift" and that cross-lingual recall is a known limitation. On-device is an enterprise program. — [GitHub attenlabs/saa-sdk](https://github.com/attenlabs/saa-sdk)

### Inferences
- The SAS design (routing on interaction history, small footprint) is the closest published match to the user's "gate by context before handing off" idea. Its ablation says history matters most.

### Gaps
- SAS model architecture and parameter count not stated in abstract. Read the full PDF before relying on it.

## 7. Text-only signals (ASR transcript plus context, e.g. typing activity)

### Key question 4 (partial): Can text alone work?

### Takeaway
Published text-only results exist: Apple 2411 uses ASR text and prior-turn context with a 7B LLM (relative FAR reductions of 20 to 40%), and Wagner 2312 reports text-only is weaker than audio-only in their setup. No published study found uses keyboard or mouse activity as a DDSD input.

### Cited Findings
- Text-only context (Apple 2411): see section 1. Key number: 20 to 40% reduction in FAR at 10% FRR when adding the previous query's text and ASR uncertainty. — [arXiv 2411.00023](https://arxiv.org/html/2411.00023v2)
- Text-only vs audio (Wagner 2312): text-only is worse than audio-only in the paper's Table 1 (text-only EER not extracted numerically from fetched text). — [arXiv 2312.03632](https://arxiv.org/html/2312.03632v1)
- Mallidi 2018: ASR 1-best text alone gives EER 20.1%, the weakest single input. — [arXiv 1808.02504](https://arxiv.org/pdf/1808.02504)

### Inferences
- A transcript-only classifier is likely to be weak on single short words like "yes" or "uh-huh", since the 2018 and 2023 numbers show text alone trails audio. Inferred, not measured.
- Typing and idle state are not in any DDSD paper. They could be a strong prior for the user's case (the user has already observed high false rate in idle-triggered recordings), but no accuracy figure exists. This is an open research gap, and the user should measure it on their own logs.

### Gaps
- No text+typing-context accuracy number was found.

## 8. Open models and code

### Key question 4: Available code and models

### Takeaway
No open-weight DDSD model with weights and training code was found. The only published-matching repository is a hosted SDK. Open wake-word engines (openWakeWord, Howl) are not DDSD and are not a substitute for it.

### Cited Findings
- attenlabs/saa-sdk: client SDKs (Apache-2.0), hosted model (not open). — [GitHub](https://github.com/attenlabs/saa-sdk)
- openWakeWord and Howl: open wake word engines, not device-directed detection. — [openWakeWord](https://github.com/cnhup/openWakeWord); [Howl](https://github.com/castorini/howl)
- Whisper tiny (~39M params) and Whisper large (~1.5B) are the audio encoders named in Wagner 2312 Appendix D. — [arXiv 2312.03632](https://arxiv.org/html/2312.03632v1)

### Inferences
- A feasible on-PC build is a small reimplementation of the Mallidi 2018 design (two small networks on acoustic features and ASR text, plus decoder features) with a tiny gate network. This is inference. The user would need labeled data from their own logs.
- A 7B LLM (Apple 2312, 2411) is not realistic for real-time on a typical Windows PC without a capable GPU. Quantized 1 to 3B models were not evaluated in any source found here. Not verified.
- Latency targets from the literature: SAS under 150 ms on ARM Cortex-A. Google Look and Talk claims latency comparable to hotword systems, with no number given.

### Gaps
- No open DDSD weights or code found. No real-time CPU benchmark of any DDSD model on Windows was found.

## 9. Mapping to the user's problem (inference, not sourced)

- The user's automatic-wake recordings (44 to 62% backchannel, laughter, noise, video) are the undirected class that these papers train against. Published results cover this in general terms, not with per-class breakdown.
- Strongest available signal in the literature for "addressed or not" is audio plus ASR text plus context (interaction history). The user's typing and idle timeline is a context signal that no paper tests.
- A practical test: log candidate features (audio energy, duration, SAS-style history, idle time, ASR text) on their own labeled logs, then measure FAR and FRR at a chosen operating point. Published EERs are not transferable.

## Source list (all URLs cited above)
- https://arxiv.org/pdf/2203.15975
- https://www.isca-archive.org/interspeech_2022/garg22_interspeech.html
- https://machinelearning.apple.com/research/device-directed-speech
- https://arxiv.org/html/2312.03632v1
- https://arxiv.org/abs/2403.14438
- https://arxiv.org/pdf/2403.14438v1
- https://arxiv.org/html/2411.00023v2
- https://pr-mlr-shield-prod.apple.com/research/device-directed
- https://arxiv.org/pdf/2508.02801
- https://machinelearning.apple.com/research/voice-trigger
- https://www.amazon.science/blog/how-alexa-knows-when-youre-talking-to-her
- https://arxiv.org/pdf/1808.02504
- https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/11551685
- https://research.google/blog/look-and-talk-natural-conversations-with-google-assistant/
- https://support.google.com/assistant/answer/10004407
- https://patents.google.com/patent/US12347420
- https://www.microsoft.com/en-us/research/?p=163170
- https://doaj.org/article/d860f100bc5e47e590b7f2d15577d865
- https://amlap.coli.uni-saarland.de/~steiner/publications/ESSV2019Raveh.pdf
- https://export.arxiv.org/pdf/2501.16643
- https://papers.cool/arxiv/2606.17542
- https://papers.cool/arxiv/2607.15648
- https://arxiv.org/pdf/2604.08412
- https://github.com/attenlabs/saa-sdk
- https://github.com/cnhup/openWakeWord
- https://github.com/castorini/howl
