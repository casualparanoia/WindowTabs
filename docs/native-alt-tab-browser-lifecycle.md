# Isolated Vivaldi native Alt+Tab — group membership lifecycle

This is an **opt-in, test-only** extension of
[the isolated-browser native Alt+Tab fixture](native-alt-tab-browser.md).
The earlier steady-state browser fixture passed 20/20 native switches on
the user's Windows 11 desktop. The important missing condition is
**browser-window creation and closure while WindowTabs is managing a group**.

## First interactive run

Save your work, exit **normal WindowTabs, Flow Launcher, and every Vivaldi
process**, including background processes. The test explicitly refuses a
preexisting same-name browser because WindowTabs groups by executable path,
not by test-run PID. Use an unlocked, otherwise idle desktop.

```powershell
pwsh -NoProfile -File .\tests\Run-DesktopE2E.ps1 `
    -Interactive -NativeAltTab -AltTabBrowser `
    -AltTabBrowserLifecycle -AltTabSwitches 12
```

It creates a fresh temporary `browser-profile` under a unique
`tests/Debug/desktop-e2e-*` stage and opens only `about:blank` in the
installed Vivaldi executable. As with the baseline, custom browser installs
may be selected with an explicit `-BrowserExecutable` path.

The first run deliberately uses **12 native switches**, divided into
three short membership phases (4 switches each). It does not
require 100/200 repetitive switches.

## Workload / oracle

1. Start **two isolated Vivaldi windows**, then staged WindowTabs with
   taskbar icon combining and auto grouping enabled. Confirm one grouped
   tab strip, two real Chromium browser HWNDs and one synthetic native
   owner root shared by both. Perform the first native Alt+Tab pairs
   against a separate, disposable anchor window.
2. While WindowTabs remains running, open a **third window** through the
   *same* isolated Vivaldi profile using `--new-window about:blank`.
   Identify the newly created HWND by membership difference in the
   original browser process. Require exactly three browser HWNDs, one
   combined strip, and matching synthetic owner roots. Read the
   three-tab strip order and repeat genuine Alt+Tab to rotating active
   browser members.
3. Send `WM_CLOSE` to **only the third staged HWND**. Confirm it was
   destroyed, the original two HWNDs still exist, and the tab strip
   contracts to exactly two members sharing one native owner root.
   Rotate between the surviving members and run the final native
   Alt+Tab pairs.

Each switch checks expected exact foreground HWND, active tab-strip
owner and the topmost HWND hit-tested in the expected browser's client
center. A wrong native foreground writes `foreground-mismatch.json`
containing PID, class, owner/root and observed foreground transitions.
Group creation/closure/setup failures are **not** classified as native
Alt+Tab wrong-target failures.

Existing safety boundaries are retained: no personal browser profile,
never enumerate/reuse arbitrary same-executable personal browser windows,
refuse existing Vivaldi processes, and close only explicitly started
staged browser processes. Browser cleanup writes `browser-cleanup.json`
and retains a disposable profile if its files remain locked. Vivaldi
startup/IndexedDB warnings may be unrelated to switching and should
not be silently discarded.

CI compiles this driver under Windows PowerShell 5.1 and PowerShell 7
but cannot exercise the native Explorer switcher. The live behavior of
this new three-phase workload is **unverified until its first local run**.
No production WindowTabs activation or grouping code is changed.
