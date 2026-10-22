# Isolated Chromium-family browser native Alt+Tab E2E

The earlier native Explorer fixture tests used disposable WinForms HWNDs.
With Flow Launcher exited, those passed group-to-anchor and group-to-group
tests with changing active members, exact foreground, and visual z-order
checks. They do **not** exercise Chromium's real top-level window machinery.

This optional workload adds **real Vivaldi/Chromium top-level browser windows**
without touching the user's existing browsing state.

## Running

On an unlocked, idle Windows desktop, save your work, exit your **normal
WindowTabs instance**, and preferably fully exit Flow Launcher while trying
to isolate the original WindowTabs-only native Alt+Tab issue. **You must
also fully exit all Vivaldi processes, including background mode, before
running this test**, because WindowTabs filters included browsers by
executable path; it cannot otherwise distinguish the staged Vivaldi from
your normal Vivaldi session:

```powershell
pwsh -NoProfile -File .\tests\Run-DesktopE2E.ps1 `
    -Interactive -NativeAltTab -AltTabBrowser -AltTabSwitches 20
```

The script auto-discovers Vivaldi in standard user/system install locations.
For a portable or custom install, specify its **full installed executable
path** explicitly:

```powershell
pwsh -NoProfile -File .\tests\Run-DesktopE2E.ps1 `
    -Interactive -NativeAltTab -AltTabBrowser `
    -BrowserExecutable 'C:\Path\To\vivaldi.exe' -AltTabSwitches 20
```

The same fixture may use `chrome.exe`, `chromium.exe`, or `msedge.exe`
through an explicit `-BrowserExecutable` path. The short 20-switch run is
intended as **a first functional check**, not a prolonged stress test.

## Isolation and controls

- Creates a **new** `browser-profile` inside the unique staged
  `tests/Debug/desktop-e2e-<id>` directory. Aborts if that profile
  directory already exists.
- Launches **two `--new-window about:blank` windows**, using the *same*
  private `--user-data-dir` and disabling extensions, sync, and first-run
  prompts. The second launch should forward to the first isolated browser
  process. No normal profile path is used.
- **Refuses startup if any same-name browser process is already running.**
  This is essential: while the driver identifies its own browser by PID,
  WindowTabs's `includedPaths` matches the **executable**, including all
  user windows launched from the same installed binary. The test also
  checks for unexpected external top-level browser HWNDs before grouping
  and before every native switching pair. A failure here is a safety
  preflight, not an Alt+Tab bug.
- Identifies and allows input only to top-level `Chrome_WidgetWin_*`
  windows with the **exact PID of the browser process launched by this
  stage**. It does **not** attach to arbitrary same-executable windows.
  If Chromium creates windows in another process, the test reports a
  setup failure rather than falling back to the user's ordinary session.
- Staged WindowTabs portable settings include only that browser executable
  path; the two browser windows are grouped into one taskbar group.
- Reuses the independent `DesktopAnchor.exe` as a known foreground
  counterpart. Rotates the active browser group member before each pair
  of genuine native Explorer `Alt+Tab` key inputs.
- Verifies **exact expected HWND**, tab strip ownership, and the visible
  top-level HWND at the center of the expected browser or anchor.
  Unexpected foregrounds write `foreground-mismatch.json` with native
  PID, process, class, owner and observed foreground changes.
- Cleanup closes only the specific staged browser windows and spawned
  browser process handles, and removes the temporary profile if Chromium
  has released its files. It never issues `taskkill /IM vivaldi.exe`,
  `Get-Process vivaldi | Stop-Process`, or closes user profile windows.
  If files remain locked, the staged profile can be cleaned manually
  after the isolated browser exits.

**Important limitation:** The profile is disposable but the browser is
still a real installed application. A system policy, a first-run prompt,
browser startup differences, security software, multi-process ownership or
a browser update may cause fixture **setup** to fail. That does not
establish a native Alt+Tab defect. The setup and expected/actual diagnostics
must be interpreted separately.

The test harness is compiled by Windows CI under PowerShell 5.1 and 7,
but **CI does not execute genuine desktop Alt+Tab**. No production
WindowTabs ownership, activation, or taskbar behavior changes are made.
