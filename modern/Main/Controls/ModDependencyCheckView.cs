// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Inspector = PCL.ModDependencyInspector;
using CheckController = PCLCS.ModDependencyCheckViewController<PCL.ModDependencyInspector.InspectionContext, PCL.ModDependencyInspector.ModInfo, PCL.ModDependencyInspector.Dependency, PCL.ModDependencyInspector.DependencyEdge, PCL.ModDependencyInspector.InspectionIssue, PCL.ModDependencyInspector.RemovalImpact, PCL.ModDependencyInspector.InspectionReport, PCL.ModDependencyInspector.OnlineProjectInfo, PCL.ModDependencyInspector.OnlineDependency, PCL.ModDependencyInspector.OnlineFileInfo, PCL.ResourceVersion, PCL.ModDependencyAutoFill.Item, PCL.ModDependencyActivation.Item, PCL.ModDependencyAutoFill.Plan, PCL.MyCard>;
using CheckHost = PCLCS.ModDependencyCheckViewHost<PCL.ResourceVersion, PCL.ModDependencyInspector.InspectionIssue, PCL.MyCard>;

namespace PCL
{

    internal sealed class ModDependencyCheckView : Grid
    {

        internal double PreferredWidth { get; private set; } = CheckController.PreferredWidth;
        internal readonly StackPanel Groups = new StackPanel();
        internal readonly MyCard Details;

        internal ModDependencyCheckView(Inspector.InspectionReport Report, ModDependencyAutoFill.Plan Plan = null)
        {
            Details = CheckController.Build(this, Groups, Report, Plan, CreateHost());
        }

        // 仅原 LF 自定义控件、实际旧 DTO 及资源平台原语，不保留第二份分组／确认逻辑。
        private static CheckHost CreateHost()
        {
            return new CheckHost()
            {
                VersionDisplay = new Func<ResourceVersion, string>(Version => Version.Display),
                IssueKey = ModDependencyActivation.IssueKey,
                CreateCard = new Func<string, Thickness, MyCard>((Title, Margin) => new MyCard() { Title = Title, Margin = Margin }),
                AddCardChild = new Action<MyCard, UIElement>((Card, Child) => Card.Children.Add(Child)),
                CollapseDetails = CollapseDetails,
                EnsureCaption = new Action<MyCard>(Card => { var Caption = Card.MainTextBlock; }),
                CreateScroll = new Func<UIElement, ScrollViewer>(Content => new MyScrollViewer() { Content = Content }),
                CreateWarningHint = new Func<string, Thickness, FrameworkElement>((Text, Margin) => new MyHint() { Text = Text, Theme = MyHint.Themes.Yellow, Margin = Margin }),
                DefaultIcon = new Func<ImageSource>(() => (ImageSource)new MyBitmap(ModBase.PathImage + "Icons/NoIcon.png"))
            };
        }
        private static void CollapseDetails(MyCard Card, UIElement Content)
        {
            Card.CanSwap = true;
            Card.Children.Add(Content);
            Card.SwapControl = (FrameworkElement)Content;
            Card.IsSwapped = true;
        }
    }
}