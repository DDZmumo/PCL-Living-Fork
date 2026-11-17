#!/usr/bin/env python3
"""Inventory retained fixture JAR payloads and actual LF native output hashes.

Original LF read-only evidence tool, governed by the repository LICENCE.
This is not a Prism extractor or oracle. It consumes already produced native
plans and compares concrete retained bytes, without Java, GUI or extraction.
"""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile
from Compare import path_role


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(data):
    return hashlib.sha256(data).hexdigest()


def child(root, relative):
    path = (root / relative).resolve()
    if not path.is_relative_to(root):
        raise ValueError("Receipt path escapes the explicit fixture instance root.")
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--import-receipt", type=Path, required=True)
    parser.add_argument("--case", type=Path, required=True)
    parser.add_argument("--comparison-evidence", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError("Choose a new evidence output.")
    receipt, case = read(args.import_receipt), read(args.case)
    if receipt["Status"] != "Succeeded":
        raise ValueError("A successful retained fixture import receipt is required.")
    root = Path(receipt["InstanceRoot"]).resolve()
    if not root.is_relative_to(args.import_receipt.resolve().parent / "game"):
        raise ValueError("Instance root is outside the supplied fixture receipt's game subtree.")
    core = read(args.comparison_evidence / "core.json")
    prism = read(args.comparison_evidence / "prism.json")
    native = {a["path"]: a for a in core["nativeJars"]}
    selected = {path_role(p, case) for p in prism["nativeJars"]}
    source_facts = [x for x in receipt["Libraries"] if x["Role"] == "Native"]
    if selected != set(native) or selected != {"global/" + a["RelativePath"] for a in source_facts}:
        raise ValueError("Concrete receipt and the two actual plans select different native input files.")
    inventory, output_files, payload, collisions = [], [], {}, []
    for artifact in source_facts:
        path = child(root, artifact["TargetRelativePath"])
        if path.stat().st_size > 32 * 1024 * 1024:
            raise ValueError("This fixture inventory bounds each native JAR to 32 MiB.")
        data = path.read_bytes()
        if sha(data) != artifact["AdoptedSha256"]:
            raise ValueError("Retained native input no longer matches its adoption receipt.")
        declaration = native["global/" + artifact["RelativePath"]]
        if declaration["sha1"] and hashlib.sha1(data).hexdigest() != declaration["sha1"]:
            raise ValueError("Retained native input differs from the fixed compiled metadata digest.")
        entries = []
        with zipfile.ZipFile(path, "r") as archive:
            for entry in archive.infolist():
                if entry.is_dir():
                    continue
                if entry.file_size > 32 * 1024 * 1024:
                    raise ValueError("Native entry inventory budget exceeded.")
                value = archive.read(entry)
                fact = {"path": entry.filename, "bytes": len(value), "sha256": sha(value)}
                entries.append(fact)
                if any(entry.filename.startswith(prefix) for prefix in declaration["excludes"]):
                    continue
                if entry.filename in payload:
                    collisions.append({"path": entry.filename, "sameBytes": payload[entry.filename]["sha256"] == fact["sha256"]})
                payload[entry.filename] = fact
        inventory.append({"relativePath": artifact["RelativePath"], "sha256": sha(data), "bytes": len(data), "entries": entries})
    for observed in receipt["NativeFiles"]:
        value = child(root, observed["TargetRelativePath"]).read_bytes()
        if sha(value) != observed["Sha256"] or sha(value) != payload[observed["RelativePath"]]["sha256"]:
            raise ValueError("LF native output differs from its receipt or declared input payload.")
        output_files.append({"path": observed["RelativePath"], "bytes": len(value), "sha256": sha(value)})
    if set(payload) != {x["path"] for x in output_files}:
        raise ValueError("LF materialized output and the inventory have different file sets.")
    report = {"scope": "Read-only actual selected JAR entry hashes and LF materialized native files. No Prism extraction executed.",
              "importReceipt": str(args.import_receipt.resolve()), "importReceiptSha256": sha(args.import_receipt.read_bytes()),
              "prismOutputSha256": sha((args.comparison_evidence / "prism.json").read_bytes()),
              "coreOutputSha256": sha((args.comparison_evidence / "core.json").read_bytes()),
              "jars": inventory, "entryCollisions": collisions, "lfOutputFiles": output_files,
              "nativePayloadCount": len(payload), "lfMaterializedCount": len(output_files),
              "lfOutputMatchesInputPayloadHashes": True,
              "orderIndependentPayloadBytes": all(x["sameBytes"] for x in collisions),
              "notFullPrismExtractionEvidence": "Same input payload bytes and no differing-byte collisions prove order invariance for these inputs; full Prism filesystem extraction was not run."}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2)
        stream.write("\n")
    print(f"Verified {len(inventory)} selected JARs and {len(output_files)} materialized native files; collisions={len(collisions)}.")


if __name__ == "__main__":
    main()
