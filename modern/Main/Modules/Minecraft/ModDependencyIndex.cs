// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Threading;
using IndexCore = PCLCS.ModDependencyInspection<PCL.ModDependencyInspector.InspectionContext, PCL.ModDependencyInspector.ModInfo, PCL.ModDependencyInspector.Dependency, PCL.ModDependencyInspector.DependencyEdge, PCL.ModDependencyInspector.InspectionIssue, PCL.ModDependencyInspector.RemovalImpact, PCL.ModDependencyInspector.InspectionReport, PCL.ModDependencyInspector.OnlineProjectInfo, PCL.ModDependencyInspector.OnlineDependency, PCL.ModDependencyInspector.OnlineFileInfo>.Index;

namespace PCL
{

    // Original LF names; the persistent graph, leases and file transactions live in C#.
    internal sealed class ModDependencyIndex
    {
        private ModDependencyIndex()
        {
        }
        private const int Schema = IndexCore.Schema;
        private const long MaxBytes = IndexCore.MaxBytes;
        internal static string CacheDirectory
        {
            get
            {
                return IndexCore.CacheDirectory;
            }
            set
            {
                IndexCore.CacheDirectory = value;
            }
        }
        internal static string IndexPath(string Root, ModDependencyInspector.InspectionContext Context)
        {
            return IndexCore.IndexPath(Root, Context);
        }
        internal static void ClearMemory()
        {
            IndexCore.ClearMemory();
        }
        internal static ModDependencyInspector.InspectionReport Inspect(string Root, ModDependencyInspector.InspectionContext Context, CancellationToken Token, bool Force)
        {
            return IndexCore.Inspect(Root, Context, Token, Force);
        }
        internal static void RememberOnline(string Root, ModDependencyInspector.InspectionReport Tree, CancellationToken Token)
        {
            IndexCore.RememberOnline(Root, Tree, Token);
        }
    }
}