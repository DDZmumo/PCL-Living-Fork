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
using DialogBase = PCLCS.ModpackDialogController<System.Windows.Controls.Button, System.Windows.Controls.ScrollViewer>;
using PreviewController = PCLCS.ModpackUpdatePreviewController<PCL.PackUpdateFile, PCL.PackUpdatePackage, PCL.PackUpdateChange, PCL.PackUpdatePlan, PCL.ModpackUpdatePreviewView.ChangeKind, System.Windows.Controls.Button, System.Windows.Controls.ScrollViewer>;
using VersionController = PCLCS.ModpackVersionSelectController<PCL.ResourceVersion, PCL.GlassDialogChoiceRow, System.Windows.Controls.Button, System.Windows.Controls.ScrollViewer>;

namespace PCL
{

    internal abstract class ModpackDialogView : DialogBase, IGlassDialogContent, IGlassDialogConfirmation
    {
        internal ScrollViewer PageScroller => Scroller;
        internal void FitToPage(FrameworkElement notice, FrameworkElement footer)
        {
            Height = double.NaN;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            Header.Margin = new Thickness();
            Filters.Margin = new Thickness(0, 13, 0, 7);
            // Keep the scroll track's column when a shorter tab hides its scrollbar.
            // Read its styled width so the fixed header and footer also follow any theme or DPI change.
            Scroller.Loaded += (_, _) => {
                if (Scroller.Template?.FindName("PART_VerticalScrollBar", Scroller) is System.Windows.Controls.Primitives.ScrollBar bar &&
                    VisualTreeHelper.GetParent(bar) is Grid layout)
                    System.Windows.Data.BindingOperations.SetBinding(layout.ColumnDefinitions[Grid.GetColumn(bar)], ColumnDefinition.MinWidthProperty,
                        new System.Windows.Data.Binding(nameof(Width)) { Source = bar });
            };
            // The rows occupy the scrollable viewport. Its width excludes the scrollbar at every DPI.
            foreach (var element in new FrameworkElement[] { Header, Filters, notice, footer })
            {
                element.HorizontalAlignment = HorizontalAlignment.Left;
                element.SetBinding(WidthProperty, new System.Windows.Data.Binding(nameof(ScrollViewer.ViewportWidth)) { Source = Scroller });
            }
            void SpaceTabs()
            {
                int count = Math.Max(1, Filters.Columns);
                for (int i = 0; i < Filters.Children.Count; i++)
                {
                    int column = i % count;
                    // Split the gap across neighbouring cells without indenting either outside edge.
                    ((FrameworkElement)Filters.Children[i]).Margin = new Thickness(6d * column / count, 0, 6d * (count - 1 - column) / count, 6);
                }
            }
            SpaceTabs();
            Filters.SizeChanged += (_, args) => { if (args.WidthChanged) SpaceTabs(); };
        }
        protected ModpackDialogView(string Title, string Subtitle, int FilterCount, bool instancePage = false) : base(Title, Subtitle, FilterCount, new ModpackDialogHost<Button, ScrollViewer>()
        {
            AnimationName = () => "Modpack Dialog " + ModBase.GetUuid(),
            CreateButton = () => GlassDialogControls.Button(""),
            SetButtonText = (button, text) => button.Content = text,
            HighlightButton = (button, highlight) => button.Style =
                GlassDialogControls.Style(highlight ? "GlassPresentationAccentButton" : "GlassPresentationButton"),
            OnClick = (button, work) => button.Click += (_, _) => { if (button.IsEnabled) work(); },
            CreateScroll = GlassDialogControls.Scroll,
            CreateRadio = (text, selected, select) =>
            {
                var control = new RadioButton { Content = text, IsChecked = selected, Style = GlassDialogControls.Style("GlassPresentationRadioBox") };
                control.Checked += (_, _) => select();
                return control;
            },
            CreateCheck = (text, selected, change) =>
            {
                var control = GlassDialogControls.Check(text, selected);
                control.Checked += (_, _) => change(true);
                control.Unchecked += (_, _) => change(false);
                return control;
            },
            CreateSection = instancePage ? GlassPageLayout.TitledSettingsCard : GlassDialogControls.Section,
            CreateHint = (text, tone) => {
                var notice = GlassDialogControls.Notice(text, (DialogTone)tone);
                if (instancePage) notice.Margin = new Thickness(0, 10, 0, 12);
                return notice;
            },
            UpdateHint = (element, text, tone) => GlassDialogControls.UpdateNotice((Border)element, text, (DialogTone)tone),
            AnimationControl = () => ModAnimation.AniControlEnabled,
            StopAnimation = ModAnimation.AniStop,
            AnimateOpacity = (element, value, duration, name) => ModAnimation.AniStart(ModAnimation.AaOpacity(element, value, duration), name)
        })
        {
            Resources.MergedDictionaries.Add(GlassPageResources.GetSharedResources());
            Resources[typeof(Button)] = GlassDialogControls.Style("GlassPresentationButton");
            Resources[typeof(TextBox)] = GlassDialogControls.Style("GlassPresentationTextBox");
            Resources[typeof(ComboBox)] = GlassDialogControls.Style("GlassPresentationComboBox");
            var palette = new GlassPageResources(this);
            Loaded += (_, _) => palette.ApplyPalette();
        }
    }

    internal sealed class ModpackVersionSelectView : ModpackDialogView
    {
        private readonly List<ResourceVersion> Versions;
        private ResourceVersion SelectedVersion;
        internal readonly List<GlassDialogChoiceRow> Selections = new();
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
            string group = "Modpack releases " + ModBase.GetUuid();
            Controller = new VersionController(this, Items, Selections, new ModpackVersionSelectHost<ResourceVersion, GlassDialogChoiceRow>()
            {
                CreateItem = () => new GlassDialogChoiceRow(group),
                Configure = (item, version, info, tooltip) => item.Configure(version.Display, info, version, tooltip),
                Checked = item => item.IsSelected,
                OnCheck = (item, work) => item.Selected += work,
                OnChanged = (item, work) => item.SelectionChanged += work,
                Select = value => { SelectedVersion = value; SetCanConfirm(value is not null); },
                SetChannel = new Action<int>(Value => Channel = (ResourceVersion.ReleaseTypes)Value),
                SelectionChanged = new Action(() => SelectionChanged?.Invoke(this, EventArgs.Empty))
            });
            Versions = Controller.Versions;
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
            Controller = new PreviewController(this, Plan, CurrentMinecraft, CurrentLoaders, Counts, Value => Kind = (ChangeKind)Value, Text => GlassDialogControls.Notice(Text, DialogTone.Warning),
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
