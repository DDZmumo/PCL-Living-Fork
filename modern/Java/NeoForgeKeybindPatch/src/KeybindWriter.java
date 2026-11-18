// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

package org.pcllf.neoforgekeys.io;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.OutputStreamWriter;
import java.io.PrintWriter;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.util.LinkedHashMap;
import java.util.Map;

public final class KeybindWriter extends PrintWriter {
    private static final int MAX_OPTIONS_BYTES = 4 * 1024 * 1024;
    private static volatile boolean bindingsReady;
    private final Map<String, String> remaining;

    private KeybindWriter(File file, Map<String, String> saved) throws IOException {
        super(new OutputStreamWriter(new FileOutputStream(file), StandardCharsets.UTF_8));
        remaining = saved;
    }

    public static PrintWriter open(File file) throws IOException {
        // The original save truncates the file when opening the stream. Take
        // the snapshot first so missing, not-yet-registered bindings survive.
        Map<String, String> saved = bindingsReady ? Map.of() : readBindings(file.toPath());
        if (saved.isEmpty())
            return new PrintWriter(new OutputStreamWriter(new FileOutputStream(file), StandardCharsets.UTF_8));
        return new KeybindWriter(file, saved);
    }

    public static void beginLoad(boolean keybindsOnly) {
        // NeoForge's second read runs after all keys have been registered.
        // Once it begins, game saves must accept new user choices as usual.
        if (keybindsOnly) bindingsReady = true;
    }

    private static Map<String, String> readBindings(Path path) {
        var result = new LinkedHashMap<String, String>();
        if (path.getFileName() == null || !"options.txt".equalsIgnoreCase(path.getFileName().toString()) ||
                !Files.isRegularFile(path, LinkOption.NOFOLLOW_LINKS)) return result;
        try (var input = Files.newInputStream(path, LinkOption.NOFOLLOW_LINKS)) {
            byte[] bytes = input.readNBytes(MAX_OPTIONS_BYTES + 1);
            if (bytes.length > MAX_OPTIONS_BYTES) return result;
            String text = StandardCharsets.UTF_8.newDecoder().decode(ByteBuffer.wrap(bytes)).toString();
            if (text.startsWith("\ufeff")) text = text.substring(1);
            text.lines().forEach(line -> {
                String key = bindingKey(line);
                if (key != null) result.put(key, line);
            });
        } catch (IOException | SecurityException ignored) {
            result.clear();
        }
        return result;
    }

    private static String bindingKey(String line) {
        if (line == null || !line.startsWith("key_")) return null;
        int colon = line.indexOf(':');
        return colon > 4 && line.indexOf('\0') < 0 ? line.substring(0, colon) : null;
    }

    @Override
    public void println(String line) {
        synchronized (lock) {
            String saved = remaining.remove(bindingKey(line));
            super.println(saved == null ? line : saved);
        }
    }

    @Override
    public void close() {
        synchronized (lock) {
            for (String binding : remaining.values()) super.println(binding);
            remaining.clear();
            super.close();
        }
    }
}
