Imports System.IO

Partial Public Class PageInstanceResources
    Private WorldBackupPoints As New List(Of JObject)
    Private ReadOnly WorldHosts As New Dictionary(Of String, StackPanel)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly BackupGroups As New Dictionary(Of String, WorldBackupGroup)(StringComparer.OrdinalIgnoreCase)
    Private BackupSelectionChanging As Boolean
    Private OrphanBackupCard As MyCard
    Private OrphanBackupPanel As StackPanel

    Private Function VisibleResourceRows() As IEnumerable(Of MyLocalModItem)
        If Kind = InstanceResourceKind.Saves Then
            Return PanWorlds.Children.OfType(Of StackPanel)().SelectMany(Function(Host) Host.Children.OfType(Of MyLocalModItem)())
        End If
        Return PanList.Children.OfType(Of MyLocalModItem)()
    End Function
    Private Function SelectedBackupIds() As List(Of String)
        If Kind <> InstanceResourceKind.Saves Then Return New List(Of String)
        Return BackupGroups.Values.SelectMany(Function(Group) Group.SelectedIds).Distinct(StringComparer.Ordinal).ToList()
    End Function
    Private Sub ClearBackupSelection()
        BackupSelectionChanging = True
        Try
            For Each Group In BackupGroups.Values
                Group.ClearSelection(False)
            Next
        Finally
            BackupSelectionChanging = False
        End Try
    End Sub
    Private Sub BackupSelectionChanged(Sender As WorldBackupGroup)
        If Rendering OrElse BackupSelectionChanging OrElse Busy Then Return
        Rendering = True
        Try
            SelectedPaths.Clear()
            For Each Row In Rows.Values
                Row.Checked = False
            Next
        Finally
            Rendering = False
        End Try
        SelectResource(Nothing)
    End Sub
    Private Function BackupGroup(Name As String) As WorldBackupGroup
        Dim Group As WorldBackupGroup = Nothing
        If Not BackupGroups.TryGetValue(Name, Group) Then
            Group = New WorldBackupGroup With {.SelectionBar = CardSelect, .Margin = New Thickness(39, 5, 0, 10)}
            Group.BeforeCreate = Function() BackupSettings.CommitPending()
            AddHandler Group.SelectionChanged, AddressOf BackupSelectionChanged
            BackupGroups.Add(Name, Group)
        End If
        Group.Bind(Owner, Name, WorldBackupPoints.Where(Function(Point) String.Equals(Point("world")?.ToString(), Name, StringComparison.OrdinalIgnoreCase)).ToList())
        Return Group
    End Function
    Private Shared Sub MoveInto(Host As Panel, Element As FrameworkElement)
        If Element.Parent Is Host Then Return
        Dim Previous = TryCast(Element.Parent, Panel)
        If Previous IsNot Nothing Then Previous.Children.Remove(Element)
        Host.Children.Add(Element)
    End Sub
    Private Sub ResetWorldGroups()
        ClearBackupSelection()
        PanWorlds.Children.Clear()
        WorldHosts.Clear()
        Rows.Clear()
        BackupGroups.Clear()
        OrphanBackupPanel = Nothing
        OrphanBackupCard = Nothing
    End Sub
    Private Sub RenderWorldGroups(Visible As List(Of InstanceResourceEntry), Query As String)
        Dim Desired As New List(Of UIElement), VisibleGroups As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each Entry In Visible
            Dim Row As MyLocalModItem = Nothing
            If Not Rows.TryGetValue(Entry.FullPath, Row) Then
                Row = CreateResourceRow()
                Rows.Add(Entry.FullPath, Row)
            End If
            Row.BindResource(Entry, Nothing)
            Row.Checked = SelectedPaths.Contains(Entry.FullPath)
            Dim Host As StackPanel = Nothing
            If Not WorldHosts.TryGetValue(Entry.FullPath, Host) Then
                Host = New StackPanel
                WorldHosts.Add(Entry.FullPath, Host)
            End If
            MoveInto(Host, Row)
            If Owner IsNot Nothing Then
                MoveInto(Host, BackupGroup(Entry.Name))
                VisibleGroups.Add(Entry.Name)
            End If
            Desired.Add(Host)
        Next
        '原世界已移走或删除时，仍保留其备份和恢复入口。
        Dim Names = WorldBackupPoints.Select(Function(Point) Point("world")?.ToString()).Where(Function(Name) Not String.IsNullOrWhiteSpace(Name)).Distinct(StringComparer.OrdinalIgnoreCase).
            Where(Function(Name) Not Entries.Any(Function(Entry) String.Equals(Entry.Name, Name, StringComparison.OrdinalIgnoreCase)) AndAlso
                (Query = "" OrElse Name.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0)).OrderBy(Function(Name) Name).ToList()
        If Names.Count > 0 AndAlso Owner IsNot Nothing Then
            If OrphanBackupCard Is Nothing Then
                OrphanBackupPanel = New StackPanel With {.Margin = New Thickness(15, 40, 15, 8)}
                OrphanBackupCard = New MyCard With {.Margin = New Thickness(0, 10, 0, 10), .SwapControl = OrphanBackupPanel, .IsSwapped = True}
                OrphanBackupCard.Children.Add(OrphanBackupPanel)
            End If
            OrphanBackupCard.Title = $"仅有备份的存档（{Names.Count}）"
            Dim Header = OrphanBackupCard.MainTextBlock
            For Each Old In OrphanBackupPanel.Children.OfType(Of WorldBackupGroup)().Where(Function(Group) Not Names.Contains(Group.FolderName, StringComparer.OrdinalIgnoreCase)).ToList()
                OrphanBackupPanel.Children.Remove(Old)
            Next
            For Each WorldName As String In Names
                Dim Group = BackupGroup(WorldName)
                Dim Card = DirectCast(Group.FindName("CardBackups"), MyCard)
                Card.Title = WorldName & " · " & Card.Title
                MoveInto(OrphanBackupPanel, Group)
                VisibleGroups.Add(WorldName)
            Next
            Desired.Add(OrphanBackupCard)
            LabEmpty.Visibility = Visibility.Collapsed
        End If
        For Each Element In PanWorlds.Children.OfType(Of UIElement)().Where(Function(Item) Not Desired.Contains(Item)).ToList()
            PanWorlds.Children.Remove(Element)
        Next
        For Index = 0 To Desired.Count - 1
            Dim Element = Desired(Index)
            If PanWorlds.Children.IndexOf(Element) = Index Then Continue For
            PanWorlds.Children.Remove(Element)
            PanWorlds.Children.Insert(Index, Element)
        Next
        For Each Group In BackupGroups.Values
            If Not VisibleGroups.Contains(Group.FolderName) Then Group.ClearSelection(False)
        Next
        For Each Key In WorldHosts.Keys.Where(Function(Value) Not Entries.Any(Function(Entry) PathUtils.IsSame(Entry.FullPath, Value))).ToList()
            WorldHosts.Remove(Key)
            Rows.Remove(Key)
        Next
    End Sub
    Private Sub RefreshBackupActions()
        Dim Ids = SelectedBackupIds(), IsBackup = Ids.Count > 0
        BtnBackupAll.IsEnabled = Not Busy AndAlso Not WorldBackups.IsBusy AndAlso Owner IsNot Nothing AndAlso Entries.Count > 0
        BtnBackupRestore.Visibility = If(IsBackup, Visibility.Visible, Visibility.Collapsed)
        BtnBackupRestore.IsEnabled = Not Busy AndAlso Not WorldBackups.IsBusy AndAlso Ids.Count = 1
        BtnExport.Visibility = If(IsBackup, Visibility.Collapsed, Visibility.Visible)
        If IsBackup Then
            BtnUpdate.Visibility = Visibility.Collapsed
            BtnDelete.IsEnabled = Not Busy AndAlso Not WorldBackups.IsBusy
        End If
        Dim Count = If(IsBackup, Ids.Count, SelectedPaths.Count)
        LabSelect.Text = If(IsBackup, $"已选择 {Count} 份存档备份", $"已选择 {Count} 个资源")
        CardList.Margin = New Thickness(0, 0, 0, If(Count > 0, 95, 15))
        LocalResourceUi.RefreshSelectionBar(CardSelect, TransSelect, Count, BottomBarShownCount, "Resource Sidebar " & PageUuid, IsLoaded AndAlso AniControlEnabled = 0)
    End Sub
    Private Sub BackupAll() Handles BtnBackupAll.Click
        If Owner IsNot Nothing AndAlso BackupSettings.CommitPending() Then WorldBackupsUi.Start(Owner)
    End Sub
    Private Sub RestoreWorldBackup() Handles BtnBackupRestore.Click
        Dim Ids = SelectedBackupIds()
        If Ids.Count = 1 Then WorldBackupsUi.Start(Owner, RestoreId:=Ids.Single())
    End Sub
End Class
