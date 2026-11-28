"""Main-menu starting forms contracts; native interop requires Windows."""
from pathlib import Path
import unittest

SRC = Path(__file__).resolve().parents[2] / "src" / "Framework"

class MainMenuStartingFormsTests(unittest.TestCase):
    def read(self, file):
        p = SRC / file
        return p.read_text() if p.exists() else ""

    def test_native_main_menu_lifecycle_captures_and_releases_context(self):
        source = self.read("StartingFormMenu.cs")
        for item in ('"Jade.Application.AppStateMainMenu"', '"Enter"', '"Exit"', 'get_MainMenuModel',
                     'CaptureMenu', 'ReleaseMenu', 'Plugin.Instance?.VerifiedGame'):
            self.assertIn(item, source)
        starting = self.read("HeroStartingForms.cs")
        self.assertIn("StartingFormMenu.Attach", starting)
        self.assertIn("StartingFormMenu.Stop", starting)

    def test_reads_local_saved_heroes_before_a_board_exists(self):
        source = self.read("StartingFormMenu.cs")
        for item in ("get_CharacterSheetsStorage", "GetAllCharacterSheets", "DoesCharacterSheetBelongingToLocalPlayer",
                     "StartingFormStore.CharacterKey", "isRetired", "get_AccountId", "get_PieceRegistry"):
            self.assertIn(item, source)
        self.assertNotIn("CreateCharacterSheet", source)
        self.assertNotIn("UpdateCharacterSheet", source)

    def test_main_menu_catalog_verifies_exact_registry_without_board(self):
        source = self.read("StartingFormMenu.cs")
        for item in ("pieceDefinitions", "GetPieceDefinition", "ValidateMenuStartingForm", "FormId", "FormGuid"):
            self.assertIn(item, source)
        self.assertIn("ValidateMenuStartingForm", self.read("LevelEditor.cs"))
        self.assertNotIn("HeroForms.Queue", source)
        self.assertNotIn("TryGetHeroFormsContext", source)

    def test_preferences_save_from_fresh_menu_lookup_without_live_board(self):
        starting = self.read("HeroStartingForms.cs")
        self.assertIn("AssignFromMenu", starting)
        assign = starting[starting.index("internal static void AssignFromMenu"):starting.index("internal static void ClearChoice")]
        for item in ("StartingFormMenu.TryReadAssignment", "store.Set"):
            self.assertIn(item, assign)
        self.assertNotIn("TryGetHeroFormsContext", assign)
        self.assertNotIn("HeroForms.Queue", assign)
        self.assertNotIn("run.Defer", assign)
        self.assertIn("StartingFormMenu.Heroes", starting)

    def test_picker_uses_menu_catalog_even_without_snapshot(self):
        source = self.read("LevelEditor.cs")
        self.assertIn("StartingFormMenu.Refresh", source)
        draw = source[source.index("private void DrawStartingForms"):]
        for item in ("StartingFormMenu.IsOpen", "StartingFormMenu.Monsters", "HeroStartingForms.AssignFromMenu",
                     "Save starting form", "Use original next adventure"):
            self.assertIn(item, draw)

if __name__ == "__main__":
    unittest.main()
