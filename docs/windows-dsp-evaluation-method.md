# Windows DSP evaluation method and limits

This document records the DSP evaluation method used for a manual local study. The exact extended study corpus and helper scripts are not checked in. The reproducible repository pieces are `tests/windows/compose-fixtures.ps1`, `tests/windows/SyntheticDictationRuntimeHarness`, `tests/windows/NoiseBenchmark`, and `tests/windows/run-noise-matrix.ps1`.

The method does not use microphone, speaker, private audio, Superwhisper, dependency/model downloads, installation, or deployment. Measurements and experimental output belong in an isolated validation folder, not an installed application folder.

Final additional DSP results are recorded in [Windows DSP evaluation results 2026-10-03](./windows-dsp-evaluation-results-20261003.md). This page documents the measurement design and limits.

## Existing production option

`ConservativeWiener` is a decision-directed spectral Wiener analysis processor for PCM16 mono at 16 kHz.

- Window: square-root Hann, 320 samples, 20 ms.
- Hop: 160 samples, 10 ms.
- FFT: 512.
- Reconstruction: overlap-add, preserving original sample coordinates.
- Noise initialization: 50 consecutive full quiet hops, about 500 ms, with raw RMS below 0.005.
- Speech handling: learning pauses during speech and uses a 30-hop, 300 ms hangover.
- Noise PSD smoothing: beta `0.9900498337491681`.
- Prior smoothing: alpha `0.96`.
- Gain: prior divided by `1 + prior`, bounded by `10^(-MaxAttenuationDb/20)` through 1.
- Frequency bins: positive-frequency gains are mirrored once.
- EOF: flush preserves length.
- State: bounded.

The existing default is Off. Registered options allow 0 through 6 dB attenuation. The primary treatment uses 6 dB. The preregistered alternative uses 3 dB. Neither uses a neural model.

Declared processor status reports 160-sample availability and 480-sample publication lag. Those values are not an end-to-end measured latency. DSP wall time and SAPI request latency are measured separately.

## Scope boundary

DSP touches only the SAPI analysis/control lane. The original PCM, including its noise, is used for emitted body WAV and handoff. Therefore acoustic reduction of analysis audio is not noise reduction of the delivered file.

Any BODY CER measured by this method concerns local SAPI hypotheses. It is not actual Superwhisper transcription, not pasted text, and not delivered-transcription accuracy.

## Paired corpus and accuracy method

The primary paired corpus uses controlled synthetic proxies:

- Three Japanese synthetic bodies.
- Installed Microsoft Haruka Desktop, rate 0.
- 900 ms body-to-stop gap.
- Fixed normalized speech.
- 800 ms prefix.
- Conditions: clean; seeded Gaussian white noise at 35, 30, 25, and 20 dB; equal 60 Hz plus 120 Hz hum at 30 and 20 dB; AR1 fan-like noise at 25 dB with 0.985 feedback, 0.015 input, and 4096 warmup samples.

These are controlled synthetic proxies, not measured real-room noise. Waveforms, gain, timing, and samples stay identical across modes. Hashes and measured post-quantization SNR were recorded in the local study manifest. That exact manifest is not part of this repository.

The primary native run design is:

- 3 bodies.
- 8 conditions.
- Off, Wiener 6 dB, and Wiener 3 dB.
- 72 primary runs.
- One expected standalone-stop handoff per run.

The retained data includes separate stop/wake outcomes, strict body-string CER, recognized request boundaries, calibration, source integrity, process CPU, and request elapsed time.

CER uses a fixed clean-derived body interval and NFKC/punctuation normalization. It includes hypotheses from failed sessions. It is a surface-text metric with numeric, orthographic, and word-boundary limitations. It is not perceptual or semantic transcription scoring.

The strongest observed white-35 effect is checked separately as a post-hoc replication. Its denominator must not be pooled into the 72 primary runs.

## Isolated alternatives and acoustic metrics

An isolated standard .NET 8 probe references the unchanged stable `NoiseProcessor` DLL for Off, Wiener 6 dB, and Wiener 3 dB. It also implements two fixed standard-IIR analysis-only experiments:

- Cascaded 60 Hz plus 120 Hz notch, Q 20.
- 80 Hz second-order high-pass, Q `1/sqrt(2)`.

IIR coefficients follow the W3C Audio EQ Cookbook at `https://www.w3.org/TR/audio-eq-cookbook/`. No production enum, config, or algorithm changes are part of this method. IIR has no lookahead, but it has frequency-dependent phase/group delay.

Acoustic measurement uses known clean pairs:

- Report steady noise-only prefix 650 to 800 ms after calibration.
- Report tail beginning 500 ms after the actual stop end.
- Compare input and output RMS.
- Compare processed mixture with the original clean waveform as noise plus distortion plus phase-sensitive error.
- Compare with equivalently processed clean control as a paired difference. This is not a perfect noise decomposition for nonlinear Wiener.
- Report clean waveform distortion separately.

RMS attenuation alone is not improved ASR.

The probe warms the calibrated branch. It separates full 480-sample push wall times from partial EOF and flush. It must not label `Stopwatch` time as CPU time or invent total paced latency. Native self-tests and independent review must pass before measurement. Filtered-input IIR recognition is an ASR proxy. It does not validate production original-PCM handoff or external transcription.

## User-experience boundary

[Windows feature parity audit](./windows-feature-parity.md) maps all 28 allowed differences to user-visible features. Allowed differences are not a user waiver. Passing parity does not establish the complete macOS dictation experience.

Safe automatic external completion, paste, target restoration, and no-touch consecutive external dictation remain missing or HOLD. Global shortcuts, exclusion and mic-use guards, and device recovery also remain incomplete. These gaps are separate from DSP scores.
