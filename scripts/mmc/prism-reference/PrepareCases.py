#!/usr/bin/env python3
"""Read the two authorized ZIPs and fixed adopted metadata; generate owned cases.

Original LF orchestration, governed by the repository LICENCE. No payloads,
accounts, installed instances, Java processes or network calls are used.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import zipfile

ARCHIVES = {
    "gtnh-java8": ("GT_New_Horizons_2.9.0_Java_8.zip", 732488324, "ae94a01a5367065f71ac9b5150966182a5b55762c841749fcb3d603ab5ed78af", 8),
    "gtnh-java21": ("GT_New_Horizons_2.9.0_Java_17-26.zip", 747848389, "6d0ebe7e6be18fb74b4fc920916cc5710e142a3f885072d324da05244d398b16", 21),
}
ADOPTED = {
    "org.lwjgl": ("2.9.4-nightly-20150209", "29a245e8def0c2eaa53d1373f70af0f8b43e677a88a529989e8de0f6a3e93d4c"),
    "net.minecraft": ("1.7.10", "5da6b22bde2571e1e9602f390cf5ba6535f85414fb82dd0d63782f44f33a38d4"),
    "net.minecraftforge": ("10.13.4.1614", "8805569218d16a3d66bf519961d9dbb96da82398c42056345450639b568dd199"),
}


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def case_config(root, folder, manifest, metadata, remote, entries, components, major, arch="amd64", bits=64):
    config = {
        "runtime": {"system": "windows", "javaArchitecture": str(bits), "javaRealArchitecture": arch, "javaMajor": major},
        "globalLibraryRoot": str(root / "layout/global-libraries"),
        "instanceLibraryRoot": str(root / "layout/instance/libraries"),
        "manifest": manifest,
        "metadata": metadata,
        "remoteMetadata": remote,
        "archiveEntries": entries,
        "components": components,
    }
    write(folder / "case.json", config)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--archive-directory", type=Path, default=Path(r"D:\User\Downloads"))
    parser.add_argument("--adopted-metadata", type=Path, required=True)
    args = parser.parse_args()
    root = args.root.resolve()
    if (root / "cases").exists():
        raise ValueError("Choose a new root: cases already exist.")
    inputs = []
    for name, (filename, size, expected, major) in ARCHIVES.items():
        archive = args.archive_directory / filename
        if archive.stat().st_size != size or digest(archive) != expected:
            raise ValueError(f"Authorized original ZIP identity mismatch: {archive}")
        folder = root / "cases" / name
        folder.mkdir(parents=True)
        metadata = {}
        with zipfile.ZipFile(archive, "r") as source:
            infos = source.infolist()
            manifests = [x for x in infos if x.filename.endswith("/mmc-pack.json")]
            if len(manifests) != 1:
                raise ValueError("Expected one original manifest.")
            manifest_info = manifests[0]
            prefix = manifest_info.filename[:-len("mmc-pack.json")]
            for info in infos:
                if not info.filename.startswith(prefix):
                    continue
                relative = info.filename[len(prefix):]
                if relative in ["mmc-pack.json", "instance.cfg"] or (relative.startswith("patches/") and relative.endswith(".json") and relative.count("/") == 1):
                    if info.file_size > 16 * 1024 * 1024:
                        raise ValueError("Metadata budget exceeded.")
                    target = folder / "metadata" / relative
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(source.read(info))
                    metadata[relative] = str(target)
            entries = [{"path": x.filename[len(prefix):], "size": x.file_size, "directory": x.is_dir()} for x in infos if x.filename.startswith(prefix) and x.filename != prefix]
        manifest = json.loads(Path(metadata["mmc-pack.json"]).read_text(encoding="utf-8-sig"))
        remote = []
        components = []
        for declaration in manifest["components"]:
            uid = declaration["uid"]
            local = metadata.get("patches/" + uid + ".json")
            if local:
                path = Path(local)
            else:
                version, adopted_sha = ADOPTED[uid]
                if declaration["version"] != version:
                    raise ValueError("Package requests a version outside the historical adopted lock.")
                prior = args.adopted_metadata / f"{uid}-{version}.json"
                if digest(prior) != adopted_sha:
                    raise ValueError(f"Adopted historical metadata mismatch: {prior}")
                path = folder / "remote" / f"{uid}.json"
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(prior.read_bytes())
                remote.append({"uid": uid, "version": version, "path": str(path), "sha256": adopted_sha,
                               "source": f"https://meta.prismlauncher.org/v1/{uid}/{version}.json"})
            enabled = declaration.get("important", False) or declaration.get("dependencyOnly", False) or not declaration.get("disabled", False)
            components.append({"uid": uid, "path": str(path), "enabled": enabled})
        case_config(root, folder, manifest, metadata, remote, entries, components, major)
        inputs.append({"case": name, "archive": str(archive), "size": size, "sha256": expected,
                       "metadata": [{"path": key, "sha256": digest(Path(value))} for key, value in metadata.items()],
                       "adoptedMetadata": remote, "originalZipReadOnly": True})

    def lib(name, **extra):
        return {"name": name, **extra}

    base = {"formatVersion": 1, "uid": "net.minecraft", "version": "1", "mainClass": "fixture.Main", "type": "release",
            "mainJar": lib("fixture:minecraft:1:client"),
            "assetIndex": {"id": "fixture", "url": "https://example.invalid/assets", "sha1": "0" * 40, "size": 1, "totalSize": 1}}
    first = {**base, "minecraftArguments": ' --quote "two words" --tab left\tright --empty ${client_token} --literal "" ',
             "+jvmArgs": ["-Dpath=中文 space", "", "-Dsame=1", "-Dsame=1"], "+tweakers": ["A", "B"],
             "compatibleJavaMajors": [8], "compatibleJavaName": "old",
             "libraries": [lib("fixture:a:1.2"), lib("fixture:b:1")],
             "mavenFiles": [lib("fixture:tool:1"), lib("fixture:tool:1"), lib("fixture:tool:2")]}
    later = {"formatVersion": 1, "uid": "custom", "version": "1", "mainClass": "fixture.Final", "+tweakers": ["A", "A"],
             "+jvmArgs": ["--add-opens", "java.base/java.lang=ALL-UNNAMED"], "compatibleJavaMajors": [17, 21, 17], "compatibleJavaName": "new",
             "libraries": [lib("fixture:a:1.10"), lib("fixture:early:1", **{"MMC-hint": "local", "MMC-filename": "custom.jar"})]}
    empty = {"formatVersion": 1, "uid": "empty", "version": "1", "mainClass": "", "minecraftArguments": "", "compatibleJavaMajors": [], "compatibleJavaName": ""}
    native = lib("fixture:platform:1", natives={"windows": "natives-windows-${arch}"}, extract={"exclude": ["META-INF/"]}, downloads={"classifiers": {
        "natives-windows-32": {"url": "https://example.invalid/32.jar", "sha1": "1" * 40, "size": 1},
        "natives-windows-64": {"url": "https://example.invalid/64.jar", "sha1": "2" * 40, "size": 1}}})
    rulebase = {**base, "libraries": [native,
        lib("fixture:wide:1", rules=[{"action": "allow", "os": {"name": "windows-x86_64", "arch": "x86", "version": "["}}]),
        lib("fixture:late:1", rules=[{"action": "disallow"}, {"action": "allow", "os": {"name": "windows"}}])]}
    fixtures = [
        ("ordered-fields", [first, later, empty], "amd64", 64),
        ("rules-native32", [rulebase], "i386", 32),
        ("rules-native-arm64", [rulebase], "aarch64", 64),
        ("null-mainjar", [base, {**empty, "mainJar": None}], "amd64", 64),
    ]
    for name, patches, arch, bits in fixtures:
        folder = root / "cases" / name
        metadata = {}
        components = []
        for patch in copy.deepcopy(patches):
            relative = "patches/" + patch["uid"] + ".json"
            target = folder / "metadata" / relative
            write(target, patch)
            metadata[relative] = str(target)
            components.append({"uid": patch["uid"], "path": str(target), "enabled": True})
        manifest = {"formatVersion": 1, "components": [{"uid": p["uid"], "version": p["version"]} for p in patches]}
        target = folder / "metadata/mmc-pack.json"
        write(target, manifest)
        metadata["mmc-pack.json"] = str(target)
        entries = [{"path": k, "size": Path(v).stat().st_size, "directory": False} for k, v in metadata.items()]
        case_config(root, folder, manifest, metadata, [], entries, components, 0, arch, bits)
        inputs.append({"case": name, "origin": "Original synthetic boundary fixture; no upstream metadata copied."})
    write(root / "inputs.json", inputs)
    print("Prepared two hash-verified original packages and four boundary fixtures.")


if __name__ == "__main__":
    main()
