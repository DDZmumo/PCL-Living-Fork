// SPDX-License-Identifier: GPL-3.0-only
// Original standalone adapter linked only with the pinned Prism reference.
// Upstream files retain their authors, license texts and per-file notices.
#include <QCoreApplication>
#include <QCryptographicHash>
#include <QDir>
#include <QFile>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <iostream>
#include "Json.h"
#include "minecraft/LaunchProfile.h"
#include "minecraft/OneSixVersionFormat.h"

QStringList referenceUnboundGameArguments(const LaunchProfile* profile);

static QJsonArray strings(const QStringList& values) { return QJsonArray::fromStringList(values); }
static QJsonObject library(const LibraryPtr& lib, const RuntimeContext& runtime, const QString& localRoot) {
    if (!lib) return {{"missing", true}};
    QStringList jars, native, native32, native64;
    lib->getApplicableFiles(runtime, jars, native, native32, native64, localRoot);
    return {{"name", lib->rawName().serialize()}, {"local", lib->isLocal()}, {"native", lib->isNative()},
            {"classifier", lib->getCompatibleNative(runtime)}, {"jars", strings(jars)}, {"natives", strings(native)},
            {"natives32", strings(native32)}, {"natives64", strings(native64)},
            {"definition", OneSixVersionFormat::libraryToJson(lib.get())}};
}

int main(int argc, char** argv) {
    QCoreApplication app(argc, argv);
    if (argc != 2) { std::cerr << "Usage: prism-reference CASE.json\n"; return 2; }
    QFile input(QString::fromLocal8Bit(argv[1]));
    if (!input.open(QIODevice::ReadOnly)) return 3;
    auto parsed = Json::requireDocument(input.readAll(), "reference case");
    if (!parsed || !parsed->isObject()) return 4;
    const auto root = parsed->object();
    const auto env = root["runtime"].toObject();
    RuntimeContext runtime;
    runtime.system = env["system"].toString();
    runtime.javaArchitecture = env["javaArchitecture"].toString();
    runtime.javaRealArchitecture = env["javaRealArchitecture"].toString();
    if (runtime.system.isEmpty() || runtime.javaArchitecture.isEmpty() || runtime.javaRealArchitecture.isEmpty()) return 5;
    const auto globalRoot = root["globalLibraryRoot"].toString();
    const auto localRoot = root["instanceLibraryRoot"].toString();
    if (!QDir::isAbsolutePath(globalRoot) || !QDir::isAbsolutePath(localRoot)) return 6;
    LaunchProfile profile;
    QJsonArray components;
    for (const auto value : root["components"].toArray()) {
        const auto component = value.toObject();
        QFile metadata(component["path"].toString());
        if (!metadata.open(QIODevice::ReadOnly)) return 7;
        const auto bytes = metadata.readAll();
        const auto doc = Json::requireDocument(bytes, metadata.fileName());
        if (!doc) { std::cerr << doc.error().toStdString(); return 8; }
        auto version = OneSixVersionFormat::versionFileFromJson(*doc, metadata.fileName(), false);
        if (!version) { std::cerr << version.error().toStdString(); return 9; }
        auto patch = *version;
        if (patch->uid != component["uid"].toString()) return 10;
        for (auto& lib : patch->libraries) lib->setStoragePrefix(globalRoot);
        for (auto& lib : patch->mavenFiles) lib->setStoragePrefix(globalRoot);
        if (patch->mainJar) patch->mainJar->setStoragePrefix(globalRoot);
        QJsonArray problems;
        for (const auto& problem : patch->getProblems()) problems.append(QJsonObject{{"severity", int(problem.m_severity)}, {"description", problem.m_description}});
        components.append(QJsonObject{{"uid", patch->uid}, {"version", patch->version},
            {"sourceSha256", QString::fromLatin1(QCryptographicHash::hash(bytes, QCryptographicHash::Sha256).toHex())},
            {"problems", problems}});
        if (component["enabled"].toBool(true)) patch->applyTo(&profile, runtime);
    }
    QStringList classpath, natives;
    profile.getLibraryFiles(runtime, classpath, natives, localRoot, QString(), false);
    QJsonArray libraries, nativeLibraries, mavenFiles, majors;
    for (const auto& lib : profile.getLibraries()) libraries.append(library(lib, runtime, localRoot));
    for (const auto& lib : profile.getNativeLibraries()) nativeLibraries.append(library(lib, runtime, localRoot));
    for (const auto& lib : profile.getMavenFiles()) mavenFiles.append(library(lib, runtime, localRoot));
    for (const auto major : profile.getCompatibleJavaMajors()) majors.append(major);
    auto traits = profile.getTraits().values();
    traits.sort(); // Traits are a set; only output formatting is ordered here.
    QJsonObject output{{"kind", "PrismNativeReferenceSubset"}, {"upstreamCommit", "899d2f3957215a09fe398a8a8cbb8a8a9c619968"},
        {"qtVersion", qVersion()}, {"runtime", env}, {"components", components},
        {"mainClass", profile.getMainClass()}, {"mainJar", library(profile.getMainJar(), runtime, localRoot)},
        {"minecraftVersion", profile.getMinecraftVersion()}, {"minecraftArguments", profile.getMinecraftArguments()},
        {"gameArguments", strings(referenceUnboundGameArguments(&profile))}, {"jvmArguments", strings(profile.getAddnJvmArguments())},
        {"tweakers", strings(profile.getTweakers())}, {"compatibleJavaMajorsRaw", majors},
        {"compatibleJavaName", profile.getCompatibleJavaName()}, {"traits", strings(traits)},
        {"classpath", strings(classpath)}, {"nativeJars", strings(natives)}, {"libraries", libraries},
        {"nativeLibraries", nativeLibraries}, {"mavenFiles", mavenFiles}, {"problemSeverity", int(profile.getProblemSeverity())},
        {"scope", "Native version parsing/applyTo/LaunchProfile/Library/Rule/Gradle/Version; original pre-binding game-token prefix. Explicit ordered inputs; no PackProfile resolver, downloads, credentials, JVM creation, GUI or game."}};
    std::cout << QJsonDocument(output).toJson(QJsonDocument::Indented).constData();
    return 0;
}
