#!/usr/bin/env python3
"""Offline MMC archive-audit regression tests using only LF-authored tiny ZIPs."""
from __future__ import annotations

import hashlib
from contextlib import redirect_stderr, nullcontext
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import warnings
import zipfile

SCRIPT = Path(__file__).resolve().parents[1] / "mmc" / "AuditArchive.py"
spec = importlib.util.spec_from_file_location("mmc_audit", SCRIPT)
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)
MANIFEST = {"formatVersion": 1, "components": [{"uid": "net.minecraft", "version": "1.7.10"}]}


class ArchiveTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="pcllf-mmc-audit-")
        self.root = Path(self.directory.name)

    def tearDown(self):
        self.directory.cleanup()

    def archive(self, files=None, manifest=MANIFEST, prefix=""):
        result = self.root / "sample.zip"
        with warnings.catch_warnings(), zipfile.ZipFile(result, "w", compression=zipfile.ZIP_DEFLATED) as out:
            warnings.simplefilter("ignore", UserWarning)
            if manifest is not None:
                out.writestr(prefix + "mmc-pack.json", json.dumps(manifest))
            for path, value in files or []:
                out.writestr(path, json.dumps(value) if isinstance(value, dict) else value)
        return result

    def test_nested_root_without_directory_entries_and_missing_cfg(self):
        result = self.archive([("Pack/.minecraft/config/test.cfg", "x")], prefix="Pack/")
        observed = audit.audit_archive(result)
        self.assertEqual("metadata-audited", observed["auditStatus"])
        self.assertEqual("not-evaluated", observed["compatibilityStatus"])
        self.assertEqual("Pack", observed["instanceRoot"])
        self.assertEqual(".minecraft", observed["gameRoot"]["selected"])
        self.assertFalse(observed["cfg"]["present"])
        self.assertEqual(hashlib.sha256(result.read_bytes()).hexdigest(), observed["source"]["sha256"])
        self.assertFalse(observed["archive"]["payloadCrcVerified"])

    def test_two_game_trees_and_two_library_scopes_do_not_merge(self):
        result = self.archive([
            ("minecraft/libraries/a.jar", b"game"), ("libraries/a.jar", b"launcher"),
            (".minecraft/saves/keep.txt", "other tree")])
        observed = audit.audit_archive(result)
        self.assertEqual("minecraft", observed["gameRoot"]["selected"])
        self.assertTrue(observed["gameRoot"]["bothTreesPreserved"])
        self.assertEqual(["libraries/a.jar"], observed["localLibraryFiles"])
        self.assertEqual(["minecraft/libraries/a.jar"], observed["gameLibraryFiles"])

    def test_metadata_observes_discrete_java_and_local_patch(self):
        manifest = {"formatVersion": 1, "components": [
            {"uid": "net.minecraft", "version": "1.7.10"},
            {"uid": "test.bootstrap", "version": "1", "disabled": True, "important": True}]}
        result = self.archive([("patches/test.bootstrap.json", {
            "formatVersion": 1, "uid": "test.bootstrap", "version": "2", "compatibleJavaMajors": [17, 21, 26],
            "mainClass": "example.Main", "+jvmArgs": ["-Dname=one token"],
            "+libraries": [{"name": "example:local:1", "MMC-hint": "local"}],
        })], manifest)
        observed = audit.audit_archive(result)
        self.assertTrue(observed["components"][1]["effectiveEnabled"])
        self.assertEqual([17, 21, 26], observed["patches"][0]["compatibleJavaMajors"])
        self.assertTrue(observed["patches"][0]["libraries"][0]["local"])
        self.assertEqual(["net.minecraft"], observed["remoteMetadataRequired"])

    def test_unknown_fields_and_executors_are_reported_not_claimed_supported(self):
        result = self.archive([("patches/example.json", {
            "fileId": "example", "newLaunchOperation": True, "jarMods": [{"name": "example:a:1"}],
            "+traits": ["legacyLaunch"],
        })])
        observed = audit.audit_archive(result)
        self.assertEqual(["newLaunchOperation"], observed["patches"][0]["unregisteredFields"])
        self.assertEqual(["jarMods"], observed["patches"][0]["requiresUnimplementedExecutor"])
        self.assertEqual(["legacyLaunch"], observed["patches"][0]["unregisteredOrUnsupportedTraits"])
        self.assertFalse(observed["patches"][0]["referencedByManifest"])

    def test_ambiguous_roots_rejected(self):
        with self.assertRaisesRegex(audit.AuditError, "exactly one"):
            audit.audit_archive(self.archive([("Other/mmc-pack.json", MANIFEST)]))

    def test_no_root_rejected(self):
        with self.assertRaisesRegex(audit.AuditError, "exactly one"):
            audit.audit_archive(self.archive([("readme.txt", "nothing")], manifest=None))

    def test_unsafe_paths_rejected(self):
        for name in ("../escape", "/rooted", "C:/drive", "safe/../escape", "safe\\..\\escape",
                     "safe//empty", "safe/./dot", "file:stream", "NUL.txt", "a/trailing.", "a/trailing ",
                     "COM¹.log", "safe/lpt²", "LPT³.cfg", "CONIN$", "conout$.txt"):
            with self.subTest(name=name), self.assertRaises(audit.AuditError):
                audit.audit_archive(self.archive([(name, "x")]))

    def test_case_collisions_and_role_collisions_rejected(self):
        for files in ([('a.jar', 'a'), ('A.jar', 'b')], [('Mods/a', 'a'), ('mods/A', 'b')],
                      [('folder', 'a'), ('folder/file', 'b')], [('same', 'a'), ('same', 'b')]):
            with self.subTest(files=files), self.assertRaises(audit.AuditError):
                audit.audit_archive(self.archive(files))

    def test_directory_aliases_preserve_both_disjoint_file_sources(self):
        observed = audit.audit_archive(self.archive([
            ('minecraft/Mods/', ''), ('minecraft/mods/', ''),
            ('minecraft/Mods/a', 'a'), ('minecraft/mods/b', 'b')]))
        self.assertEqual("metadata-audited", observed["auditStatus"])
        self.assertEqual([["minecraft/Mods", "minecraft/mods"]], observed["archive"]["directoryAliases"])
        self.assertEqual(3, observed["archive"]["fileCount"])

    def test_same_bytes_file_alias_still_rejected(self):
        with self.assertRaisesRegex(audit.AuditError, "file target"):
            audit.audit_archive(self.archive([('Mods/a', 'same'), ('mods/A', 'same')]))

    def test_metadata_lookup_uses_target_key_and_preserves_source_spelling(self):
        observed = audit.audit_archive(self.archive([
            ('pack/Instance.CFG', 'name=Mixed'), ('PACK/Patches/x.JSON', {'uid': 'x'}),
            ('pack/MineCraft/a', 'game'), ('PACK/Libraries/a.jar', 'local')], prefix='Pack/'))
        self.assertEqual('Pack', observed['instanceRoot'])
        self.assertTrue(observed['cfg']['present'])
        self.assertEqual(['Patches/x.JSON'], [item['path'] for item in observed['patches']])
        self.assertEqual(['Libraries/a.jar'], observed['localLibraryFiles'])
        self.assertEqual('minecraft', observed['gameRoot']['selected'])

    def test_mixed_case_manifest_and_directory_role_collision(self):
        observed = audit.audit_archive(self.archive([('Pack/MMC-Pack.JSON', MANIFEST)], manifest=None))
        self.assertEqual('Pack', observed['instanceRoot'])
        for path in ('Minecraft', '.MineCraft', 'PATCHES', 'Libraries'):
            with self.subTest(path=path), self.assertRaisesRegex(audit.AuditError, 'directory role'):
                audit.audit_archive(self.archive([(path, 'file')]))

    def test_unicode_profile_uses_fixed_dotnet_oracle_vectors_without_normalization(self):
        # Values checked against .NET 10.0.14 StringComparison.OrdinalIgnoreCase.
        for left, right, equal in [('ß', 'ss', False), ('ß', 'ẞ', False), ('ı', 'I', False),
                                   ('ſ', 'S', False), ('K', 'K', False), ('σ', 'ς', True),
                                   ('é', 'e\u0301', False), ('ﬃ', 'ffi', False), ('µ', 'Μ', True)]:
            with self.subTest(left=left, right=right):
                self.assertEqual(equal, audit.target_key(left) == audit.target_key(right))
        observed = audit.audit_archive(self.archive([('minecraft/ß', 'one'), ('minecraft/ss', 'two'),
                                                     ('minecraft/é', 'three'), ('minecraft/e\u0301', 'four')]))
        self.assertEqual(audit.PATH_CASE_PROFILE, observed['pathCaseProfile']['name'])
        self.assertFalse(observed['pathCaseProfile']['productionAuthority'])

    def test_unknown_expanding_unicode_case_is_explicitly_unsupported(self):
        with self.assertRaisesRegex(audit.AuditError, 'Unsupported path casing'):
            audit.audit_archive(self.archive([('minecraft/ᾳ', 'unknown simple uppercase mapping')]))

    def test_deep_zero_byte_path_rejected_before_prefix_tree_growth(self):
        with self.assertRaisesRegex(audit.AuditError, 'depth budget'):
            audit.audit_archive(self.archive([('a/' * 129 + 'empty', b'')]))

    def test_single_path_segment_and_character_budgets(self):
        with self.assertRaisesRegex(audit.AuditError, 'segment exceeds'):
            audit.audit_archive(self.archive([('a' * 256, b'')]))
        with patch.object(audit, 'MAX_PATH_CHARS', 40), self.assertRaisesRegex(audit.AuditError, 'length budget'):
            audit.audit_archive(self.archive([('a/' * 21 + 'end', b'')]))
        with patch.object(audit, 'MAX_PATH_INPUT_CHARS', 30), self.assertRaisesRegex(audit.AuditError, 'input path character'):
            audit.audit_archive(self.archive([('path-one', b''), ('path-two', b''), ('path-three', b'')]))

    def test_target_node_count_and_cumulative_prefix_character_budget(self):
        result = self.archive([('folder/subfolder/empty', b'')])
        with patch.object(audit, 'MAX_PATH_NODES', 2), self.assertRaisesRegex(audit.AuditError, 'node count'):
            audit.audit_archive(result)
        with patch.object(audit, 'MAX_PATH_NODE_CHARS', 35), self.assertRaisesRegex(audit.AuditError, 'node character'):
            audit.audit_archive(result)

    def test_directory_role_is_not_flat_file(self):
        for name in ("minecraft", ".minecraft", "libraries", "patches"):
            with self.subTest(name=name), self.assertRaisesRegex(audit.AuditError, "directory role"):
                audit.audit_archive(self.archive([(name, "conflict")]))

    def test_symlink_rejected(self):
        link = zipfile.ZipInfo("outside")
        link.create_system = 3
        link.external_attr = (stat.S_IFLNK | 0o777) << 16
        result = self.archive([(link, "../target")])
        with self.assertRaisesRegex(audit.AuditError, "Symbolic-link"):
            audit.audit_archive(result)

    def test_reserved_lf_management_metadata_rejected(self):
        for name in ("PCL/InstanceDefinition.json", "PCL/MmcSource/x/file", "PCL/MmcTransactions/x/journal",
                     "PCL/MmcSource", "PCL/MmcOverrides", "PCL/MmcRuntime/file", "pcl/mmcartifacts", "PCL/MmcFuture"):
            with self.subTest(name=name), self.assertRaisesRegex(audit.AuditError, "reserved"):
                audit.audit_archive(self.archive([(name, "injected")]))

    def test_empty_reserved_management_directory_is_not_accepted(self):
        with self.assertRaisesRegex(audit.AuditError, "reserved"):
            audit.audit_archive(self.archive([('PCL/MmcRuntime/', '')]))

    def test_duplicate_json_fields_rejected(self):
        with self.assertRaisesRegex(audit.AuditError, "Duplicate JSON"):
            audit.audit_archive(self.archive([("mmc-pack.json", '{"formatVersion":1,"formatVersion":1}')], manifest=None))

    def test_invalid_manifest_schema_and_flags_rejected(self):
        for manifest in ({"formatVersion": True, "components": [{}]}, {"formatVersion": 2, "components": [{}]},
                         {"formatVersion": 1, "components": []},
                         {"formatVersion": 1, "components": [{"uid": "x", "disabled": "false"}]},
                         {"formatVersion": 1, "components": [{"uid": "x"}, {"uid": "x"}]}):
            with self.subTest(manifest=manifest), self.assertRaises(audit.AuditError):
                audit.audit_archive(self.archive(manifest=manifest))

    def test_invalid_patch_types_rejected(self):
        for prop, value in (("compatibleJavaMajors", [True]), ("compatibleJavaMajors", [0]),
                            ("+traits", "legacyLaunch"), ("+jvmArgs", [2]), ("libraries", ["bad"])):
            with self.subTest(prop=prop, value=value), self.assertRaises(audit.AuditError):
                audit.audit_archive(self.archive([("patches/x.json", {"uid": "x", prop: value})]))

    def test_patch_filename_uid_and_disagreeing_alias_rejected(self):
        with self.assertRaisesRegex(audit.AuditError, "filename does not match"):
            audit.audit_archive(self.archive([("patches/wrong.json", {"uid": "dep"})]))
        with self.assertRaisesRegex(audit.AuditError, "filename does not match"):
            audit.audit_archive(self.archive([("patches/Dep.json", {"uid": "dep"})]))
        with self.assertRaisesRegex(audit.AuditError, "Conflicting uid"):
            audit.audit_archive(self.archive([("patches/a.json", {"uid": "x", "fileId": "y"})]))

    def test_nested_backup_patch_is_inventoried_but_cannot_supply_component(self):
        manifest = {'formatVersion': 1, 'components': [{'uid': 'dep', 'version': '1'}]}
        observed = audit.audit_archive(self.archive([('patches/backup/dep.json', {'uid': 'dep', 'version': '1'})], manifest))
        self.assertEqual([], observed['patches'])
        self.assertEqual(['dep'], observed['remoteMetadataRequired'])
        self.assertEqual('patches/backup/dep.json', observed['ignoredNestedPatchMetadata'][0]['path'])

    def test_single_metadata_and_aggregate_limits_are_bounded(self):
        result = self.archive([("instance.cfg", "name=" + "a" * 50)])
        with patch.object(audit, "MAX_METADATA_BYTES", 20), self.assertRaisesRegex(audit.AuditError, "per-file"):
            audit.audit_archive(result)
        with patch.object(audit, "MAX_METADATA_TOTAL", 100), self.assertRaisesRegex(audit.AuditError, "aggregate"):
            audit.audit_archive(result)

    def test_entry_count_and_declared_total_budget(self):
        result = self.archive([("minecraft/a", "a" * 200)])
        with patch.object(audit, "MAX_ENTRIES", 1), self.assertRaisesRegex(audit.AuditError, "entry count"):
            audit.audit_archive(result)
        with patch.object(audit, "MAX_TOTAL_BYTES", 100), self.assertRaisesRegex(audit.AuditError, "total budget"):
            audit.audit_archive(result)

    def test_json_depth_nonfinite_and_text_encoding(self):
        for data in (b'{"bad":NaN}', b'\xff', b'[]', b'{"nested":[[[[0]]]]}'):
            with self.subTest(data=data), patch.object(audit, "MAX_JSON_DEPTH", 3), self.assertRaises(audit.AuditError):
                audit.parse_json(data, "test")

    def test_cfg_bom_and_first_line_are_preserved_not_executed(self):
        result = self.archive([("instance.cfg", '\ufeffname=First Line\r\nOverrideCommands=true\r\nPreLaunchCommand=never run\r\n')])
        observed = audit.audit_archive(result)
        self.assertTrue(observed["cfg"]["present"])
        self.assertFalse(observed["cfg"]["semanticsEvaluated"])

    def test_cli_rejects_same_output_without_modifying_zip(self):
        source = self.archive()
        original = source.read_bytes()
        completed = subprocess.run([sys.executable, str(SCRIPT), str(source), "--output", str(source)], capture_output=True)
        self.assertEqual(2, completed.returncode)
        self.assertEqual(original, source.read_bytes())

    def test_cli_rejects_hardlink_output_without_modifying_zip(self):
        source = self.archive()
        original = source.read_bytes()
        alias = self.root / 'alias-report.json'
        os.link(source, alias)
        completed = subprocess.run([sys.executable, str(SCRIPT), str(source), '--output', str(alias)], capture_output=True)
        self.assertEqual(2, completed.returncode)
        self.assertIn(b'hardlink', completed.stderr)
        self.assertEqual(original, source.read_bytes())
        self.assertEqual(original, alias.read_bytes())

    def test_cli_preserves_existing_unrelated_output(self):
        source = self.archive()
        output = self.root / 'already-exists.json'
        output.write_bytes(b'preserve existing user data')
        completed = subprocess.run([sys.executable, str(SCRIPT), str(source), '--output', str(output)], capture_output=True)
        self.assertEqual(2, completed.returncode)
        self.assertEqual(b'preserve existing user data', output.read_bytes())

    def test_cli_output_created_after_audit_is_not_overwritten(self):
        source = self.archive()
        output = self.root / 'concurrent-report.json'
        run_audit = audit.audit_archive

        def concurrent_writer(*args, **kwargs):
            report = run_audit(*args, **kwargs)
            output.write_bytes(b'concurrent owner')
            return report

        with patch.object(audit, 'audit_archive', concurrent_writer), redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as observed:
                audit.main([str(source), '--output', str(output)])
        self.assertEqual(2, observed.exception.code)
        self.assertEqual(b'concurrent owner', output.read_bytes())

    def test_cli_valid_and_rejected_reports(self):
        for files, expected in (([], 0), ([("../bad", "bad")], 1)):
            with self.subTest(expected=expected):
                source = self.archive(files)
                report = self.root / "evidence" / f"audit-{expected}.json"
                completed = subprocess.run([sys.executable, str(SCRIPT), str(source), "--output", str(report)], capture_output=True)
                self.assertEqual(expected, completed.returncode, completed.stderr)
                observed = json.loads(report.read_text(encoding="utf-8"))
                self.assertEqual("not-evaluated", observed["compatibilityStatus"])
                self.assertEqual("rejected" if expected else "metadata-audited", observed["auditStatus"])

    def test_cli_unknown_unicode_mapping_reports_rejected_profile(self):
        source = self.archive([('minecraft/ᾳ', 'unknown casing')])
        output = self.root / 'unsupported-unicode.json'
        completed = subprocess.run([sys.executable, str(SCRIPT), str(source), '--output', str(output)], capture_output=True)
        self.assertEqual(1, completed.returncode)
        observed = json.loads(output.read_text(encoding='utf-8'))
        self.assertEqual('rejected', observed['auditStatus'])
        self.assertEqual(audit.PATH_CASE_PROFILE, observed['pathCaseProfile']['name'])
        self.assertTrue(any('Unsupported path casing' in x for x in observed['diagnostics']))

    def assert_preflight_rejected_before_index(self, source_bytes, reason, **limits):
        source = self.root / 'malformed.zip'
        source.write_bytes(source_bytes)
        with (patch.multiple(audit, **limits) if limits else nullcontext()), patch.object(audit.zipfile, 'ZipFile', side_effect=AssertionError('ZipFile constructor must not run')):
            with self.assertRaisesRegex(audit.AuditError, reason):
                audit.audit_archive(source)

    @staticmethod
    def zip64_bytes(classic, payload_bytes=44, disk_total=1):
        end = struct.unpack('<4s4H2LH', classic[-22:])
        count, central_bytes, central_offset = end[4:7]
        zip64_offset = len(classic) - 22
        record = struct.pack('<4sQ2H2L4Q', b'PK\x06\x06', payload_bytes, 45, 45, 0, 0,
                             count, count, central_bytes, central_offset)
        locator = struct.pack('<4sLQL', b'PK\x06\x07', 0, zip64_offset, disk_total)
        end64 = struct.pack('<4s4H2LH', b'PK\x05\x06', 0, 0, 0xffff, 0xffff, 0xffffffff, 0xffffffff, 0)
        return classic[:-22] + record + locator + end64

    def test_preflight_valid_classic_and_fixed_zip64(self):
        source = self.archive()
        classic = source.read_bytes()
        observed = audit.audit_archive(source)
        self.assertFalse(observed['zipPreflight']['zip64'])
        self.assertEqual(1, observed['zipPreflight']['scannedEntryCount'])
        source.write_bytes(self.zip64_bytes(classic))
        observed = audit.audit_archive(source)
        self.assertTrue(observed['zipPreflight']['zip64'])
        self.assertEqual('metadata-audited', observed['auditStatus'])

    def test_preflight_central_byte_budget_before_zipfile_allocation(self):
        source = self.archive()
        self.assert_preflight_rejected_before_index(source.read_bytes(), 'central-directory byte budget', MAX_CENTRAL_DIRECTORY_BYTES=1)

    def test_preflight_does_not_trust_underdeclared_entry_count(self):
        data = bytearray(self.archive([('a', '')]).read_bytes())
        struct.pack_into('<HH', data, len(data) - 22 + 8, 1, 1)
        self.assert_preflight_rejected_before_index(data, 'scanned entry count', MAX_ENTRIES=1)

    def test_preflight_malformed_eocd_multidisk_and_variable_header(self):
        classic = self.archive().read_bytes()
        self.assert_preflight_rejected_before_index(classic[:-10], 'end-of-central-directory')
        self.assert_preflight_rejected_before_index(classic + b'trailing', 'trailing data')
        multidisk = bytearray(classic)
        struct.pack_into('<H', multidisk, len(multidisk) - 22 + 4, 1)
        self.assert_preflight_rejected_before_index(multidisk, 'Multi-disk')
        end = struct.unpack('<4s4H2LH', classic[-22:])
        truncated = bytearray(classic)
        struct.pack_into('<H', truncated, end[6] + 28, 0xffff)
        self.assert_preflight_rejected_before_index(truncated, 'variable fields')

    def test_preflight_zip64_unsupported_records_before_zipfile_allocation(self):
        classic = self.archive().read_bytes()
        self.assert_preflight_rejected_before_index(self.zip64_bytes(classic, payload_bytes=45), 'ZIP64 end record size')
        self.assert_preflight_rejected_before_index(self.zip64_bytes(classic, disk_total=2), 'Multi-disk ZIP64')
        bad_locator = bytearray(self.zip64_bytes(classic))
        struct.pack_into('<Q', bad_locator, len(bad_locator) - 22 - 20 + 8, 0)
        self.assert_preflight_rejected_before_index(bad_locator, 'locator offset')
        sentinel = bytearray(classic)
        struct.pack_into('<HH', sentinel, len(sentinel) - 22 + 8, 0xffff, 0xffff)
        self.assert_preflight_rejected_before_index(sentinel, 'ZIP64 sentinel')


if __name__ == "__main__":
    unittest.main(verbosity=2)
