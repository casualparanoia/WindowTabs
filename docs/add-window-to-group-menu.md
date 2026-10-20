# Add window to group

Right-click an existing WindowTabs tab and choose **Add window to group →**.
This is a conventional cascading native-style flyout, like the Windows desktop
**New →** menu; it **does not** expand or resize the parent context menu.

The submenu snapshots currently available application windows and includes
members of other WindowTabs groups. Group membership is read on the main STA
through the dispatched `IDesktop.memberWindows` accessor, never directly from
foreign group actors on the tab-strip thread. Rows show the app name and window title
(truncated for long titles), without fetching large images or application icons
on the tab-strip thread. It excludes:

- the current group's own windows,
- destroyed and non-tabbable HWNDs,
- windows confirmed to be on another virtual desktop.

If there are no eligible windows, the flyout shows one disabled placeholder.
Clicking an item posts a main-STA transfer command; the handler validates that
the selected native window still exists, remains eligible, and is not already a
member of the destination group.

The `IDesktop.addWindowToGroup` operation marks the destination as a manual
group, preserving the user's explicit choice against later auto regrouping.
If a window belongs to another group, it uses the same staged native
`GroupInfo.addAfterDetaching` flow as tab drag/rebuild and waits for the previous
group's HWND ownership cleanup before adopting it. Windows without a group
attach normally.

## Validation

The `GroupOperations` native regression transfers an existing group member into
another live group and then back; it checks membership uniqueness, manual-group
identity and native teardown. Run Windows CI for every update.

Manual checks (required before merging):

1. Hover **Add window to group →**; the flyout opens to the right, and the
   parent menu's dimensions stay fixed.
2. Use keyboard arrows, hover and click; the submenu closes normally.
3. Transfer an ungrouped window and then a window from another group; ensure
   neither duplicates nor disappears, and focus/taskbar ownership stays correct.
4. Repeat with a candidate closed before clicking, no eligible candidates,
   and windows on multiple virtual desktops.
5. Test with combined and separate taskbar icons.

This feature is independent of remembered-group restore, proxy previews and
independent window sizing.
