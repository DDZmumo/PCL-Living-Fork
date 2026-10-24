Imports System.IO

Friend NotInheritable Class ModpackUpdateFlow
    Private Shared Active As Integer
    Friend Shared ReadOnly Property IsUpdating As Boolean
        Get
            Return Threading.Volatile.Read(Active) <> 0
        End Get
    End Property
    Friend Shared Sub Start(Instance As McInstance, Online As Boolean)
        If Instance Is Nothing OrElse EnvironmentHistoryUi.IsBusy OrElse HasRunningMinecraft OrElse QuickPlayWorlds.HasRunningSessions() OrElse McLaunchLoader.State = LoadState.Loading OrElse McInstanceLock.IsLocked(Instance) Then
            Hint("请先关闭游戏并等待版本任务完成。", HintType.Red)
            Return
        End If
        Dim ArchivePath As String = Nothing
        If Not Online Then
            ArchivePath = Dialogs.SelectFile("选择新版整合包", False, filter:={({"zip", "mrpack"}, "整合包文件")}).FirstOrDefault()
            If String.IsNullOrEmpty(ArchivePath) Then Return
        End If
        If Interlocked.CompareExchange(Active, 1, 0) <> 0 Then
            Hint("请等待当前整合包更新完成。")
            Return
        End If
        Dim StepLoader As New LoaderTask(Of String, String)("比较并更新整合包文件", Sub(Task) Execute(Instance, Online, ArchivePath, Task))
        Dim Loader As New LoaderCombo(Of String)("更新整合包：" & Instance.Name, {StepLoader}) With {.OnStateChanged = AddressOf LoaderStateChangedHintOnly}
        Dim Worker As New RedoableWorker(Sub() Loader.WaitForExit(Instance.PathVersion))
        If Not McInstanceLock.TryAcquire(Worker, Instance.PathVersion) Then
            Interlocked.Exchange(Active, 0)
            Return
        End If
        AddHandler Worker.Stopped, Sub() Interlocked.Exchange(Active, 0)
        Worker.Start()
        LoaderTaskbarAdd(Loader)
        FrmMain.BtnExtraDownload.ShowRefresh()
    End Sub
    Friend Shared Sub OpenBackups(Instance As McInstance)
        If Instance Is Nothing Then Return
        Dim Folder = Path.Combine(Instance.PathVersion, "PCL", "PackUpdateBackups")
        InstanceResourceStore.EnsureWithin(Instance.PathVersion, Folder)
        If Directory.Exists(Folder) Then OpenExplorer(Folder) Else Hint("此版本还没有整合包更新备份。")
    End Sub
    Private Shared Function SelectArchive(Title As String) As String
        Dim Result As String = Nothing
        RunInUiWait(Sub() Result = Dialogs.SelectFile(Title, False, filter:={({"zip", "mrpack"}, "整合包文件")}).FirstOrDefault())
        If String.IsNullOrEmpty(Result) Then Throw New OperationCanceledException()
        Return Result
    End Function
    Private Shared Function ChooseOnline(Baseline As JObject, Stage As String, Token As CancellationToken, ByRef Source As JObject) As String
        Source = TryCast(Baseline("source"), JObject)
        If Source IsNot Nothing Then Source = CType(Source.DeepClone(), JObject)
        If Source Is Nothing Then
            Dim Url As String = Nothing
            RunInUiWait(Sub() Url = MyMsgBoxInput("整合包项目地址", "CurseForge 或 Modrinth 整合包项目地址", "", Button2:="取消"))
            If String.IsNullOrWhiteSpace(Url) Then Throw New OperationCanceledException()
            Dim Match = System.Text.RegularExpressions.Regex.Match(Url, "\Ahttps://(?:www\.)?(modrinth\.com/modpack/([A-Za-z0-9_-]+)|curseforge\.com/minecraft/modpacks/([A-Za-z0-9_-]+))/?\z")
            If Not Match.Success Then Throw New IOException("请输入 CurseForge 或 Modrinth 的整合包项目地址。")
            If Match.Groups(2).Success Then
                Source = New JObject(New JProperty("platform", "Modrinth"), New JProperty("project", Match.Groups(2).Value))
            Else
                Dim Response = DlModRequest("https://api.curseforge.com/v1/mods/search?gameId=432&classId=4471&slug=" & Uri.EscapeDataString(Match.Groups(3).Value))
                Dim Project = CType(Response("data"), JArray).FirstOrDefault(Function(Item) Item("slug")?.ToString() = Match.Groups(3).Value)
                If Project Is Nothing Then Throw New IOException("没有找到该 CurseForge 整合包。")
                Source = New JObject(New JProperty("platform", "CurseForge"), New JProperty("project", Project("id").ToString()))
            End If
        End If
        Dim Platform As ResourcePlatforms
        If Not [Enum].TryParse(Source("platform").ToString(), Platform) OrElse Not {ResourcePlatforms.Modrinth, ResourcePlatforms.CurseForge}.Contains(Platform) Then Throw New IOException("无效的整合包更新来源。")
        Dim Id = Source("project").ToString()
        If Not System.Text.RegularExpressions.Regex.IsMatch(Id, "\A[A-Za-z0-9_-]+\z") Then Throw New IOException("无效的整合包项目编号。")
        Dim ProjectInfo = ResourceProject.FromProjectId(Id, Platform)
        If ProjectInfo.Types <> ResourceTypes.ModPack Then Throw New IOException("此地址不是整合包项目。")
        '每次明确检查更新时重新获取版本列表。
        ResourceVersion.ProjectFilesCache.Remove(Id)
        Dim CurrentId = Source("version")?.ToString()
        Dim Versions = ResourceVersion.FromProjectId(Id, Platform, False).Where(Function(Item) Item.Id <> CurrentId).OrderByDescending(Function(Item) Item.ReleaseDate).Take(60).ToList()
        Token.ThrowIfCancellationRequested()
        If Versions.Count = 0 Then Throw New IOException("没有其他可下载的整合包版本。")
        Dim Choice As Integer? = Nothing
        RunInUiWait(Sub() Choice = MyMsgBoxSelect(Versions.Select(Function(Item) CType(New MyRadioBox With {.Text = Item.Display & " · " & Item.ReleaseDate.ToString("yyyy-MM-dd") & " · " & Item.ReleaseTypeDisplay}, IMyRadio)), "选择整合包版本", "下载并预览", "取消"))
        If Not Choice.HasValue Then Throw New OperationCanceledException()
        Dim Selected = Versions(Choice.Value), Destination = Path.Combine(Stage, If(Platform = ResourcePlatforms.Modrinth, "update.mrpack", "update.zip"))
        ModpackUpdate.DownloadFiles(New List(Of NetFile) From {Selected.ToNetFile(Destination, ResourceVersion.DownloadReason.Update, Selected.GameVersions.FirstOrDefault(), Selected.ModLoaders)}, Token)
        Source("project") = ProjectInfo.Id
        Source("version") = Selected.Id
        Return Destination
    End Function

    Private Shared Sub Execute(Instance As McInstance, Online As Boolean, ArchivePath As String, Task As LoaderTask(Of String, String))
        Dim Token = Task.CreateCancellationToken(), Stage = RequestTaskTempFolder(), Root = Instance.PathIndie
        If ModpackUpdate.Pending(Instance.PathVersion).Any() Then
            If MyMsgBox("上次整合包更新被中断，需要先恢复更新前的文件。", "恢复整合包", "恢复", "取消", IsWarn:=True) <> 1 Then Throw New OperationCanceledException()
            ModpackUpdate.Recover(Instance.PathVersion, Root)
        End If
        Dim Baseline = ModpackUpdate.ReadManifest(Instance.PathVersion)
        If Baseline Is Nothing Then
            If MyMsgBox("此版本尚无作者文件记录。请选择当前版本原始整合包，以识别作者文件和个人修改。", "建立整合包记录", "选择原始包", "取消") <> 1 Then Throw New OperationCanceledException()
            Dim Original = ModpackUpdate.ReadPackage(SelectArchive("选择当前版本原始整合包"), Path.Combine(Stage, "original"), Token)
            If Instance.Version.VanillaName <> Original.Minecraft Then Throw New IOException("原始整合包的 Minecraft 版本与当前版本不同，请选择正确的旧包。")
            Baseline = ModpackUpdate.Manifest(Original)
        End If
        Dim Source As JObject = Nothing
        If Online Then ArchivePath = ChooseOnline(Baseline, Stage, Token, Source)
        Dim Package = ModpackUpdate.ReadPackage(ArchivePath, Path.Combine(Stage, "next"), Token)
        Package.Source = Source '本地包不能冒用旧在线版本号，之后可重新关联项目。
        Dim Plan = ModpackUpdate.Plan(Root, Baseline, Package)
        Task.Progress = 0.15
        '仅暂存变化的文件，读取模组 ID 后才展示最终冲突；此时不修改游戏目录。
        ModpackUpdate.DownloadChanges(Plan, Stage, Token)
        Dim RuntimeChanged = Instance.Version.VanillaName <> Package.Minecraft OrElse Instance.Version.ModLoaderVersions.Count <> Package.Loaders.Count OrElse
            Package.Loaders.Any(Function(Pair) Not Instance.Version.ModLoaderVersions.ContainsKey(Pair.Key) OrElse Instance.Version.ModLoaderVersions(Pair.Key) <> Pair.Value)
        Dim Summary = Package.Name & " " & Package.Version & vbCrLf &
            $"更新 {Plan.Changes.Where(Function(Item) Not Item.Conflict).Count()} 项，保留个人修改 {Plan.Changes.Where(Function(Item) Item.Conflict).Count()} 项。" & vbCrLf &
            If(RuntimeChanged, "Minecraft 或加载器将更新；存档会保留，请确认新版本兼容。" & vbCrLf, "") & vbCrLf &
            String.Join(vbCrLf, Plan.Changes.Select(Function(Item) Item.Reason & "：" & Item.Relative))
        If MyMsgBox(Summary, "整合包更新预览", "保留个人修改并更新", "取消", IsWarn:=Plan.Changes.Any(Function(Item) Item.Conflict) OrElse RuntimeChanged) <> 1 Then Throw New OperationCanceledException()
        Dim Extras As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Dim RuntimeBefore As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        If RuntimeChanged Then
            For Each Relative In {Instance.Name & ".json", Instance.Name & ".jar", "PCL/config.json"}
                RuntimeBefore(Relative) = ModpackUpdate.Digest(ModpackUpdate.Target(Instance.PathVersion, Relative))
            Next
            Dim BuildFolder = Path.Combine(Stage, "runtime", Instance.Name) & "\"
            Dim Build As New LoaderCombo(Of String)("准备整合包游戏组件", McInstallLoadersGet(New McInstallRequest With {
                .NewInstanceName = Instance.Name, .InstanceFolder = BuildFolder, .Components = New McComponents(Package.Minecraft, Package.Loaders), .IsIntermediateOutput = True,
                .MinecraftFolder = Path.GetDirectoryName(Path.GetDirectoryName(Instance.PathVersion.TrimEnd("\"c))) & "\"}))
            Using Registration = Token.Register(AddressOf Build.Cancel)
                Build.WaitForExit(BuildFolder, c:=Token)
            End Using
            For Each Extension In {".json", ".jar"}
                Dim Payload = Path.Combine(BuildFolder, Instance.Name & Extension)
                If File.Exists(Payload) Then Extras.Add(Instance.Name & Extension, Payload)
            Next
            If Not Extras.ContainsKey(Instance.Name & ".json") Then Throw New IOException("新版本游戏组件未生成。")
            Dim ExistingConfig = Path.Combine(Instance.PathVersion, "PCL", "config.json")
            InstanceResourceStore.EnsureWithin(Instance.PathVersion, ExistingConfig)
            Dim Config = If(File.Exists(ExistingConfig), JObject.Parse(File.ReadAllText(ExistingConfig)), New JObject)
            Dim NewConfig = JObject.Parse(File.ReadAllText(Path.Combine(BuildFolder, "PCL", "config.json")))
            Config("MinecraftInstallManifest") = NewConfig("MinecraftInstallManifest")?.DeepClone()
            Dim ConfigPayload = Path.Combine(Stage, "updated-config.json")
            File.WriteAllText(ConfigPayload, Config.ToString())
            Extras.Add("PCL/config.json", ConfigPayload)
        End If
        Token.ThrowIfCancellationRequested()
        If HasRunningMinecraft OrElse QuickPlayWorlds.HasRunningSessions() OrElse McLaunchLoader.State = LoadState.Loading Then Throw New IOException("游戏已开始运行，未替换文件。")
        For Each Pair In RuntimeBefore
            If ModpackUpdate.Digest(ModpackUpdate.Target(Instance.PathVersion, Pair.Key)) <> Pair.Value Then Throw New IOException("游戏组件或版本设置在预览后发生变化，请重新检查更新。")
        Next
        Task.Progress = 0.85
        EnvironmentHistory.CaptureInstance(Instance, "整合包更新前", Token)
        Dim Backup = ModpackUpdate.Commit(Plan, Instance.PathVersion, Token, Extras)
        Task.Output = Backup
        RunInUiWait(Sub()
                        Dim Refreshed = New McInstance(Instance.PathVersion).Load()
                        If PageInstanceLeft.Instance IsNot Nothing AndAlso PathUtils.IsSame(PageInstanceLeft.Instance.PathVersion, Instance.PathVersion) Then PageInstanceLeft.Instance = Refreshed
                        If McInstanceSelected IsNot Nothing AndAlso PathUtils.IsSame(McInstanceSelected.PathVersion, Instance.PathVersion) Then McInstanceSelected = Refreshed
                    End Sub)
        Logger.Info("整合包更新完成，原文件备份：" & Backup)
    End Sub
End Class
