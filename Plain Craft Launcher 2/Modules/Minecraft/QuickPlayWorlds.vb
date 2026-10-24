Imports System.IO
Imports System.Globalization

Friend Class QuickPlaySession
    Friend Id As String = Guid.NewGuid().ToString("N")
    Friend VersionPath As String
    Friend GameFolder As String
    Friend LogFile As String
End Class

Friend Class RecentWorld
    Friend VersionPath As String
    Friend GameFolder As String
    Friend Identifier As String
    Friend Name As String
    Friend PlayedUtc As Date
End Class

Friend Module QuickPlayWorlds
    Private Const HistoryName As String = "recent-worlds.json"
    Friend Function Supported(Instance As McInstance) As Boolean
        Return Instance IsNot Nothing AndAlso Instance.Version.Valid AndAlso Instance.Version.Vanilla.Major >= 20 AndAlso
            (Not Instance.Version.IsSnapshot OrElse Instance.ReleaseTime.ToUniversalTime() >= New Date(2023, 4, 5, 0, 0, 0, DateTimeKind.Utc))
    End Function
    Friend Function WorldPath(GameFolder As String, Identifier As String) As String
        If String.IsNullOrWhiteSpace(Identifier) OrElse Identifier = "." OrElse Identifier = ".." OrElse Path.GetFileName(Identifier) <> Identifier OrElse Identifier.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 Then Throw New IOException("存档文件夹名称无效。")
        Dim Root = Path.Combine(GameFolder, "saves"), World = Path.Combine(Root, Identifier)
        InstanceResourceStore.EnsureWithin(Root, World)
        If Not Directory.Exists(World) OrElse Not File.Exists(Path.Combine(World, "level.dat")) Then Throw New IOException("该存档已移动、删除或尚未完整生成，请刷新存档列表。")
        Return World
    End Function
    Friend Function Start(Instance As McInstance, Identifier As String) As Boolean
        Try
            If Instance Is Nothing OrElse Not ModModpackMode.AllowsInstance(Instance.PathVersion) Then Throw New IOException("请使用当前整合包目录中的游戏版本。")
            Instance.Load()
            If Not Supported(Instance) Then Throw New IOException("直接进入存档需要支持 Quick Play 的 Minecraft Java 1.20 或更新版本。")
            WorldPath(Instance.PathIndie, Identifier)
            If HasRunningMinecraft Then Throw New IOException("请先关闭正在运行的游戏，再直接进入存档。")
            FrmMain.PageChange(FormMain.PageType.Launch)
            Return McLaunchStart(New McLaunchOptions With {.Instance = Instance, .ServerIp = "", .QuickPlayWorld = Identifier})
        Catch ex As Exception
            Hint(ex.Message, HintType.Red)
            Return False
        End Try
    End Function
    Friend Function Prepare(Instance As McInstance, Identifier As String, Arguments As List(Of String), SaveBatch As Boolean) As QuickPlaySession
        If Identifier IsNot Nothing Then
            If Not Supported(Instance) Then Throw New IOException("该 Minecraft 版本不支持 Quick Play。")
            WorldPath(Instance.PathIndie, Identifier)
            RemoveArguments(Arguments, {"--quickPlaySingleplayer", "--quickPlayMultiplayer", "--quickPlayRealms", "--server", "--port"})
            Arguments.Add("--quickPlaySingleplayer")
            Arguments.Add(Identifier)
        End If
        If Not Supported(Instance) OrElse Not Settings.Get(Of Boolean)("LaunchRecentWorlds") OrElse SaveBatch Then Return Nothing
        '尊重用户手动指定的记录路径，不抢占其他启动工具的日志。
        If Arguments.Contains("--quickPlayPath") Then Return Nothing
        Try
            Dim Session As New QuickPlaySession With {.VersionPath = Instance.PathVersion, .GameFolder = Instance.PathIndie}
            Session.LogFile = LocalGameData.Address("quickplay-logs/" & Session.Id & ".json")
            Directory.CreateDirectory(Path.GetDirectoryName(Session.LogFile))
            Arguments.Add("--quickPlayPath")
            Arguments.Add(Session.LogFile)
            Return Session
        Catch ex As Exception
            Logger.Warn(ex, "无法记录最近存档，不影响本次启动")
            Return Nothing
        End Try
    End Function
    Private Sub RemoveArguments(Arguments As List(Of String), Names As IEnumerable(Of String))
        Dim SetNames As New HashSet(Of String)(Names, StringComparer.Ordinal)
        Dim Index = 0
        Do While Index < Arguments.Count
            If SetNames.Contains(Arguments(Index)) Then
                Arguments.RemoveAt(Index)
                If Index < Arguments.Count AndAlso Not Arguments(Index).StartsWith("--", StringComparison.Ordinal) Then Arguments.RemoveAt(Index)
            Else
                Index += 1
            End If
        Loop
    End Sub
    Friend Sub Track(Session As QuickPlaySession, Game As Process)
        If Session Is Nothing Then Return
        SyncLock LocalGameData.Gate
            LocalGameData.WriteJson("quickplay-sessions/" & Session.Id & ".json", New JObject(New JProperty("versionPath", Session.VersionPath), New JProperty("gameFolder", Session.GameFolder), New JProperty("logFile", Session.LogFile), New JProperty("pid", Game.Id), New JProperty("started", Game.StartTime.ToUniversalTime().Ticks)))
        End SyncLock
    End Sub
    Friend Sub Finish(Session As QuickPlaySession)
        If Session Is Nothing Then Return
        SyncLock LocalGameData.Gate
        Try
            ReadSessionLog(Session)
        Finally
            Dim Journal = LocalGameData.Address("quickplay-sessions/" & Session.Id & ".json")
            If File.Exists(Journal) Then File.Delete(Journal)
            If File.Exists(Session.LogFile) Then
                InstanceResourceStore.EnsureWithin(LocalGameData.Address("quickplay-logs"), Session.LogFile)
                File.Delete(Session.LogFile)
            End If
        End Try
        End SyncLock
    End Sub
    Private Sub ReadSessionLog(Session As QuickPlaySession)
        If Not Settings.Get(Of Boolean)("LaunchRecentWorlds") OrElse Not File.Exists(Session.LogFile) Then Return
        InstanceResourceStore.EnsureWithin(LocalGameData.Address("quickplay-logs"), Session.LogFile)
        If New FileInfo(Session.LogFile).Length > 1024 * 1024 Then Throw New IOException("Quick Play 记录过大。")
        Dim Json = JToken.Parse(File.ReadAllText(Session.LogFile)), Container = TryCast(Json, JContainer)
        If Container Is Nothing Then Return
        Dim Objects = Container.Descendants().OfType(Of JObject)().ToList()
        If TypeOf Container Is JObject Then Objects.Insert(0, DirectCast(Container, JObject))
        For Each Entry In Objects.Where(Function(Item) String.Equals(Item("type")?.ToString(), "singleplayer", StringComparison.OrdinalIgnoreCase))
            Dim Identifier = If(Entry("id")?.ToString(), Entry("identifier")?.ToString())
            Try
                WorldPath(Session.GameFolder, Identifier)
                Dim Played = File.GetLastWriteTimeUtc(Session.LogFile), Parsed As DateTimeOffset
                If DateTimeOffset.TryParse(Entry("lastPlayedTime")?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, Parsed) Then Played = Parsed.UtcDateTime
                Remember(New RecentWorld With {.VersionPath = Session.VersionPath, .GameFolder = Session.GameFolder, .Identifier = Identifier, .Name = If(Entry("name")?.ToString(), Identifier), .PlayedUtc = Played})
            Catch ex As IOException
                Logger.Warn(ex, "忽略不可用的最近存档记录")
            End Try
        Next
    End Sub
    Friend Sub Remember(World As RecentWorld)
        SyncLock LocalGameData.Gate
            If ReadEntries().Any(Function(Item) PathUtils.IsSame(Item.VersionPath, World.VersionPath) AndAlso Item.Identifier = World.Identifier AndAlso (Item.PlayedUtc > World.PlayedUtc OrElse (Item.PlayedUtc = World.PlayedUtc AndAlso Item.Name = World.Name))) Then Return
            Dim Existing = ReadEntries().Where(Function(Item) Not (PathUtils.IsSame(Item.VersionPath, World.VersionPath) AndAlso Item.Identifier = World.Identifier)).ToList()
            Existing.Add(World)
            Dim Values As New JArray(Existing.OrderByDescending(Function(Item) Item.PlayedUtc).Take(40).Select(Function(Item) New JObject(New JProperty("versionPath", Item.VersionPath), New JProperty("gameFolder", Item.GameFolder), New JProperty("identifier", Item.Identifier), New JProperty("name", Item.Name), New JProperty("playedUtc", Item.PlayedUtc))))
            LocalGameData.WriteJson(HistoryName, New JObject(New JProperty("version", 1), New JProperty("worlds", Values)))
        End SyncLock
    End Sub
    Friend Sub DiscoverExistingWorlds(Instance As McInstance)
        If Not Settings.Get(Of Boolean)("LaunchRecentWorlds") OrElse Not Supported(Instance) Then Return
        '只有已确定的隔离目录能可靠关联版本；共享目录仍以真实 Quick Play 记录为准。
        If Not Settings.HasSaved("VersionArgumentIndieV2", Instance:=Instance) OrElse Not Settings.Get(Of Boolean)("VersionArgumentIndieV2", Instance:=Instance) Then Return
        Dim Saves = Path.Combine(Instance.PathVersion, "saves")
        If Not Directory.Exists(Saves) Then Return
        For Each Folder In Directory.EnumerateDirectories(Saves)
            Try
                InstanceResourceStore.EnsureWithin(Saves, Folder)
                Dim Info = WorldMetadata.Read(Folder)
                If Info Is Nothing Then Continue For
                Remember(New RecentWorld With {.VersionPath = Instance.PathVersion, .GameFolder = Instance.PathVersion, .Identifier = Path.GetFileName(Folder), .Name = If(String.IsNullOrWhiteSpace(Info.Name), Path.GetFileName(Folder), Info.Name), .PlayedUtc = Info.LastPlayedUtc})
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException
                '损坏或仍在写入的单个存档不影响其他记录，不修改任何存档文件。
            End Try
        Next
    End Sub
    Private Function ReadEntries() As List(Of RecentWorld)
        Dim Values = TryCast(LocalGameData.ReadJson(HistoryName)("worlds"), JArray)
        Dim Result As New List(Of RecentWorld)
        For Each Item In If(Values, New JArray).OfType(Of JObject)()
            Try
                Result.Add(New RecentWorld With {.VersionPath = Item("versionPath").ToString(), .GameFolder = Item("gameFolder").ToString(), .Identifier = Item("identifier").ToString(), .Name = Item("name").ToString(), .PlayedUtc = Item("playedUtc").ToObject(Of Date)().ToUniversalTime()})
            Catch
                '单个旧记录无效时仍保留其他记录。
            End Try
        Next
        Return Result
    End Function
    Friend Function Recent() As List(Of RecentWorld)
        Dim Result As New List(Of RecentWorld)
        If Not Settings.Get(Of Boolean)("LaunchRecentWorlds") Then Return Result
        SyncLock LocalGameData.Gate
            For Each World In ReadEntries().OrderByDescending(Function(Item) Item.PlayedUtc)
                Try
                    If Not Directory.Exists(World.VersionPath) OrElse Not ModModpackMode.AllowsInstance(World.VersionPath) Then Continue For
                    WorldPath(World.GameFolder, World.Identifier)
                    Result.Add(World)
                Catch
                    '删除或移动整合包后不报错，不创建缺失目录。
                End Try
                If Result.Count = 5 Then Exit For
            Next
        End SyncLock
        Return Result
    End Function
    Friend Sub RecoverExitedSessions()
        Dim Folder = LocalGameData.Address("quickplay-sessions")
        If Not Directory.Exists(Folder) Then Return
        SyncLock LocalGameData.Gate
        For Each FileName In Directory.EnumerateFiles(Folder, "*.json")
            Try
                Dim Json = LocalGameData.ReadJson("quickplay-sessions/" & Path.GetFileName(FileName))
                Dim Session As New QuickPlaySession With {.Id = Path.GetFileNameWithoutExtension(FileName), .VersionPath = Json("versionPath").ToString(), .GameFolder = Json("gameFolder").ToString(), .LogFile = Json("logFile").ToString()}
                If LocalGameData.ProcessMatches(Json("pid").ToObject(Of Integer)(), Json("started").ToObject(Of Long)()) Then
                    ReadSessionLog(Session)
                Else
                    Finish(Session)
                End If
            Catch ex As Exception
                Logger.Warn(ex, "恢复最近存档记录失败")
            End Try
        Next
        End SyncLock
    End Sub
    Friend Sub OnEnabledChanged(Value As Object)
        RunInUi(Sub() FrmLaunchRight?.RefreshRecentWorlds())
    End Sub
    Friend Function HasRunningSessions() As Boolean
        Dim Folder = LocalGameData.Address("quickplay-sessions")
        If Not Directory.Exists(Folder) Then Return False
        SyncLock LocalGameData.Gate
            For Each FileName In Directory.EnumerateFiles(Folder, "*.json")
                Try
                    Dim Json = LocalGameData.ReadJson("quickplay-sessions/" & Path.GetFileName(FileName))
                    If LocalGameData.ProcessMatches(Json("pid").ToObject(Of Integer)(), Json("started").ToObject(Of Long)()) Then Return True
                Catch ex As Exception
                    Logger.Warn(ex, "读取游戏运行状态失败")
                End Try
            Next
        End SyncLock
        Return False
    End Function
End Module
