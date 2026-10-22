# Rapid two-entry native Explorer Alt+Tab: grouped / ungrouped Vivaldi A/B

The user clarified that the original intermittent problem occurs with **a
quick press and release of Alt+Tab to the most recently used window**, not
during a multi-tile Explorer switcher navigation with Alt held down.

Earlier steady-state and lifecycle tests passed 20/100/200 switches but
re-primed the Explorer MRU by clicking the desired window and anchor
**between every two-switch pair**, and sent Alt+Tab with a **125 ms Alt
hold** followed by a **120 ms dwell**. That is important positive
coverage but not the same input pattern.

This separate opt-in E2E uses the **same two disposable real Vivaldi
windows**, same independent anchor and same quick key timing in both
scenarios, with exactly one initial known-MRU setup and then **no clicks
or foreground forcing between switches**.

## A/B commands

On an unlocked idle Windows desktop, first save work and **fully exit
normal WindowTabs, Flow Launcher and every Vivaldi process** including
background mode. The runner enforces the Vivaldi absence because staged
WindowTabs groups by executable path, which could otherwise include
normal browser windows.

**A: Ungrouped control (no WindowTabs):**

```powershell
pwsh -NoProfile -File .\tests\Run-DesktopE2E.ps1 `
    -Interactive -NativeAltTab -AltTabBrowser `
    -AltTabQuickMRU -AltTabUngroupedControl -AltTabSwitches 20
```

**B: Same rapid input with two Vivaldi windows combined by WindowTabs:**

```powershell
pwsh -NoProfile -File .\tests\Run-DesktopE2E.ps1 `
    -Interactive -NativeAltTab -AltTabBrowser `
    -AltTabQuickMRU -AltTabSwitches 20
```

A requires no WindowTabs process. B launches staged WindowTabs, checks
one grouped strip, both real browser HWNDs and common synthetic owner
root. Both create *two* real Vivaldi browser windows in a unique
`browser-profile` stage, plus a separate test anchor; the difference
is whether the browser windows are grouped by WindowTabs.

In both, establish the **known pair** once: after the new anchor is
created, click a real isolated browser window (or explicitly click the
selected WindowTabs group member **again**), verify it is foreground,
then click the independent anchor. The grouped fixture must not assert
browser foreground immediately after an earlier anchor click: that was
a harness setup defect observed in its first interactive run. Next,
use 20 consecutive native Alt+Tab key sequences
expected to alternate **browser ↔ anchor**. No mouse, group-member
rotation, foreground forcing, or MRU re-priming occurs between switches.

The quick chord **requests a 35 ms Alt key hold** (vs the earlier 125 ms);
after expected foreground/visual checks, it waits **35 ms** (vs 120 ms).
These are requested scheduler delays, not a claim of millisecond-accurate
human typing or a zero-latency shell. The test **waits up to 1600 ms for
each expected HWND**, so a pass means switch correctness, not necessarily
that Windows switched within a human-visible instantaneous interval.

For each step, the harness records before/expected/observed HWND,
timestamp, process/owner metadata on a wrong foreground, native tab
strip ownership in B, and actual visible top-level window at the center
of the expected client area. `foreground-mismatch.json` classifies
unexpected foreground process and owner relationships; `result.json`
contains recent actions; `browser-cleanup.json` captures disposable
profile removal. A bad hit-test should also be inspected for unrelated
overlays rather than automatically blamed on WindowTabs.

## Limits and interpretation

- A and B have two real Vivaldi browser windows, but in A they remain
  **separate eligible Alt+Tab entries**, whereas in B WindowTabs combines
  them into one group. Both prime the same **most recent browser/anchor
  pair** before toggling. A therefore controls key timing and baseline
  Explorer MRU switching, not a fully identical shell entry topology.
- A failure in both A and B suggests the rapid input or shell conditions
  deserve attention; an isolated failure in B suggests an interaction
  involving WindowTabs but still needs PID/ownership evidence.
- Passing both does **not** disprove user-observed failures during ordinary
  use, and is not a justification for a production activation hack.
- This is a **short 20-switch** diagnostic, not another 200-switch soak.
  CI compiles the E2E driver and runs noninteractive regressions but
  cannot validate the native Explorer switcher; the first interactive
  A/B run remains necessary.
- Existing `-AltTabBrowser` baseline, lifecycle, WinForms, and
  two-group modes are unchanged. No WindowTabs production code changes.

## Initial live A/B finding and harness correction

The first A/B attempt on 2026-10-22 had **A: PASS 20/20** using
ungrouped real Vivaldi, but **B failed before switch 1**. The
`Activated` setup assertion logged expected Vivaldi HWND `1311098`,
foreground anchor HWND `7014982`, and tab-strip owner
`1311098`. The setup had just clicked the anchor and then incorrectly
called `Activated` for the browser **without clicking the browser
again**. This was a test-order error, not evidence of failed native
Alt+Tab. The grouped quick-MRU setup now explicitly re-clicks the
selected tab, verifies foreground and strip owner, then clicks the
anchor and begins the uninterrupted chord series. Previously passing
baseline and lifecycle modes are unchanged.
