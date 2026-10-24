# Durability and permissions

The journal only appends. A crash may leave a partial last line and must not damage earlier lines. The journal directory is mode 0700 and the file is mode 0600. A symlink is not a place the journal will write. Windows uses a user-only ACL in place of those mode bits, matching the mode-restore store.

## Sub-features

- `append-only` leaves earlier lines byte-identical and tolerates a partial trailing fragment.
- `immutable-text` has no text-rewrite API.
- `no-symlink` falls back when the target is a symlink.
- `perms-unix` sets 0700 on the directory and 0600 on the file.
- `perms-leaf-only` does not chmod the parent.
- `windows-acl` is the Windows DACL. This Linux host skips it.
- `no-ui` adds no viewer.
- `swift-harness` runs the Swift twin when the compiler exists.

## How to get to it (user POV)

- Dictate twice. Open the day's file. The first line is still the original text.
- The journal folder is not readable by other users on the machine.
- No new menu item or window appears for the journal.

## Driving it with verify-journal

Preconditions:

- `$V doctor` printed `doctor: ready`.
- The scratch directory is on a filesystem that stores Unix mode bits (this VM's `/tmp` does).

- **Append only.** Run `.cursor/skills/verify-dictation-journal/verify.sh drive durability-permissions`. Stdout contains `PASS append-only` and `PASS immutable-text`.
- **Symlink.** The same run contains `PASS no-symlink`.
- **Modes.** The same run contains `PASS perms-unix` and `PASS perms-leaf-only`.
- **Windows ACL.** On this host stdout contains `SKIP windows-acl`. That line is not a pass.
- **No viewer.** The same run contains `PASS no-ui`.
- **Swift.** If `swiftc` is missing, stdout contains `SKIP swift-harness`. That line is not a pass.
- **Proof.** `stdout.txt` and `exitcode.txt` are in the evidence directory. `exitcode.txt` is `0` only when every required id passed. The two SKIP lines may be present.

## Gotchas

- `File.Create` mode is masked by umask. The assertion reads the mode back with `stat`. A requested 0600 that lands as 0644 is a failure.
- Do not chmod a parent such as `Application Support` or the temp root. Only the leaf journal directory is 0700.
- Surviving lines must be byte-identical, not just JSON-equal. A rewrite that reorders keys fails `append-only`.
- `SKIP windows-acl` does not prove the ACL. A Windows run has to execute that branch.
