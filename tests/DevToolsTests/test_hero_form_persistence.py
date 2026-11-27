"""Source contracts for level-to-level native transformation restoration."""
import pathlib
import unittest

SRC = pathlib.Path(__file__).resolve().parents[2] / "src" / "Framework"

class FormPersistenceTests(unittest.TestCase):
    def source(self):
        path = SRC / "HeroFormPersistence.cs"
        self.assertTrue(path.exists(), "Missing hero form transition retention")
        return path.read_text()

    def test_records_only_confirmed_bmf_enemy_spawns(self):
        s = self.source()
        for token in ('"SpawnPieceInternal"', '"pieceToReplace"', '"isTransformedPiece"',
                      '"get_PlayerControllable"', 'SameNative', '"characterSheetId"', '"ownerId"',
                      'FormKey', 'OriginalKey', 'ReplacementId', 'armed = null'):
            self.assertIn(token, s)
        forms = (SRC / "HeroForms.cs").read_text()
        self.assertIn('HeroFormPersistence.Arm', forms)
        self.assertIn('HeroFormPersistence.Attach', forms)
        self.assertIn('HeroFormPersistence.Stop()', forms)

    def test_transition_snapshots_live_forms_before_native_revert(self):
        s = self.source()
        for token in ('<EndCurrentScenarioStepAndMoveToNext>d__16', '<CompleteScenario>d__17',
                      '"MoveNext"', 'BeforeTransition', '"transformedPieces"',
                      '"replacementPieceId"', '"IsDead"', '"scenarioSelection"',
                      '"sessionId"', '"campaignId"', '"hostPlayerId"'):
            self.assertIn(token, s)
        self.assertNotIn('Patch(revert', s)
        self.assertNotIn('SetValue(', s)

    def test_restore_waits_for_next_playable_board_and_uses_native_queue(self):
        s = self.source()
        for token in ('Jade.GameView.ViewEventScenarioStepLoadedHandler', 'OnStepLoaded', 'destination',
                      'TryGetHeroFormsContext', 'TryValidateLocalHero', 'HeroForms.Waiting',
                      'get_HasRequestsInQueue', 'HeroForms.ValidatePiece', 'HeroForms.ValidateFormFit',
                      'HeroForms.Queue', 'automatic: true', 'carry.Remove'):
            self.assertIn(token,s)
        rules = (SRC / "HouseRules.cs").read_text()
        self.assertIn('HeroFormPersistence.Tick()', rules)
        forms = (SRC / "HeroForms.cs").read_text()
        self.assertIn('pending.Automatic', forms)
        self.assertIn('HeroFormPersistence.Clear()', rules)

if __name__ == "__main__":
    unittest.main()
