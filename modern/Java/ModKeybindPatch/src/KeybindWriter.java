// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

package org.pcllf.keybinds.io;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.OutputStreamWriter;
import java.io.PrintWriter;
import java.io.StringWriter;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

public final class KeybindWriter extends StringWriter {
    private static final int MAX_OPTIONS_BYTES = 4 * 1024 * 1024;
    private static volatile boolean bindingsReady;
    private final Path file;
    private final String snapshot;
    private final Map<String, String> savedBindings;
    private boolean closed;

    private KeybindWriter(Path file, String snapshot, Map<String, String> savedBindings) {
        this.file = file;
        this.snapshot = snapshot;
        this.savedBindings = savedBindings;
    }

    public static PrintWriter open(File file) throws IOException {
        if (!bindingsReady) {
            try {
                Path path = file.toPath().toAbsolutePath();
                String original = readOptions(path);
                if (original != null) {
                    Map<String, String> saved = readBindings(original);
                    if (!saved.isEmpty()) return new PrintWriter(new KeybindWriter(path, original, saved));
                }
            } catch (IOException | SecurityException ignored) {
                // Unreadable input keeps the game's ordinary save behavior.
            }
        }
        return new PrintWriter(new OutputStreamWriter(new FileOutputStream(file), StandardCharsets.UTF_8));
    }

    public static void beginLoad(boolean keybindsOnly) {
        // The loader's second read runs after all keys have been registered.
        // Once it begins, game saves must accept new user choices as usual.
        if (keybindsOnly) bindingsReady = true;
    }

    private static String readOptions(Path path) throws IOException {
        if (!path.isAbsolute() || path.getFileName() == null || !"options.txt".equalsIgnoreCase(path.getFileName().toString()) ||
                !Files.isRegularFile(path, LinkOption.NOFOLLOW_LINKS)) return null;
        try (var input = Files.newInputStream(path, LinkOption.NOFOLLOW_LINKS)) {
            byte[] bytes = input.readNBytes(MAX_OPTIONS_BYTES + 1);
            if (bytes.length > MAX_OPTIONS_BYTES) return null;
            return StandardCharsets.UTF_8.newDecoder().decode(ByteBuffer.wrap(bytes)).toString();
        }
    }

    private static Map<String, String> readBindings(String text) {
        var result = new LinkedHashMap<String, String>();
        withoutBom(text).lines().forEach(line -> {
            String key = bindingKey(line);
            if (key != null) result.put(key, line);
        });
        return result;
    }

    private static String withoutBom(String text) {
        return text.startsWith("\ufeff") ? text.substring(1) : text;
    }

    private static String bindingKey(String line) {
        if (!line.startsWith("key_")) return null;
        int colon = line.indexOf(':');
        return colon > 4 && line.indexOf('\0') < 0 ? line.substring(0, colon) : null;
    }

    private static List<String> lastBindingRows(List<String> lines) {
        var result = new ArrayList<String>(lines.size());
        var seen = new HashSet<String>();
        for (int index = lines.size() - 1; index >= 0; index--) {
            String line = lines.get(index);
            String key = bindingKey(line);
            if (key == null || seen.add(key)) result.add(line);
        }
        Collections.reverse(result);
        return result;
    }

    public static void repairStartupOptions(String fileName) {
        if (fileName == null || fileName.isBlank()) return;
        try {
            Path path = Path.of(fileName);
            String text = readOptions(path);
            if (text == null) return;
            List<String> rows = withoutBom(text).lines().toList();
            List<String> cleaned = lastBindingRows(rows);
            if (rows.size() == cleaned.size()) return;
            String newline = text.contains("\r\n") ? "\r\n" : text.contains("\n") ? "\n" : "\r";
            String bom = text.startsWith("\ufeff") ? "\ufeff" : "";
            String ending = text.endsWith("\n") || text.endsWith("\r") ? newline : "";
            replaceOptions(path, text, bom + String.join(newline, cleaned) + ending);
            System.out.println("[PCLLF-KEYBINDS] Removed duplicate startup keybind rows.");
        } catch (IOException | IllegalArgumentException | SecurityException error) {
            System.err.println("[PCLLF-KEYBINDS] Cannot repair startup options: " + error.getClass().getSimpleName());
        }
    }

    private static void replaceOptions(Path file, String expected, String text) throws IOException {
        Path temporary = Files.createTempFile(file.getParent(), ".pcllf-options-", ".tmp");
        try {
            Files.writeString(temporary, text, StandardCharsets.UTF_8);
            if (!expected.equals(readOptions(file))) throw new IOException("Options changed during startup");
            Files.move(temporary, file, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
        } finally {
            Files.deleteIfExists(temporary);
        }
    }

    @Override
    public void close() throws IOException {
        synchronized (lock) {
            if (closed) return;
            closed = true;
            if (getBuffer().isEmpty()) return;
            // Process completed rows so both print(key)/println(value) and
            // println(fullRow) follow the same merge rules.
            List<String> lines = lastBindingRows(toString().lines().toList());
            for (int index = 0; index < lines.size(); index++) {
                String saved = savedBindings.remove(bindingKey(lines.get(index)));
                if (saved != null) lines.set(index, saved);
            }
            lines.addAll(savedBindings.values());
            String newline = System.lineSeparator();
            replaceOptions(file, snapshot, String.join(newline, lines) + newline);
        }
    }
}
