// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCLCS;
using DialogBase = PCLCS.ModpackDialogController<PCL.MyButton, PCL.MyScrollViewer>;
using PreviewController = PCLCS.ModpackUpdatePreviewController<PCL.PackUpdateFile, PCL.PackUpdatePackage, PCL.PackUpdateChange, PCL.PackUpdatePlan, PCL.ModpackUpdatePreviewView.ChangeKind, PCL.MyButton, PCL.MyScrollViewer>;
using VersionController = PCLCS.ModpackVersionSelectController<PCL.ResourceVersion, PCL.MyListItem, PCL.MyButton, PCL.MyScrollViewer>;

namespace PCL
{

    internal abstract class ModpackDialogView : DialogBase
    {
        internal new double PreferredWidth { get; private set; } = 740d;
        internal new readonly StackPanel Header;
        internal new readonly System.Windows.Controls.Primitives.UniformGrid Filters;
        internal new readonly StackPanel Rows;
        private readonly MyScrollViewer Scroll;
        private readonly string AnimationName;
        protected ModpackDialogView(string Title, string Subtitle, int FilterCount) : base(Title, Subtitle, FilterCount, new ModpackDialogHost<MyButton, MyScrollViewer>()
        {
            AnimationName = new Func<string>(() => "Modpack Dialog " + ModBase.GetUuid()),
            CreateButton = new Func<MyButton>(() => new MyButton()),
            SetButtonText = new Action<MyButton, string>((Button, Text) => Button.Text = Text),
            HighlightButton = new Action<MyButton, bool>((Button, Highlight) => Button.ColorType = Highlight ? MyButton.ColorState.Highlight : MyButton.ColorState.Normal),
            OnClick = new Action<MyButton, Action>((Button, Work) => Button.Click += (Sender, Args) => Work()),
            CreateScroll = new Func<MyScrollViewer>(() => new MyScrollViewer()),
            CreateRadio = (text, selected, select) =>
            {
                MyRadioBox control = new() { Text = text, Checked = selected };
                control.Check += (_, _) => select();
                return control;
            },
            CreateCheck = (text, selected, change) =>
            {
                MyCheckBox control = new() { Text = text, Checked = selected };
                control.Change += (_, _) => change(control.Checked);
                return control;
            },
            CreateSection = (title, content) => {
                var card = new MyCard { Title = title, Margin = new Thickness(3, 3, 8, 14) };
                card.Children.Add(new Border { Child = content, Padding = new Thickness(16, 40, 16, 14) });
                return card;
            },
            CreateHint = (text, tone) => new MyHint { Text = text, Theme = (MyHint.Themes)tone, Margin = new Thickness(0, 10, 0, 12) },
            UpdateHint = (element, text, tone) => { var hint = (MyHint)element; hint.Text = text; hint.Theme = (MyHint.Themes)tone; },
            AnimationControl = new Func<int>(() => ModAnimation.AniControlEnabled),
            StopAnimation = new Action<string>(Name => ModAnimation.AniStop(Name)),
            AnimateOpacity = new Action<UIElement, double, int, string>((Element, Value, Duration, Name) => ModAnimation.AniStart(ModAnimation.AaOpacity(Element, Value, Duration), Name))
        })
        {
            Header = base.Header;
            Filters = base.Filters;
            Rows = base.Rows;
            Scroll = Scroller;
            AnimationName = AnimationKey;
        }
    }

    internal sealed class ModpackVersionSelectView : ModpackDialogView
    {
        private readonly List<ResourceVersion> Versions;
        private ResourceVersion SelectedVersion;
        internal readonly List<MyListItem> Selections = new List<MyListItem>();
        internal event EventHandler SelectionChanged;
        private ResourceVersion.ReleaseTypes Channel;
        private readonly VersionController Controller;
        internal ResourceVersion Selected
        {
            get
            {
                return SelectedVersion;
            }
        }
        internal ResourceVersion.ReleaseTypes ActiveChannel
        {
            get
            {
                return Channel;
            }
            private set
            {
                Channel = value;
            }
        }
        internal ModpackVersionSelectView(IEnumerable<ResourceVersion> Items, string ProjectName, string CurrentVersion) : base(ProjectName, "当前版本：" + (string.IsNullOrWhiteSpace(CurrentVersion) ? "未记录" : CurrentVersion), 3)
        {
            Controller = new VersionController(this, Items, Selections, new ModpackVersionSelectHost<ResourceVersion, MyListItem>()
            {
                CreateItem = new Func<MyListItem>(() => new MyListItem()),
                Configure = ConfigureItem,
                Checked = new Func<MyListItem, bool>(Item => Item.Checked),
                OnCheck = new Action<MyListItem, Action>((Item, Work) => Item.Check += (Sender, Args) => Work()),
                OnChanged = new Action<MyListItem, Action>((Item, Work) => Item.Changed += (Sender, Args) => Work()),
                Select = new Action<ResourceVersion>(Value => SelectedVersion = Value),
                SetChannel = new Action<int>(Value => Channel = (ResourceVersion.ReleaseTypes)Value),
                SelectionChanged = new Action(() => SelectionChanged?.Invoke(this, EventArgs.Empty))
            });
            Versions = Controller.Versions;
        }
        private static void ConfigureItem(MyListItem Item, ResourceVersion Version, string Info, string ToolTip)
        {
            Item.Title = Version.Display;
            Item.Info = Info;
            Item.Type = MyListItem.CheckType.RadioBox;
            Item.Height = 64d;
            Item.PaddingLeft = 14;
            Item.MinPaddingRight = 12;
            Item.Margin = new Thickness(3d, 0d, 7d, 8d);
            Item.Tag = Version;
            Item.Background = PCLLF.Presentation.Foundation.LegacyThemePalette.Brush("ColorBrushWhite");
            Item.ToolTip = ToolTip;
        }
        private static ResourceVersion.ReleaseTypes ChannelOf(ResourceVersion Version)
        {
            return (ResourceVersion.ReleaseTypes)VersionController.ChannelOf(Version);
        }
        private static string ChannelName(ResourceVersion.ReleaseTypes Kind)
        {
            return VersionController.ChannelName((int)Kind);
        }
        internal void ShowChannel(ResourceVersion.ReleaseTypes Kind)
        {
            Controller.ShowChannel((int)Kind);
        }
    }

    internal sealed class ModpackUpdatePreviewView : ModpackDialogView
    {
        internal enum ChangeKind
        {
            Added,
            Updated,
            Removed,
            Preserved
        }
        private readonly List<PackUpdateChange> Changes;
        internal readonly Dictionary<ChangeKind, int> Counts = new Dictionary<ChangeKind, int>();
        private ChangeKind Kind;
        private readonly PreviewController Controller;
        internal ChangeKind ActiveKind
        {
            get
            {
                return Kind;
            }
            private set
            {
                Kind = value;
            }
        }
        internal ModpackUpdatePreviewView(PackUpdatePlan Plan, string CurrentMinecraft, Dictionary<ModLoaders, string> CurrentLoaders) : this(Plan, CurrentMinecraft, CurrentLoaders, false) { }
        internal ModpackUpdatePreviewView(PackUpdatePlan Plan, string CurrentMinecraft, Dictionary<ModLoaders, string> CurrentLoaders, bool ValidateSelection) : base(PreviewController.PackageTitle(Plan.Package), RuntimeSummary(Plan.Package, CurrentMinecraft, CurrentLoaders), 4)
        {
            Controller = new PreviewController(this, Plan, CurrentMinecraft, CurrentLoaders, Counts, Value => Kind = (ChangeKind)Value, Text => new MyHint() { Text = Text, Theme = MyHint.Themes.Yellow, Margin = new Thickness(0d, 10d, 0d, 0d) },
                ValidateSelection ? ModpackUpdate.Engine.SnapshotForPreview : null, ValidateSelection ? ModpackUpdate.Engine.ReviewPreview : null);
            Changes = Controller.Changes;
        }
        internal System.Threading.Tasks.Task CloseAndDrainAsync() => Controller.CloseAndDrainAsync();
        internal void ShowCompatibility() => Controller.ShowKind(4);
        internal static ChangeKind Classify(PackUpdateChange Change)
        {
            return (ChangeKind)PreviewController.Classify(Change);
        }
        private static string KindName(ChangeKind Value)
        {
            return PreviewController.KindName((int)Value);
        }
        private static string RuntimeSummary(PackUpdatePackage Package, string Minecraft, Dictionary<ModLoaders, string> Loaders)
        {
            return PreviewController.RuntimeSummary(Package, Minecraft, Loaders);
        }
        private static string FolderName(string Relative)
        {
            return PreviewController.FolderName(Relative);
        }
        internal void ShowKind(ChangeKind Value)
        {
            Controller.ShowKind((int)Value);
        }
    }
}
