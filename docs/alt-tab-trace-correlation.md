# Offline Alt+Tab trace correlation

This is an **offline, read-only** follow-up to
[passive-alt-tab-trace.md](passive-alt-tab-trace.md) and
[alt-tab-focus-trace.md](alt-tab-focus-trace.md).

It does not start WindowTabs, install hooks, synthesize input, change foreground
or window ownership, or transmit trace contents. Python 3.8+ stdlib only.

## Run on already collected traces

From the repository root (PowerShell):

~~~powershell
py -3 .\tests\Analyze-AltTabTraces.py --passive '.\tests\Debug\passive-alt-tab-<id>\foreground-trace.jsonl' --internal '.\WtProgram\bin\Debug\focus-trace.jsonl'
~~~

The internal trace is optional. If supplied, its timestamps must overlap with
the external recording; otherwise analysis fails rather than assuming the
recordings were simultaneous.

The tool prints JSON to standard output. It contains timestamps, HWNDs, process
names, window classes, owner/root/last-active-popup relationships and diagnostic
stage names. **Review/redact process identities before sharing.** It does not
collect window titles, browser URLs, text contents, executable paths or images.

## Strict detection rule

Report a *candidate anomalous persistent foreground* only if:

1. Alt was actually released after Alt+Tab. Multiple Tabs while Alt is held
   do not each complete a switch.
2. An external sample **at least 650 ms after Alt release**, **before the next
   Alt+Tab**, still observes the *invisible* Explorer XamlExplorerHostIslandWindow.
3. For independent confirmation, match an internal Alt-release event within
   150 ms, then require WindowTabs' delayed 800 ms sample to report the
   **same HWND and attempt** before any new chord.

External-only candidates are labeled as such. A correlation is **not** a claim
that the user perceived a failure, nor knowledge of the selected Alt+Tab tile.
The last observed application transition is a *candidate* destination.

The callbacks are asynchronous; this analysis cannot prove ordering or
causality on a few-millisecond timescale. No recorded WindowTabs activation
action does not prove WindowTabs' owner topology was uninvolved.

A manual marker is helpful but **not required**. Do not force repeated
manual Alt+Tab tests just to obtain one.

## Validation

~~~powershell
py -3 .\tests\test_analyze_alt_tab_traces.py
~~~

Six synthetic cases cover sustained hidden Explorer, held Alt, a subsequent
chord, visible Explorer, mismatched internal confirmation, and non-overlapping
files. The first simultaneous ordinary-use recordings from 2026-10-23
(10:22–10:29 UTC) yield one confirmed-by-two-recorders candidate at
10:24:47.669 UTC, without a manual marker. It is not a proven user-visible
failure or a production fix.
