import pathlib
import re
import unittest

SRC = pathlib.Path(__file__).resolve().parents[2] / "src" / "Framework"

class HouseRulesTabTests(unittest.TestCase):
    def test_single_top_level_house_rules_tab_preserves_other_tab_ids(self):
        menu = (SRC / "ModMenu.cs").read_text()
        definition = re.search(r'string\[\] Tabs = \{ (.*?) \};', menu).group(1)
        labels = [x.strip() for x in definition.split(",")]
        self.assertEqual(labels, ['"Overview"', '"House Rules"', 'null', '"Presets"', '"Mods"', '"Reports"', '"Updates"', '"Dev Tools"', '"Level Editor"', '"Hero Forms"', '"Recurring Cards"'])
        policy = (SRC / "MenuModuleVisibility.cs").read_text()
        self.assertIn('if (title == null) return false;', policy)
        self.assertIn('if (title == "House Rules" || title == "Presets") return houseRulesEnabled;', policy)

    def test_both_pages_still_use_existing_rule_controls(self):
        menu = (SRC / "ModMenu.cs").read_text()
        start = menu.index('if (tab == 1)')
        end = menu.index('if (tab == 3)', start)
        rules = menu[start:end]
        self.assertIn('healthRulesPage = false', rules)
        self.assertIn('healthRulesPage = true', rules)
        self.assertNotIn('if (tab == 2)', rules)
        for action in ('HouseRules.ChangeBonus(-1)', 'HouseRules.ChangeBonus(1)',
                       'HouseRules.ChangeBonus(-3)', 'HouseRules.ToggleAdjustExisting()',
                       'HouseRules.ToggleRefundMode()', 'HouseRules.ChangeHealthPercent(-25)',
                       'HouseRules.ChangeHealthPercent(25)', 'HouseRules.ChangeHealthPercent(-100)',
                       'HouseRules.ToggleNativeHealthArm()'):
            self.assertEqual(rules.count(action), 1, action)
        # At the minimum supported height, both rule pages must fit below the
        # second-level navigation. These are explicit fixed-position controls.
        tops = [int(x) for x in re.findall(r'\b(?:Label|Button|Small)\((\d+),', rules)]
        self.assertLessEqual(max(tops) + 28, 410)
