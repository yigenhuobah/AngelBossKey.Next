# Contributing

## Local setup

Use Windows 10 22H2 or Windows 11 x64 with .NET SDK `10.0.302`. The repository
contains `global.json` to reject preview SDKs.

Run the full local gate before proposing a change:

```powershell
.\eng\quality.ps1
```

Use `-Coverage` when changing recovery, window visibility, audio, privacy
desktop, or Elevated Broker behavior.

## Change expectations

- Keep the main process non-elevated. Do not add injection, remote-memory,
  self-loading DLL, network, or telemetry behavior.
- Add regression tests for observable behavior changes. Prefer focused tests to
  broad coverage-only changes.
- Treat imported scenes, recovery data, log material, and pipe data as
  untrusted input.
- Diagnostic output must not include window titles, executable paths, launch
  arguments, recovery records, or authentication tokens.
- Do not run disruptive Explorer, DPI, multi-monitor, or process-termination
  checks on a contributor's daily workspace. Keep them for an isolated VM or
  dedicated test machine.

## Review checklist

Describe the user-visible behavior, affected security boundary, tests run, and
any manual validation. Broker changes must cover authorization failure and
invalid-request handling. Changes to release files must preserve deterministic
build settings and the portable publish profile.
