Public Class PageInstanceRestorePoints
    Implements IRefreshable
    Private ReadOnly PointsLoader As New LoaderTask(Of String, List(Of JObject))("读取还原点", Sub(Task) Task.Output = EnvironmentHistory.Points(Task.Input))
    Private Entries As New List(Of JObject)
    Private ReadOnly SelectedIds As New HashSet(Of String)(StringComparer.Ordinal)
    Private Rendering As Boolean
    Private BarCount As Integer
    Private HealthyOnly As Boolean
    Private Owner As McInstance
    Private PendingFocusId As String
    Friend Sub FocusPoint(Id As String)
        PendingFocusId = Id
        SearchBox.Text = ""
        FilterAll.Checked = True
        If Entries.Any(Function(Item) Item("id").ToString() = Id) Then
            SelectedIds.Clear()
            SelectedIds.Add(Id)
            RenderList()
        End If
    End Sub

    Private Sub Init() Handles Me.Initialized
        DisabledPageAnimControls.Add(CardSelect)
        LocalResourceUi.ApplySelectionIcons(BtnRestore, BtnDelete, BtnDeselect)
        BtnRestore.Logo = Logo.IconButtonRefresh
        FilterAll.LabText.Margin = New Thickness(-2, 0, 8, 0)
        FilterHealthy.LabText.Margin = New Thickness(-2, 0, 8, 0)
        PageLoaderInit(Load, PanLoad, PanBack, Nothing, PointsLoader, AddressOf LoadedPoints, AutoRun:=False)
    End Sub
    Private Sub PageLoaded() Handles Me.Loaded
        AddHandler EnvironmentHistoryUi.Changed, AddressOf Refresh
        PanBack.ScrollToHome()
        Refresh()
    End Sub
    Private Sub PageUnloaded() Handles Me.Unloaded
        RemoveHandler EnvironmentHistoryUi.Changed, AddressOf Refresh
        PointsLoader.Cancel()
    End Sub
    Public Sub Refresh() Implements IRefreshable.Refresh
        Owner = PageInstanceLeft.Instance
        If Owner Is Nothing Then Return
        SelectedIds.Clear()
        BtnRestore.IsEnabled = False
        PointsLoader.Start(Owner.PathVersion, IsForceRestart:=True)
    End Sub
    Private Sub LoadedPoints()
        If Owner Is Nothing OrElse Owner IsNot PageInstanceLeft.Instance Then Return
        Entries = PointsLoader.Output
        If PendingFocusId IsNot Nothing AndAlso Entries.Any(Function(Item) Item("id").ToString() = PendingFocusId) Then
            SelectedIds.Add(PendingFocusId)
            PendingFocusId = Nothing
        End If
        RenderList()
    End Sub
    Private Sub SearchChanged() Handles SearchBox.TextChanged
        RenderList()
    End Sub
    Private Sub FilterChanged(Sender As MyRadioButton, User As Boolean) Handles FilterAll.Check, FilterHealthy.Check
        HealthyOnly = Sender Is FilterHealthy
        RenderList()
    End Sub
    Friend Sub RenderList()
        If PanEntries Is Nothing Then Return
        PanEntries.Children.Clear()
        Rendering = True
        BtnRestore.IsEnabled = False
        Dim Query = SearchBox.Text.Trim()
        Dim Matching = Entries.Where(Function(Item) (Item("label").ToString() & " " & Item("description")?.ToString() & " " & CDate(Item("created")).ToLocalTime().ToString("yyyy-MM-dd HH:mm")).IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0).ToList()
        FilterAll.Text = $"全部 ({Matching.Count})"
        FilterHealthy.Text = $"正常运行 ({Matching.Where(Function(Item) CBool(Item("healthy"))).Count()})"
        Dim Visible = Matching.Where(Function(Item) Not HealthyOnly OrElse CBool(Item("healthy"))).ToList()
        SelectedIds.IntersectWith(Visible.Select(Function(Item) Item("id").ToString()))
        LabEmpty.Visibility = If(Visible.Any(), Visibility.Collapsed, Visibility.Visible)
        LabEmpty.Text = If(Entries.Any(), "没有找到符合条件的还原点。", "暂无还原点。更新前会自动保存，也可以手动创建。")
        For Each Point In Visible
            Dim Item As New MyLocalModItem With {.SelectionBar = CardSelect}
            Item.BindRestorePoint(Point)
            Item.Checked = SelectedIds.Contains(Point("id").ToString())
            Item.Buttons = PointButtons(Point)
            AddHandler Item.Click, Sub() Item.Checked = Not Item.Checked
            AddHandler Item.Changed, AddressOf SelectionChanged
            PanEntries.Children.Add(Item)
        Next
        Rendering = False
        RefreshSelection()
    End Sub
    Private Function PointButtons(Point As JObject) As List(Of MyIconButton)
        Dim Restore As New MyIconButton With {.Logo = Logo.IconButtonRefresh, .LogoScale = 0.85, .ToolTip = "恢复此还原点"}
        Dim Details As New MyIconButton With {.Logo = Logo.IconButtonInfo, .ToolTip = "详情"}
        Dim Open As New MyIconButton With {.Logo = Logo.IconButtonOpen, .LogoScale = 1.05, .ToolTip = "打开还原点文件夹"}
        Dim Delete As New MyIconButton With {.Logo = Logo.IconButtonDelete, .ToolTip = "删除此还原点"}
        AddHandler Restore.Click, Sub() EnvironmentHistoryUi.Start(Owner, True, Point("id").ToString())
        AddHandler Details.Click, Sub() MyMsgBox(Point("label").ToString() & vbCrLf & CDate(Point("created")).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") & vbCrLf &
            Point("description")?.ToString() & vbCrLf & If(CBool(Point("healthy")), "正常运行记录", "尚未标记正常运行") & vbCrLf &
            $"包含 {If(TryCast(Point("files"), JObject)?.Count, 0)} 个环境文件", "还原点详情")
        AddHandler Open.Click, Sub() OpenFolder()
        AddHandler Delete.Click, Sub() EnvironmentHistoryUi.Start(Owner, False, DeleteIds:={Point("id").ToString()})
        Return New List(Of MyIconButton) From {Restore, Details, Open, Delete}
    End Function
    Private Sub SelectionChanged(Sender As Object, e As RouteEventArgs)
        If Rendering Then Return
        Dim Row = DirectCast(Sender, MyLocalModItem)
        If Row.Checked Then SelectedIds.Add(Row.RestorePoint("id").ToString()) Else SelectedIds.Remove(Row.RestorePoint("id").ToString())
        RefreshSelection()
    End Sub
    Private Sub RefreshSelection()
        BtnRestore.IsEnabled = SelectedIds.Count = 1
        LabSelect.Text = $"已选择 {SelectedIds.Count} 个还原点"
        CardList.Margin = New Thickness(0, 0, 0, If(SelectedIds.Count > 0, 95, 14))
        LocalResourceUi.RefreshSelectionBar(CardSelect, TransSelect, SelectedIds.Count, BarCount, "Restore Point Selection " & PageUuid, IsLoaded AndAlso AniControlEnabled = 0)
    End Sub
    Friend Sub ChangeAllSelected(Value As Boolean)
        Rendering = True
        SelectedIds.Clear()
        For Each Row As MyLocalModItem In PanEntries.Children
            Row.Checked = Value
            If Value Then SelectedIds.Add(Row.RestorePoint("id").ToString())
        Next
        Rendering = False
        RefreshSelection()
    End Sub
    Private Sub SelectAll() Handles BtnSelectAll.Click
        ChangeAllSelected(SelectedIds.Count < PanEntries.Children.Count)
    End Sub
    Private Sub Deselect() Handles BtnDeselect.Click
        ChangeAllSelected(False)
    End Sub
    Private Sub SelectionKeys(Sender As Object, e As KeyEventArgs) Handles Me.PreviewKeyDown
        If e.Key = Key.Escape AndAlso SelectedIds.Count > 0 Then ChangeAllSelected(False) : e.Handled = True
        If e.Key = Key.A AndAlso Keyboard.Modifiers = ModifierKeys.Control AndAlso Not SearchBox.IsKeyboardFocusWithin Then ChangeAllSelected(True) : e.Handled = True
    End Sub
    Private Sub DeleteSelected() Handles BtnDelete.Click
        If SelectedIds.Count > 0 Then EnvironmentHistoryUi.Start(Owner, False, DeleteIds:=SelectedIds.ToArray())
    End Sub
    Private Sub CreatePoint() Handles BtnCreate.Click
        EnvironmentHistoryUi.Start(Owner, False)
    End Sub
    Private Sub RestorePoint() Handles BtnRestore.Click
        If SelectedIds.Count = 1 Then EnvironmentHistoryUi.Start(Owner, True, SelectedIds.Single())
    End Sub
    Private Sub OpenFolder() Handles BtnFolder.Click
        If Owner IsNot Nothing Then EnvironmentHistory.OpenFolder(Owner.PathVersion)
    End Sub
    Private Sub Retry() Handles Load.Click
        If PointsLoader.State = LoadState.Failed Then Refresh()
    End Sub
End Class
