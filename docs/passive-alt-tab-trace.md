# Passive Alt+Tab foreground trace — normal desktop

## Purpose

Use this only after controlled native Alt+Tab tests (WinForms, two combined
taskbar groups, disposable Vivaldi group, dynamic 2→3→2 browser grouping,
and fast 35ms browser↔ungrouped-anchor MRU A/B) pass but the **real manual
Alt+Tab failure remains intermittent**.

The preceding A/B tests **did combine taskbar icons** for two Vivaldi windows
and switch **to and from one independent, ungrouped test window**. They
clicked to establish a known MRU pair **once**, then sent 20/100 consecutive
native Alt+Tab chords with **no mouse clicks between switches**. They
verified eventual foreground activation with up to 1600ms timeout, not
real human-perceived latency. Other tests clicked between switching pairs.
The user's original symptom is quick Alt+Tab between the two most recent
windows; it is **not** prolonged selection among many Alt+Tab tiles.

This recorder targets the missing dimension: **ordinary manual input**
with the user's real windows, normal WindowTabs settings, and normal shell
activation timing. It is entirely independent of WindowTabs production code.

## Run

From the repository root on an unlocked Windows desktop:

```powershell
pwsh -NoProfile -File .\tests\Run-PassiveAltTabTrace.ps1 `
    -Interactive -Minutes 10
```

Unlike the interactive E2E fixtures, **leave your usual WindowTabs and
Vivaldi sessions running**. Prefer Flow Launcher exited for the first
capture, to investigate the historically reported Flow-independent case.
Do not close your personal windows or use a throwaway browser profile.

Alt+Tab naturally as you normally do. On observing a wrong target,
apparently slow activation, or window shown behind another, press
**Ctrl+Shift+F12** once to add a manual marker to the trace. This
chord is **observed, not consumed**, so another installed app could also
respond to it; if it conflicts, instead note the approximate wall time.
Press **Ctrl+C** in the recording console to stop early, or allow the
10-minute duration to expire. The script accepts 1–30 minutes.

Each run creates its own
`tests/Debug/passive-alt-tab-<random-id>/foreground-trace.jsonl`
and prints the path. Keep the file private until reviewed. To compile
without installing hooks or recording a session:

```powershell
pwsh -NoProfile -File .\tests\Run-PassiveAltTabTrace.ps1 -BuildOnly
```

No normal browser, WindowTabs or other applications are started/stopped by
this recorder, and **no mouse or keyboard input is injected**.

## Recorded data and privacy boundaries

- Low-level global keyboard hook detects **only Alt+Tab press timing and
  Alt-release**, and the optional **Ctrl+Shift+F12 marker**. It deliberately
  ignores ordinary keypresses and records **no typed text or other keys**.
  It never suppresses keystrokes; callbacks always pass the event along.
- Windows `EVENT_SYSTEM_FOREGROUND` hook records foreground HWND changes
  and monotonic timestamps. On each Alt+Tab chord it also requests
  snapshots near 0, 40, 120, 300, and 900ms (actual sampled delays are
  recorded). These are **approximate**, Windows-scheduled observations.
- For relevant HWNDs the trace includes numerical handle, PID, thread ID,
  **process name**, window class, native `GW_OWNER` / root-owner
  / last-active-popup relationships, visible/minimized flags, extended
  styles, presence/absence of `WindowTabs.Group.v1`, and **the top-level
  HWND hit at the center of the foreground window**. A bounded topology
  snapshot is recorded on each Alt+Tab chord and manual marker. The
  center hit-test is a visual-stacking **heuristic** and can be misleading
  for DPI virtualization, overlapping surfaces or unrelated overlays.
- It does **not** collect window titles, browser URLs, filenames or
  executable paths, process command lines, screenshots, clipboard, network
  data, full key history, or content from application windows.
- Process names and window classes can still reveal which applications
  were open. **Review/redact before sharing**. Captures may also have
  HWND reuse, stale process IDs and missed events under system load.
- The recorder does not claim to know which window the user *intended*
  to target. A Ctrl+Shift+F12 mark plus a brief note about expected vs
  actual window is necessary to interpret a wrong-target event.

## Interpretation

Compare each Alt+Tab event's pre-switch foreground and preceding foreground
history with subsequent `foreground` and `post-alt-tab-sample` records.
A sequence with no foreground change despite an Alt+Tab press differs from
an activation delayed by hundreds of milliseconds. Correct foreground
but a different top-level window at its client center suggests visual
stacking/occlusion. When WindowTabs grouping is relevant, examine the
root-owner chain, proxy ownership, and process name before assigning blame.

The global keyboard hook observes only a user-initiated subset of keys.
Some secure desktops, elevated windows, access-control boundaries, shell
overlays or high load can suppress/delay event delivery. The trace is
diagnostic evidence, **not a universal proof of input or Explorer behavior**.

## Validation

The CI Release job compiles the standalone recorder under Windows PowerShell
5.1 and PowerShell 7. It **does not install system hooks or run a live
interactive capture** on the CI runner. An ordinary desktop session is
required for the first actual validation.
