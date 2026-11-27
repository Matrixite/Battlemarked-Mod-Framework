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
                      "TryGetHeroFormsContext", "SameSession", "sessionId", "hostPlayerId", "characterSheetId",
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
        self.assertIn('if (title == "Hero Forms") return heroFormsEnabled;', policy)
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
        self.assertIn('(tab == 8 && DeveloperModeEnabled || tab == 9 && HeroFormsEnabled)', menu)
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

    def test_hero_forms_tick_and_draw_ignore_developer_mode(self):
        menu = (SRC / "ModMenu.cs").read_text()
        self.assertIn('if (tab == 9) levelEditor.Tick(HeroFormsEnabled && open, true);', menu)
        self.assertIn('if (HeroFormsEnabled) levelEditor.DrawHeroForms(x, y, width, height);', menu)
        source = (SRC / "HeroForms.cs").read_text()
        capture = source[source.index('private static bool CaptureAllowed'):source.index('private static HarmonyMethod Hook')]
        self.assertNotIn('DeveloperModeEnabled', capture)

    def test_form_requests_and_picker_have_separate_feature_guards(self):
        rules = (SRC / "HouseRules.cs").read_text()
        editor = (SRC / "LevelEditor.cs").read_text()
        self.assertIn('TryGetEditorContext(false, out context, out reason)', rules)
        self.assertIn('TryGetEditorContext(true, out context, out reason)', rules)
        self.assertIn('TryGetBoardPicker(false, context, out handler, out reason)', rules)
        self.assertIn('TryGetBoardPicker(true, context, out handler, out reason)', rules)
        self.assertIn('heroForms ? Plugin.Instance?.HeroFormsEnabled == true : Plugin.Instance?.DeveloperModeEnabled == true', rules)
        form = editor[editor.index('private void TransformHero'):editor.index('private static RemovalTarget ValidateRemovalTarget')]
        self.assertIn('TryGetHeroFormsContext', form)
        self.assertIn('TryGetHeroFormsPicker', form)
        self.assertNotIn('TryGetLevelEditorContext', form)
        spawn = editor[editor.index('private void Spawn('):editor.index('private static void RequireMapIdle')]
        remove = editor[editor.index('private void Remove('):editor.index('internal bool DrawMapTool()')]
        for action in (spawn, remove):
            self.assertIn('TryGetLevelEditorContext', action)
            self.assertNotIn('TryGetHeroFormsContext', action)

    def test_developer_mode_off_keeps_shared_picker_and_hero_requests_live(self):
        rules = (SRC / "HouseRules.cs").read_text()
        capture = rules[rules.index('private static void CaptureDebugBoardPicker'):rules.index('internal static bool TryGetLevelEditorPicker')]
        self.assertIn('Plugin.Instance?.DeveloperModeEnabled != true && Plugin.Instance?.HeroFormsEnabled != true', capture)
        update = rules[rules.index('public override void OnUpdate()'):rules.index('private void Write()')]
        self.assertIn('if (Plugin.Instance?.HeroFormsEnabled != true) HeroForms.Cancel();', update)
        self.assertIn('if (Plugin.Instance?.DeveloperModeEnabled != true && Plugin.Instance?.HeroFormsEnabled != true) debugBoardHandler = null;', update)

    def test_catalog_includes_large_boss_footprints(self):
        editor = (SRC / "LevelEditor.cs").read_text()
        forms = editor[editor.index('private static bool ValidateFormDefinition'):editor.index('internal static MethodInfo FindCast')]
        self.assertIn('HeroForms.FormFootprint', forms)
        self.assertNotIn('!= 1', forms)
        scan = editor[editor.index('private void ScanEnemies'):editor.index('private static int CompareEnemies')]
        self.assertIn('formSize', scan)
        self.assertIn('formSize + "]"', scan)
        # The ordinary enemy placement list must retain its existing limit.
        enemy = editor[editor.index('private static bool ValidateEnemy'):editor.index('private static bool ValidateFormDefinition')]
        self.assertIn('(int)Get(footprint, "x") != 1', enemy)

    def test_large_form_fit_checked_at_picker_queue_and_native_handler(self):
        editor = (SRC / "LevelEditor.cs").read_text()
        source = (SRC / "HeroForms.cs").read_text()
        picker = editor[editor.index('private void PickMapTile'):editor.index('private static RemovalTarget ValidateHeroTarget')]
        self.assertIn('HeroForms.ValidateFormFit', picker)
        queue = source[source.index('internal static string Queue('):source.index('private static bool HandleCommand')]
        self.assertLess(queue.index('ValidateFormFit'), queue.index('send.Invoke'))
        handler = source[source.index('private static bool HandleCommand'):]
        self.assertLess(handler.index('ValidateFormFit'), handler.index('transform.Invoke'))
        for token in ('PieceSizeToTileSize', 'get_Center', '"orientation"', 'CanSpawnPieceInArea', 'TileRect', '"pieceId"'):
            self.assertIn(token, source)

    def test_fit_uses_native_size_and_clearly_rejects_blocked_footprint(self):
        source = (SRC / "HeroForms.cs").read_text()
        self.assertIn('internal static object FormFootprint', source)
        self.assertIn('internal static void ValidateFormFit', source)
        self.assertIn('No room for', source)
        self.assertIn('move your hero to an open area', source)
