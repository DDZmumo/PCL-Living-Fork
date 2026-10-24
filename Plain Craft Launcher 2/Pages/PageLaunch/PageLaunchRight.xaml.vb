Imports System.Windows.Threading

Public Class PageLaunchRight
    Implements IRefreshable, IDispatcherUnhandledException
    Private RecentWorldRevision As Integer
    Private LatestRecentWorld As Tuple(Of RecentWorld, ImageSource)
    Private RecentWorldVisible As Boolean
    Private RecentSessionRunning As Boolean
    Private RecentLaunchPending As Boolean

    Private Sub InitRecentWorldCard() Handles Me.Initialized
        DisabledPageAnimControls.Add(CardRecentWorlds)
        Dim Title = CardRecentWorlds.MainTextBlock
        Title.FontSize = 12
        Title.Margin = New Thickness(12, 7, 12, 0)
    End Sub
    Private Sub RecentPageEntered() Handles Me.PageEnter
        RunInUi(AddressOf UpdateRecentWorldVisibility, True)
    End Sub

    Private Sub Init() Handles Me.Loaded
        PanBack.ScrollToHome()
        PanLog.Visibility = ModeDebug.ToVisibility
        RefreshRecentWorlds()
    End Sub
    Friend Sub RefreshRecentWorlds()
        Dim Revision = Interlocked.Increment(RecentWorldRevision)
        Dim SelectedInstance = McInstanceSelected
        RunInNewThread(Sub()
                           Dim Item As Tuple(Of RecentWorld, ImageSource) = Nothing
                           Dim SessionRunning As Boolean = False
                           Try
                               QuickPlayWorlds.RecoverExitedSessions()
                               QuickPlayWorlds.DiscoverExistingWorlds(SelectedInstance)
                               SessionRunning = QuickPlayWorlds.HasRunningSessions()
                               Dim World = QuickPlayWorlds.Recent().FirstOrDefault()
                               If World IsNot Nothing Then Item = Tuple.Create(World, DirectCast(InstanceResourceStore.LoadImage(IO.Path.Combine(QuickPlayWorlds.WorldPath(World.GameFolder, World.Identifier), "icon.png"), 64), ImageSource))
                           Catch ex As Exception
                               Logger.Warn(ex, "读取最近存档失败")
                           End Try
                           RunInUi(Sub()
                                       If Revision <> RecentWorldRevision Then Return
                                       RecentSessionRunning = SessionRunning
                                       ShowRecentWorld(Item)
                                   End Sub)
                       End Sub, "Load Recent Worlds")
    End Sub
    Friend Sub ShowRecentWorld(Item As Tuple(Of RecentWorld, ImageSource))
        LatestRecentWorld = Item
        If Item Is Nothing Then
            ApplyRecentWorldState(True, IsLoaded AndAlso AniControlEnabled = 0)
            Return
        End If
        Dim World = Item.Item1
        ItemRecentWorld.Tag = World
        ItemRecentWorld.Title = World.Name
        Dim Played = World.PlayedUtc.ToLocalTime()
        ItemRecentWorld.Info = If(Played.Date = Date.Today, Played.ToString("HH:mm"), Played.ToString("MM/dd")) & " · " & New IO.DirectoryInfo(World.VersionPath).Name
        Dim Tip = TryCast(ItemRecentWorld.ToolTip, ToolTip)
        If Tip Is Nothing Then
            Tip = New ToolTip
            ItemRecentWorld.ToolTip = Tip
        End If
        Tip.Content = World.Name & vbCrLf & New IO.DirectoryInfo(World.VersionPath).Name & " · " & Played.ToString("yyyy-MM-dd HH:mm")
        Dim Icon = ItemRecentWorld.Children.OfType(Of Canvas)().FirstOrDefault(Function(Canvas) TypeOf Canvas.Background Is ImageBrush)
        If Icon IsNot Nothing Then
            Dim Picture = DirectCast(Icon.Background, ImageBrush).CloneCurrentValue()
            Picture.Stretch = Stretch.Uniform
            Picture.ImageSource = If(Item.Item2, CType(New MyBitmap(PathImage & "Blocks/Grass.png"), ImageSource))
            Icon.Background = Picture
            Icon.Width = 32
            Icon.Height = 32
            Icon.HorizontalAlignment = HorizontalAlignment.Center
            Icon.VerticalAlignment = VerticalAlignment.Center
            Icon.Clip = New RectangleGeometry(New Rect(0, 0, 32, 32), 6, 6)
        End If
        UpdateRecentWorldVisibility()
    End Sub
    Friend Sub UpdateRecentWorldVisibility()
        If Not IsLoaded Then Return
        ApplyRecentWorldState(RecentLaunchPending OrElse HasRunningMinecraft OrElse RecentSessionRunning OrElse McLaunchLoader.State = LoadState.Loading, AniControlEnabled = 0)
    End Sub
    Friend Sub ApplyRecentWorldState(GameActive As Boolean, Animate As Boolean)
        Dim Visible = LatestRecentWorld IsNot Nothing AndAlso Not GameActive AndAlso Not RecentLaunchPending AndAlso Settings.Get(Of Boolean)("LaunchRecentWorlds")
        Dim AnimationName = "Recent World Card " & PageUuid
        If RecentWorldVisible = Visible AndAlso (AniIsRun(AnimationName) OrElse CardRecentWorlds.Visibility = If(Visible, Visibility.Visible, Visibility.Collapsed)) Then Return
        RecentWorldVisible = Visible
        AniStop(AnimationName)
        ToolTipService.SetIsEnabled(ItemRecentWorld, False)
        Dim Tip = TryCast(ItemRecentWorld.ToolTip, ToolTip)
        If Tip IsNot Nothing Then Tip.IsOpen = False
        CardRecentWorlds.IsHitTestVisible = Visible
        If Not Animate Then
            CardRecentWorlds.Visibility = If(Visible, Visibility.Visible, Visibility.Collapsed)
            CardRecentWorlds.Opacity = If(Visible, 1, 0)
            TransRecentWorld.X = If(Visible, 0, -24)
            ToolTipService.SetIsEnabled(ItemRecentWorld, Visible)
        ElseIf Visible Then
            If CardRecentWorlds.Visibility <> Visibility.Visible Then
                CardRecentWorlds.Opacity = 0
                TransRecentWorld.X = -24
            End If
            CardRecentWorlds.Visibility = Visibility.Visible
            AniStart({
                AaOpacity(CardRecentWorlds, 1 - CardRecentWorlds.Opacity, 180),
                AaTranslateX(CardRecentWorlds, -TransRecentWorld.X, 280, Ease:=New AniEaseOutBack(AniEasePower.Weak)),
                AaCode(Sub() ToolTipService.SetIsEnabled(ItemRecentWorld, RecentWorldVisible AndAlso Not RecentLaunchPending), After:=True)
            }, AnimationName)
        Else
            AniStart({
                AaOpacity(CardRecentWorlds, -CardRecentWorlds.Opacity, 100),
                AaTranslateX(CardRecentWorlds, -24 - TransRecentWorld.X, 150, Ease:=New AniEaseInFluent(AniEasePower.Weak)),
                AaCode(Sub() CardRecentWorlds.Visibility = Visibility.Collapsed, After:=True)
            }, AnimationName)
        End If
    End Sub
    Private Sub RecentWorldClick(Sender As Object, e As MouseButtonEventArgs) Handles ItemRecentWorld.Click
        e.Handled = True
        Dim World = TryCast(ItemRecentWorld.Tag, RecentWorld)
        If World Is Nothing Then Return
        BeginRecentWorldLaunch(Function()
                                   Dim Instance As New McInstance(World.VersionPath)
                                   If Not PathUtils.IsSame(Instance.PathIndie, World.GameFolder) Then
                                       Hint("此版本的游戏目录已经变化，请从存档管理页重新选择存档。", HintType.Red)
                                       Return False
                                   End If
                                   Return QuickPlayWorlds.Start(Instance, World.Identifier)
                               End Function, AniControlEnabled = 0)
    End Sub
    Friend Sub BeginRecentWorldLaunch(Start As Func(Of Boolean), Optional Animate As Boolean = True)
        If RecentLaunchPending OrElse HasRunningMinecraft OrElse McLaunchLoader.State = LoadState.Loading Then Return
        RecentLaunchPending = True
        ApplyRecentWorldState(True, Animate)
        Dim ContinueLaunch As Action = Sub()
                                           Dim Accepted As Boolean
                                           Try
                                               Accepted = Start()
                                           Catch ex As Exception
                                               Logger.Error(ex, "启动最近游玩的存档失败", LogBehavior.Toast)
                                           Finally
                                               RecentLaunchPending = False
                                               If Not Accepted Then ApplyRecentWorldState(False, Animate)
                                           End Try
                                       End Sub
        If Animate Then
            AniStart({AaCode(Sub() ContinueLaunch(), 180)}, "Recent World Launch " & PageUuid)
        Else
            ContinueLaunch()
        End If
    End Sub
    Private Sub ReleaseRecentWorlds() Handles Me.Unloaded
        Interlocked.Increment(RecentWorldRevision)
        AniStop("Recent World Card " & PageUuid)
        AniStop("Recent World Launch " & PageUuid)
        RecentLaunchPending = False
        ToolTipService.SetIsEnabled(ItemRecentWorld, False)
        Dim Tip = TryCast(ItemRecentWorld.ToolTip, ToolTip)
        If Tip IsNot Nothing Then Tip.IsOpen = False
        RecentWorldVisible = False
        CardRecentWorlds.Visibility = Visibility.Collapsed
        CardRecentWorlds.Opacity = 0
        TransRecentWorld.X = -24
    End Sub

#Region "主页"

    ''' <summary>
    ''' 刷新主页。
    ''' </summary>
    Private Sub Refresh() Handles Me.Loaded
        RunInThread(
        Sub()
            Try
                SyncLock RefreshLock
                    RefreshReal()
                End SyncLock
            Catch ex As Exception
                Logger.Error(ex, "加载 PCL 主页自定义信息失败", If(ModeDebug, LogBehavior.Alert, LogBehavior.Toast))
            End Try
        End Sub)
    End Sub
    Private Sub RefreshReal()
        Dim Content As String = Nothing, Url As String = Nothing
        Select Case Settings.Get(Of Integer)("UiCustomType")
            Case 1
                '加载本地文件
                Logger.Info("主页自定义数据来源：本地文件")
                Content = FileUtils.TryReadAsString(Paths.Base & "PCL\Custom.xaml")
            Case 2
                '联网下载
                Url = Settings.Get(Of String)("UiCustomNet")
            Case 3
                '预设
                Select Case Settings.Get(Of Integer)("UiCustomPreset")
                    Case 0
                        Logger.Info("主页预设：你知道吗")
                        Content = "
                            <local:MyCard Title=""你知道吗？"" Margin=""0,0,0,15"">
                                <StackPanel Margin=""25,38,23,15"">
                                    <TextBlock FontSize=""13.5"" IsHitTestVisible=""False"" Text=""{hint}"" TextWrapping=""Wrap"" Foreground=""{DynamicResource ColorBrush1}"" />
                                    <WrapPanel Margin=""0,14,0,0"">
                                        <local:MyButton Text=""获取官方 PCL"" Height=""35"" Padding=""16,0"" Margin=""0,0,12,5"" EventType=""打开网页"" EventData=""https://meloong.com/PCL"" />
                                        <local:MyButton Text=""支持原作者"" Height=""35"" Padding=""16,0"" Margin=""0,0,12,5"" EventType=""打开网页"" EventData=""https://afdian.com/a/LTCat"" />
                                        <local:MyButton Text=""购买正版 Minecraft"" Height=""35"" Padding=""16,0"" Margin=""0,0,0,5"" EventType=""打开网页"" EventData=""https://www.minecraft.net/zh-hans/store/minecraft-java-bedrock-edition-pc"" />
                                    </WrapPanel>
                                </StackPanel>
                                <local:MyIconButton Height=""22"" Width=""22"" Margin=""9"" VerticalAlignment=""Top"" HorizontalAlignment=""Right"" 
                                    EventType=""刷新主页"" EventData=""/""
                                    Logo=""M512.0 838.3c-80.2 0-153.4-29.3-210.2-77.4l75.5-75.5c11.5-11.5 25.8-22.0 25.8-37.0a27.2 27.2 0 0 0-27.1-27.1H104.0c-27.1 0-27.1 23.9-27.1 27.1v271.9a27.1 27.1 0 0 0 27.1 27.1c15.0 0 27.8-16.6 42.5-31.2l77.9-77.9c76.6 67.7 177.1 108.9 287.4 108.9 221.7 0 404.5-166.0 431.2-380.6h-109.8c-25.9 154.2-159.7 271.9-321.3 271.9zM919.9 76.6c-15.0 0-27.8 16.6-42.5 31.3L799.5 185.8c-76.5-67.7-177.1-108.9-287.4-108.9-221.8 0-404.5 166.1-431.3 380.6H190.6c25.9-154.2 159.7-271.9 321.4-271.9 80.2 0 153.4 29.3 210.1 77.4l-75.5 75.5c-11.6 11.5-25.8 22.0-25.8 37.1a27.2 27.2 0 0 0 27.1 27.1h271.9c27.1 0 27.1-23.9 27.1-27.1V103.8a27.1 27.1 0 0 0-27.1-27.1z"" />
                            </local:MyCard>"
                    Case 1
                        Logger.Info("主页预设：回声洞")
                        Content = "
                            <local:MyCard Title=""回声洞"" Margin=""0,0,0,15"">
                                <TextBlock Margin=""25,38,23,15"" FontSize=""13.5"" IsHitTestVisible=""False"" Text=""{cave}"" TextWrapping=""Wrap"" Foreground=""{DynamicResource ColorBrush1}"" />
                                <local:MyIconButton Height=""22"" Width=""22"" Margin=""9"" VerticalAlignment=""Top"" HorizontalAlignment=""Right"" 
                                    EventType=""刷新主页"" EventData=""/""
                                    Logo=""M512.0 838.3c-80.2 0-153.4-29.3-210.2-77.4l75.5-75.5c11.5-11.5 25.8-22.0 25.8-37.0a27.2 27.2 0 0 0-27.1-27.1H104.0c-27.1 0-27.1 23.9-27.1 27.1v271.9a27.1 27.1 0 0 0 27.1 27.1c15.0 0 27.8-16.6 42.5-31.2l77.9-77.9c76.6 67.7 177.1 108.9 287.4 108.9 221.7 0 404.5-166.0 431.2-380.6h-109.8c-25.9 154.2-159.7 271.9-321.3 271.9zM919.9 76.6c-15.0 0-27.8 16.6-42.5 31.3L799.5 185.8c-76.5-67.7-177.1-108.9-287.4-108.9-221.8 0-404.5 166.1-431.3 380.6H190.6c25.9-154.2 159.7-271.9 321.4-271.9 80.2 0 153.4 29.3 210.1 77.4l-75.5 75.5c-11.6 11.5-25.8 22.0-25.8 37.1a27.2 27.2 0 0 0 27.1 27.1h271.9c27.1 0 27.1-23.9 27.1-27.1V103.8a27.1 27.1 0 0 0-27.1-27.1z"" />
                            </local:MyCard>"
                    Case 2
                        Logger.Info("主页预设：Minecraft 新闻")
                        Url = "https://mcnews.meloong.com"
                    Case 3
                        Logger.Info("主页预设：简单主页")
                        Url = "https://pclhomeplazaoss.lingyunawa.top:26994/d/Homepages/MFn233/Custom.xaml"
                    Case 4
                        Logger.Info("主页预设：每日整合包推荐")
                        Url = "https://pclsub.sodamc.com/"
                    Case 5
                        Logger.Info("主页预设：Minecraft 皮肤推荐")
                        Url = "https://forgepixel.com/pcl_sub_file"
                    Case 6
                        Logger.Info("主页预设：OpenBMCLAPI 仪表盘 Lite")
                        Url = "https://pcl-bmcl.milu.ink/"
                    Case 7
                        Logger.Info("主页预设：主页市场")
                        Url = "https://pclhomeplazaoss.lingyunawa.top:26994/p/Homepages/Homepage.Market/Custom.xaml"
                    Case 8
                        Logger.Info("主页预设：更新日志")
                        Url = "https://pclhomeplazaoss.lingyunawa.top:26994/d/Homepages/Joker2184/UpdateHomepage.xaml"
                    Case 9
                        Logger.Info("主页预设：PCL 新功能说明书")
                        Url = "https://raw.gitcode.com/WForst-Breeze/WhatsNewPCL/raw/main/Custom.xaml"
                    Case 10
                        Logger.Info("主页预设：OpenMCIM Dashboard")
                        Url = "https://files.mcimirror.top/PCL"
                    Case 11
                        Logger.Info("主页预设：杂志主页")
                        Url = "https://gh-proxy.com/https://github.com/Neclyon/Magazine-Homepage-PCL/raw/main/output/Custom.xaml"
                    Case 12
                        Logger.Info("主页预设：PCL GitHub 仪表盘")
                        Url = "https://ddf.pcl-community.top/Custom.xaml"
                    Case 13
                        Logger.Info("主页预设：PCL 更新摘要")
                        Url = "https://raw.gitcode.com/ENC_Euphony/PCL-AI-Summary-HomePage/raw/master/Custom.xaml"
                    Case 14
                        Logger.Info("主页预设：今日新闻热点")
                        Url = "https://pcl.wyc-w.top/index.xaml"
                    Case 15
                        Logger.Info("主页预设：Minecraft 芝士站")
                        Url = "https://mkss-services.awaeric.xyz"
                    Case 16
                        Logger.Info("主页预设：整合包推荐引擎")
                        Url = "https://qawsedrftgyhujiko.fun/pcl2/Custom.xaml"
                    Case 17
                        Logger.Info("主页预设：Bangumi 番剧主页")
                        Url = "https://bangumi.p.kaphia.top"
                    Case 18
                        Logger.Info("主页预设：Bilibili 热门")
                        Url = "https://bilibili.p.kaphia.top"
                    Case 19
                        Logger.Info("主页预设：Music 云音乐热门")
                        Url = "https://cloudmusic.p.kaphia.top"
                    Case 20
                        Logger.Info("主页预设：模组推荐")
                        Url = "https://g-fish.dpdns.org/download/ModPage.xaml"
                    Case 21
                        Logger.Info("主页预设：每日游戏推荐")
                        Url = "https://pcl.xn--9iq209asnop94b.top/download/GamePage.xaml"
                    Case 22
                        Logger.Info("主页预设：Wallpaper 每日壁纸")
                        Url = "https://wallpaper.p.kaphia.top"
                    Case 23
                        Logger.Info("主页预设：Weather 天气预报")
                        Url = "https://weather-p.kaphia.top/{variable:PCLDailyWeatherPage:choose}.xaml"
                    Case 24
                        Logger.Info("主页预设：新一代狗屁王大杂烩主页")
                        Url = "https://end.g-fish.dpdns.org/Custom.xaml"
                End Select
        End Select
        '联网下载
        If Not String.IsNullOrWhiteSpace(Url) Then
            '替换自定义变量与设置
            Url = ArgumentReplace(Url, AddressOf WebUtility.HtmlEncode)
            If Url = Settings.Get(Of String)("CacheSavedPageUrl") AndAlso FileUtils.Exists(PathTemp & "Cache\Custom.xaml") Then
                '缓存可用
                Logger.Info("主页自定义数据来源：联网缓存文件")
                Content = FileUtils.TryReadAsString(PathTemp & "Cache\Custom.xaml")
                '后台更新缓存
                OnlineLoader.Start((Url, False))
            Else
                '缓存不可用
                Logger.Info("主页自定义数据来源：联网全新下载")
                Hint("正在加载主页……")
                RunInUiWait(Sub() LoadContent(Nothing)) '在加载结束前清空页面
                Settings.Set("CacheSavedPageVersion", "")
                OnlineLoader.Start((Url, True)) '下载完成后将会再次触发更新
                Return
            End If
        End If
        '同步到 UI
        RunInUi(Sub() LoadContent(Content))
    End Sub
    Private RefreshLock As New Object

    '联网获取主页文件
    Private OnlineLoader As New LoaderTask(Of (Address As String, ShouldRefresh As Boolean), Integer)("下载主页", AddressOf OnlineLoaderSub) With {.ReloadTimeout = 10 * 60 * 1000}
    Private Sub OnlineLoaderSub(Task As LoaderTask(Of (Address As String, ShouldRefresh As Boolean), Integer))
        Dim Address As String = Task.Input.Address '#3721 中连续触发两次导致内容变化
        Dim ShouldRefresh As Boolean = Task.Input.ShouldRefresh
        Try
            '获取版本校验地址
            Dim VersionAddress As String
            If Address.Contains(".xaml") Then
                VersionAddress = Address.Replace(".xaml", ".xaml.ini")
            Else
                VersionAddress = Address.BeforeFirst("?")
                If Not VersionAddress.EndsWithF("/") Then VersionAddress += "/"
                VersionAddress += "version"
                If Address.Contains("?") Then VersionAddress += "?" & Address.AfterFirst("?")
            End If
            '校验版本
            Dim Version As String = ""
            Try
                Version = NetRequestByClientRetry(VersionAddress)
                If Version.Length > 1000 Then Throw New Exception($"获取的主页版本过长（{Version.Length} 字符）")
                Dim CurrentVersion As String = Settings.Get(Of String)("CacheSavedPageVersion")
                If Version <> "" AndAlso CurrentVersion <> "" AndAlso Version = CurrentVersion Then
                    Logger.Info($"当前缓存的主页已为最新，当前版本：{Version}，检查源：{VersionAddress}")
                    Return
                End If
                Logger.Info($"需要下载联网主页，当前版本：{Version}，检查源：{VersionAddress}")
            Catch exx As Exception
                Logger.Warn(exx, $"联网获取主页版本失败")
                Logger.Info($"无法检查联网主页版本，将直接下载，检查源：{VersionAddress}")
            End Try
            '实际下载
            Dim FileContent As String = NetRequestByClientRetry(Address)
            Logger.Info($"已联网下载主页，内容长度：{FileContent.Length}，来源：{Address}")
            Settings.Set("CacheSavedPageUrl", Address)
            Settings.Set("CacheSavedPageVersion", Version)
            FileUtils.Write(PathTemp & "Cache\Custom.xaml", FileContent)
            '若内容变更则要求刷新
            If LoadedContentHash <> FileContent.GetStableHashCode() AndAlso ShouldRefresh Then Refresh()
        Catch ex As Exception
            Logger.Error(ex, $"下载主页失败（{Address}）", If(ModeDebug, LogBehavior.Alert, LogBehavior.Toast))
        End Try
    End Sub

    ''' <summary>
    ''' 立即强制刷新主页。
    ''' 必须在 UI 线程调用。
    ''' </summary>
    Public Sub ForceRefresh() Implements IRefreshable.Refresh
        Logger.Info("要求强制刷新主页")
        ClearCache()
        '实际的刷新
        If FrmMain.PageCurrent.Page = FormMain.PageType.Launch Then
            PanBack.ScrollToHome()
            Refresh()
        Else
            FrmMain.PageChange(FormMain.PageType.Launch)
        End If
    End Sub

    ''' <summary>
    ''' 清空主页缓存信息。
    ''' </summary>
    Private Sub ClearCache()
        LoadedContentHash = Nothing
        OnlineLoader.Input = ("", True)
        Settings.Set("CacheSavedPageUrl", "")
        Settings.Set("CacheSavedPageVersion", "")
        Logger.Info("已清空主页缓存")
    End Sub

    ''' <summary>
    ''' 从文本内容中加载主页。
    ''' 必须在 UI 线程调用。
    ''' </summary>
    Private Sub LoadContent(Content As String)
        Try
            SyncLock LoadContentLock
                '如果加载目标内容一致则不加载
                Dim Hash = If(Content, "").GetStableHashCode()
                If Hash = LoadedContentHash Then Return
                LoadedContentHash = Hash
                '实际加载内容
                PanCustom.Children.Clear()
                If String.IsNullOrWhiteSpace(Content) Then
                    Logger.Info($"实例化：清空主页 UI，来源为空")
                    Return
                End If
                Dim LoadStartTime As Date = Date.Now
                '修改时应同时修改 PageOtherHelpDetail.Init
                Content = ArgumentReplace(Content, AddressOf StringUtils.XmlEscape)
                Do While Content.Contains("xmlns")
                    Content = Content.RegexReplace("xmlns[^""']*(""|')[^""']*(""|')", "").Replace("xmlns", "") '禁止声明命名空间
                Loop
                Content = "<StackPanel xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:sys=""clr-namespace:System;assembly=mscorlib"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"" xmlns:local=""clr-namespace:PCL;assembly=Plain Craft Launcher 2"" xmlns:core=""clr-namespace:MeloongCore;assembly=MeloongCore"" xmlns:corewpf=""clr-namespace:MeloongCore.Wpf;assembly=MeloongCore.Wpf"">" & Content & "</StackPanel>"
                Logger.Info($"实例化：加载主页 UI 开始，最终内容长度：{Content.Count}")
                PanCustom.Children.Add(GetObjectFromXML(Content))
                '加载计时
                Dim LoadCostTime = (Date.Now - LoadStartTime).Milliseconds
                Logger.Info($"实例化：加载主页 UI 完成，耗时 {LoadCostTime}ms")
                If LoadCostTime > 3000 Then Hint($"主页加载过于缓慢（花费了 {Math.Round(LoadCostTime / 1000, 1)} 秒），请向主页作者反馈此问题，或暂时停止使用该主页")
            End SyncLock
        Catch ex As Exception
            Logger.Warn(ex, $"加载失败的主页内容：{vbCrLf}{Content}")
            OnLoadContentFailed(ex)
        End Try
    End Sub
    ''' <summary>
    ''' 加载主页失败时调用。
    ''' </summary>
    Private Sub OnLoadContentFailed(ex As Exception)
        If ModeDebug OrElse Settings.Get(Of Integer)("UiCustomType") = 1 Then
            Logger.Warn(ex, "加载主页失败")
            If MyMsgBox(If(TypeOf ex Is UnauthorizedAccessException, ex.Message, $"主页内容编写有误，请根据下列错误信息进行检查：{vbCrLf}{ex.GetDisplay(False)}"),
                        "加载主页失败", "重试", "取消") = 1 Then ForceRefresh()
        Else
            Logger.Error(ex, "加载主页失败", LogBehavior.Toast)
        End If
    End Sub
    ''' <summary>
    ''' 捕获主页在 Measure 和 Arrange 阶段抛出的异常。
    ''' </summary>
    Private Sub DispatcherUnhandledException(sender As Object, e As DispatcherUnhandledExceptionEventArgs) Implements IDispatcherUnhandledException.DispatcherUnhandledException
        If TypeOf e.Exception IsNot Markup.XamlParseException Then Return
        e.Handled = True
        LoadContent(Nothing)
        OnLoadContentFailed(e.Exception)
    End Sub

    Private LoadedContentHash As ULong? = Nothing
    Private LoadContentLock As New Object

#End Region

End Class
