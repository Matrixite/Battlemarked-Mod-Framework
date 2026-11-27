"""Source contracts for verified game hooks; Windows/native play is separately tested."""
import pathlib
import unittest

SRC = pathlib.Path(__file__).resolve().parents[2] / "src" / "Framework"


class HandSizeTests(unittest.TestCase):
    def test_independent_opt_in_switch_and_saved_limit(self):
        menu = (SRC / "ModMenu.cs").read_text()
        policy = (SRC / "MenuModuleVisibility.cs").read_text()
        self.assertIn('config.Bind("Menu", "HandSizeEnabled", false', menu)
        self.assertIn('config.Bind("Menu", "MaxHandSize", 12', menu)
        self.assertIn('HandSizePolicy.Clamp', menu)
        self.assertIn('if (title == "Hand Size") return handSizeEnabled;', policy)
        self.assertIn('HandSize.Status', menu)
        self.assertIn('HandSizeEnabled', (SRC / "Plugin.cs").read_text())

    def test_actual_native_capacity_and_full_paths_are_patched(self):
        p = SRC / "HandSize.cs"
        self.assertTrue(p.exists(), "Missing Hand Size game hooks")
        s = p.read_text()
        for token in ('Model("CardInventory")', '"get_Capacity"', '"get_Full"',
                      'typeof(int)', 'typeof(bool)', 'ref int __result', 'ref bool __result',
                      'HandSizePolicy.IsFull', '"get_Count"', 'get_Source'):
            self.assertIn(token, s)
        self.assertNotIn('SetCapacityStat', s)
        self.assertNotIn('SetMaxValue', s)
        self.assertNotIn('TryRemove', s)
        self.assertNotIn('TryAdd(', s)

    def test_only_verified_local_party_hands_in_matching_live_and_authoritative_states(self):
        p = SRC / "HandSize.cs"
        self.assertTrue(p.exists(), "Missing Hand Size game hooks")
        s = p.read_text()
        for token in ('TryGetHandSizeContext', 'TryValidateHandSizeHero', 'VerifiedGame',
                      'Environment.CurrentManagedThreadId', '"RequestHandlerSpawnPiece"',
                      '"pieceSpawner"', '"gameState"', '"sessionId"', '"hostPlayerId"',
                      'MatchesSession', '"get_Hand"', '"Players"', '"isPlayerControlled"',
                      '"turnQueue"', 'SameNative', '"Hand"', 'get_IsLoaded'):
            self.assertIn(token, s)
        rules = (SRC / "HouseRules.cs").read_text()
        self.assertIn('HandSize.Attach(ctx)', rules)
        self.assertIn('HandSize.Stop()', rules)
        self.assertIn('TryGetHandSizeContext', rules)
        self.assertIn('TryValidateHandSizeHero', rules)


if __name__ == "__main__":
    unittest.main()
