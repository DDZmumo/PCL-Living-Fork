Imports System
Imports System.Collections.Concurrent
Imports System.IO
Imports System.Text.RegularExpressions

'自定义下载沿用 PCL 下载引擎；先写独立暂存文件，成功后才替换用户目标。
Friend NotInheritable Class ToolboxDownloadSession
    Private Shared ReadOnly Targets As New ConcurrentDictionary(Of String, Byte)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly Gate As New Object
    Private Released As Boolean
    Private Canceled As Boolean
    Private Applied As Boolean
    Private ReplacedExisting As Boolean
    Private FinishedSuccessfully As Boolean
    Friend ReadOnly TargetPath As String
    Friend ReadOnly StageFolder As String
    Friend ReadOnly StagePath As String
    Private ReadOnly BackupPath As String

    Private Sub New(Target As String)
        TargetPath = Target
        StageFolder = Path.Combine(Path.GetDirectoryName(Target), ".livingpcl-download-" & Guid.NewGuid().ToString("N"))
        StagePath = Path.Combine(StageFolder, "payload.tmp")
        BackupPath = Path.Combine(StageFolder, "previous.tmp")
    End Sub

    Friend Shared Function ParseUrl(Value As String) As Uri
        Dim Result As Uri = Nothing
        If String.IsNullOrWhiteSpace(Value) OrElse Not Uri.TryCreate(Value.Trim(), UriKind.Absolute, Result) OrElse
            (Result.Scheme <> Uri.UriSchemeHttp AndAlso Result.Scheme <> Uri.UriSchemeHttps) OrElse String.IsNullOrEmpty(Result.Host) OrElse Result.UserInfo <> "" Then
            Throw New ArgumentException("请输入完整的 HTTP 或 HTTPS 下载地址。")
        End If
        Return Result
    End Function

    Friend Shared Function ValidateName(Value As String) As String
        If String.IsNullOrWhiteSpace(Value) Then Return "请输入文件名。"
        If Value <> Value.Trim() OrElse Value.EndsWith(".", StringComparison.Ordinal) OrElse Value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 OrElse
            Value.Contains("\") OrElse Value.Contains("/") OrElse Value.Contains(":") OrElse Value = "." OrElse Value = ".." OrElse Value.Length > 255 Then Return "文件名不能包含路径或 Windows 不允许的字符。"
        Dim Stem = Value.Split("."c)(0)
        If Regex.IsMatch(Stem, "\A(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])\z", RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant) Then Return "不能使用 Windows 保留的设备名称。"
        Return ""
    End Function

    Friend Shared Function SuggestName(Value As String) As String
        Try
            Dim Address = ParseUrl(Value)
            Dim Name = Uri.UnescapeDataString(Address.AbsolutePath.Substring(Address.AbsolutePath.LastIndexOf("/"c) + 1))
            Return If(ValidateName(Name) = "", Name, "")
        Catch
            Return ""
        End Try
    End Function

    Friend Shared Function ResolveTarget(Folder As String, Name As String) As String
        Dim ErrorText = ValidateName(Name)
        If ErrorText <> "" Then Throw New ArgumentException(ErrorText)
        If String.IsNullOrWhiteSpace(Folder) OrElse Not Regex.IsMatch(Folder.Replace("/", "\"), "\A[A-Za-z]:\\") Then Throw New ArgumentException("保存位置必须是以盘符开头的完整文件夹路径。")
        Dim Parent = Path.GetFullPath(Folder.Replace("/", "\")).TrimEnd("\"c) & "\"
        Dim Result = Path.GetFullPath(Path.Combine(Parent, Name))
        If Not String.Equals(Path.GetDirectoryName(Result).TrimEnd("\"c) & "\", Parent, StringComparison.OrdinalIgnoreCase) Then Throw New ArgumentException("文件名不能离开选择的保存文件夹。")
        If Directory.Exists(Result) Then Throw New IOException("保存位置已存在同名文件夹。")
        If File.Exists(Result) AndAlso (File.GetAttributes(Result) And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("不能覆盖链接文件。")
        Return Result
    End Function

    Friend Shared Function Create(Folder As String, Name As String) As ToolboxDownloadSession
        Dim Target = ResolveTarget(Folder, Name)
        If Not Targets.TryAdd(Target, 0) Then Throw New IOException("这个文件已有下载任务，请等待完成或取消原任务。")
        Dim Session As New ToolboxDownloadSession(Target)
        Try
            Directory.CreateDirectory(Session.StageFolder)
            Return Session
        Catch
            Dim Ignored As Byte
            Targets.TryRemove(Target, Ignored)
            Throw
        End Try
    End Function

    Private Sub ValidateStage()
        Dim ExpectedParent = Path.GetDirectoryName(TargetPath)
        If Not String.Equals(Path.GetDirectoryName(StageFolder), ExpectedParent, StringComparison.OrdinalIgnoreCase) OrElse
            Not Regex.IsMatch(Path.GetFileName(StageFolder), "\A\.livingpcl-download-[0-9a-f]{32}\z") OrElse
            (Directory.Exists(StageFolder) AndAlso (File.GetAttributes(StageFolder) And FileAttributes.ReparsePoint) <> 0) Then Throw New IOException("暂存目录校验失败。")
        For Each Item In New String() {StagePath, BackupPath}
            If File.Exists(Item) AndAlso (File.GetAttributes(Item) And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("暂存文件不能是链接。")
        Next
    End Sub

    Friend Sub Commit(AllowOverwrite As Boolean, ShouldCancel As Func(Of Boolean))
        SyncLock Gate
            ValidateStage()
            If Released OrElse Canceled OrElse ShouldCancel() Then Throw New OperationCanceledException()
            If Applied Then Throw New InvalidOperationException("文件已保存，不能重复提交。")
            If Not File.Exists(StagePath) Then Throw New IOException("未找到已下载的文件。")
            Dim Existed = File.Exists(TargetPath)
            If Existed AndAlso Not AllowOverwrite Then Throw New IOException("目标文件已经存在，请重新选择文件名。")
            If Existed AndAlso (File.GetAttributes(TargetPath) And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("不能覆盖链接文件。")
            Try
                If Existed Then
                    File.Replace(StagePath, TargetPath, BackupPath)
                Else
                    File.Move(StagePath, TargetPath)
                End If
                ReplacedExisting = Existed
                Applied = True
                If ShouldCancel() Then Throw New OperationCanceledException()
            Catch
                Rollback()
                Throw
            End Try
        End SyncLock
    End Sub

    Private Sub Rollback()
        If Not Applied Then Return
        ValidateStage()
        If File.Exists(TargetPath) AndAlso (File.GetAttributes(TargetPath) And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("不能通过链接恢复原文件。")
        If ReplacedExisting Then
            If Not File.Exists(BackupPath) Then Throw New IOException("未找到原文件备份。")
            If File.Exists(TargetPath) Then
                File.Replace(BackupPath, TargetPath, Nothing)
            Else
                File.Move(BackupPath, TargetPath)
            End If
        ElseIf File.Exists(TargetPath) Then
            File.Move(TargetPath, StagePath)
        End If
        Applied = False
    End Sub

    Friend ReadOnly Property RecoveryPath As String
        Get
            SyncLock Gate
                Return If(Not FinishedSuccessfully AndAlso File.Exists(BackupPath), BackupPath, Nothing)
            End SyncLock
        End Get
    End Property

    Friend ReadOnly Property UnrevertedPath As String
        Get
            SyncLock Gate
                Return If(Not FinishedSuccessfully AndAlso Applied AndAlso File.Exists(TargetPath), TargetPath, Nothing)
            End SyncLock
        End Get
    End Property

    Private Sub Cleanup()
        ValidateStage()
        If File.Exists(BackupPath) AndAlso Not FinishedSuccessfully Then Throw New IOException("原文件备份已保留，不能删除。")
        For Each FilePath In New String() {StagePath, BackupPath}
            If File.Exists(FilePath) Then File.Delete(FilePath)
        Next
        If Directory.Exists(StageFolder) Then Directory.Delete(StageFolder, False)
    End Sub

    '下载引擎的取消/合并完成回调可重复调用，仅清理本任务的精确暂存文件。
    Friend Sub OnCanceledMergeFinished()
        SyncLock Gate
            If Not Released Then Return
            Try
                Cleanup()
            Catch ex As IOException
            Catch ex As UnauthorizedAccessException
            End Try
        End SyncLock
    End Sub

    Friend Sub Complete(Success As Boolean)
        SyncLock Gate
            If Released Then Return
            Canceled = Not Success
            Try
                If Success Then
                    FinishedSuccessfully = True
                Else
                    Rollback()
                End If
                Cleanup()
            Catch ex As IOException
            Catch ex As UnauthorizedAccessException
            Finally
                Released = True
                Dim Ignored As Byte
                Targets.TryRemove(TargetPath, Ignored)
            End Try
        End SyncLock
    End Sub
End Class
