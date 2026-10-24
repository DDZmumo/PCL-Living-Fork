Imports System.Reflection
Imports System.Security.Cryptography

Public Module ModLauncherUpdate
    Private Const Repository As String = LauncherRepository
    Private Const CompatibilityRepository As String = "DDZmumo/LivingPCL2-Re"
    Private Const ReleasePage As String = LauncherReleasesUrl & "/latest"
    Private CheckRunning As Integer

    Friend Function NewerRelease(Tag As String, Current As String) As Boolean
        Dim LatestVersion As System.Version = Nothing, CurrentVersion As System.Version = Nothing
        If Not System.Version.TryParse(Tag?.TrimStart("v"c, "V"c), LatestVersion) OrElse Not System.Version.TryParse(Current, CurrentVersion) Then Return False
        LatestVersion = New System.Version(LatestVersion.Major, LatestVersion.Minor, Math.Max(0, LatestVersion.Build), Math.Max(0, LatestVersion.Revision))
        CurrentVersion = New System.Version(CurrentVersion.Major, CurrentVersion.Minor, Math.Max(0, CurrentVersion.Build), Math.Max(0, CurrentVersion.Revision))
        Return LatestVersion > CurrentVersion
    End Function

    Friend Function ValidAssetUrl(Value As String) As Boolean
        Dim Url As Uri = Nothing
        Return Uri.TryCreate(Value, UriKind.Absolute, Url) AndAlso Url.Scheme = "https" AndAlso Url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) AndAlso
            (Url.AbsolutePath.StartsWith("/" & Repository & "/releases/download/", StringComparison.OrdinalIgnoreCase) OrElse
             Url.AbsolutePath.StartsWith("/" & CompatibilityRepository & "/releases/download/", StringComparison.OrdinalIgnoreCase)) AndAlso Url.UserInfo = "" AndAlso Url.IsDefaultPort
    End Function

    Friend Function ShouldPromptForUpdate(Tag As String, Current As String, IgnoredTag As String) As Boolean
        Return NewerRelease(Tag, Current) AndAlso Not String.Equals(Tag, IgnoredTag, StringComparison.Ordinal)
    End Function

    Friend Function IgnoreTagAfterChoice(IgnoredTag As String, Tag As String, Choice As Integer) As String
        If Choice = 2 Then Return Tag
        If Choice = 1 AndAlso String.Equals(IgnoredTag, Tag, StringComparison.Ordinal) Then Return ""
        Return IgnoredTag
    End Function

    Friend Function ReleaseExecutable(Info As JObject) As JObject
        Dim Assets = TryCast(Info("assets"), JArray)
        If Assets Is Nothing Then Throw New Exception("发布中没有下载附件。")
        Dim Executables = Assets.OfType(Of JObject)().Where(Function(Asset)
            Dim Name = Asset("name")?.ToString()
            Return Name IsNot Nothing AndAlso Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        End Function).ToList()
        Dim Candidates = Executables.Where(Function(Asset) Asset("name").ToString().StartsWith("PCL-Living-Fork-", StringComparison.OrdinalIgnoreCase)).ToList()
        If Candidates.Count = 0 Then Candidates = Executables.Where(Function(Asset) Asset("name").ToString().StartsWith("LivingPCL2-Re-", StringComparison.OrdinalIgnoreCase)).ToList()
        If Candidates.Count <> 1 Then Throw New Exception("无法确定本版本的启动器 EXE。")
        If Not ValidAssetUrl(Candidates(0)("browser_download_url")?.ToString()) Then Throw New Exception("下载地址不属于本项目。")
        Return Candidates(0)
    End Function

    Friend Function ManifestHash(Manifest As String, Name As String) As String
        For Each Line In Manifest.Replace(vbCr, "").Split(vbLf)
            Dim Match = System.Text.RegularExpressions.Regex.Match(Line, "\A([0-9a-fA-F]{64})\s+\*?(.+?)\s*\z")
            If Match.Success AndAlso Match.Groups(2).Value = Name Then Return Match.Groups(1).Value.ToLowerInvariant()
        Next
        Throw New Exception("发布附件缺少对应的 SHA256 校验值。")
    End Function

    Friend Sub CheckForUpdates(Startup As Boolean)
        If Interlocked.CompareExchange(CheckRunning, 1, 0) <> 0 Then
            If Not Startup Then Hint("正在检查更新，请稍候……")
            Return
        End If
        If Not Startup Then Hint("正在检查 PCL Living Fork 的 GitHub Releases……")
        RunInNewThread(Sub()
            Try
                Dim Info As JObject = NetRequestByClientRetry("https://api.github.com/repos/" & Repository & "/releases/latest", Accept:="application/vnd.github+json", RequireJson:=True).DeserializeJson()
                Dim Tag = Info("tag_name")?.ToString()
                Dim ParsedVersion As System.Version = Nothing
                If String.IsNullOrWhiteSpace(Tag) OrElse Not System.Version.TryParse(Tag.TrimStart("v"c, "V"c), ParsedVersion) Then Throw New Exception("发布标签不是可识别的版本号。")
                If CBool(If(Info("draft"), False)) OrElse CBool(If(Info("prerelease"), False)) Then Return
                If Not NewerRelease(Tag, VersionBaseName) Then
                    If Not Startup Then MyMsgBox($"当前版本：{VersionBaseName}{vbCrLf}最新正式版：{Tag}{vbCrLf}{vbCrLf}当前没有更新的正式版本。", "PCL Living Fork 更新")
                    Return
                End If
                Dim IgnoredTag = Settings.Get(Of String)("ToolLauncherUpdateIgnoredTag")
                If Startup AndAlso Not ShouldPromptForUpdate(Tag, VersionBaseName, IgnoredTag) Then Return
                Dim Choice = MyMsgBox($"当前版本：{VersionBaseName}{vbCrLf}最新正式版：{Tag}{vbCrLf}{vbCrLf}下载并校验更新后，启动器将自动替换并重新启动。", "发现 PCL Living Fork 新版本", "下载并更新", "暂不更新")
                If Choice = 1 Then
                    Settings.Set("ToolLauncherUpdateIgnoredTag", IgnoreTagAfterChoice(IgnoredTag, Tag, Choice))
                    DownloadAndUpdate(Info)
                ElseIf Choice = 2 Then
                    Settings.Set("ToolLauncherUpdateIgnoredTag", IgnoreTagAfterChoice(IgnoredTag, Tag, Choice))
                End If
            Catch ex As Exception
                If Startup Then
                    Logger.Info("启动更新检查未完成，将在下次启动时重试。")
                Else
                    UpdateFailure(ex.Message)
                End If
            Finally
                Interlocked.Exchange(CheckRunning, 0)
            End Try
        End Sub, "PCL Living Fork 更新检查", ThreadPriority.BelowNormal)
    End Sub

    Private Sub DownloadAndUpdate(Info As JObject)
        Try
            Dim Asset = ReleaseExecutable(Info)
            Dim HashAsset = TryCast(Info("assets"), JArray).OfType(Of JObject)().SingleOrDefault(Function(Item) Item("name")?.ToString() = "SHA256SUMS.txt")
            If HashAsset Is Nothing OrElse Not ValidAssetUrl(HashAsset("browser_download_url")?.ToString()) Then Throw New Exception("发布中没有可信的校验文件。")
            Dim Manifest As String = NetRequestByClientRetry(HashAsset("browser_download_url").ToString())
            Dim ExpectedHash = ManifestHash(Manifest, Asset("name").ToString())
            Dim Folder = System.IO.Path.Combine(Paths.Base, "PCL", "Updates")
            Directory.CreateDirectory(Folder)
            Dim Staged = System.IO.Path.Combine(Folder, "PCL Living Fork.exe")
            Hint("正在下载 PCL Living Fork 更新……")
            NetDownloadByLoader(Asset("browser_download_url").ToString(), Staged, Check:=New FileChecker With {.Hash = ExpectedHash, .ActualSize = CLng(Asset("size"))})
            ValidateExecutable(Staged, ExpectedHash)
            If HasDownloadingTask() Then Throw New Exception("仍有下载任务正在进行。请等待任务结束后重试更新。")
            Dim Target = Process.GetCurrentProcess().MainModule.FileName
            Dim Arguments = $"--livingpcl-update {Process.GetCurrentProcess().Id} ""{System.IO.Path.GetFileName(Target)}"" {ExpectedHash}"
            Dim Helper = Process.Start(New ProcessStartInfo(Staged, Arguments) With {.UseShellExecute = False, .CreateNoWindow = True, .WindowStyle = ProcessWindowStyle.Hidden, .WorkingDirectory = Folder})
            If Helper Is Nothing Then Throw New Exception("无法启动更新替换程序。")
            If Helper.WaitForExit(500) Then Throw New Exception("更新替换程序未能正常启动。")
            RunInUi(Sub() FrmMain.EndProgram(SendWarning:=False))
        Catch ex As Exception
            UpdateFailure(ex.Message)
        End Try
    End Sub

    Private Sub UpdateFailure(Reason As String)
        If MyMsgBox("更新未完成，现有启动器已保留。" & vbCrLf & vbCrLf & Reason, "PCL Living Fork 更新失败", "打开发布页", "取消", IsWarn:=True) = 1 Then OpenWebsite(ReleasePage)
    End Sub

    Friend Sub ValidateExecutable(FileName As String, ExpectedHash As String)
        If Not System.Text.RegularExpressions.Regex.IsMatch(If(ExpectedHash, ""), "\A[0-9a-fA-F]{64}\z") Then Throw New Exception("更新校验值无效。")
        Using Stream = System.IO.File.OpenRead(FileName), Hash = SHA256.Create()
            Dim Actual = BitConverter.ToString(Hash.ComputeHash(Stream)).Replace("-", "")
            If Not Actual.Equals(ExpectedHash, StringComparison.OrdinalIgnoreCase) Then Throw New Exception("更新文件 SHA256 校验失败。")
        End Using
        If Not IsLivingExecutable(FileName) Then Throw New Exception("更新文件不是 PCL Living Fork 启动器。")
    End Sub

    '兼容新旧构建的程序集名称，且产品信息须属于 LivingPCL，避免覆盖官方程序。
    Friend Function IsLivingExecutable(FileName As String) As Boolean
        Dim Identity = AssemblyName.GetAssemblyName(FileName).Name
        Return (Identity = "LivingPCL" OrElse Identity = "Plain Craft Launcher 2") AndAlso
            {"LivingPCL", LauncherDisplayName}.Contains(FileVersionInfo.GetVersionInfo(FileName).ProductName, StringComparer.Ordinal)
    End Function

    '沿用官方 PCL 的流程：运行已下载的新 EXE，由新进程替换退出后的旧 EXE。
    Friend Sub ReplaceExecutable(Staged As String, Target As String, ExpectedHash As String, Restart As Boolean)
        ValidateExecutable(Staged, ExpectedHash)
        Dim Temporary = Target & ".updating-" & Guid.NewGuid().ToString("N")
        Dim Backup = Target & ".update-backup-" & Guid.NewGuid().ToString("N")
        Dim Replaced As Boolean = False
        Dim HadTarget = System.IO.File.Exists(Target)
        If HadTarget AndAlso Not IsLivingExecutable(Target) Then Throw New Exception("更新目标不是 PCL Living Fork 启动器。")
        Try
            System.IO.File.Copy(Staged, Temporary)
            ValidateExecutable(Temporary, ExpectedHash)
            If HadTarget Then
                System.IO.File.Replace(Temporary, Target, Backup, True)
            Else
                System.IO.File.Move(Temporary, Target)
            End If
            Replaced = True
            If Restart Then
                Dim Started = Process.Start(New ProcessStartInfo(Target) With {.UseShellExecute = True, .WorkingDirectory = System.IO.Path.GetDirectoryName(Target)})
                If Started Is Nothing OrElse Started.WaitForExit(3000) Then Throw New Exception("新版本启动失败。")
            End If
            If System.IO.File.Exists(Backup) Then System.IO.File.Delete(Backup)
        Catch
            If Replaced Then
                If HadTarget AndAlso System.IO.File.Exists(Backup) Then
                    System.IO.File.Copy(Backup, Target, True)
                    System.IO.File.Delete(Backup)
                ElseIf Not HadTarget AndAlso System.IO.File.Exists(Target) Then
                    System.IO.File.Delete(Target)
                End If
            End If
            Throw
        Finally
            If System.IO.File.Exists(Temporary) Then System.IO.File.Delete(Temporary)
        End Try
    End Sub

    Friend Function HandleUpdateCommand(Arguments As String()) As Boolean
        If Arguments.Length = 0 OrElse Arguments(0) <> "--livingpcl-update" Then Return False
        Try
            If Arguments.Length <> 4 Then Throw New Exception("更新参数不完整。")
            Dim ParentId As Integer
            If Not Integer.TryParse(Arguments(1), ParentId) OrElse ParentId <= 0 Then Throw New Exception("更新进程号无效。")
            Dim Staged = Assembly.GetExecutingAssembly().Location
            Dim UpdateFolder = System.IO.Path.GetDirectoryName(Staged)
            Dim PclFolder = System.IO.Path.GetDirectoryName(UpdateFolder)
            If Not System.IO.Path.GetFileName(UpdateFolder).Equals("Updates", StringComparison.OrdinalIgnoreCase) OrElse
                Not System.IO.Path.GetFileName(PclFolder).Equals("PCL", StringComparison.OrdinalIgnoreCase) Then Throw New Exception("更新程序位置无效。")
            Dim Name = Arguments(2)
            If System.IO.Path.GetFileName(Name) <> Name OrElse Not Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) Then Throw New Exception("更新目标无效。")
            Dim Target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(PclFolder), Name)
            ValidateExecutable(Staged, Arguments(3))
            Dim Parent As Process = Nothing
            Try
                Parent = Process.GetProcessById(ParentId)
            Catch ex As ArgumentException
            End Try
            If Parent IsNot Nothing Then
                If Not String.Equals(Parent.MainModule.FileName, Target, StringComparison.OrdinalIgnoreCase) Then Throw New Exception("更新进程与目标不一致。")
                If Not Parent.WaitForExit(20000) Then Throw New Exception("原启动器尚未退出。")
            End If
            ReplaceExecutable(Staged, Target, Arguments(3), True)
        Catch ex As Exception
            If MessageBox.Show("自动更新未完成，请从发布页手动更新。" & vbCrLf & ex.Message, "PCL Living Fork 更新失败", MessageBoxButton.YesNo, MessageBoxImage.Warning) = MessageBoxResult.Yes Then
                Process.Start(New ProcessStartInfo(ReleasePage) With {.UseShellExecute = True})
            End If
        End Try
        Environment.Exit(0)
        Return True
    End Function
End Module
