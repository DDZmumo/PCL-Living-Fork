#!/usr/bin/env python3
"""Build an isolated reference executable from hash-locked Prism C++ sources.

This original orchestration is governed by the repository LICENCE. Downloaded
and generated Prism source retains its own notices and GPL/Apache obligations.
It is never referenced by a product project or copied to the Core directory.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import urllib.request

HERE = Path(__file__).resolve().parent
COMMIT = "899d2f3957215a09fe398a8a8cbb8a8a9c619968"


def sha(path):
    return hashlib.file_digest(path.open("rb"), "sha256").hexdigest()


def checked_download(url, path, digest):
    if not path.exists():
        path.parent.mkdir(parents=True, exist_ok=True)
        with urllib.request.urlopen(url, timeout=60) as response, path.open("xb") as output:
            shutil.copyfileobj(response, output)
    if sha(path) != digest:
        raise ValueError(f"Pinned SHA256 mismatch: {path}")


def between(text, start, end):
    begin = text.index(start)
    return text[begin:text.index(end, begin)]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--fetch", action="store_true")
    parser.add_argument("--download-tools", action="store_true")
    parser.add_argument("--build", action="store_true")
    parser.add_argument("--resume", action="store_true", help="Keep already compiled unchanged translation units.")
    args = parser.parse_args()
    root = args.root.resolve()
    root.mkdir(parents=True, exist_ok=True)
    lock = json.loads((HERE / "source-lock.json").read_text())
    for entry in lock["files"]:
        target = root / "reference" / entry["path"]
        if args.fetch:
            checked_download(f"https://raw.githubusercontent.com/PrismLauncher/PrismLauncher/{COMMIT}/{entry['path']}", target, entry["sha256"])
        elif sha(target) != entry["sha256"]:
            raise ValueError(f"Pinned source mismatch: {target}")
    if args.download_tools:
        packages = json.loads((HERE / "toolchain-lock.json").read_text())
        tools, sdk = root / "tools", root / "sdk"
        tools.mkdir(exist_ok=True)
        sdk.mkdir(exist_ok=True)
        for entry in packages:
            target = root / "packages" / entry["url"].rsplit("/", 1)[1]
            checked_download(entry["url"], target, entry["sha256"])
            if target.name.endswith(".7z.exe"):
                # Use 7-Zip as an archive reader; do not execute the self-extractor.
                seven = shutil.which("7z") or r"C:\Program Files\7-Zip\7z.exe"
                subprocess.run([seven, "x", str(target), "-o" + str(tools), "-y", "-bsp0", "-bso0"], check=True)
            else:
                subprocess.run(["tar", "-xf", str(target), "-C", str(sdk)], check=True)

    generated = root / "generated"
    generated.mkdir(exist_ok=True)
    transformations = []

    def write(path, text, changes):
        target = generated / path
        target.parent.mkdir(parents=True, exist_ok=True)
        if not target.exists() or target.read_text(encoding="utf-8") != text:
            target.write_text(text, encoding="utf-8", newline="\n")
        transformations.append({"path": path, "sha256": sha(target), "changes": changes})

    def original(path):
        return (root / "reference" / path).read_text(encoding="utf-8")

    # Keep parser, merge, comparison, filtering and path function bodies intact.
    # Only cut dependency edges to network, launcher settings and GUI lifecycle.
    for entry in lock["files"]:
        path = entry["path"]
        if not path.startswith("launcher/"):
            continue
        text = original(path)
        changes = []
        if path == "launcher/minecraft/OneSixVersionFormat.h":
            text = text.replace("#include <minecraft/PackProfile.h>\n", "")
            changes.append("Remove unused PackProfile include.")
        elif path == "launcher/minecraft/VersionFile.cpp":
            text = text.replace('#include "minecraft/PackProfile.h"', '#include "minecraft/LaunchProfile.h"')
            changes.append("Replace unused PackProfile include with the actual LaunchProfile declaration.")
        elif path == "launcher/minecraft/Library.h":
            text = text.replace('#include "net/Request.h"\n', "")
            block = between(text, "    QList<Net::Request::Ptr> getDownloads(", "    QString getCompatibleNative(")
            text = text.replace(block, "")
            changes.append("Remove unmeasured getDownloads declaration and network include.")
        elif path == "launcher/minecraft/Library.cpp":
            for include in ['#include "MinecraftInstance.h"', '#include "net/Request.h"', '#include <BuildConfig.h>', '#include <net/ApiRequest.h>', '#include <net/ChecksumValidator.h>']:
                text = text.replace(include + "\n", "")
            block = between(text, "QList<Net::Request::Ptr> Library::getDownloads(", "/**\n * @brief Check if the library is active")
            text = text.replace(block, "")
            changes.append("Remove unmeasured network getDownloads definition and its dependency includes; all active/filter/path methods intact.")
        elif path == "launcher/RuntimeContext.h":
            text = text.replace('#include "SysInfo.h"\n', "").replace('#include "settings/SettingsObject.h"\n', "")
            text = text.replace("QString system = SysInfo::currentSystem();", "QString system; // Harness supplies this field explicitly.")
            block = between(text, "    void updateFromInstanceSettings(", "    QString getClassifier()")
            text = text.replace(block, "")
            changes.append("Harness supplies runtime explicitly; omit system discovery/settings updater. Classifier/mapping functions intact.")
        elif path in ["launcher/FileSystem.h", "launcher/FileSystem.cpp", "launcher/meta/JsonFormat.cpp", "launcher/minecraft/PackProfile.cpp", "launcher/minecraft/PackProfile.h", "launcher/minecraft/MinecraftInstance.cpp", "launcher/minecraft/MinecraftInstance.h"]:
            continue
        write(path, text, changes)

    fs = original("launcher/FileSystem.cpp")
    notice = fs[:fs.index('#include "FileSystem.h"')]
    paths = between(fs, "QString PathCombine(const QString& path1, const QString& path2)", "QString AbsolutePath(")
    invalid = between(fs, 'const QString g_badChars =', "QString DirNameFromString(")
    write("launcher/FileSystem.cpp", notice + '\n#include "FileSystem.h"\n#include <QFileInfo>\n#include <QDir>\nnamespace FS {\n' + paths + '\nnamespace {\n' + invalid + "\n}\n", ["Extract original PathCombine overloads and invalid-character helpers only, unchanged; preserve notice."])
    path_declaration = next(line for line in original("launcher/FileSystem.h").splitlines() if line.startswith("QString RemoveInvalidPathChars("))
    write("launcher/FileSystem.h", '#pragma once\n#include "Result.h"\nnamespace FS {\nQString PathCombine(const QString&, const QString&);\n' + path_declaration + '\nResult<> write(const QString&, const QByteArray&);\nResult<QByteArray> read(const QString&);\n}\n', ["Minimal declarations; copy the actual invalid-path declaration including its default replacement character. IO declarations have fail-closed adapters."])
    meta = original("launcher/meta/JsonFormat.cpp")
    notice = meta[:meta.index('#include "JsonFormat.h"')]
    format_functions = between(meta, "Result<int> parseFormatVersion(", "Result<> parseIndex(")
    requires = meta[meta.index("Result<> parseRequires("):]
    write("launcher/meta/JsonFormat.cpp", notice + '#include "JsonFormat.h"\n#include "Json.h"\nusing namespace Json;\nnamespace Meta {\n' + format_functions + requires, ["Extract original format/requirement parse and serialization functions; omit network metadata/index classes."])
    minecraft = original("launcher/minecraft/MinecraftInstance.cpp")
    prefix = between(minecraft, "    auto args = profile->getMinecraftArguments().split(", "    if (targetToJoin)")
    write("UnboundGameArguments.cpp", minecraft[:minecraft.index("#include")] + '#include "minecraft/LaunchProfile.h"\nQStringList referenceUnboundGameArguments(const LaunchProfile* profile) {\n' + prefix + "    return args;\n}\n", ["Extract unchanged pre-auth/pre-server tokenization and tweaker statements from processMinecraftArgs; no macro/account binding measured."])
    write("launcher/BuildConfig.h", '#pragma once\n#include <QString>\nstruct ReferenceBuildConfig { QString LAUNCHER_DISPLAYNAME = "Prism native reference"; };\ninline const ReferenceBuildConfig BuildConfig;\n', ["Only build display name used in unsupported-launcher warning; not a rule or parsed value."])
    write("ExcludedIo.cpp", '#include "launcher/FileSystem.h"\nnamespace FS {\nResult<> write(const QString&, const QByteArray&) { return std::unexpected("Excluded reference IO path"); }\nResult<QByteArray> read(const QString&) { return std::unexpected("Excluded reference IO path"); }\n}\n', ["Fail-closed adapters for unused Json filename/write overloads. Input is read by harness QFile, not these adapters."])
    preserved = []
    checks = [
        ("launcher/minecraft/OneSixVersionFormat.cpp", "Result<LibraryPtr> OneSixVersionFormat::libraryFromJson(", "QJsonDocument OneSixVersionFormat::versionFileToJson("),
        ("launcher/minecraft/VersionFile.cpp", "void VersionFile::applyTo(", None),
        ("launcher/minecraft/LaunchProfile.cpp", "void LaunchProfile::clear()", None),
        ("launcher/minecraft/Library.cpp", "void Library::getApplicableFiles(", "/**\n * @brief Get download requests"),
        ("launcher/minecraft/Library.cpp", "bool Library::isActive(", None),
        ("launcher/minecraft/Rule.cpp", "Rule Rule::fromJson(", None),
    ]
    for path, start, end in checks:
        source = original(path)
        begin = source.index(start)
        finish = source.index(end, begin) if end else len(source)
        excerpt = source[begin:finish]
        if excerpt not in (generated / path).read_text(encoding="utf-8"):
            raise ValueError(f"Measured native body was changed: {path}: {start}")
        preserved.append({"path": path, "startLine": source[:begin].count("\n") + 1,
                          "endLine": source[:finish].count("\n") + 1,
                          "sha256": hashlib.sha256(excerpt.encode()).hexdigest()})
    (root / "transformations.json").write_text(json.dumps({"commit": COMMIT, "files": transformations, "preservedNativeBodySpans": preserved}, indent=2), encoding="utf-8")
    if not args.build:
        print("Verified sources; generated dependency-isolated C++ files.")
        return
    compiler = root / "tools/w64devkit/bin/g++.exe"
    qt = root / "sdk/mingw64"
    build = root / "build"
    build.mkdir(exist_ok=True)
    sources = ["launcher/minecraft/OneSixVersionFormat.cpp", "launcher/minecraft/MojangVersionFormat.cpp", "launcher/minecraft/VersionFile.cpp", "launcher/minecraft/LaunchProfile.cpp", "launcher/minecraft/Library.cpp", "launcher/minecraft/Rule.cpp", "launcher/minecraft/ParseUtils.cpp", "launcher/Json.cpp", "launcher/Version.cpp", "launcher/StringUtils.cpp", "launcher/java/JavaMetadata.cpp", "launcher/java/JavaVersion.cpp", "launcher/meta/JsonFormat.cpp", "launcher/FileSystem.cpp", "UnboundGameArguments.cpp", "ExcludedIo.cpp"]
    common = [str(compiler), "-std=c++23", "-O1", "-ffunction-sections", "-fdata-sections", "-DUNICODE", "-D_UNICODE", "-DQT_CORE_LIB", "-I" + str(generated), "-I" + str(generated / "launcher"), "-I" + str(generated / "launcher/minecraft"), "-I" + str(qt / "include/qt6"), "-I" + str(qt / "include/qt6/QtCore")]
    objects = []
    log = build / "build.log"
    header_time = max(p.stat().st_mtime for p in generated.rglob("*.h"))
    with log.open("a" if args.resume else "w", encoding="utf-8") as output:
        for index, source in enumerate([*(generated / s for s in sources), HERE / "Reference.cpp"]):
            obj = build / (str(index) + ".o")
            if args.resume and obj.exists() and obj.stat().st_mtime >= max(source.stat().st_mtime, header_time):
                print(f"KEEP {source.name}", flush=True)
                objects.append(str(obj))
                continue
            print(f"CXX {source.name}", flush=True)
            subprocess.run([*common, "-c", str(source), "-o", str(obj)], stdout=output, stderr=subprocess.STDOUT, check=True)
            objects.append(str(obj))
        subprocess.run([str(compiler), *objects, "-Wl,--gc-sections", "-L" + str(qt / "lib"), "-lQt6Core", "-o", str(build / "prism-reference.exe")], stdout=output, stderr=subprocess.STDOUT, check=True)
    (build / "identity.json").write_text(json.dumps({"commit": COMMIT, "compiler": subprocess.check_output([str(compiler), "--version"], text=True).splitlines()[0], "compilerSha256": sha(compiler), "executableSha256": sha(build / "prism-reference.exe"), "qtCoreSha256": sha(qt / "bin/Qt6Core.dll"), "sourceLockSha256": sha(HERE / "source-lock.json"), "toolchainLockSha256": sha(HERE / "toolchain-lock.json"), "adapterSourceSha256": sha(HERE / "Reference.cpp"), "transformationsSha256": sha(root / "transformations.json"), "serialTranslationUnits": len(objects)}, indent=2), encoding="utf-8")
    print(f"Built {build / 'prism-reference.exe'}")


if __name__ == "__main__":
    main()
