# Native Alt+Tab investigation: transient owner-root topology

## Scope

The existing combined taskbar design is deliberately preserved. We do **not**
repeat the rejected ownerless PR #11; it broke the invariant that a WindowTabs
group is a **single entity in the native Windows Alt+Tab switcher**, even
though the combined taskbar icon and thumbnails still worked.

Native user observations: failures are confirmed when taskbar combining is
enabled, even with Flow Launcher exited and both diagnostics disabled. The
absence of a confidently observed failure with combining disabled is **not**
proof that the problem is exclusive to combining. PR #10 did not resolve it.

## Concrete code path

`TaskBarButton` arranges the owner chain:

```text
Shared taskbar host
  -> Preview proxy A -> Real application A
  -> Preview proxy B -> Real application B
```

When Explorer requests DWM thumbnail/Peek contents, `releasePreviews()`
temporarily changes **the per-tab preview proxies'** owners to zero.
The real applications **remain owned by their respective previews**:

```text
Shared taskbar host [no longer the root of real apps]
Preview proxy A -> Real application A
Preview proxy B -> Real application B
```

The `rechain` timer later reattaches the previews to the host and re-registers
them with Explorer once it believes the taskbar popup is closed.

The native `GroupLifecycle` fixture tests the owner chains before and
immediately after `releasePreviews()` using real HWNDs. This is a
deterministic structural test, not an Alt+Tab success assertion.

## Interpretation

Historical Microsoft engineering commentary describes the **classic**
Alt+Tab selection algorithm in terms of visible windows, root owners, and
the most recently active visible popup. It explicitly cautions that the
algorithm is an implementation detail that has changed across versions:
https://devblogs.microsoft.com/oldnewthing/20071008-00/?p=24863

The `GetLastActivePopup` API itself is documented separately:
https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastactivepopup

**Inference, not proven causation:** a change in ownership roots during
thumbnail interaction could perturb Explorer's native switcher eligibility
or activation behavior. A transient split into different roots is not
equivalent to a confirmed extra Alt+Tab tile on Windows 11. The earlier
traces did not establish that the group entered this state at a failed
attempt; one failure snapshot showed an intact owner chain.

## Suggested discriminating experiment

Before attempting any change to the release/rechain code, establish whether
a real failure occurs **near** a preview-owner detachment or reattachment.
If needed, design a *bounded event-driven test* around those two native
owner mutations (not a broad keyboard or foreground recorder), with an
independent indication of whether the switcher selected the intended
group. If failures occur with the owner chain intact and no adjacent
preview transition, deprioritize this hypothesis.

Do not change `WS_EX_NOACTIVATE`, `GWL_HWNDPARENT`, Alt+Tab keyboard
handling, or use foreground forcing as a speculative workaround. Any
new implementation must retain: one native Alt+Tab entity per group,
correct foreground activation, one taskbar button with functional
thumbnails/Peek, and reliable virtual desktop isolation.
