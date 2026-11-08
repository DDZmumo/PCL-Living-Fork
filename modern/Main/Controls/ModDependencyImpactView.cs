// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media;
using Inspector = PCL.ModDependencyInspector;
using PCLCS;
using ImpactController = PCLCS.ModDependencyImpactViewController<PCL.ModDependencyInspector.InspectionContext, PCL.ModDependencyInspector.ModInfo, PCL.ModDependencyInspector.Dependency, PCL.ModDependencyInspector.DependencyEdge, PCL.ModDependencyInspector.InspectionIssue, PCL.ModDependencyInspector.RemovalImpact, PCL.ModDependencyInspector.InspectionReport, PCL.ModDependencyInspector.OnlineProjectInfo, PCL.ModDependencyInspector.OnlineDependency, PCL.ModDependencyInspector.OnlineFileInfo, PCL.ModDependencyImpactView.Item>;

namespace PCL
{

    internal sealed class ModDependencyImpactView : Grid
    {

        internal class Item : ModDependencyImpactItemData
        {
        }
        internal readonly Grid Cards = new Grid();
        internal double PreferredWidth { get; private set; } = ImpactController.PreferredWidth;

        internal static List<Item> Collect(Inspector.InspectionReport Report, IEnumerable<string> Paths)
        {
            return ImpactController.Collect(Report, Paths);
        }
        internal ModDependencyImpactView(List<Item> Items, string ActionName)
        {
            ImpactController.Build(this, Cards, Items, ActionName, () => (ImageSource)new MyBitmap(ModBase.PathImage + "Icons/NoIcon.png"));
        }
    }
}