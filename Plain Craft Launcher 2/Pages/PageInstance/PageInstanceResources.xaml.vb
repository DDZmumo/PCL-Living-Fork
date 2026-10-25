Imports System.IO
Imports System.Threading

Public Class PageInstanceResources
    Implements IRefreshable

    Private ReadOnly Kind As InstanceResourceKind
    Private ReadOnly ResourceLoader As New LoaderTask(Of ResourceRequest, ResourceResult)("读取游戏资源", AddressOf ReadResources)
    Private Owner As McInstance
    Private CurrentRoot As String
    Private CurrentFolder As String
    Private RecycleRoot As String
    Private Entries As New List(Of InstanceResourceEntry)
    Private Selected As InstanceResourceEntry
    Private Operation As CancellationTokenSource
    Private Busy As Boolean
    Private ReadOnly SelectedPaths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private Online As New Dictionary(Of String, InstanceResourceOnlineInfo)(StringComparer.OrdinalIgnoreCase)
    Private OnlineCancellation As CancellationTokenSource
    Private OnlineRevision As Integer
    Private Checking As Boolean
    Private Rendering As Boolean
    Private Filter As Integer
    Private ReadOnly Rows As New Dictionary(Of String, MyLocalModItem)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly ScreenshotRows As New Dictionary(Of String, MyScreenshotItem)(StringComparer.OrdinalIgnoreCase)
    Private BottomBarShownCount As Integer
    Private KeyboardOwner As FormMain
    Private ReadOnly Property NeedsSchematicLoader As Boolean
        Get
            Return Kind = InstanceResourceKind.Schematics AndAlso Owner IsNot Nothing AndAlso Owner.Version.ModLoaders = ModLoaders.None
        End Get
    End Property

    Private Class ResourceRequest
        Public Instance As McInstance
        Public Root As String
        Public Folder As String
        Public GameFolder As String
    End Class
    Private Class ResourceResult
        Public Request As ResourceRequest
        Public Entries As List(Of InstanceResourceEntry)
        Public Backups As List(Of JObject)
        Public BackupError As String
    End Class

    Public Sub New()
        InitializeComponent()
        LocalResourceUi.ApplySelectionIcons(BtnUpdate, BtnDelete, BtnDeselect)
        BtnExport.Logo = Logo.IconButtonSave
        BtnBackupRestore.Logo = Logo.IconButtonRefresh
        For Each Button As MyRadioButton In PanFilter.Children
            Button.LabText.Margin = New Thickness(-2, 0, 8, 0)
        Next
    End Sub
    Friend Sub New(Kind As InstanceResourceKind)
        Me.New()
        Me.Kind = Kind
    End Sub

    Private Sub LoaderInit() Handles Me.Initialized
        PageLoaderInit(Load, PanLoad, PanAllBack, Nothing, ResourceLoader, AddressOf ResourcesLoaded, AutoRun:=False)
    End Sub
    Private Sub PageLoaded() Handles Me.Loaded
        If Kind = InstanceResourceKind.Saves Then AddHandler WorldBackups.Changed, AddressOf RefreshResources
        If KeyboardOwner Is Nothing AndAlso FrmMain IsNot Nothing Then
            KeyboardOwner = FrmMain
            AddHandler KeyboardOwner.KeyDown, AddressOf MainKeyDown
        End If
        ClearSelection()
        PanBack.ScrollToHome()
        RefreshResources()
    End Sub
    Private Sub ClearSelection() Handles Me.PageExit
        Rendering = True
        SelectedPaths.Clear()
        ClearBackupSelection()
        For Each Row In Rows.Values
            Row.Checked = False
        Next
        For Each Tile In ScreenshotRows.Values
            Tile.Checked = False
        Next
        Rendering = False
        SelectResource(Nothing)
    End Sub
    Private Sub PageUnloaded() Handles Me.Unloaded
        If Kind = InstanceResourceKind.Saves Then RemoveHandler WorldBackups.Changed, AddressOf RefreshResources
        If KeyboardOwner IsNot Nothing Then
            RemoveHandler KeyboardOwner.KeyDown, AddressOf MainKeyDown
            KeyboardOwner = Nothing
        End If
        ResourceLoader.Cancel()
        For Each Tile In ScreenshotRows.Values
            Tile.ReleaseThumbnail()
        Next
        Operation?.Cancel()
        CancelOnline()
        AniStop("Resource Sidebar " & PageUuid)
        BottomBarShownCount = 0
        CardSelect.Visibility = Visibility.Collapsed
        CardSelect.Opacity = 0
        TransSelect.Y = -10
    End Sub

    Public Sub RefreshResources() Implements IRefreshable.Refresh
        Dim Instance = PageInstanceLeft.Instance
        If Instance Is Nothing OrElse Not ModModpackMode.AllowsInstance(Instance.PathVersion) Then Return
        Dim GameFolder = Instance.PathIndie
        Dim Root = InstanceResourceStore.Root(GameFolder, Kind)
        If Owner IsNot Instance OrElse Not PathUtils.IsSame(CurrentRoot, Root) Then
            CurrentFolder = Root
            SelectedPaths.Clear()
            Online.Clear()
            Filter = 0
            WorldBackupPoints.Clear()
            ResetWorldGroups()
        End If
        CancelOnline()
        Owner = Instance
        CurrentRoot = Root
        If CurrentFolder Is Nothing OrElse Not Directory.Exists(CurrentFolder) Then CurrentFolder = Root
        Dim MinecraftRoot = Path.GetDirectoryName(Path.GetDirectoryName(Instance.PathVersion.TrimEnd("\"c)))
        RecycleRoot = Path.Combine(MinecraftRoot, "PCL", "ResourceRecycle", Kind.ToString())
        ResourceLoader.Start(New ResourceRequest With {.Instance = Instance, .Root = Root, .Folder = CurrentFolder, .GameFolder = GameFolder}, IsForceRestart:=True)
    End Sub
    Private Sub ReadResources(Loader As LoaderTask(Of ResourceRequest, ResourceResult))
        Dim Request = Loader.Input
        Dim Items = InstanceResourceStore.Scan(Request.Root, Request.Folder, Kind, Loader.CreateCancellationToken())
        If Loader.IsCanceled Then Return
        Dim Result As New ResourceResult With {.Request = Request, .Entries = Items}
        If Kind = InstanceResourceKind.Saves Then
            Try
                Result.Backups = WorldBackups.Points(Request.GameFolder)
            Catch ex As Exception
                Logger.Warn(ex, "读取此游戏目录的存档备份失败")
                Result.BackupError = "存档备份读取失败，请刷新后重试。"
            End Try
        End If
        Loader.Output = Result
    End Sub
    Private Sub ResourcesLoaded(Loader As LoaderTask(Of ResourceRequest, ResourceResult))
        Dim Result = Loader.Output
        If Result Is Nothing OrElse Owner IsNot Result.Request.Instance OrElse PageInstanceLeft.Instance IsNot Owner OrElse Not PathUtils.IsSame(CurrentFolder, Result.Request.Folder) Then Return
        If Kind = InstanceResourceKind.Saves AndAlso Not PathUtils.IsSame(Owner.PathIndie, Result.Request.GameFolder) Then Return
        WorldBackupPoints = If(Result.Backups, New List(Of JObject))
        LabBackupStatus.Text = Result.BackupError
        LabBackupStatus.Visibility = If(String.IsNullOrEmpty(Result.BackupError), Visibility.Collapsed, Visibility.Visible)
        ShowResources(Result.Request.Root, Result.Request.Folder, Result.Entries)
        CheckUpdates()
    End Sub

    Friend Sub ShowResources(Root As String, Folder As String, Items As IEnumerable(Of InstanceResourceEntry))
        CurrentRoot = Root
        CurrentFolder = Folder
        Entries = Items.ToList()
        BackupSettings.Visibility = If(Kind = InstanceResourceKind.Saves, Visibility.Visible, Visibility.Collapsed)
        BtnBackupAll.Visibility = BackupSettings.Visibility
        PanManageButtons.ColumnDefinitions(4).SharedSizeGroup = If(Kind = InstanceResourceKind.Saves, "Button", Nothing)
        If Kind = InstanceResourceKind.Saves Then BackupSettings.BindOwner(Owner)
        For Each Key In Online.Keys.ToList()
            Dim Current = Entries.FirstOrDefault(Function(Item) PathUtils.IsSame(Item.FullPath, Key))
            If Current Is Nothing OrElse Current.ModifiedUtc <> Online(Key).Entry.ModifiedUtc OrElse Current.Length <> Online(Key).Entry.Length Then Online.Remove(Key)
        Next
        BtnOpenFolder.ToolTip = Folder
        SearchBox.HintText = "搜索" & InstanceResourceStore.Title(Kind) & If(InstanceResourceUpdates.Supported(Kind), "名称 / 描述 / 标签", "名称 / 文件信息")
        Dim IsSchematic = Kind = InstanceResourceKind.Schematics
        Dim NeedsLoader = NeedsSchematicLoader
        BtnDownload.Text = If(IsSchematic, If(NeedsLoader, "先安装加载器", "下载投影 Mod"), "下载新" & InstanceResourceStore.Title(Kind))
        BtnHintDownload.Text = If(NeedsLoader, "先安装 Mod 加载器", BtnDownload.Text)
        BtnDownload.ToolTip = If(IsSchematic, If(NeedsLoader, "当前版本尚未安装 Mod 加载器，点击前往此版本的修改页面。", "前往 Mod 下载页面搜索 Litematica（投影），使用当前游戏版本及加载器筛选。"), Nothing)
        BtnHintDownload.ToolTip = BtnDownload.ToolTip
        BtnDownload.Visibility = If(NeedsLoader OrElse ((InstanceResourceUpdates.Supported(Kind) OrElse IsSchematic) AndAlso Not ModModpackMode.Enabled), Visibility.Visible, Visibility.Collapsed)
        PanManageButtons.ColumnDefinitions(2).SharedSizeGroup = If(BtnDownload.Visibility = Visibility.Visible, "Button", Nothing)
        BtnUpdate.Visibility = If(InstanceResourceUpdates.Supported(Kind), Visibility.Visible, Visibility.Collapsed)
        BtnHintDownload.Visibility = BtnDownload.Visibility
        LabEmptyTitle.Text = "暂无" & InstanceResourceStore.Title(Kind)
        LabEmptyHint.Text = If(InstanceResourceUpdates.Supported(Kind), "你可以下载新的" & InstanceResourceStore.Title(Kind) & "，或者从已经下载好的文件安装。", "可以从本地文件安装，或将文件拖入此页面。") & vbCrLf & "如果资源已经存在，请在版本设置中检查版本隔离选项。"
        If IsSchematic Then
            LabEmptyHint.Text = "投影/原理图文件需要配合投影 Mod（如 Litematica）在游戏内使用。" & vbCrLf &
                If(NeedsLoader, "当前版本还没有 Mod 加载器，请先前往修改页面安装加载器。", "可以前往 Mod 下载页面获取投影 Mod，再安装或拖入原理图文件。")
        End If
        BtnUp.Visibility = If(Kind = InstanceResourceKind.Schematics AndAlso Not PathUtils.IsSame(Root, Folder), Visibility.Visible, Visibility.Collapsed)
        RenderList()
    End Sub
    Private Sub SearchChanged() Handles SearchBox.TextChanged
        RenderList()
    End Sub
    Private Sub RenderList()
        If PanList Is Nothing Then Return
        Rendering = True
        Try
        For Each Button As MyRadioButton In PanFilter.Children
            Button.Checked = CInt(Button.Tag) = Filter
        Next
        Dim Query = SearchBox.Text.Trim()
        Dim Matches = Entries.Where(Function(Item) Query = "" OrElse (Item.Name & " " & Item.Description & " " & OnlineSearchText(Item)).IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0).ToList()
        Dim Visible = Matches.Where(Function(Item) Filter = 0 OrElse (Filter = 1 AndAlso HasUpdate(Item)) OrElse (Filter = 2 AndAlso Not Item.IsDirectory) OrElse (Filter = 3 AndAlso Item.IsDirectory)).ToList()
        BtnFilterAll.Text = If(Query = "", "全部", "搜索结果") & $" ({Matches.Count})"
        BtnFilterUpdates.Text = $"可更新 ({Matches.Where(Function(Item) HasUpdate(Item)).Count()})"
        BtnFilterUpdates.Visibility = If(InstanceResourceUpdates.Supported(Kind) AndAlso (Filter = 1 OrElse Matches.Any(Function(Item) HasUpdate(Item))), Visibility.Visible, Visibility.Collapsed)
        BtnFilterFiles.Text = $"文件 ({Matches.Where(Function(Item) Not Item.IsDirectory).Count()})"
        BtnFilterFolders.Text = $"文件夹 ({Matches.Where(Function(Item) Item.IsDirectory).Count()})"
        Dim Mixed = Entries.Any(Function(Item) Item.IsDirectory) AndAlso Entries.Any(Function(Item) Not Item.IsDirectory)
        BtnFilterFiles.Visibility = If(Mixed OrElse Filter = 2, Visibility.Visible, Visibility.Collapsed)
        BtnFilterFolders.Visibility = If(Mixed OrElse Filter = 3, Visibility.Visible, Visibility.Collapsed)
        LabEmpty.Visibility = If(Visible.Any(), Visibility.Collapsed, Visibility.Visible)
        LabEmpty.Text = If(Query = "" AndAlso Filter = 0, "暂无" & InstanceResourceStore.Title(Kind) & "，可以从本地文件安装" & If(Kind = InstanceResourceKind.Screenshots, "或拖入图片。", "或拖入文件夹。"), If(Filter = 1, "未发现可更新的资源，可刷新列表重新检查。", "没有找到匹配的资源。"))
        SelectedPaths.IntersectWith(Visible.Select(Function(Item) Item.FullPath))
        PanList.Visibility = If(Kind = InstanceResourceKind.Screenshots OrElse Kind = InstanceResourceKind.Saves, Visibility.Collapsed, Visibility.Visible)
        PanWorlds.Visibility = If(Kind = InstanceResourceKind.Saves, Visibility.Visible, Visibility.Collapsed)
        PanScreenshots.Visibility = If(Kind = InstanceResourceKind.Screenshots, Visibility.Visible, Visibility.Collapsed)
        If Kind = InstanceResourceKind.Screenshots Then
            RenderScreenshots(Visible)
            SelectResource(Selection().FirstOrDefault())
            Return
        End If
        If Kind = InstanceResourceKind.Saves Then
            RenderWorldGroups(Visible, Query)
            SelectResource(Selection().FirstOrDefault())
            Return
        End If
        For Each Key In Rows.Keys.Where(Function(Value) Not Entries.Any(Function(Item) String.Equals(Item.FullPath, Value, StringComparison.OrdinalIgnoreCase))).ToList()
            Rows.Remove(Key)
        Next
        Dim DesiredRows As New List(Of MyLocalModItem)
        For Each Entry In Visible
            Dim Info As InstanceResourceOnlineInfo = Nothing
            Online.TryGetValue(Entry.FullPath, Info)
            Dim Row As MyLocalModItem = Nothing
            If Not Rows.TryGetValue(Entry.FullPath, Row) Then
                Row = CreateResourceRow()
                Rows.Add(Entry.FullPath, Row)
            End If
            Row.BindResource(Entry, Info)
            Row.Checked = SelectedPaths.Contains(Entry.FullPath)
            DesiredRows.Add(Row)
        Next
        '在线元数据到达时复用现有控件，避免移出视口再插回导致悬停、动画和滚动跳变。
        For Each Row In PanList.Children.OfType(Of MyLocalModItem)().Where(Function(Item) Not DesiredRows.Contains(Item)).ToList()
            PanList.Children.Remove(Row)
        Next
        For Index = 0 To DesiredRows.Count - 1
            Dim Row = DesiredRows(Index)
            If PanList.Children.IndexOf(Row) = Index Then Continue For
            PanList.Children.Remove(Row)
            PanList.Children.Insert(Index, Row)
        Next
        Dim NextSelected = If(Selected Is Nothing, Nothing, Visible.FirstOrDefault(Function(Item) SelectedPaths.Contains(Item.FullPath) AndAlso PathUtils.IsSame(Item.FullPath, Selected.FullPath)))
        SelectResource(If(NextSelected, Visible.FirstOrDefault(Function(Item) SelectedPaths.Contains(Item.FullPath))))
        Finally
            Rendering = False
        End Try
    End Sub

    Private Sub RenderScreenshots(Visible As List(Of InstanceResourceEntry))
        For Each Key In ScreenshotRows.Keys.Where(Function(Value) Not Entries.Any(Function(Item) String.Equals(Item.FullPath, Value, StringComparison.OrdinalIgnoreCase))).ToList()
            ScreenshotRows(Key).ReleaseThumbnail()
            ScreenshotRows.Remove(Key)
        Next
        Dim Desired As New List(Of MyScreenshotItem)
        For Each Entry In Visible
            Dim Tile As MyScreenshotItem = Nothing
            If Not ScreenshotRows.TryGetValue(Entry.FullPath, Tile) Then
                Tile = New MyScreenshotItem
                AddHandler Tile.Changed, AddressOf ScreenshotSelectionChanged
                AddHandler Tile.ClickRequested, Sub(Sender, Count) ScreenshotClick(Sender.Entry, Count)
                AddHandler Tile.Loaded, Sub() Tile.UpdateViewport(PanBack)
                ScreenshotRows.Add(Entry.FullPath, Tile)
            End If
            Tile.Bind(Entry)
            Tile.Checked = SelectedPaths.Contains(Entry.FullPath)
            Desired.Add(Tile)
        Next
        For Each Tile In PanScreenshots.Children.OfType(Of MyScreenshotItem)().Where(Function(Item) Not Desired.Contains(Item)).ToList()
            Tile.ReleaseThumbnail()
            PanScreenshots.Children.Remove(Tile)
        Next
        For Index = 0 To Desired.Count - 1
            Dim Tile = Desired(Index)
            If PanScreenshots.Children.IndexOf(Tile) = Index Then Continue For
            PanScreenshots.Children.Remove(Tile)
            PanScreenshots.Children.Insert(Index, Tile)
        Next
        RefreshScreenshotViewport()
    End Sub
    Private Sub RefreshScreenshotViewport() Handles PanBack.ScrollChanged, PanBack.SizeChanged
        If Kind <> InstanceResourceKind.Screenshots Then Return
        For Each Tile As MyScreenshotItem In PanScreenshots.Children
            Tile.UpdateViewport(PanBack)
        Next
    End Sub
    Private Sub ScreenshotSelectionChanged(Tile As MyScreenshotItem)
        If Rendering OrElse Busy Then Return
        If Tile.Checked Then SelectedPaths.Add(Tile.Entry.FullPath) Else SelectedPaths.Remove(Tile.Entry.FullPath)
        SelectResource(Selection().FirstOrDefault())
    End Sub
    Friend Sub ScreenshotClick(Entry As InstanceResourceEntry, Count As Integer, Optional Open As Action(Of ProcessStartInfo) = Nothing)
        If Busy OrElse Kind <> InstanceResourceKind.Screenshots OrElse Entry Is Nothing OrElse Count < 1 Then Return
        If Not Entries.Any(Function(Item) Item Is Entry) Then Return
        Rendering = True
        If Keyboard.Modifiers <> ModifierKeys.Control OrElse Count = 2 Then
            SelectedPaths.Clear()
            SelectedPaths.Add(Entry.FullPath)
            For Each Tile In ScreenshotRows.Values
                Tile.Checked = Tile.Entry Is Entry
            Next
        Else
            Dim Tile = ScreenshotRows(Entry.FullPath)
            Tile.Checked = Not Tile.Checked
            If Tile.Checked Then SelectedPaths.Add(Entry.FullPath) Else SelectedPaths.Remove(Entry.FullPath)
        End If
        Rendering = False
        SelectResource(Selection().FirstOrDefault())
        If Count <> 2 Then Return
        Try
            InstanceResourceStore.ValidateEntry(CurrentRoot, Entry)
            Dim Start As New ProcessStartInfo(Entry.FullPath) With {.UseShellExecute = True}
            If Open Is Nothing Then Open = Sub(Info) Process.Start(Info)
            Open(Start)
        Catch ex As Exception
            Logger.Error(ex, "使用默认图片软件打开截图失败", LogBehavior.Toast)
        End Try
    End Sub

    Private Function CreateResourceRow() As MyLocalModItem
        Dim Row As New MyLocalModItem With {.SelectionBar = CardSelect}
        AddHandler Row.Changed, AddressOf SelectionChanged
        AddHandler Row.Click, Sub() Row.Checked = Not Row.Checked
        AddHandler Row.MouseRightButtonUp, Sub(Sender, e) ShowDetails(Row.ResourceEntry)
        Row.ResourceUpdateRequested = Sub() UpdateResources({Row.ResourceEntry})
        Row.ResourceDetailsRequested = Sub() ShowDetails(Row.ResourceEntry)
        Row.Buttons = ResourceButtons(Row)
        AddHandler Row.MouseLeftButtonDown, Sub(Sender, e)
                                               If e.ClickCount <> 2 OrElse Busy OrElse Kind <> InstanceResourceKind.Schematics OrElse Not Row.ResourceEntry.IsDirectory Then Return
                                               CurrentFolder = Row.ResourceEntry.FullPath
                                               SelectedPaths.Clear()
                                               Filter = 0
                                               RefreshResources()
                                           End Sub
        Return Row
    End Function

    Private Function OnlineSearchText(Entry As InstanceResourceEntry) As String
        Dim Info As InstanceResourceOnlineInfo = Nothing
        If Not Online.TryGetValue(Entry.FullPath, Info) Then Return ""
        Return Info.ProjectName & " " & String.Join(" ", Info.Tags)
    End Function
    Private Function HasUpdate(Entry As InstanceResourceEntry) As Boolean
        Dim Info As InstanceResourceOnlineInfo = Nothing
        Return Online.TryGetValue(Entry.FullPath, Info) AndAlso Info.HasUpdate
    End Function
    Private Function Selection() As List(Of InstanceResourceEntry)
        Return Entries.Where(Function(Item) SelectedPaths.Contains(Item.FullPath)).ToList()
    End Function
    Private Sub SelectionChanged(Sender As Object, e As RouteEventArgs)
        If Rendering Then Return
        Dim Row = DirectCast(Sender, MyLocalModItem), Entry = Row.ResourceEntry
        If Busy Then
            e.Handled = True
            Return
        End If
        ClearBackupSelection()
        If Row.Checked Then SelectedPaths.Add(Entry.FullPath) Else SelectedPaths.Remove(Entry.FullPath)
        SelectResource(If(Row.Checked, Entry, Selection().LastOrDefault()))
    End Sub
    Friend Sub ChangeAllSelected(Value As Boolean)
        If Busy Then Return
        SelectedPaths.Clear()
        ClearBackupSelection()
        Rendering = True
        For Each Row As MyLocalModItem In VisibleResourceRows()
            Row.Checked = Value
            If Value Then SelectedPaths.Add(Row.ResourceEntry.FullPath)
        Next
        For Each Tile As MyScreenshotItem In PanScreenshots.Children
            Tile.Checked = Value
            If Value Then SelectedPaths.Add(Tile.Entry.FullPath)
        Next
        Rendering = False
        SelectResource(Selection().FirstOrDefault())
    End Sub
    Private Sub SelectAllClick() Handles BtnSelectAll.Click
        ChangeAllSelected(SelectedPaths.Count < If(Kind = InstanceResourceKind.Screenshots, PanScreenshots.Children.Count, VisibleResourceRows().Count()))
    End Sub
    Private Sub DeselectClick() Handles BtnDeselect.Click
        ChangeAllSelected(False)
    End Sub
    Private Sub FilterChanged(Sender As MyRadioButton, User As Boolean) Handles BtnFilterAll.Check, BtnFilterUpdates.Check, BtnFilterFiles.Check, BtnFilterFolders.Check
        If Rendering OrElse PanList Is Nothing Then Return
        Filter = CInt(Sender.Tag)
        RenderList()
    End Sub
    Private Sub MainKeyDown(Sender As Object, e As KeyEventArgs)
        If FrmMain?.PageRight Is Me Then ResourceKeyDown(Sender, e)
    End Sub
    Private Sub SearchKeyDown(Sender As Object, e As KeyEventArgs) Handles SearchBox.PreviewKeyDown
        If SearchBox.Text = "" AndAlso e.Key = Key.A AndAlso Keyboard.Modifiers = ModifierKeys.Control Then PanBack.Focus()
    End Sub
    Private Sub ResourceKeyDown(Sender As Object, e As KeyEventArgs) Handles Me.KeyDown
        If e.Key = Key.Escape AndAlso (SelectedPaths.Count > 0 OrElse SelectedBackupIds().Count > 0) Then ChangeAllSelected(False) : e.Handled = True
        If e.Key = Key.A AndAlso Keyboard.Modifiers = ModifierKeys.Control AndAlso Not SearchBox.IsKeyboardFocusWithin Then ChangeAllSelected(True) : e.Handled = True
    End Sub

    Private Function ResourceButtons(Row As MyLocalModItem) As List(Of MyIconButton)
        Dim Buttons As New List(Of MyIconButton)
        If Kind = InstanceResourceKind.Saves AndAlso QuickPlayWorlds.Supported(Owner) Then
            Dim Play As New MyIconButton With {.Logo = LocalResourceUi.PlayLogo, .LogoScale = 0.9, .ToolTip = "启动此版本并直接进入存档"}
            AddHandler Play.Click, Sub()
                                       If CanChange() Then QuickPlayWorlds.Start(Owner, Row.ResourceEntry.Name)
                                   End Sub
            Buttons.Add(Play)
        End If
        Dim Details As New MyIconButton With {.Logo = Logo.IconButtonInfo, .ToolTip = If(Kind = InstanceResourceKind.Screenshots, "预览截图", "详情")}
        AddHandler Details.Click, Sub() ShowDetails(Row.ResourceEntry)
        Buttons.Add(Details)
        Dim Open As New MyIconButton With {.Logo = Logo.IconButtonOpen, .LogoScale = 1.05, .ToolTip = "打开文件位置"}
        AddHandler Open.Click, Sub() OpenEntry(Row.ResourceEntry)
        Buttons.Add(Open)
        Dim Rename As New MyIconButton With {.Logo = Logo.IconButtonEdit, .ToolTip = "重命名"}
        AddHandler Rename.Click, Sub() RenameEntry(Row.ResourceEntry)
        Buttons.Add(Rename)
        Dim Delete As New MyIconButton With {.Logo = Logo.IconButtonDelete, .ToolTip = "删除"}
        AddHandler Delete.Click, Sub() DeleteResources({Row.ResourceEntry})
        Buttons.Add(Delete)
        Return Buttons
    End Function
    Private Sub ShowDetails(Entry As InstanceResourceEntry)
        If Kind = InstanceResourceKind.Saves Then
            Dim Root = CurrentRoot
            RunInNewThread(Sub()
                               Try
                                   InstanceResourceStore.ValidateEntry(Root, Entry)
                                   Dim Content = WorldMetadata.Describe(Entry.FullPath)
                                   RunInUi(Sub() MyMsgBox(Content, "存档详情", "确定"))
                               Catch ex As Exception
                                   Logger.Error(ex, "读取存档详情失败", LogBehavior.Toast)
                               End Try
                           End Sub, "Local World Details")
            Return
        End If
        If Kind = InstanceResourceKind.Screenshots Then Return
        Dim Info As InstanceResourceOnlineInfo = Nothing
        Online.TryGetValue(Entry.FullPath, Info)
        Dim Choice = MyMsgBox(Entry.Name & vbCrLf & Entry.Description & vbCrLf & Entry.FullPath & If(Info Is Nothing, "", vbCrLf & Info.Status), "资源详情", "确定", If(String.IsNullOrEmpty(Info?.Website), "", "项目主页"))
        If Choice = 2 AndAlso Info?.Website IsNot Nothing Then OpenWebsite(Info.Website)
    End Sub

    Private Sub SelectResource(Entry As InstanceResourceEntry)
        Selected = Entry
        UpdateButtons()
    End Sub

    Private Sub UpdateButtons()
        BtnImportFile.IsEnabled = Not Busy
        BtnUp.IsEnabled = Not Busy
        BtnDownload.IsEnabled = Not Busy
        BtnSelectAll.IsEnabled = Not Busy AndAlso If(Kind = InstanceResourceKind.Screenshots, PanScreenshots.Children.Count, VisibleResourceRows().Count()) > 0
        BtnExport.IsEnabled = Not Busy AndAlso SelectedPaths.Count > 0
        BtnDelete.IsEnabled = BtnExport.IsEnabled
        BtnDeselect.IsEnabled = Not Busy
        BtnUpdate.IsEnabled = Not Busy AndAlso Selection().Any(Function(Item) HasUpdate(Item))
        BtnRestore.IsEnabled = Not Busy AndAlso CurrentRoot IsNot Nothing
        BtnHintRestore.IsEnabled = BtnRestore.IsEnabled
        Try
            BtnRestore.Visibility = If(RecycleRoot IsNot Nothing AndAlso Directory.Exists(RecycleRoot) AndAlso Directory.EnumerateDirectories(RecycleRoot).Any(), Visibility.Visible, Visibility.Collapsed)
        Catch
            BtnRestore.Visibility = Visibility.Collapsed
        End Try
        BtnHintRestore.Visibility = BtnRestore.Visibility
        '后台检查不改变工具栏和列表的位置；取消检查仍可通过切页或刷新执行。
        BtnCancel.Visibility = If(Busy, Visibility.Visible, Visibility.Collapsed)
        PanOperations.Visibility = If(BtnUp.Visibility = Visibility.Visible OrElse LabOperation.Visibility = Visibility.Visible OrElse BtnRestore.Visibility = Visibility.Visible OrElse Busy, Visibility.Visible, Visibility.Collapsed)
        PanEmpty.Visibility = If(Entries.Count = 0 AndAlso Not Busy AndAlso Kind <> InstanceResourceKind.Saves, Visibility.Visible, Visibility.Collapsed)
        PanBack.Visibility = If(PanEmpty.Visibility = Visibility.Visible, Visibility.Collapsed, Visibility.Visible)
        PanList.IsHitTestVisible = Not Busy
        PanWorlds.IsHitTestVisible = Not Busy
        PanScreenshots.IsHitTestVisible = Not Busy
        RefreshBackupActions()
    End Sub

    Private Sub CancelOnline()
        Interlocked.Increment(OnlineRevision)
        OnlineCancellation?.Cancel()
        OnlineCancellation = Nothing
        Checking = False
    End Sub
    Private Sub CheckUpdates()
        If Not InstanceResourceUpdates.Supported(Kind) OrElse Owner Is Nothing OrElse Busy OrElse Checking Then Return
        If Not Entries.Any(Function(Item) Not Item.IsDirectory) Then Return
        CancelOnline()
        Dim Cancellation As New CancellationTokenSource()
        OnlineCancellation = Cancellation
        Dim Revision = OnlineRevision, Target = Owner, Root = CurrentRoot, Folder = CurrentFolder, Items = Entries.ToList(), Version = Owner.Version.VanillaName
        Checking = True
        BtnFilterUpdates.ToolTip = "正在检查资源更新……"
        UpdateButtons()
        RunInNewThread(Sub()
                           Dim Result As List(Of InstanceResourceOnlineInfo) = Nothing, Failure As Exception = Nothing
                           Try
                               Result = InstanceResourceUpdates.Check(Root, Items, Kind, Version, Cancellation.Token)
                           Catch ex As Exception
                               Failure = ex
                           End Try
                           RunInUi(Sub()
                                       If Revision <> OnlineRevision OrElse Owner IsNot Target OrElse Not PathUtils.IsSame(CurrentFolder, Folder) Then
                                           Cancellation.Dispose()
                                           Return
                                       End If
                                       OnlineCancellation = Nothing
                                       Checking = False
                                       Cancellation.Dispose()
                                       If Failure Is Nothing Then
                                           ApplyOnlineInfo(Result)
                                           BtnFilterUpdates.ToolTip = $"已找到 {Result.Where(Function(Item) Item.HasUpdate).Count()} 个兼容更新。"
                                       ElseIf TypeOf Failure Is OperationCanceledException Then
                                           SetStatus("已取消检查更新。")
                                       Else
                                           SetStatus("检查资源更新失败，可通过左侧刷新入口重试。")
                                           Logger.Warn(Failure, "检查资源更新失败")
                                       End If
                                       UpdateButtons()
                                   End Sub)
                       End Sub, "Resource Update Check")
    End Sub
    Friend Sub ApplyOnlineInfo(Infos As IEnumerable(Of InstanceResourceOnlineInfo))
        For Each Info In Infos
            Dim Current = Entries.FirstOrDefault(Function(Item) PathUtils.IsSame(Item.FullPath, Info.Entry.FullPath))
            If Current IsNot Nothing AndAlso Current.ModifiedUtc = Info.Entry.ModifiedUtc AndAlso Current.Length = Info.Entry.Length Then Online(Current.FullPath) = Info
        Next
        RenderList()
    End Sub
    Private Sub SetStatus(Text As String)
        LabOperation.Text = Text
        LabOperation.Visibility = If(String.IsNullOrEmpty(Text), Visibility.Collapsed, Visibility.Visible)
        UpdateButtons()
    End Sub
    Private Sub DownloadNew() Handles BtnDownload.Click, BtnHintDownload.Click
        If Busy OrElse Owner Is Nothing Then Return
        If NeedsSchematicLoader Then
            FrmMain.PageChange(FormMain.PageType.InstanceSetup, FormMain.PageSubType.InstanceModify)
            Return
        End If
        If ModModpackMode.Enabled OrElse (Not InstanceResourceUpdates.Supported(Kind) AndAlso Kind <> InstanceResourceKind.Schematics) Then Return
        PageResource.TargetInstance = Owner
        PageResource.TargetName = If(Kind = InstanceResourceKind.Schematics, "Litematica", SearchBox.Text)
        Dim TargetPage = If(Kind = InstanceResourceKind.Schematics, FormMain.PageSubType.DownloadMod,
            If(Kind = InstanceResourceKind.ResourcePacks, FormMain.PageSubType.DownloadResourcePack, FormMain.PageSubType.DownloadShader))
        FrmMain.PageChange(FormMain.PageType.Download, TargetPage)
    End Sub
    Private Sub UpdateSelected() Handles BtnUpdate.Click
        UpdateResources(Selection())
    End Sub
    Private Sub UpdateResources(Resources As IEnumerable(Of InstanceResourceEntry))
        If Not CanChange() Then Return
        If HasRunningMinecraft Then
            Hint("请先关闭游戏，再更新资源包或光影包。", HintType.Red)
            Return
        End If
        Dim Infos = Resources.Where(Function(Item) HasUpdate(Item)).Select(Function(Item) Online(Item.FullPath)).ToList()
        If Infos.Count = 0 Then Return
        If MyMsgBox($"将更新 {Infos.Count} 个{InstanceResourceStore.Title(Kind)}。{vbCrLf}新版本可能改变游戏画面或与整合包不兼容，请确认后继续。旧文件会保留在本地回收区，可通过恢复按钮找回。", "资源更新", "更新", "取消", IsWarn:=True) <> 1 Then Return
        Dim Root = CurrentRoot, Recycle = RecycleRoot, Version = Owner.Version.VanillaName
        RunOperation("更新资源", Sub(Token) InstanceResourceUpdates.Install(Root, Infos, Recycle, Kind, Version, Token))
    End Sub

    Private Function CanChange(Optional Exporting As Boolean = False) As Boolean
        If Busy OrElse Owner Is Nothing OrElse CurrentRoot Is Nothing Then Return False
        If McInstanceLock.IsLocked(Owner) Then
            Hint("该版本正在被修改，请稍后再管理资源！", HintType.Red)
            Return False
        End If
        If HasRunningMinecraft AndAlso Kind = InstanceResourceKind.Saves Then
            Hint("请先关闭游戏，再修改或导出存档，避免存档数据不完整。", HintType.Red)
            Return False
        End If
        Return True
    End Function
    Private Function ConfirmImport() As Boolean
        Return Not ModModpackMode.Enabled OrElse MyMsgBox("更改整合包的本地资源可能影响整合包运行。是否继续导入？", "整合包资源管理", "继续", "取消", IsWarn:=True) = 1
    End Function
    Private Sub RunOperation(Name As String, Work As Action(Of CancellationToken))
        CancelOnline()
        Busy = True
        Operation = New CancellationTokenSource()
        Dim Token = Operation.Token
        Dim Target = Owner
        SetStatus("正在" & Name & "……")
        UpdateButtons()
        Dim Worker As New Thread(
            Sub()
                Dim Failure As Exception = Nothing
                Try
                    Work(Token)
                Catch ex As Exception
                    Failure = ex
                End Try
                RunInUi(Sub()
                            Busy = False
                            Operation.Dispose()
                            Operation = Nothing
                            UpdateButtons()
                            If Failure Is Nothing Then
                                SetStatus(Name & "完成。")
                            ElseIf TypeOf Failure Is OperationCanceledException Then
                                SetStatus("操作已取消，已完成的内容会保留。")
                            Else
                                SetStatus(Failure.Message)
                                Logger.Error(Failure, Name & "失败", LogBehavior.Toast)
                            End If
                            If Owner Is Target AndAlso IsLoaded Then RefreshResources()
                        End Sub)
            End Sub) With {.Name = "Resource " & Name, .IsBackground = True}
        Worker.Start()
    End Sub

    Private Sub OpenFolder() Handles BtnOpenFolder.Click, BtnHintOpen.Click
        If CurrentRoot Is Nothing Then Return
        Try
            InstanceResourceStore.EnsureWithin(CurrentRoot, CurrentFolder, True)
            DirectoryUtils.Create(CurrentFolder)
            OpenExplorer(CurrentFolder)
        Catch ex As Exception
            Logger.Error(ex, "打开资源文件夹失败", LogBehavior.Toast)
        End Try
    End Sub
    Private Sub OpenEntry(Entry As InstanceResourceEntry)
        Try
            InstanceResourceStore.ValidateEntry(CurrentRoot, Entry)
            OpenExplorer(Entry.FullPath)
        Catch ex As Exception
            Logger.Error(ex, "打开资源位置失败", LogBehavior.Toast)
        End Try
    End Sub
    Private Sub RetryLoad() Handles Load.Click
        If ResourceLoader.State = LoadState.Failed Then RefreshResources()
    End Sub
    Private Sub UpClick() Handles BtnUp.Click
        If CurrentRoot Is Nothing OrElse PathUtils.IsSame(CurrentRoot, CurrentFolder) Then Return
        CurrentFolder = Path.GetDirectoryName(CurrentFolder.TrimEnd("\"c))
        SelectedPaths.Clear()
        Filter = 0
        RefreshResources()
    End Sub
    Private Sub ImportFiles() Handles BtnImportFile.Click, BtnHintInstall.Click
        If Not CanChange() OrElse Not ConfirmImport() Then Return
        Dim Extensions As String()
        Select Case Kind
            Case InstanceResourceKind.Saves, InstanceResourceKind.ResourcePacks, InstanceResourceKind.ShaderPacks
                Extensions = {"zip"}
            Case InstanceResourceKind.Screenshots
                Extensions = {"png", "jpg", "jpeg", "bmp"}
            Case Else
                Extensions = {"litematic", "schem", "schematic", "nbt"}
        End Select
        Dim Files = Dialogs.SelectFile("导入" & InstanceResourceStore.Title(Kind), Kind <> InstanceResourceKind.Saves, filter:={(Extensions, "资源文件")})
        If Not Files.Any() Then Return
        Dim Name As String = Nothing
        If Kind = InstanceResourceKind.Saves Then
            Name = MyMsgBoxInput("导入存档", "请输入导入后的存档文件夹名称。", Path.GetFileNameWithoutExtension(Files(0)), New ObjectModel.Collection(Of Validate) From {New ValidateFolderName(CurrentRoot)})
            If String.IsNullOrWhiteSpace(Name) Then Return
        End If
        Dim Root = CurrentRoot, Folder = CurrentFolder
        RunOperation("导入资源", Sub(Token)
                                  For Each FileName In Files
                                      If Kind = InstanceResourceKind.Saves Then InstanceResourceStore.ImportWorld(Root, FileName, Name, Token) Else InstanceResourceStore.ImportFile(Root, Folder, Kind, FileName, Token)
                                  Next
                              End Sub)
    End Sub
    Private Sub ResourceDragOver(Sender As Object, e As DragEventArgs) Handles Me.PreviewDragOver
        If Not e.Data.GetDataPresent(DataFormats.FileDrop) Then Return
        e.Effects = If(Busy OrElse Owner Is Nothing, DragDropEffects.None, DragDropEffects.Copy)
        e.Handled = True
    End Sub
    Private Sub ResourceDrop(Sender As Object, e As DragEventArgs) Handles Me.PreviewDrop
        If Not e.Data.GetDataPresent(DataFormats.FileDrop) Then Return
        e.Handled = True
        If Not CanChange() OrElse Not ConfirmImport() Then Return
        Dim Sources = TryCast(e.Data.GetData(DataFormats.FileDrop), String())
        If Sources Is Nothing OrElse Sources.Length = 0 Then Return
        Dim Root = CurrentRoot, Folder = CurrentFolder
        RunOperation("安装资源", Sub(Token)
                                  For Each Source In Sources.Distinct(StringComparer.OrdinalIgnoreCase)
                                      Token.ThrowIfCancellationRequested()
                                      If Directory.Exists(Source) Then
                                          InstanceResourceStore.ImportFolder(Root, Folder, Kind, Source, New DirectoryInfo(Source).Name, Token)
                                      ElseIf Kind = InstanceResourceKind.Saves Then
                                          InstanceResourceStore.ImportWorld(Root, Source, Path.GetFileNameWithoutExtension(Source), Token)
                                      Else
                                          InstanceResourceStore.ImportFile(Root, Folder, Kind, Source, Token)
                                      End If
                                  Next
                              End Sub)
    End Sub
    Private Sub ExportSelected() Handles BtnExport.Click
        Dim Items = Selection(), Root = CurrentRoot
        If Items.Count = 0 OrElse Not CanChange(True) Then Return
        If Items.Count = 1 Then
            Dim Entry = Items(0)
            Dim Extension = If(Entry.IsDirectory, "zip", Path.GetExtension(Entry.Name).TrimStart("."c))
            Dim Destination = Dialogs.SaveFile("导出" & InstanceResourceStore.Title(Kind), If(Entry.IsDirectory, Entry.Name & ".zip", Entry.Name), filter:={(Extension, "资源文件")})
            If String.IsNullOrEmpty(Destination) Then Return
            RunOperation("导出资源", Sub(Token) InstanceResourceStore.Export(Root, Entry, Destination, Token))
        Else
            Dim Folder = Dialogs.SelectFolder("选择批量导出的目标文件夹", False).FirstOrDefault()
            If String.IsNullOrWhiteSpace(Folder) Then Return
            RunOperation("批量导出资源", Sub(Token) InstanceResourceStore.ExportMany(Root, Items, Folder, Token))
        End If
    End Sub
    Private Sub RenameEntry(Entry As InstanceResourceEntry)
        If Not CanChange() Then Return
        Dim Root = CurrentRoot
        Dim Name = MyMsgBoxInput("重命名资源", If(Kind = InstanceResourceKind.Saves, "只修改存档文件夹名称，不修改游戏内的世界名称。", "请输入新的文件或文件夹名称。") & If(ModModpackMode.Enabled, vbCrLf & "更改整合包资源可能影响运行，请确认后继续。", ""), Entry.Name,
            New ObjectModel.Collection(Of Validate) From {New ValidateNullOrWhiteSpace, New ValidateLength(1, 120)})
        If String.IsNullOrWhiteSpace(Name) OrElse Name = Entry.Name Then Return
        RunOperation("重命名资源", Sub(Token) InstanceResourceStore.Rename(Root, Entry, Name))
    End Sub
    Private Sub DeleteSelected() Handles BtnDelete.Click
        Dim BackupIds = SelectedBackupIds()
        If BackupIds.Count > 0 Then
            WorldBackupsUi.Start(Owner, DeleteIds:=BackupIds)
            Return
        End If
        DeleteResources(Selection())
    End Sub
    Private Sub DeleteResources(Resources As IEnumerable(Of InstanceResourceEntry))
        If Not CanChange() Then Return
        Dim Items = Resources.ToList(), Root = CurrentRoot, Recycle = RecycleRoot
        If Items.Count = 0 Then Return
        Dim Names = String.Join(vbCrLf, Items.Take(6).Select(Function(Item) " · " & Item.Name))
        If MyMsgBox($"确定删除以下 {Items.Count} 个资源吗？{vbCrLf}{Names}{If(Items.Count > 6, vbCrLf & "……", "")}{vbCrLf}文件夹内的内容也会被移走。资源会移入 PCL Living Fork 本地回收区，可通过上方恢复按钮找回。", "删除资源", "删除", "取消", IsWarn:=True) <> 1 Then Return
        RunOperation("删除资源", Sub(Token) InstanceResourceStore.RecycleMany(Root, Items, Recycle, Token))
    End Sub
    Private Sub RestoreDeleted() Handles BtnRestore.Click, BtnHintRestore.Click
        If Not CanChange() Then Return
        Dim Root = CurrentRoot, Recycle = RecycleRoot
        RunOperation("恢复删除", Sub(Token) InstanceResourceStore.RestoreLast(Root, Recycle))
    End Sub
    Private Sub CancelClick() Handles BtnCancel.Click
        Operation?.Cancel()
        If Checking Then
            CancelOnline()
            SetStatus("已取消检查更新。")
            UpdateButtons()
        End If
    End Sub
End Class
