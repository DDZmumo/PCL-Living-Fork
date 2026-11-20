// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

package org.pcllf.keybinds.io;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;

public final class KeybindWriterTest {
    public static void main(String[] args) throws Exception {
        Path directory = Files.createDirectory(Path.of(args[0]).resolve("test-options"));
        Path file = directory.resolve("options.txt");
        Files.write(file, List.of("lang:en_us", "key_mod.action:key.keyboard.r:CONTROL",
            "key_mod.waiting:key.keyboard.p"));
        try (var writer = KeybindWriter.open(file.toFile())) {
            // Minecraft's FieldAccess writes the key, separator and value separately.
            writer.print("key_mod.action");
            writer.print(':');
            writer.println("key.keyboard.k");
            writer.println("lang:zh_cn");
        }
        require(Files.readAllLines(file).equals(List.of("key_mod.action:key.keyboard.r:CONTROL",
            "lang:zh_cn", "key_mod.waiting:key.keyboard.p")), "Early save duplicated or changed a binding.");

        try (var writer = KeybindWriter.open(file.toFile())) {
            writer.println("key_mod.action:key.keyboard.k");
            Files.writeString(file, "key_mod.action:key.keyboard.f8\n");
        }
        require(Files.readString(file).equals("key_mod.action:key.keyboard.f8\n"), "Early save overwrote changed options.");

        Files.writeString(file, "\ufeffkey_mod.action:key.keyboard.k\r\nlang:zh_cn\r\nkey_mod.action:key.keyboard.r:CONTROL\r\n");
        KeybindWriter.repairStartupOptions(file.toString());
        require(Files.readString(file).equals("\ufefflang:zh_cn\r\nkey_mod.action:key.keyboard.r:CONTROL\r\n"),
            "Startup repair did not keep the last binding and UTF-8 BOM.");

        KeybindWriter.beginLoad(true);
        try (var writer = KeybindWriter.open(file.toFile())) {
            writer.print("key_mod.action:");
            writer.println("key.keyboard.y");
        }
        require(Files.readAllLines(file).equals(List.of("key_mod.action:key.keyboard.y")), "New bindings could not be saved.");
    }

    private static void require(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }
}
