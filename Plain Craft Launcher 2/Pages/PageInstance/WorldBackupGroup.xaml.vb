Imports System.Collections.ObjectModel
Imports System.Text.RegularExpressions

'嵌入存档行下方；只显示父页传入的记录，选择由父页的统一操作栏处理。
Public Class WorldBackupGroup
    Private Shared ReadOnly ExpandedStates As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)
    Private Owner As McInstance
    Private Identifier As String
    Private StateKey As String
    Private Entries As New List(Of JObject)
    Private ReadOnly Selection As New HashSet(Of String)(StringComparer.Ordinal)
    Private Rendering As Boolean
    Private SharedBar As FrameworkElement

    Friend Event SelectionChanged(Sender As WorldBackupGroup)
    Friend Property BeforeCreate As Func(Of Boolean)
    Friend ReadOnly Property SelectedIds As IReadOnlyCollection(Of String)
        Get
            Return New ReadOnlyCollection(Of String)(Selection.ToList())
        End Get
    End Property
    Friend ReadOnly Property FolderName As String
        Get
            Return Identifier
        End Get
    End Property
    Friend ReadOnly Property IsExpanded As Boolean
        Get
            Return Not CardBackups.IsSwapped
        End Get
    End Property
    Friend Property SelectionBar As FrameworkElement
        Get
            Return SharedBar
        End Get
        Set(Value As FrameworkElement)
            SharedBar = Value
            For Each Row As MyLocalModItem In PanEntries.Children
                Row.SelectionBar = Value
            Next
        End Set
    End Property
    Private Sub Init() Handles Me.Initialized
        CardBackups.SwapControl = PanContent
        BtnCreate.Logo = Logo.IconButtonSave
    End Sub
    Friend Sub Bind(Value As McInstance, WorldFolderName As String, Points As List(Of JObject))
        If Value Is Nothing Then Throw New ArgumentNullException(NameOf(Value))
        If String.IsNullOrWhiteSpace(WorldFolderName) OrElse IO.Path.GetFileName(WorldFolderName) <> WorldFolderName OrElse WorldFolderName = "." OrElse WorldFolderName = ".." OrElse WorldFolderName.IndexOfAny(IO.Path.GetInvalidFileNameChars()) >= 0 Then Throw New ArgumentException("存档文件夹名称无效。", NameOf(WorldFolderName))
        Dim GameFolder = Value.PathIndie
        Dim Key = IO.Path.GetFullPath(PathUtils.RemoveExtendedPrefix(GameFolder)).TrimEnd("\"c, "/"c) & ChrW(0) & WorldFolderName
        Dim PreviousSelection = Selection.ToList()
        If Not String.Equals(StateKey, Key, StringComparison.OrdinalIgnoreCase) Then Selection.Clear()
        Owner = Value
        Identifier = WorldFolderName
        StateKey = Key
        '使用实体文件夹标识归组，两个世界的显示名称相同也不会混入记录。
        Entries = If(Points, New List(Of JObject)).Where(Function(Point) Point IsNot Nothing AndAlso
            String.Equals(Point("world")?.ToString(), Identifier, StringComparison.OrdinalIgnoreCase) AndAlso
            Regex.IsMatch(If(Point("id")?.ToString(), ""), "\A[a-f0-9]{32}\z")).
            GroupBy(Function(Point) Point("id").ToString(), StringComparer.Ordinal).
            Select(Function(Batch) Batch.First()).OrderByDescending(Function(Point) Point("created").ToObject(Of Date)()).ToList()
        Selection.IntersectWith(Entries.Select(Function(Point) Point("id").ToString()))
        Dim Available As Boolean
        Try
            QuickPlayWorlds.WorldPath(GameFolder, Identifier)
            Available = True
        Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException
            Available = False
        End Try
        BtnCreate.IsEnabled = Available
        BtnCreate.ToolTip = If(Available, "立即备份此存档；备份完成并校验后才会清理它的旧备份。", "原存档已不存在，仍可恢复已有备份。")
        LabState.Text = If(Available, "按备份时间从新到旧排列。", "原存档已不存在，可从以下备份恢复。")
        LabEmpty.Text = If(Available, "暂无备份，可先备份此存档。", "此存档当前没有可用备份。")
        LabEmpty.Visibility = If(Entries.Count = 0, Visibility.Visible, Visibility.Collapsed)
        CardBackups.Title = "备份（" & Entries.Count & "）"
        Rendering = True
        PanEntries.Children.Clear()
        Rendering = False
        CardBackups.SetContentBuilder(Sub(Panel) BuildRows())
        Dim Expanded As Boolean
        ExpandedStates.TryGetValue(StateKey, Expanded)
        CardBackups.IsSwapped = Not Expanded
        If Expanded Then CardBackups.BuildContent()
        If Not New HashSet(Of String)(PreviousSelection, StringComparer.Ordinal).SetEquals(Selection) Then RaiseEvent SelectionChanged(Me)
    End Sub
    Private Sub BuildRows()
        Rendering = True
        Try
            PanEntries.Children.Clear()
            For Each Point In Entries
                Dim Row As New MyLocalModItem With {.SelectionBar = SharedBar}
                Row.BindRestorePoint(Point)
                Row.Checked = Selection.Contains(Point("id").ToString())
                Row.Buttons = BackupButtons(Point)
                AddHandler Row.Click, Sub() Row.Checked = Not Row.Checked
                AddHandler Row.Changed, AddressOf RowSelectionChanged
                PanEntries.Children.Add(Row)
            Next
        Finally
            Rendering = False
        End Try
    End Sub
    Private Sub RememberExpansion() Handles CardBackups.Swap
        If StateKey IsNot Nothing Then ExpandedStates(StateKey) = Not CardBackups.IsSwapped
    End Sub
    Private Sub RowSelectionChanged(Sender As Object, e As RouteEventArgs)
        If Rendering Then Return
        Dim Row = DirectCast(Sender, MyLocalModItem)
        Dim Id = Row.RestorePoint("id").ToString()
        If Row.Checked Then Selection.Add(Id) Else Selection.Remove(Id)
        RaiseEvent SelectionChanged(Me)
    End Sub
    Friend Sub ClearSelection(Optional Notify As Boolean = True)
        Dim HadSelection = Selection.Count > 0
        Rendering = True
        Try
            Selection.Clear()
            For Each Row As MyLocalModItem In PanEntries.Children
                Row.Checked = False
            Next
        Finally
            Rendering = False
        End Try
        If Notify AndAlso HadSelection Then RaiseEvent SelectionChanged(Me)
    End Sub
    Private Function BackupButtons(Point As JObject) As List(Of MyIconButton)
        Dim Restore As New MyIconButton With {.Logo = Logo.IconButtonRefresh, .LogoScale = 0.85, .ToolTip = "恢复此世界"}
        Dim Details As New MyIconButton With {.Logo = Logo.IconButtonInfo, .ToolTip = "备份详情"}
        Dim Open As New MyIconButton With {.Logo = Logo.IconButtonOpen, .LogoScale = 1.05, .ToolTip = "打开备份文件夹"}
        Dim Delete As New MyIconButton With {.Logo = Logo.IconButtonDelete, .ToolTip = "移除此备份"}
        AddHandler Restore.Click, Sub() WorldBackupsUi.Start(Owner, RestoreId:=Point("id").ToString())
        AddHandler Details.Click, Sub() ShowDetails(Point)
        AddHandler Open.Click, Sub() OpenBackup(Point("id").ToString())
        AddHandler Delete.Click, Sub() WorldBackupsUi.Start(Owner, DeleteIds:={Point("id").ToString()})
        Return New List(Of MyIconButton) From {Restore, Details, Open, Delete}
    End Function
    Private Sub ShowDetails(Point As JObject)
        MyMsgBox("世界：" & Point("label").ToString() & vbCrLf & "文件夹：" & Identifier & vbCrLf &
            "备份时间：" & Point("created").ToObject(Of Date)().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") & vbCrLf &
            "备份原因：" & Point("reason").ToString() & vbCrLf & "存档大小：" & StringUtils.FormatByteSize(Point("bytes").ToObject(Of Long)()) & vbCrLf &
            "压缩大小：" & StringUtils.FormatByteSize(Point("archiveBytes").ToObject(Of Long)()) & vbCrLf & "文件数量：" & DirectCast(Point("files"), JObject).Count & vbCrLf & vbCrLf &
            "恢复时会校验完整备份，并先自动保存当前世界。", "存档备份详情")
    End Sub
    Private Sub OpenBackup(Id As String)
        Try
            If Owner Is Nothing OrElse Not Regex.IsMatch(Id, "\A[a-f0-9]{32}\z") Then Return
            Dim Root = WorldBackups.Store(Owner.PathIndie), Folder = IO.Path.Combine(Root, "points", Id)
            InstanceResourceStore.EnsureWithin(Root, Folder)
            If IO.Directory.Exists(Folder) Then OpenExplorer(Folder) Else Hint("此备份已移动或删除，请刷新存档列表。")
        Catch ex As Exception
            Logger.Warn(ex, "打开存档备份目录失败")
            Hint(ex.Message, HintType.Red)
        End Try
    End Sub
    Private Sub CreateBackup() Handles BtnCreate.Click
        If BeforeCreate IsNot Nothing AndAlso Not BeforeCreate.Invoke() Then Return
        If Owner IsNot Nothing AndAlso BtnCreate.IsEnabled Then WorldBackupsUi.Start(Owner, WorldIdentifier:=Identifier)
    End Sub
End Class
