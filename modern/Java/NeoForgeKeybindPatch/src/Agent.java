// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

package org.pcllf.neoforgekeys;

import java.lang.instrument.Instrumentation;
import java.nio.file.Path;
import java.util.jar.JarFile;

public final class Agent {
    public static void premain(String unused, Instrumentation instrumentation) {
        try {
            // NeoForge already supplies ASM on the boot module path. Resolve it from
            // the agent's loader before making the writer visible to the game layer.
            var transformer = new SaveTransformer(instrumentation);
            var jarPath = Path.of(Agent.class.getProtectionDomain().getCodeSource().getLocation().toURI());
            instrumentation.appendToBootstrapClassLoaderSearch(new JarFile(jarPath.toFile()));
            transformer.writerModule = Class.forName("org.pcllf.neoforgekeys.io.KeybindWriter", false, null).getModule();
            instrumentation.addTransformer(transformer);
        } catch (Exception | LinkageError error) {
            System.err.println("[PCLLF-KEYBINDS] Compatibility patch unavailable: " + error.getClass().getSimpleName());
        }
    }
}
