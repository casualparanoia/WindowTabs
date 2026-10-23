#!/usr/bin/env python3
"""Read-only classification of the isolated 2x2 native Alt+Tab test fixture.

An unexpected foreground HWND or hidden Explorer host is an OBSERVATION, not
a proven regression or indication of which native switcher tile was selected.
Requires no packages, no hooks, no input, and no application access.
"""
import argparse
import json
from pathlib import Path

FACTORS = (
    ("register-off_owner-off", False, False),
    ("register-on_owner-off", True, False),
    ("register-off_owner-on", False, True),
    ("register-on_owner-on", True, True),
)


def read_jsonl(path):
    result = []
    with Path(path).open(encoding="utf-8-sig") as handle:
        for lineno, line in enumerate(handle, 1):
            if not line.strip():
                continue
            try:
                record = json.loads(line)
            except (ValueError, TypeError) as exc:
                raise ValueError("%s:%d: invalid JSONL: %s" % (path, lineno, exc))
            if not isinstance(record, dict) or not all(
                key in record for key in ("scenario", "stage", "data")
            ):
                raise ValueError("%s:%d: missing trace fields" % (path, lineno))
            if not isinstance(record["data"], dict):
                raise ValueError("%s:%d: invalid event data" % (path, lineno))
            result.append(record)
    if not result:
        raise ValueError("Empty 2x2 topology fixture trace")
    return result


def is_hidden_explorer(window):
    return (
        isinstance(window, dict)
        and window.get("isShellExplorer") is True
        and window.get("windowClass") == "XamlExplorerHostIslandWindow"
        and window.get("visible") is False
        and window.get("exists") is True
    )


def snapshot(window):
    if not isinstance(window, dict):
        return None
    return {k: window.get(k) for k in (
        "hwnd", "windowClass", "visible", "isShellExplorer",
        "owner", "rootOwner", "lastActivePopup"
    )}


def analyze(records):
    cases = {}
    for name, register, own in FACTORS:
        cases[name] = {
            "registration": register, "nativeOwnership": own,
            "topologyVerified": False, "cellComplete": False,
            "attempted": 0, "passed": 0, "lateSamples": 0,
            "persistentHiddenExplorer": 0, "otherUnexpectedForeground": 0,
            "incompleteAttempts": 0, "findings": [],
            "_steps": {},
        }
    run_started = False
    run_finished = False
    run_failed = None

    for index, record in enumerate(records):
        stage = record["stage"]
        name = record["scenario"]
        data = record["data"]
        if stage == "run-start":
            if run_started:
                raise ValueError("Duplicate run-start event")
            run_started = True
            continue
        if stage == "run-complete":
            run_finished = True
            continue
        if stage == "run-failed":
            run_failed = data.get("reason", "unknown")
            continue
        if name not in cases:
            # The startup scenario is allowed before the first case.
            if name != "startup":
                raise ValueError("Unknown topology matrix case: " + str(name))
            continue

        case = cases[name]
        if stage == "topology":
            if case["topologyVerified"]:
                raise ValueError("Duplicate topology for " + name)
            if (data.get("name") != name or
                data.get("registered") is not case["registration"] or
                data.get("nativeOwnership") is not case["nativeOwnership"]):
                raise ValueError("Invalid factorial labels in " + name)
            case["topologyVerified"] = True
        elif stage == "cell-complete":
            case["cellComplete"] = True
        elif stage in (
            "before-chord", "post-chord-sample",
            "switch-passed", "unexpected-foreground"
        ):
            step = data.get("step")
            if not isinstance(step, int) or isinstance(step, bool) or step < 1:
                raise ValueError("Invalid switch step in " + name)
            state = case["_steps"].setdefault(
                step, {"began": False, "passed": False,
                       "late": None, "mismatch": None}
            )
            if stage == "before-chord":
                if state["began"]:
                    raise ValueError("Duplicate switch start in " + name)
                state["began"] = True
            elif stage == "switch-passed":
                state["passed"] = True
            elif stage == "unexpected-foreground":
                state["mismatch"] = data
            elif (
                data.get("requestedDelayMs") == 900
                and isinstance(data.get("actualDelayMs"), (int, float))
                and data["actualDelayMs"] >= 650
            ):
                if state["late"] is not None:
                    raise ValueError("Duplicate +900ms sample in " + name)
                state["late"] = data

    if not run_started:
        raise ValueError("Trace has no run-start event")

    for name, register, own in FACTORS:
        case = cases[name]
        steps = case.pop("_steps")
        case["attempted"] = sum(s["began"] for s in steps.values())
        case["passed"] = sum(s["passed"] and s["began"] for s in steps.values())
        case["lateSamples"] = sum(s["late"] is not None for s in steps.values())
        case["incompleteAttempts"] = sum(
            s["began"] and (not s["passed"] or s["late"] is None)
            for s in steps.values()
        )
        for number in sorted(steps):
            s = steps[number]
            sample = s["late"]
            if sample is None:
                if s["mismatch"] is not None:
                    case["findings"].append({
                        "step": number, "kind": "no-late-sample",
                        "reportedMismatch": True
                    })
                continue
            window = sample.get("observed")
            expected = sample.get("expectedHwnd")
            observed = window.get("hwnd") if isinstance(window, dict) else None
            if expected == observed:
                continue
            if is_hidden_explorer(window):
                category = "persistent-hidden-explorer"
                case["persistentHiddenExplorer"] += 1
            else:
                category = "unexpected-other-foreground"
                case["otherUnexpectedForeground"] += 1
            case["findings"].append({
                "step": number, "kind": category,
                "elapsedAfterAltReleaseMs": sample["actualDelayMs"],
                "expectedHwnd": expected,
                "observed": snapshot(window),
                "reportedMismatch": bool(s["mismatch"] is not None),
            })
        # Mark completely unobserved cells as NOT RUN, never "0 failures".
        case["status"] = (
            "not-run" if not case["topologyVerified"]
            else "completed" if case["cellComplete"] and
                case["attempted"] == case["passed"] and
                case["attempted"] == case["lateSamples"] and
                case["incompleteAttempts"] == 0
            else "incomplete"
        )

    return {
        "analysis": "descriptive-only; experimental results cannot prove causality",
        "runComplete": (
            run_finished and run_failed is None and
            all(cases[x]["status"] == "completed" for x, _, _ in FACTORS)
        ),
        "failureReason": run_failed,
        "cells": cases,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("trace", type=Path, help="private topology-trace.jsonl")
    args = parser.parse_args()
    print(json.dumps(analyze(read_jsonl(args.trace)), indent=2))


if __name__ == "__main__":
    main()
