# Code Quality Audit

Last reviewed: 2026-07-25

This document records findings deliberately deferred after the deep reliability
review.  Items are deferred only when a change would alter a user-visible
contract, needs a real Windows desktop test matrix, or requires a larger design
than a local correction.

## Deferred Findings

### Recovery journal state transitions

Window recovery data is written before an asynchronous hide request.  This is
intentional: an abnormal exit after `ShowWindowAsync` is requested must still
have enough information to restore the window.  The narrow trade-off is that a
crash between journal persistence and the hide action can cause the next launch
to restore a window that never became hidden.

Do not change this ordering in isolation.  A future improvement needs an
explicit persisted `prepared`, `requested`, and `confirmed` state machine plus
failure-injection tests for every transition.

### Rule ordering

The UI allows target-rule reordering, but matching currently uses an inclusive
`Any` rule set.  Reordering therefore does not change matching behaviour.

Before assigning semantics to order, decide whether the product uses
first-match wins, explicit exclusions, or priority groups.  Changing this
without migration and UI wording would silently alter existing scenes.

### Privacy desktop security boundary

The privacy desktop is a user-experience isolation feature, not a security
sandbox.  Same-user processes on the private desktop can discover and send the
documented return/close thread messages.  Supporting authenticated control
messages would require a compatible handshake between the owner process and
desktop shell helpers.

The user documentation should continue to avoid promising isolation from
malicious same-user processes, anti-cheat software, exclusive fullscreen apps,
or secure desktops.

### Workspace process-count warning

The exit warning is based on applications whose paths can be queried from the
Job object.  A Job member whose path cannot be inspected can be missed.

The safe long-term fix is job-level PID enumeration with a separate unknown
member count.  Do not replace this with a confirmation for every empty
workspace: it would degrade normal exit behaviour without making the count
accurate.

### Import size and shape limits

`SceneProfileTransfer.MaximumImportBytes` is currently enforced as a character
count by the Core API.  The UI path has an input file limit, but direct callers
can supply more UTF-8 bytes than the name suggests.

When import hardening is scheduled, measure UTF-8 byte length and introduce
explicit limits for target rules and launch items.  Keep the schema compatible
with existing exports.

### Corrupt settings fallback

Unlike recovery and audio journals, a syntactically corrupt `settings.json`
still falls back to a fresh default profile.  This preserves the existing
first-run recovery experience, but a later normal exit can replace the corrupt
file rather than retaining a forensic copy.

Do not make this automatic archival policy change without deciding how users
discover, restore, or intentionally discard the backup.  Recovery journals are
different: unreadable, corrupt, or unknown-schema journals now stop startup
without deleting the file, because they may be the only route to restore a
hidden window or muted session.

### Event-storm capacity

The window-event watcher now coalesces repeated SHOW and DESTROY notifications
per HWND and cancels pending work during disposal.  A burst containing many
different HWNDs can still create one pending worker per handle before the
controller serializes the actual visibility operation.

Do not add an arbitrary global drop limit without defining which windows may
remain visible and how the user is notified.  Validate a bounded queue or
back-pressure design with real high-churn desktop software before changing the
reliability contract.

### Desktop and UI verification gap

GitHub-hosted CI covers build, unit tests, packaging, and static checks.  It
cannot certify Explorer restart behaviour, private desktop switching,
multi-monitor DPI, or real tray interaction.  These need manual verification
on supported Windows 10/11 devices before a stable release.
