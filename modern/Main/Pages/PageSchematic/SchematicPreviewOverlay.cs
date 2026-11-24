// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeloongCore;
using SchematicKit;

namespace PCL
{
    /// <summary>
    ///     投影预览：铺在启动器正文区，外观用 LF 控件（MyButton / MyCard / MyCheckBox / Slider），
    ///     3D 用 SchematicKit 的 Viewer。不做成独立窗口，也不受玻璃界面页面投影影响。
    /// </summary>
    internal static class SchematicPreviewOverlay
    {
        private static Grid PanelRoot;
        private static Border Frame;
        private static TextBlock TitleText;
        private static Border ViewportHost;
        private static StackPanel BlockList;
        private static CheckBox LayerCheck;
        private static Slider LayerSlider;
        private static TextBlock LayerText;
        private static SchematicSession Session;
        private static SchematicViewer3D Viewer;
        private static Grid LiveHost;
        private static FrameworkElement LiveAnchor;
        private static Panel LiveLayer;

        internal static bool IsOpen => PanelRoot is not null && PanelRoot.Visibility == Visibility.Visible;

        /// <summary>把预览直接嵌进宿主容器（例如投影管理页自己的 PanPreview），范围由页面决定。</summary>
        internal static void ShowIn(Grid Host, string Path, string? JarPath, string Title)
        {
            if (Host is null) return;
            PanelRoot = Host;
            SchematicSupport.EnsureInitialized();
            if (Frame is null) BuildUi();
            Frame.HorizontalAlignment = HorizontalAlignment.Stretch;
            Frame.VerticalAlignment = VerticalAlignment.Stretch;
            Frame.Margin = new Thickness(0);
            Frame.Width = double.NaN;
            Frame.Height = double.NaN;
            PanelRoot.Visibility = Visibility.Visible;
            TitleText.Text = Title ?? System.IO.Path.GetFileName(Path);
            // 新界面把页面投影成镜像，Viewport3D 不会跟着渲染；把预览整体搬进正文卡片位置，3D 才是活的。
            if (ModMain.FrmMain is { GlassShellActive: true } Main && Main.GlassView is { } View &&
                VisualTreeHelper.GetParent(View) is Panel Layer && View.FindName("BodyFrame") is FrameworkElement Body)
            {
                Rect Bounds = Body.TransformToAncestor(Layer).TransformBounds(new Rect(Body.RenderSize));
                if (!ReferenceEquals(Host.Parent, Layer))
                {
                    (Host.Parent as Panel)?.Children.Remove(Host);
                    Layer.Children.Add(Host);
                }
                Host.HorizontalAlignment = HorizontalAlignment.Left;
                Host.VerticalAlignment = VerticalAlignment.Top;
                Host.Margin = new Thickness(Bounds.X, Bounds.Y, 0, 0);
                Host.Width = Bounds.Width;
                Host.Height = Bounds.Height;
                System.Windows.Controls.Panel.SetZIndex(Host, 40);
            }
            _ = LoadAsync(Path, JarPath);
        }

        /// <summary>从页面里的宿主容器收起预览。</summary>

        /// <summary>直接用 SchematicKit 的预览视图（与 SE 的投影页一致：返回/标题/详情 + 深色 3D 卡 + 方块统计 + 分层条）。</summary>
        internal static async void ShowSeView(Grid Host, string Path, string? JarPath, string Title, Action OnBack = null)
        {
            if (Host is null) return;
            SchematicSupport.EnsureInitialized();
            try
            {
                CloseSession();
                Session = await SchematicSupport.Service.LoadAsync(Path, JarPath).ConfigureAwait(true);
                if (Session.Data.IsEmpty)
                {
                    ModMain.Hint("无法预览该投影文件：格式暂不支持或文件已损坏。", ModMain.HintType.Red);
                    return;
                }

                void Back() { HideIn(Host); OnBack?.Invoke(); }
                var View = new SchematicPreviewView(Title ?? System.IO.Path.GetFileName(Path), Session.Info,
                    Session.Data, Session.Textures, null, Back, JarPath ?? SchematicSupport.Service.ResolveJarPath(Path));
                Host.Children.Clear();
                Host.Children.Add(View);
                Host.Visibility = Visibility.Visible;

                if (ModMain.FrmMain is { GlassShellActive: true } Main && Main.GlassView is { } Glass &&
                    VisualTreeHelper.GetParent(Glass) is Panel Layer && Glass.FindName("PageScene") is FrameworkElement Body)
                {
                    Rect Bounds = Body.TransformToAncestor(Layer).TransformBounds(new Rect(Body.RenderSize));
                    if (!ReferenceEquals(Host.Parent, Layer))
                    {
                        (Host.Parent as Panel)?.Children.Remove(Host);
                        Layer.Children.Add(Host);
                    }
                    Host.HorizontalAlignment = HorizontalAlignment.Left;
                    Host.VerticalAlignment = VerticalAlignment.Top;
                    Host.Margin = new Thickness(Bounds.X + 16, Bounds.Y + 12, 0, 0);
                    Host.Width = Math.Max(360, Bounds.Width - 32);
                    Host.Height = Math.Max(260, Bounds.Height - 24);
                    AttachLiveLayout(Host, Body, Layer);
                    System.Windows.Controls.Panel.SetZIndex(Host, 40);
                }
            }
            catch (Exception Error)
            {
                Logger.Error(Error, "加载投影预览失败");
                ModMain.Hint("加载投影预览失败，详见日志。", ModMain.HintType.Red);
            }
        }

        /// <summary>让预览跟着窗口/页面一起拉伸：锚点尺寸一变就重算矩形。</summary>
        private static void AttachLiveLayout(Grid Host, FrameworkElement Anchor, Panel Layer)
        {
            if (ReferenceEquals(LiveAnchor, Anchor) && ReferenceEquals(LiveHost, Host)) return;
            if (LiveAnchor is not null) LiveAnchor.SizeChanged -= OnLiveAnchorSizeChanged;
            LiveHost = Host; LiveAnchor = Anchor; LiveLayer = Layer;
            Anchor.SizeChanged += OnLiveAnchorSizeChanged;
        }

        private static void OnLiveAnchorSizeChanged(object Sender, SizeChangedEventArgs Args) => RelayoutLive();

        private static void RelayoutLive()
        {
            if (LiveHost is null || LiveAnchor is null || LiveLayer is null) return;
            Rect Bounds = LiveAnchor.TransformToAncestor(LiveLayer).TransformBounds(new Rect(LiveAnchor.RenderSize));
            LiveHost.Margin = new Thickness(Bounds.X + 16, Bounds.Y + 12, 0, 0);
            LiveHost.Width = Math.Max(360, Bounds.Width - 32);
            LiveHost.Height = Math.Max(260, Bounds.Height - 24);
        }
        internal static void HideIn(Grid Host)
        {
            if (Host is null) return;
            Host.Visibility = Visibility.Collapsed;
            // 页面切走时静默收起：解绑尺寸跟随，不触发任何“返回”回调。
            if (LiveAnchor is not null) LiveAnchor.SizeChanged -= OnLiveAnchorSizeChanged;
            LiveHost = null; LiveAnchor = null; LiveLayer = null;
            if (ReferenceEquals(PanelRoot, Host)) CloseSession();
        }
        internal static void Show(string Path, string? JarPath, string Title)
        {
            if (ModMain.FrmMain is not { } Main) return;
            PanelRoot = Main.PanSchematicPreview;
            if (PanelRoot is null) return;
            SchematicSupport.EnsureInitialized();
            AttachToActiveShell(Main);
            if (Frame is null) BuildUi();
            LayoutFrame(Main);
            PanelRoot.Visibility = Visibility.Visible;
            Main.PreviewKeyDown -= OverlayKeyDown;
            Main.PreviewKeyDown += OverlayKeyDown;
            TitleText.Text = Title ?? System.IO.Path.GetFileName(Path);
            _ = LoadAsync(Path, JarPath);
        }

        private static void AttachToActiveShell(FormMain Main)
        {
            // 玻璃界面把页面重建成镜像树；浮层要挂到新界面的可视根上才是活的 WPF。
            if (Main.GlassShellActive && Main.GlassView is { } GlassView &&
                VisualTreeHelper.GetParent(GlassView) is Panel GlassLayer && !ReferenceEquals(PanelRoot.Parent, GlassLayer))
            {
                (PanelRoot.Parent as Panel)?.Children.Remove(PanelRoot);
                Grid.SetRow(PanelRoot, 0);
                Grid.SetRowSpan(PanelRoot, 2);
                PanelRoot.Margin = new Thickness();
                System.Windows.Controls.Panel.SetZIndex(PanelRoot, 40);
                GlassLayer.Children.Add(PanelRoot);
            }
        }

        private static void BuildUi()
        {
            var Root = new Grid();
            Root.Background = (Brush)Application.Current.TryFindResource("ColorBrushBg0") ?? new SolidColorBrush(ModTheme.IsDarkTheme ? Color.FromRgb(0x22,0x22,0x22) : Color.FromRgb(0xF7,0xF7,0xF7));
            Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 顶栏：返回 / 标题 / 详情
            var Header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            Header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var Back = new MyButton { Text = "返回", Width = 96, Height = 34 };
            Back.Click += (Sender, Args) => Hide();
            Header.Children.Add(Back);
            TitleText = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            TitleText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
            Grid.SetColumn(TitleText, 1);
            Header.Children.Add(TitleText);
            var Detail = new MyButton { Text = "详情", Width = 80, Height = 34 };
            Detail.Click += (Sender, Args) => ShowDetails();
            Grid.SetColumn(Detail, 2);
            Header.Children.Add(Detail);
            Root.Children.Add(Header);

            // 正文：3D + 所需方块
            var Body = new Grid();
            Body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            Grid.SetRow(Body, 1);
            Root.Children.Add(Body);

            ViewportHost = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x26)),
                CornerRadius = new CornerRadius(6),
                ClipToBounds = true,
                Margin = new Thickness(0, 0, 14, 0)
            };
            Body.Children.Add(ViewportHost);

            var ListCard = new MyCard { Margin = new Thickness(0) };
            var ListRoot = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
            ListRoot.Children.Add(new TextBlock { Text = "所需方块", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            BlockList = new StackPanel();
            ListRoot.Children.Add(new ScrollViewer { Content = BlockList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 520 });
            ListCard.BorderChild = ListRoot;
            Grid.SetColumn(ListCard, 1);
            Body.Children.Add(ListCard);

            // 底栏：分层显示
            var Footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            LayerCheck = new CheckBox { Content = "分层显示", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) };
            LayerCheck.Click += (Sender, Args) => SyncLayer();
            Footer.Children.Add(LayerCheck);
            LayerSlider = new Slider { Width = 260, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 1 };
            LayerSlider.ValueChanged += (Sender, Args) => SyncLayer();
            Footer.Children.Add(LayerSlider);
            LayerText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Opacity = 0.7 };
            Footer.Children.Add(LayerText);
            var Hint = new TextBlock { Text = "拖动旋转 · 滚轮缩放 · WASD 移动 · R 复位", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0.5 };
            var FooterGrid = new Grid();
            FooterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            FooterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            FooterGrid.Children.Add(Footer);
            Grid.SetColumn(Hint, 1);
            FooterGrid.Children.Add(Hint);
            Grid.SetRow(FooterGrid, 2);
            Root.Children.Add(FooterGrid);

            Frame = new Border { Child = Root, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            PanelRoot.Children.Add(Frame);
            PanelRoot.MouseLeftButtonDown += (Sender, Args) =>
            {
                if (ReferenceEquals(Args.OriginalSource, PanelRoot)) Hide();
            };
        }

        private static void LayoutFrame(FormMain Main)
        {
            if (Frame is null) return;
            if (Main.GlassShellActive && Main.GlassView?.FindName("BodyFrame") is FrameworkElement Body &&
                VisualTreeHelper.GetParent(PanelRoot) is Visual Root)
            {
                Rect Bounds = Body.TransformToAncestor(Root).TransformBounds(new Rect(Body.RenderSize));
                Frame.Margin = new Thickness(Bounds.X + 24, Bounds.Y + 22, 0, 0);
                Frame.Width = Math.Max(420, Bounds.Width - 48);
                Frame.Height = Math.Max(300, Bounds.Height - 38);
            }
            else
            {
                Frame.HorizontalAlignment = HorizontalAlignment.Stretch;
                Frame.VerticalAlignment = VerticalAlignment.Stretch;
                Frame.Margin = new Thickness(25, 10, 25, 10);
                Frame.Width = double.NaN;
                Frame.Height = double.NaN;
            }
        }

        private static async System.Threading.Tasks.Task LoadAsync(string Path, string? JarPath)
        {
            try
            {
                CloseSession();
                Session = await SchematicSupport.Service.LoadAsync(Path, JarPath).ConfigureAwait(true);
                if (Session.Data.IsEmpty)
                {
                    Hide();
                    ModMain.Hint("无法预览该投影文件：格式暂不支持或文件已损坏。", ModMain.HintType.Red);
                    return;
                }

                ViewportHost.Child = null;
                Viewer = null;
                if (Session.Textures is not null && SchematicViewer3D.CanRender(Session.Data))
                {
                    Viewer = new SchematicViewer3D(Session.Data, Session.Textures);
                    ViewportHost.Child = Viewer;
                }
                else
                {
                    ViewportHost.Child = new TextBlock
                    {
                        Text = "该投影过大或缺少客户端 JAR，无法渲染 3D，只能查看方块列表。",
                        Foreground = Brushes.White,
                        Margin = new Thickness(20),
                        TextWrapping = TextWrapping.Wrap
                    };
                }

                RenderBlocks(Session.Data);
                SetupLayers();
            }
            catch (Exception Error)
            {
                Logger.Error(Error, "加载投影预览失败");
                Hide();
                ModMain.Hint("加载投影预览失败，详见日志。", ModMain.HintType.Red);
            }
        }

        private static void RenderBlocks(SchematicData Data)
        {
            BlockList.Children.Clear();
            foreach (var Group in Data.Blocks.GroupBy(Block => Block.Name)
                         .Select(Group => (Name: Group.Key, Count: Group.Count()))
                         .OrderByDescending(Entry => Entry.Count).Take(24))
            {
                var Row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                Row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                Row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Color Color = BlockColors.Get(Group.Name);
                Row.Children.Add(new Border
                {
                    Width = 10, Height = 10, CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(Color), VerticalAlignment = VerticalAlignment.Center
                });
                var Name = new TextBlock { Text = (Group.Name ?? "未知方块").Replace("minecraft:", ""), FontSize = 12, Margin = new Thickness(8, 0, 10, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(Name, 1);
                Row.Children.Add(Name);
                var Count = new TextBlock { Text = "×" + Group.Count, FontSize = 11, Opacity = 0.6 };
                Grid.SetColumn(Count, 2);
                Row.Children.Add(Count);
                BlockList.Children.Add(Row);
            }
        }

        private static void SetupLayers()
        {
            bool Multi = Viewer is not null && Viewer.MaxLayer > Viewer.MinLayer;
            LayerCheck.IsEnabled = Multi;
            LayerSlider.IsEnabled = Multi;
            LayerCheck.IsChecked = false;
            Viewer.VisibleTopLayer = null;
            Viewer.VisibleTopLayer = null;
            if (!Multi)
            {
                LayerText.Text = "";
                return;
            }
            LayerSlider.Minimum = Viewer.MinLayer;
            LayerSlider.Maximum = Viewer.MaxLayer;
            LayerSlider.Value = Viewer.MaxLayer;
            SyncLayer();
        }

        private static void SyncLayer()
        {
            if (Viewer is null) return;
            bool Split = LayerCheck.IsChecked == true;
            Viewer.VisibleTopLayer = Split ? (int)LayerSlider.Value : null;
            LayerText.Text = Split ? $"第 {LayerSlider.Value:0} 层" : "全部层";
        }

        private static void ShowDetails()
        {
            if (Session is null) return;
            ModMain.MyMsgBox(Session.Info, "投影详情");
        }

        private static void OverlayKeyDown(object Sender, KeyEventArgs Args)
        {
            if (Args.Key != Key.Escape) return;
            Args.Handled = true;
            Hide();
        }

        internal static void Hide()
        {
            if (PanelRoot is null) return;
            if (ModMain.FrmMain is { } Main)
            {
                Main.PreviewKeyDown -= OverlayKeyDown;
                PanelRoot.Visibility = Visibility.Collapsed;
            }
            ViewportHost?.Child?.Let(Child => (Child as IDisposable)?.Dispose());
            ViewportHost.Child = null;
            Viewer = null;
            CloseSession();
        }

        private static void CloseSession()
        {
            Session?.Dispose();
            Session = null;
        }
    }

    internal static class PreviewOverlayExtensions
    {
        internal static void Let<T>(this T Value, Action<T> Work) => Work(Value);
    }
}
