"""Synthetic, CI-safe tests for the standalone shell-topology result analyzer."""
import importlib.util
import unittest
from pathlib import Path

mod = Path(__file__).with_name("Analyze-NativeAltTabTopologyMatrix.py")
spec = importlib.util.spec_from_file_location("matrix_analyzer", mod)
analyzer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(analyzer)

SHELL = {
    "hwnd": 131322, "exists": True, "windowClass": "XamlExplorerHostIslandWindow",
    "isShellExplorer": True, "visible": False, "owner": 0,
    "rootOwner": 131322, "lastActivePopup": 131322
}
APP = {
    "hwnd": 200, "exists": True, "windowClass": "WindowsForms10.Window",
    "isShellExplorer": False, "visible": True, "owner": 100,
    "rootOwner": 100, "lastActivePopup": 200
}


def event(name, stage, data=None):
    return {"scenario": name, "stage": stage, "data": data or {}}


def fixture():
    rows = [event("startup", "run-start", {"switchesPerCell": 2})]
    for name, registered, own in analyzer.FACTORS:
        rows.extend([
            event(name, "topology", {
                "name": name, "registered": registered, "nativeOwnership": own
            }),
            event(name, "before-chord", {"step": 1}),
            event(name, "post-chord-sample", {
                "step": 1, "requestedDelayMs": 120, "actualDelayMs": 122,
                "expectedHwnd": 200, "observed": APP
            }),
            event(name, "post-chord-sample", {
                "step": 1, "requestedDelayMs": 900, "actualDelayMs": 903,
                "expectedHwnd": 200, "observed": APP
            }),
            event(name, "switch-passed", {"step": 1}),
            event(name, "cell-complete", {"switches": 1}),
        ])
    rows.append(event("register-on_owner-on", "run-complete", {"cells": 4}))
    return rows


def late(records, name="register-on_owner-on"):
    return next(row for row in records if row["scenario"] == name
                and row["stage"] == "post-chord-sample"
                and row["data"]["requestedDelayMs"] == 900)


class TopologyAnalyzerTests(unittest.TestCase):
    def test_complete_four_cell_matrix(self):
        result = analyzer.analyze(fixture())
        self.assertTrue(result["runComplete"])
        self.assertEqual(len(result["cells"]), 4)
        for row in result["cells"].values():
            self.assertEqual(row["status"], "completed")
            self.assertEqual(row["attempted"], 1)
            self.assertEqual(row["passed"], 1)
            self.assertEqual(row["persistentHiddenExplorer"], 0)

    def test_persistent_hidden_shell_is_classified(self):
        rows = fixture()
        late(rows)["data"]["observed"] = dict(SHELL)
        result = analyzer.analyze(rows)
        cell = result["cells"]["register-on_owner-on"]
        self.assertEqual(cell["persistentHiddenExplorer"], 1)
        self.assertEqual(cell["findings"][0]["kind"], "persistent-hidden-explorer")

    def test_transient_shell_host_is_not_counted(self):
        rows = fixture()
        event120 = next(r for r in rows if r["scenario"] == "register-on_owner-on"
                        and r["stage"] == "post-chord-sample"
                        and r["data"]["requestedDelayMs"] == 120)
        event120["data"]["observed"] = dict(SHELL)
        result = analyzer.analyze(rows)
        self.assertEqual(result["cells"]["register-on_owner-on"]["persistentHiddenExplorer"], 0)

    def test_shell_class_from_wrong_pid_is_not_explorer(self):
        rows = fixture()
        late(rows)["data"]["observed"] = dict(SHELL, isShellExplorer=False)
        cell = analyzer.analyze(rows)["cells"]["register-on_owner-on"]
        self.assertEqual(cell["persistentHiddenExplorer"], 0)
        self.assertEqual(cell["otherUnexpectedForeground"], 1)

    def test_wrong_application_is_not_explorer(self):
        rows = fixture()
        late(rows)["data"]["observed"] = dict(APP, hwnd=300)
        cell = analyzer.analyze(rows)["cells"]["register-on_owner-on"]
        self.assertEqual(cell["otherUnexpectedForeground"], 1)
        self.assertEqual(cell["persistentHiddenExplorer"], 0)

    def test_missing_late_sample_is_incomplete_not_pass(self):
        rows = fixture()
        sample = late(rows)
        rows.remove(sample)
        result = analyzer.analyze(rows)
        self.assertFalse(result["runComplete"])
        cell = result["cells"]["register-on_owner-on"]
        self.assertEqual(cell["status"], "incomplete")
        self.assertEqual(cell["incompleteAttempts"], 1)

    def test_unexecuted_cells_are_not_run(self):
        rows = fixture()
        rows = [r for r in rows if r["scenario"] in ("startup", "register-off_owner-off")]
        result = analyzer.analyze(rows)
        self.assertFalse(result["runComplete"])
        self.assertEqual(result["cells"]["register-on_owner-off"]["status"], "not-run")

    def test_failure_marker_prevents_completed_result(self):
        rows = fixture()
        rows.append(event("register-on_owner-on", "run-failed", {"reason": "foreground moved"}))
        result = analyzer.analyze(rows)
        self.assertFalse(result["runComplete"])
        self.assertEqual(result["failureReason"], "foreground moved")

    def test_duplicate_sample_fails_schema(self):
        rows = fixture()
        rows.insert(-1, dict(late(rows)))
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            analyzer.analyze(rows)

    def test_factor_mismatch_fails_schema(self):
        rows = fixture()
        row = next(r for r in rows if r["stage"] == "topology")
        row["data"]["registered"] = True
        with self.assertRaisesRegex(ValueError, "factorial"):
            analyzer.analyze(rows)

    def test_missing_run_start_fails_schema(self):
        rows = fixture()[1:]
        with self.assertRaisesRegex(ValueError, "run-start"):
            analyzer.analyze(rows)


if __name__ == "__main__":
    unittest.main()
