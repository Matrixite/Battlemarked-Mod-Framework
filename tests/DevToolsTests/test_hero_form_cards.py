import pathlib
import unittest

SRC = pathlib.Path(__file__).resolve().parents[2] / "src" / "Framework"

class HeroFormCardTests(unittest.TestCase):
    def test_enemy_deck_is_preflighted_and_armed_only_by_confirmed_native_transform(self):
        forms = (SRC / "HeroForms.cs").read_text()
        self.assertIn("HeroFormCards.Attach(context)", forms)
        self.assertIn("HeroFormCards.Stop()", forms)
        handler = forms[forms.index("private static bool HandleCommand"):]
        self.assertIn("HeroFormCards.Arm", handler)
        self.assertLess(handler.index("ValidateFormFit"), handler.index("HeroFormCards.Arm"))
        self.assertLess(handler.index("HeroFormCards.Arm"), handler.index("transform.Invoke"))
        self.assertIn("HeroFormCards.CancelArmed()", handler)

    def test_form_deck_uses_registered_definition_cards_and_native_hand(self):
        path = SRC / "HeroFormCards.cs"
        self.assertTrue(path.exists(), "Missing enemy form deck implementation")
        source = path.read_text()
        for token in ('"get_Cards"', '"GetCardDefinition"', '"get_CardTypeId"', '"get_PlayerControllable"',
                      '"get_Hand"', '"capacityStat"', '"TryAdd"', '"GenerateNewCardInstanceId"',
                      '"SpawnPieceInternal"', 'postfix:', 'hand.TryAdd', 'inventorySetter.Invoke'):
            self.assertIn(token, source)
        for forbidden in ('set_Cards', 'set_inventoryTags', '.Clear()', 'Destroy(', 'SetValue('):
            self.assertNotIn(forbidden, source)
        self.assertLess(source.index("hand.TryAdd"), source.index("inventorySetter.Invoke"))

    def test_boss_fixed_ai_cards_are_included_without_browsing_unrelated_assets(self):
        path = SRC / "HeroFormCards.cs"
        self.assertTrue(path.exists())
        source = path.read_text()
        for token in ('SequentialAIBehaviourDefinition', 'TargetableActionUseSpecificCard',
                      'ActionTargetableWithFallback', 'get_PreTurn', 'get_Turns',
                      'get_NamedSequences', 'get_Enabled', 'MaxAiNodes',
                      'ReadSequentialCards', 'AddRegisteredCard'):
            self.assertIn(token, source)
        self.assertNotIn('FindObjectsOfTypeAll', source)

    def test_deck_scope_excludes_original_hero_and_unrelated_spawns(self):
        path = SRC / "HeroFormCards.cs"
        self.assertTrue(path.exists(), "Missing enemy form deck implementation")
        source = path.read_text()
        hook = source[source.index("private static void ApplySpawnDeck"):]
        for token in ('"pieceToReplace"', '"isTransformedPiece"', '"isPlayerControlled"', '"characterSheetId"',
                      '"ownerId"', '"team"', 'SameNative', 'OriginalInventory', 'OriginalPiece',
                      'Expires', 'armed = null', 'SameSession'):
            self.assertIn(token, hook)
        self.assertLess(hook.index("armed = null"), hook.index("inventorySetter.Invoke"))
        self.assertIn("Status", source)

if __name__ == "__main__":
    unittest.main()
