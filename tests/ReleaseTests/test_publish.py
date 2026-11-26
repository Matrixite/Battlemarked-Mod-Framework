"""Exercise the publication script against a local Git remote and fake gh CLI."""

import hashlib
import json
import os
import shutil
import subprocess
import tempfile
import unittest
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / "source"
        self.remote = self.root / "remote.git"
        self.output = self.root / "out"
        self.output.mkdir()
        self.source.mkdir()
        self.run_git("init", str(self.source), cwd=self.root)
        self.run_git("init", "--bare", str(self.remote), cwd=self.root)
        self.run_git("remote", "add", "origin", str(self.remote))
        for rel in ("scripts/release_gate.py", "scripts/publish-v1-release.sh"):
            dst = self.source / rel
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / rel, dst)
        self.write("Install-BMF.cmd", "source installer")
        self.write("Directory.Build.props", "<Project><Version>1.0.0</Version></Project>")
        self.write("src/Framework/Api.cs", 'public const string Version = "1.0.0";')
        self.write("src/Example/ExamplePlugin.cs", '[BepInPlugin("org.battlemarked.example", "BMF Example", "1.0.0")]')
        self.write("bmf-update.json", json.dumps({"schema": 1, "version": "1.0.0", "channel": "stable"}))
        self.commit()
        self.sha = self.run_git("rev-parse", "HEAD")
        self.run_git("push", "origin", "HEAD:refs/heads/main")
        self.bin = self.root / "bin"
        self.bin.mkdir()
        fake = self.bin / "gh"
        fake.write_text("""#!/usr/bin/env bash
set -euo pipefail
case "$1 $2" in
 'release view')
   [[ -f "$FAKE_OUTPUT/released" ]] || exit 1
   echo false
   ;;
 'api --method')
   sha=
   ref=
   for arg in "$@"; do
     case "$arg" in sha=*) sha="${arg#sha=}";; ref=*) ref="${arg#ref=}";; esac
   done
   git --git-dir="$FAKE_REMOTE" update-ref "$ref" "$sha" 0000000000000000000000000000000000000000
   ;;
 'release create')
   [[ "$3" == v1.0.0 ]]
   [[ " $* " == *" --verify-tag "* ]]
   cp "$4" "$5" "$FAKE_OUTPUT/"
   echo published > "$FAKE_OUTPUT/released"
   ;;
 *) exit 99;;
esac
""")
        fake.chmod(0o755)

    def write(self, path, contents):
        dst = self.source / path
        dst.parent.mkdir(parents=True, exist_ok=True)
        dst.write_text(contents, encoding="utf-8")

    def run_git(self, *args, cwd=None):
        result = subprocess.run(["git", *args], cwd=cwd or self.source, check=True, capture_output=True, text=True)
        return result.stdout.strip()

    def commit(self):
        self.run_git("add", ".")
        self.run_git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "fixture")

    def publish(self, sha=None, ref="refs/heads/main"):
        return subprocess.run(["bash", "scripts/publish-v1-release.sh"], cwd=self.source, text=True, capture_output=True,
            env=dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ["PATH"],
                GITHUB_REPOSITORY="Matrixite/Battlemarked-Mod-Framework", GITHUB_REF=ref,
                GITHUB_SHA=sha or self.sha, FAKE_REMOTE=str(self.remote), FAKE_OUTPUT=str(self.output)))

    def test_exact_commit_package_and_skip_repeat(self):
        self.write("untracked.txt", "must not be included")
        result = self.publish()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.run_git("--git-dir=" + str(self.remote), "rev-parse", "refs/tags/v1.0.0"), self.sha)
        archive = self.output / "Battlemarked-Mod-Framework-v1.0.0.zip"
        checksum = self.output / (archive.name + ".sha256")
        self.assertEqual(checksum.read_text().split()[0], hashlib.sha256(archive.read_bytes()).hexdigest())
        with zipfile.ZipFile(archive) as bundle:
            self.assertIn("Battlemarked-Mod-Framework-v1.0.0/Install-BMF.cmd", bundle.namelist())
            self.assertNotIn("Battlemarked-Mod-Framework-v1.0.0/untracked.txt", bundle.namelist())
        repeat = self.publish()
        self.assertEqual(repeat.returncode, 0, repeat.stderr)
        self.assertIn("already exists", repeat.stdout)

    def test_conflicting_tag_refused(self):
        old = self.sha
        self.write("README.md", "changed")
        self.commit()
        new = self.run_git("rev-parse", "HEAD")
        self.run_git("push", "origin", "HEAD:refs/heads/main")
        self.run_git("--git-dir=" + str(self.remote), "update-ref", "refs/tags/v1.0.0", old)
        result = self.publish(new)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("refusing to change", result.stderr)
        self.assertEqual(self.run_git("--git-dir=" + str(self.remote), "rev-parse", "refs/tags/v1.0.0"), old)
        self.assertFalse((self.output / "released").exists())

    def test_wrong_branch_refused(self):
        result = self.publish(ref="refs/heads/test")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.output / "released").exists())

    def test_wrong_version_refused(self):
        self.write("bmf-update.json", json.dumps({"schema": 1, "version": "0.6.13", "channel": "alpha"}))
        self.commit()
        result = self.publish(self.run_git("rev-parse", "HEAD"))
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.output / "released").exists())


if __name__ == "__main__":
    unittest.main()
