import copy
from contextlib import redirect_stderr
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import runpy
import sys
import tempfile
import time
from types import SimpleNamespace
import unittest
from unittest import mock
from urllib.error import HTTPError

spec = importlib.util.spec_from_file_location("cnb_mirror", Path(__file__).with_name("Sync-CnbRelease.py"))
mirror = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mirror)


class Response(io.BytesIO):
    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()


class FakeGit:
    stable = b'{"tag_name":"v1.3.4","prerelease":false}\n'

    def __init__(self, preview=None):
        self.staged = []
        self.preview = preview

    def run(self, *arguments, cwd=None, optional=False):
        if arguments[0] == "ls-remote":
            return SimpleNamespace(stdout="stable refs/heads/updates\n", returncode=0)
        if arguments[0] == "clone":
            folder = Path(arguments[-1])
            folder.mkdir()
            (folder / "latest.json").write_bytes(self.stable)
            if self.preview:
                (folder / "latest-preview.json").write_text(json.dumps(self.preview), encoding="utf-8")
        if arguments[0] == "add":
            self.staged = list(arguments[2:])
        return SimpleNamespace(stdout="", returncode=1 if arguments[0] == "diff" else 0)


class FakeHttp(mirror.Http):
    def __init__(self, tag="v1.3.4", prerelease=False):
        super().__init__("synthetic-cnb-token", mirror.CNB_REPO)
        self.base = "https://api.cnb.cool/" + mirror.CNB_REPO + "/-/releases"
        self.release = None
        self.calls = []
        self.pending = None
        self.corrupt_anonymous = False
        self.cached = None
        self.version_cached = None
        self.latest_source = {"tag_name": "v1.3.4", "draft": False, "prerelease": False} if prerelease else None
        self.contents = {}
        executable = "PCL-Living-Fork-" + tag[1:] + ".exe"
        archive = "PCL-Living-Fork-" + tag[1:] + "-win-x64.zip"
        data = {executable: b"synthetic public executable", archive: b"synthetic public archive"}
        data["SHA256SUMS.txt"] = "".join(hashlib.sha256(data[name]).hexdigest() + "  " + name + "\n" for name in (executable, archive)).encode()
        assets = []
        for name, value in data.items():
            url = "https://github.com/" + mirror.GITHUB_REPO + "/releases/download/" + tag + "/" + name
            self.contents[url] = value
            assets.append({"name": name, "size": len(value), "digest": "sha256:" + hashlib.sha256(value).hexdigest(), "browser_download_url": url})
        self.source = {"tag_name": tag, "name": "PCL Living Fork " + tag[1:], "body": "source notes", "draft": False, "prerelease": prerelease, "assets": assets}

    def json(self, url, method="GET", data=None, auth=False, missing=False, *, step=""):
        self.calls.append((method, url, auth))
        if url == mirror.GITHUB_API + "/releases/latest":
            assert not auth
            return copy.deepcopy(self.latest_source or self.source)
        if url == mirror.GITHUB_API + "/releases/tags/" + self.source["tag_name"]:
            assert not auth
            return copy.deepcopy(self.source)
        if "/-/git/raw/updates/releases/" in url:
            assert not auth
            return copy.deepcopy(self.version_cached)
        if url.endswith("/-/git/raw/updates/latest.json"):
            assert not auth
            return copy.deepcopy(self.cached)
        assert auth
        if method == "POST" and url == self.base:
            self.release = dict(data, id="fixture-id", assets=[])
        elif url.endswith("/asset-upload-url"):
            self.pending = data
            return {"upload_url": "https://storage.example.invalid/upload?signature=synthetic", "verify_url": self.base + "/fixture-id/asset-upload-confirmation/synthetic/file"}
        elif "/asset-upload-confirmation/" in url:
            assert "ttl=0" in url
            name = self.pending["asset_name"]
            download = "https://cnb.cool/" + mirror.CNB_REPO + "/-/releases/download/" + self.source["tag_name"] + "/" + name
            self.contents[download] = self.uploaded
            if self.pending.get("overwrite"):
                self.release["assets"] = [asset for asset in self.release["assets"] if asset["name"] != name]
            self.release["assets"].append({"name": name, "size": len(self.uploaded), "hash_algo": "sha256", "hash_value": hashlib.sha256(self.uploaded).hexdigest(), "browser_download_url": download})
        elif method == "PATCH":
            self.release.update(data)
        return copy.deepcopy(self.release)

    def open(self, url, method="GET", data=None, auth=False, headers=None):
        self.calls.append((method, url, auth))
        if method == "PUT":
            assert not auth
            self.uploaded = data.read()
            return Response(b"")
        assert not auth
        value = self.contents[url]
        if self.corrupt_anonymous and url.startswith("https://cnb.cool/"):
            value = b"corrupt"
        return Response(value)


class MirrorHttpTests(unittest.TestCase):
    github_latest = mirror.GITHUB_API + "/releases/latest"
    cnb_releases = "https://api.cnb.cool/" + mirror.CNB_REPO + "/-/releases"

    def setUp(self):
        self.http = mirror.Http("synthetic-cnb-secret", mirror.CNB_REPO)
        self.http.opener = mock.Mock()
        sleep = mock.patch.object(time, "sleep")
        self.sleep = sleep.start()
        self.addCleanup(sleep.stop)

    @staticmethod
    def error(url, status, headers=None, body=b""):
        return HTTPError(url, status, "synthetic-untrusted-error", headers or {}, Response(body))

    def test_plain_403_after_redirect_names_step_and_cdn(self):
        source = "https://github.com/" + mirror.GITHUB_REPO + "/releases/download/v1.3.4/SHA256SUMS.txt"
        redirected = "https://release-assets.githubusercontent.com/synthetic-signed-path?sig=synthetic-signed-query"
        self.http.opener.open.side_effect = self.error(redirected, 403)
        with tempfile.TemporaryDirectory() as folder, self.assertRaisesRegex(
            RuntimeError, r"Fetch checksum.*GET .*releases/download.*release-assets\.githubusercontent\.com.*HTTP 403"
        ) as result:
            self.http.download(source, Path(folder) / "checksum", 1, "a" * 64, step="Fetch checksum")
        self.assertNotIn("synthetic-", str(result.exception))
        self.http.opener.open.assert_called_once()
        self.sleep.assert_not_called()

    def test_signed_upload_and_confirmation_report_safe_routes(self):
        upload = "https://cnb.cool/synthetic-signed-path?signature=synthetic-signed-query"
        confirm = self.cnb_releases + "/fixture-id/asset-upload-confirmation/synthetic-confirmation-token/synthetic-object-path?ttl=0"
        errors = (self.error(upload, 403), self.error(confirm, 403, body=b"synthetic-cnb-secret"))
        self.http.opener.open.side_effect = errors
        with self.assertRaisesRegex(RuntimeError, r"Upload fixture.*PUT CNB attachment storage.*HTTP 403") as result:
            self.http.upload(upload, io.BytesIO(b"x"), 1, step="Upload fixture")
        self.assertNotIn("synthetic-", str(result.exception))
        with self.assertRaisesRegex(RuntimeError, r"Confirm fixture.*POST .*asset-upload-confirmation/\{redacted\}.*HTTP 403") as result:
            self.http.json(confirm, "POST", auth=True, step="Confirm fixture")
        self.assertNotIn("synthetic-", str(result.exception))
        self.assertNotIn("ttl=", str(result.exception))
        self.sleep.assert_not_called()

    def test_github_403_respects_rate_limit_reset(self):
        self.http.opener.open.side_effect = [
            self.error(self.github_latest, 403, {"X-RateLimit-Remaining": "0", "X-RateLimit-Reset": "1007"}),
            Response(b'{"tag_name":"v1.3.4"}'),
        ]
        logs = io.StringIO()
        with mock.patch.object(time, "time", return_value=1000), redirect_stderr(logs):
            self.assertEqual(self.http.json(self.github_latest)["tag_name"], "v1.3.4")
        self.sleep.assert_called_once_with(7)
        self.assertRegex(logs.getvalue(), r"Mirror retry: .*GET .*releases/latest.*HTTP 403.*rate limit")

    def test_github_403_secondary_rate_limit_waits_at_least_a_minute(self):
        self.http.opener.open.side_effect = [
            self.error(self.github_latest, 403, body=b'{"message":"You have exceeded a secondary rate limit."}'),
            Response(b'{"tag_name":"v1.3.4"}'),
        ]
        with redirect_stderr(io.StringIO()):
            self.assertEqual(self.http.json(self.github_latest)["tag_name"], "v1.3.4")
        self.sleep.assert_called_once_with(60)

    def test_429_respects_retry_after_and_declines_wait_beyond_budget(self):
        self.http.opener.open.side_effect = [
            self.error(self.github_latest, 429, {"Retry-After": "Thu, 01 Jan 1970 00:16:46 GMT"}),
            self.error(self.github_latest, 429, {"Retry-After": "120"}),
        ]
        with mock.patch.object(time, "time", return_value=1000), redirect_stderr(io.StringIO()):
            with self.assertRaisesRegex(RuntimeError, r"HTTP 429.*retry wait exceeds"):
                self.http.json(self.github_latest)
        self.sleep.assert_called_once_with(6)
        self.assertEqual(self.http.opener.open.call_count, 2)

    def test_unclassified_403_and_cnb_writes_are_single_attempt(self):
        for method, url, status in (
            ("GET", self.github_latest, 403),
            ("GET", self.cnb_releases + "/tags/v1.3.4", 403),
            ("POST", self.cnb_releases, 503),
            ("PATCH", self.cnb_releases + "/1", 429),
        ):
            with self.subTest(method=method, url=url):
                self.http.opener.open.reset_mock()
                self.http.opener.open.side_effect = self.error(url, status)
                with self.assertRaisesRegex(RuntimeError, f"HTTP {status}"):
                    self.http.json(url, method, auth=url.startswith(self.cnb_releases))
                self.http.opener.open.assert_called_once()
        self.http.opener.open.reset_mock()
        upload = "https://storage.example.invalid/upload?signature=synthetic"
        self.http.opener.open.side_effect = self.error(upload, 503)
        with self.assertRaisesRegex(RuntimeError, r"HTTP 503"):
            self.http.upload(upload, io.BytesIO(b"x"), 1, step="Upload fixture")
        self.http.opener.open.assert_called_once()
        self.sleep.assert_not_called()

    def test_gateway_error_has_a_bounded_attempt_count(self):
        self.http.opener.open.side_effect = [self.error(self.github_latest, 503) for _ in range(3)]
        logs = io.StringIO()
        with redirect_stderr(logs), self.assertRaisesRegex(RuntimeError, r"HTTP 503"):
            self.http.json(self.github_latest)
        self.assertEqual(self.http.opener.open.call_count, 3)
        self.assertEqual(self.sleep.call_args_list, [mock.call(1), mock.call(2)])
        self.assertEqual(logs.getvalue().count("Mirror retry:"), 2)

    def test_download_restarts_after_interrupted_body(self):
        class InterruptedResponse(Response):
            def read(self, size):
                if self.tell():
                    raise ConnectionResetError("synthetic-untrusted-error")
                return super().read(3)

        data = b"complete attachment"
        self.http.opener.open.side_effect = [InterruptedResponse(data), Response(data)]
        with tempfile.TemporaryDirectory() as folder, redirect_stderr(io.StringIO()) as logs:
            target = Path(folder) / "asset"
            self.http.download(self.github_latest, target, len(data), hashlib.sha256(data).hexdigest())
            self.assertEqual(target.read_bytes(), data)
        self.assertEqual(self.http.opener.open.call_count, 2)
        self.assertNotIn("synthetic-untrusted-error", logs.getvalue())

    def test_missing_release_is_still_an_optional_404(self):
        url = self.cnb_releases + "/tags/v1.3.4"
        self.http.opener.open.side_effect = self.error(url, 404)
        self.assertIsNone(self.http.json(url, auth=True, missing=True))
        self.http.opener.open.assert_called_once()
        self.sleep.assert_not_called()

    def test_cli_hides_unexpected_errors(self):
        logs = io.StringIO()
        with (
            mock.patch("urllib.request.OpenerDirector.open", side_effect=RuntimeError("synthetic-secret?signature=synthetic")),
            mock.patch.dict(os.environ, {"CNB_TOKEN": "synthetic-secret"}),
            mock.patch.object(sys, "argv", ["mirror.py"]),
            redirect_stderr(logs),
            self.assertRaises(SystemExit) as result,
        ):
            runpy.run_path(mirror.__file__, run_name="__main__")
        self.assertEqual(result.exception.code, 1)
        self.assertEqual(logs.getvalue(), "Mirror failed: unexpected RuntimeError\n")

    def test_git_push_reports_status_without_raw_stderr(self):
        result = SimpleNamespace(returncode=128, stderr="fatal: signed-query: The requested URL returned error: 403")
        with tempfile.TemporaryDirectory() as folder, mock.patch.object(mirror.subprocess, "run", return_value=result):
            git = mirror.Git(folder, "synthetic-secret")
            with self.assertRaisesRegex(RuntimeError, r"Git push.*cnb\.cool.*HTTP 403") as failure:
                git.run("push", "origin", "HEAD:updates")
        self.assertNotIn("signed-query", str(failure.exception))


class MirrorTests(unittest.TestCase):
    def test_manifest_conflicts(self):
        self.assertEqual(mirror.manifest_hash("a" * 64 + "  a.exe\n", "a.exe"), "a" * 64)
        with self.assertRaises(RuntimeError):
            mirror.manifest_hash("a" * 64 + "  a.exe\n" + "b" * 64 + "  a.exe\n", "a.exe")

    def test_source_is_own_published_release(self):
        http = FakeHttp()
        self.assertEqual(len(mirror.source_files(http.source)), 3)
        for field, value in (("draft", True), ("prerelease", "true"), ("prerelease", None), ("tag_name", "v1.3.4-pre-"), ("tag_name", "v1.3.4-beta")):
            bad = copy.deepcopy(http.source)
            bad[field] = value
            with self.assertRaises(RuntimeError):
                mirror.source_files(bad)
        bad = copy.deepcopy(http.source)
        bad["assets"][0]["browser_download_url"] = "https://github.com/other/repo/releases/download/v1.3.4/a.exe"
        with self.assertRaises(RuntimeError):
            mirror.source_files(bad)

    def test_prerelease_is_mirrored_without_promotion_and_reuses_its_own_index(self):
        http = FakeHttp("v1.3.5-pre-1", True)
        source, latest = mirror.select_release(http, "v1.3.5-pre-1")
        self.assertFalse(latest)
        with tempfile.TemporaryDirectory(prefix="pcl-cnb-test-") as directory:
            result = mirror.sync(http, mirror.CNB_REPO, directory, source, latest=True)
        self.assertTrue(http.release["prerelease"])
        self.assertTrue(result["prerelease"])
        self.assertEqual(http.release["make_latest"], "false")
        self.assertEqual(mirror.release_form(source, True)["make_latest"], "false")
        self.assertTrue(all("v1.3.5-pre-1/" in item["browser_download_url"] for item in result["assets"]))
        http.version_cached = result
        http.calls.clear()
        with tempfile.TemporaryDirectory(prefix="pcl-cnb-test-") as directory:
            self.assertEqual(mirror.sync(http, mirror.CNB_REPO, directory, source), result)
        self.assertFalse(any(method == "PUT" or "/releases/download/" in url for method, url, _ in http.calls))
        self.assertFalse(any(url.endswith("/updates/latest.json") for _, url, _ in http.calls))

    def test_prerelease_index_keeps_stable_bytes_and_separate_receipt(self):
        http = FakeHttp("v1.3.5-pre-1", True)
        git = FakeGit()
        with tempfile.TemporaryDirectory(prefix="pcl-cnb-test-") as directory:
            index = mirror.sync(http, mirror.CNB_REPO, directory, http.source)
            mirror.publish_index(git, mirror.CNB_REPO, index, directory, False, latest=False, receipt=mirror.receipt_for(http.source))
            control = Path(directory) / "control"
            self.assertEqual((control / "latest.json").read_bytes(), git.stable)
            self.assertEqual(json.loads((control / "releases/v1.3.5-pre-1.json").read_text()), index)
            self.assertEqual(json.loads((control / "receipts/v1.3.5-pre-1.json").read_text()), mirror.receipt_for(http.source))
            self.assertNotIn("latest.json", git.staged)
            self.assertEqual(json.loads((control / "latest-preview.json").read_text()), index)
        with tempfile.TemporaryDirectory(prefix="pcl-cnb-test-") as directory:
            with self.assertRaisesRegex(RuntimeError, "cannot replace"):
                mirror.publish_index(None, mirror.CNB_REPO, index, directory, False, latest=True)

    def test_opt_in_feed_orders_prereleases_and_stable_without_downgrades(self):
        ordered = ("v1.3.4", "v1.3.5-pre-1", "v1.3.5-pre-2", "v1.3.5-pre-10", "v1.3.5", "v1.3.6-pre-1")
        self.assertEqual(sorted(reversed(ordered), key=mirror.release_version_key), list(ordered))
        preview = {"tag_name": "v1.3.5-pre-10", "draft": False, "prerelease": True, "assets": []}
        for tag, prerelease, advances in (("v1.3.4", False, False), ("v1.3.5-pre-2", True, False), ("v1.3.5", False, True)):
            git = FakeGit(preview)
            index = {"tag_name": tag, "name": "release", "draft": False, "prerelease": prerelease, "assets": []}
            with tempfile.TemporaryDirectory(prefix="pcl-cnb-test-") as directory:
                mirror.publish_index(git, mirror.CNB_REPO, index, directory, False, latest=False)
                result = json.loads((Path(directory) / "control/latest-preview.json").read_text())
                self.assertEqual(result, index if advances else preview)
                self.assertEqual((Path(directory) / "control/latest.json").read_bytes(), git.stable)

    def test_prerelease_note_edits_do_not_retransfer_or_promote(self):
        http = FakeHttp("v1.3.5-rc.2", True)
        with tempfile.TemporaryDirectory(prefix="pcl-cnb-test-") as directory:
            http.version_cached = mirror.sync(http, mirror.CNB_REPO, directory, http.source)
        http.source["body"] += " revised"
        http.calls.clear()
        with tempfile.TemporaryDirectory(prefix="pcl-cnb-test-") as directory:
            mirror.sync(http, mirror.CNB_REPO, directory, http.source)
        self.assertEqual(http.release["body"], http.source["body"])
        self.assertTrue(http.release["prerelease"])
        self.assertEqual(http.release["make_latest"], "false")
        self.assertFalse(any(method == "PUT" or "/releases/download/" in url for method, url, _ in http.calls))

    def test_publication_and_anonymous_read_back(self):
        http = FakeHttp()
        with tempfile.TemporaryDirectory(prefix="pcl-cnb-test-") as directory:
            result = mirror.sync(http, mirror.CNB_REPO, directory)
        self.assertEqual(result["tag_name"], "v1.3.4")
        self.assertEqual(len(result["assets"]), 3)
        self.assertFalse(http.release["draft"])
        for asset in result["assets"]:
            self.assertTrue(asset["browser_download_url"].startswith("https://cnb.cool/" + mirror.CNB_REPO + "/-/releases/download/"))
            self.assertTrue(asset["digest"].startswith("sha256:"))
        self.assertEqual(sum(method == "PUT" for method, _, _ in http.calls), 3)
        self.assertFalse(any(auth for _, url, auth in http.calls if not url.startswith("https://api.cnb.cool/")))

    def test_corrupt_mirror_does_not_produce_index(self):
        http = FakeHttp()
        http.corrupt_anonymous = True
        with tempfile.TemporaryDirectory(prefix="pcl-cnb-test-") as directory:
            with self.assertRaises(RuntimeError):
                mirror.sync(http, mirror.CNB_REPO, directory)

    def test_unchanged_release_does_not_transfer_attachments(self):
        http = FakeHttp()
        with tempfile.TemporaryDirectory(prefix="pcl-cnb-test-") as directory:
            http.cached = mirror.sync(http, mirror.CNB_REPO, directory)
        http.calls.clear()
        with tempfile.TemporaryDirectory(prefix="pcl-cnb-test-") as directory:
            result = mirror.sync(http, mirror.CNB_REPO, directory)
        self.assertEqual(result, http.cached)
        self.assertFalse(any(method == "PUT" or "/releases/download/" in url for method, url, _ in http.calls))

    def test_changed_mirror_digest_invalidates_cache(self):
        http = FakeHttp()
        with tempfile.TemporaryDirectory(prefix="pcl-cnb-test-") as directory:
            http.cached = mirror.sync(http, mirror.CNB_REPO, directory)
        http.release["assets"][0]["hash_value"] = "0" * 64
        self.assertIsNone(mirror.cached_index(http, mirror.CNB_REPO, http.source, mirror.source_files(http.source), http.release))

    def test_replaced_source_asset_updates_only_changed_files(self):
        http = FakeHttp()
        with tempfile.TemporaryDirectory(prefix="pcl-cnb-test-") as directory:
            http.cached = mirror.sync(http, mirror.CNB_REPO, directory)
        item = http.source["assets"][0]
        payload = http.contents[item["browser_download_url"]] + b"new content"
        http.contents[item["browser_download_url"]] = payload
        item["size"] = len(payload)
        item["digest"] = "sha256:" + hashlib.sha256(payload).hexdigest()
        manifest = http.source["assets"][2]
        content = "".join(asset["digest"][7:] + "  " + asset["name"] + "\n" for asset in http.source["assets"][:2]).encode()
        http.contents[manifest["browser_download_url"]] = content
        manifest["size"] = len(content)
        manifest["digest"] = "sha256:" + hashlib.sha256(content).hexdigest()
        http.calls.clear()
        with tempfile.TemporaryDirectory(prefix="pcl-cnb-test-") as directory:
            mirror.sync(http, mirror.CNB_REPO, directory)
        self.assertEqual(sum(method == "PUT" for method, _, _ in http.calls), 2)

    def test_completed_receipt_excludes_timestamps_and_note_text(self):
        http = FakeHttp()
        value = mirror.receipt_for(http.source)
        self.assertEqual(value["tag_name"], "v1.3.4")
        self.assertNotIn("source notes", str(value))
        self.assertEqual(len(value["source_fingerprint"]), 64)

    def test_checksum_disagreement_stops_before_publication(self):
        http = FakeHttp()
        asset = http.source["assets"][0]
        http.contents[asset["browser_download_url"]] += b"changed"
        asset["size"] += len(b"changed")
        asset["digest"] = "sha256:" + hashlib.sha256(http.contents[asset["browser_download_url"]]).hexdigest()
        with tempfile.TemporaryDirectory(prefix="pcl-cnb-test-") as directory:
            with self.assertRaises(RuntimeError):
                mirror.sync(http, mirror.CNB_REPO, directory)
        self.assertIsNone(http.release)

    def test_cnb_token_host_and_repository_boundary(self):
        http = mirror.Http("synthetic-secret", mirror.CNB_REPO)
        for url in ("https://api.github.com/repos/any/repo", "https://api.cnb.cool/other/repo/-/releases", "http://api.cnb.cool/" + mirror.CNB_REPO + "/-/releases"):
            with self.assertRaises(RuntimeError):
                http.open(url, auth=True)

    def test_git_helper_contains_no_credential_and_rejects_github(self):
        import subprocess
        with tempfile.TemporaryDirectory(prefix="pcl-cnb-test-") as directory:
            git = mirror.Git(directory, "synthetic-token")
            helper = Path(directory) / "askpass.py"
            self.assertNotIn("synthetic-token", helper.read_text())
            result = subprocess.run([sys.executable, str(helper), "Password for 'https://github.com':"], env=git.environment, capture_output=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertNotIn(b"synthetic-token", result.stdout)
            result = subprocess.run([sys.executable, str(helper), "Password for 'https://cnb.cool':"], env=git.environment, capture_output=True)
            self.assertEqual(result.stdout.strip(), b"synthetic-token")

    def test_pipeline_uses_temporary_token_and_does_not_recurse(self):
        value = mirror.pipeline(mirror.CNB_REPO)
        self.assertIn('"crontab: */30 * * * *"', value)
        self.assertIn("web_trigger:", value)
        self.assertNotIn("push:", value)
        self.assertNotIn("GITHUB_TOKEN", value)

    def test_release_only_without_source_history_import(self):
        self.assertFalse(hasattr(mirror, "mirror_code"))
        self.assertFalse(hasattr(mirror, "SOURCE_GIT"))
        http = FakeHttp()
        with tempfile.TemporaryDirectory(prefix="pcl-cnb-test-") as directory:
            mirror.sync(http, mirror.CNB_REPO, directory)
        self.assertEqual(http.release["target_commitish"], "main")

    def test_readme_preserves_upstream_attribution(self):
        value = mirror.download_readme(mirror.CNB_REPO)
        for required in ("龙腾猫跃", "成都瓜皮龙科技有限公司", "2.14.0.0", "NOTICE.md", "LICENCE", "https://github.com/Meloong-Git/PCL", "https://meloong.com/afd/a/LTCat"):
            self.assertIn(required, value)

    def test_historical_release_is_not_promoted(self):
        http = FakeHttp()
        with tempfile.TemporaryDirectory(prefix="pcl-cnb-test-") as directory:
            mirror.sync(http, mirror.CNB_REPO, directory, latest=False)
        self.assertNotEqual(http.release.get("make_latest"), "true")

    def test_requested_version_does_not_silently_switch(self):
        http = FakeHttp()
        selected, latest = mirror.select_release(http, "v1.3.4")
        self.assertTrue(latest)
        self.assertEqual(selected["tag_name"], "v1.3.4")
        def wrong_tag(url, *args, **kwargs):
            return http.source
        http.json = wrong_tag
        with self.assertRaises(RuntimeError):
            mirror.select_release(http, "v1.0.0")


if __name__ == "__main__":
    unittest.main()
