import pathlib
import unittest
SRC = pathlib.Path(__file__).resolve().parents[2] / "src" / "Framework"

class HeroFormTests(unittest.TestCase):
    def test_catalog_and_confirmed_map_selection(self):
        editor = (SRC / "LevelEditor.cs").read_text()
        for token in ("Hero forms", "ValidateFormDefinition", "Confirm hero form", "ValidateHeroTarget", "pendingForm", "mapMode == 3"):
            self.assertIn(token, editor)
        self.assertIn("HeroForms.Queue", editor)
        self.assertIn("SameSession", editor)
    def test_authoritative_native_transform_only(self):
        source = (SRC / "HeroForms.cs").read_text()
        for token in ("RequestHandlerDebugCommandFactory", "SpawnHeroPieceForPlayers", "SwapPieceHelper", "TransformIntoNewPiece",
                      "TryGetLevelEditorContext", "SameSession", "sessionId", "hostPlayerId", "characterSheetId",
                      "isPlayerControlled", "IsCurrentPiece", "GetPieceDefinition", "GetPiece", "Guid.NewGuid", "pending = null"):
            self.assertIn(token, source)
        self.assertNotIn("SetValue(", source)
        self.assertNotIn("Destroy(", source)
        self.assertIn("isTransformationReversible", source)
        self.assertIn("ref Il2CppSystem.Collections.IEnumerator __result", source)
    def test_optional_hooks_and_cleanup(self):
        rules = (SRC / "HouseRules.cs").read_text()
        editor = (SRC / "LevelEditor.cs").read_text()
        self.assertIn("HeroForms.Attach", rules)
        self.assertIn("HeroForms.Stop", rules)
        self.assertIn("HeroForms.Cancel", editor)
        self.assertIn("TryValidateLocalHero", rules)
