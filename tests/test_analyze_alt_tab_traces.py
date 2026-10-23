"""Synthetic tests for the passive Alt+Tab JSONL correlation classifier."""
import importlib.util
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('analyzer', Path(__file__).with_name('Analyze-AltTabTraces.py'))
analyzer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(analyzer)


def record(time, kind, window=None, internal=False, **fields):
    row = {
        'utc': '2026-10-23T10:00:%06.3fZ' % time,
        '_time': time,
        'stage' if internal else 'type': kind,
    }
    if internal:
        row.update(attempt=1, hwnd=0, foreground=window if window is not None else 0)
    else:
        row['foreground'] = window or {'hwnd': 14, 'process': 'vivaldi', 'visible': True}
    row.update(fields)
    return row


EXPLORER = {'hwnd': 131322, 'process': 'explorer',
            'windowClass': 'XamlExplorerHostIslandWindow', 'visible': False}
APP = {'hwnd': 1234, 'process': 'dopus', 'windowClass': 'dopus.lister',
       'rootOwner': 6666, 'owner': 7777, 'visible': True}


class CorrelationTests(unittest.TestCase):
    def setUp(self):
        self.passive = [
            record(0.00, 'recorder-start'),
            record(1.00, 'alt-tab', chordId=100),
            record(1.11, 'alt-release', EXPLORER),
            record(1.15, 'foreground', APP),
            record(1.16, 'foreground', EXPLORER),
            record(2.02, 'post-alt-tab-sample', EXPLORER, chordId=100),
            record(2.70, 'alt-tab', EXPLORER, chordId=101),
            record(2.80, 'recorder-stop'),
        ]
        self.internal = [
            record(0.05, 'recorder-start', 14, internal=True),
            record(1.01, 'alt-tab-pressed', 14, internal=True),
            record(1.115, 'alt-released', 131322, internal=True),
            record(1.95, 'sample-800ms', 131322, internal=True),
            record(2.75, 'recorder-stop', 14, internal=True),
        ]

    def test_persistent_explorer_is_correlated(self):
        result = analyzer.correlation(self.passive, self.internal)
        self.assertEqual(len(result['candidates']), 1)
        item = result['candidates'][0]
        self.assertTrue(item['internal_confirmed'])
        self.assertEqual(item['candidate_destination']['process'], 'dopus')
        self.assertFalse(item['manual_failure_marked'])

    def test_sample_while_alt_still_held_does_not_count(self):
        self.passive[2]['_time'] = 2.20
        self.passive[2]['utc'] = '2026-10-23T10:00:02.200Z'
        self.passive.sort(key=lambda r: r['_time'])
        self.assertEqual(analyzer.correlation(self.passive)['candidates'], [])

    def test_new_chord_invalidates_old_late_sample(self):
        self.passive.append(record(1.60, 'alt-tab', EXPLORER))
        self.passive.sort(key=lambda r: r['_time'])
        self.assertEqual(analyzer.correlation(self.passive)['candidates'], [])

    def test_stale_sample_from_other_chord_does_not_count(self):
        # Late samples exist concurrently; timestamp proximity alone
        # must not attribute an older chord's observations to this release.
        self.passive[5]['chordId'] = 99
        self.assertEqual(analyzer.correlation(self.passive)['candidates'], [])

    def test_missing_chord_id_does_not_fabricate_anomaly(self):
        del self.passive[5]['chordId']
        self.assertEqual(analyzer.correlation(self.passive)['candidates'], [])

    def test_visible_shell_is_not_persistent_hidden_host(self):
        self.passive[5]['foreground'] = dict(EXPLORER, visible=True)
        self.assertEqual(analyzer.correlation(self.passive)['candidates'], [])

    def test_internal_without_matching_sample_is_unconfirmed(self):
        self.internal[3]['foreground'] = 6666
        result = analyzer.correlation(self.passive, self.internal)
        self.assertEqual(len(result['candidates']), 1)
        self.assertFalse(result['candidates'][0]['internal_confirmed'])

    def test_non_overlapping_recordings_rejected(self):
        for row in self.internal:
            row['_time'] += 200
        with self.assertRaisesRegex(ValueError, 'do not overlap'):
            analyzer.correlation(self.passive, self.internal)

    def test_preview_owner_handoff_stages_are_attributed(self):
        self.internal.extend([
            record(1.30, 'taskbar-dwm-preview-request', 7777, internal=True),
            record(1.31, 'taskbar-preview-release', 6666, internal=True),
            record(1.33, 'taskbar-preview-owner-after', 0, internal=True),
            record(1.60, 'taskbar-preview-rechain-before', 6666, internal=True),
            record(1.70, 'taskbar-preview-rechain-after', 6666, internal=True),
            record(1.75, 'taskbar-active-after', 7777, internal=True),
        ])
        self.internal.sort(key=lambda r: r['_time'])
        result = analyzer.correlation(self.passive, self.internal)
        self.assertEqual(len(result['candidates']), 1)
        actions = result['candidates'][0]['windowtabs_actions_before_observation']
        self.assertEqual(actions['taskbar-preview-release'], 1)
        self.assertEqual(actions['taskbar-preview-owner-after'], 1)
        self.assertEqual(actions['taskbar-preview-rechain-after'], 1)
        self.assertEqual(actions['taskbar-active-after'], 1)

    def test_handoff_after_foreground_observation_is_not_attributed(self):
        self.internal.append(record(2.30, 'taskbar-preview-release', 6666, internal=True))
        self.internal.sort(key=lambda r: r['_time'])
        result = analyzer.correlation(self.passive, self.internal)
        self.assertNotIn('taskbar-preview-release',
                         result['candidates'][0]['windowtabs_actions_before_observation'])


if __name__ == '__main__':
    unittest.main()
