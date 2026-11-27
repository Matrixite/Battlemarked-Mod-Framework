import pathlib
import unittest

SRC = pathlib.Path(__file__).resolve().parents[2] / "src" / "Framework"

class HeroFormHandRefreshTests(unittest.TestCase):
    def test_refresh_only_queued_after_successful_deck_publication(self):
        source = (SRC / "HeroFormCards.cs").read_text()
        self.assertIn("HeroFormHandRefresh.Attach(context)", source)
        self.assertIn("HeroFormHandRefresh.Stop()", source)
        hook = source[source.index("private static void ApplySpawnDeck"):]
        self.assertLess(hook.index("inventorySetter.Invoke"), hook.index("HeroFormHandRefresh.Queue"))
        self.assertIn("instanceIds", hook[hook.index("HeroFormHandRefresh.Queue"):])

    def test_native_presenters_refresh_from_the_live_board_after_request(self):
        source = (SRC / "HeroFormHandRefresh.cs").read_text()
        for token in ("CardHandPresenterNonVR", "CardHandPresenterVR", '"Tick"', "postfix:",
                      '"RefreshCardHandAfterLoad"', '"TryGetCurrentPiece"', '"TryGetCard"',
                      '"get_HasRequestsInQueue"', '"get_IsProcessingRequests"', '"get_LocalPlayerID"',
                      '"gameState"', '"pieceId"', '"ownerId"', '"isTransformedPiece"', '"isPlayerControlled"',
                      '"characterSheetId"', '"typeId"', "SameSession", "SameNative",
                      "Expires", "pending.Remove"):
            self.assertIn(token, source)
        tick = source[source.index("private static void AfterTick"):]
        self.assertLess(tick.index("TryGetHeroFormsContext"), tick.index("refresh.Invoke"))
        self.assertLess(tick.index("tryGetCard.Invoke"), tick.index("refresh.Invoke"))
        self.assertLess(tick.rindex("pending.Remove", 0, tick.index("refresh.Invoke")), tick.index("refresh.Invoke"))

    def test_multiple_forms_refresh_only_their_own_displayed_hand(self):
        source = (SRC / "HeroFormHandRefresh.cs").read_text()
        self.assertIn('Dictionary<int, RefreshTicket>', source)
        self.assertIn('"TryGetPiece"', source)
        self.assertIn('"ShowForPiece"', source)
        self.assertIn('"showingForPiece"', source)
        self.assertIn('SourceId', source)
        tick = source[source.index("private static void AfterTick"):]
        self.assertIn('if (isCurrent)', tick)
        self.assertIn('else if (isShown)', tick)
        self.assertIn('pending.Remove(ticket.PieceId)', tick)
        self.assertLess(tick.index('tryGetCard.Invoke'), tick.index('show.Invoke'))

    def test_refresh_is_presentation_only(self):
        source = (SRC / "HeroFormHandRefresh.cs").read_text()
        for token in ('"MovePiece"', "inventorySetter", "GenerateNewCardInstanceId", '"TryAdd"',
                      'SendRequest', 'SpawnPiece', 'SetValue('):
            self.assertNotIn(token, source)

if __name__ == "__main__":
    unittest.main()
