Friend Module ModModpackMode
    Friend Const SettingKey As String = "LivingModpackMode"
    Friend Const ExportMarkerKey As String = "LivingModpackExported"
    Private CompactSettings As PageSetupModpack

    Friend ReadOnly Property CanEnable As Boolean
        Get
            Return Settings.Get(Of Boolean)(ExportMarkerKey)
        End Get
    End Property

    Friend ReadOnly Property Enabled As Boolean
        Get
            Return CanEnable AndAlso Settings.Get(Of Boolean)(SettingKey)
        End Get
    End Property

    Friend Function LauncherSettingsPage() As MyPageRight
        If Not Enabled Then
            If FrmSetupSystem Is Nothing Then FrmSetupSystem = New PageSetupSystem
            Return FrmSetupSystem
        End If
        If CompactSettings Is Nothing Then CompactSettings = New PageSetupModpack
        Return CompactSettings
    End Function

    Friend Function NormalizeSetupPage(Page As FormMain.PageSubType) As FormMain.PageSubType
        If Enabled AndAlso Page <> FormMain.PageSubType.SetupLaunch AndAlso Page <> FormMain.PageSubType.SetupSystem Then Return FormMain.PageSubType.SetupSystem
        Return Page
    End Function

    Friend Function BlocksPage(Page As FormMain.PageType) As Boolean
        Return Enabled AndAlso (Page = FormMain.PageType.Download OrElse Page = FormMain.PageType.Other OrElse
            Page = FormMain.PageType.Link)
    End Function

    '确认逻辑可注入测试；默认勾选和配置载入不调用此函数。
    Friend Function ConfirmChange(Current As Boolean, Desired As Boolean, Confirm As Func(Of Boolean, Boolean)) As Boolean
        If Current = Desired Then Return Current
        Return If(Confirm(Desired), Desired, Current)
    End Function

    Friend Function ShowWarning(Enable As Boolean) As Boolean
        Dim Message = If(Enable,
            "开启 PCL Living Fork 整合包模式后，将隐藏下载和更多，版本选择仅显示本目录的整合包游戏，不提供新建、添加或导入入口，设置也会精简。" & vbCrLf & vbCrLf & "原有的其他游戏文件夹和收藏记录会保留。之后可在 设置 → 启动器 中关闭此模式。是否继续？",
            "关闭 PCL Living Fork 整合包模式后，将恢复普通启动器界面，允许选择版本并使用下载、更多和完整设置。" & vbCrLf & vbCrLf & "修改版本或组件可能影响整合包正常运行；关闭模式不会删除游戏文件。是否继续？")
        Return MyMsgBox(Message, If(Enable, "开启整合包模式", "关闭整合包模式"), If(Enable, "开启", "关闭"), "取消", IsWarn:=True) = 1
    End Function

    Friend Sub Toggle(Check As MyCheckBox, User As Boolean)
        If Not User Then Return
        If Check.Checked AndAlso Not CanEnable Then
            Check.Checked = False
            MyMsgBox("只有导出并附带 PCL Living Fork 启动器的整合包才能开启此模式。" & vbCrLf & vbCrLf &
                "当前目录的 PCL\Setup.ini 中没有整合包导出标记。请先导出整合包，再运行解压后的启动器。", "无法开启整合包模式")
            Return
        End If
        Dim Current = Enabled
        Dim Confirmed = ConfirmChange(Current, Check.Checked, AddressOf ShowWarning)
        If Confirmed <> Current Then Settings.Set(SettingKey, Confirmed)
        Check.Checked = Enabled
    End Sub

    Friend Sub OnChanged(Value As Object)
        RunInUi(
            Sub()
                PageSetupUI.HiddenRefresh()
                ApplyVisibility()
                FrmMain?.UpdateTitleBar()
                FrmLaunchLeft?.RefreshButtonsUI()
                RefreshFolderScope()
                If FrmMain IsNot Nothing AndAlso FrmMain.PageCurrent = FormMain.PageType.Setup Then
                    FrmSetupLeft?.PageChange(FormMain.PageSubType.SetupSystem, True)
                End If
            End Sub)
    End Sub

    Friend Function DisplayTitle(Title As String) As String
        Return If(Enabled, If(String.IsNullOrWhiteSpace(Title), LauncherDisplayName, Title) & " - 整合包模式", Title)
    End Function

    Friend Sub ApplyVisibility(Optional Main As FormMain = Nothing)
        Main = If(Main, FrmMain)
        Dim Active = Enabled
        Dim ForceShow = PageSetupUI.HiddenForceShow
        If Main IsNot Nothing Then
            ApplyHeader(Main.PanTitleSelect, Main.BtnTitleSelect1, Main.BtnTitleSelect2, Main.BtnTitleSelect3, Main.BtnTitleSelect4)
        End If
        If FrmSetupLeft IsNot Nothing Then
            FrmSetupLeft.ItemSystem.Title = If(Active, "启动器", "其他")
            FrmSetupLeft.ItemLaunch.Visibility = If(Active OrElse ForceShow OrElse Not Settings.Get(Of Boolean)("UiHiddenSetupLaunch"), Visibility.Visible, Visibility.Collapsed)
            FrmSetupLeft.ItemSystem.Visibility = If(Active OrElse ForceShow OrElse Not Settings.Get(Of Boolean)("UiHiddenSetupSystem"), Visibility.Visible, Visibility.Collapsed)
            FrmSetupLeft.ItemUI.Visibility = If(Active OrElse (Not ForceShow AndAlso Settings.Get(Of Boolean)("UiHiddenSetupUi")), Visibility.Collapsed, Visibility.Visible)
            FrmSetupLeft.ItemLink.Visibility = Visibility.Collapsed
            Dim VisibleCount = {FrmSetupLeft.ItemLaunch, FrmSetupLeft.ItemSystem, FrmSetupLeft.ItemUI}.Count(Function(Item) Item.Visibility = Visibility.Visible)
            FrmSetupLeft.PanItem.Visibility = If(Active OrElse ForceShow OrElse VisibleCount >= 2, Visibility.Visible, Visibility.Collapsed)
            If FrmSetupLeft.ItemSystem.Buttons IsNot Nothing Then
                For Each Button As UIElement In FrmSetupLeft.ItemSystem.Buttons
                    Button.Visibility = If(Active, Visibility.Collapsed, Visibility.Visible)
                Next
            End If
        End If
        If FrmLaunchLeft IsNot Nothing Then
            FrmLaunchLeft.BtnVersion.Visibility = If(Active OrElse ForceShow OrElse Not Settings.Get(Of Boolean)("UiHiddenFunctionSelect"), Visibility.Visible, Visibility.Collapsed)
            ApplyLaunchLayout(FrmLaunchLeft)
        End If
        ApplyInstanceVisibility()
    End Sub

    Friend Function NormalizeInstancePage(Page As FormMain.PageSubType) As FormMain.PageSubType
        If Page = FormMain.PageSubType.InstanceBackups Then Return FormMain.PageSubType.InstanceSaves
        Return If(Enabled AndAlso Page = FormMain.PageSubType.InstanceExport, FormMain.PageSubType.InstanceOverall, Page)
    End Function

    Friend Function CanChangeMinecraft(Current As String, Requested As String) As Boolean
        Return Not Enabled OrElse (Not String.IsNullOrWhiteSpace(Current) AndAlso String.Equals(Current, Requested, StringComparison.Ordinal))
    End Function

    Friend Function ConfirmModAction(ActionName As String, Optional Confirm As Func(Of String, Boolean) = Nothing) As Boolean
        If Not Enabled Then Return True
        If Confirm IsNot Nothing Then Return Confirm(ActionName)
        Dim Detail = "修改整合包的 Mod 可能导致依赖缺失、版本不兼容或整合包无法启动。操作前建议备份存档，并确认整合包允许这项改动。"
        If ActionName.Contains("删除") Then Detail &= vbCrLf & "按住 Shift 删除会永久移除文件。"
        Return MyMsgBox($"即将{ActionName}。{vbCrLf}{vbCrLf}{Detail}", "整合包 Mod 操作警告", "继续", "取消", IsWarn:=True) = 1
    End Function

    Friend Sub ApplyInstanceVisibility()
        Dim Hidden = If(Enabled, Visibility.Collapsed, Visibility.Visible)
        If FrmInstanceLeft IsNot Nothing Then FrmInstanceLeft.ItemExport.Visibility = Hidden
        FrmInstanceOverall?.RefreshModpackVisibility()
        FrmDownloadModify?.RefreshModpackRestrictions()
        If FrmInstanceMod IsNot Nothing Then
            FrmInstanceMod.BtnManageDownload.Visibility = Hidden
            FrmInstanceMod.BtnHintDownload.Visibility = Hidden
            HideButtonColumn(FrmInstanceMod.BtnManageDownload)
            HideButtonColumn(FrmInstanceMod.BtnHintDownload)
            FrmInstanceMod.LabEmptyHint.Text = If(Enabled, "你可以从本地文件安装 Mod，或打开 Mod 文件夹。",
                "你可以下载新的 Mod，或者从已经下载好的文件安装 Mod。" & vbLf & "如果你已经安装了 Mod，可能是版本隔离设置有误，请在设置中调整版本隔离选项。")
        End If
        If FrmInstanceModDisabled IsNot Nothing Then
            FrmInstanceModDisabled.BtnVersion.Visibility = Hidden
            FrmInstanceModDisabled.LabModHint.Text = If(Enabled, "该版本未安装可用的 Mod 加载器，可以在「修改」页检查。修改组件可能影响整合包运行。",
                "你需要先安装 Forge、Fabric 等 Mod 加载器才能使用 Mod，请在下载页面安装这些版本。" & vbLf & "如果你已经安装过了 Mod 加载器，那么你很可能选择了错误的版本，请点击版本选择按钮切换版本。")
            FrmInstanceModDisabled.BtnDownload_Loaded()
        End If
    End Sub

    Private Sub HideButtonColumn(Button As FrameworkElement)
        Dim Parent = TryCast(Button.Parent, Grid)
        If Parent Is Nothing Then Return
        Dim Column = Parent.ColumnDefinitions(Grid.GetColumn(Button))
        Column.SharedSizeGroup = If(Enabled, Nothing, "Button")
        Column.Width = If(Enabled, New GridLength(0), GridLength.Auto)
    End Sub

    Friend Sub ApplyHeader(Panel As Panel, Download As UIElement, Link As UIElement, Setup As MyRadioButton, Other As UIElement)
        Dim Active = Enabled
        Dim ForceShow = PageSetupUI.HiddenForceShow
        Panel.Visibility = If(Active OrElse ForceShow OrElse Not (Settings.Get(Of Boolean)("UiHiddenPageDownload") AndAlso
            Settings.Get(Of Boolean)("UiHiddenPageLink") AndAlso Settings.Get(Of Boolean)("UiHiddenPageSetup") AndAlso Settings.Get(Of Boolean)("UiHiddenPageOther")), Visibility.Visible, Visibility.Collapsed)
        Download.Visibility = If(Active OrElse (Not ForceShow AndAlso Settings.Get(Of Boolean)("UiHiddenPageDownload")), Visibility.Collapsed, Visibility.Visible)
        Link.Visibility = Visibility.Collapsed
        Other.Visibility = If(Active OrElse (Not ForceShow AndAlso Settings.Get(Of Boolean)("UiHiddenPageOther")), Visibility.Collapsed, Visibility.Visible)
        Setup.Visibility = If(Active OrElse ForceShow OrElse Not Settings.Get(Of Boolean)("UiHiddenPageSetup"), Visibility.Visible, Visibility.Collapsed)
        Setup.Text = "设置"
    End Sub

    Friend Sub ApplyLaunchLayout(Target As PageLaunchLeft)
        Grid.SetColumn(Target.BtnMore, 3)
        Grid.SetColumnSpan(Target.BtnMore, 1)
    End Sub

    Friend Function CreatePackFolderList() As List(Of McFolder)
        Return New List(Of McFolder) From {New McFolder With {.Name = "整合包文件夹", .Location = MinecraftFolderPolicy.PackFolder(Paths.Base), .Type = McFolder.Types.Vanilla}}
    End Function

    Friend Function AllowsInstance(Folder As String) As Boolean
        Return Not Enabled OrElse MinecraftFolderPolicy.IsPackVersion(Paths.Base, Folder)
    End Function

    Private FolderScopeRevision As Integer
    Private Sub RefreshFolderScope()
        Dim Revision = Threading.Interlocked.Increment(FolderScopeRevision)
        Dim PackMode = Enabled
        Dim Folder = McFolderSelected
        Dim SelectedName = ReadIni(Folder & "PCL.ini", "Version")
        McInstanceListForceRefresh = True
        McFolderListLoader.Start(IsForceRestart:=True)
        FrmSelectLeft?.RefreshForModpackMode()
        RunInNewThread(
            Sub()
                Try
                    McFolderListLoader.WaitForExit()
                    LoaderFolderRun(McInstanceListLoader, Folder, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\", WaitForExit:=True)
                    Dim Instances = McInstanceList.Values.SelectMany(Function(List) List).Where(Function(Instance) Instance.State <> McInstance.McInstanceState.Error).ToList()
                    Dim Selected = If(Instances.FirstOrDefault(Function(Instance) Instance.Name = SelectedName), Instances.FirstOrDefault())
                    RunInUi(
                        Sub()
                            If Revision <> FolderScopeRevision OrElse PackMode <> Enabled OrElse Not PathUtils.IsSame(Folder, McFolderSelected) Then Return
                            McInstanceSelected = Selected
                            FrmLaunchLeft?.RefreshPage(True, False)
                            FrmLaunchLeft?.RefreshButtonsUI()
                        End Sub)
                Catch ex As Exception
                    If Not ex.IsCanceled Then Logger.Warn(ex, "刷新整合包文件夹范围失败")
                End Try
            End Sub, "Refresh Modpack Folder Scope")
    End Sub

    '只写入本次导出的副本，且在个性化配置复制完毕后调用，避免继承制作者的开关状态。
    Friend Sub ConfigureExport(ExportRoot As String, IncludeLauncher As Boolean, PackMode As Boolean)
        If Not IncludeLauncher Then Return
        WriteIni(IO.Path.Combine(ExportRoot, "PCL", "Setup.ini"), ExportMarkerKey, True)
        WriteIni(IO.Path.Combine(ExportRoot, "PCL", "Setup.ini"), SettingKey, PackMode)
    End Sub
End Module
