# Independent window sizes

In **Settings → General → Tab behavior**, enable **Keep individual window sizes**
(`keepIndividualWindowSizes`, default `false`).

## Placement policy

When disabled, WindowTabs continues using the upstream `AlignWindow` follower request:
it synchronizes size, visible-frame position, and minimize/maximize transitions.

When enabled, the group uses a new `AlignPosition` request. A normal follower keeps
its existing native width and height; only its visible-frame **top-left corner** is
placed on the group's anchor. Tabs remain members of one group, not separate
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
