// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

package org.pcllf.keybinds;

import java.lang.instrument.Instrumentation;
import java.nio.charset.StandardCharsets;
import java.nio.file.Path;
import java.util.Base64;
import java.util.jar.JarFile;

public final class Agent {
    public static void premain(String encodedOptionsPath, Instrumentation instrumentation) {
        try {
            // The loader supplies ASM. Resolve it from the agent's loader before
            // making the writer visible to the game's module.
            var transformer = new SaveTransformer(instrumentation);
            var jarPath = Path.of(Agent.class.getProtectionDomain().getCodeSource().getLocation().toURI());
            instrumentation.appendToBootstrapClassLoaderSearch(new JarFile(jarPath.toFile()));
            var writer = Class.forName("org.pcllf.keybinds.io.KeybindWriter", false, null);
            if (encodedOptionsPath != null) {
                String optionsPath = new String(Base64.getDecoder().decode(encodedOptionsPath), StandardCharsets.UTF_8);
                writer.getMethod("repairStartupOptions", String.class).invoke(null, optionsPath);
            }
            transformer.writerModule = writer.getModule();
            instrumentation.addTransformer(transformer);
        } catch (Exception | LinkageError error) {
            System.err.println("[PCLLF-KEYBINDS] Compatibility patch unavailable: " + error.getClass().getSimpleName());
        }
    }
}
