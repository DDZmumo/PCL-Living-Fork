using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using MeloongCore;
using MeloongCore.Extensions;
using MeloongCore.Wpf.Extensions;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;
using Newtonsoft.Json.Linq;

namespace PCL
{
    public partial class PageSpeedLeft
    {
        private const int WatcherInterval = 300;
        // 初始化
        private bool IsLoad;
        private readonly PCLCS.FileTaskProgress.IoRateSampler FileRates = new();
        public PageSpeedLeft()
        {
            __LfAttachOwnHandles();
            this.IsLoad = false;
            this.RightCards = new Dictionary<string, MyCard>();
            this.InitializeComponent();
            UpdateMetricLayout(ModBase.ModeDebug);
        }

        private void UpdateMetricLayout(bool debug)
        {
            PanThread.Visibility = debug ? Visibility.Visible : Visibility.Collapsed;
            PanMetrics.RowDefinitions[5].Height = debug ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateMetricLayout(ModBase.ModeDebug);
            FileRates.Reset();
            // 进入时就刷新一次显示
            Watcher();
            // 如果在页面切换动画的 “上一页消失” 部分已经完成了下载，就直接尝试返回
            TryReturnToHome();
            if (IsLoad)
                return;
            IsLoad = true;
            // 监控定时器
            var timer = new System.Windows.Threading.DispatcherTimer()
            {
                Interval = new TimeSpan(0, 0, 0, 0, WatcherInterval)
            };
            timer.Tick += (_, __) => Watcher();
            timer.Start();
        }

        // 定时器任务
        private readonly Dictionary<string, MyCard> RightCards;
        private void Watcher()
        {
            if (!(ModMain.FrmMain.PageCurrent == (FormMain.PageStackData)FormMain.PageType.TaskManager))
                return;
            try
            {
#region 更新左边栏
                if (!ModLoader.LoaderTaskbar.Any())
                {
                    // 无任务
                    this.LabProgress.Text = "100 %";
                    this.LabSpeed.Text = "0 B/s";
                    FileRates.Reset(); this.LabReadSpeed.Text = this.LabWriteSpeed.Text = "0 B/s";
                    this.LabFile.Text = "0";
                    this.LabThread.Text = "0 / " + ModNet.NetTaskThreadLimit;
                }
                else
                {
                    // 有任务，输出基本信息
                    var Tasks = ModLoader.LoaderTaskbar.Where(l => l.Show).ToList(); // 筛选掉启动 MC 的任务（#6270）
                    double RawPercent = Tasks.Any() ? Tasks.Select(l => l.Progress).Average().Clamp(0d, 1d) : 1d;
                    string PredictText = Math.Floor(RawPercent * 100d) + "." + Math.Floor((RawPercent * 100d - Math.Floor(RawPercent * 100d)) * 100d).ToString().EnsureLength('0', 2) + " %";
                    this.LabProgress.Text = RawPercent > 0.999999d ? "100 %" : PredictText;
                    this.LabSpeed.Text = StringUtils.FormatByteSize(ModNet.NetManager.Speed) + "/s";
                    FileRates.Sample();
                    this.LabReadSpeed.Text = StringUtils.FormatByteSize((long)FileRates.ReadPerSecond) + "/s";
                    this.LabWriteSpeed.Text = StringUtils.FormatByteSize((long)FileRates.WritePerSecond) + "/s";
                    this.LabFile.Text = Conversions.ToString(ModNet.NetManager.FileRemain < 0 ? "0*" : global::PCL.ModNet.NetManager.FileRemain);
                    this.LabThread.Text = ModNet.NetTaskThreadCount + " / " + ModNet.NetTaskThreadLimit;
                }
            }
#endregion
            catch (Exception ex)
            {
                Logger.Error(ex, "任务管理左栏监视出错");
            }

            if (ModMain.FrmSpeedRight is null || ModMain.FrmSpeedRight.PanMain is null)
                return;
            try
            {
                foreach (var Loader in ModLoader.LoaderTaskbar.ToList())
                    TaskRefresh(Loader);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "任务管理右栏监视出错");
            }
        }

        public void TaskRefresh(ModLoader.LoaderBase Loader)
        {
            if (Loader is null || !Loader.Show)
                return;
            try
            {
                if (TryRefreshMmcImport(Loader)) return;
                // 获取实际加载器列表
                List<ModLoader.LoaderBase> LoaderList = (List<ModLoader.LoaderBase>)NewLateBinding.LateGet(Loader, null, "GetLoaderList", Array.Empty<object>(), null, null, null);
                var PackStages = ModpackUpdateFlow.GetStageSnapshot(LoaderList);
                if (RightCards.ContainsKey(Loader.Name))
                {
                    // 已有此卡片
                    Grid Card = RightCards[Loader.Name];
                    if (PackStages is not null && Loader.State is ModBase.LoadState.Loading or ModBase.LoadState.Waiting or ModBase.LoadState.Failed &&
                        Card.Children.Count > 3 && Card.Children[3] is Grid PackStageRows)
                    {
                        RefreshPackStages(PackStageRows, PackStages, Loader.State == ModBase.LoadState.Failed ? Loader.Error : null);
                        Card.Tag = Loader.Progress + (double)Loader.State;
                        return;
                    }
                    // Stage text can change during hashing or while waiting for the
                    // preview, even when the aggregate percentage stays the same.
                    if (Loader.State == ModBase.LoadState.Loading && Card.Children.Count > 3 && Card.Children[3] is Grid StageRows)
                        for (int i = 0; i < LoaderList.Count && i * 2 + 1 < StageRows.Children.Count; i++)
                            if (StageRows.Children[i * 2 + 1] is TextBlock StageLabel) StageLabel.Text = LoaderList[i].Name;
                    double NewValue = Loader.Progress + (double)Loader.State;
                    if (ModBase.Val(Card.Tag) == NewValue)
                        return;
                    Card.Tag = NewValue;
                    if (Card.Children.Count <= 3)
                    {
                        Logger.Warn($"元素不足的卡片：{Loader.Name}");
                        return;
                    }

                    Card = (Grid)Card.Children[3];
                    try
                    {
                        switch (Loader.State)
                        {
                            case ModBase.LoadState.Failed:
                            {
#region 失败，更新卡片
                                Card.RowDefinitions.Clear();
                                Card.Children.Clear();
                                Card.Children.Add((UIElement)ModBase.GetObjectFromXML("<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Stretch=\"Uniform\" Tag=\"Failed\" Data=\"F1 M2.5,0 L0,2.5 7.5,10 0,17.5 2.5,20 10,12.5 17.5,20 20,17.5 12.5,10 20,2.5 17.5,0 10,7.5 2.5,0Z\" Height=\"15\" Width=\"15\" HorizontalAlignment=\"Center\" Grid.Column=\"0\" Grid.Row=\"0\" Fill=\"{DynamicResource ColorBrush3}\" Margin=\"0,1,0,0\" VerticalAlignment=\"Top\"/>"));
                                TextBlock Tb = (TextBlock)ModBase.GetObjectFromXML("<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TextWrapping=\"Wrap\" HorizontalAlignment=\"Left\" ToolTip=\"单击复制错误详情\" Grid.Column=\"1\" Grid.Row=\"0\" Margin=\"0,0,0,5\" />");
                                Tb.Text = Loader.Error.GetDisplay(true);
                                Tb.MouseLeftButtonDown += (sender, e) =>
                                {
                                    ModBase.ClipboardSet(((TextBlock)sender).Text, false);
                                    ModMain.Hint("已复制错误详情！", ModMain.HintType.Green);
                                };
                                Card.Children.Add(Tb);
                                break;
                            }

#endregion
                            case ModBase.LoadState.Finished:
                            case ModBase.LoadState.Canceled:
                            {
#region 完成或中断，销毁卡片并返回
                                ModAnimation.AniDispose((MyCard)Card.Parent, true, (_) => TryReturnToHome());
                                break;
                            }

#endregion
                            case ModBase.LoadState.Loading:
                            case ModBase.LoadState.Waiting:
                            {
#region 进度不同，更新卡片
                                do
                                {
                                    try
                                    {
                                        if (Card.Children.Count < LoaderList.Count * 2)
                                        {
                                            Logger.Warn($"刷新任务管理卡片 {Loader.Name} 失败：卡片中仅有 {Card.Children.Count} 个子项，要求至少有 {LoaderList.Count * 2} 个子项");
                                            break;
                                        }

                                        int Row = 0;
                                        foreach (var SubTask in LoaderList)
                                        {
                                            switch (SubTask.State)
                                            {
                                                case ModBase.LoadState.Waiting:
                                                {
                                                    if (Conversions.ToBoolean(Operators.ConditionalCompareObjectNotEqual(((FrameworkElement)Card.Children[Row * 2]).Tag, "Waiting", false)))
                                                    {
                                                        Card.Children.RemoveAt(Row * 2);
                                                        Card.Children.Insert(Row * 2, (UIElement)ModBase.GetObjectFromXML("<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:local=\"clr-namespace:PCL;assembly=Plain Craft Launcher 2\" Stretch=\"Uniform\" Tag=\"Waiting\" Data=\"F1 M5,0 a5,5 360 1 0 0,0.0001 m15,0 a5,5 360 1 0 0,0.0001 m15,0 a5,5 360 1 0 0,0.0001 Z\" Width=\"18\" HorizontalAlignment=\"Center\" Grid.Column=\"0\" Grid.Row=\"" + Row + "\" Fill=\"{DynamicResource ColorBrush3}\" Margin=\"0,7,0,0\" VerticalAlignment=\"Top\" Height=\"6\"/>"));
                                                    }

                                                    break;
                                                }

                                                case ModBase.LoadState.Loading:
                                                {
                                                    if (Conversions.ToBoolean(Operators.ConditionalCompareObjectNotEqual(((FrameworkElement)Card.Children[Row * 2]).Tag, "Loading", false)))
                                                    {
                                                        Card.Children.RemoveAt(Row * 2);
                                                        Card.Children.Insert(Row * 2, (UIElement)ModBase.GetObjectFromXML("<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:local=\"clr-namespace:PCL;assembly=Plain Craft Launcher 2\" Text=\"" + Math.Floor(SubTask.Progress * 100d) + "%\" Tag=\"Loading\" HorizontalAlignment=\"Center\" Grid.Column=\"0\" Grid.Row=\"" + Row + "\" Foreground=\"{DynamicResource ColorBrush3}\"/>"));
                                                    }
                                                    else
                                                    {
                                                        ((TextBlock)Card.Children[Row * 2]).Text = Math.Floor(SubTask.Progress * 100d) + "%";
                                                    }

                                                    break;
                                                }

                                                case ModBase.LoadState.Finished:
                                                {
                                                    if (Conversions.ToBoolean(Operators.ConditionalCompareObjectNotEqual(((FrameworkElement)Card.Children[Row * 2]).Tag, "Finished", false)))
                                                    {
                                                        Card.Children.RemoveAt(Row * 2);
                                                        Card.Children.Insert(Row * 2, (UIElement)ModBase.GetObjectFromXML("<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:local=\"clr-namespace:PCL;assembly=Plain Craft Launcher 2\" Stretch=\"Uniform\" Tag=\"Finished\" Data=\"F1 M 23.7501,33.25L 34.8334,44.3333L 52.2499,22.1668L 56.9999,26.9168L 34.8334,53.8333L 19.0001,38L 23.7501,33.25 Z\" Height=\"16\" Width=\"15\" HorizontalAlignment=\"Center\" Grid.Column=\"0\" Grid.Row=\"" + Row + "\" Fill=\"{DynamicResource ColorBrush3}\" Margin=\"0,3,0,0\" VerticalAlignment=\"Top\"/>"));
                                                    }

                                                    break;
                                                }
                                            }

                                            Row += 1;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.Error(ex, $"刷新任务管理卡片 {Loader.Name} 失败");
                                    }
                                }
                                while (false);
                                break;
                            }
#endregion
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, $"更新任务管理显示失败（{Loader.State}）");
                    }
                }
                else if (!(Loader.State == ModBase.LoadState.Canceled || Loader.State == ModBase.LoadState.Finished))
                {
                    try
                    {
#region 没有卡片且未中断或完成，添加新的卡片
                        string CardXAML = "\n                        <local:MyCard xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:local=\"clr-namespace:PCL;assembly=Plain Craft Launcher 2\"\n                            Tag=\"" + (Loader.Progress + (double)Loader.State) + "\" Title=\"" + StringUtils.XmlEscape(Loader.Name) + "\" Margin=\"0,0,0,15\">\n                            <Grid Margin=\"14,40,15,10\">\n                                <Grid.ColumnDefinitions>\n                                    <ColumnDefinition Width=\"50\"/>\n                                    <ColumnDefinition/>\n                                </Grid.ColumnDefinitions>\n                                <Grid.RowDefinitions>";
                        foreach (var SubTask in LoaderList)
                            CardXAML += "<RowDefinition Height=\"26\"/>";
                        CardXAML += "</Grid.RowDefinitions>";
                        int Row = 0;
                        foreach (var SubTask in LoaderList)
                        {
                            switch (SubTask.State)
                            {
                                case ModBase.LoadState.Waiting:
                                {
                                    CardXAML += "<Path Stretch=\"Uniform\" Tag=\"Waiting\" Data=\"F1 M5,0 a5,5 360 1 0 0,0.0001 m15,0 a5,5 360 1 0 0,0.0001 m15,0 a5,5 360 1 0 0,0.0001 Z\" Width=\"18\" HorizontalAlignment=\"Center\" Grid.Column=\"0\" Grid.Row=\"" + Row + "\" Fill=\"{DynamicResource ColorBrush3}\" Margin=\"0,7,0,0\" VerticalAlignment=\"Top\" Height=\"6\"/>";
                                    break;
                                }

                                case ModBase.LoadState.Loading:
                                {
                                    CardXAML += "<TextBlock Text=\"" + Math.Floor(SubTask.Progress * 100d) + "%\" Tag=\"Loading\" HorizontalAlignment=\"Center\" Grid.Column=\"0\" Grid.Row=\"" + Row + "\" Foreground=\"{DynamicResource ColorBrush3}\" />";
                                    break;
                                }

                                case ModBase.LoadState.Finished:
                                {
                                    CardXAML += "<Path Stretch=\"Uniform\" Tag=\"Finished\" Data=\"F1 M 23.7501,33.25L 34.8334,44.3333L 52.2499,22.1668L 56.9999,26.9168L 34.8334,53.8333L 19.0001,38L 23.7501,33.25 Z\" Height=\"16\" Width=\"15\" HorizontalAlignment=\"Center\" Grid.Column=\"0\" Grid.Row=\"" + Row + "\" Fill=\"{DynamicResource ColorBrush3}\" Margin=\"0,3,0,0\" VerticalAlignment=\"Top\"/>";
                                    break;
                                }

                                default:
                                {
                                    CardXAML += "<Path Stretch=\"Uniform\" Tag=\"Failed\" Data=\"F1 M2.5,0 L0,2.5 7.5,10 0,17.5 2.5,20 10,12.5 17.5,20 20,17.5 12.5,10 20,2.5 17.5,0 10,7.5 2.5,0Z\" Height=\"15\" Width=\"15\" HorizontalAlignment=\"Center\" Grid.Column=\"0\" Grid.Row=\"" + Row + "\" Fill=\"{DynamicResource ColorBrush3}\" Margin=\"0,1,0,0\" VerticalAlignment=\"Top\"/>";
                                    break;
                                }
                            }

                            CardXAML += "<TextBlock Text=\"" + StringUtils.XmlEscape(SubTask.Name) + "\" HorizontalAlignment=\"Left\" Grid.Column=\"1\" Grid.Row=\"" + Row + "\"/>";
                            Row += 1;
                        }

                        CardXAML += "</Grid></local:MyCard>";
                        // 实例化控件
                        MyCard Card;
                        try
                        {
                            Card = (MyCard)ModBase.GetObjectFromXML(CardXAML);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "新建任务管理卡片失败");
                            Logger.Info($"出错的卡片内容：{Constants.vbCrLf}{CardXAML}");
                            throw;
                        }

                        ModMain.FrmSpeedRight.PanMain.Children.Insert(0, Card);
                        RightCards.Add(Loader.Name, Card);
                        Logger.Info($"新建任务管理卡片：{Loader.Name}");
                        // 添加取消按钮
                        var Cancel = new MyIconButton()
                        {
                            Name = "BtnCancel",
                            Logo = "F1 M2,0 L0,2 8,10 0,18 2,20 10,12 18,20 20,18 12,10 20,2 18,0 10,8 2,0Z",
                            Height = 20d,
                            Margin = new Thickness(0d, 10d, 10d, 0d),
                            LogoScale = 1.1d,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            VerticalAlignment = VerticalAlignment.Top
                        };
                        Card.Children.Add(Cancel);
                        Cancel.Click += (sender, e) =>
                        {
                            ModAnimation.AniDispose((MyIconButton)sender, false);
                            ModAnimation.AniDispose(Card, true, _ =>
                            {
                                if (ModMain.FrmSpeedRight.PanMain.Children.Count == 0 && ModMain.FrmMain.PageCurrent == (FormMain.PageStackData)FormMain.PageType.TaskManager)
                                    ModMain.FrmMain.PageBack();
                            });
                            RightCards.Remove(Loader.Name);
                            ModLoader.LoaderTaskbar.Remove(Loader);
                            Logger.Info($"关闭任务管理卡片：{Loader.Name}，且移出任务列表");
                            ModBase.RunInThread(() => Loader.Cancel());
                        };
                        if (PackStages is not null && Card.Children.Count > 3 && Card.Children[3] is Grid PackStageRows)
                            RefreshPackStages(PackStageRows, PackStages, Loader.State == ModBase.LoadState.Failed ? Loader.Error : null);
                        // 如果已经失败，再刷新一次，修改成失败的控件
                        if (Loader.State == ModBase.LoadState.Failed)
                        {
                            Card.Tag = null; // 避免重复导致刷新无效
                            TaskRefresh(Loader);
                        }
                    }
#endregion
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "添加任务管理卡片失败");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "刷新任务管理显示失败");
            }
        }

        public void TaskRemove(object Loader)
        {
            if (TryRemoveMmcImport(Loader)) return;
            if (RightCards.ContainsKey(Conversions.ToString(Conversions.ToString(NewLateBinding.LateGet(Loader, null, "Name", Array.Empty<object>(), null, null, null)))))
            {
                ModBase.RunInUiWait(() =>
                {
                    // 移除已有的卡片
                    Grid Card = RightCards[Conversions.ToString(Conversions.ToString(NewLateBinding.LateGet(Loader, null, "Name", Array.Empty<object>(), null, null, null)))];
                    ModMain.FrmSpeedRight.PanMain.Children.Remove(Card);
                    RightCards.Remove(Conversions.ToString(Conversions.ToString(NewLateBinding.LateGet(Loader, null, "Name", Array.Empty<object>(), null, null, null))));
                    Logger.Info($"移除任务管理卡片：{Conversions.ToString(NewLateBinding.LateGet(Loader, null, "Name", Array.Empty<object>(), null, null, null))}");
                });
            }
        }

        /// <summary>
        /// 若没有任务，尝试返回主页。
        /// </summary>
        private void TryReturnToHome()
        {
            if (ModMain.FrmSpeedRight.PanMain.Children.Count == 0 && ModMain.FrmMain.PageCurrent == (FormMain.PageStackData)FormMain.PageType.TaskManager)
            {
                ModMain.FrmMain.PageBack();
            }
        }
    }
}
