#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
command -v dotnet >/dev/null
command -v swiftc >/dev/null

dotnet build dotnet/VoiceSwitch.Windows -c Release --ignore-failed-sources
dotnet run --project dotnet/VoiceSwitch.Windows.Tests -c Release
dotnet run --project dotnet/VoiceSwitch.WakeReplay -- --self-test
python3 scripts/overnight_metrics.py --self-test
python3 tests/test_wake_metrics.py
python3 tests/test_wake_acceptance.py
python3 tests/parity/run_parity.py --require-swift
python3 tests/url-actions/run.py --require-swift
python3 scripts/superwhisper_mode_proof.py
for project in NativeDictationRecognizerProbe SyntheticDictationRuntimeHarness TrayDiagnosticsProbe; do
    dotnet build "tests/windows/$project" -c Release --ignore-failed-sources
done
dotnet run --project tests/windows/SyntheticDictationRuntimeHarness -c Release -- --self-test-validation
swiftc -frontend -parse -target arm64-apple-macosx26.0 Sources/voice-switch/*.swift
printf '%s\n' 'VM checks passed. Mac native build, UI/audio, permissions, and real-world acceptance remain unverified.'
