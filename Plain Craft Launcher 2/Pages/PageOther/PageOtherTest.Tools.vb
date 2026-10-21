Partial Public Class PageOtherTest
    Private PageLoaded As Boolean
    Private Shared MemoryRunning As Integer
    Private Shared CleanupRunning As Integer

    Public Sub New()
        InitializeComponent()
        TextDownloadUrl.ValidateRules.Add(New ValidateFunc(
            Function(Value)
                Try
                    ToolboxDownloadSession.ParseUrl(Value)
                    Return ""
                Catch ex As ArgumentException
                    Return ex.Message
                End Try
            End Function))
        TextDownloadFolder.ValidateRules.Add(New ValidateFolderPath(False))
        TextDownloadName.ValidateRules.Add(New ValidateFunc(AddressOf ToolboxDownloadSession.ValidateName))
    End Sub

    Private Sub MeLoaded() Handles Me.Loaded
        If PageLoaded Then Return
        Dim Folder = Settings.Get(Of String)("CacheDownloadFolder")
        If String.IsNullOrWhiteSpace(Folder) OrElse New ValidateFolderPath(False).Validate(Folder) <> "" Then Folder = Paths.Base & "PCL\MyDownload\"
        TextDownloadFolder.Text = Folder
        TextDownloadFolder.Validate()
        TextDownloadUrl.Validate()
        TextDownloadName.Validate()
        PageLoaded = True
        StartButtonRefresh()
    End Sub

    Private Sub StartButtonRefresh() Handles TextDownloadUrl.TextChanged, TextDownloadFolder.TextChanged, TextDownloadName.TextChanged
        If BtnDownloadStart Is Nothing Then Return
        BtnDownloadStart.IsEnabled = PageLoaded AndAlso TextDownloadUrl.IsValidated AndAlso TextDownloadFolder.IsValidated AndAlso TextDownloadName.IsValidated
        BtnDownloadOpen.IsEnabled = PageLoaded AndAlso TextDownloadFolder.IsValidated
    End Sub

    Private Sub TextDownloadUrl_TextChanged() Handles TextDownloadUrl.TextChanged
        If Not PageLoaded OrElse Not String.IsNullOrEmpty(TextDownloadName.Text) Then Return
        TextDownloadName.Text = ToolboxDownloadSession.SuggestName(TextDownloadUrl.Text)
    End Sub

    Private Sub SaveCacheDownloadFolder() Handles TextDownloadFolder.ValidatedTextChanged
        If PageLoaded AndAlso TextDownloadFolder.IsValidated Then Settings.Set("CacheDownloadFolder", TextDownloadFolder.Text)
    End Sub

    Private Sub TextDownloadUrl_KeyDown(sender As Object, e As KeyEventArgs) Handles TextDownloadUrl.KeyDown, TextDownloadName.KeyDown
        If e.Key = Key.Return AndAlso BtnDownloadStart.IsEnabled Then BtnDownloadStart_Click()
    End Sub

    Private Sub BtnDownloadStart_Click() Handles BtnDownloadStart.Click
        If Not TryStartCustomDownload(TextDownloadUrl.Text, TextDownloadName.Text, TextDownloadFolder.Text) Then Return
        TextDownloadUrl.Text = ""
        TextDownloadName.Text = ""
        TextDownloadUrl.Validate()
        TextDownloadName.Validate()
        TextDownloadUrl.ForceShowAsSuccess()
        TextDownloadName.ForceShowAsSuccess()
        StartButtonRefresh()
    End Sub

    Public Shared Sub StartCustomDownload(Url As String, FileName As String, Optional Folder As String = Nothing)
        TryStartCustomDownload(Url, FileName, Folder)
    End Sub

    Private Shared Function TryStartCustomDownload(Url As String, FileName As String, Folder As String) As Boolean
        Dim Session As ToolboxDownloadSession = Nothing
        Try
            If IsCleaningGameFiles Then Throw New InvalidOperationException("正在清理游戏文件，请稍后再下载。")
            Dim Address = ToolboxDownloadSession.ParseUrl(Url)
            If String.IsNullOrWhiteSpace(Folder) Then
                Dim Chosen = RunInUiWait(Function() Dialogs.SaveFile("选择文件保存位置", FileName))
                If Chosen Is Nothing Then Return False
                FileName = IO.Path.GetFileName(Chosen)
                Folder = IO.Path.GetDirectoryName(Chosen)
            End If
            Session = ToolboxDownloadSession.Create(Folder, FileName)
            Dim AllowOverwrite = IO.File.Exists(Session.TargetPath)
            If AllowOverwrite AndAlso MyMsgBox("目标文件已存在，下载成功后是否替换？" & vbCrLf & vbCrLf & Session.TargetPath, "覆盖确认", "替换", "取消", IsWarn:=True) <> 1 Then
                Session.Complete(False)
                Return False
            End If
            Dim ActiveSession = Session
            Dim File = New NetFile({Address.AbsoluteUri}, Session.StagePath, New FileChecker With {.MinSize = 0}, SimulateBrowserHeaders:=True)
            File.AllowCredentialHeaders = False
            File.CanceledMergeFinished = AddressOf ActiveSession.OnCanceledMergeFinished
            Dim Download As New LoaderDownload("下载文件：" & FileName, New List(Of NetFile) From {File})
            Dim Combo As LoaderCombo(Of Integer) = Nothing
            Dim Save As New LoaderTask(Of Integer, Integer)("保存下载文件",
                Sub(Task)
                    ActiveSession.Commit(AllowOverwrite, Function() Task.IsCanceled OrElse Combo.State = LoadState.Canceled)
                End Sub) With {.ProgressWeight = 0.1}
            Combo = New LoaderCombo(Of Integer)("自定义下载：" & FileName & " (" & GetUuid() & ")", New LoaderBase() {Download, Save})
            Combo.OnStateChanged = Sub(Loader) DownloadState(CType(Loader, LoaderCombo(Of Integer)), ActiveSession)
            Combo.Start()
            LoaderTaskbarAdd(Combo)
            RunInUi(Sub()
                        FrmMain?.BtnExtraDownload.ShowRefresh()
                        FrmMain?.BtnExtraDownload.Ribble()
                    End Sub)
            Return True
        Catch ex As Exception
            Session?.Complete(False)
            Logger.Error(ex, "开始自定义下载失败", LogBehavior.Toast)
            Return False
        End Try
    End Function

    Private Shared Sub DownloadState(Loader As LoaderCombo(Of Integer), Session As ToolboxDownloadSession)
        Select Case Loader.State
            Case LoadState.Finished
                Session.Complete(True)
                Hint(Loader.Name & "完成！", HintType.Green)
            Case LoadState.Canceled
                Session.Complete(False)
                If Session.RecoveryPath IsNot Nothing Then
                    Hint("下载已取消，原文件备份保留在：" & Session.RecoveryPath, HintType.Red)
                ElseIf Session.UnrevertedPath IsNot Nothing Then
                    Hint("下载已取消，但保存的文件因占用或权限问题未能撤回：" & Session.UnrevertedPath, HintType.Red)
                Else
                    Hint("自定义下载已取消。")
                End If
            Case LoadState.Failed
                Session.Complete(False)
                Logger.Error(Loader.Error, If(Session.RecoveryPath IsNot Nothing,
                    "自定义下载失败，原文件备份保留在：" & Session.RecoveryPath,
                    If(Session.UnrevertedPath IsNot Nothing,
                        "自定义下载失败，保存的文件未能撤回：" & Session.UnrevertedPath,
                        "自定义下载失败，原文件已保留。")), LogBehavior.Toast)
        End Select
    End Sub

    Private Sub BtnDownloadSelect_Click() Handles BtnDownloadSelect.Click
        Dim Folder = Dialogs.SelectFolder("选择目标文件夹", False).FirstOrDefault()
        If Folder IsNot Nothing Then TextDownloadFolder.Text = Folder
    End Sub

    Private Sub BtnDownloadOpen_Click() Handles BtnDownloadOpen.Click
        Try
            Dim Folder = TextDownloadFolder.Text
            If New ValidateFolderPath(False).Validate(Folder) <> "" Then Return
            IO.Directory.CreateDirectory(Folder)
            StartProcess(Folder)
        Catch ex As Exception
            Logger.Error(ex, "打开下载文件夹失败", LogBehavior.Toast)
        End Try
    End Sub

    Friend Shared ReadOnly Property IsCleaningGameFiles As Boolean
        Get
            Return Threading.Volatile.Read(CleanupRunning) <> 0
        End Get
    End Property

    Private Sub BtnClear_Click() Handles BtnClear.Click
        RubbishClear()
    End Sub

    Public Shared Sub RubbishClear()
        If Threading.Interlocked.CompareExchange(CleanupRunning, 1, 0) <> 0 Then Return
        RunInNewThread(
            Sub()
                RunInUi(Sub()
                            If FrmOtherTest IsNot Nothing Then FrmOtherTest.BtnClear.IsEnabled = False
                        End Sub)
                Try
                    If GameFilesBusy() Then
                        Hint("请先关闭游戏，并等待启动和下载任务结束。")
                        Return
                    End If
                    If Not McFolderList.Any() Then McFolderListLoader.Start()
                    McFolderListLoader.WaitForExit()
                    Dim Folders = McFolderList.Select(Function(Item) Item.Location).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    If MyMsgBox("清理已添加游戏目录中的日志、崩溃报告和可重建临时文件。" & vbCrLf & "存档、模组、配置、截图及自定义缓存目录将保留。", "清理游戏垃圾", "清理", "取消", IsWarn:=True) <> 1 Then Return
                    If GameFilesBusy() Then
                        Hint("游戏或下载状态已改变，本次清理已停止。")
                        Return
                    End If
                    Dim Result = GameGarbageCleaner.Clean(Folders, AddressOf GameFilesBusy)
                    Dim Message = $"已清理 {Result.DeletedFiles} 个文件，释放 {StringUtils.FormatByteSize(Result.DeletedBytes)}。"
                    If Result.SkippedItems > 0 Then
                        Hint(Message & $"{Result.SkippedItems} 项因链接、占用或权限问题未清理。")
                    Else
                        Hint(Message, HintType.Green)
                    End If
                Catch ex As OperationCanceledException
                    Hint("检测到游戏或下载任务，已停止继续清理。")
                Catch ex As Exception
                    Logger.Error(ex, "清理游戏垃圾失败", LogBehavior.Toast)
                Finally
                    Threading.Interlocked.Exchange(CleanupRunning, 0)
                    RunInUi(Sub()
                                If FrmOtherTest IsNot Nothing Then FrmOtherTest.BtnClear.IsEnabled = True
                            End Sub)
                End Try
            End Sub, "Game Rubbish Clear")
    End Sub

    Private Shared Function GameFilesBusy() As Boolean
        Return HasRunningMinecraft OrElse McLaunchLoader.State = LoadState.Loading OrElse HasDownloadingTask()
    End Function

    Private Sub BtnMemory_Click() Handles BtnMemory.Click
        RunInThread(Sub() MemoryOptimize(True))
    End Sub

    Public Shared Sub MemoryOptimize(ShowHint As Boolean)
        If Threading.Interlocked.CompareExchange(MemoryRunning, 1, 0) <> 0 Then
            If ShowHint Then Hint("内存优化尚未结束，请稍等！")
            Return
        End If
        Try
            If ShowHint Then Hint("正在进行内存优化……")
            Dim Released As Long
            If WindowsUtils.HasAdminRole() Then
                Dim Before = CLng(My.Computer.Info.AvailablePhysicalMemory)
                MemoryOptimizeInternal(ShowHint)
                Released = Math.Max(0L, CLng(My.Computer.Info.AvailablePhysicalMemory) - Before)
            Else
                Dim ExitCode = RunAsAdmin("--memory")
                If ExitCode < 0 Then Return
                Released = CLng(ExitCode) * 1024L
            End If
            If ShowHint Then Hint($"内存优化完成，目前可用 {StringUtils.FormatByteSize(CLng(My.Computer.Info.AvailablePhysicalMemory))}，增加约 {StringUtils.FormatByteSize(Released)}。", HintType.Green)
        Catch ex As Exception
            If ShowHint Then
                Logger.Warn(ex, "内存优化未完成", LogBehavior.Toast)
            Else
                Logger.Warn(ex, "内存优化未完成")
            End If
        Finally
            Threading.Interlocked.Exchange(MemoryRunning, 0)
        End Try
    End Sub

    Public Shared Sub MemoryOptimizeInternal(ShowHint As Boolean)
        NativeMemoryOptimizer.Run()
    End Sub
End Class
