// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Windows.Media;
using PCLCS;

namespace PCL
{

    internal sealed class ModDependencyIcon
    {
        // 保留原图视图／异步图标宿主入口，解码与 JAR 生命周期由 C# 实现。
        internal static ImageSource Read(string FileName, string EntryName)
        {
            return ModDependencyIconFiles.Read(FileName, EntryName);
        }
    }
}