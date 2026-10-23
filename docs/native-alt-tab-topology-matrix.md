# Isolated native Alt+Tab topology matrix (2×2)

## Purpose and limitations

This opt-in fixture tests **two different Win32 mechanisms independently**,
instead of relying on another 200–600-switch session with the user's normal
windows. It is deliberately **not a WindowTabs production fix** and does not
exercise the WindowTabs UI, group rules, taskbar previews, cross-process
ownership, virtual-desktop migration, or DWM preview-release timer.

All four cells create an equivalent **disposable taskbar host**, two transparent
per-tab proxy HWNDs, two ordinary WinForms application HWNDs, and an independent
fixture anchor. They differ only in the two controlled factors:

| Cell | ITaskbarList3 tab registration | Application/proxy native owner tree |
|---|---|---|
| 00 | No | No |
| 10 | Yes | No |
| 01 | No | Yes |
| 11 | Yes | Yes |

In all cells the host exists and is added to the taskbar. With registration
enabled, two proxies are registered via RegisterTab, ordered via
SetTabOrder, and the selected proxy is reported using SetTabActive.
With native ownership enabled, the two test application windows are owned by
their individual proxy HWNDs, whose GW_OWNER is the combined host. With
ownership disabled, application windows and proxy HWNDs have no native owner.
The probe verifies every direct/root-owner relationship before issuing input.

The 2×2 cells are **not identical in resulting Explorer tile eligibility**;
this is a consequence of changing native ownership itself. Unregistered
preview proxies are not a faithful replacement for WindowTabs' DWM thumbnails.
One binary pass/fail per cell cannot prove a causal effect; compare shell
foreground events and topology first, and treat repetition/order effects
carefully. A mismatch only means the native shell switched away from the
fixture's *expected MRU pair*, not that we know the highlighted tile.

## Safety and scope

- Runs as an independent 64-bit .NET Framework 4.8 C#5 process. It does not
  load WindowTabs, change application settings, or modify any external HWND.
- Refuses to start with normal WindowTabs running, without an unlocked
  Default input desktop, without Explorer's taskbar, with modifiers or mouse
  keys held, or without room for all disposable fixture windows.
- Requires explicit -Interactive to create test HWNDs or inject input.
  Build-only mode **never creates GUI windows, installs hooks, or sends keys**.
- Before each Alt+Tab press it checks that the exact expected fixture HWND is
  already foreground; if an unexpected app or Explorer host is foreground,
  it records the mismatch and sends **no further input**.
- One case primes a recent A/anchor pair only once. Consecutive quick
  Alt+Tab presses (nominal 35 ms hold) use no mouse clicks or forced
  re-priming. Each switch is observed at 0, 40, 120, 300, and 900 ms
  from Alt release, then compared to the expected fixture HWND.
- The optional foreground WinEvent recorder is passive, out-of-context, and
  does not alter foreground policy.
- On exit, native ownership is removed from fixture applications **before**
  the owners are destroyed; taskbar registrations are released. Unexpected
  Win32/COM errors abort testing rather than continuing with invalid topology.

The private JSONL report contains UTC, elapsed milliseconds, scenario,
numeric HWNDs, PIDs, window classes, ownership chains, foreground events and
switch samples. It contains no window titles, URLs, executable paths, user
content, clipboard, or full key history. Review process IDs before sharing.

## Build-only check (safe for CI and normal use)

~~~powershell
pwsh -NoProfile -File .\tests\Run-NativeAltTabTopologyMatrix.ps1 -BuildOnly
~~~

This compiles the standalone driver and verifies it covers exactly the four
unique factor combinations, plus the expected keyboard INPUT layout. The
Windows Release CI job does this under PowerShell 5.1 and 7. It does **not**
run any native switch or claim the live 2×2 matrix passed.

## Optional isolated interactive run

This is **not currently required from the user**. For a later controlled
interactive validation, exit WindowTabs first and use an idle unlocked Windows
desktop with a working area of at least 1100 × 850 pixels:

~~~powershell
pwsh -NoProfile -File .\tests\Run-NativeAltTabTopologyMatrix.ps1 -Interactive -SwitchesPerCell 4
~~~

The driver writes a fresh
tests/Debug/topology-matrix-<random-id>/topology-trace.jsonl and stops at
the first failed preflight, unexpected foreground or topology assertion.
Do not increase switch counts or conduct extended manual sessions without a
specific hypothesis derived from the existing traces.

## Source and API documentation

- Microsoft ITaskbarList3::RegisterTab, SetTabOrder, SetTabActive:
  https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-itaskbarlist3-registertab
- Microsoft SetWindowLongPtrW / GWLP_HWNDPARENT:
  https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowlongptrw
- Microsoft owned-window constraints:
  https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features

Registration reports taskbar thumbnail grouping, not application activation.
Owner relationships affect stacking, minimizing, and destruction; they must
be restored before any experimental proxy teardown.

## Offline result interpretation

The fixture's output is deliberately not a binary "Explorer bug yes/no" verdict.
To classify an existing private recording without running WindowTabs or sending
any input:

~~~powershell
py -3 .\tests\Analyze-NativeAltTabTopologyMatrix.py '.\tests\Debug\topology-matrix-<id>\topology-trace.jsonl'
~~~

The standard-library analyzer returns one cell for each registration/ownership
factor combination. For each, it reports whether that cell actually ran,
whether its fixture switches completed, which +900 ms samples were observed,
and the number of **candidate** persistent hidden Explorer hosts versus other
unexpected foreground HWNDs. All four cells must have both their topology
event and completed switch observations to be marked complete. Missing steps,
missing late samples, aborted runs and unexecuted scenarios do **not** become
zero-failure results.

A persistent hidden Explorer candidate requires an observed foreground window
that is invisible, has class XamlExplorerHostIslandWindow, and has a PID
matching the live Explorer taskbar process **at the time it was observed**.
The late sample is measured at 900 ms nominal after the injected Alt release,
not at the initial shell callback. A transient Explorer callback at +120 ms,
or a foreign process with the same window class, is explicitly not counted as
this candidate. The analyzer does not establish user-selected tile, visual
stacking, or causality.

~~~powershell
py -3 .\tests\test_analyze_native_alt_tab_topology_matrix.py
~~~

The synthetic unit tests check successful four-cell reports, persistent
Explorer, transient Explorer, foreign PID, wrong-application foreground,
incomplete data, unrun cells, failed runs, and invalid trace structure. CI
runs these tests but never runs the fixture interactively.

**Interpretation:** A difference between cells would motivate further
controlled investigation, **not** a claim that one factor caused the actual
WindowTabs defect. The synthetic proxy windows are not DWM bitmap producers,
and all forms run within one process; neither fact reproduces the app/proxy
cross-process ownership topology of the normal WindowTabs user session.
