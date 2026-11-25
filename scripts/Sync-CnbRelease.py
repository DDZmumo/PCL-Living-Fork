#!/usr/bin/env python3
"""Mirror verified GitHub releases to CNB and publish an anonymous update index.

Uses CNB_TOKEN only against CNB's API/Git endpoint. In CNB CI this is the
repository-scoped temporary token; end users never receive a token.
"""
import argparse
from datetime import timezone
from email.utils import parsedate_to_datetime
import hashlib
from http.client import HTTPException, IncompleteRead
import json
import math
import os
from pathlib import Path
import re
import socket
import subprocess
import sys
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlparse
from urllib.request import HTTPRedirectHandler, Request, build_opener
from ReleaseSyncState import receipt_for, valid_tag, validate_release, release_version_key

GITHUB_REPO = "DDZmumo/PCL-Living-Fork"
CNB_REPO = "ddzmumo/PCL-Living-Fork"
GITHUB_API = "https://api.github.com/repos/" + GITHUB_REPO
MAX_BYTES = 1024 * 1024 * 1024
MAX_REQUEST_ATTEMPTS = 3
MAX_RETRY_WAIT = 60
TRANSIENT_HTTP_STATUSES = {408, 500, 502, 503, 504}


class MirrorFailure(RuntimeError):
    """An error message built without credentials or raw server output."""


class RequestFailure(MirrorFailure):
    def __init__(self, context, detail, status=None):
        super().__init__(f"{context}: {detail}")
        self.status = status


def retry_after_seconds(value, now):
    if not value:
        return None
    value = value.strip()
    if re.fullmatch(r"[0-9]+", value):
        return float(value)
    try:
        when = parsedate_to_datetime(value)
        if when.tzinfo is None:
            when = when.replace(tzinfo=timezone.utc)
        return max(0, when.timestamp() - now)
    except (ValueError, OverflowError, OSError):
        return None


def github_rate_limit_message(error):
    # Inspect the documented 403 message locally. Never include the body in logs.
    try:
        payload = json.loads(error.read(16 * 1024))
    except (ValueError, OSError, HTTPException, RecursionError):
        return False
    message = payload.get("message", "") if isinstance(payload, dict) else ""
    message = message.lower() if isinstance(message, str) else ""
    return "secondary rate limit" in message or "api rate limit exceeded" in message


def http_retry(error, github_api, attempt):
    headers = error.headers or {}
    now = time.time()
    delay = retry_after_seconds(headers.get("Retry-After"), now)
    exhausted = github_api and headers.get("X-RateLimit-Remaining") == "0"
    rate_limited = error.code == 429 or (
        error.code == 403 and github_api and (exhausted or delay is not None or github_rate_limit_message(error))
    )
    if not rate_limited:
        if error.code in TRANSIENT_HTTP_STATUSES:
            return "temporary failure", max(delay or 0, 2 ** attempt)
        return "", None
    if exhausted:
        reset = headers.get("X-RateLimit-Reset", "")
        if re.fullmatch(r"[0-9]+", reset):
            delay = max(delay or 0, float(reset) - now)
        elif delay is None:
            return "rate limited, reset time unavailable", None
    if github_api and delay is None:
        # GitHub asks for at least one minute when a secondary limit has no Retry-After.
        delay = 60 * 2 ** attempt
    return "rate limited", max(delay or 0, 2 ** attempt)


class SafeRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if urlparse(newurl).scheme != "https":
            raise MirrorFailure("Refusing a non-HTTPS redirect")
        if req.has_header("Authorization"):
            raise MirrorFailure("Authenticated API requests must not redirect")
        return super().redirect_request(req, fp, code, msg, headers, newurl)


class Http:
    def __init__(self, token, repository):
        self.token = token
        self.repository = repository
        self.opener = build_opener(SafeRedirect())

    def endpoint(self, url, method):
        # Label fixed routes instead of copying URLs. Upload URLs and confirmation
        # paths can both carry credentials, even before the query string.
        if method == "PUT":
            return "CNB attachment storage (signed URL redacted)"
        try:
            parsed = urlparse(url)
        except ValueError:
            return "HTTPS endpoint (URL redacted)"
        host, path = parsed.hostname, parsed.path
        if host == "api.github.com":
            base = "/repos/" + GITHUB_REPO + "/releases"
            if path == base + "/latest":
                return GITHUB_API + "/releases/latest"
            if path.startswith(base + "/tags/"):
                return GITHUB_API + "/releases/tags/{tag}"
            return "https://api.github.com/{redacted}"
        if host == "github.com":
            return "https://github.com/" + GITHUB_REPO + "/releases/download/{tag}/{asset}"
        if host in {"release-assets.githubusercontent.com", "objects.githubusercontent.com"}:
            return "https://" + host + "/{redacted}"
        if host == "api.cnb.cool":
            prefix = "/" + self.repository + "/-/releases"
            if path != prefix and not path.startswith(prefix + "/"):
                return "https://api.cnb.cool/{redacted}"
            if path == prefix:
                route = ""
            elif path.startswith(prefix + "/tags/"):
                route = "/tags/{tag}"
            elif "/asset-upload-confirmation/" in path:
                route = "/{id}/asset-upload-confirmation/{redacted}"
            elif path.endswith("/asset-upload-url"):
                route = "/{id}/asset-upload-url"
            else:
                route = "/{id}"
            return "https://api.cnb.cool" + prefix + route
        if host == "cnb.cool":
            prefix = "https://cnb.cool/" + self.repository + "/-"
            if "/releases/download/" in path:
                return prefix + "/releases/download/{tag}/{asset}"
            name = path.rsplit("/", 1)[-1]
            manifest = name if name in {"latest.json", "latest-preview.json"} else "releases/{tag}.json"
            return prefix + "/git/raw/updates/" + manifest
        return "HTTPS attachment storage (URL redacted)"

    def open(self, url, method="GET", data=None, auth=False, headers=None):
        try:
            parsed = urlparse(url)
        except ValueError:
            raise MirrorFailure("Invalid HTTPS request URL") from None
        if parsed.scheme != "https" or parsed.username or parsed.password:
            raise MirrorFailure("Untrusted HTTPS request")
        merged = {"User-Agent": "PCLLF-CNB-Mirror", "Accept": "application/json"}
        merged.update(headers or {})
        if auth:
            if parsed.hostname != "api.cnb.cool" or not parsed.path.startswith("/" + self.repository + "/-/"):
                raise MirrorFailure("CNB credentials cannot leave the target repository API")
            merged["Authorization"] = "Bearer " + self.token
            merged["Accept"] = "application/vnd.cnb.api+json"
        return self.opener.open(Request(url, data=data, headers=merged, method=method), timeout=45)

    def request(self, url, consume, *, step, method="GET", data=None, auth=False, headers=None):
        endpoint = self.endpoint(url, method)
        context = f"{step} [{method} {endpoint}]"
        retry_wait = 0
        for attempt in range(MAX_REQUEST_ATTEMPTS):
            status = None
            location = context
            try:
                with self.open(url, method, data, auth, headers) as response:
                    return consume(response)
            except HTTPError as error:
                status = error.code
                effective = self.endpoint(error.geturl() or url, method)
                if effective != endpoint:
                    location += " via " + effective
                with error:
                    reason, delay = http_retry(error, effective.startswith("https://api.github.com/"), attempt)
                detail = f"HTTP {status}" + (f" ({reason})" if reason else "")
            except (OSError, HTTPException) as error:
                reason = error.reason if isinstance(error, URLError) else error
                temporary = isinstance(reason, (TimeoutError, ConnectionError, IncompleteRead)) or (
                    isinstance(reason, socket.gaierror) and reason.errno == socket.EAI_AGAIN
                )
                detail = f"I/O failure ({type(reason).__name__})"
                delay = 2 ** attempt if temporary else None
            except MirrorFailure as error:
                raise RequestFailure(context, str(error)) from None
            if method not in {"GET", "HEAD"} or delay is None or attempt + 1 == MAX_REQUEST_ATTEMPTS:
                raise RequestFailure(location, detail, status) from None
            if retry_wait + delay > MAX_RETRY_WAIT:
                detail += f", retry wait exceeds {MAX_RETRY_WAIT}s budget"
                raise RequestFailure(location, detail, status) from None
            delay = math.ceil(delay)
            print(
                f"Mirror retry: {location}: {detail}, retry {attempt + 1}/{MAX_REQUEST_ATTEMPTS - 1} in {delay}s",
                file=sys.stderr, flush=True,
            )
            time.sleep(delay)
            retry_wait += delay

    def json(self, url, method="GET", data=None, auth=False, missing=False, *, step="Read release data"):
        payload = None if data is None else json.dumps(data, ensure_ascii=False).encode("utf-8")

        def read(response):
            raw = response.read(4 * 1024 * 1024 + 1)
            if len(raw) > 4 * 1024 * 1024:
                raise MirrorFailure("API response is too large")
            try:
                return json.loads(raw) if raw else None
            except (ValueError, RecursionError):
                raise MirrorFailure("Invalid JSON response") from None

        try:
            return self.request(url, read, step=step, method=method, data=payload, auth=auth, headers={"Content-Type": "application/json"})
        except RequestFailure as error:
            if missing and error.status == 404:
                return None
            raise

    def download(self, url, target, size, digest, *, step="Download release attachment"):
        if size < 1 or size > MAX_BYTES:
            raise MirrorFailure("Invalid attachment size")

        def read(response):
            total, actual = 0, hashlib.sha256()
            with target.open("wb") as output:
                while chunk := response.read(256 * 1024):
                    total += len(chunk)
                    if total > size:
                        raise MirrorFailure("Attachment exceeds its declared size")
                    actual.update(chunk)
                    output.write(chunk)
            if total < size and getattr(response, "headers", {}).get("Content-Length") == str(size):
                raise IncompleteRead(b"", size - total)
            if total != size or actual.hexdigest() != digest:
                raise MirrorFailure("Attachment failed SHA256/size verification")

        self.request(url, read, step=step)

    def upload(self, url, stream, size, *, step):
        self.request(url, lambda response: response.read(), step=step, method="PUT", data=stream,
                     headers={"Content-Type": "application/octet-stream", "Content-Length": str(size)})


def manifest_hash(text, name):
    values = []
    for line in text.splitlines():
        match = re.fullmatch(r"([0-9a-fA-F]{64})\s+\*?(.+?)\s*", line)
        if match and match[2] == name:
            values.append(match[1].lower())
    if len(set(values)) != 1:
        raise MirrorFailure("Missing or conflicting SHA256 entry: " + name)
    return values[0]


def source_files(release):
    try:
        tag = validate_release(release)
    except RuntimeError:
        raise MirrorFailure("GitHub returned invalid release metadata") from None
    version = tag[1:]
    expected = ["PCL-Living-Fork-" + version + ".exe", "PCL-Living-Fork-" + version + "-win-x64.zip", "SHA256SUMS.txt"]
    result = []
    for name in expected:
        items = [item for item in release.get("assets", []) if item.get("name") == name]
        if len(items) != 1:
            raise MirrorFailure("Source attachment is missing or ambiguous: " + name)
        item = items[0]
        wanted = "https://github.com/" + GITHUB_REPO + "/releases/download/" + quote(tag, safe="") + "/" + quote(name, safe="")
        if item.get("browser_download_url") != wanted or not re.fullmatch(r"sha256:[0-9a-f]{64}", item.get("digest", "")):
            raise MirrorFailure("Source URL or GitHub attachment digest is invalid")
        if type(item.get("size")) is not int or not 0 < item["size"] <= MAX_BYTES:
            raise MirrorFailure("Source attachment size is invalid")
        result.append(item)
    return result


class Git:
    def __init__(self, directory, token, repository=CNB_REPO):
        self.repository = repository
        directory = Path(directory)
        helper = directory / "askpass.py"
        helper.write_text(
            "import os,sys,re\nfrom urllib.parse import urlparse\n"
            "p=sys.argv[1] if len(sys.argv)>1 else ''\n"
            "m=re.search(r'https://[^\\s\\\'\\\"]+',p)\n"
            "if not m or urlparse(m[0]).hostname != 'cnb.cool': sys.exit(1)\n"
            "print('cnb' if p.lower().startswith('username') else os.environ['CNB_TOKEN'])\n",
            encoding="utf-8",
        )
        launcher = directory / ("askpass.cmd" if os.name == "nt" else "askpass")
        if os.name == "nt":
            launcher.write_text('@"' + sys.executable + '" "' + str(helper) + '" %*\r\n', encoding="utf-8")
        else:
            import shlex
            launcher.write_text("#!/bin/sh\nexec " + shlex.quote(sys.executable) + " " + shlex.quote(str(helper)) + ' "$@"\n', encoding="utf-8")
            launcher.chmod(0o700)
        self.environment = dict(os.environ, CNB_TOKEN=token, GIT_ASKPASS=str(launcher), GIT_TERMINAL_PROMPT="0")
        self.environment.update(GIT_AUTHOR_NAME="PCL Living Fork Mirror", GIT_AUTHOR_EMAIL="mirror@users.noreply.cnb.cool", GIT_COMMITTER_NAME="PCL Living Fork Mirror", GIT_COMMITTER_EMAIL="mirror@users.noreply.cnb.cool")

    def run(self, *arguments, cwd=None, optional=False):
        result = subprocess.run(["git", "-c", "credential.helper=", *arguments], cwd=cwd, env=self.environment, capture_output=True, text=True, encoding="utf-8", errors="replace")
        if result.returncode and not optional:
            operation = "Git " + arguments[0]
            if arguments[0] in {"clone", "ls-remote", "push"}:
                operation += " [https://cnb.cool/" + self.repository + "]"
            status = re.search(r"The requested URL returned error: ([1-5][0-9]{2})\b", result.stderr)
            detail = "HTTP " + status[1] if status else f"exit code {result.returncode}"
            raise MirrorFailure(f"{operation}: {detail}")
        return result


def pipeline(repository):
    # No push trigger: publishing latest.json must not create a recursive pipeline.
    return """updates:
  web_trigger: &mirror
    - name: mirror-release
      lock:
        key: pcl-release-mirror
        wait: true
        timeout: 600
        expires: 900
      docker:
        image: python:3.12-alpine
      stages:
        - name: git
          script: apk add --no-cache git
        - name: mirror
          script: python mirror.py --repository REPOSITORY
  "crontab: */30 * * * *": *mirror
  api_trigger: *mirror
""".replace("REPOSITORY", repository)


def attribution():
    return """## 来源与许可

原作作者为龙腾猫跃及 PCL 原项目贡献者，原作版权归成都瓜皮龙科技有限公司。基线为上游 2.14.0.0，保留原作与第三方组件的署名和许可。

- [上游 PCL](https://github.com/Meloong-Git/PCL)
- [来源与版权声明](https://github.com/DDZmumo/PCL-Living-Fork/blob/main/NOTICE.md)
- [许可与使用指南](https://github.com/DDZmumo/PCL-Living-Fork/blob/main/LICENCE)
- [支持原作者](https://meloong.com/afd/a/LTCat)
"""


def download_readme(repository):
    return ("# PCL Living Fork 国内下载源\n\n"
            "本仓库用于同步 PCL Living Fork 的正式版与预发布版说明、安装包及校验文件，供国内用户下载。\n\n"
            "- [下载正式版与预发布版](https://cnb.cool/" + repository + "/-/releases)\n"
            "- [源码与问题反馈](https://github.com/" + GITHUB_REPO + ")\n"
            "- [公开更新清单](https://cnb.cool/" + repository + "/-/git/raw/updates/latest.json)\n\n"
            "预发布版保留独立版本清单，不替换正式版更新清单。完整源码在 GitHub 主仓库维护。`updates` 分支仅保留更新清单和发布同步配置。\n\n" + attribution())


def publish_index(git, repository, index, directory, bootstrap, latest=True, receipt=None):
    tag = validate_release(index)
    if latest and index["prerelease"]:
        raise MirrorFailure("A prerelease cannot replace the stable update index")
    remote = "https://cnb.cool/" + repository
    control = Path(directory) / "control"
    exists = bool(git.run("ls-remote", remote, "refs/heads/updates").stdout.strip())
    if exists:
        git.run("clone", "--depth", "1", "--single-branch", "--branch", "updates", remote, str(control))
    else:
        control.mkdir()
        git.run("init", "--initial-branch=updates", cwd=control)
        git.run("remote", "add", "origin", remote, cwd=control)
    folder = control / "releases"
    folder.mkdir(exist_ok=True)
    version_index = "releases/" + tag + ".json"
    (control / version_index).write_text(json.dumps(index, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    files = [version_index]
    # The opt-in channel receives both stable and prerelease updates. Never downgrade its head.
    preview_path = control / "latest-preview.json"
    preview = None
    if preview_path.exists():
        try:
            preview = json.loads(preview_path.read_text(encoding="utf-8"))
            validate_release(preview)
        except (ValueError, RuntimeError, AttributeError):
            preview = None
    if preview is None or release_version_key(tag) >= release_version_key(preview["tag_name"]):
        preview_path.write_text(json.dumps(index, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        files.append("latest-preview.json")
    if latest:
        files.append("latest.json")
        (control / "latest.json").write_text(json.dumps(index, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if receipt:
        if not valid_tag(receipt.get("tag_name", "")):
            raise MirrorFailure("Invalid synchronization receipt tag")
        folder = control / "receipts"
        folder.mkdir(exist_ok=True)
        relative = "receipts/" + receipt["tag_name"] + ".json"
        (control / relative).write_text(json.dumps(receipt, sort_keys=True, indent=2) + "\n", encoding="utf-8")
        files.append(relative)
    if bootstrap or not exists:
        (control / "mirror.py").write_bytes(Path(__file__).read_bytes())
        (control / "ReleaseSyncState.py").write_bytes(Path(__file__).with_name("ReleaseSyncState.py").read_bytes())
        (control / ".cnb.yml").write_text(pipeline(repository), encoding="utf-8")
        (control / "README.md").write_text("# PCL Living Fork 更新清单\n\n`latest.json` 仅用于正式版更新；`latest-preview.json` 用于主动选择接收正式版与预发布版的通道；`releases/<tag>.json` 保存各版本的独立清单。同步按内容指纹去重，发布时校验 GitHub SHA256，流水线使用 CNB 临时令牌。\n\n" + attribution(), encoding="utf-8")
        files += ["mirror.py", "ReleaseSyncState.py", ".cnb.yml", "README.md"]
    git.run("add", "--", *files, cwd=control)
    if git.run("diff", "--cached", "--quiet", cwd=control, optional=True).returncode == 0:
        print("Anonymous update index is already current")
        return
    git.run("commit", "-m", "chore: refresh update mirror " + index["tag_name"], cwd=control)
    git.run("push", "origin", "HEAD:updates", cwd=control)
    print("Published anonymous update index")


def cached_index(http, repository, source, items, release):
    if not release or release.get("draft") is not False or release.get("prerelease") is not source["prerelease"]:
        return None
    try:
        value = http.json("https://cnb.cool/" + repository + "/-/git/raw/updates/releases/" + quote(source["tag_name"], safe="") + ".json",
                          missing=True, step="Read CNB version manifest")
        if not value and not source["prerelease"]:
            value = http.json("https://cnb.cool/" + repository + "/-/git/raw/updates/latest.json",
                              missing=True, step="Read CNB stable manifest")
        if not value or value.get("tag_name") != source["tag_name"] or value.get("draft") is not False or value.get("prerelease") is not source["prerelease"]:
            return None
        if len(value.get("assets", [])) != len(items):
            return None
        for item in items:
            indexed = [asset for asset in value["assets"] if asset.get("name") == item["name"]]
            stored = [asset for asset in release.get("assets", []) if asset.get("name") == item["name"]]
            wanted = "https://cnb.cool/" + repository + "/-/releases/download/" + source["tag_name"] + "/" + item["name"]
            if len(indexed) != 1 or len(stored) != 1:
                return None
            if indexed[0].get("size") != item["size"] or indexed[0].get("digest") != item["digest"] or indexed[0].get("browser_download_url") != wanted:
                return None
            if stored[0].get("size") != item["size"] or stored[0].get("hash_algo", "").lower().replace("-", "") != "sha256" or stored[0].get("hash_value", "").lower() != item["digest"][7:]:
                return None
        return value
    except RequestFailure as error:
        print("Mirror cache lookup failed: " + str(error), file=sys.stderr, flush=True)
        return None
    except (ValueError, RuntimeError):
        return None


def release_form(source, latest):
    validate_release(source)
    return {"tag_name": source["tag_name"], "name": source["name"], "body": source["body"], "draft": False,
            "prerelease": source["prerelease"], "make_latest": "true" if latest and not source["prerelease"] else "false"}


def select_release(http, tag=""):
    if tag and not valid_tag(tag):
        raise MirrorFailure("Invalid requested release tag")
    current = http.json(GITHUB_API + "/releases/latest", step="Read latest GitHub release")
    source = current
    if tag and tag != current["tag_name"]:
        source = http.json(GITHUB_API + "/releases/tags/" + quote(tag, safe=""), step="Read requested GitHub release " + tag)
    source_files(source)
    if tag and source["tag_name"] != tag:
        raise MirrorFailure("Source response does not match the requested release")
    return source, not source["prerelease"] and source["tag_name"] == current["tag_name"]


def sync(http, repository, directory, source=None, latest=True):
    api = "https://api.cnb.cool/" + quote(repository, safe="/") + "/-/releases"
    if source is None:
        source = http.json(GITHUB_API + "/releases/latest", step="Read latest GitHub release")
    items = source_files(source)
    latest = latest and not source["prerelease"]
    tag = source["tag_name"]
    release = http.json(api + "/tags/" + quote(tag, safe=""), auth=True, missing=True, step="Find CNB release " + tag)
    cached = cached_index(http, repository, source, items, release)
    if cached:
        if release.get("body") != source["body"] or release.get("name") != source["name"] or (latest and not release.get("is_latest")):
            promote = latest and http.json(GITHUB_API + "/releases/latest", step="Check GitHub latest before updating CNB release")["tag_name"] == tag
            http.json(api + "/" + quote(str(release["id"]), safe=""), "PATCH", release_form(source, promote),
                      auth=True, step="Update CNB release metadata")
        print("Release payloads unchanged; no attachment transfer needed")
        return cached
    cache = Path(directory) / "downloads"
    cache.mkdir()
    manifest = next(item for item in items if item["name"] == "SHA256SUMS.txt")
    http.download(manifest["browser_download_url"], cache / manifest["name"], manifest["size"], manifest["digest"][7:],
                  step="Download GitHub checksums")
    text = (cache / manifest["name"]).read_text(encoding="utf-8-sig")
    for item in items:
        digest = item["digest"][7:]
        if item is not manifest:
            if manifest_hash(text, item["name"]) != digest:
                raise MirrorFailure("GitHub digest and SHA256SUMS disagree for " + item["name"])
            http.download(item["browser_download_url"], cache / item["name"], item["size"], digest,
                          step="Download GitHub attachment " + item["name"])
    if release is None:
        release = http.json(api, "POST", {"tag_name": tag, "target_commitish": "main", "name": source["name"], "body": source["body"], "draft": True, "prerelease": source["prerelease"], "make_latest": "false"},
                            auth=True, step="Create CNB draft release")
    release_url = api + "/" + quote(str(release["id"]), safe="")
    for item in items:
        name = item["name"]
        current = http.json(release_url, auth=True, step="Check existing CNB attachment " + name)
        existing = [asset for asset in current.get("assets", []) if asset.get("name") == name]
        overwrite = False
        if existing:
            if len(existing) != 1:
                raise MirrorFailure("An existing CNB attachment is ambiguous: " + name)
            algorithm = existing[0].get("hash_algo", "").lower().replace("-", "")
            value = existing[0].get("hash_value", "").lower()
            overwrite = existing[0].get("size") != item["size"] or (algorithm == "sha256" and value != item["digest"][7:])
            if not overwrite:
                continue
        upload = http.json(release_url + "/asset-upload-url", "POST", {"asset_name": name, "size": item["size"], "overwrite": overwrite, "ttl": 0},
                           auth=True, step="Request CNB upload URL for " + name)
        with (cache / name).open("rb") as stream:
            http.upload(upload["upload_url"], stream, item["size"], step="Upload CNB attachment " + name)
        verify = upload["verify_url"]
        if not verify.startswith(release_url + "/asset-upload-confirmation/"):
            raise MirrorFailure("Untrusted CNB upload confirmation endpoint for " + name)
        http.json(verify + ("&" if "?" in verify else "?") + "ttl=0", "POST", auth=True, step="Confirm CNB attachment " + name)
        print("Uploaded " + name)
    promote = latest and http.json(GITHUB_API + "/releases/latest", step="Check GitHub latest before publishing CNB release")["tag_name"] == tag
    http.json(release_url, "PATCH", release_form(source, promote), auth=True, step="Publish CNB release")
    release = http.json(release_url, auth=True, step="Read published CNB release")
    if release.get("draft") is not False or release.get("prerelease") is not source["prerelease"]:
        raise MirrorFailure("CNB publication does not preserve the release channel")
    index = {"tag_name": tag, "name": source["name"], "draft": False, "prerelease": source["prerelease"], "assets": []}
    for item in items:
        candidates = [asset for asset in release.get("assets", []) if asset.get("name") == item["name"]]
        if len(candidates) != 1:
            raise MirrorFailure("CNB publication is incomplete: " + item["name"])
        url = candidates[0].get("browser_download_url", "")
        wanted = "https://cnb.cool/" + repository + "/-/releases/download/" + quote(tag, safe="") + "/" + quote(item["name"], safe="")
        if url != wanted:
            raise MirrorFailure("CNB returned an unexpected public download URL for " + item["name"])
        # Anonymous read-back is mandatory before advertising this mirror to players.
        http.download(url, cache / ("verified-" + item["name"]), item["size"], item["digest"][7:],
                      step="Verify anonymous CNB attachment " + item["name"])
        index["assets"].append({"name": item["name"], "size": item["size"], "digest": item["digest"], "browser_download_url": url})
        print("Verified anonymous SHA256: " + item["name"])
    return index


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", default=CNB_REPO)
    parser.add_argument("--auth-file", type=Path)
    parser.add_argument("--bootstrap-ci", action="store_true")
    parser.add_argument("--tag", default=os.environ.get("GITHUB_RELEASE_TAG", ""))
    args = parser.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*", args.repository):
        raise MirrorFailure("Invalid target repository")
    token = os.environ.get("CNB_TOKEN", "")
    if args.auth_file:
        try:
            token = json.loads(args.auth_file.read_text(encoding="utf-8-sig")).get("token", "")
        except (OSError, ValueError, AttributeError):
            raise MirrorFailure("Could not read the CNB credential file") from None
    if not token:
        raise MirrorFailure("Set CNB_TOKEN or provide a local ignored --auth-file")
    http = Http(token, args.repository)
    with tempfile.TemporaryDirectory(prefix="pcl-cnb-mirror-") as directory:
        git = Git(directory, token, args.repository)
        release, latest = select_release(http, args.tag)
        print("Mirroring GitHub release " + release["tag_name"], flush=True)
        index = sync(http, args.repository, directory, release, latest)
        publish_latest = latest and http.json(GITHUB_API + "/releases/latest", step="Check GitHub latest before publishing manifests")["tag_name"] == release["tag_name"]
        publish_index(git, args.repository, index, directory, args.bootstrap_ci, latest=publish_latest, receipt=receipt_for(release))
        if not publish_latest:
            print("Prerelease or historical release synchronized; stable update index preserved")
    print("CNB release mirror is ready: " + index["tag_name"])


if __name__ == "__main__":
    try:
        main()
    except MirrorFailure as failure:
        print("Mirror failed: " + str(failure), file=sys.stderr)
        sys.exit(1)
    except Exception as failure:
        print("Mirror failed: unexpected " + type(failure).__name__, file=sys.stderr)
        sys.exit(1)
