Public Class PageInstanceLeft
    Implements IRefreshable

    ''' <summary>
    ''' 当前显示设置的 MC 版本。
    ''' </summary>
    Public Shared Instance As McInstance = Nothing

    Private Sub PageInstanceLeft_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PageSetupUI.HiddenRefresh()
    End Sub

    Private Sub ResourceButtonsInit() Handles Me.Initialized
        ItemRestorePoints.Logo = Logo.IconButtonRefresh
        ItemRestorePoints.LogoScale = 0.85
        ItemKeybindings.Logo = ItemSetup.Logo
        ItemKeybindings.LogoScale = ItemSetup.LogoScale
        For Each Item In {ItemSaves, ItemScreenshots, ItemResourcePacks, ItemShaderPacks, ItemSchematics, ItemKeybindings, ItemRestorePoints}
            Dim Button As New MyIconButton With {.Tag = Item.Tag, .ToolTip = "刷新", .LogoScale = 0.85, .Logo = DirectCast(ItemModify.Buttons.First(), MyIconButton).Logo}
            AddHandler Button.Click, AddressOf Refresh_Click
            Item.Buttons = {Button}
        Next
    End Sub

    Public Sub RefreshModDisabled() Handles Me.Loaded
        If Instance IsNot Nothing AndAlso Instance.Modable Then
            ItemMod.Visibility = Visibility.Visible
            ItemModDisabled.Visibility = Visibility.Collapsed
        Else
            ItemMod.Visibility = Visibility.Collapsed
            ItemModDisabled.Visibility = Visibility.Visible
        End If
    End Sub

#Region "页面切换"

    ''' <summary>
    ''' 当前页面的编号。从 0 开始计算。
    ''' </summary>
    Public PageID As FormMain.PageSubType = FormMain.PageSubType.Default

    ''' <summary>
    ''' 勾选事件改变页面。
    ''' </summary>
    Private Sub PageCheck(sender As FrameworkElement, e As RouteEventArgs) Handles ItemOverall.Check, ItemMod.Check, ItemModDisabled.Check, ItemSetup.Check, ItemModify.Check, ItemExport.Check, ItemSaves.Check, ItemScreenshots.Check, ItemResourcePacks.Check, ItemShaderPacks.Check, ItemSchematics.Check, ItemKeybindings.Check, ItemRestorePoints.Check
        '尚未初始化控件属性时，sender.Tag 为 Nothing，会导致切换到页面 0
        '若使用 IsLoaded，则会导致模拟点击不被执行（模拟点击切换页面时，控件的 IsLoaded 为 False）
        If sender.Tag IsNot Nothing Then PageChange(Val(sender.Tag))
    End Sub

    Friend Function NavigationItem(ID As FormMain.PageSubType) As MyListItem
        Return PanItem.Children.OfType(Of MyListItem)().First(Function(Item) CInt(Item.Tag) = CInt(ID))
    End Function

    Private ReadOnly ResourcePages As New Dictionary(Of FormMain.PageSubType, PageInstanceResources)
    Private KeybindingsPage As PageInstanceKeybindings
    Private RestorePointsPage As PageInstanceRestorePoints

    Public Function PageGet(Optional ID As FormMain.PageSubType = -1)
        If ID = -1 Then ID = PageID
        ID = ModModpackMode.NormalizeInstancePage(ID)
        Select Case ID
            Case FormMain.PageSubType.InstanceOverall
                If FrmInstanceOverall Is Nothing Then FrmInstanceOverall = New PageInstanceOverall
                Return FrmInstanceOverall
            Case FormMain.PageSubType.InstanceMod
                If FrmInstanceMod Is Nothing Then FrmInstanceMod = New PageInstanceMod
                Return FrmInstanceMod
            Case FormMain.PageSubType.InstanceModDisabled
                If FrmInstanceModDisabled Is Nothing Then FrmInstanceModDisabled = New PageInstanceModDisabled
                Return FrmInstanceModDisabled
            Case FormMain.PageSubType.InstanceSetup
                If FrmInstanceSetup Is Nothing Then FrmInstanceSetup = New PageInstanceSetup
                Return FrmInstanceSetup
            Case FormMain.PageSubType.InstanceExport
                If FrmInstanceExport Is Nothing Then FrmInstanceExport = New PageInstanceExport
                Return FrmInstanceExport
            Case FormMain.PageSubType.InstanceModify
                If FrmDownloadModify Is Nothing Then FrmDownloadModify = New PageDownloadModify
                Return FrmDownloadModify
            Case FormMain.PageSubType.InstanceRestorePoints
                If RestorePointsPage Is Nothing Then RestorePointsPage = New PageInstanceRestorePoints
                Return RestorePointsPage
            Case FormMain.PageSubType.InstanceKeybindings
                If KeybindingsPage Is Nothing Then KeybindingsPage = New PageInstanceKeybindings
                Return KeybindingsPage
            Case FormMain.PageSubType.InstanceSaves To FormMain.PageSubType.InstanceSchematics
                If Not ResourcePages.ContainsKey(ID) Then ResourcePages(ID) = New PageInstanceResources(CType(CInt(ID) - CInt(FormMain.PageSubType.InstanceSaves), InstanceResourceKind))
                Return ResourcePages(ID)
            Case Else
                Throw New Exception("未知的版本设置子页面种类：" & ID)
        End Select
    End Function

    ''' <summary>
    ''' 切换现有页面。
    ''' </summary>
    Public Sub PageChange(ID As FormMain.PageSubType)
        ID = ModModpackMode.NormalizeInstancePage(ID)
        If PageID = ID Then Return
        If Instance IsNot Nothing AndAlso McInstanceLock.IsLocked(Instance) Then
            Hint("该版本正在被修改，请稍后再切换页面！", HintType.Red)
            Return
        End If
        AniControlEnabled += 1
        Try
            PageChangeRun(PageGet(ID))
            PageID = ID
        Catch ex As Exception
            Logger.Error(ex, $"切换分页面失败（ID {ID}）")
        Finally
            AniControlEnabled -= 1
        End Try
    End Sub
    Private Shared Sub PageChangeRun(Target As MyPageRight)
        AniStop("FrmMain PageChangeRight") '停止主页面的右页面切换动画，防止它与本动画一起触发多次 PageOnEnter
        If Target.Parent IsNot Nothing Then Target.SetValue(ContentPresenter.ContentProperty, Nothing)
        FrmMain.PageRight = Target
        CType(FrmMain.PanMainRight.Child, MyPageRight).PageOnExit()
        AniStart({
            AaCode(Sub()
                       CType(FrmMain.PanMainRight.Child, MyPageRight).PageOnForceExit()
                       FrmMain.PanMainRight.Child = FrmMain.PageRight
                       FrmMain.PageRight.Opacity = 0
                   End Sub, 130),
            AaCode(Sub()
                       '延迟触发页面通用动画，以使得在 Loaded 事件中加载的控件得以处理
                       FrmMain.PageRight.Opacity = 1
                       FrmMain.PageRight.PageOnEnter()
                   End Sub, 30, True)
        }, "PageLeft PageChange")
    End Sub

#End Region

    Public Sub Refresh_Click(sender As Object, e As EventArgs) '由边栏按钮匿名调用
        Refresh(Val(sender.Tag))
    End Sub
    Public Sub Refresh() Implements IRefreshable.Refresh
        Refresh(FrmMain.PageCurrentSub)
    End Sub
    Public Sub Refresh(SubType As FormMain.PageSubType)
        If ModModpackMode.Enabled AndAlso SubType = FormMain.PageSubType.InstanceExport Then Return
        If Instance IsNot Nothing AndAlso McInstanceLock.IsLocked(Instance) Then
            Hint("该版本正在被修改，请稍后再刷新！", HintType.Red)
            Return
        End If
        Select Case SubType
            Case FormMain.PageSubType.InstanceMod
                PageInstanceMod.Refresh()
                ItemMod.Checked = True
            Case FormMain.PageSubType.InstanceExport
                FrmInstanceExport?.RefreshAll()
                ItemExport.Checked = True
            Case FormMain.PageSubType.InstanceModify
                FrmDownloadModify?.PageLoaderRestart()
                ItemModify.Checked = True
            Case FormMain.PageSubType.InstanceRestorePoints
                DirectCast(PageGet(SubType), PageInstanceRestorePoints).Refresh()
                ItemRestorePoints.Checked = True
            Case FormMain.PageSubType.InstanceKeybindings
                DirectCast(PageGet(SubType), PageInstanceKeybindings).RefreshBindings()
                ItemKeybindings.Checked = True
            Case FormMain.PageSubType.InstanceSaves To FormMain.PageSubType.InstanceSchematics
                DirectCast(PageGet(SubType), PageInstanceResources).RefreshResources()
                NavigationItem(SubType).Checked = True
        End Select
    End Sub

    Public Sub Reset(sender As Object, e As EventArgs)
        If MyMsgBox("是否要初始化该版本的版本独立设置？该操作不可撤销。", "初始化确认",, "取消", IsWarn:=True) = 1 Then
            If IsNothing(FrmInstanceSetup) Then FrmInstanceSetup = New PageInstanceSetup
            FrmInstanceSetup.Reset()
            ItemSetup.Checked = True
        End If
    End Sub

    '加载当前版本将要选择的 Java
    Public Shared CurrentJavaWorker As New RedoableWorker(Of Java)(Function(c, p) SelectOrDownloadJava(PageInstanceLeft.Instance, False, c, p))
    Public Shared Sub ReloadCurrentJava() Handles Me.Loaded
        If AniControlEnabled <> 0 Then Return
        If PageInstanceLeft.Instance Is Nothing Then Return
        CurrentJavaWorker.Start()
    End Sub

End Class
