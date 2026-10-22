# Controlled native Explorer Alt+Tab end-to-end test

## Why it exists

The existing `Run-DesktopE2E.ps1` workload verifies tab clicks, Alt+digit,
WindowTabs next/previous shortcuts and grouping. It did **not** send
Windows' native `Alt+Tab`. The user reports intermittent native Alt+Tab
failures with taskbar combining enabled, including failures not identified by
the event-only `activation-probe.enabled` recorder. Passing existing UI
and activation callback tests does not establish native shell switcher
correctness.

The new **NativeAltTab** workload uses the actual Explorer switcher on an
**unlocked, interactive Windows desktop**, with known application HWNDs,
automated key input and an independently created external anchor process.
It is a behavioral black-box test, not another passive focus recorder.

## Invocation

Close any existing WindowTabs instance, save other work, and allow the test
to temporarily take over keyboard/foreground/mouse for its bounded sequence:

```powershell
pwsh -NoProfile -File ./tests/Run-DesktopE2E.ps1 -Interactive -NativeAltTab -AltTabSwitches 20
```

The baseline above repeats one group member versus one independent anchor.
The **separate opt-in, higher-coverage case** rotates the active group tab
between each pair of native Alt+Tab switches:

```powershell
pwsh -NoProfile -File ./tests/Run-DesktopE2E.ps1 -Interactive -NativeAltTab -AltTabRotateMembers -AltTabSwitches 20
```

The rotating case checks the newly selected member each time, rather than
100 switches to an unchanged app HWND.

To add **a second independent external window** and alternate which external
window is the recent Alt+Tab counterpart of the group, use:

```powershell
pwsh -NoProfile -File ./tests/Run-DesktopE2E.ps1 -Interactive -NativeAltTab -AltTabRotateMembers -AltTabExtraAnchor -AltTabSwitches 20
```

This yields **three real switchable entities** (one combined two-tab group
and two separate external processes). A known recent group member and
one anchor are explicitly activated before each two-switch pair; the
selected anchor alternates on successive pairs. This tests a busier native
MRU history without assuming the ordering of every shell tile, and
preserves the simple baseline as a separate mode. A passing run does
not assert that the shell successfully navigates all three entries in
one held-Alt operation. An existing unrelated application can still
interfere, and such a mismatch is classified separately.

In all modes, the harness additionally
checks that the **visible top-level window at the center of the expected app**
matches the expected foreground HWND. This detects cases where an app reports
foreground but a different window is visibly stacked over it. The live hit test
only works when those fixture surfaces are unobstructed; external overlays can
cause a clear test failure rather than an inferred WindowTabs fault.

The workload never runs as part of ordinary CI. With `-BuildOnly`, it
only compiles the standalone driver and the product Release executable:

```powershell
pwsh -NoProfile -File ./tests/Run-DesktopE2E.ps1 -BuildOnly -NativeAltTab
```

Two temporary WinForms application windows from the driver process are
auto-grouped by WindowTabs with combined taskbar icons. A separate
`DesktopAnchor.exe` process is not included by those path rules.
The test verifies the grouped windows share a synthetic native root owner,
then establishes the anchor as foreground after the group's tab was active.
It sends **real Alt+Tab** via SendInput and expects the foreground to
alternate between the group's most recent tab and the independent anchor.
An unexpected foreground HWND (or a foreground HWND obscured at its
blank window center) ends the test and records expected/observed HWNDs and
hit-test data in `recentActions` and `failure.txt`. For an **unexpected
foreground HWND**, the driver additionally writes
`foreground-mismatch.json` *before teardown* containing: the actual,
expected, anchor and grouped member HWNDs; each window's PID, process
name, native class, owner chain root, visibility and last-active-popup
metadata; and a bounded sequence of foreground changes observed during
the 1.6-second wait. No window titles, command lines or paths are logged.

This extra record is important because the ordinary `windows.json` fixture
snapshot enumerates only the staged WindowTabs and test helper windows;
a third-party HWND, such as a shell or launcher window, does not appear
there. A failure with a foreign process foreground is not automatically a
WindowTabs bug; correlate its identity and the switching sequence first.

All application settings, windows, logs and generated executables live in a
unique staged `tests/Debug/desktop-e2e-*` directory. The test restores its
mouse cursor position and modifier state in cleanup, closes its anchor process
and its helper forms, and terminates its staged WindowTabs instance.
The script refuses to run interactive input unless `-Interactive` was
supplied and the desktop is unlocked; its driver refuses unexpected
foreground processes before sending Alt+Tab.

## What passing means

Passing verifies that the **specific synthetic group and external anchor**
can alternate foreground under actual Windows native Alt+Tab for the bounded
run; it is not proof that every application's group works or that an
intermittent problem is fixed. A failing run includes an exact expected and
actual HWND at the first divergence. The test does not infer the shell's
highlighted tile; it avoids needing one by establishing a known two-entry
switching sequence.

Unlike production focus logging, this is an active interference test:
running it while working on something else would disrupt normal desktop use.
The test is deliberately an **explicit user action**, not scheduled or
triggered by launching WindowTabs. It does not modify production Alt+Tab
handling, ownership, the taskbar, or default diagnostic settings.
