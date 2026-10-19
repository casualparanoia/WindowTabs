# Fork switch: automatic remembered-group restoration

The fork defaults to **no automatic restoration** of previous manual groups.
This is an internal policy switch, not a setting and not an app Auto-group rule.

Change `Program.RememberedGroupRestore.enabled` in
`WtProgram/Program.fs` from `false` to `true` to regain the upstream
default. `Program.Program(...,restoreRememberedGroups=true)` is also available
for tests, and is used by the upstream `StartupGrouping` regression.

With the switch disabled:

* `Program.restoredToken` never reads a saved `WindowGroupToken`.
* `Program.savedPlacement` does not find stored groups by app and window title.
* Newly discovered windows do not enter `settling` for later title matching;
  the settling callback also refuses to regroup if a record is present.
* Current-session drag/drop grouping, per-app Auto-group and explicitly named
  group rules remain in their existing code paths.
* **Recording stays on.** `rememberGroups` continues to maintain remembered
  group records during current-session manual edits; the flag itself does not
  wipe or migrate settings JSON. Existing records can still change through the
  ordinary editing/removal/expiry logic. This is a *restore-only* policy, not
  a freeze of persistent history.

The no-restore regression also verifies that previously stored tokens and
title records do not recombine fixture windows, that a delayed title change
does not recombine them, and that the group can still be joined manually.

## From Nushell on Windows

```nu
git fetch origin
git switch feature/disable-remembered-group-restore
git pull --ff-only origin feature/disable-remembered-group-restore
dotnet build .\WindowTabs.sln -c Debug
pwsh -NoProfile -File .\tests\Run-Tests.ps1 -Suites RememberedRestoreDisabled
pwsh -NoProfile -File .\tests\Run-Tests.ps1 -Suites StartupGrouping
```

The existing startup test opts into `restoreRememberedGroups=true` so that
the old token/title restoration algorithm remains covered. To validate
real-world behavior, group two unrelated programs by dragging, close and
reopen them, restart WindowTabs with the original windows still open, and
confirm neither automatically combines. With Auto-group configured ON for
one app or a named app-group rule, independently verify that rule still works.

Tests require an interactive Windows environment. Linux cannot execute the
native WinForms / shell integration.
