"""Starting forms source contracts; gameplay requires the verified Windows game."""
from pathlib import Path
import unittest

SRC = Path(__file__).resolve().parents[2] / "src" / "Framework"

class StartingFormsTests(unittest.TestCase):
    def read(self, name):
        return (SRC / name).read_text()

    def test_store_is_character_scoped_and_atomic(self):
        store = self.read("StartingFormStore.cs")
        for token in ("Guid.TryParse", "SHA256.HashData", "account", "originalKey", "JsonSerializer", "File.Move", "MaxEntries"):
            self.assertIn(token, store)
        self.assertIn("starting-forms.json", self.read("HeroStartingForms.cs"))

    def test_defaults_use_actual_owned_character_and_registered_monster(self):
        s = self.read("HeroStartingForms.cs")
        for token in ("TryReadCharacterSheet", "TryValidateLocalHero", "get_PlayerControllable", "GetPieceDefinition", "ValidateFormFit",
                      "HeroForms.Queue", "startingKey:", "automatic: true", "isTransformedPiece"):
            self.assertIn(token, s)

    def test_run_guard_not_every_level(self):
        s = self.read("StartingFormStore.cs")
        for token in ("StartingFormRun", "FirstBoard", "attempted", "step", "run"):
            self.assertIn(token, s)
        worker = self.read("HeroStartingForms.cs")
        self.assertIn("currentScenarioRunId", worker)
        self.assertIn("currentScenarioStepIndex", worker)
        self.assertIn("run.MarkAttempt", worker)
        self.assertIn("run.CanApply", worker)

    def test_background_and_authoritative_cancellation(self):
        rules = self.read("HouseRules.cs")
        self.assertLess(rules.index("HeroStartingForms.Tick()"), rules.index("HeroFormPersistence.Tick()"))
        forms = self.read("HeroForms.cs")
        for token in ("HeroStartingForms.Attach", "HeroStartingForms.Stop", "HeroStartingForms.Authorizes"):
            self.assertIn(token, forms)
        self.assertIn("command.StartingKey", forms)

    def test_saved_default_wins_over_old_carry_and_clear_forgets(self):
        persist = self.read("HeroFormPersistence.cs")
        self.assertIn("HeroStartingForms.PreferStartingForm", persist)
        self.assertIn("ForgetStartingChoice", persist)
        self.assertIn("HeroFormPersistence.ForgetStartingChoice", self.read("HeroStartingForms.cs"))

    def test_menu_has_separate_saved_assignments_and_independent_switch(self):
        s = self.read("LevelEditor.cs")
        for token in ("Starting Forms", "Live forms", "Save starting form", "Use original next adventure", "HeroStartingForms.SetEnabled",
                      "HeroStartingForms.Assign", "HeroStartingForms.ClearChoice"):
            self.assertIn(token, s)
        self.assertIn('Bind("StartingForms", "Enabled", false', self.read("HeroStartingForms.cs"))

if __name__ == "__main__":
    unittest.main()
