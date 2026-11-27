import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
SRC = ROOT / "src" / "Framework"

class DevToolsSourceTests(unittest.TestCase):
    def test_dev_tools_visibility_and_stable_tab_ids(self):
        policy = (SRC / "MenuModuleVisibility.cs").read_text()
        menu = (SRC / "ModMenu.cs").read_text()
        self.assertRegex(policy, r'if \(title == "Reports" \|\| title == "Dev Tools"\) return developerMode;')
        self.assertIn('"Mods", "Reports", "Updates", "Dev Tools"', menu)
        self.assertIn('if (tab == 7)', menu)
        self.assertIn('if (!visibleTabs.Contains(tab)) tab = 4', menu)

    def test_native_debug_menu_is_opt_in_and_closed_when_developer_mode_disabled(self):
        menu = (SRC / "ModMenu.cs").read_text()
        self.assertIn('nativeDebugMenu.SetVisible(true)', menu)
        self.assertIn('nativeDebugMenu.SetVisible(false)', menu)
        self.assertIn('if (!developerMode.Value) nativeDebugMenu.CloseOwned()', menu)
        self.assertIn('if (developerMode.Value &&', menu)
        bridge = (SRC / "NativeDebugMenu.cs").read_text()
        self.assertIn('plugin.VerifiedGame', bridge)
        self.assertIn('"ShowDebugMenu"', bridge)
        self.assertIn('"HideDebugMenu"', bridge)
        self.assertIn('activeInHierarchy', bridge)
        self.assertNotIn('AddComponent', bridge)
        self.assertNotIn('Instantiate(', bridge)

if __name__ == "__main__":
    unittest.main()
