# Secret values stay out of the log

When a command, URL, or SMTP password lookup names an environment variable that is missing, the log keeps the variable name. A shell default such as `${API_TOKEN:-s3cret-value}` and the value of a set variable whose name contains password, secret, token, or api_key are not written. Mac `log` and Windows `Log.Info` / `Log.Fatal` both redact. An empty SMTP password is the same as a missing one.

## Sub-features

- A missing `${NAME:-default}` or `%NAME%` becomes `$NAME`.
- A set secret-named value already pasted into a log line becomes `$NAME`.
- SMTP send with `passwordEnv` unset throws `environment variable <name> is not set` and does not include a password.

## How to get to it (user POV)

Put a command or action that refers to a password variable, leave that variable unset, and open the log after the action fails. The line names the variable. It does not contain the password.

## Driving it with verify.sh

This box has no microphone and no Swift. The proof is the shared check, which runs the production `SecretLog` and `MailAction` sources:

```sh
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet run --project dotnet/VoiceSwitch.SharedChecks/VoiceSwitch.SharedChecks.csproj -c Release
```

Pass: every line starts with `PASS` and the process exits 0. `$V wav` does not set a missing secret, so it cannot prove this.

## Gotchas

- A variable name in the log is intentional. Only the value is removed.
- Mac SMTP send is not implemented. Mac still redacts through `log`. A live tray log on Windows is unverified here.
- Values shorter than 8 characters are left alone so ordinary short settings are not eaten.
