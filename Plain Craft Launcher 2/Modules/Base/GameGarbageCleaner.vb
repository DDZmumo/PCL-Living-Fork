Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Security

'只处理登记游戏目录中的明确垃圾项；不使用会跟随目录链接的递归删除辅助函数。
Friend NotInheritable Class GameGarbageCleaner
    Friend NotInheritable Class CleanupResult
        Public Property DeletedFiles As Integer
        Public Property DeletedBytes As Long
        Public Property SkippedItems As Integer
    End Class

    Private Sub New()
    End Sub

    Friend Shared Function Clean(GameFolders As IEnumerable(Of String), Optional ShouldStop As Func(Of Boolean) = Nothing) As CleanupResult
        If GameFolders Is Nothing Then Throw New ArgumentNullException(NameOf(GameFolders))
        Dim Context As New CleanupContext(ShouldStop)
        Dim Seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each InputFolder As String In GameFolders
            Context.CheckCancellation()
            Dim Root As String = Nothing
            Try
                If String.IsNullOrWhiteSpace(InputFolder) OrElse Not IsAbsolutePath(InputFolder) Then Throw New ArgumentException("游戏目录必须是绝对路径。")
                Root = FullPath(InputFolder)
                If String.Equals(Root, FullPath(Path.GetPathRoot(Root)), StringComparison.OrdinalIgnoreCase) Then Throw New ArgumentException("不能清理磁盘或共享根目录。")
            Catch ex As Exception When IsPathFailure(ex)
                Context.Skip(If(InputFolder, ""))
                Continue For
            End Try
            If Not Seen.Add(Root) Then Continue For
            Dim Attributes As FileAttributes
            If Context.TryValidate(Root, Root, Attributes) AndAlso (Attributes And FileAttributes.Directory) <> 0 Then
                Context.Roots.Add(Root)
            Else
                Context.Skip(Root)
            End If
        Next
        For Each Root In Context.Roots
            CleanRoot(Root, Context)
        Next
        Return Context.Result
    End Function

    Private Shared Sub CleanRoot(Root As String, Context As CleanupContext)
        Dim Entries = Context.ReadEntries(Root, Root)
        If Entries Is Nothing Then Return
        CleanGameDirectory(Root, Root, Entries, Context)
        For Each Entry In Entries
            If Not String.Equals(Path.GetFileName(Entry), "versions", StringComparison.OrdinalIgnoreCase) Then Continue For
            Dim Attributes As FileAttributes
            If Not Context.TryValidate(Root, Entry, Attributes) Then Continue For
            If (Attributes And FileAttributes.Directory) = 0 Then Continue For
            Dim Versions = Context.ReadEntries(Root, Entry)
            If Versions Is Nothing Then Continue For
            For Each Version In Versions
                Context.CheckCancellation()
                If Not Context.TryValidate(Root, Version, Attributes) Then Continue For
                If (Attributes And FileAttributes.Directory) = 0 Then Continue For
                Dim VersionEntries = Context.ReadEntries(Root, Version)
                If VersionEntries IsNot Nothing Then CleanGameDirectory(Root, Version, VersionEntries, Context)
            Next
        Next
    End Sub

    Private Shared Sub CleanGameDirectory(Root As String, Folder As String, Entries As String(), Context As CleanupContext)
        If Not Context.GameDirectories.Add(Folder) Then Return
        Dim NativesName = Path.GetFileName(Folder) & "-natives"
        For Each Entry In Entries
            Context.CheckCancellation()
            Dim Name = Path.GetFileName(Entry)
            Dim IsGarbageDirectory = String.Equals(Name, ".mixin.out", StringComparison.OrdinalIgnoreCase) OrElse
                String.Equals(Name, "crash-reports", StringComparison.OrdinalIgnoreCase) OrElse
                String.Equals(Name, "debug", StringComparison.OrdinalIgnoreCase) OrElse
                String.Equals(Name, "logs", StringComparison.OrdinalIgnoreCase) OrElse
                String.Equals(Name, NativesName, StringComparison.OrdinalIgnoreCase) OrElse
                String.Equals(Name, "natives-windows-x86_64", StringComparison.OrdinalIgnoreCase)
            Dim IsGarbageFile = String.Equals(Path.GetExtension(Name), ".log", StringComparison.OrdinalIgnoreCase) OrElse
                String.Equals(Name, "WailaErrorOutput.txt", StringComparison.OrdinalIgnoreCase)
            'hs_err_pid 只允许日志；不因文件名前缀相同而删除 hprof、dmp 或其他用户文件。
            If Not IsGarbageDirectory AndAlso Not IsGarbageFile Then Continue For
            Dim Attributes As FileAttributes
            If Not Context.TryValidate(Root, Entry, Attributes) Then Continue For
            If (Attributes And FileAttributes.Directory) <> 0 Then
                If IsGarbageDirectory Then DeleteTree(Root, Entry, Context)
            ElseIf IsGarbageFile Then
                DeleteFile(Root, Entry, Context)
            End If
        Next
    End Sub

    Private Shared Sub DeleteTree(Root As String, Folder As String, Context As CleanupContext)
        If Not IsDescendant(Root, Folder) Then
            Context.Skip(Folder)
            Return
        End If
        '一个登记游戏目录即使位于另一个游戏的 logs 等目录中，也不能被整树删除。
        For Each Registered In Context.Roots
            If String.Equals(Registered, Folder, StringComparison.OrdinalIgnoreCase) OrElse IsDescendant(Folder, Registered) Then
                Context.Skip(Folder)
                Return
            End If
        Next
        Dim Pending As New Stack(Of Tuple(Of String, Boolean))
        Pending.Push(Tuple.Create(Folder, False))
        While Pending.Count > 0
            Context.CheckCancellation()
            Dim Frame = Pending.Pop()
            Dim Attributes As FileAttributes
            If Not Context.TryValidate(Root, Frame.Item1, Attributes) Then Continue While
            If (Attributes And FileAttributes.Directory) = 0 Then
                Context.Skip(Frame.Item1)
                Continue While
            End If
            If Frame.Item2 Then
                Try
                    Context.CheckCancellation()
                    '非递归删除；有链接、拒绝访问或后来新建的内容时保留目录。
                    If Not Context.TryValidate(Root, Frame.Item1, Attributes) Then Continue While
                    Directory.Delete(Frame.Item1, False)
                Catch ex As Exception When IsPathFailure(ex)
                    Context.Skip(Frame.Item1)
                End Try
            Else
                If Not Context.GarbageDirectories.Add(Frame.Item1) Then Continue While
                Dim Entries = Context.ReadEntries(Root, Frame.Item1)
                If Entries Is Nothing Then Continue While
                Pending.Push(Tuple.Create(Frame.Item1, True))
                For Each Entry In Entries
                    Context.CheckCancellation()
                    If Not Context.TryValidate(Root, Entry, Attributes) Then Continue For
                    If (Attributes And FileAttributes.Directory) <> 0 Then
                        Pending.Push(Tuple.Create(Entry, False))
                    Else
                        DeleteFile(Root, Entry, Context)
                    End If
                Next
            End If
        End While
    End Sub

    Private Shared Sub DeleteFile(Root As String, FileName As String, Context As CleanupContext)
        If Not IsDescendant(Root, FileName) Then
            Context.Skip(FileName)
            Return
        End If
        If Not Context.Files.Add(FileName) Then Return
        Try
            Dim Attributes As FileAttributes
            If Not Context.TryValidate(Root, FileName, Attributes) Then Return
            If (Attributes And FileAttributes.Directory) <> 0 Then
                Context.Skip(FileName)
                Return
            End If
            Dim Length = New FileInfo(FileName).Length
            Context.CheckCancellation()
            If Not Context.TryValidate(Root, FileName, Attributes) Then Return
            File.Delete(FileName)
            Context.Result.DeletedFiles += 1
            Context.Result.DeletedBytes += Length
        Catch ex As Exception When IsPathFailure(ex)
            Context.Skip(FileName)
        End Try
    End Sub

    Private Shared Function IsAbsolutePath(Value As String) As Boolean
        Value = Value.Replace("/", "\")
        If Value.StartsWith("\\?\", StringComparison.Ordinal) OrElse Value.StartsWith("\\.\", StringComparison.Ordinal) Then Return False
        Return (Value.Length >= 3 AndAlso Char.IsLetter(Value(0)) AndAlso Value(1) = ":"c AndAlso Value(2) = "\"c) OrElse
            Value.StartsWith("\\", StringComparison.Ordinal)
    End Function

    Private Shared Function FullPath(Value As String) As String
        Dim Full = Path.GetFullPath(Value.Replace("/", "\"))
        Dim VolumeRoot = Path.GetPathRoot(Full)
        If String.Equals(Full.TrimEnd("\"c), VolumeRoot.TrimEnd("\"c), StringComparison.OrdinalIgnoreCase) Then Return VolumeRoot
        Return Full.TrimEnd("\"c)
    End Function

    Private Shared Function IsDescendant(Root As String, Target As String) As Boolean
        Return Target.StartsWith(Root & "\", StringComparison.OrdinalIgnoreCase)
    End Function

    Private Shared Function IsPathFailure(ex As Exception) As Boolean
        Return TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
            TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException OrElse TypeOf ex Is SecurityException
    End Function

    Private NotInheritable Class CleanupContext
        Friend ReadOnly Result As New CleanupResult
        Friend ReadOnly Roots As New List(Of String)
        Friend ReadOnly GameDirectories As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Friend ReadOnly GarbageDirectories As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Friend ReadOnly Files As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly Skipped As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly ShouldStop As Func(Of Boolean)

        Friend Sub New(StopRequested As Func(Of Boolean))
            ShouldStop = StopRequested
        End Sub

        Friend Sub CheckCancellation()
            If ShouldStop IsNot Nothing AndAlso ShouldStop() Then Throw New OperationCanceledException("游戏垃圾清理已取消。")
        End Sub

        Friend Sub Skip(Item As String)
            If Skipped.Add(Item) Then Result.SkippedItems += 1
        End Sub

        Friend Function ReadEntries(Root As String, Folder As String) As String()
            CheckCancellation()
            Try
                Dim Attributes As FileAttributes
                If Not TryValidate(Root, Folder, Attributes) Then Return Nothing
                If (Attributes And FileAttributes.Directory) = 0 Then
                    Skip(Folder)
                    Return Nothing
                End If
                'GetFileSystemEntries 在删除前取得快照，不在修改目录时延迟迭代。
                Return Directory.GetFileSystemEntries(Folder)
            Catch ex As Exception When IsPathFailure(ex)
                Skip(Folder)
                Return Nothing
            End Try
        End Function

        Friend Function TryValidate(Root As String, Target As String, ByRef Attributes As FileAttributes) As Boolean
            CheckCancellation()
            Try
                Target = FullPath(Target)
                If Not String.Equals(Root, Target, StringComparison.OrdinalIgnoreCase) AndAlso Not IsDescendant(Root, Target) Then
                    Skip(Target)
                    Return False
                End If
                Dim Current = Root
                Attributes = File.GetAttributes(Current)
                If (Attributes And FileAttributes.ReparsePoint) <> 0 OrElse (Attributes And FileAttributes.Directory) = 0 Then
                    Skip(Target)
                    Return False
                End If
                If String.Equals(Root, Target, StringComparison.OrdinalIgnoreCase) Then Return True
                Dim Segments = Target.Substring(Root.Length + 1).Split("\"c)
                For Index As Integer = 0 To Segments.Length - 1
                    Current = Path.Combine(Current, Segments(Index))
                    Attributes = File.GetAttributes(Current)
                    If (Attributes And FileAttributes.ReparsePoint) <> 0 OrElse
                        (Index < Segments.Length - 1 AndAlso (Attributes And FileAttributes.Directory) = 0) Then
                        Skip(Target)
                        Return False
                    End If
                Next
                Return True
            Catch ex As Exception When IsPathFailure(ex)
                Skip(Target)
                Return False
            End Try
        End Function
    End Class
End Class
