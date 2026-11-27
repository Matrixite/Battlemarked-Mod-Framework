import pathlib
import unittest

SRC = pathlib.Path(__file__).resolve().parents[2] / "src" / "Framework"

class DebugWorkspaceTests(unittest.TestCase):
    def test_framework_owned_workspace_is_default(self):
        menu = (SRC / "ModMenu.cs").read_text()
        workspace = (SRC / "DebugWorkspace.cs").read_text()
        self.assertIn("debugWorkspace.Draw", menu)
        self.assertIn("debugWorkspace.Tick(developerMode.Value && enabled.Value && !broken", menu)
        for page in ("Scene", "Board", "Pieces", "Cards", "Encounters", "Events", "Native"):
            self.assertIn('"' + page + '"', workspace)
        self.assertIn('Resources.FindObjectsOfTypeAll', workspace)
        self.assertIn('GUI.TextField', workspace)
        self.assertIn('ClearSession()', workspace)
        self.assertIn('CoreLogicProbe.Summary()', workspace)
        self.assertIn('GameplayProbe.Summary()', workspace)

    def test_card_request_is_queued_explicitly_and_rechecked(self):
        workspace = (SRC / "DebugWorkspace.cs").read_text()
        self.assertIn('Confirm grant', workspace)
        tick = workspace[workspace.index('internal void Tick('):workspace.index('private void Scan(')]
        self.assertIn('if (!enabled)', tick)
        self.assertIn('pending = null;', tick)
        self.assertIn('GrantCard(', tick)
        action = workspace[workspace.index('private void GrantCard('):]
        self.assertLess(action.index('plugin.VerifiedGame'), action.index('HouseRules.TryGetDebugRoomForPieceId'))
        self.assertLess(action.index('HouseRules.TryGetDebugRoomForPieceId'), action.index('send.Invoke'))
        self.assertLess(action.index('get_HasRequestsInQueue'), action.index('send.Invoke'))
        self.assertIn('Jade.Messaging.RequestGiveCard', action)

    def test_action_requires_hud_backed_current_local_room(self):
        rules = (SRC / "HouseRules.cs").read_text()
        guard = rules[rules.index('internal static bool TryGetDebugRoomForPieceId'):rules.index('private int Allowance')]
        for check in ('mainThread', 'RetainCapture', 'modelFromHud', 'BoardHelpers', 'GetPiece', 'Scope(piece)', 'TryGetLocalRoom'):
            self.assertIn(check, guard)
        self.assertIn('local solo hero', guard)
        self.assertIn('debugCardRegistries.TryGetValue(state.Pointer', guard)
        self.assertIn('GetCardDefinition', guard)
        self.assertIn('registered != definition', guard)
        self.assertIn('DeveloperModeEnabled != true', guard)

    def test_selection_identity_and_no_automatic_retry(self):
        workspace = (SRC / "DebugWorkspace.cs").read_text()
        self.assertIn('PieceKey(id) != command.Target.Key', workspace)
        self.assertIn('CardKey(cardId) != command.Card.Key', workspace)
        self.assertIn('command.Scene == scene.handle', workspace)
        self.assertIn('!scanPage.HasValue', workspace)
        clear = workspace[workspace.index('private void ClearSession()'):workspace.index('internal void Tick(')]
        self.assertIn('pending = null;', clear)
        self.assertIn('selected = target = card = null;', clear)

if __name__ == "__main__":
    unittest.main()
