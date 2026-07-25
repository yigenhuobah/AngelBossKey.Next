# Windows Release Validation

Run this checklist on an isolated VM or a dedicated Windows test machine. Do
not change DPI, Explorer, display, or process settings on a daily-use machine
just to complete release validation.

## Before testing

1. Download the signed release zip and its SHA-256 file from the release draft.
2. Compare the published SHA-256 with `Get-FileHash` for the downloaded zip.
3. Extract to a new directory outside the source tree.
4. Verify every shipped `.exe` and `.dll` reports `Valid` with
   `Get-AuthenticodeSignature`.
5. Start `AngelBossKey.Next.exe` without elevation. Do not reuse a previous
   `%LocalAppData%\AngelBossKey.Next` profile for the first-pass test.

## Core smoke test

1. Configure a test target such as Notepad and assign a non-conflicting hotkey.
2. Hide and restore normal, minimized, and maximized target windows. Confirm
   the original display state is preserved.
3. Open another target window while hidden and confirm it also restores.
4. Toggle a rule off while hidden, then restore. Confirm only windows still
   covered by enabled rules are restored.
5. Enable the optional per-application mute rule, verify the target session is
   muted while hidden, then verify its prior volume and mute state returns.
6. Close the main window, reopen it from the tray, then explicitly exit from
   the tray. Confirm all managed windows are restored before the process exits.
7. With only test applications hidden, end the process and relaunch it. Confirm
   recovery restores only handles whose process identity still matches.

## Isolated-environment checks

Run these only on the VM or dedicated machine, and record the Windows build,
display layout, and DPI values in the test result.

1. Repeat the core smoke test at 100%, 150%, and 200% DPI.
2. Repeat it with a secondary display connected and disconnected.
3. Restart Explorer while the app is resident and confirm the tray icon returns.
4. Test an administrator window with on-demand elevation disabled and enabled;
   the disabled case must remain visible and report a clear failure.
5. Enter and return from both independent-desktop modes. Confirm the emergency
   return hotkey works and the complete Explorer mode falls back safely when it
   cannot establish a shell.

## Reporting a result

Record the release version, Windows version, test section, and pass or fail
outcome. For failures, include the diagnostic report only after confirming it
contains no window titles, executable paths, launch arguments, recovery data,
or tokens. Do not attach screenshots that reveal user content.
