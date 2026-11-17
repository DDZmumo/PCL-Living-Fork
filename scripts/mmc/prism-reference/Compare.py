#!/usr/bin/env python3
"""Run both actual implementations and preserve ordered field-by-field evidence.

Original LF orchestration, governed by the repository LICENCE. No rules are
implemented here: normalization only replaces two explicit path-role prefixes
and stable-deduplicates Prism's raw Java-major list as documented by LF.
"""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def path_role(value, case):
    value = value.replace("\\", "/")
    for key, role in [("instanceLibraryRoot", "instance/"), ("globalLibraryRoot", "global/")]:
        prefix = case[key].replace("\\", "/").rstrip("/") + "/"
        if value.startswith(prefix):
            return role + value[len(prefix):]
    raise ValueError("Reference returned a file outside the two explicit library roots: " + value)


def compare(prism, core, case):
    fields = {}

    def equal(name, left, right):
        fields[name] = {"equal": left == right, "prism": left, "core": right}

    for name in ["mainClass", "minecraftVersion", "gameArguments", "jvmArguments", "tweakers", "compatibleJavaName", "traits"]:
        equal(name, prism[name], core[name])
    equal("componentOrder", [x["uid"] for x in prism["components"]], [x["uid"] for x in core["components"]])
    equal("sourceHashes", [x["sourceSha256"] for x in prism["components"]], [x["sourceSha256"] for x in core["components"]])
    equal("mainJar.coordinate", prism["mainJar"]["name"], core["mainJar"]["name"])
    equal("mainJar.path", [path_role(x, case) for x in prism["mainJar"]["jars"]], [core["mainJar"]["path"]])
    equal("classpath.orderedPaths", [path_role(x, case) for x in prism["classpath"]], [x["path"] for x in core["classpath"]])
    equal("natives.orderedPaths", [path_role(x, case) for x in prism["nativeJars"]], [x["path"] for x in core["nativeJars"]])
    equal("natives.extractExcludes", [x["definition"].get("extract", {}).get("exclude", []) for x in prism["nativeLibraries"]], [x["excludes"] for x in core["nativeJars"]])
    equal("mavenFiles.orderedPaths", [path_role(p, case) for x in prism["mavenFiles"] for p in x["jars"]], [x["path"] for x in core["mavenFiles"]])
    equal("java.discreteStableSet", list(dict.fromkeys(prism["compatibleJavaMajorsRaw"])), core["compatibleJavaMajors"])
    fields["java.discreteStableSet"]["normalization"] = "LF explicitly stable-deduplicates the appended Prism list; no interval expansion or constraint intersection."
    return fields


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--core-adapter", type=Path, required=True)
    parser.add_argument("--dotnet", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True, help="New report file; existing evidence is never overwritten.")
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError("Choose a new output file.")
    root = args.root.resolve()
    native = root / "build/prism-reference.exe"
    identity = load(root / "build/identity.json")
    here = Path(__file__).resolve().parent
    bound = {"executableSha256": native, "transformationsSha256": root / "transformations.json",
             "sourceLockSha256": here / "source-lock.json", "toolchainLockSha256": here / "toolchain-lock.json",
             "adapterSourceSha256": here / "Reference.cpp", "qtCoreSha256": root / "sdk/mingw64/bin/Qt6Core.dll"}
    for key, path in bound.items():
        if identity.get(key) != sha(path):
            raise ValueError(f"Reference build identity is stale or changed ({key}); rebuild before comparison.")
    env = os.environ.copy()
    env["PATH"] = str(root / "sdk/mingw64/bin") + os.pathsep + env["PATH"]
    if os.name == "nt":
        ctypes.windll.kernel32.SetErrorMode(3)  # Inherit fail-fast loader errors; never show DLL-error dialogs.
    results = []
    for folder in sorted((root / "cases").iterdir()):
        case = load(folder / "case.json")
        output = args.output.parent / (args.output.stem + "-cases") / folder.name
        output.mkdir(parents=True, exist_ok=False)
        programs = {
            "prism": [str(native), str(folder / "case.json")],
            "core": [str(args.dotnet), str(args.core_adapter.resolve()), str(folder / "case.json")],
        }
        executions = {}
        documents = {}
        for name, command in programs.items():
            child = subprocess.run(command, capture_output=True, env=env, timeout=60)
            (output / (name + ".json")).write_bytes(child.stdout)
            (output / (name + ".stderr.log")).write_bytes(child.stderr)
            executions[name] = {"exitCode": child.returncode, "stdoutSha256": sha(output / (name + ".json")),
                                "stderrSha256": sha(output / (name + ".stderr.log"))}
            if child.returncode == 0:
                documents[name] = json.loads(child.stdout)
        fields = compare(documents["prism"], documents["core"], case) if len(documents) == 2 else {}
        results.append({"case": folder.name, "caseSha256": sha(folder / "case.json"), "executions": executions,
                        "allComparedFieldsEqual": bool(fields) and all(x["equal"] for x in fields.values()),
                        "fields": fields, "evidence": str(output)})
        print(folder.name, "equal" if results[-1]["allComparedFieldsEqual"] else "DIFFERENCE", flush=True)
    dlls = ["Qt6Core.dll", "libstdc++-6.dll", "libgcc_s_seh-1.dll", "libwinpthread-1.dll", "libicudt77.dll", "libicuuc77.dll", "libicuin77.dll", "libpcre2-16-0.dll", "libdouble-conversion.dll", "libb2-1.dll", "zlib1.dll", "libzstd.dll"]
    report = {
        "schemaVersion": 1, "kind": "PrismNativeSubsetVsActualCore", "upstreamCommit": "899d2f3957215a09fe398a8a8cbb8a8a9c619968",
        "os": platform.platform(), "nativeExecutableSha256": sha(native), "adapterSha256": sha(args.core_adapter),
        "buildIdentity": identity, "transformationsSha256": sha(root / "transformations.json"),
        "runtimeDllSha256": {name: sha(root / "sdk/mingw64/bin" / name) for name in dlls},
        "cases": results,
        "scope": "Actual pinned native parsing/applyTo/library plan subset. Explicit package component order supplied by adapter; PackProfile/Component resolver, Java auto-selection, filesystem materialization, credentials, wrappers, GUI and full launch are not executed.",
        "notAnOracle": "Neither a handwritten Prism port nor Prism12 portable output. Reference.cpp calls compiled original function bodies; all dependency trimming and excluded IO is recorded in transformations.json.",
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    # Differences remain evidence, including known grammar differences; do not
    # mutate rules or convert a rejected native case into an equivalence pass.
    return 0 if all(x["allComparedFieldsEqual"] for x in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
