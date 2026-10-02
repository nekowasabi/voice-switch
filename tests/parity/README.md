# Platform parity tests

These checks guard the contract between the macOS Swift implementation and the Windows C# implementation without editing either runtime.

Run:

```bash
python3 tests/parity/run_parity.py
python3 tests/parity/run_parity.py --require-swift
```

`--require-swift` makes the native pure-Swift fixture harness mandatory. Without it, `swiftc` absence is reported as a skip.

What the checker proves:

- Comments are stripped before static marker and call-chain checks.
- Every discovered CLI switch must be shared or covered by `allowed_cli_gaps`.
- Every sample config key that appears on only one platform must be covered by `allowed_sample_config_gaps`.
- Swift and C# config types must keep the same retained compatibility fields.
- Windows core behavior is executed through compiled `VoiceSwitch.Windows.Core` copied into a temporary project.
- Swift pure behavior is executed through production `Config.swift`, `Transcript.swift`, and `Segmenter.swift` when `swiftc` is present.
- Static call-chain checks tie the Windows CLI parse to `Main`, recognized text to `TextMatching.Decide`, and the decision to `CommandRunner.Run`.
- Static call-chain checks tie the Windows `--vad-selftest` alias to `SelfTest.Run`, and require that self-test to exercise `Segmenter`.
- Static call-chain checks tie macOS `--fire` and `--vad-selftest` branches to the actual Swift handlers, and inspect the macOS wake/stop runtime branch in `Listener.consume`.

What parity means here:

- Shared pure behavior, such as text normalization and the energy segmenter, should match through common fixtures.
- Platform runtime surfaces may differ only with a reasoned allowlist entry.
- The 29 allowed differences are accounting rows. They do not prove complete user-experience parity.
- Windows currently parses `skipWhileMicInUseBy` for config compatibility but does not enforce that runtime guard.
- Windows command mode keeps a standalone Segmenter core, while the dictation runtime uses finite SAPI recognition over PCM. Runtime VAD wiring is not the dictation file-ingress story.
- Stop words differ by runtime session model. macOS command mode fires stop only when a configured recorder is actively using the microphone. Windows legacy command mode treats configured stop words as direct grammar commands, while the Windows dictation runtime has its own session-gated stop handling.
- macOS `--check` and `--simulate` are public audio-file/sample-driver CLI modes. Windows now has public `--input-wav` synthetic PCM dictation ingress plus native probes, but it still does not claim live microphone validation or external Superwhisper intake/paste success.
- Windows has an implemented tray host. Allowed difference 27 covers the mixed device picker and login/autostart gaps. Global dictation shortcuts are allowed difference 25, target restoration is 24, and automatic completion is 26.
- See [Windows feature parity audit](../../docs/windows-feature-parity.md) for every allowed row mapped to user-visible behavior, and [Windows DSP evaluation method](../../docs/windows-dsp-evaluation-method.md) for the analysis/control lane boundary.

Static parsing limits:

- The checker is not a Swift or C# compiler front end.
- Function-body extraction is brace-based after comment stripping.
- Static checks prove required branch text is in the intended handler body, not that macOS framework code ran.
- macOS full runtime is not tested in WSL because Apple Speech, AVFoundation, CoreAudio, and the macOS SDK are unavailable.

Mutation evidence:

- Removing the Windows stop-word implementation from `TextMatching.Decide` fails the contract.
- Removing the Windows runtime `CommandRunner.Run(decision.Command)` call site fails while the core method remains.
- Bypassing the Windows self-test `Segmenter` invocation fails the contract.
- Adding a Windows-only CLI switch without classification fails.
- Adding a Windows-only config member without classification fails.
- Leaving a required implementation only in a comment fails.

Rule for adding features:

1. Add a shared fixture when the feature has pure behavior.
2. Add a call-chain contract when the feature must be wired to a runtime handler.
3. Add an allowlist entry only for an intentional platform gap.
4. Include a reason that explains the product/runtime difference, not just "not implemented".
