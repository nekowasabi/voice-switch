# Evaluate cough filtering on Windows and macOS

Use `scripts/cough_acceptance.py` to compare local observations from the same labeled recordings on both native apps. Python 3.9 or newer is sufficient. The script uses the standard library and makes no network requests.

No human cough corpus or native results are included in this repository. The unit tests contain generated WAV bytes and invented observations. They prove evaluator behavior, not cough detection or application acceptance.

## Prepare a private corpus

1. Create a directory outside the repository for audio, labels, logs, and observations. Do not commit these files or upload them with a test report.
2. Collect at least 20 distinct recordings of natural coughs and throat clears. Include both labels. Record only if comfortable. Do not induce coughing, hold your breath, or cause distress for a test.
3. Collect at least 20 distinct short-speech recordings. Include ordinary short commands and legitimate utterances such as `はい`, `うん`, `何`, `あっ`, and `ああ`. Include quiet voices and variation in duration. Keep the spoken part short, roughly 100–1500 ms, and record its duration in your local notes.
4. Include quiet lead-in and enough trailing silence for the configured session timeout. Use the same clip boundaries for all runs.
5. Export each clip as a nonempty, uncompressed, mono 16 kHz PCM16 WAV. Keep each file below 64 MiB.
6. Have a person listen to every clip before testing the candidate. Label each clip `cough`, `throat_clear`, or `short_speech`. Separate mixed cough-and-speech clips from this first evaluation. Do not infer labels from the recognizer's output.
7. Assign opaque case IDs such as `case-001`. Use opaque aliases for people and devices. The report includes case IDs and run IDs, so do not put names or transcript text in those fields.
8. Freeze the labels and acceptable transcripts before candidate runs. Record the label timestamp and each run timestamp with its timezone. The evaluator rejects labels dated after either candidate run. Do not change reference text to match a candidate error.

Count recordings, not copies. The evaluator rejects duplicate audio hashes. A different hash does not establish an independent recording, so review the corpus for copied or re-encoded clips yourself.

## Write the manifest

Create `manifest.json` in the private corpus directory. Use the following shape, replacing every placeholder and adding all cases. This three-case example is incomplete and cannot pass.

```json
{
	"schema_version": 1,
	"corpus_source": "human_recording",
	"label_source": "human",
	"labeler": "reviewer-1",
	"labeled_at": "2026-10-12T09:00:00+09:00",
	"cases": [
		{
			"id": "case-001",
			"label": "cough",
			"audio": {"path": "audio/case-001.wav", "sha256": "REPLACE_WITH_SHA256"},
			"expected_transcripts": []
		},
		{
			"id": "case-002",
			"label": "throat_clear",
			"audio": {"path": "audio/case-002.wav", "sha256": "REPLACE_WITH_SHA256"},
			"expected_transcripts": []
		},
		{
			"id": "case-021",
			"label": "short_speech",
			"audio": {"path": "audio/case-021.wav", "sha256": "REPLACE_WITH_SHA256"},
			"expected_transcripts": ["はい"]
		}
	]
}
```

Calculate a file's SHA-256 with Python on either platform. On Windows, use `py -3` if `python3` is unavailable.

```sh
python3 -c "import hashlib,pathlib,sys; print(hashlib.sha256(pathlib.Path(sys.argv[1]).read_bytes()).hexdigest())" /private/cough-eval/audio/case-001.wav
```

Use lowercase hashes. Use forward-slash paths relative to the JSON file's directory. Absolute paths, parent-directory traversal, and symlinks that leave that directory are rejected.

Supply at least one nonempty reference for each short-speech case. Supply an empty reference list for coughs and throat clears. For synthetic fixtures, set `corpus_source` to `synthetic`. Set `label_source` to `synthetic` or `unlabeled` when applicable. These sources never receive a passing verdict.

## Run the same corpus through both native apps

1. Build and record the full Git revision for each variant. Use the same baseline revision on both platforms and the same candidate revision on both platforms.
2. Prepare a baseline with the interjection history filter disabled. Record the effective automatic minimum speech duration, including any fallback to the existing global minimum.
3. Choose one candidate configuration. To test A alone, enable the interjection filter and keep the duration unchanged. To test B alone, change the duration and leave the filter disabled. To test A and B together, change both. Use a separate observations file for each comparison.
4. Use the same effective duration and filter flag for a variant on both platforms. The evaluator rejects comparisons that mix different interventions across platforms.
5. Keep the microphone, playback method, volume, room, locale, recognizer, model version, and other settings fixed between variants on each platform. Record those details in a private evidence log under an opaque `setup_id` and `recognizer_id`.
6. Save the unchanged settings in a private file. Hash that file and use its digest as `fixed_settings_sha256` for both variants on that platform. Exclude only the two candidate controls from that file. Keep full resolved configuration snapshots with the evidence logs.
7. Use an isolated test target with automatic insertion or forwarding disabled. Check the recognizer's privacy settings before testing. This evaluator does not upload files, but the app or its configured recognizer may use a service.
8. Exercise the actual native automatic-start path. Play each fixed human recording through the same speaker-to-microphone setup or the same native audio input device. Record which path you use. Do not replace this step with a policy-unit-test result or hand-entered expected output.
9. Start each clip from the same idle and follow-up state. Wait for prior recognition work to finish. Keep enough silence between cases to avoid joining sessions. Include the clip's lead-in and trailing silence in its observation window.
10. Record the start and end of each case window in the local evidence log. Count every `dictation session: automatic` event in that window on both apps. Count a start even if the session is later canceled or its history entry is removed. Do not count manual-hotkey or strict wake-word starts as automatic starts.
11. Capture the final recognition text before history filtering. Use actual native output or a private debugger capture at the finalization boundary. If the app does not expose that text, leave the result unknown and stop acceptance. Do not reconstruct it from cleaned history, reference text, or a guessed empty string.
12. Check local history separately after completion. Mark `retained` true only if the entire final transcript is retained. For multiple sessions within one case, concatenate final transcripts in time order and require every part to be retained.
13. Repeat every case for baseline and candidate on Windows and macOS. Preserve the same case IDs and WAV files. Keep the run order and any anomalies in the evidence log.

Use existing native recording and playback tools. These steps do not install software, enable new cloud services, or alter privacy settings. Resolve unavailable native output before claiming a completed run.

To assess variability, repeat the full paired evaluation and report each repetition. Do not select only the best run or replace a failed case with a later success. The initial 40-case minimum cannot establish general reliability across speakers, microphones, rooms, or recognizer versions.

## Record observations

Create `observations.json` beside the manifest or in a separate private directory. Include exactly four runs, one for each combination of `windows` or `macos` and `baseline` or `candidate`.

Copy the following run shape for each combination. Add one observation for every manifest case. This one-run example is incomplete and cannot pass.

```json
{
	"schema_version": 1,
	"runs": [
		{
			"id": "windows-baseline-1",
			"platform": "windows",
			"variant": "baseline",
			"source": "native_run",
			"revision": "REPLACE_WITH_FULL_40_CHARACTER_GIT_SHA",
			"recorded_at": "2026-10-12T10:00:00+09:00",
			"setup_id": "windows-setup-1",
			"recognizer_id": "windows-recognizer-1",
			"fixed_settings_sha256": "REPLACE_WITH_SHA256",
			"automatic_min_speech_ms": 300,
			"interjection_filter_enabled": false,
			"evidence": {"path": "logs/windows-baseline-1.txt", "sha256": "REPLACE_WITH_SHA256"},
			"observations": [
				{
					"case_id": "case-001",
					"automatic_starts": 1,
					"transcript": "あっ",
					"retained": true
				},
				{
					"case_id": "case-021",
					"automatic_starts": 1,
					"transcript": "はい。",
					"retained": true
				}
			]
		}
	]
}
```

Enter `automatic_starts` as a nonnegative integer. Enter `retained` as a JSON boolean. Use `transcript: ""` only when the full observation confirms no transcript. A completed no-start observation has `automatic_starts: 0`, `transcript: ""`, and `retained: false`.

For an unknown result, use `null` or leave its field absent while collecting evidence. The evaluator rejects that incomplete input. Never convert unknowns to zero starts, empty text, or false retention to obtain a report.

Use `source: "synthetic"` for generated or simulated runs. Hash the actual private evidence file after collection. Include native log excerpts, the case-window ledger, configuration details, and how pre-filter text was observed. Hashes establish file consistency, not the truth of the contents.

## Evaluate and inspect the result

From the repository root, run the following command on either platform.

```sh
python3 scripts/cough_acceptance.py /private/cough-eval/manifest.json /private/cough-eval/observations.json
```

Use `py -3` on Windows when needed. The evaluator reads local files and prints JSON to standard output. It does not change app configuration, create a corpus, run the apps, or upload evidence.

Read each platform's counts separately. Acceptance requires every gate on both platforms and complete eligible evidence.

- `zero_cough_automatic_starts` requires zero candidate starts across all cough and throat-clear cases.
- `short_speech_start_misses_nonworse` requires no increase in cases with zero automatic starts.
- `short_speech_transcript_misses_nonworse` requires no increase in incorrect or absent final recognition text.
- `short_speech_retention_misses_nonworse` requires no increase in cases without a correctly recognized, retained transcript.

Compute transcript recall as `short_speech_transcript_hits / case_counts.short_speech`. The report prints those counts rather than hiding the sample size behind a percentage. Reference matching applies Unicode NFKC normalization, case folding, and removal of whitespace and punctuation. It does not assess semantic equivalence. Add acceptable alternative transcripts only during human labeling before candidate runs.

Inspect `cough_automatic_starts` independently of `cough_retained_cases`. A can reduce retained cough text after recognition, but it cannot prevent the automatic session from starting. An empty history with one cough-induced start still fails. A zero-start baseline and candidate do not demonstrate that A improved start behavior.

Check the exit code before accepting results.

- `0` means the supplied observations meet the gates and declared evidence requirements.
- `1` means at least one gate or evidence requirement is unmet. The verdict is `not_accepted`.
- `2` means input validation failed. Missing observations, unknown fields, duplicate IDs or JSON keys, bad types, and unverifiable local files are invalid.

Treat `pass` as a consistency result for supplied observations. A reviewer must still verify human labels, native execution, case windows, fixed settings, and the recorded interpretation of retention. The script cannot distinguish honest metadata from fabricated claims or prove that a hashed log describes the corresponding run.

Keep the full evidence local. The default report contains IDs, counts, gate results, and fixed explanatory text. It excludes transcripts, audio paths, log paths, and person aliases. Inspect even the safe report before sharing it, because IDs may become identifying if you choose them poorly.

## Verify the evaluator itself

Run the synthetic behavior tests from the repository root.

```sh
python3 -m unittest discover -s tests -p test_cough_acceptance.py -v
```

The tests create temporary WAV files and invented metadata, including native and human declarations needed to exercise successful evaluation. They delete their temporary files afterward. A passing test suite verifies parsing, pairing, privacy of report output, and acceptance arithmetic. Obtain actual native recordings and runs before reporting that the app meets the cough or short-speech KPI.
