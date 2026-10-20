# Alt+Tab foreground flight recorder

This is a **passive, opt-in diagnostic** for intermittent failures to activate the
selected window after the native Windows Alt+Tab switcher has appeared.

It does **not** fix or replace Alt+Tab, change foreground policy, intercept keys,
or attempt to determine the selected switcher tile. The Windows switcher does
not expose a trustworthy selected-target HWND through these WinEvent hooks.

## Enable before launching WindowTabs

From Nushell in the repository:

```nu
let dir = ('.\\WtProgram\\bin\\Debug' | path expand)
'' | save --force ($dir | path join 'focus-trace.enabled')
```

After building, completely exit WindowTabs and reopen the actual
`WtProgram\\bin\\Debug\\WindowTabs.exe`. The marker is read **once** when the
program starts. Output is written to `focus-trace.jsonl` beside that executable.
No Administrator privileges are needed.

Use WindowTabs normally rather than forcing a reproduction. Immediately after
a **successful** Alt+Tab switch, press **Ctrl+Shift+F11** once to mark a baseline.
If the switcher appeared and a chosen item did not activate, press
**Ctrl+Shift+F12** to mark that attempt. These hotkeys are registered **only**
while tracing is enabled; if another application owns a chord, a
`marker-hotkey` entry reports `registered=false`. Markers don't change focus
intentionally, but pressing them may still affect which app is foreground, so
the trace uses their timestamps as human annotations, not the target itself.

## Recorded events

Each JSONL line contains UTC, monotonic `elapsedMs`, `attempt`, `stage`,
`hwnd`, `foreground` and `detail`.

- `alt-tab-pressed`, `tab-released`, `alt-released`: the **Alt+Tab shortcut
  only**, including whether its Tab transition was injected. No other typed
  character or keyboard history is captured.
- `switcher-start` and `switcher-end`: Windows accessibility WinEvents,
  when emitted by the OS (not guaranteed on every Windows release).
- `os-foreground`: every actual foreground transition observed by WinEvent,
  including transitions to other applications.
- `sample-200ms` and `sample-800ms`: late foreground samples after
  Alt release; may show that Windows never activated a target, changed the
  foreground briefly, or took focus back after a delay.
- `group-reassert-foreground`: WindowTabs called its normal group-top
  focus reassertion; compare its HWND with adjacent OS foreground transitions.
- `group-received-foreground`: one group received a foreground event.
- `manual-success` and `manual-failure`: your annotation via F11/F12.

No window titles, URLs, text keystrokes, screenshots, app names or file contents
are collected. **HWNDs are process-session identifiers**, so review the trace
before sharing it. The current file rotates after approximately 5 MiB to
`focus-trace.previous.jsonl`. Only collect data during a focused diagnostic
session; turn it off when finished.

## Interpretation

A failure marker after an Alt+Tab attempt can be compared with earlier
`alt-tab-pressed`, optional `switcher-*`, foreground changes, and
`group-reassert-foreground` entries.

1. A candidate HWND became foreground and then the previous grouped HWND
   became foreground again, with a WindowTabs reassertion between them:
   **possible WindowTabs focus steal**; reproduce and inspect the group event
   ordering before changing behavior.
2. No foreground change at all following Alt release: **possible shell
   cancellation, selection back to the same window, or activation refused**.
   Without observing the user's selected tile we cannot claim which.
3. Foreground changed to a different HWND and remained there: Alt+Tab likely
   succeeded at the OS level, even if the user saw a blank or occluded window.
4. WinEvents may be delayed, dropped or coalesced, and elevation/UIPI can affect
   hooks; corroborate with a user timestamp and actual foreground samples.
5. If `replaceAltTab=true` in `switcher-mode`, WindowTabs' own task switcher
   must be considered instead of the native Explorer path.

Do not apply speculative foreground or tab-order patches solely on the basis
of no-foreground-change: it isn't a correctness verdict.

## Disable and collect

Fully exit WindowTabs and remove `focus-trace.enabled`. Restart normally.
Attach `focus-trace.jsonl` to the debugging conversation, ideally with one
normal successful marker and one failure marker. Include a sentence saying
which apps were involved (not in the recording), and whether the native switcher
was visible. The trace intentionally contains no semantic window identity.

The GitHub Actions Windows jobs compile the recording code and test neutral
focus-history classifications. An interactive Windows diagnostic session is
still required to identify the intermittent defect.
