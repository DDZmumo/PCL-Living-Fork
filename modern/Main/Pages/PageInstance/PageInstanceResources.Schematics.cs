// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.IO;
using System.Windows;
using SchematicKit;

namespace PCL
{
    // 投影页（Kind == Schematics）的预览入口与「投影管理」按钮。
    public partial class PageInstanceResources
    {
        /// <summary>该页是否是实例的「投影」资源页。</summary>
        internal bool IsSchematicPage => Kind == InstanceResourceKind.Schematics;

        protected override void OnInitialized(EventArgs Args)
        {
            base.OnInitialized(Args);
            if (!IsSchematicPage) return;
            BtnSchematicManager.Visibility = Visibility.Visible;
            BtnSchematicManager.Click += (Sender, EventArgs) => SchematicManagerOpen();
        }

        private void SchematicManagerOpen()
        {
            // 「更多 → 投影管理」是同一页面的独立入口。
            ModMain.FrmMain?.PageChange(new FormMain.PageStackData { Page = FormMain.PageType.Schematics });
        }

        /// <summary>在启动器窗口内打开投影 3D 预览。</summary>
        internal void ShowSchematicPreview(InstanceResourceEntry Entry)
        {
            if (Entry is null || Entry.IsDirectory) return;
            string TargetPath = Entry.FullPath;
            if (string.IsNullOrEmpty(TargetPath) || !File.Exists(TargetPath))
            {
                ModMain.Hint("投影文件已被移动或删除，请刷新列表后重试。", ModMain.HintType.Red);
                return;
            }

            SchematicPreviewOverlay.Show(TargetPath, SchematicSupport.ResolveJarPath(PageInstanceLeft.Instance), Entry.Name);
        }
    }
}
