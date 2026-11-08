# SPDX-License-Identifier: MPL-2.0
# This Source Code Form is subject to the terms of the Mozilla Public License,
# v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
# one at https://mozilla.org/MPL/2.0/.
"""Verify scope/notice files and optional published dependency coverage."""
import argparse
import importlib.util
import json
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--deps", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    manifest = json.loads((root / "licenses/manifest.json").read_text(encoding="utf-8"))
    covered = (root / manifest["originalSourceList"]).read_text(encoding="utf-8").splitlines()
    assert len(set(covered)) == len(covered), "Duplicate original-source entries"
    for name in covered:
        file = (root / name).resolve()
        assert file.is_relative_to(root), "Source path leaves workspace"
        assert not name.startswith(("MeloongCore/", "modern/Presentation/NativeWpf/", "presentation/Controls/")), "Third-party/inherited source in MPL scope"
        assert "SPDX-License-Identifier: MPL-2.0" in file.read_text(encoding="utf-8-sig")[:700], "Missing MPL notice: " + name
    assert "Mozilla Public License Version 2.0" in (root / manifest["originalLicenseText"]).read_text(encoding="utf-8"), "Missing MPL text"
    for component in manifest["components"]:
        assert component["source"], "Missing component source"
        for name in component["texts"]:
            file = (root / name).resolve()
            assert file.is_relative_to(root) and file.is_file() and file.stat().st_size > 50, "Missing license: " + name
    spec = importlib.util.spec_from_file_location("license_notices_builder", root / "scripts/Build-LicenseNotices.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    index, offline = module.render(root)
    assert (root / "THIRD-PARTY-NOTICES.md").read_text(encoding="utf-8") == index, "Regenerate third-party index"
    assert (root / "licenses/THIRD-PARTY-NOTICES.txt").read_text(encoding="utf-8") == offline, "Regenerate offline notices"
    assert 'Licenses/ThirdParty.txt' in (root / "modern/Main/PCL-Living-Fork.Modern.csproj").read_text(encoding="utf-8-sig"), "Offline notices are not embedded"
    if args.deps:
        deps = json.loads(args.deps.read_text(encoding="utf-8-sig"))
        names = {item["id"] for item in manifest["components"]}
        names.update(name for item in manifest["components"] for name in item.get("runtimeNames", []))
        own = {"LivingPCL", "PCLCS", "PCLCS.Reference", "PCLLF.Presentation", "PCLLF.Presentation.Reference"}
        missing = {name.rsplit("/", 1)[0] for name in deps["libraries"]} - names - own
        assert not missing, "Runtime dependencies absent from notices: " + ", ".join(sorted(missing))
    print(f"PASS licenses: {len(covered)} MPL source files, {len(manifest['components'])} components, embedded offline texts in sync.")
    print(f"OPEN evidence items: {len(manifest['unresolved'])}; see docs/licensing-audit.md.")


if __name__ == "__main__":
    main()
