using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeloongCore;
using MeloongCore.Extensions;

namespace PCL;

public partial class PageSpeedLeft {
    // MMC discovers the final instance name after reading the archive. Titles are
    // presentation, and two distinct operations can also legitimately share one.
    private readonly Dictionary<ModLoader.LoaderBase, MyCard> MmcImportCards = new(ReferenceEqualityComparer.Instance);

    private bool TryRefreshMmcImport(ModLoader.LoaderBase loader) {
        if (loader is not IMmcImportProgressOperation operation) return false;
        bool terminal = loader.State is ModBase.LoadState.Finished or ModBase.LoadState.Canceled;
        if (terminal && operation.ProducerCompleted) {
            if (MmcImportCards.Remove(loader, out var old)) ModAnimation.AniDispose(old, true, _ => TryReturnToHome());
            return true;
        }
        if (!MmcImportCards.TryGetValue(loader, out var card)) {
            card = (MyCard)ModBase.GetObjectFromXML("<local:MyCard xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"clr-namespace:PCL;assembly=Plain Craft Launcher 2\" Margin=\"0,0,0,15\"><Grid Margin=\"14,40,15,10\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"50\"/><ColumnDefinition/></Grid.ColumnDefinitions></Grid></local:MyCard>");
            MmcImportCards.Add(loader, card); ModMain.FrmSpeedRight.PanMain.Children.Insert(0, card);
            var cancel = new MyIconButton {
                Name = "BtnCancel", Logo = "F1 M2,0 L0,2 8,10 0,18 2,20 10,12 18,20 20,18 12,10 20,2 18,0 10,8 2,0Z",
                Height = 20, Margin = new Thickness(0, 10, 10, 0), LogoScale = 1.1,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top
            };
            card.Children.Add(cancel);
            cancel.Click += (_, _) => {
                if (loader.State is ModBase.LoadState.Loading or ModBase.LoadState.Waiting) {
                    // Keep both taskbar entry and card until the actual producer has
                    // returned. A decided transaction rejects this cancellation.
                    cancel.IsEnabled = false;
                    ModBase.RunInThread(() => { loader.Cancel(); ModBase.RunInUi(() => TaskRefresh(loader)); });
                } else if (operation.ProducerCompleted) {
                    MmcImportCards.Remove(loader); ModLoader.LoaderTaskbar.Remove(loader);
                    ModAnimation.AniDispose(card, true, _ => TryReturnToHome());
                }
            };
            Logger.Info($"新建任务管理卡片：{loader.Name}");
        }
        card.Title = loader.Name;
        var button = card.Children.OfType<MyIconButton>().Single(value => value.Name == "BtnCancel");
        button.IsEnabled = loader.State == ModBase.LoadState.Failed || !operation.ImportProgress.CancellationRequested && !operation.ImportProgress.DecisionStarted;
        button.ToolTip = operation.ImportProgress.CancellationRequested ? "正在取消，等待文件操作结束" :
            operation.ImportProgress.DecisionStarted ? "正在保存导入结果，请等待文件操作结束" : loader.State == ModBase.LoadState.Failed ? "关闭任务" : "取消导入";
        RefreshMmcImportStages((Grid)card.Children[3], operation.ImportProgress.Snapshot(loader.State), loader.State == ModBase.LoadState.Failed ? loader.Error : null);
        return true;
    }

    private bool TryRemoveMmcImport(object value) {
        if (value is not ModLoader.LoaderBase loader || loader is not IMmcImportProgressOperation operation) return false;
        // TaskRemove is also called by Taskbar.Add. It must only address this exact
        // root, and must not hide an already-running import with active writers.
        if (loader.State == ModBase.LoadState.Loading && !operation.ProducerCompleted) return true;
        ModBase.RunInUiWait(() => {
            if (MmcImportCards.Remove(loader, out var card)) ModMain.FrmSpeedRight?.PanMain.Children.Remove(card);
        });
        return true;
    }

    private static void RefreshMmcImportStages(Grid rows, MmcImportStageSnapshot[] stages, Exception error) {
        string diagnostic = error?.GetDisplay(true);
        string signature = string.Join("\n", stages.Select(stage => stage.State + ":" + stage.Detail + ":" + stage.Progress + ":" + stage.CompletedBytes + ":" + stage.TotalBytes + ":" + stage.CompletedFiles + ":" + stage.TotalFiles)) + "\n" + diagnostic;
        if (Equals(rows.Tag, signature)) return;
        rows.Tag = signature; rows.Children.Clear(); rows.RowDefinitions.Clear();
        foreach (var stage in stages) {
            int index = rows.RowDefinitions.Count;
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 26 });
            string state = stage.State switch {
                MmcImportStageState.Running when stage.Progress.HasValue => Math.Floor(stage.Progress.Value * 100) + "%",
                MmcImportStageState.Running => "进行中", MmcImportStageState.Cancelling => "取消中",
                MmcImportStageState.Canceled => "已取消", MmcImportStageState.NotRun => "未执行", MmcImportStageState.Skipped => "跳过", _ => null
            };
            FrameworkElement marker;
            if (state is not null) {
                marker = new TextBlock { Text = state, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
                marker.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush3");
            } else {
                bool done = stage.State == MmcImportStageState.Finished, failed = stage.State == MmcImportStageState.Failed;
                marker = new System.Windows.Shapes.Path {
                    Data = Geometry.Parse(done ? "F1 M23.7501,33.25 L34.8334,44.3333 52.2499,22.1668 56.9999,26.9168 34.8334,53.8333 19.0001,38 Z" :
                        failed ? "F1 M2.5,0 L0,2.5 7.5,10 0,17.5 2.5,20 10,12.5 17.5,20 20,17.5 12.5,10 20,2.5 17.5,0 10,7.5 Z" :
                        "F1 M5,0 a5,5 360 1 0 0,0.0001 m15,0 a5,5 360 1 0 0,0.0001 m15,0 a5,5 360 1 0 0,0.0001 Z"),
                    Stretch = Stretch.Uniform, Width = done || failed ? 15 : 18, Height = done ? 16 : failed ? 15 : 6,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, done ? 3 : failed ? 1 : 7, 0, 0)
                };
                marker.SetResourceReference(System.Windows.Shapes.Path.FillProperty, "ColorBrush3");
            }
            marker.Tag = stage.State;
            marker.ToolTip = stage.Detail ?? stage.State switch {
                MmcImportStageState.Finished => "已完成", MmcImportStageState.Waiting => "等待执行", MmcImportStageState.Failed => "失败", _ => state
            };
            Grid.SetRow(marker, index); rows.Children.Add(marker);
            var label = new StackPanel { Tag = stage.Stage, Margin = new Thickness(0, 0, 0, 5), ToolTip = stage.Detail };
            var title = new TextBlock { Text = stage.Name, TextWrapping = TextWrapping.Wrap };
            title.SetResourceReference(TextBlock.ForegroundProperty, stage.State is MmcImportStageState.Running or MmcImportStageState.Cancelling ? "ColorBrush3" : "ColorBrush1");
            label.Children.Add(title);
            string caption = stage.Detail;
            if (stage.CompletedFiles is { } completedFiles) caption += " " + completedFiles + (stage.TotalFiles is { } knownTotal ? "/" + knownTotal : "");
            else if (stage.TotalFiles is { } totalFiles) caption += "（共 " + totalFiles + " 个文件）";
            if (stage.TotalBytes > 0) caption += " · " + StringUtils.FormatByteSize(stage.CompletedBytes) + " / " + StringUtils.FormatByteSize(stage.TotalBytes);
            else if (stage.CompletedBytes > 0) caption += " · 累计 " + StringUtils.FormatByteSize(stage.CompletedBytes);
            if (!string.IsNullOrWhiteSpace(caption)) {
                var detail = new TextBlock { Text = caption, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 1), Tag = "mmc-import-detail" };
                detail.SetResourceReference(TextBlock.ForegroundProperty, stage.State == MmcImportStageState.Cancelling ? "ColorBrush3" : "ColorBrushGray2"); label.Children.Add(detail);
            }
            Grid.SetColumn(label, 1); Grid.SetRow(label, index); rows.Children.Add(label);
        }
        if (diagnostic is not null) {
            int index = rows.RowDefinitions.Count; rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var message = new TextBlock { Text = diagnostic, TextWrapping = TextWrapping.Wrap, ToolTip = "单击复制错误详情", Margin = new Thickness(0, 4, 0, 5), Tag = "mmc-import-error" };
            message.MouseLeftButtonDown += (_, _) => { ModBase.ClipboardSet(message.Text, false); ModMain.Hint("已复制错误详情！", ModMain.HintType.Green); };
            Grid.SetColumn(message, 1); Grid.SetRow(message, index); rows.Children.Add(message);
        }
    }
}
