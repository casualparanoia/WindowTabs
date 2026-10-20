# Independent window sizes

In **Settings → General → Tab behavior**, enable **Keep individual window sizes**
(`keepIndividualWindowSizes`, default `false`).

## Placement policy

When disabled, WindowTabs continues using the upstream `AlignWindow` follower request:
it synchronizes size, visible-frame position, and minimize/maximize transitions.

When enabled, the group uses a new `AlignPosition` request. A normal follower keeps
its existing native width and height. **Align windows by** is a subordinate dropdown
with nine corners/edge-centres/centre anchor points; **Top left** is the default.
The corresponding point of each follower's *visible frame* is placed on the active
window's anchor. For example, **Center** aligns the window centres while preserving
each one's dimensions. Tabs remain members of one group, not separate
groups. Their native windows may have unequal widths and heights. Resizing the active
normal window must not propagate its dimensions to other normal windows.
Moving the group continues to align the follower origins.

The position-only path avoids applying the active window's `WINDOWPLACEMENT`
or resize bounds to minimized or maximized followers; their native states are
not overwritten by this request. It also does not try to manufacture or restore
historic sizes already overwritten while synchronized mode was enabled.

Toggling independent sizing **on** preserves the member windows' current sizes;
toggling **off** immediately re-aligns followers with the group's current active
window, restoring the original synchronized behavior.

## Threading and compatibility

`GroupInfo` reads the preference before starting the group's STA thread, rather
than blocking the initiating main thread during construction. Later changes
use `ISettings.notifyValue`, dispatched to each group's STA. Requests run on
the existing cancellation-aware `WindowPlacementQueue`, retaining cross-process
and HWND-lifetime guards. The original mode and its placement path are unchanged.

## Validation

Run `pwsh -NoProfile -File .\tests\Run-Tests.ps1 -Suites GroupOperations`
and `... -Suites SettingsEditors` (GitHub Actions runs both in Windows CI).

Manual test with two normal, resizable app windows:

1. Enable the setting; group A and B, giving them visibly different sizes.
2. Switch A → B → A: each should retain its own dimensions.
3. Resize B: A must not resize.
4. Move the group: the windows should share the same visible-frame origin.
5. Disable the setting: all followers should immediately match the active size.
6. Verify ungroup/re-group, maximized/minimized/Snap, DPI changes and different
   window decorations. Snap/maximized states are native OS-specific behavior and
   remain an explicit integration risk.

This change is independent of remembered-group restoration and the DWM proxy
thumbnail investigation. It should not be merged until Windows automated and
interactive validation have passed.


## Optional independent positions

Under **Keep individual window sizes**, enable **Keep individual window positions**
(`keepIndividualWindowPositions`, persisted default `false`). When enabled, member
windows retain both their native **size** and **position**. Moving or resizing one
member does not drag or resize the others. Switching to a different tab moves the
tab strip to that active window rather than repositioning the application HWND.

This is not a separate free-floating collection: Windows remain members of the
same group, with normal tab selection, manual grouping, and group minimize/restore.
While the setting is active, **Align windows by** is hidden because anchors
have no effect. Disabling the position option realigns windows to the selected
anchor while retaining each native size. Disabling independent sizes reverts
the upstream synchronized size-and-position policy.

The existing group-move handler temporarily parks hidden followers off-screen
during drag/resize. That parking is **suppressed** when positions are unlocked,
so finishing a drag does not leave a member stranded. If the setting is changed
during an ongoing resize, a previously parked follower is restored once at
move end, before free-position mode takes effect.

The new `GroupOperations` regression verifies the arbitrary follower location
survives the group's placement update and move/size sequence, then validates
the relock/resize policy. Its runtime flags are applied per group through the
same STA-safe settings notifications as independent sizing.

Future **Hide non-selected group windows** is a different policy. It needs
persistent per-HWND restore locations and explicit lifecycle handling for
activation, minimize/restore, ungrouping, virtual desktops, and app exit.
The temporary hide-for-resize behavior alone is not sufficient.


### Temporary parking recovery (2026-10-20)

Real-world testing uncovered a serious native-window regression: some grouped app HWNDs
(e.g., Spotify/Kate) could remain parked almost entirely off the desktop at the
lower-right after they were moved out of a group, or even after WindowTabs exited.
Maximizing with **Win+Up**, or **Alt+Space → M**, brought them back. This
does not indicate the process or window was destroyed.

The culprit is the existing resize-time `HideForMove` path, which writes a
temporary off-screen `WINDOWPLACEMENT.rcNormalPosition`. Group detach/teardown
previously cancelled the placement queue without any guaranteed inverse move.

The placement worker now saves the original native window rectangle before
parking, tracks the parked rectangle and HWND process/thread identity, and can
restore only if the native window is **still at the parking location** (never
overwrite a later user move). Both tab removal and group shutdown call
`FollowerPlacement.releaseAndRestore`: queue ownership is cancelled first;
a per-HWND lock then waits for an already-running native placement call to
finish before applying recovery. A mode transition can request
`RestoreParked` explicitly. If the HWND is gone or reused, recovery does
nothing.

**Independent-size groups no longer park their follower application windows
during resize.** This removes an unsafe behavior from an experimental mode;
the future **Hide non-selected group windows** option must have its own
well-defined recovery and visibility lifecycle.

`GroupLifecycle` includes actual Windows HWND tests for release-time recovery
and explicit unpark. This does not establish success for all third-party apps,
snap/maximized states or unusual multi-monitor arrangements: keep the PR
draft until manual validation.
