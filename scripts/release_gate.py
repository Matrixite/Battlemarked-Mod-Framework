"""Decide whether the checked-out source is eligible for the first stable release."""

import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

TARGET_VERSION = "1.0.0"


def eligible(root: Path) -> bool:
    manifest = json.loads((root / "bmf-update.json").read_text(encoding="utf-8"))
    if not isinstance(manifest, dict) or type(manifest.get("schema")) is not int or manifest["schema"] != 1:
        raise ValueError("Expected updater manifest schema 1.")
    version = manifest.get("version")
    if not isinstance(version, str) or not re.fullmatch(r"(0|[1-9][0-9]{0,4})(\.(0|[1-9][0-9]{0,4})){2}", version):
        raise ValueError("Invalid updater version.")
    if version != TARGET_VERSION:
        return False
    if manifest.get("channel") != "stable":
        raise ValueError("v1.0.0 must explicitly use the stable channel before release.")
    props = ET.parse(root / "Directory.Build.props").getroot().findall(".//Version")
    api = (root / "src/Framework/Api.cs").read_text(encoding="utf-8")
    example = (root / "src/Example/ExamplePlugin.cs").read_text(encoding="utf-8")
    framework_versions = re.findall(r'\bpublic\s+const\s+string\s+Version\s*=\s*"([^"]+)"\s*;', api)
    example_versions = re.findall(r'\[BepInPlugin\(\s*"org\.battlemarked\.example"\s*,\s*"[^"]+"\s*,\s*"([^"]+)"\s*\)\]', example)
    if [p.text.strip() if p.text else "" for p in props] != [version]:
        raise ValueError("Directory.Build.props version does not match manifest.")
    if framework_versions != [version]:
        raise ValueError("Framework API version does not match manifest.")
    if example_versions != [version]:
        raise ValueError("Example plugin version does not match manifest.")
    return True


if __name__ == "__main__":
    root = Path(__file__).resolve().parents[1]
    try:
        print("eligible=" + ("true" if eligible(root) else "false"))
    except (OSError, ValueError, ET.ParseError) as error:
        print(f"Release gate failed: {error}", file=sys.stderr)
        sys.exit(1)
