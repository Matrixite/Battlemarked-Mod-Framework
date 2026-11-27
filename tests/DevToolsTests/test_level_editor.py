import pathlib
import unittest

SRC = pathlib.Path(__file__).resolve().parents[2] / "src" / "Framework"

class LevelEditorTests(unittest.TestCase):
    def test_dev_only_tab_and_update(self):
        self.assertIn('if (title == "Level Editor") return developerMode;', (SRC / "MenuModuleVisibility.cs").read_text())
        menu = (SRC / "ModMenu.cs").read_text()
        self.assertIn('if (tab == 8)', menu)
        self.assertIn('levelEditor.Tick(developerMode.Value && enabled.Value && !broken && open && tab == 8)', menu)

    def test_local_scope_guard(self):
        rules = (SRC / "HouseRules.cs").read_text()
        guard = rules[rules.index('internal static bool TryGetLevelEditorContext'):rules.index('internal static bool TryGetDebugRoomForPieceId')]
        for check in ('VerifiedGame', 'DeveloperModeEnabled', 'mainThread', 'RetainCapture', 'modelFromHud', 'TryGetLocalRoom', 'SessionRejection', 'HouseRules.model', 'get_IsLoaded'):
            self.assertIn(check, guard)
        self.assertNotIn('r.model,', guard)

    def test_current_hud_registry_does_not_require_spawn_constructor_capture(self):
        rules = (SRC / "HouseRules.cs").read_text()
        guard = rules[rules.index('internal static bool TryGetLevelEditorContext'):rules.index('internal static bool TryGetDebugRoomForPieceId')]
        editor = (SRC / "LevelEditor.cs").read_text()
        # Native HeroSelectModel has a pieceRegistry field. A missing spawner
        # capture must not prevent the already-verified current HUD registry from being used.
        self.assertIn('ReadMember(capturedModel, "pieceRegistry")', guard)
        scan = editor[editor.index('private void Scan(bool full)'):editor.index('private static bool ValidateEnemy')]
        self.assertNotIn('if (live.Spawner == null)', scan)
        self.assertIn('ResolveConfig(live)', scan)
        self.assertIn('Jade.GameTags.AffectedPiecesConfig', editor)
        self.assertIn('Ambiguous affected-pieces config', editor)

    def test_ai_definition_is_native_object_not_unity_asset(self):
        editor = (SRC / "LevelEditor.cs").read_text()
        # Verified IL2CPP metadata: PieceAIBehaviourDefinition derives
        # System.Object, not UnityEngine.Object. The old check rejected every AI.
        validate = editor[editor.index('private static bool ValidateEnemy'):editor.index('private void ScanEnemies')]
        self.assertNotIn('is not UObject ai', validate)
        self.assertIn('Jade.GameDefinitions.PieceAIBehaviourDefinition', validate)
        self.assertIn('ai.Pointer == IntPtr.Zero', validate)
        self.assertIn('GetPieceDefinition', validate)
        self.assertIn('EnemyOfPlayers', validate)

    def test_map_pick_uses_game_camera_and_native_board_hit_test(self):
        editor = (SRC / "LevelEditor.cs").read_text()
        menu = (SRC / "ModMenu.cs").read_text()
        rules = (SRC / "HouseRules.cs").read_text()
        self.assertIn('DrawMapTool()', menu)
        self.assertIn('Place on map', editor)
        self.assertIn('Remove on map', editor)
        self.assertIn('e.button == 2', editor)  # middle click, not the normal piece grab
        self.assertIn('e.Use()', editor)
        self.assertIn('CaptureDebugBoardPicker', rules)
        for check in ('TryGetLevelEditorPicker', 'get_ActiveCamera', 'ScreenPointToRay', 'TryGetHoveredTile', 'get_IsValid', 'tilePoint'):
            self.assertIn(check, editor)

    def test_removal_is_confirmed_bounded_and_checks_piece_identity(self):
        editor = (SRC / "LevelEditor.cs").read_text()
        self.assertIn('Confirm removal', editor)
        self.assertIn('pendingRemoval = null;', editor)
        action = editor[editor.index('private void Remove('):editor.index('internal bool DrawMapTool()')]
        for check in ('TryGetLevelEditorContext', 'SameSession', 'SamePiece', 'ValidateRemovalTarget',
                      'RequestTryDealDamageToPiece', 'get_HasRequestsInQueue'):
            self.assertLess(action.index(check), action.index('send.Invoke'))
        self.assertEqual(action.count('send.Invoke'), 1)
        self.assertNotIn('Destroy(', editor)
        self.assertNotIn('DespawnPiece(', editor)
        for flag in ('isPlayerControlled', 'isDownable', 'isPOI', 'usesHealth', 'isCreatureNonNpc', 'isAttackableProp'):
            self.assertIn('"' + flag + '"', editor)

    def test_board_snapshot_and_single_request(self):
        editor = (SRC / "LevelEditor.cs").read_text()
        self.assertIn('WindowSize = 8', editor)
        self.assertIn('TileExist', editor)
        self.assertIn('TryGetPieceAtPoint', editor)
        self.assertIn('Confirm enemy spawn', editor)
        self.assertIn('pending = null;', editor)
        action = editor[editor.index('private void Spawn('):editor.index('private void Remove(') if 'private void Remove(' in editor else editor.index('internal void Draw(')]
        for check in ('TryGetLevelEditorContext', 'SameSession', 'CanSpawnPieceOnPosition', 'ValidateEnemy', 'get_HasRequestsInQueue', 'RequestSpawnPiece'):
            self.assertLess(action.index(check), action.index('send.Invoke'))
        self.assertEqual(action.count('send.Invoke'), 1)
        self.assertNotIn('SpawnPieces', action)
        self.assertNotIn('SetLevelEditorTile', editor)

if __name__ == "__main__":
    unittest.main()
