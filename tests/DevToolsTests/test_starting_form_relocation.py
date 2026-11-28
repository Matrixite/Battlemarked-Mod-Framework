"""Starting-form relocation contracts; runtime requires the verified game."""
from pathlib import Path
import unittest
SRC = Path(__file__).resolve().parents[2] / "src" / "Framework"

class StartingFormRelocationTests(unittest.TestCase):
    def source(self, name):
        path = SRC / name
        return path.read_text() if path.exists() else ""

    def test_only_a_real_blocked_starting_form_requests_relocation(self):
        worker = self.source("HeroStartingForms.cs")
        self.assertIn("catch (HeroFormSpaceException", worker)
        self.assertIn("run.CanRelocate", worker)
        self.assertIn("StartingFormRelocation.TryFind", worker)
        self.assertIn("EnemyMover.QueueStarting", worker)
        self.assertIn("run.MarkRelocation", worker)
        self.assertNotIn("QueueStarting", self.source("HeroFormPersistence.cs"))

    def test_search_is_bounded_and_uses_native_terrain_and_full_footprint(self):
        native = self.source("StartingFormRelocation.cs")
        for token in ("WallInTheWayOfMove", "CanSpawnPieceOnPosition", "CanSpawnPieceInArea",
                      "TryGetPieceAtPoint", "TileExist", "tileIsTemporarilyBlocked", "HeroForms.FormFootprint",
                      "StartingFormPlacement.TryFind"):
            self.assertIn(token, native)
        policy = self.source("StartingFormPlacement.cs")
        for token in ("MaxDistance", "Queue<Node>", "CanStep", "FitsForm", "long"):
            self.assertIn(token, policy)

    def test_move_rechecks_starting_preference_and_native_destination(self):
        mover = self.source("EnemyMover.cs")
        self.assertIn("QueueStarting", mover)
        self.assertIn("StartingReady", mover)
        self.assertIn("HeroStartingForms.Authorizes", mover)
        self.assertIn("HeroFormPersistence.MatchesStage", mover)
        self.assertIn("StartingFormRelocation.TryFind", mover)
        self.assertIn("HeroForms.ValidatePiece", mover)
        self.assertIn("TryGetHeroFormsContext", mover)
        self.assertIn("CancelStarting", mover)

    def test_transform_waits_for_exact_hero_arrival(self):
        worker = self.source("HeroStartingForms.cs")
        for token in ("Relocation", "ObserveRelocation", "DestX", "DestY", "SameSession",
                      "EnemyMover.Waiting", "relocation.Expires", "run.Epoch"):
            self.assertIn(token, worker)
        self.assertIn("relocated.Clear()", self.source("StartingFormStore.cs"))
        self.assertIn("EnemyMover.Waiting", self.source("HeroForms.cs"))
        self.assertIn("EnemyMover.CancelStarting()", worker)

if __name__ == "__main__":
    unittest.main()
