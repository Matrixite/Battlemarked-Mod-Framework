import pathlib
import unittest

SRC = pathlib.Path(__file__).resolve().parents[2] / "src" / "Framework"

class RecurringCardsTests(unittest.TestCase):
    def test_independent_live_switch_and_tab(self):
        menu = (SRC / "ModMenu.cs").read_text()
        visibility = (SRC / "MenuModuleVisibility.cs").read_text()
        self.assertIn('"Recurring Cards"', menu)
        self.assertIn('config.Bind("Menu", "RecurringCardsEnabled"', menu)
        self.assertIn('if (title == "Recurring Cards") return recurringCardsEnabled;', visibility)
        self.assertIn('RecurringCards.Tick', menu)
        self.assertIn('RecurringCards.ClearChoices()', menu)
        self.assertIn('RecurringCards.Draw', menu)

    def test_selection_is_specific_to_a_hero_and_card_instance(self):
        path = SRC / "RecurringCards.cs"
        self.assertTrue(path.exists(), "Missing recurring card implementation")
        source = path.read_text()
        for token in ('readonly struct CardKey : IEquatable<CardKey>', 'PieceId', 'Owner', 'Sheet', 'InstanceId', 'TypeKey',
                      'HashSet<CardKey>', 'TryGetRecurringCardsContext', 'SameSession', 'TryValidateRecurringHero',
                      '"get_Hand"', '"get_Backpack"', '"TryGetCard"', 'selected.Contains(key)', '"get_IsConsumable"'):
            self.assertIn(token, source)
        self.assertIn('selected.Remove', source)
        self.assertIn('selected.Add', source)

    def test_card_identity_avoids_known_interop_compiler_risks(self):
        source = (SRC / "RecurringCards.cs").read_text()
        self.assertNotIn('record struct CardKey', source)
        self.assertIn('bool Equals(CardKey other)', source)
        for field in ('PieceId', 'Owner', 'Sheet', 'InstanceId', 'TypeKey'):
            self.assertIn('other.' + field, source)
        self.assertIn('HashCode.Combine(PieceId, Owner, Sheet, InstanceId, TypeKey)', source)
        self.assertNotIn('RemoveWhere(', source)
        self.assertNotIn('FindIndex(', source)
        self.assertNotIn('.Where(', source)
        self.assertNotIn('yield return', source)

    def test_native_cooldown_is_scoped_to_one_card_coroutine(self):
        path = SRC / "RecurringCards.cs"
        self.assertTrue(path.exists(), "Missing recurring card execution hook")
        source = path.read_text()
        for token in ('"RequestHandlerPlayCard"', '"HandleRequest"', '"UpdateCardInstance"', '"MoveNext"',
                      '"SetCooldownOnUse"', '"cardUsageType"', 'UObject.Instantiate', 'UObject.Destroy',
                      'GetCardDefinition', 'SameNative', 'finalizer:', 'RestoreDefinition'):
            self.assertIn(token, source)
        self.assertIn('Set(usageMember, clone,', source)
        self.assertNotIn('Set(usageMember, original,', source)
        self.assertNotIn('GenerateNewCardInstanceId', source)
        self.assertNotIn('TryAddCard', source)

if __name__ == "__main__":
    unittest.main()
