import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("release_gate", ROOT / "scripts" / "release_gate.py")
gate = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gate)


class ReleaseGateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def snapshot(self, version, channel="stable", example=None, schema=1):
        files = {
            "bmf-update.json": json.dumps({"schema": schema, "version": version, "channel": channel}),
            "Directory.Build.props": f"<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>",
            "src/Framework/Api.cs": f'public const string Version = "{version}";',
            "src/Example/ExamplePlugin.cs": f'[BepInPlugin("org.battlemarked.example", "BMF Example", "{example or version}")]',
        }
        for name, data in files.items():
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(data, encoding="utf-8")

    def test_current_alpha_does_not_release(self):
        self.snapshot("0.6.13", "alpha")
        self.assertFalse(gate.eligible(self.root))

    def test_later_version_does_not_create_first_release(self):
        self.snapshot("1.0.1")
        self.assertFalse(gate.eligible(self.root))

    def test_matching_stable_version_eligible(self):
        self.snapshot("1.0.0")
        self.assertTrue(gate.eligible(self.root))

    def test_alpha_1_0_0_rejected(self):
        self.snapshot("1.0.0", "alpha")
        with self.assertRaises(ValueError):
            gate.eligible(self.root)

    def test_example_version_mismatch_rejected(self):
        self.snapshot("1.0.0", example="0.6.13")
        with self.assertRaises(ValueError):
            gate.eligible(self.root)

    def test_framework_version_mismatch_rejected(self):
        self.snapshot("1.0.0")
        (self.root / "src/Framework/Api.cs").write_text('public const string Version = "0.6.13";')
        with self.assertRaises(ValueError):
            gate.eligible(self.root)

    def test_props_version_mismatch_rejected(self):
        self.snapshot("1.0.0")
        (self.root / "Directory.Build.props").write_text("<Project><Version>0.6.13</Version></Project>")
        with self.assertRaises(ValueError):
            gate.eligible(self.root)

    def test_manifest_schema_rejected(self):
        self.snapshot("1.0.0", schema=2)
        with self.assertRaises(ValueError):
            gate.eligible(self.root)


if __name__ == "__main__":
    unittest.main()
