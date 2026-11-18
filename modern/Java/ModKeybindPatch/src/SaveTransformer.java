// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

package org.pcllf.keybinds;

import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.Instrumentation;
import java.security.ProtectionDomain;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Set;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.AbstractInsnNode;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.FieldInsnNode;
import org.objectweb.asm.tree.FrameNode;
import org.objectweb.asm.tree.InsnList;
import org.objectweb.asm.tree.MethodInsnNode;
import org.objectweb.asm.tree.MethodNode;
import org.objectweb.asm.tree.TypeInsnNode;
import org.objectweb.asm.tree.VarInsnNode;

final class SaveTransformer implements ClassFileTransformer {
    private static final String OPTIONS = "net/minecraft/client/Options";
    private static final String PRINT_WRITER = "java/io/PrintWriter";
    private static final String STREAM_WRITER = "java/io/OutputStreamWriter";
    private static final String FILE_STREAM = "java/io/FileOutputStream";
    private static final String WRITER = "org/pcllf/keybinds/io/KeybindWriter";
    private final Instrumentation instrumentation;
    Module writerModule;

    SaveTransformer(Instrumentation instrumentation) {
        this.instrumentation = instrumentation;
        // Force resolution while this class is still in the system loader.
        new ClassNode(Opcodes.ASM9);
    }

    @Override
    public byte[] transform(Module module, ClassLoader loader, String name, Class<?> redefined,
                            ProtectionDomain domain, byte[] original) {
        if (!OPTIONS.equals(name) || module == null || !"minecraft".equals(module.getName()) || redefined != null)
            return null;
        try {
            byte[] changed = rewrite(original);
            if (changed == null) {
                System.err.println("[PCLLF-KEYBINDS] No supported early-save pattern detected.");
                return null;
            }
            if (!module.canRead(writerModule))
                instrumentation.redefineModule(module, Set.of(writerModule), Map.of(), Map.of(), Set.of(), Map.of());
            System.out.println("[PCLLF-KEYBINDS] Startup keybind preservation enabled.");
            return changed;
        } catch (RuntimeException | LinkageError error) {
            System.err.println("[PCLLF-KEYBINDS] Compatibility patch skipped: " + error.getClass().getSimpleName());
            return null;
        }
    }

    static byte[] rewrite(byte[] original) {
        var input = new ClassReader(original);
        var type = new ClassNode(Opcodes.ASM9);
        input.accept(type, 0);
        if (!OPTIONS.equals(type.name)) return null;
        // Forge's upstream fix keeps the unmapped keys through startup.
        if (type.fields.stream().anyMatch(field -> "unknownKeys".equals(field.name) &&
                "Ljava/util/Map;".equals(field.desc))) return null;
        MethodNode lateLoad = type.methods.stream().filter(method -> "load".equals(method.name) && "(Z)V".equals(method.desc) &&
            (method.access & (Opcodes.ACC_STATIC | Opcodes.ACC_ABSTRACT | Opcodes.ACC_NATIVE)) == 0).findFirst().orElse(null);
        if (lateLoad == null || lateLoad.instructions.size() == 0) return null;
        for (MethodNode method : type.methods) {
            if (!"save".equals(method.name) || !"()V".equals(method.desc) || (method.access & Opcodes.ACC_STATIC) != 0)
                continue;
            var prefix = new ArrayList<AbstractInsnNode>(12);
            for (var insn = method.instructions.getFirst(); insn != null && prefix.size() < 12; insn = insn.getNext()) {
                if (insn instanceof FrameNode) return null;
                if (insn.getOpcode() >= 0) prefix.add(insn);
            }
            if (!expectedWriter(prefix)) return null;
            // Preserve the original method, exception handlers, and saved fields.
            // Replace just the straight-line constructor for the UTF-8 options writer.
            var factory = new InsnList();
            factory.add(new VarInsnNode(Opcodes.ALOAD, 0));
            factory.add(new FieldInsnNode(Opcodes.GETFIELD, OPTIONS, "optionsFile", "Ljava/io/File;"));
            factory.add(new MethodInsnNode(Opcodes.INVOKESTATIC, WRITER, "open",
                "(Ljava/io/File;)Ljava/io/PrintWriter;", false));
            method.instructions.insertBefore(prefix.get(0), factory);
            for (var insn : prefix) method.instructions.remove(insn);
            var readiness = new InsnList();
            readiness.add(new VarInsnNode(Opcodes.ILOAD, 1));
            readiness.add(new MethodInsnNode(Opcodes.INVOKESTATIC, WRITER, "beginLoad", "(Z)V", false));
            lateLoad.instructions.insert(readiness);
            lateLoad.maxStack = Math.max(lateLoad.maxStack, 1);
            var output = new ClassWriter(input, 0);
            type.accept(output);
            return output.toByteArray();
        }
        return null;
    }

    private static boolean expectedWriter(List<AbstractInsnNode> code) {
        return code.size() == 12 &&
            newAndDup(code, 0, PRINT_WRITER) && newAndDup(code, 2, STREAM_WRITER) && newAndDup(code, 4, FILE_STREAM) &&
            code.get(6) instanceof VarInsnNode var && var.getOpcode() == Opcodes.ALOAD && var.var == 0 &&
            field(code.get(7), Opcodes.GETFIELD, OPTIONS, "optionsFile", "Ljava/io/File;") &&
            constructor(code.get(8), FILE_STREAM, "(Ljava/io/File;)V") &&
            field(code.get(9), Opcodes.GETSTATIC, "java/nio/charset/StandardCharsets", "UTF_8", "Ljava/nio/charset/Charset;") &&
            constructor(code.get(10), STREAM_WRITER, "(Ljava/io/OutputStream;Ljava/nio/charset/Charset;)V") &&
            constructor(code.get(11), PRINT_WRITER, "(Ljava/io/Writer;)V");
    }

    private static boolean newAndDup(List<AbstractInsnNode> code, int index, String type) {
        return code.get(index) instanceof TypeInsnNode ctor && ctor.getOpcode() == Opcodes.NEW &&
            type.equals(ctor.desc) && code.get(index + 1).getOpcode() == Opcodes.DUP;
    }

    private static boolean field(AbstractInsnNode node, int op, String owner, String name, String desc) {
        return node instanceof FieldInsnNode field && field.getOpcode() == op &&
            owner.equals(field.owner) && name.equals(field.name) && desc.equals(field.desc);
    }

    private static boolean constructor(AbstractInsnNode node, String owner, String desc) {
        return node instanceof MethodInsnNode call && call.getOpcode() == Opcodes.INVOKESPECIAL &&
            owner.equals(call.owner) && "<init>".equals(call.name) && desc.equals(call.desc);
    }
}
