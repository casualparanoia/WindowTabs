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
- `group-raise-owned-window`: a group confirmed an owned window is *still*
  foreground and is about to raise it in native z-order.
- `group-stale-foreground-ignored`: a group received a delayed foreground
  callback for a tab while some **other** HWND is currently foreground; it
  deliberately does not raise the stale tab (guarded experiment in PR #7).
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


## Combined-taskbar owner-chain investigation

An A → B → A session (combining off → on → off) found five apparent
same-HWND returns only in the combined interval, versus zero in both off
intervals. See PR #2 for the timestamped counts. To distinguish a failed
native Alt+Tab selection from a hidden/proxy-owned application, the recorder
now captures additional **read-only** diagnostics:

- `taskbar-combine-setting`: startup state and global setting changes with
  timestamps. Individual groups can override the global setting via their
  own menu; an enabled global setting is not proof any particular HWND
  currently belongs to a combined group.
- `owner-chain-200ms` and `owner-chain-800ms`: after Alt release, record
  current foreground HWND, up to four successive `GW_OWNER` links, numeric
  process IDs, visible/minimized state and extended style bitmask. No
  process names, window titles, text or screenshot data are captured.
  This helps show whether a foreground application is owned by one of
  WindowTabs' proxy HWNDs and whether a `WS_EX_NOACTIVATE` host participates.

The snapshots happen after release on the same background sampling path as
the existing focus snapshots. No foreground, style, or ownership **changes**
are performed by these diagnostics.

When reporting, compare whether the target app's owner chain is empty with
combining disabled and populated with combining enabled, and whether a
same-window return correlates with a specific proxy/host chain.
`WS_EX_NOACTIVATE` on the combined taskbar host is a **suspect**, not
a proven root cause; changing it without isolating the shell activation
mechanism could introduce unwanted focus changes on minimize/restore.

For concise review after your session, upload the whole
`focus-trace.jsonl` rather than relying on F11/F12 annotations alone;
the return-to-start samples may detect unmarked incidents.


## Proxy activation correlation (follow-up to PR #8)

With `focus-trace.enabled` present at launch, WindowTabs also records
`taskbar-proxy-activation` for its own combined host and per-tab proxies.
Each event names the role and carries the raw Win32 message ID and
`wParam`/`lParam` values for `WM_ACTIVATE` (0x06),
`WM_ACTIVATEAPP` (0x1C), `WM_MOUSEACTIVATE` (0x21), and
`WM_NCACTIVATE` (0x86). Parameter meanings are **message-specific**;
do not interpret `lParam` as a HWND for every message.

The recorder additionally takes `owner-chain-alt-release` immediately
after releasing Alt. Delayed 200/800 ms records now preserve the **original**
Alt+Tab attempt ID, even if a subsequent press occurs before the sample.
If presses overlap, a late sample still describes foreground at its actual
sample time; it is **not** reliable evidence that the earlier attempt failed.

Compare `taskbar-proxy-activation`, `os-foreground`, Alt-release snapshots
and the existing `switcher-*` WinEvents. These messages reveal how our
proxy windows react during shell switching; they do **not** expose which
Alt+Tab tile the user selected. We deliberately do not hook external
application WndProcs, alter proxy styles or modify focus behavior. An
absence of proxy activation messages is not proof the proxy was uninvolved.

For a useful controlled session, compare the same two applications with
taskbar combining off, on, then off. Mark an observed failed selection via
Ctrl+Shift+F12, and retain the full JSONL file for correlation.

## Temporary preview-owner handoff: read-only correlation

The combined taskbar host and per-tab proxy windows are not static throughout
the taskbar thumbnail lifecycle. A DWM iconic thumbnail or live-preview request
on a per-tab proxy calls `TaskBarButton.releasePreviews()`: the per-tab proxies
temporarily lose their `GW_OWNER` link to the combined host, but **real
application windows remain owned by their individual proxies**. A 400 ms timer
reconnects those proxy owners (if no taskbar thumbnail popup is visible),
unregisters/re-registers the proxy thumbnails, restores tab order, and calls
`ActivateTab` / `SetTabActive`.

This flow existed before the diagnostic addition. It is a **plausible,
unconfirmed interaction** with native Explorer Alt+Tab enumeration and must not
be patched merely because a failure used a combined owner root.

With `focus-trace.enabled`, WindowTabs now records bounded events surrounding
that previously unobserved lifecycle:

- `taskbar-dwm-preview-request` identifies the first thumbnail/live
  request before each preview-release interval.
- `taskbar-preview-release` and `taskbar-preview-owner-before/after`
  show the actual numeric proxy `GW_OWNER` transition to zero, and whether
  Windows accepted it.
- `taskbar-preview-rechain-before/after`,
  `taskbar-preview-unregister`, and
  `taskbar-preview-register-before/after` bracket re-registration.
- `taskbar-active-before/after` bracket the shell taskbar display-state
  notifications `ITaskbarList::ActivateTab` and
  `ITaskbarList3::SetTabActive`. Neither API is a request to transfer
  foreground to an actual application.
- `taskbar-app-adopt-before/after` records owner reassignment when a real
  application first joins its per-tab proxy.

No new timer, input hook, Win32 state change, or foreground override is added.
When tracing is disabled, these new records do not run. Instrumentation on
the trace-enabled process can still affect timing, so a temporal correlation
is **not** causal proof. This opt-in data captures HWND/owner and numerical
group IDs; it deliberately does not capture titles or application contents.

Use `tests/Analyze-AltTabTraces.py` from the next stacked diagnostic PR to
display these stage counts near a persistent hidden Explorer foreground sample.
In particular, distinguish a preview owner-chain mutation *preceding* a failed
shell switch from one occurring *after* the failure, and distinguish taskbar
button state notifications from true Win32 foreground activation.

**The previous 2026-10-23 recording cannot settle this hypothesis**, because
this owner-handoff instrumentation was not yet present. Do not ask for another
manual 600-switch exercise; collect opportunistically only if needed.
