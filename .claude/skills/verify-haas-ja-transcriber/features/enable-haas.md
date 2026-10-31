# Enable Haas

A person who has a Linux NVIDIA GPU, or a command that reaches one, can switch Japanese body transcription to Haas-ja-v1.0 without changing the wake word or the stop word.

## Sub-features

- `HJ-CFG-CS` loads `dictation.transcriber` and `dictation.haas`.
- `HJ-DOCS` shows the GPU limit, the model id, and a config example.
- `HJ-MAKE` adds setup and download targets that normal builds do not run.
- `HJ-RUNNER` checks the runner script without the model installed.
- `HJ-GPU` records that a live transcription was not run on this box.

## How to get to it (user POV)

- Read the "Haas-ja-v1.0 (Japanese local option)" section in `README.md` or `README_ja.md`.
- Copy `config.example.haas.json` and set `dictation.haas.command` to the GPU runner.
- On the GPU machine, run `make haas-setup` and `make haas-model` once. Daily `make` and `make win-test` do not do this.

## Driving it with verify.sh

Preconditions:

- `verify.sh doctor` reports dotnet 8.x and python3.
- The repo is the worktree under test.

- **Decode.** Run `verify.sh drive HJ-CFG-CS`. The evidence log contains `PASS haas config decodes command timeout and vocabulary` and `PASS haas config rejects an unknown transcriber`.
- **Docs.** Run `verify.sh drive HJ-DOCS`. Exit 0. The captured grep lists `third-intelligence/Haas-ja-v1.0` in both READMEs and in `config.example.haas.json`.
- **Makefile.** Run `verify.sh drive HJ-MAKE`. The dry-run transcripts for `build`, `win-test`, and `parity-test` do not mention `haas-model`.
- **Runner help.** Run `verify.sh drive HJ-RUNNER`. `py_compile` and `--help` exit 0.
- **GPU.** Run `verify.sh drive HJ-GPU`. The line is `SKIP HJ-GPU` and names the missing GPU.

## Gotchas

- `config.example.json` and `config.example.windows.json` must stay free of `transcriber`. Adding the key there fails the sample-config parity check or changes the default.
- `make haas-model` contacts Hugging Face and is not part of a drive.
- A README that names the model and forgets the NVIDIA requirement fails `HJ-DOCS`.
