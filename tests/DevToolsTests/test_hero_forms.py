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

    def test_independent_saved_switch_and_tab(self):
        menu = (SRC / "ModMenu.cs").read_text()
        policy = (SRC / "MenuModuleVisibility.cs").read_text()
        self.assertIn('"Level Editor", "Hero Forms"', menu)
        self.assertIn('if (title == "Hero Forms") return developerMode && heroFormsEnabled;', policy)
        self.assertIn('houseRulesEnabled, heroFormsEnabled.Value)', menu)
        self.assertIn('config.Bind("Menu", "HeroFormsEnabled"', menu)
        toggle = menu[menu.index('if (GUI.Button', menu.index('Label(125, "Hero forms')):menu.index('int rows', menu.index('Label(125, "Hero forms'))]
        self.assertIn('heroFormsEnabled.Value = !heroFormsEnabled.Value', toggle)
        self.assertIn('HeroForms.Cancel()', toggle)
        self.assertIn('config.Save()', toggle)

    def test_separate_page_and_map_toolbar_lifecycle(self):
        menu = (SRC / "ModMenu.cs").read_text()
        editor = (SRC / "LevelEditor.cs").read_text()
        self.assertIn('if (tab == 9)', menu)
        self.assertIn('levelEditor.DrawHeroForms(x, y, width, height)', menu)
        self.assertIn('(tab == 8 || tab == 9', menu)
        normal = editor[editor.index('internal void Draw(float'):editor.index('internal void DrawHeroForms')]
        self.assertNotIn('Transform hero', normal)
        self.assertNotIn('showForms ? "Enemy list"', normal)
        self.assertIn('Place on map', normal)
        self.assertIn('Remove on map', normal)
        forms = editor[editor.index('internal void DrawHeroForms'):]
        self.assertIn('BeginMapTool(3)', forms)
        self.assertIn('plugin.HeroFormsEnabled', forms)

    def test_disabled_cannot_dispatch_or_consume_native_form_requests(self):
        source = (SRC / "HeroForms.cs").read_text()
        plugin = (SRC / "Plugin.cs").read_text()
        self.assertIn('menu?.HeroFormsEnabled == true', plugin)
        self.assertIn('private static bool Active => CaptureAllowed && Plugin.Instance?.HeroFormsEnabled == true;', source)
        queue = source[source.index('internal static string Queue('):source.index('private static bool HandleCommand')]
        self.assertLess(queue.index('!Active'), queue.index('send.Invoke'))
        handler = source[source.index('private static bool HandleCommand'):]
        self.assertLess(handler.index('!Active'), handler.index('transform.Invoke'))
        # Read-only native captures remain available while disabled, so a
        # live re-enable can use the current session rather than force restart.
        capture = source[source.index('private static void CaptureHandler'):source.index('private static string Session')]
        self.assertEqual(capture.count('if (!CaptureAllowed) return;'), 2)
