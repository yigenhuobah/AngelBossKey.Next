# Reliability Testing

The reliability suite exercises recovery behavior without controlling real user
applications. It combines a deterministic audio-session model with the existing
synthetic HWND integration host.

## Safety invariants

Every generated operation sequence continuously checks that:

- an audio session is never muted before its original state is durably recorded;
- a changed live session always has a recovery snapshot for the same PID, process
  start time, executable path, and session identifier;
- a healthy restore returns every present session to its original volume and mute
  state;
- process exit and PID reuse cannot apply a stale snapshot to a new process;
- a clean final restore removes obsolete recovery entries;
- repeated synthetic-window hide and restore cycles preserve visibility and do
  not leave a recovery journal behind.

Generated operations include session arrival and disappearance, rule changes,
application exit, PID reuse, controller restart, journal write failure, audio
update failure, restore, and recovery. The model uses fake executable paths and
contains no window titles or user data.

## Profiles

The ordinary `eng/quality.ps1` gate runs a small deterministic profile: 12 seeds,
80 operations per seed, and 100 synthetic-window cycles. This keeps pull request
feedback fast while covering the same invariants.

Run the larger profile with:

```powershell
.\eng\reliability.ps1
```

The defaults execute 256 seeds, 500 operations per seed, and 1000 synthetic HWND
hide/restore cycles. Parameters can reduce or increase each dimension:

```powershell
.\eng\reliability.ps1 -Seeds 64 -Steps 250 -WindowCycles 250 -BaseSeed 4301281
```

GitHub Actions runs the larger profile every week and also exposes the same
values through `workflow_dispatch`. This workflow is a reliability stress test,
not GUI certification for tray interaction, Explorer, DPI, multiple displays,
audio hardware, or an independent desktop.

## Reproducing a failure

The script stores its TRX result under `artifacts/reliability/<run-id>`. A
model failure also writes `audio-model-<seed>.json` containing the bounded fake
operation trace. The xUnit failure reports the exact reproduction command:

```powershell
.\eng\reliability.ps1 -BaseSeed <seed> -Seeds 1 -Steps <reported-steps> -WindowCycles 1
```

Keep a minimized failing seed as a permanent regression test when it exposes a
product defect. Do not replace a clear fixed regression with an opaque random
seed alone.
