"""Regression checks for repeated native Hero Forms transformations."""

import pathlib
import unittest

SRC = pathlib.Path(__file__).resolve().parents[2] / "src" / "Framework"


class HeroFormSwitchingTests(unittest.TestCase):
    def test_transformed_party_creatures_including_large_ones_are_eligible(self):
        source = (SRC / "HeroForms.cs").read_text()
        validate = source[source.index("internal static void ValidatePiece"):source.index("internal static object FormFootprint")]
        self.assertNotIn("|| transformed ||", validate)
        self.assertIn("!transformed &&", validate)
        self.assertIn("width is < 1 or > 4", validate)
        for guard in ('"isPlayerControlled"', '"isCreatureNonNpc"', '"hostPlayerId"', '"GetTurnOrderForPiece"', '"IsDead"'):
            self.assertIn(guard, validate)
        editor = (SRC / "LevelEditor.cs").read_text()
        self.assertIn("hero or transformed form", editor)

    def test_existing_native_original_must_be_verified_before_switch(self):
        source = (SRC / "HeroForms.cs").read_text()
        handler = source[source.index("private static bool HandleCommand"):]
        self.assertIn("HeroFormPersistence.RequireOriginalHero(state, piece)", handler)
        self.assertLess(handler.index("RequireOriginalHero"), handler.index("HeroFormCards.Arm"))
        self.assertLess(handler.index("RequireOriginalHero"), handler.index("transform.Invoke"))
        persistence = (SRC / "HeroFormPersistence.cs").read_text()
        resolve = persistence[persistence.index("internal static object RequireOriginalHero"):persistence.index("private static bool HasNativeOriginal")]
        for guard in ('"transformedPieces"', '"replacementPieceId"', '"piece"', 'match != null', '"isTransformedPiece"', '"isPlayerControlled"', '"ownerId"', 'Sheet(piece)', 'Sheet(original)'):
            self.assertIn(guard, resolve)
        self.assertNotIn("SetValue(", persistence)

    def test_latest_form_tracks_current_source_but_original_hero_for_carry_over(self):
        persistence = (SRC / "HeroFormPersistence.cs").read_text()
        arm = persistence[persistence.index("internal static void Arm("):persistence.index("private static bool MatchesSession")]
        self.assertIn("SourceId = PieceId(current)", arm)
        self.assertIn("OriginalId = PieceId(original)", arm)
        self.assertIn('OriginalKey = Key(Get(original, "typeId"))', arm)
        track = persistence[persistence.index("private static void TrackSpawn"):persistence.index("private static object FindPiece")]
        self.assertIn("!= form.SourceId", track)
        self.assertIn("active.RemoveAt(i)", track)
        self.assertIn("active.Add(form)", track)
        self.assertIn("GetNativeOriginal(state, form.ReplacementId)", persistence)

    def test_switch_deck_cannot_overwrite_original_hero_inventory(self):
        cards = (SRC / "HeroFormCards.cs").read_text()
        handler = (SRC / "HeroForms.cs").read_text()
        self.assertIn("HeroFormCards.Arm(live, state, helper, piece, original, registered)", handler)
        self.assertIn('RootInventory = Native(Get(root, "inventory"), inventoryType)', cards)
        spawn = cards[cards.index("private static void ApplySpawnDeck"):]
        self.assertIn("SameNative(current, ticket.RootInventory)", spawn)
        self.assertLess(spawn.index("ticket.RootInventory"), spawn.index("inventorySetter.Invoke"))


if __name__ == "__main__":
    unittest.main()
