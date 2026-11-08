#!/usr/bin/env python3
"""Check the real migration selector with fixed reviewable scopes and CLI events.

Only Python's standard library and Git are needed. No product is built or run.
The temporary Git graph stays on migration/csharp-modern-dotnet; no real
repository file, index, ref, user profile or Git configuration is written.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

BRANCH = "migration/csharp-modern-dotnet"
HERE = Path(__file__).resolve().parent
GOLD = json.loads((HERE / "MigrationSelection.golden.json").read_text(encoding="utf-8"))
FIELDS = tuple(GOLD["provenance"]["scopeFields"])
BOOLEAN_FIELDS = tuple(name for name in FIELDS if name not in {"groups", "windows_scripts"})
# Do not inherit tokens, external Git commands, signing hooks or caller event
# variables. Native executable discovery still uses the caller's normal PATH.
ENV = {name: os.environ[name] for name in ("PATH", "LANG", "LC_ALL", "SYSTEMROOT", "WINDIR") if name in os.environ}
ENV.update(GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull)


def require(value: bool, reason: str) -> None:
    if not value:
        raise AssertionError(reason)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo-root", type=Path, default=HERE.parents[1], help="Repository containing the real scripts/Select-MigrationChecks.py")
    args = parser.parse_args()
    repo = args.repo_root.resolve()
    selector = repo / "scripts/Select-MigrationChecks.py"
    require(selector.is_file(), "The real production selector is missing.")
    count = 0

    def cli(paths: tuple[str, ...] | list[str] = (), *, extra: tuple[str, ...] | list[str] = (), cwd: Path = repo, env: dict[str, str] | None = None) -> dict:
        command = [sys.executable, str(selector)]
        for path in paths:
            command.extend(("--path", path))
        result = subprocess.run(command + list(extra), cwd=cwd, env=ENV if env is None else env,
                                capture_output=True, text=True, check=True, timeout=30)
        return json.loads(result.stdout)

    def expect(plan: dict, scope: str, native: bool, label: str, paths: list[str] | None = None, *, docs_only: bool | None = None) -> None:
        nonlocal count
        require(set(plan) == set(FIELDS) | {"docs_only", "native_run", "reasons", "changed_paths"}, label + ": job/report output keys changed")
        for name in (*BOOLEAN_FIELDS, "native_run", "docs_only"):
            require(type(plan[name]) is bool, label + ": non-boolean job flag " + name)
        expected = GOLD["scopes"][scope]
        actual = {name: plan[name] for name in FIELDS}
        require(actual == expected, label + ": changed legacy scope\nexpected=" + json.dumps(expected, ensure_ascii=True, sort_keys=True) + "\nactual=" + json.dumps(actual, ensure_ascii=True, sort_keys=True))
        require(plan["native_run"] is native, label + ": native dependency lost or added to unrelated checks")
        if docs_only is not None:
            require(plan["docs_only"] is docs_only, label + ": wrong documentation-only decision")
        if paths is not None:
            require(plan["changed_paths"] == paths, label + ": input paths were lost or rewritten")
        count += 1
        print("PASS " + label, flush=True)

    # These golds were captured from the actual pre-native Root selector. They
    # retain every old group/script/oracle/credential gate, not a second router.
    for case in GOLD["legacyCases"]:
        expect(cli([case["path"]]), case["scope"], case["native"], "preserved scope: " + case["path"], [case["path"]])
    # New author-ownership/JAR fixtures must be selected on their own changes
    # and when a shared loader parser changes. Retain the original gold cases.
    for case in GOLD["packUpdateCases"]:
        expect(cli(case["paths"]), case["scope"], case["native"], "pack update: " + case["label"], case["paths"])
    for path in GOLD["nativeOnlyPaths"]:
        expect(cli([path]), "no-checks", True, "native-only: " + path, [path])
    for path in ("scripts/tests/Test-MigrationSelection.py", "scripts/tests/MigrationSelection.golden.json"):
        expect(cli([path]), "no-checks", False, "routing self-check only: " + path, [path])
    for path in ("scripts/Test-NativeUpdateBridge.ps1", "scripts/native-upgrade/NativeUpdateFixture.cs", "scripts/tests/Test-BridgePayloadIdentity.ps1"):
        expect(cli([path]), "no-checks", True, "real native upgrade acceptance only: " + path, [path])
    mixed = ["modern/Main/FormMain.xaml.cs", "Plain Craft Launcher 2/Modules/Minecraft/WorldBackups.vb"]
    expect(cli(mixed), "world-backups", True, "mixed native and legacy consumers", mixed)
    expect(cli(extra=["--full"]), "complete", True, "explicit complete checkpoint", [])
    for path in ("README.md", "docs/BUILDING_MODERN.md", "docs/assets/calendar-dark.svg", "modern/Main/SOURCE_PROVENANCE.md"):
        expect(cli([path]), "no-checks", False, "documentation whitelist: " + path, [path], docs_only=True)
    for path in ("docs/assets/Generate-ReadmeCalendar.py", "docs/migration/data.json", "docs/AGENTS.md", "docs/../PCLCS/input.md", "docs/a\nREADME.md"):
        expect(cli([path]), "complete", True, "code or ambiguous doc lookalike: " + path, [path], docs_only=False)
    expect(cli(["README.md"], extra=["--full"]), "complete", True, "manual full overrides docs whitelist", ["README.md"], docs_only=False)
    expect(cli(["README.md", "modern/Main/Fixture.cs"]), "no-checks", True, "mixed docs and native input retains product validation", ["README.md", "modern/Main/Fixture.cs"], docs_only=False)
    for path, scope, native in (
        ("PCLCS/Resources/LegacyModFavorites.cs", "mod-favorites-tests", True),
        ("tests/PCLLF.Migration.Tests/LegacyModFavoritesTests.cs", "mod-favorites-tests", False),
        ("PCLCS/Resources/ResourceFavorites.cs", "mod-favorites-tests", True),
        ("PCLCS/Resources/ResourceProjectKey.cs", "mod-favorites-tests", True),
        ("tests/PCLLF.Migration.Tests/ResourceFavoriteTests.cs", "mod-favorites-tests", False),
        ("PCLCS/Settings/LegacyUpdateLogPrompt.cs", "settings-tests", True),
        ("tests/PCLLF.Migration.Tests/UpdateLogPromptTests.cs", "settings-tests", False),
    ):
        expect(cli([path]), scope, native, "A-003 routing: " + path, [path])
    registry_source = (repo / "tests/PCLLF.Migration.Tests/Program.cs").read_text(encoding="utf-8")
    registry_block = re.search(r'\bgroups\s*=\s*\[(.*?)\];', registry_source, re.DOTALL)
    require(registry_block is not None, "Production migration test group registration is missing.")
    registry = re.findall(r'^\s+\("([a-z0-9-]+)",', registry_block.group(1), re.MULTILINE)
    require(registry and len(registry) == len(set(registry)), "Production migration test registry is empty or contains duplicate keys.")
    require(registry == GOLD["scopes"]["complete"]["groups"], "Complete CI scope does not contain exactly the production migration test groups in canonical order.")
    toolbox_path = "PCLCS/Toolbox/SymbioLauncherSwitch.cs"
    toolbox_plan = cli([toolbox_path])
    expect(toolbox_plan, "complete", True, "new toolbox code retains complete product validation", [toolbox_path])
    require("toolbox-surprise" in toolbox_plan["groups"], "Toolbox production changes must run their registered migration tests.")
    # No event is deliberately conservative; it must not be mistaken for a
    # valid, empty git diff (the latter is exercised below).

    with tempfile.TemporaryDirectory(prefix="PclMigrationSelection-") as temporary:
        fixture = Path(temporary)
        event_file = fixture / "event.json"
        expect(cli(extra=["--event-file", str(event_file)]), "complete", True, "missing event fails closed", [])
        hooks = fixture / "empty-hooks"
        hooks.mkdir()
        git_options = ["git", "-c", "init.templateDir=", "-c", "core.hooksPath=" + str(hooks),
                       "-c", "user.name=PCLLF selection fixture", "-c", "user.email=fixture@pcllf.invalid", "-c", "commit.gpgsign=false"]

        def git(*arguments: str) -> str:
            result = subprocess.run(git_options + list(arguments), cwd=fixture, env=ENV,
                                    capture_output=True, text=True, check=True, timeout=30)
            return result.stdout.strip()

        git("init", "--quiet", "--initial-branch=" + BRANCH, ".")
        (fixture / "docs").mkdir()
        (fixture / "docs/fixture.md").write_text("Only Git event fixture data.\n", encoding="utf-8")
        git("add", "docs/fixture.md")
        git("commit", "--quiet", "-m", "selection fixture baseline")
        initial = git("rev-parse", "HEAD")
        native_path = "modern/Main/Fixture.cs"
        (fixture / native_path).parent.mkdir(parents=True)
        (fixture / native_path).write_text("// Git path fixture; never compiled.\n", encoding="utf-8")
        git("add", native_path)
        git("commit", "--quiet", "-m", "selection fixture native input")
        native_head = git("rev-parse", "HEAD")

        def event(data: dict, event_name: str, *, scope: str, native: bool, label: str, paths: list[str], ref: str | None = None, docs_only: bool | None = None) -> None:
            event_file.write_text(json.dumps(data), encoding="utf-8")
            environment = ENV | {"GITHUB_EVENT_NAME": event_name, "GITHUB_REF": ref or "refs/heads/" + BRANCH}
            plan = cli(extra=["--event-file", str(event_file)], cwd=fixture, env=environment)
            expect(plan, scope, native, label, paths, docs_only=docs_only)

        event({"before": initial, "after": native_head}, "push", scope="no-checks", native=True,
              label="actual push git diff selects only native input", paths=[native_path])
        event({"before": native_head, "after": native_head}, "push", scope="no-checks", native=False,
              label="actual empty git diff selects no product checks", paths=[])
        # Create a diverged base commit without switching any branch. Comparing
        # base..head would incorrectly include the base's shared Core change;
        # the real PR's base...head must select only the proposed native path.
        git("read-tree", initial)
        shared_path = "PCLCS/Minecraft/WorldBackupEngine.cs"
        (fixture / shared_path).parent.mkdir(parents=True)
        (fixture / shared_path).write_text("// Diverged base path fixture; never compiled.\n", encoding="utf-8")
        git("add", shared_path)
        diverged_base = git("commit-tree", git("write-tree"), "-p", initial, "-m", "selection fixture diverged base")
        require(git("branch", "--show-current") == BRANCH, "Temporary fixture switched away from the migration branch.")
        event({"pull_request": {"base": {"ref": BRANCH, "sha": diverged_base}, "head": {"sha": native_head}}},
              "pull_request", scope="no-checks", native=True, label="actual PR triple-dot excludes unrelated diverged base changes", paths=[native_path])
        event({}, "workflow_dispatch", scope="complete", native=True, label="manual checkpoint retains both complete graphs", paths=[])
        event({"before": initial, "after": native_head}, "push", scope="complete", native=True,
              label="main push runs the complete product and migration graphs", paths=[], ref="refs/heads/main")
        event({"before": native_head, "after": native_head}, "push", scope="complete", native=True,
              label="main empty git diff fails closed to complete product checks", paths=[], ref="refs/heads/main")
        event({"pull_request": {"base": {"ref": "main", "sha": diverged_base}, "head": {"sha": native_head}}},
              "pull_request", scope="complete", native=True, label="main PR runs complete acceptance instead of affected-only checks", paths=[], ref="refs/pull/7/merge")
        event({"merge_group": {"base_ref": "refs/heads/main", "base_sha": initial, "head_sha": native_head}},
              "merge_group", scope="complete", native=True, label="main merge queue runs the complete combined snapshot", paths=[], ref="refs/heads/gh-readonly-queue/main/pr-7-fixture")
        event({}, "workflow_dispatch", scope="complete", native=True, label="manual feature-branch checkpoint cannot be skipped", paths=[], ref="refs/heads/codex/ci-fixture")
        event({"before": initial, "after": native_head}, "unknown_event", scope="complete", native=True,
              label="unknown event cannot request incremental checks", paths=[])
        event({"pull_request": {"base": [], "head": {"sha": native_head}}}, "pull_request", scope="complete", native=True,
              label="malformed PR branch context falls back to complete checks", paths=[])
        event({"before": "0" * 40, "after": native_head}, "push", scope="complete", native=True,
              label="initial diff base fails closed", paths=[])
        event({"before": "f" * 40, "after": native_head}, "push", scope="complete", native=True,
              label="unavailable git object fails closed", paths=[])
        # Run actual main push, PR and merge-queue comparisons over docs-only
        # commits, including deletion and a code-to-doc rename.
        git("read-tree", native_head)
        (fixture / "docs/fixture.md").write_text("Updated document fixture.\n", encoding="utf-8")
        git("add", "docs/fixture.md")
        docs_head = git("commit-tree", git("write-tree"), "-p", native_head, "-m", "docs-only fixture")
        event({"before": native_head, "after": docs_head}, "push", scope="no-checks", native=False,
              label="main docs-only push skips product execution", paths=["docs/fixture.md"], ref="refs/heads/main", docs_only=True)
        event({"pull_request": {"base": {"ref": "main", "sha": native_head}, "head": {"sha": docs_head}}}, "pull_request", scope="no-checks", native=False,
              label="main docs-only PR skips product execution", paths=["docs/fixture.md"], ref="refs/pull/8/merge", docs_only=True)
        event({"merge_group": {"base_ref": "refs/heads/main", "base_sha": native_head, "head_sha": docs_head}}, "merge_group", scope="no-checks", native=False,
              label="main docs-only merge queue skips product execution", paths=["docs/fixture.md"], docs_only=True)
        git("update-index", "--force-remove", "docs/fixture.md")
        deleted_doc = git("commit-tree", git("write-tree"), "-p", docs_head, "-m", "delete retired documentation fixture")
        event({"before": docs_head, "after": deleted_doc}, "push", scope="no-checks", native=False,
              label="deleted documentation remains docs-only", paths=["docs/fixture.md"], ref="refs/heads/main", docs_only=True)
        git("read-tree", native_head)
        git("mv", native_path, "docs/renamed.md")
        renamed = git("commit-tree", git("write-tree"), "-p", native_head, "-m", "code renamed into documentation fixture")
        event({"before": native_head, "after": renamed}, "push", scope="complete", native=True,
              label="main code-to-doc rename cannot bypass product validation", paths=[], ref="refs/heads/main", docs_only=False)
        event({"before": native_head, "after": renamed}, "push", scope="no-checks", native=True,
              label="rename diff retains both old code and new doc paths", paths=["docs/renamed.md", native_path], docs_only=False)
        event({"before": native_head, "after": docs_head}, "push", scope="complete", native=True,
              label="docs cannot bypass an unknown branch context", paths=[], ref="refs/heads/unrecognized", docs_only=False)
        event_file.write_text("{not-json", encoding="utf-8")
        expect(cli(extra=["--event-file", str(event_file)], cwd=fixture, env=ENV | {"GITHUB_EVENT_NAME": "push", "GITHUB_REF": "refs/heads/" + BRANCH}),
               "complete", True, "malformed event fails closed", [])
        output = fixture / "github-output"
        report = fixture / "report.json"
        untrusted = "modern/Main/path\nNOT_AN_OUTPUT=true.cs"
        require("\n" in untrusted, "The output escaping fixture must contain a real newline.")
        plan = cli([untrusted], extra=["--github-output", str(output), "--report", str(report)])
        expect(plan, "no-checks", True, "real newline cannot inject a GitHub job output", [untrusted])
        lines = output.read_text(encoding="utf-8").splitlines()
        values = dict(line.split("=", 1) for line in lines)
        require(len(lines) == len(FIELDS) + 2 and set(values) == set(FIELDS) | {"docs_only", "native_run"}, "Untrusted path escaped fixed-name GitHub outputs.")
        require({name: json.loads(value) for name, value in values.items()} == {name: plan[name] for name in (*FIELDS, "native_run", "docs_only")}, "GitHub outputs differ from the actual selected scope.")
        require(json.loads(report.read_text(encoding="utf-8")) == plan, "Saved selection report differs from stdout.")

    print("PASS actual migration selection checks: " + str(count) + "; no product build/run", flush=True)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, OSError, ValueError, subprocess.SubprocessError) as error:
        print("FAIL migration selection: " + str(error), file=sys.stderr)
        if isinstance(error, subprocess.CalledProcessError):
            print(error.stderr or error.stdout or "", file=sys.stderr)
        raise SystemExit(1)
