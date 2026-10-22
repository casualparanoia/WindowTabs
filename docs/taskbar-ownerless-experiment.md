# Ownerless combined-taskbar experiment

This is an **experimental** alternative to WindowTabs' normal combined-taskbar
ownership model, intended to isolate a possible native Windows Alt+Tab
interaction. It is **off by default** and is not a verified Alt+Tab fix.

## Design

The normal mode creates a taskbar host and per-tab DWM preview proxies, then
assigns each real grouped application's `GW_OWNER` to its preview proxy. The
preview proxy is owned by the shared host. That arrangement keeps applications
on their group's virtual desktop but changes the native owner chain that
Windows and Explorer use when enumerating and activating windows.

In ownerless mode, **the same host and per-tab thumbnail proxies remain**,
using `ITaskbarList3::RegisterTab`, `SetTabOrder`, `SetTabActive`, and the
existing iconic thumbnail and Peek renderer. Instead of assigning external
apps a synthetic owner, WindowTabs uses `ITaskbarList::DeleteTab` to remove
each real app's separate taskbar button, and `AddTab` on release. The app's
existing native owner and window styles are not changed by this mode.

This tests whether taskbar representation can be decoupled from external
native window ownership. The RegisterTab API accepts synthetic tab and host
HWNDs without requiring that either HWND be a native owner of the real app:
https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-itaskbarlist3-registertab

## Enabling for an isolated run

Place an empty file named `taskbar-ownerless.enabled` next to the actual
`WindowTabs.exe` (e.g. `WtProgram/bin/Debug/`) **before launching it**.
Exit and restart WindowTabs completely. No foreground or Peek diagnostics need
to be enabled. To return to the normal mode, exit WindowTabs, remove that file,
and relaunch. The marker is read when each group is created, so changing it
while the app is running does not switch existing groups.

Only combined taskbar groups exercise this mode; ordinary groups are unchanged.
Do not enable this for production use or rely on it with important multi-desktop
arrangements until it is tested interactively.

## What automation proves and what it cannot prove

The native `GroupLifecycle` regression checks that a two-member group creates
its synthetic combined previews, hides its separate app taskbar registrations,
and does not change either app's `GW_OWNER` or extended window styles. It also
checks safe release and destruction.

**It does not prove:** that Explorer visually shows one stable taskbar button
or correct thumbnails; that Windows' Alt+Tab uses the intended target; that
programmatic DeleteTab persists after all application reactivation events;
that Peek is correct on the user's multi-monitor configuration; that
minimize/restore animations behave correctly; or that external apps remain on
the correct virtual desktop. CI runs on a noninteractive isolated Windows
desktop and cannot exercise the live Explorer switcher.

### Key limitation

The normal owner chain also carries **virtual desktop association** for real
application windows. A real HWND without its own taskbar entry and without a
proxy owner may become visible on multiple Windows virtual desktops. Do not
declare this approach suitable for production until that behavior is measured.
Also, after Explorer restarts or a native app re-registers itself, its own
taskbar button may reappear; taskbar COM registration is not a permanent
window-style change.

No attempt to intercept Alt+Tab keys or synthesize focus changes is added.
The original implementation remains the default; keep this PR in draft.
