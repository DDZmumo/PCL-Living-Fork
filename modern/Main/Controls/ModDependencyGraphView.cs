// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Windows.Controls;
using System.Windows.Media;
using Inspector = PCL.ModDependencyInspector;
using PCLCS;
using GraphController = PCLCS.ModDependencyGraphController<PCL.ModDependencyInspector.InspectionContext, PCL.ModDependencyInspector.ModInfo, PCL.ModDependencyInspector.Dependency, PCL.ModDependencyInspector.DependencyEdge, PCL.ModDependencyInspector.InspectionIssue, PCL.ModDependencyInspector.RemovalImpact, PCL.ModDependencyInspector.InspectionReport, PCL.ModDependencyInspector.OnlineProjectInfo, PCL.ModDependencyInspector.OnlineDependency, PCL.ModDependencyInspector.OnlineFileInfo, PCL.ModDependencyGraphView.GraphNode>;

namespace PCL
{

    // 原 LF 图节点身份与弹窗入口；完整关系、布局、渲染及异步图标生命周期由 C# 维护。
    internal sealed class ModDependencyGraphView : Grid
    {

        private readonly GraphController Controller;
        internal readonly Canvas GraphCanvas;
        internal double PreferredWidth
        {
            get
            {
                return Controller.PreferredWidth;
            }
        }

        internal class GraphNode : ModDependencyGraphNodeData
        {
        }

        internal ModDependencyGraphView(Inspector.InspectionReport Report, string FullPath)
        {
            Controller = new GraphController(this, Report, FullPath, new ModDependencyGraphHost()
            {
                DefaultIcon = new Func<string, ImageSource>(Name => (ImageSource)new MyBitmap(ModBase.PathImage + Name)),
                ReadIcon = ModDependencyIcon.Read
            });
            GraphCanvas = Controller.GraphCanvas;
        }

        internal void LoadIconsForPreview()
        {
            Controller.LoadIconsForPreview();
        }
    }
}