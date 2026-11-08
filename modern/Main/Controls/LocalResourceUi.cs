// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace PCL
{
    // 模组页与游戏资源页共用的原生选择栏图标和动画。
    internal static class LocalResourceUi
    {
        internal static void InitializeSortButton(MyIconTextButton button, Action toggle, DockPanel header, StackPanel filters)
        {
            button.MinHeight = button.MaxHeight = button.Height = 24;
            button.ShapeLogo.MaxHeight = button.ShapeLogo.MaxWidth = 12;
            button.ShapeLogo.Margin = new Thickness(6, 0, 0, 0);
            button.LabText.FontSize = 12;
            button.LabText.Margin = new Thickness(5, 0, 6, 1);
            button.Focusable = true;
            KeyboardNavigation.SetIsTabStop(button, true);
            button.KeyDown += (_, args) =>
            {
                if (!button.IsEnabled || args.Key is not (Key.Enter or Key.Space)) return;
                toggle();
                args.Handled = true;
            };
            void FitHeader()
            {
                double required = button.DesiredSize.Width;
                foreach (FrameworkElement filter in filters.Children)
                    if (filter.Visibility != Visibility.Collapsed) required += filter.DesiredSize.Width;
                Dock target = required > header.ActualWidth + 0.5 ? Dock.Top : Dock.Right;
                if (DockPanel.GetDock(button) != target) DockPanel.SetDock(button, target);
            }
            header.SizeChanged += (_, _) => FitHeader();
            filters.SizeChanged += (_, _) => FitHeader();
            button.SizeChanged += (_, _) => FitHeader();
            header.Loaded += (_, _) => FitHeader();
        }
        internal static void UpdateSortButton(MyIconTextButton button, int mode)
        {
            button.Text = mode switch { 1 => "添加时间 · 倒序", 2 => "添加时间 · 正序", 3 => "最后游玩 · 倒序", 4 => "最后游玩 · 正序", _ => "默认排序" };
            button.Logo = mode switch
            {
                1 or 3 => "M3 15L0 12L1.4 10.6L2 11.2V1H4V11.2L4.6 10.6L6 12Z M8 2H16V4H8Z M8 7H14V9H8Z M8 12H12V14H8Z",
                2 or 4 => "M3 1L0 4L1.4 5.4L2 4.8V15H4V4.8L4.6 5.4L6 4Z M8 2H12V4H8Z M8 7H14V9H8Z M8 12H16V14H8Z",
                _ => "M1 2H15V4H1Z M1 7H15V9H1Z M1 12H15V14H1Z"
            };
            AutomationProperties.SetName(button, "排序：" + button.Text);
        }

        // 从原模组条目的选中/悬停反馈抽出，行与截图卡片共用相同时间和缓动。
        internal static void AppendItemSelection(List<ModAnimation.AniData> Ani, Border Back, bool Hovered, bool Selected, bool Pressed, bool TintBackground = true)
        {
            int Time = Hovered ? 120 : 180;
            if (Hovered || Selected)
            {
                if (TintBackground)
                    Ani.Add(ModAnimation.AaColor(Back, Border.BackgroundProperty, Pressed ? "ColorBrush6" : "ColorBrushBg1", Time));
                Ani.Add(ModAnimation.AaOpacity(Back, 1d - Back.Opacity, Time, Ease: new ModAnimation.AniEaseOutFluent()));
                Ani.Add(ModAnimation.AaScaleTransform(Back, (Pressed ? 0.996d : 1d) - ((ScaleTransform)Back.RenderTransform).ScaleX, (int)Math.Round(Time * 1.2d), Ease: new ModAnimation.AniEaseOutFluent()));
            }
            else
            {
                Ani.AddRange(new[] { ModAnimation.AaOpacity(Back, -Back.Opacity, Time), ModAnimation.AaScaleTransform(Back, 0.996d - ((ScaleTransform)Back.RenderTransform).ScaleX, Time, Ease: new ModAnimation.AniEaseOutFluent()), ModAnimation.AaScaleTransform(Back, -0.196d, 1, After: true) });
            }
        }
        internal const string PlayLogo = "M52.1,164.5c-1.4,0-3.1-0.5-4.2-1.3c-2.6-1.7-4-4.2-4-7V43.8c0-2.9,1.6-5.8,4.1-7c1.2-0.8,2.7-1.2,4.1-1.2c1.5,0,2.9,0.4,4.2,1.2L153.1,93c0,0,0.1,0,0.1,0.1c2.6,1.7,4,4.2,4,7c0,3-1.7,5.8-4.2,7.1l-96.8,56.2C55.1,164,53.5,164.5,52.1,164.5z M60.4,142.1l72.1-42.1L60.4,58.2V142.1z";
        internal static void ApplySelectionIcons(MyIconTextButton Update, MyIconTextButton Delete, MyIconTextButton Cancel)
        {
            Update.Logo = "M640 768H384l-32-32V509H213L190 454l298-298h45l298 298L810 509h-138v226z m-224-64h192V477l32-32h93L512 223 290 445H384l32 32zM352 831h320v64h-320z";
            Update.LogoScale = 1d;
            Delete.Logo = "M520.192 0C408.43 0 317.44 82.87 313.563 186.734H52.736c-29.038 0-52.663 21.943-52.663 49.079s23.625 49.152 52.663 49.152h58.075v550.473c0 103.35 75.118 187.757 167.717 187.757h472.43c92.599 0 167.716-83.894 167.716-187.757V285.477h52.59c29.038 0 52.59-21.943 52.663-49.08-0.073-27.135-23.625-49.151-52.663-49.151H726.235C723.237 83.017 631.955 0 520.192 0zM404.846 177.957c3.803-50.03 50.176-89.015 107.447-89.015 57.197 0 103.57 38.985 106.788 89.015H404.92zM284.379 933.669c-33.353 0-69.997-39.351-69.997-95.525v-549.01H833.39v549.522c0 56.247-36.645 95.525-69.998 95.525H284.379v-0.512z M357.23 800.695a48.274 48.274 0 0 0 47.616-49.006V471.7a48.274 48.274 0 0 0-47.543-49.08 48.274 48.274 0 0 0-47.69 49.006V751.69c0 27.282 20.846 49.006 47.617 49.006z m166.62 0a48.274 48.274 0 0 0 47.688-49.006V471.7a48.274 48.274 0 0 0-47.689-49.08 48.274 48.274 0 0 0-47.543 49.006V751.69c0 27.282 21.431 49.006 47.543 49.006z m142.92 0a48.274 48.274 0 0 0 47.543-49.006V471.7a48.274 48.274 0 0 0-47.543-49.08 48.274 48.274 0 0 0-47.616 49.006V751.69c0 27.282 20.773 49.006 47.543 49.006z";
            Delete.LogoScale = 0.96d;
            Cancel.Logo = "M867.648 951.296 512 595.648l-355.648 355.648c-11.52 11.52-30.272 11.52-41.856 0L72.64 909.44c-11.52-11.52-11.52-30.272 0-41.856L428.352 512 72.64 156.352c-11.52-11.52-11.52-30.272 0-41.856l41.856-41.856c11.52-11.52 30.272-11.52 41.856 0L512 428.288l355.648-355.648c11.52-11.52 30.272-11.52 41.856 0l41.856 41.856c11.52 11.52 11.52 30.272 0 41.856L595.648 512l355.648 355.648c11.52 11.52 11.52 30.272 0 41.856l-41.856 41.856C897.984 962.88 879.168 962.88 867.648 951.296L867.648 951.296z";
            Cancel.LogoScale = 0.8d;
        }

        internal static void RefreshSelectionBar(MyCard Card, TranslateTransform Transform, int Count, ref int PreviousCount, string AnimationName, bool Animate)
        {
            if (!Animate)
            {
                ModAnimation.AniStop(AnimationName);
                PreviousCount = Count;
                Card.Visibility = Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                Card.Opacity = Count > 0 ? 1 : 0;
                Transform.Y = Count > 0 ? -25 : -10;
                return;
            }
            if (Count > 0)
            {
                if (PreviousCount >= Count)
                {
                    PreviousCount = Count;
                    return;
                }
                PreviousCount = Count;
                Card.Visibility = Visibility.Visible;
                ModAnimation.AniStart(new[] { ModAnimation.AaOpacity(Card, 1d - Card.Opacity, 60), ModAnimation.AaTranslateY(Card, -27 - Transform.Y, 120, Ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)), ModAnimation.AaTranslateY(Card, 3d, 150, 120, Ease: new ModAnimation.AniEaseInoutFluent(ModAnimation.AniEasePower.Weak)), ModAnimation.AaTranslateY(Card, -1, 90, 270, Ease: new ModAnimation.AniEaseInoutFluent(ModAnimation.AniEasePower.Weak)) }, AnimationName);
            }
            else
            {
                if (PreviousCount == 0)
                    return;
                PreviousCount = 0;
                ModAnimation.AniStart(new[] { ModAnimation.AaOpacity(Card, -Card.Opacity, 90), ModAnimation.AaTranslateY(Card, -10 - Transform.Y, 90, Ease: new ModAnimation.AniEaseInFluent(ModAnimation.AniEasePower.Weak)), ModAnimation.AaCode(() => Card.Visibility = Visibility.Collapsed, After: true) }, AnimationName);
            }
        }
    }
}
