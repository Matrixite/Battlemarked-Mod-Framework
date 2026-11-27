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

    def test_board_snapshot_and_single_request(self):
        editor = (SRC / "LevelEditor.cs").read_text()
        self.assertIn('WindowSize = 8', editor)
        self.assertIn('TileExist', editor)
        self.assertIn('TryGetPieceAtPoint', editor)
        self.assertIn('Confirm enemy spawn', editor)
        self.assertIn('pending = null;', editor)
        action = editor[editor.index('private void Spawn('):editor.index('internal void Draw(')]
        for check in ('TryGetLevelEditorContext', 'SameSession', 'CanSpawnPieceOnPosition', 'ValidateEnemy', 'get_HasRequestsInQueue', 'RequestSpawnPiece'):
            self.assertLess(action.index(check), action.index('send.Invoke'))
        self.assertEqual(action.count('send.Invoke'), 1)
        self.assertNotIn('SpawnPieces', action)
        self.assertNotIn('SetLevelEditorTile', editor)

if __name__ == "__main__":
    unittest.main()
