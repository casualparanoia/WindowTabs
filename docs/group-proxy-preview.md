# Combined taskbar proxy: Alt+Tab / AltTabby preview experiment

This change is deliberately independent of `feature/disable-remembered-group-restore`.
It changes only the combined group proxy's DWM preview publishing and the
related diagnostics. It does **not** change Windows' Alt+Tab switcher or
Stardock AltTabby, and does not prove either consumer uses DWM iconic bitmaps.

## HWND model (from source)

* The main `TaskBarButton.window.hwnd` uses `WS_POPUP` plus
  `WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_APPWINDOW`, is shown without
  activation, and is added to the taskbar by `ITaskbarList::AddTab`.
* Each `TaskbarTab.window.hwnd` is a distinct layered proxy registered
  using `RegisterTab(child, host)`. These already publish their own
  `WM_DWMSENDICONICTHUMBNAIL` and
  `WM_DWMSENDICONICLIVEPREVIEWBITMAP` pictures for Explorer taskbar hover.
* Application HWNDs are owned by the per-tab proxy so they follow the
  group across virtual desktops. The per-tab ownership can be temporarily
  released while Explorer's hover thumbnails are displayed.
* The host handlers now publish the front tab's **standalone** `TbButtonTab.hostPreview`
  bitmap: the active application's content, without embedding it in the group's
  full-size tab-strip canvas. Explorer's per-tab hover still uses `TbButtonTab.preview`
  and retains its existing composite content and strip rendering. `TbButton.activeKey`
  is recomputed from the group's leading member. DWM's host cache is invalidated
  on active-tab changes, member insertion/removal/reorder, group geometry edits and
  explicit group bounds events.
* The host advertises `DWMWA_FORCE_ICONIC_REPRESENTATION=1` and
  `DWMWA_HAS_ICONIC_BITMAP=1`. The host no longer delegates a
  `WM_DWMSENDICONICLIVEPREVIEWBITMAP` request to `DefWindowProc`.
* Both paths use `TaskbarPreview.send`, which disposes captured, transformed
  GDI+ bitmaps after publication. Win32's publisher deletes the temporary
  HBITMAP after the DWM call, and logs negative HRESULTs.

## Windows verification (run from Nushell)

Build and run **one branch at a time**. Close any running WindowTabs first.
The app is a Windows-only .NET Framework 4.8/WinForms program; a Linux
build does not substitute for these tests.

```nu
git switch feature/group-proxy-preview
dotnet build .\WindowTabs.sln -c Debug
pwsh -NoProfile -File .\tests\Run-Tests.ps1 -Suites TaskbarPreview
let appdir = ('.\WtProgram\bin\Debug' | path expand)
'' | save --force ($appdir | path join 'peek-trace.enabled')
```

Run `WindowTabs.exe` from `$appdir` and enable *Combine taskbar icons per
group*. Group two ordinary windows (preferably belonging to different apps).
Inspect the native Windows Alt+Tab and Stardock AltTabby views separately;
switch active tabs and repeat. The user-reported baseline is **one generic or
blank combined tile**, while Explorer's per-tab hover images already work.

The trace lives alongside the executable. Inspect it with Nushell:

```nu
let appdir = ('.\WtProgram\bin\Debug' | path expand)
open --raw ($appdir | path join 'peek-trace.jsonl') | lines |
    where { |line| not ($line | is-empty) } |
    each { |line| $line | from json } |
    where stage =~ 'host'
```

Look for:

1. `host-created`: host HWND, window styles and owner HWND. Compare with
   the visible combined entry using a native HWND inspector (e.g. Spy++).
2. `host-thumbnail-request` and `host-thumbnail-publish`: proof **DWM**
   asked the host for a bitmap, with requested size and HRESULT.
3. `host-live-request` and `host-live-publish`: the full-sized Peek path.
4. `host-active`: maps selected per-tab proxy HWND on tab changes.
5. `host-source`: source dimensions before the host fits the active window to
   DWM's requested canvas. A small underlying capture may still look blurred.
6. `host-selection-sync`: foreground-marked tab proxy and the saved group
   z-order initially disagreed, and the host was updated to publish the tab
   that Explorer actually marked active.
7. `host-invalidate-active` / `host-invalidate-update`: DWM's response HRESULT
   when WindowTabs marks a newly active tab or changes the group bounds/members.
   DWM requests a replacement only when needed, so a successful invalidation
   need not immediately produce a new `host-thumbnail-request`.
8. Existing `thumbnail-request`, `capture`, `publish`, and
   `register` stages: verify ordinary Explorer taskbar-hover paths still work.

If PrintWindow produces a 1x1 failure image but there is a previously valid
capture, fallback now uses the known native window size, **not** the failed
image's 1x1 size. This is regression-tested in `TaskbarPreview`. The opt-in
`capture-choice` trace stage distinguishes `live`, `cached-hidden`,
`cached-after-blank`, and `uncached-blank`, with requested and resulting
pixel dimensions. A black first capture without prior usable cache remains
an unresolved case.

A blank initial capture (especially a Chromium/Electron window that cannot be
printed while hidden) cannot be fixed by centring or invalidation alone.
WindowTabs retains a previous non-blank capture up to 800 px on its longest side,
so switching to an inactive tab can temporarily use a lower-resolution image.
If black/soft frames remain, compare `host-source` dimensions and publish
HRESULTs with the app's foreground/minimized state before changing capture backends.

A successful HRESULT proves DWM accepted a published bitmap **for this
HWND**, not that AltTabby draws it. If AltTabby captures the real application
HWND or uses PrintWindow, WGC, DWM redirection surfaces, or another
mechanism, this patch alone cannot repair it. A comparison of actual HWNDs
and capture methods is required before changing window styles/ownership.
Do not work around an unknown capture path by drawing into a transparent
proxy: that risks breaking input, focus and desktop isolation.

### Regression matrix

| Scenario | Expected |
|---|---|
| Native Alt+Tab, active tab A → B | One group entry with new B thumbnail |
| AltTabby, active tab A → B | Same, **verify independently** |
| Explorer taskbar hover | Separate A/B thumbnails; click activates selected tab |
| Minimize/restore | Saved image or safe fallback, no crash or blank if cached |
| Cross-app manual group | Foreground app content matches preview |
| Moving groups between desktops | Correct ownership/visibility, no ghost entry |
| Resize and DPI change | Bitmap fits requested thumbnail/client dimensions |
| Closing group or switching repeatedly | No leaked GDI objects, no stale preview |

After testing, remove the trace toggle by deleting `peek-trace.enabled`
from the application directory. Tracing logs HWNDs and sizes, not
window titles or image contents.
