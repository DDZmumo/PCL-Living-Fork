Imports System.IO

Friend NotInheritable Class InstanceCloneGroupNode
    Public VersionFolder As String
    Public DeclaredSourceFolder As String
    Public ParentFolder As String
    Public Children As New List(Of InstanceCloneGroupNode)
End Class

Friend NotInheritable Class InstanceCloneGroupSet
    Public Roots As New List(Of InstanceCloneGroupNode)
    Public ByPath As New Dictionary(Of String, InstanceCloneGroupNode)(StringComparer.OrdinalIgnoreCase)
    Friend Function Find(VersionFolder As String) As InstanceCloneGroupNode
        Dim Result As InstanceCloneGroupNode = Nothing
        ByPath.TryGetValue(InstanceCloneGroups.Key(VersionFolder), Result)
        Return Result
    End Function
End Class

'只为显示解析来源关系；迁移仍必须通过 InstanceCloner 自身的完整校验。
Friend NotInheritable Class InstanceCloneGroups
    Private Const MaximumOriginBytes As Long = 64 * 1024
    Private Const MaximumDepth As Integer = 32

    Friend Shared Function Key(VersionFolder As String) As String
        If String.IsNullOrWhiteSpace(VersionFolder) OrElse Not Path.IsPathRooted(VersionFolder) Then Throw New IOException("版本显示路径必须是完整路径。")
        Dim Full = Path.GetFullPath(PathUtils.RemoveExtendedPrefix(VersionFolder))
        If Full.Length > Path.GetPathRoot(Full).Length Then Full = Full.TrimEnd("\"c, "/"c)
        Return Full
    End Function
    Private Shared Function ReadSource(VersionFolder As String, Token As CancellationToken) As String
        Token.ThrowIfCancellationRequested()
        Try
            Dim Versions = Path.GetDirectoryName(VersionFolder)
            If Not String.Equals(Path.GetFileName(Versions), "versions", StringComparison.OrdinalIgnoreCase) Then Return Nothing
            InstanceCloner.EnsureNoLinks(VersionFolder)
            Dim Marker = Path.Combine(VersionFolder, "PCL", "CloneOrigin.json")
            InstanceCloner.EnsureNoLinks(Marker)
            If Not File.Exists(Marker) Then Return Nothing
            Dim Data As JObject
            Using Input As New FileStream(Marker, FileMode.Open, FileAccess.Read, FileShare.Read)
                If Input.Length > MaximumOriginBytes Then Return Nothing
                Token.ThrowIfCancellationRequested()
                Using Reader As New StreamReader(Input, System.Text.Encoding.UTF8, True)
                    Data = JObject.Parse(Reader.ReadToEnd())
                End Using
            End Using
            If Data.Value(Of Integer?)("schema").GetValueOrDefault() <> 1 Then Return Nothing
            Dim RawSource = Data("sourceVersionFolder")?.ToString()
            If String.IsNullOrWhiteSpace(RawSource) OrElse Not Path.IsPathRooted(RawSource) Then Return Nothing
            Dim Source = Key(RawSource)
            If String.Equals(Source, VersionFolder, StringComparison.OrdinalIgnoreCase) OrElse Not String.Equals(Path.GetDirectoryName(Source), Versions, StringComparison.OrdinalIgnoreCase) Then Return Nothing
            InstanceCloner.EnsureNoLinks(Source)
            Dim CloneName = Data("cloneName")?.ToString(), SourceName = Data("sourceName")?.ToString()
            If CloneName IsNot Nothing AndAlso Not String.Equals(CloneName, Path.GetFileName(VersionFolder), StringComparison.OrdinalIgnoreCase) Then Return Nothing
            If SourceName IsNot Nothing AndAlso Not String.Equals(SourceName, Path.GetFileName(Source), StringComparison.OrdinalIgnoreCase) Then Return Nothing
            Dim RawGameRoot = Data("sourceGameRoot")?.ToString()
            If RawGameRoot IsNot Nothing Then
                Dim GameRoot = Key(RawGameRoot), McRoot = Path.GetDirectoryName(Versions)
                If Not String.Equals(GameRoot, Source, StringComparison.OrdinalIgnoreCase) AndAlso Not String.Equals(GameRoot, McRoot, StringComparison.OrdinalIgnoreCase) Then Return Nothing
                InstanceCloner.EnsureNoLinks(GameRoot)
            End If
            Return Source
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is Newtonsoft.Json.JsonException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is InvalidCastException OrElse TypeOf ex Is OverflowException OrElse TypeOf ex Is FormatException OrElse TypeOf ex Is System.Security.SecurityException
            '坏记录和不可读取的记录只取消分组，不使该版本从选择页消失。
            Return Nothing
        End Try
    End Function
    Friend Shared Function Build(VisibleFolders As IEnumerable(Of String), Token As CancellationToken) As InstanceCloneGroupSet
        If VisibleFolders Is Nothing Then Throw New ArgumentNullException(NameOf(VisibleFolders))
        Dim Result As New InstanceCloneGroupSet, Order As New List(Of InstanceCloneGroupNode)
        For Each Folder In VisibleFolders
            Token.ThrowIfCancellationRequested()
            Dim Address = Key(Folder)
            If Result.ByPath.ContainsKey(Address) Then Continue For
            Dim Node As New InstanceCloneGroupNode With {.VersionFolder = Address}
            Result.ByPath.Add(Address, Node)
            Order.Add(Node)
        Next
        Dim Parents As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        For Each Node In Order
            Token.ThrowIfCancellationRequested()
            Node.DeclaredSourceFolder = ReadSource(Node.VersionFolder, Token)
            If Node.DeclaredSourceFolder IsNot Nothing AndAlso Result.ByPath.ContainsKey(Node.DeclaredSourceFolder) Then Parents.Add(Node.VersionFolder, Node.DeclaredSourceFolder)
        Next
        Dim Invalid As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each Node In Order
            Token.ThrowIfCancellationRequested()
            Dim Seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase), Current = Node.VersionFolder
            While Parents.ContainsKey(Current)
                Token.ThrowIfCancellationRequested()
                If Not Seen.Add(Current) OrElse Seen.Count > MaximumDepth OrElse Invalid.Contains(Current) Then
                    Invalid.UnionWith(Seen)
                    Exit While
                End If
                Current = Parents(Current)
            End While
        Next
        For Each Node In Order
            Token.ThrowIfCancellationRequested()
            Dim Parent As String = Nothing
            If Not Invalid.Contains(Node.VersionFolder) AndAlso Parents.TryGetValue(Node.VersionFolder, Parent) AndAlso Not Invalid.Contains(Parent) Then
                Node.ParentFolder = Parent
                Result.ByPath(Parent).Children.Add(Node)
            Else
                Result.Roots.Add(Node)
            End If
        Next
        Return Result
    End Function
    Friend Shared Function Flatten(Node As InstanceCloneGroupNode) As List(Of InstanceCloneGroupNode)
        Dim Result As New List(Of InstanceCloneGroupNode), Pending As New Stack(Of InstanceCloneGroupNode)
        Pending.Push(Node)
        While Pending.Count > 0
            Dim Current = Pending.Pop()
            Result.Add(Current)
            For Each Child In Current.Children.AsEnumerable().Reverse()
                Pending.Push(Child)
            Next
        End While
        Return Result
    End Function
    Friend Shared Function Contains(Node As InstanceCloneGroupNode, VersionFolder As String) As Boolean
        If String.IsNullOrWhiteSpace(VersionFolder) Then Return False
        Dim Address = Key(VersionFolder)
        Return Flatten(Node).Any(Function(Item) String.Equals(Item.VersionFolder, Address, StringComparison.OrdinalIgnoreCase))
    End Function
End Class
