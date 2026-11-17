#!/usr/bin/env python3
"""Read-only MMC ZIP inventory for P0 evidence; this is not a compatibility compiler.

Only bounded manifests/cfg/patches are decompressed. No game file is extracted,
command is executed, user directory is inspected, or network request is made.
This independent LF host tool retains the repository's existing LICENCE.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import struct
import sys
import unicodedata
import zipfile

MAX_ENTRIES = 100_000
MAX_TOTAL_BYTES = 64 * 1024**3
MAX_ENTRY_BYTES = 4 * 1024**3
MAX_METADATA_BYTES = 16 * 1024**2
MAX_METADATA_TOTAL = 128 * 1024**2
MAX_COMPONENTS = 4096
MAX_JSON_DEPTH = 128
MAX_PATH_CHARS = 32767
MAX_PATH_DEPTH = 128
MAX_SEGMENT_CHARS = 255
MAX_PATH_INPUT_CHARS = 16 * 1024**2
MAX_PATH_NODES = MAX_ENTRIES * 2
MAX_PATH_NODE_CHARS = 32 * 1024**2
MAX_CENTRAL_DIRECTORY_BYTES = 16 * 1024**2
PATH_CASE_PROFILE = "lf-audit-python-simple-uppercase-v1"
DEVICE = re.compile(r"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", re.I | re.ASCII)
PATCH_FIELDS = {
    "formatVersion", "uid", "fileId", "name", "version", "order", "releaseTime",
    "mainClass", "mainJar", "minecraftArguments", "+jvmArgs", "+tweakers",
    "assetIndex", "assets", "type", "libraries", "+libraries", "mavenFiles",
    "compatibleJavaMajors", "compatibleJavaName", "requires", "conflicts",
    "traits", "+traits", "jarMods", "+jarMods", "agents", "+agents", "volatile",
}
UNSUPPORTED_ACTIONS = {"jarMods", "+jarMods", "agents", "+agents"}
KNOWN_TRAITS = {"FirstThreadOnMacOS", "texturepacks", "no-texturepacks"}


class AuditError(ValueError):
    """An archive cannot be safely or unambiguously inventoried."""


@contextmanager
def evidence_on_failure(report: dict):
    try:
        yield
    except (AuditError, OSError, zipfile.BadZipFile, RuntimeError, NotImplementedError) as exc:
        exc.audit_evidence = report
        raise


def utf16_length(value: str) -> int:
    return len(value.encode("utf-16-le", errors="surrogatepass")) // 2


def target_key(value: str) -> str:
    """Explicit bounded audit profile, not a full .NET/NTFS comparison oracle.

    The exceptions below are fixed against .NET 10 OrdinalIgnoreCase vectors.
    Unknown multi-character uppercase mappings are rejected, never guessed.
    No Unicode normalization or full casefold expansion is performed.
    """
    result = []
    for char in value:
        if char in {"ß", "ﬃ", "ı", "ſ"}:
            result.append(char)
            continue
        upper = char.upper()
        if len(upper) != 1 or (ord(char) > 127 and ord(upper) < 128):
            raise AuditError(f"Unsupported path casing in {PATH_CASE_PROFILE}: U+{ord(char):04X}")
        result.append(upper)
    return "".join(result)


def preflight_zip(stream) -> dict:
    """Bound the central directory before ZipFile allocates its full index.

    Supported ZIP64 has one disk and the fixed 44-byte EOCD payload. SFX offset
    adjustments, extensible ZIP64 records, trailing data and directory digital
    signatures are explicitly outside this audit profile.
    """
    stream.seek(0, 2)
    size = stream.tell()
    tail_size = min(size, 65557)
    stream.seek(size - tail_size)
    tail = stream.read(tail_size)
    position = tail.rfind(b"PK\x05\x06")
    if position < 0 or len(tail) - position < 22:
        raise AuditError("Missing or truncated ZIP end-of-central-directory record")
    eocd = struct.unpack_from("<4s4H2LH", tail, position)
    _, disk, central_disk, disk_count, count, central_bytes, central_offset, comment_bytes = eocd
    if position + 22 + comment_bytes != len(tail):
        raise AuditError("Unsupported trailing data or ambiguous ZIP comment")
    if disk != 0 or central_disk != 0 or disk_count != count:
        raise AuditError("Multi-disk ZIP is unsupported")
    eocd_offset = size - tail_size + position
    central_end = eocd_offset
    stream.seek(max(0, eocd_offset - 20))
    locator = stream.read(min(20, eocd_offset))
    has_zip64 = len(locator) == 20 and locator[:4] == b"PK\x06\x07"
    sentinel = count == 0xFFFF or central_bytes == 0xFFFFFFFF or central_offset == 0xFFFFFFFF
    if sentinel and not has_zip64:
        raise AuditError("ZIP64 sentinel requires a valid bounded ZIP64 locator")
    if has_zip64:
        _, zip64_disk, zip64_offset, disk_total = struct.unpack("<4sLQL", locator)
        if zip64_disk != 0 or disk_total != 1:
            raise AuditError("Multi-disk ZIP64 is unsupported")
        if zip64_offset + 56 != eocd_offset - 20:
            raise AuditError("Unsupported ZIP64 locator offset or extensible record")
        stream.seek(zip64_offset)
        record = stream.read(56)
        if len(record) != 56:
            raise AuditError("Truncated ZIP64 end record")
        values = struct.unpack("<4sQ2H2L4Q", record)
        signature, payload_bytes, _, _, disk64, central_disk64, disk_count64, count64, central_bytes64, central_offset64 = values
        if signature != b"PK\x06\x06" or payload_bytes != 44:
            raise AuditError("Unsupported ZIP64 end record size or signature")
        if disk64 != 0 or central_disk64 != 0 or disk_count64 != count64:
            raise AuditError("Multi-disk ZIP64 is unsupported")
        for classic, expanded, marker in ((count, count64, 0xFFFF), (central_bytes, central_bytes64, 0xFFFFFFFF),
                                           (central_offset, central_offset64, 0xFFFFFFFF)):
            if classic != marker and classic != expanded:
                raise AuditError("Conflicting ZIP and ZIP64 directory declarations")
        count, central_bytes, central_offset = count64, central_bytes64, central_offset64
        central_end = zip64_offset
    if count > MAX_ENTRIES:
        raise AuditError("ZIP preflight exceeds entry count budget")
    if central_bytes > MAX_CENTRAL_DIRECTORY_BYTES:
        raise AuditError("ZIP preflight exceeds central-directory byte budget")
    if central_offset > central_end or central_bytes != central_end - central_offset:
        raise AuditError("Unsupported or inconsistent ZIP central-directory bounds")
    stream.seek(central_offset)
    remaining = central_bytes
    scanned = 0
    path_chars = 0
    while remaining:
        if remaining < 46:
            raise AuditError("Truncated central-directory fixed header")
        header = stream.read(46)
        if len(header) != 46:
            raise AuditError("Truncated central-directory fixed header")
        fields = struct.unpack("<4s6H3L5H2L", header)
        if fields[0] != b"PK\x01\x02":
            raise AuditError("Unsupported central-directory record signature")
        scanned += 1
        if scanned > MAX_ENTRIES:
            raise AuditError("ZIP preflight scanned entry count budget exceeded")
        name_bytes, extra_bytes, entry_comment_bytes, entry_disk = fields[10:14]
        if entry_disk != 0:
            raise AuditError("Multi-disk central-directory entry is unsupported")
        variable_bytes = name_bytes + extra_bytes + entry_comment_bytes
        if 46 + variable_bytes > remaining:
            raise AuditError("Truncated central-directory variable fields")
        raw_name = stream.read(name_bytes)
        try:
            name = raw_name.decode("utf-8" if fields[3] & 0x800 else "cp437")
        except UnicodeError as exc:
            raise AuditError("Invalid central-directory filename encoding") from exc
        path_chars += utf16_length(name)
        if path_chars > MAX_PATH_INPUT_CHARS:
            raise AuditError("ZIP preflight exceeds aggregate input path character budget")
        entry_path(name)
        target_key(name)
        stream.seek(extra_bytes + entry_comment_bytes, 1)
        remaining -= 46 + variable_bytes
    if scanned != count:
        raise AuditError("ZIP preflight entry count disagrees with end record")
    stream.seek(0)
    return {"centralDirectoryBytes": central_bytes, "centralDirectoryOffset": central_offset,
            "scannedEntryCount": scanned, "inputPathUtf16Chars": path_chars,
            "zip64": has_zip64, "maxCentralDirectoryBytes": MAX_CENTRAL_DIRECTORY_BYTES,
            "profile": "lf-audit-bounded-single-disk-zip-v1"}


def entry_path(raw: str) -> tuple[str, bool]:
    if len(raw) > MAX_PATH_CHARS or utf16_length(raw) > MAX_PATH_CHARS:
        raise AuditError("Archive path exceeds length budget")
    value = raw.replace("\\", "/")
    directory = value.endswith("/")
    value = value[:-1] if directory else value
    parts = value.split("/")
    if len(parts) > MAX_PATH_DEPTH:
        raise AuditError("Archive path exceeds segment depth budget")
    if not value or value.startswith("/") or any(part in {"", ".", ".."} for part in parts):
        raise AuditError(f"Unsafe archive path: {raw!r}")
    for part in parts:
        if len(part) > MAX_SEGMENT_CHARS or utf16_length(part) > MAX_SEGMENT_CHARS:
            raise AuditError("Archive path segment exceeds length budget")
        if (part.endswith((" ", ".")) or DEVICE.match(part)
                or any(ord(c) < 32 or c in ':<>"|?*' for c in part)):
            raise AuditError(f"Unsafe Windows archive path: {raw!r}")
    return "/".join(parts), directory


def index_archive(archive: zipfile.ZipFile) -> tuple[dict[str, zipfile.ZipInfo], dict, set[str]]:
    entries = archive.infolist()
    if len(entries) > MAX_ENTRIES:
        raise AuditError("Archive exceeds entry count budget")
    files: dict[str, zipfile.ZipInfo] = {}
    roles: dict[str, tuple[str, str]] = {}
    explicit: dict[str, str] = {}
    directory_names: dict[str, set[str]] = {}
    repeated_directories: list[str] = []
    declared_bytes = 0
    input_path_chars = 0
    node_path_chars = 0
    for info in entries:
        input_path_chars += utf16_length(info.orig_filename)
        if input_path_chars > MAX_PATH_INPUT_CHARS:
            raise AuditError("Archive exceeds aggregate input path character budget")
        path, directory = entry_path(info.orig_filename)
        path_key = target_key(path)
        entry_role = "directory" if directory else "file"
        old_explicit = explicit.get(path_key)
        if old_explicit is not None:
            if old_explicit != "directory" or entry_role != "directory":
                raise AuditError(f"Duplicate or case-colliding file target: {path}")
            repeated_directories.append(path)
        explicit[path_key] = entry_role
        mode = info.external_attr >> 16
        if stat.S_ISLNK(mode):
            raise AuditError(f"Symbolic-link entry is unsupported: {path}")
        if info.flag_bits & 1:
            raise AuditError(f"Encrypted entry is unsupported: {path}")
        if info.file_size > MAX_ENTRY_BYTES:
            raise AuditError(f"Entry exceeds unpacked byte budget: {path}")
        declared_bytes += info.file_size
        if declared_bytes > MAX_TOTAL_BYTES:
            raise AuditError("Archive exceeds declared unpacked total budget")
        parts = path.split("/")
        key_parts = path_key.split("/")
        prefix_chars = 0
        for count in range(1, len(parts) + 1):
            prefix_chars += utf16_length(parts[count - 1]) + (1 if count > 1 else 0)
            node_path_chars += prefix_chars
            if node_path_chars > MAX_PATH_NODE_CHARS:
                raise AuditError("Archive exceeds aggregate target node character budget")
            current = "/".join(parts[:count])
            current_key = "/".join(key_parts[:count])
            role = "directory" if count < len(parts) or directory else "file"
            old = roles.get(current_key)
            if old is not None and old[1] != role:
                raise AuditError(f"File/directory role collision: {current}")
            if role == "directory":
                names = directory_names.setdefault(current_key, set())
                names.add(current)
            if old is None:
                if len(roles) >= MAX_PATH_NODES:
                    raise AuditError("Archive exceeds implicit target node count budget")
                roles[current_key] = (current, role)
        if not directory:
            files[path] = info
    return files, {
        "entryCount": len(entries), "fileCount": len(files),
        "declaredUnpackedBytes": declared_bytes,
        "compressedEntryBytes": sum(x.compress_size for x in entries),
        "payloadCrcVerified": False,
        "directoryAliases": [sorted(names) for _, names in sorted(directory_names.items()) if len(names) > 1],
        "repeatedDirectoryEntries": repeated_directories,
        "inputPathUtf16Chars": input_path_chars,
        "targetNodeCount": len(roles), "targetPrefixCandidateUtf16Chars": node_path_chars,
    }, {key for key, (_, role) in roles.items() if role == "directory"}


def _unique_object(pairs: list[tuple[str, object]]) -> dict:
    result: dict = {}
    for key, value in pairs:
        if key in result:
            raise AuditError(f"Duplicate JSON property: {key}")
        result[key] = value
    return result


def parse_json(data: bytes, source: str) -> dict:
    try:
        result = json.loads(data.decode("utf-8-sig"), object_pairs_hook=_unique_object,
                            parse_constant=lambda value: (_ for _ in ()).throw(AuditError(f"Non-finite JSON: {value}")))
    except (UnicodeError, json.JSONDecodeError, RecursionError) as exc:
        raise AuditError(f"Invalid JSON in {source}: {exc}") from exc
    if not isinstance(result, dict):
        raise AuditError(f"Expected JSON object in {source}")
    pending = [(result, 1)]
    while pending:
        value, depth = pending.pop()
        if depth > MAX_JSON_DEPTH:
            raise AuditError(f"JSON nesting budget exceeded: {source}")
        if isinstance(value, dict):
            pending.extend((x, depth + 1) for x in value.values())
        elif isinstance(value, list):
            pending.extend((x, depth + 1) for x in value)
    return result


def _boolean(value: dict, key: str, source: str) -> bool:
    item = value.get(key, False)
    if not isinstance(item, bool):
        raise AuditError(f"{source}: {key} must be a boolean")
    return item


def patch_summary(patch: dict, path: str, digest: str) -> dict:
    uid = patch.get("uid", patch.get("fileId"))
    if not isinstance(uid, str) or not uid:
        raise AuditError(f"Patch has no uid/fileId: {path}")
    if "uid" in patch and "fileId" in patch and patch["uid"] != patch["fileId"]:
        raise AuditError(f"Conflicting uid/fileId: {path}")
    majors = patch.get("compatibleJavaMajors", [])
    if not isinstance(majors, list) or any(type(x) is not int or x < 1 for x in majors):
        raise AuditError(f"Invalid compatibleJavaMajors: {path}")
    libraries = []
    for field in ("libraries", "+libraries", "mavenFiles"):
        values = patch.get(field, [])
        if not isinstance(values, list) or any(not isinstance(x, dict) for x in values):
            raise AuditError(f"Invalid {field} array: {path}")
        for lib in values:
            libraries.append({"field": field, "name": lib.get("name"),
                              "local": lib.get("MMC-hint") == "local",
                              "filename": lib.get("MMC-filename"),
                              "hasRules": "rules" in lib, "hasNatives": "natives" in lib})
    traits = []
    for field in ("traits", "+traits"):
        values = patch.get(field, [])
        if not isinstance(values, list) or any(not isinstance(x, str) for x in values):
            raise AuditError(f"Invalid {field}: {path}")
        traits.extend(values)
    jvm = patch.get("+jvmArgs", [])
    if not isinstance(jvm, list) or any(not isinstance(x, str) for x in jvm):
        raise AuditError(f"Invalid +jvmArgs: {path}")
    return {
        "path": path, "sha256": digest, "uid": uid, "version": patch.get("version"),
        "formatVersion": patch.get("formatVersion"), "fields": list(patch),
        "mainClass": patch.get("mainClass"), "mainJar": patch.get("mainJar"),
        "compatibleJavaMajors": majors, "compatibleJavaName": patch.get("compatibleJavaName"),
        "jvmArgumentCount": len(jvm), "traits": traits,
        "libraries": libraries,
        "unregisteredFields": sorted(set(patch) - PATCH_FIELDS),
        "requiresUnimplementedExecutor": sorted(x for x in UNSUPPORTED_ACTIONS if patch.get(x)),
        "unregisteredOrUnsupportedTraits": sorted(set(traits) - KNOWN_TRAITS),
    }


def audit_archive(source: Path, source_url: str | None = None) -> dict:
    source = Path(source)
    report = {
        "schemaVersion": 1, "auditStatus": "rejected", "compatibilityStatus": "not-evaluated",
        "source": {"fileName": source.name, "url": source_url, "completeLocalFile": True},
        "scope": "Full local file SHA256 and bounded metadata inventory; no extraction, install, repair or game launch.",
        "pathCaseProfile": {"name": PATH_CASE_PROFILE, "pythonUnicodeVersion": unicodedata.unidata_version,
                            "productionAuthority": False, "normalization": "none",
                            "scope": "Restricted audit comparison; production Core uses .NET OrdinalIgnoreCase and host path validation."},
        "diagnostics": [],
    }
    with evidence_on_failure(report), source.open("rb") as stream:
        before = os.fstat(stream.fileno())
        report["source"].update(sizeBytes=before.st_size, sha256=hashlib.file_digest(stream, "sha256").hexdigest())
        stream.seek(0)
        report["zipPreflight"] = preflight_zip(stream)
        with zipfile.ZipFile(stream) as archive:
            files, counts, directory_keys = index_archive(archive)
            report["archive"] = counts
            manifests = [x for x in files if target_key(x.rsplit("/", 1)[-1]) == "MMC-PACK.JSON"]
            roots = [x[:-len("mmc-pack.json")] for x in manifests]
            if len(roots) != 1:
                raise AuditError(f"Expected exactly one mmc-pack.json root, found {len(roots)}")
            root = roots[0]
            root_key = target_key(root)
            lookup = {target_key(path): path for path in files}
            relative_files = {x[len(root):]: info for x, info in files.items() if target_key(x).startswith(root_key)}
            relative_lookup = {target_key(path): path for path in relative_files}
            reserved_nodes = {"PCL/INSTANCEDEFINITION.JSON", "PCL/MMCSOURCE", "PCL/MMCOVERRIDES",
                              "PCL/MMCARTIFACTS", "PCL/MMCRUNTIME", "PCL/MMCTRANSACTIONS"}
            for path in relative_files:
                folded = target_key(path)
                if folded.startswith("PCL/MMC") or any(folded == node or folded.startswith(node + "/") for node in reserved_nodes):
                    raise AuditError(f"Package contains reserved LF management metadata: {path}")
            for key in directory_keys:
                if key.startswith(root_key):
                    relative_key = key[len(root_key):]
                    if relative_key.startswith("PCL/MMC") or any(relative_key == node or relative_key.startswith(node + "/") for node in reserved_nodes):
                        raise AuditError(f"Package contains reserved LF management directory: {relative_key}")
            manifest_path = manifests[0]
            cfg_path = lookup.get(root_key + "INSTANCE.CFG")
            selected = [manifest_path]
            if cfg_path is not None:
                selected.append(cfg_path)
            selected += [x for x in files if target_key(x).startswith(root_key + "PATCHES/") and target_key(x).endswith(".JSON")]
            total_metadata = 0
            metadata: dict[str, bytes] = {}
            for path in selected:
                info = files[path]
                if info.file_size > MAX_METADATA_BYTES:
                    raise AuditError(f"Metadata exceeds per-file budget: {path}")
                total_metadata += info.file_size
                if total_metadata > MAX_METADATA_TOTAL:
                    raise AuditError("Metadata exceeds aggregate byte budget")
                with archive.open(info) as content:
                    data = content.read(MAX_METADATA_BYTES + 1)
                    if len(data) != info.file_size or len(data) > MAX_METADATA_BYTES:
                        raise AuditError(f"Metadata actual size mismatch or budget exceeded: {path}")
                    metadata[path] = data
            manifest = parse_json(metadata[manifest_path], "mmc-pack.json")
            if type(manifest.get("formatVersion")) is not int or manifest["formatVersion"] != 1:
                raise AuditError("Unsupported mmc-pack formatVersion (expected integer 1)")
            components = manifest.get("components")
            if not isinstance(components, list) or not components or len(components) > MAX_COMPONENTS:
                raise AuditError("Invalid or over-budget components array")
            summaries = []
            uids: set[str] = set()
            for comp in components:
                if not isinstance(comp, dict) or not isinstance(comp.get("uid"), str) or not comp["uid"]:
                    raise AuditError("Component requires a nonempty uid")
                if comp["uid"] in uids:
                    raise AuditError(f"Duplicate component UID: {comp['uid']}")
                uids.add(comp["uid"])
                disabled = _boolean(comp, "disabled", comp["uid"])
                important = _boolean(comp, "important", comp["uid"])
                dependency = _boolean(comp, "dependencyOnly", comp["uid"])
                summaries.append({"uid": comp["uid"], "version": comp.get("version"),
                                  "cachedVersion": comp.get("cachedVersion"), "disabled": disabled,
                                  "important": important, "dependencyOnly": dependency,
                                  "effectiveEnabled": important or dependency or not disabled,
                                  "declaredFields": list(comp)})
            patches = []
            nested_patch_metadata = []
            patch_uids: set[str] = set()
            for path, data in metadata.items():
                if not target_key(path).startswith(root_key + "PATCHES/"):
                    continue
                relative_patch_path = path[len(root):]
                patch_leaf = relative_patch_path.split("/", 1)[1]
                if "/" in patch_leaf:
                    nested_patch_metadata.append({"path": relative_patch_path,
                                                  "sha256": hashlib.sha256(data).hexdigest(),
                                                  "reason": "Nested metadata is not a direct local component patch"})
                    continue
                summary = patch_summary(parse_json(data, path), path[len(root):], hashlib.sha256(data).hexdigest())
                if patch_leaf[:-5] != summary["uid"]:
                    raise AuditError(f"Local patch filename does not match uid/fileId: {relative_patch_path}")
                if summary["uid"] in patch_uids:
                    raise AuditError(f"Duplicate local patch UID: {summary['uid']}")
                patch_uids.add(summary["uid"])
                summary["referencedByManifest"] = summary["uid"] in uids
                patches.append(summary)
            trees = [name for name in ("minecraft", ".minecraft")
                     if root_key + target_key(name) in directory_keys]
            for name in ("minecraft", ".minecraft", "libraries", "patches"):
                if target_key(name) in relative_lookup:
                    raise AuditError(f"File occupies required directory role: {name}")
            report.update(
                auditStatus="metadata-audited", instanceRoot=root.rstrip("/"),
                gameRoot={"selected": "minecraft" if "minecraft" in trees else ".minecraft" if ".minecraft" in trees else "minecraft",
                          "presentTrees": trees, "bothTreesPreserved": len(trees) == 2},
                components=summaries, patches=patches,
                ignoredNestedPatchMetadata=nested_patch_metadata,
                metadata={"fileCount": len(metadata), "totalBytes": total_metadata,
                          "digests": {x[len(root):]: hashlib.sha256(data).hexdigest() for x, data in metadata.items()}},
                localLibraryFiles=[x for x in relative_files if target_key(x).startswith("LIBRARIES/")],
                gameLibraryFiles=[x for x in relative_files if any(target_key(x).startswith(target_key(t) + "/LIBRARIES/") for t in trees)],
                remoteMetadataRequired=[x["uid"] for x in summaries if x["uid"] not in patch_uids],
            )
            cfg = metadata.get(cfg_path)
            report["cfg"] = {"present": cfg is not None, "semanticsEvaluated": False}
            if cfg is not None:
                try:
                    cfg.decode("utf-8-sig")
                except UnicodeError as exc:
                    raise AuditError(f"instance.cfg is not UTF-8: {exc}") from exc
            report["diagnostics"].extend([
                "Known field names are inventoried, not proof that an LF executor supports them.",
                "Only selected metadata CRCs were read; payload CRCs, dependency resolution, cfg behavior and native JAR budgets remain unverified.",
                "A local SHA256 identifies these bytes; it is not an upstream authenticity signature.",
            ])
            if counts["directoryAliases"] or counts["repeatedDirectoryEntries"]:
                report["diagnostics"].append(
                    "Directory aliases share logical directory nodes; original spellings are retained above. Any colliding file target or file/directory role was rejected.")
        after = os.fstat(stream.fileno())
        if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
            raise AuditError("Archive changed while it was audited")
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("--source-url", help="Provenance only; no network requests are made")
    parser.add_argument("--output", type=Path, required=True, help="New JSON destination; existing files are never overwritten")
    args = parser.parse_args(argv)
    if args.archive.resolve() == args.output.resolve():
        parser.error("Report must not overwrite the input archive")
    if args.output.exists() or args.output.is_symlink():
        if args.output.exists() and args.archive.exists() and args.output.samefile(args.archive):
            parser.error("Report must not overwrite a hardlink or alias of the input archive")
        parser.error("Report already exists; choose a new output path")
    try:
        report = audit_archive(args.archive, args.source_url)
    except (AuditError, OSError, zipfile.BadZipFile, RuntimeError, NotImplementedError) as exc:
        report = getattr(exc, "audit_evidence", {"schemaVersion": 1, "compatibilityStatus": "not-evaluated", "diagnostics": []})
        report["auditStatus"] = "rejected"
        report["diagnostics"].append(str(exc))
        code = 1
    else:
        code = 0
    serialized = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    try:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        with args.output.open("x", encoding="utf-8") as destination:
            destination.write(serialized)
    except OSError as exc:
        parser.error(f"Cannot create a new report without overwriting an existing file: {exc}")
    print(json.dumps({"auditStatus": report["auditStatus"], "compatibilityStatus": "not-evaluated",
                      "output": str(args.output)}, ensure_ascii=False))
    return code


if __name__ == "__main__":
    sys.exit(main())
