import pathlib
import unittest

SRC = pathlib.Path(__file__).resolve().parents[2] / "src" / "Framework"

class EnemyMoveTests(unittest.TestCase):
    def test_two_stage_direct_map_tool(self):
        editor = (SRC / "LevelEditor.cs").read_text()
        for token in ('"Move on map"', 'BeginMapTool(4)', 'mapMode == 4', 'moveSource == null',
                      'ValidateMoveTarget', 'Confirm move', 'Choose different enemy', 'pendingMove = new MoveCommand'):
            self.assertIn(token, editor)
        self.assertIn('e.button == 2', editor)

    def test_move_revalidates_before_dispatch(self):
        editor = (SRC / "LevelEditor.cs").read_text()
        move = editor[editor.index('private void Move(MoveCommand'):editor.index('private static RemovalTarget ValidateMoveTarget')]
        for token in ('TryGetLevelEditorContext', 'SameSession', 'SamePiece', 'RequireMapIdle',
                      'ValidateMoveTarget', 'CanSpawnPieceOnPosition'):
            self.assertLess(move.index(token), move.index('EnemyMover.Queue'))
        self.assertNotIn('MakeRemovalRequest', move)

    def test_native_move_preserves_piece_and_checks_authoritative_board(self):
        mover = (SRC / "EnemyMover.cs").read_text()
        for token in ('ForceMovePiece', 'TeleportDirectly', 'DoNotCorrect', 'Item1', 'IProgressMarker',
                      'RequestHandlerInteract', 'RequestHandlerDebugCommand', 'ProcessRequest',
                      'TryGetPieceAtPoint', 'CanSpawnPieceOnPosition', 'gridPos', 'EnemyOfPlayers',
                      'CheckedRequest', 'get_HasRequestsInQueue', 'TryGetLevelEditorContext',
                      'command.Token', 'command.Expires', 'pending = null', 'DeveloperModeEnabled'):
            self.assertIn(token, mover)
        for forbidden in ('DespawnPiece(', 'RequestSpawnPiece', 'RequestTryDealDamageToPiece', 'SetValue(', 'Destroy('):
            self.assertNotIn(forbidden, mover)
        handler = mover[mover.index('private static bool HandleCommand'):]
        self.assertLess(handler.index('pending = null'), handler.index('forceMove.Invoke'))
        self.assertLess(handler.index('ValidateDestination'), handler.index('forceMove.Invoke'))

    def test_cancel_on_editor_and_module_lifecycle(self):
        editor = (SRC / "LevelEditor.cs").read_text()
        for start, end in [('private void Clear()', 'internal void Leave()'), ('private void EndMapTool()', 'private void PickMapTile()')]:
            section = editor[editor.index(start):editor.index(end)]
            for token in ('pendingMove = null', 'moveSource = null', 'EnemyMover.Cancel()'):
                self.assertIn(token, section)
        rules = (SRC / "HouseRules.cs").read_text()
        self.assertIn('EnemyMover.Attach(ctx)', rules)
        self.assertIn('EnemyMover.Stop()', rules)
        self.assertIn('EnemyMover.Cancel()', rules)

if __name__ == "__main__":
    unittest.main()
