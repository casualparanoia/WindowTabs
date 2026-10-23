#!/usr/bin/env python3
"""Offline, read-only correlation of WindowTabs and passive Explorer Alt+Tab JSONL.

Stdlib-only Python 3.8+. Outputs bounded metadata, never window titles or content.
Candidates are anomalous OS foreground states, NOT claims about user intent.
"""
import argparse
import bisect
import collections
import datetime as dt
import json
from pathlib import Path


def parse_utc(text):
    return dt.datetime.fromisoformat(text.replace('Z', '+00:00')).timestamp()


def load_jsonl(path, expected_field):
    records = []
    with Path(path).open(encoding='utf-8-sig') as handle:
        for line_number, line in enumerate(handle, 1):
            if not line.strip():
                continue
            row = json.loads(line)
            if 'utc' not in row or expected_field not in row:
                raise ValueError('%s:%d: invalid recorder schema' % (path, line_number))
            row['_time'] = parse_utc(row['utc'])
            records.append(row)
    if not records:
        raise ValueError('%s: trace has no records' % path)
    return sorted(records, key=lambda r: r['_time'])


def is_hidden_explorer(row):
    fg = row.get('foreground') or {}
    return isinstance(fg, dict) and (
        str(fg.get('process', '')).lower() == 'explorer'
        and fg.get('windowClass') == 'XamlExplorerHostIslandWindow'
        and fg.get('visible') is False
        and bool(fg.get('hwnd'))
    )


def metadata(row):
    window = row.get('foreground') or {}
    return {key: window.get(key) for key in (
        'hwnd', 'process', 'windowClass', 'owner', 'rootOwner',
        'lastActivePopup', 'visible')}


def correlation(passive, internal=None, minimum_seconds=0.65):
    presses = [r for r in passive if r['type'] == 'alt-tab']
    releases = [r for r in passive if r['type'] == 'alt-release']
    samples = [r for r in passive if r['type'] == 'post-alt-tab-sample']
    transitions = [r for r in passive if r['type'] == 'foreground']
    press_times = [r['_time'] for r in presses]
    samples_times = [r['_time'] for r in samples]
    internal = internal or []
    internal_releases = [r for r in internal if r['stage'] == 'alt-released']
    internal_release_times = [r['_time'] for r in internal_releases]
    internal_late = [r for r in internal if r['stage'] == 'sample-800ms']
    internal_late_times = [r['_time'] for r in internal_late]
    internal_actions = [r for r in internal if r['stage'] in (
        'group-reassert-foreground', 'group-raise-owned-window',
        'group-stale-foreground-ignored', 'taskbar-proxy-activation')]
    internal_action_times = [r['_time'] for r in internal_actions]
    if internal:
        overlap = min(passive[-1]['_time'], internal[-1]['_time']) - max(
            passive[0]['_time'], internal[0]['_time'])
        if overlap <= 0:
            raise ValueError('These recordings do not overlap; cannot correlate them.')
    else:
        overlap = None

    candidates = []
    last_release_time = float('-inf')
    for release in releases:
        time = release['_time']
        pos = bisect.bisect_right(press_times, time)
        if pos == 0:
            continue
        start = presses[pos - 1]
        # A real Alt release closes the held-Alt cycle. Multiple Tab presses
        # during one hold must not count as independent completed switches.
        if start['_time'] <= last_release_time:
            continue
        last_release_time = time
        next_press_time = press_times[pos] if pos < len(press_times) else float('inf')
        # Require a sampled, *invisible* shell host after Alt was released,
        # before any subsequent chord. An old +900ms sample during an ongoing
        # Alt hold or after a new chord is not a failure signal.
        left = bisect.bisect_left(samples_times, time + minimum_seconds)
        right = bisect.bisect_left(samples_times, min(next_press_time, time + 1.8))
        # The pump schedules samples per pressed Tab, not per released Alt.
        # A delayed sample from an earlier chord must not be attributed to
        # the current completed switch even if its timestamp falls in range.
        chord_id = start.get('chordId')
        persistent = [r for r in samples[left:right]
                      if chord_id is not None and r.get('chordId') == chord_id
                      and is_hidden_explorer(r)]
        if not persistent:
            continue
        first = persistent[0]
        hwnd = first['foreground']['hwnd']
        match = None
        if internal:
            ipos = bisect.bisect_left(internal_release_times, time - 0.15)
            nearby = [r for r in internal_releases[ipos:ipos+4]
                      if abs(r['_time']-time) <= 0.15]
            if nearby:
                matched_release = min(nearby, key=lambda r: abs(r['_time'] - time))
                j = bisect.bisect_left(internal_late_times, time + minimum_seconds)
                for s in internal_late[j:]:
                    if s['_time'] >= min(next_press_time, time + 1.8):
                        break
                    if (s['attempt'] == matched_release['attempt']
                            and s.get('foreground') == hwnd):
                        match = s
                        break
        fg_transitions = [r for r in transitions if start['_time'] <= r['_time'] <= first['_time']]
        # Latest app activation is a *candidate* destination; selected tile is unknown.
        app_events = [r for r in fg_transitions
                      if isinstance(r.get('foreground'), dict)
                      and str(r['foreground'].get('process', '')).lower() != 'explorer']
        target = metadata(app_events[-1]) if app_events else None
        ai = bisect.bisect_left(internal_action_times, start['_time'] - 0.05)
        aj = bisect.bisect_right(internal_action_times, first['_time'])
        action_counts = dict(collections.Counter(r['stage'] for r in internal_actions[ai:aj]))
        marker_rows = [r for r in passive if r['type'] == 'manual-marker'
                       and time < r['_time'] <= time + 5]
        marker_rows += [r for r in internal if r['stage'] == 'manual-failure'
                        and time < r['_time'] <= time + 5]
        candidates.append({
            'alt_release_utc': release['utc'],
            'last_press_utc': start['utc'],
            'source': metadata(start),
            'candidate_destination': target,
            'persisting_explorer': metadata(first),
            'external_observation_utc': first['utc'],
            'external_ms_after_release': round((first['_time'] - time) * 1000),
            'internal_confirmed': match is not None,
            'internal_observation_utc': match['utc'] if match else None,
            'no_new_chord_for_ms': round((next_press_time - time) * 1000)
                                   if next_press_time != float('inf') else None,
            'manual_failure_marked': bool(marker_rows),
            'windowtabs_actions_before_observation': action_counts,
        })
    return {
        'external': {'start_utc': passive[0]['utc'], 'end_utc': passive[-1]['utc'],
                     'alt_tab_presses': len(presses), 'alt_releases': len(releases)},
        'internal': None if not internal else {
            'start_utc': internal[0]['utc'], 'end_utc': internal[-1]['utc'],
            'overlap_seconds': round(overlap, 1)},
        'criteria': ('Invisible explorer XamlExplorerHostIslandWindow remains foreground '
                     'at least %.0f ms after Alt release, sampled before the next Alt+Tab. '
                     'Internal confirmation requires a matching +800ms sample '
                     'for the same attempt and HWND. No user intent inferred.' % (minimum_seconds * 1000)),
        'candidates': candidates,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--passive', type=Path, required=True, help='foreground-trace.jsonl')
    parser.add_argument('--internal', type=Path, help='focus-trace.jsonl (optional)')
    args = parser.parse_args()
    passive = load_jsonl(args.passive, 'type')
    internal = load_jsonl(args.internal, 'stage') if args.internal else None
    print(json.dumps(correlation(passive, internal), indent=2, ensure_ascii=False))


if __name__ == '__main__':
    main()
