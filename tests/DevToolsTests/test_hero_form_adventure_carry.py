"""Cross-adventure carry-over contracts (native runtime unavailable here)."""
import pathlib
import unittest

SRC = pathlib.Path(__file__).resolve().parents[2] / "src" / "Framework"


class AdventureCarryTests(unittest.TestCase):
    def source(self):
        return (SRC / "HeroFormPersistence.cs").read_text()

    def test_carry_over_not_discarded_when_new_adventure_starts(self):
        s = self.source()
        tick = s[s.index("internal static void Tick()"):]
        self.assertNotIn("!MatchesSession(carry[0], live.State)", tick)
        self.assertIn("IsNewPlayableBoard(form, live, stage)", tick)
        self.assertIn("MatchesCharacter(form, live.State, piece)", tick)
        self.assertIn("currentScenarioRunId", s)
        self.assertIn("form.Session !=", s)
        loaded = s[s.index("private static void OnStepLoaded"):s.index("internal static bool MatchesStage")]
        self.assertNotIn("stage.Campaign != source.Campaign", loaded)

    def test_same_room_stage_switch_waits_for_new_loaded_board_evidence(self):
        s = self.source()
        gate = s[s.index("private static bool IsNewPlayableBoard"):s.index("private static bool AlreadyAttempted")]
        self.assertIn("loadSequence > form.SourceLoadSequence", gate)
        self.assertIn("live.Board.Pointer != form.ViewBoard", gate)
        self.assertIn("stage.SameEvent(destination)", gate)
        self.assertIn("SourceLoadSequence = loadSequence", s)
        self.assertIn("loadSequence++", s)

    def test_manual_exit_and_native_completion_capture_living_forms(self):
        s = self.source()
        for token in ('"RequestHandlerLeaveGame"', '"Jade.Networking.LocalNetworkRoom"', 'BeforeLeaveGame', 'BeforeLocalLeave',
                      'SnapshotForms(', 'MoveToCarry(', 'HasNativeOriginal', 'VerifiedLive'):
            self.assertIn(token, s)
        self.assertIn("if (!Active", s[s.index("private static void BeforeLeaveGame"):])
        self.assertIn("form.State = form.Board = form.Spawner = null", s)

    def test_cross_session_requires_actual_owned_sheet_not_old_player_number(self):
        s = self.source()
        for token in ('"TryGetPlayerForPlayerId"', '"HasCharacterSheet"', '"accountId"', '"pieceTypeId"',
                      '"characterSheetId"', '"hostPlayerId"', 'Guid.TryParse', 'SheetVerified'):
            self.assertIn(token, s)
        find = s[s.index("private static object FindHero"):s.index("internal static void Tick")]
        self.assertIn("owner != host", find)
        self.assertNotIn("owner != form.Owner", find)
        self.assertIn("TryValidateLocalHero", s)
        self.assertIn("HeroForms.ValidateFormFit", s)

    def test_choice_survives_rejected_request_without_repeating_on_same_board(self):
        s = self.source()
        tick = s[s.index("internal static void Tick()"):]
        self.assertIn("AlreadyAttempted(form, live, stage)", tick)
        self.assertIn("form.AttemptSession = ", tick)
        self.assertNotIn("carry.Remove(form)", tick)
        track = s[s.index("private static void TrackSpawn"):s.index("private static bool TryReadHolder")]
        self.assertIn("carry.RemoveAt(i)", track)

    def test_room_exit_uses_captured_local_room_even_after_hud_teardown(self):
        s = self.source()
        leave = s[s.index("private static void BeforeLocalLeave"):s.index("private static void OnStepLoaded")]
        self.assertIn("SameNative(__instance, form.Room)", leave)
        self.assertNotIn("TryGetHeroFormsContext", leave)
        self.assertIn("Room = live.Room as Il2CppObjectBase", s)
        self.assertIn("form.Room = null", s)

    def test_live_revert_or_death_does_not_become_new_adventure_choice(self):
        s = self.source()
        self.assertIn("ObserveActiveForms(live, stage)", s)
        self.assertIn("ObserveNativeReversion", s)
        self.assertIn("IsLiveForm(form", s)
        self.assertIn("active.RemoveAt(i)", s)
        self.assertIn("form.VerifiedLive", s)
        self.assertIn("HeroFormPersistence.Clear()", (SRC / "HouseRules.cs").read_text())


if __name__ == "__main__":
    unittest.main()
