# Two combined groups — native Explorer Alt+Tab E2E

This is a **separate, opt-in black-box workload** stacked on the existing
[draft native Alt+Tab E2E](native-alt-tab-e2e.md).

## Question under test

Do two *distinct* WindowTabs taskbar groups, each with two application
windows and their own synthetic taskbar owner root, switch correctly using
**real Windows native Alt+Tab**, even as the active member of both groups
changes?

The previously validated harness covered one combined group versus one or
two independent **non-grouped** windows. This workload targets group-to-group
handoffs, not another long repetition of that baseline.

## Run

On an unlocked, idle Windows desktop, save other work and **exit any normal
WindowTabs instance**. Prefer Flow Launcher fully exited so an unrelated
foreground activation does not mask the WindowTabs-specific question.

```powershell
pwsh -NoProfile -File .\tests\Run-DesktopE2E.ps1 `
    -Interactive -NativeAltTab -AltTabTwoGroups -AltTabSwitches 20
```

Run only a short first pass. This test drives the pointer and keyboard.
Ordinary Windows CI compiles the driver but **cannot exercise the live
Explorer Alt+Tab switcher**. Stage artifacts are written under an isolated
`tests/Debug/desktop-e2e-*` directory. No personal applications or
browser profiles are touched.

## Fixture and oracle

1. Create **four disposable WinForms application HWNDs** from the driver,
   initially independent with taskbar grouping manually controlled.
2. Use the existing production drag path to form **Group A = two members**
   and **Group B = two members**, with `combineIconsInTaskbar=true`.
3. Assert both members of each group share one synthetic native owner root,
   while Group A and Group B have **different** roots.
4. For each two-switch pair, choose a member from A and activate it by
   clicking its strip, then choose a member from B and activate that strip.
   Alternate both active members between pairs. Native Explorer Alt+Tab
   should return to the chosen member of A, then back to the chosen member
   of B.
5. After each native Alt+Tab, verify the **exact expected foreground HWND**,
   tab strip owner, and top-level window hit-tested at the center of the
   expected client surface. A mismatch is captured with PID/class/owner/root
   metadata in `foreground-mismatch.json` before teardown.

This tests **known two-entry MRU transitions between two combined groups**,
not the user's arbitrary multi-window shell switcher selection order. A
foreign application can still become foreground. If it does, the recorded
process identity helps distinguish external interference from a grouped
activation failure.

The ordinary single-group E2E modes and production WindowTabs behavior
remain unchanged. If this fixture passes, the next meaningful test is
application-specific (e.g. disposable Chromium/Vivaldi windows), rather
than repeating the synthetic stress loop.
