"""Check the installer preserves native compiler diagnostics before failure.

Windows PowerShell and game interop assemblies are unavailable in the Linux
test runner. These source checks enforce the logging and fail-before-copy
contract; a real Windows build remains the acceptance test.
"""
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]

class InstallerDiagnosticsTests(unittest.TestCase):
    def test_native_output_and_exit_code_are_preserved(self):
        script = (ROOT / "scripts" / "Build-Install.ps1").read_text()
        self.assertIn("build-details.log", script)
        self.assertIn("-RedirectStandardOutput", script)
        self.assertIn("-RedirectStandardError", script)
        start = script.index("-RedirectStandardOutput")
        report = script.index("Get-Content -LiteralPath $nativeLog", start)
        check = script.index("if ($build.ExitCode -ne 0)", report)
        copy = script.index("Copy-Item -LiteralPath $p.Output", check)
        self.assertLess(start, report)
        self.assertLess(report, check)
        self.assertLess(check, copy)
        self.assertIn("exit code", script[check:copy])
        self.assertIn("--disable-build-servers", script)
        self.assertIn("-p:UseSharedCompilation=false", script)

if __name__ == "__main__":
    unittest.main()
