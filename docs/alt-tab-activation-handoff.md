# Native Alt+Tab: activation handoff boundary

This work investigates the intermittent failure where Windows' native
Alt+Tab does not foreground the expected window when changing to/from a
WindowTabs group. It does **not** investigate thumbnail rendering inside
Alt+Tab, and does not change the existing combined-taskbar/owner model.

## Two activation paths

A real grouped application may be activated **directly by Windows**. In
that case, WindowTabs sees a later `EVENT_SYSTEM_FOREGROUND` notification
and may repair native stacking, but its synthetic host/proxy activation
callback is **not necessarily involved**.

Alternatively, a native `WM_ACTIVATE` with `WA_ACTIVE` may arrive on
the WindowTabs-owned combined taskbar host or a per-tab preview proxy:

- Host `WM_ACTIVATE / WA_ACTIVE` -> the host callback queues an activation
  of the group's top member with `force=true`.
- Per-tab proxy `WM_ACTIVATE / WA_ACTIVE` -> queues activation of the
  corresponding member with `force=false`.
- The group STA calls `setForegroundOrRestore` on the requested member,
  then `BringWindowToTop`, and if it actually became the foreground
  HWND, performs another explicit native `raise`.

This code handles `WA_ACTIVE` only. `WA_CLICKACTIVE` is defined for
activation *by clicking*, rather than Alt+Tab keyboard selection, and is
not changed in this investigation.

Relevant documentation:
https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-activate

**Do not infer** that seeing (or not seeing) a proxy callback proves
which item Explorer highlighted in its native switcher. No documented
API used by WindowTabs exposes that selection.

## Minimal optional probe

An empty file named `activation-probe.enabled`, placed beside the
**actual** `WindowTabs.exe` before startup, enables a small
`activation-probe.jsonl` event log in the same directory. Restart
WindowTabs after changing the marker.

It does **not** enable `focus-trace.enabled`, register any keyboard
hook, subscribe to system foreground events, poll, or capture window
contents/titles, executable names, paths, or keystrokes. It records
only real Windows messages received by WindowTabs' **own** proxy HWNDs
and WindowTabs' own tab activation dispatches, plus relevant foreground
events already handled by the group's existing WinEvent subscription.
It does not register any new hook. Logs rotate around 2 MiB into
`activation-probe.previous.jsonl`. Unlink the marker and
restart to turn it off.

Main stages:

| Stage | Meaning |
| --- | --- |
| `proxy-message` | Native `WM_ACTIVATE`, `WM_ACTIVATEAPP`, or `WM_MOUSEACTIVATE` arrived at a WindowTabs proxy |
| `proxy-activation-forwarded` | Proxy received `WA_ACTIVE` and invoked its activation callback |
| `host-callback` / `preview-callback` | Corresponding callback requested a group-STA dispatch |
| `host-activation-dispatched` / `preview-activation-dispatched` | Dispatch reached the group STA and selected an application HWND |
| `tab-activation-enter` | WindowTabs requested activation of a real app; not necessarily initiated by Alt+Tab |
| `tab-activation-after-request` | Observed foreground immediately after `SetForegroundWindow` or restore; NOT the native BOOL return |
| `tab-activation-after-zorder` | Observed foreground and native internal group z-order following `BringWindowToTop` and conditional `raise` |
| `group-foreground-event` | An existing native foreground callback arrived for a grouped HWND, the current foreground was a group member, or the cached foreground just left the group; includes callback HWND and current actual HWND separately |

A monotonically increasing `seq` makes event order reconstructible
even though disk writes are asynchronous. `foreground` is the
observed HWND at record time, not a promise about subsequent state.
Since direct app activation may bypass the proxy path, this is
**neither a selected-tile oracle nor a complete success/failure oracle**.

Interpret cautiously:
- Proxy callback and dispatched target recorded, but observed
  foreground is another HWND: a WindowTabs activation *request*
  occurred, but it did not immediately result in the target as foreground.
  It can still complete later.
- Requested app is foreground after the native request but isn't on
  top of the group: possible stacking problem.
- No callback near a user-observed failure: may be a direct app
  activation, Explorer behavior, or a path the probe doesn't observe.
  **It does not prove Explorer picked the wrong tile**.

The new `group-foreground-event` stage records only group-relevant events
from the **existing** WinEvent handler. In particular, it can show a real
app entered the foreground without a matching proxy activation callback.
That is consistent with direct native selection; it still does not reveal
the highlighted Explorer tile.

A native regression invokes `WM_ACTIVATE` on real synthetic host and
tab-proxy HWNDs to verify WndProc callback dispatch. Synthetic
`SendMessage` in CI does NOT test the real Explorer switcher or prove
activation success.

The rejected ownerless alternative (#11) remains excluded. Any later
behavioral change must preserve native Alt+Tab treating each group as
one entity, correct member activation, combined taskbar thumbnails,
and virtual-desktop isolation.
