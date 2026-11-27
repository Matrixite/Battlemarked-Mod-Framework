"""Source-level build compatibility guard.

The user's real BepInEx interop set previously failed CS0656 on record
classes. The installed Windows libraries are not present in this runner.
Reject reintroducing that known problematic syntax in game projects.
"""
import pathlib
import re
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]

class InteropCompilerCompatibilityTests(unittest.TestCase):
    def test_game_projects_do_not_use_record_classes(self):
        # Existing record structs have built successfully; the reported
        # failure involved generated nullable annotations on class records.
        record_class = re.compile(
            r"(?m)^\s*(?:(?:public|private|protected|internal|sealed|abstract|partial|file|new)\s+)*"
            r"record\s+(?:class\s+)?(?!struct\b)\w+\s*(?:[<(:{;]|$)"
        )
        found = []
        for project in ("Framework", "Example"):
            for source in (ROOT / "src" / project).rglob("*.cs"):
                if any(part in ("obj", "bin") for part in source.parts):
                    continue
                for match in record_class.finditer(source.read_text(encoding="utf-8-sig")):
                    found.append(f"{source.relative_to(ROOT)}: {match.group().strip()}")
        self.assertEqual(found, [], "Record classes can trigger CS0656 in the game's interop reference set.")

if __name__ == "__main__":
    unittest.main()
