// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
using System;

namespace PCLCS.Applications;

public static class OfflineLoginPolicy {
    public static string ValidateName(string? name, Func<Version?> readSelectedVersion) {
        name = name?.Trim() ?? "";
        if (name.Length == 0) return "玩家名不能为空！";
        if (name.Contains('"')) return "玩家名不能包含英文引号！";
        if (name.Length > 16 && readSelectedVersion() is { } version && version >= new Version(20, 0, 3))
            return "自 1.20.3 起，玩家名至多只能包含 16 个字符！";
        return "";
    }
}
