Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions

Friend NotInheritable Class InstanceCloneOptions
    Public CopySaves As Boolean
    Public CopyScreenshots As Boolean
    Public CopyServers As Boolean
End Class

Friend NotInheritable Class InstanceCloneResult
    Public Name As String
    Public VersionFolder As String
    Public FileCount As Integer
    Public Bytes As Long
End Class

Friend NotInheritable Class InstanceCloneMigrationChange
    Public Relative As String
    Public Kind As String
End Class

Friend NotInheritable Class InstanceCloneMigrationPlan
    Public SourceVersionFolder As String
    Public SourceGameRoot As String
    Public CloneVersionFolder As String
    Public OldMinecraft As String
    Public NewMinecraft As String
    Public Changes As New List(Of InstanceCloneMigrationChange)
    Friend SourceFiles As Dictionary(Of String, String)
    Friend CloneFiles As Dictionary(Of String, String)
    Friend OriginHash As String
End Class

'复制采用独立文件和同卷临时目录；提交前重新校验来源，绝不重命名或修改来源文件。
Friend NotInheritable Class InstanceCloner
    Friend Const OriginName As String = "PCL/CloneOrigin.json"
    Private Shared ReadOnly Gate As New Object
    Private Const MaxFiles As Integer = 200000
    Private Const MaxBytes As Long = 512L * 1024 * 1024 * 1024
    Private Shared ReadOnly EnvironmentFolders As String() = {"mods", "coremods", "config", "defaultconfigs", "configureddefaults", "kubejs", "scripts", "resources", "resourcepacks", "shaderpacks", "texturepacks", "schematics", "global_packs", "openloader"}
    Private Shared ReadOnly SetupKeys As New HashSet(Of String)({"VersionRamType", "VersionRamCustom", "VersionRamOptimize", "VersionArgumentTitle", "VersionArgumentInfo", "VersionArgumentJavaSelect", "VersionArgumentJavaV2", "VersionArgumentJavaRange", "VersionAdvanceAssets", "VersionAdvanceAssetsV2", "VersionAdvanceDisableJLW", "VersionAdvanceDisableLUA", "VersionAdvanceDisableModUpdate", "VersionAdvanceGC", "CustomInfo", "Logo", "LogoCustom"}, StringComparer.Ordinal)

    Friend Shared Function NormalizeRoot(Value As String) As String
        If String.IsNullOrWhiteSpace(Value) Then Throw New IOException("未指定游戏目录。")
        Dim Full = Path.GetFullPath(PathUtils.RemoveExtendedPrefix(Value))
        If Full.Length > Path.GetPathRoot(Full).Length Then Full = Full.TrimEnd("\"c, "/"c)
        EnsureNoLinks(Full)
        Return Full
    End Function
    Friend Shared Sub EnsureNoLinks(Value As String)
        Dim Current = Path.GetFullPath(PathUtils.RemoveExtendedPrefix(Value))
        Do While Not String.IsNullOrEmpty(Current)
            If (File.Exists(Current) OrElse Directory.Exists(Current)) AndAlso (File.GetAttributes(Current) And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("目录包含链接，已停止操作，请改用普通独立目录。")
            Dim Parent = Path.GetDirectoryName(Current)
            If String.Equals(Parent, Current, StringComparison.OrdinalIgnoreCase) Then Exit Do
            Current = Parent
        Loop
    End Sub
    Private Shared Function Target(Root As String, Relative As String) As String
        Dim Full = Path.Combine(Root, ModpackUpdate.RelativePath(Relative).Replace("/"c, Path.DirectorySeparatorChar))
        InstanceResourceStore.EnsureWithin(Root, Full)
        EnsureNoLinks(Full)
        Return Full
    End Function
    Private Shared Function MinecraftRoot(VersionFolder As String) As String
        Dim Versions = Path.GetDirectoryName(VersionFolder)
        If Not String.Equals(Path.GetFileName(Versions), "versions", StringComparison.OrdinalIgnoreCase) Then Throw New IOException("版本必须位于 Minecraft 的 versions 目录中。")
        Return NormalizeRoot(Path.GetDirectoryName(Versions))
    End Function
    Private Shared Sub ValidateRoots(VersionFolder As String, GameRoot As String)
        If Not Directory.Exists(VersionFolder) Then Throw New DirectoryNotFoundException("找不到原版本目录。")
        Dim McRoot = MinecraftRoot(VersionFolder)
        If Not PathUtils.IsSame(GameRoot, VersionFolder) AndAlso Not PathUtils.IsSame(GameRoot, McRoot) Then Throw New IOException("当前游戏目录不属于此 Minecraft 文件夹。")
    End Sub
    Private Shared Sub AssertStopped(ParamArray GameFolders As String())
        If HasRunningMinecraft OrElse QuickPlayWorlds.HasRunningSessions() OrElse McLaunchLoader.State = LoadState.Loading OrElse WorldBackups.IsBusy Then Throw New IOException("请先关闭游戏并等待启动或存档备份任务完成。")
        For Each Folder In GameFolders
            If Not String.IsNullOrWhiteSpace(Folder) AndAlso WorldBackups.HasActiveGame(Folder) Then Throw New IOException("该游戏目录仍有运行中的游戏，请关闭后再克隆或迁移。")
        Next
    End Sub
    '读取隔离设置而不调用可能自动写入 Setup.ini 的 PathIndie 属性。
    Friend Shared Function GameRootForInstance(Instance As McInstance) As String
        If Instance Is Nothing Then Throw New ArgumentNullException(NameOf(Instance))
        Dim Folder = NormalizeRoot(Instance.PathVersion), McRoot = MinecraftRoot(Folder)
        Dim SetupFile = Target(Folder, "PCL/Setup.ini"), Values As New Dictionary(Of String, String)(StringComparer.Ordinal)
        If File.Exists(SetupFile) Then
            For Each Line In ReadSmallText(SetupFile, 1024 * 1024).Split({vbCrLf, vbLf}, StringSplitOptions.None)
                Dim Split = Line.IndexOf(":"c)
                If Split > 0 Then Values(Line.Substring(0, Split)) = Line.Substring(Split + 1)
            Next
        End If
        Dim Raw As String = Nothing, Isolated As Boolean, Old As Integer
        If Values.TryGetValue("VersionArgumentIndieV2", Raw) AndAlso Boolean.TryParse(Raw, Isolated) Then Return If(Isolated, Folder, McRoot)
        If Values.TryGetValue("VersionArgumentIndie", Raw) AndAlso Integer.TryParse(Raw, Old) AndAlso Old > 0 Then Return If(Old = 1, Folder, McRoot)
        Dim Mods = Path.Combine(Folder, "mods"), Saves = Path.Combine(Folder, "saves")
        EnsureNoLinks(Mods) : EnsureNoLinks(Saves)
        If (Directory.Exists(Mods) AndAlso Directory.EnumerateFiles(Mods).Any()) OrElse
           (Directory.Exists(Saves) AndAlso Directory.EnumerateDirectories(Saves).Any()) Then Return Folder
        Dim IsRelease = Not Instance.Version.IsFool AndAlso Not Instance.Version.IsSnapshot AndAlso Instance.Version.VanillaName <> "Old"
        Dim Modable = Instance.Version.ModLoaders.Flags.Any OrElse Instance.DisplayType = McInstanceCardType.API
        Select Case Settings.Get(Of Integer)("LaunchArgumentIndieV2")
            Case 0 : Isolated = False
            Case 1 : Isolated = Modable
            Case 2 : Isolated = Not IsRelease
            Case 3 : Isolated = Not IsRelease OrElse Modable
            Case Else : Isolated = True
        End Select
        Return If(Isolated, Folder, McRoot)
    End Function
    Private Shared Function ReadSmallText(Address As String, Limit As Long) As String
        EnsureNoLinks(Address)
        If New FileInfo(Address).Length > Limit Then Throw New IOException("环境描述文件过大。")
        Return File.ReadAllText(Address)
    End Function
    Private Shared Function JsonFile(VersionFolder As String) As String
        Dim Exact = Target(VersionFolder, Path.GetFileName(VersionFolder) & ".json")
        If File.Exists(Exact) Then Return Exact
        For Each Candidate In Directory.EnumerateFiles(VersionFolder, "*.json").Take(128)
            Dim Data = JObject.Parse(ReadSmallText(Candidate, 16 * 1024 * 1024))
            If Data("mainClass") IsNot Nothing AndAlso Data("id") IsNot Nothing AndAlso Data("type") IsNot Nothing Then Return Candidate
        Next
        Throw New IOException("没有找到有效的 Minecraft 版本 JSON。")
    End Function
    Private Shared Function Excluded(Relative As String) As Boolean
        Dim Parts = Relative.Replace("\", "/").ToLowerInvariant().Split("/"c)
        Return Parts.Any(Function(Part) Part.StartsWith(".pcl-", StringComparison.Ordinal) OrElse {".cache", "cache", "caches", ".connector", "logs", "crash-reports", "natives", "backups", "backup", "account", "accounts", ".git"}.Contains(Part)) OrElse
            Parts.Any(Function(Part) Regex.IsMatch(Part, "\A(?:accounts?|credentials?|access[_-]?tokens?|auth[_-]?tokens?|launcher_accounts|usercache|usernamecache|launcher_profiles)(?:\.|$)", RegexOptions.IgnoreCase)) OrElse
            Relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) OrElse Relative.EndsWith(".log", StringComparison.OrdinalIgnoreCase) OrElse Relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
    End Function
    Private Shared Sub Walk(Root As String, Folder As String, Prefix As String, Result As Dictionary(Of String, String), Token As CancellationToken, Optional Depth As Integer = 0)
        Token.ThrowIfCancellationRequested()
        If Depth > 64 Then Throw New IOException("游戏目录嵌套过深。")
        EnsureNoLinks(Folder)
        If Not Directory.Exists(Folder) Then Return
        For Each Address In Directory.EnumerateFileSystemEntries(Folder)
            Token.ThrowIfCancellationRequested()
            InstanceResourceStore.EnsureWithin(Root, Address)
            Dim Relative = Address.Substring(Root.Length + 1).Replace("\", "/")
            If Excluded(Relative) Then Continue For
            EnsureNoLinks(Address)
            If Directory.Exists(Address) Then
                Walk(Root, Address, Prefix, Result, Token, Depth + 1)
            Else
                Result.Add(Prefix & Relative, Address)
                If Result.Count > MaxFiles Then Throw New IOException("环境文件超过 200000 个，请先缩小复制范围。")
            End If
        Next
    End Sub
    Private Shared Function Inventory(VersionFolder As String, GameRoot As String, Options As InstanceCloneOptions, Token As CancellationToken) As Dictionary(Of String, String)
        Dim Result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase), PrimaryJson = JsonFile(VersionFolder)
        Result.Add("v/" & Path.GetFileName(PrimaryJson), PrimaryJson)
        For Each Address In Directory.EnumerateFiles(VersionFolder, "*.jar")
            EnsureNoLinks(Address)
            Result.Add("v/" & Path.GetFileName(Address), Address)
        Next
        For Each Name In {"PCL/Setup.ini", "PCL/config.json", "PCL/Logo.png"}
            Dim Address = Target(VersionFolder, Name)
            If File.Exists(Address) Then Result.Add("v/" & Name, Address)
        Next
        For Each Name In EnvironmentFolders.Concat(If(Options.CopySaves, {"saves"}, New String() {})).Concat(If(Options.CopyScreenshots, {"screenshots"}, New String() {}))
            Walk(GameRoot, Path.Combine(GameRoot, Name), "g/", Result, Token)
        Next
        For Each Address In Directory.EnumerateFiles(GameRoot)
            Token.ThrowIfCancellationRequested()
            Dim Name = Path.GetFileName(Address).ToLowerInvariant()
            If Name = "options.txt" OrElse Name.StartsWith("optionsof") OrElse Name.StartsWith("optionsshaders") OrElse
                {".cfg", ".toml", ".properties"}.Contains(Path.GetExtension(Name)) OrElse (Options.CopyServers AndAlso {"servers.dat", "servers.dat_old"}.Contains(Name)) Then
                If Not Excluded(Name) Then
                    EnsureNoLinks(Address)
                    Result.Add("g/" & Path.GetFileName(Address), Address)
                End If
            End If
        Next
        If Result.Count > MaxFiles Then Throw New IOException("复制文件过多。")
        Return Result
    End Function
    Friend Shared Function Digest(Address As String, Token As CancellationToken) As String
        EnsureNoLinks(Address)
        Using Input As New FileStream(Address, FileMode.Open, FileAccess.Read, FileShare.Read), Hash = SHA1.Create()
            Dim Buffer(128 * 1024 - 1) As Byte
            Do
                Token.ThrowIfCancellationRequested()
                Dim Count = Input.Read(Buffer, 0, Buffer.Length)
                If Count = 0 Then Exit Do
                Hash.TransformBlock(Buffer, 0, Count, Nothing, 0)
            Loop
            Hash.TransformFinalBlock(New Byte() {}, 0, 0)
            Return "sha1:" & BitConverter.ToString(Hash.Hash).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Shared Function Fingerprints(Entries As Dictionary(Of String, String), Token As CancellationToken) As Dictionary(Of String, String)
        Dim Result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase), Bytes As Long
        For Each Pair In Entries
            Token.ThrowIfCancellationRequested()
            Bytes += New FileInfo(Pair.Value).Length
            If Bytes > MaxBytes Then Throw New IOException("复制范围超过 512 GiB，请先缩小复制范围。")
            Result.Add(Pair.Key, Digest(Pair.Value, Token))
        Next
        Return Result
    End Function
    Private Shared Sub Verify(Expected As Dictionary(Of String, String), Actual As Dictionary(Of String, String))
        If Expected.Count <> Actual.Count OrElse Expected.Any(Function(Pair) Not Actual.ContainsKey(Pair.Key) OrElse Actual(Pair.Key) <> Pair.Value) Then Throw New IOException("操作期间环境目录发生变化，请等待修改完成后重试。")
    End Sub
    Private Shared Sub CopyFile(Source As String, Destination As String, ExpectedHash As String, Token As CancellationToken)
        EnsureNoLinks(Source)
        EnsureNoLinks(Destination)
        Directory.CreateDirectory(Path.GetDirectoryName(Destination))
        Using Input As New FileStream(Source, FileMode.Open, FileAccess.Read, FileShare.Read), Output As New FileStream(Destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            Dim Buffer(128 * 1024 - 1) As Byte
            Do
                Token.ThrowIfCancellationRequested()
                Dim Count = Input.Read(Buffer, 0, Buffer.Length)
                If Count = 0 Then Exit Do
                Output.Write(Buffer, 0, Count)
            Loop
            Output.Flush(True)
        End Using
        If Digest(Destination, Token) <> ExpectedHash Then Throw New IOException("复制期间来源文件发生变化，未生成测试副本。")
        File.SetLastWriteTimeUtc(Destination, File.GetLastWriteTimeUtc(Source))
    End Sub
    Private Shared Sub DeleteStage(Parent As String, Stage As String)
        If Not Directory.Exists(Stage) Then Return
        InstanceResourceStore.EnsureWithin(Parent, Stage)
        EnsureNoLinks(Stage)
        If Not Regex.IsMatch(Path.GetFileName(Stage), "\A\.pcl-clone-[a-f0-9]{32}\z") Then Throw New IOException("无效的克隆临时目录。")
        '清理前逐项检查，避免删除外部注入的链接目标。
        Dim Pending As New Stack(Of String), Folders As New List(Of String)
        Pending.Push(Stage)
        While Pending.Count > 0
            Dim Folder = Pending.Pop()
            EnsureNoLinks(Folder)
            Folders.Add(Folder)
            For Each Item In Directory.EnumerateFileSystemEntries(Folder)
                EnsureNoLinks(Item)
                If Directory.Exists(Item) Then Pending.Push(Item) Else File.Delete(Item)
            Next
        End While
        For Each Folder In Folders.AsEnumerable().Reverse()
            Directory.Delete(Folder, False)
        Next
    End Sub
    Private Shared Sub RewritePaths(Data As JContainer, OldFolder As String, NewFolder As String)
        For Each Value In Data.DescendantsAndSelf().OfType(Of JValue)().Where(Function(Item) Item.Type = JTokenType.String)
            Dim Text = CStr(Value.Value)
            '仅替换完整的目录前缀；不修改模组、存档或任意文件的正文。
            Text = Text.Replace(OldFolder & "\", NewFolder & "\").Replace(OldFolder.Replace("\", "/") & "/", NewFolder.Replace("\", "/") & "/")
            If Text = OldFolder Then Text = NewFolder
            Value.Value = Text
        Next
    End Sub
    Private Shared Sub PrepareVersion(Stage As String, OldFolder As String, NewFolder As String, OldJsonName As String, NewName As String)
        Dim JsonAddress = Target(Stage, OldJsonName), Data = JObject.Parse(ReadSmallText(JsonAddress, 16 * 1024 * 1024)), OldName = Path.GetFileName(OldFolder)
        Dim Inherit = Data("inheritsFrom")?.ToString()
        If Inherit = OldName Then Throw New IOException("版本 JSON 包含自引用继承关系，无法安全克隆。")
        If Inherit IsNot Nothing Then InstanceResourceStore.SafeName(Inherit)
        If Data("jar") IsNot Nothing Then
            Dim JarName = Data("jar").ToString()
            InstanceResourceStore.SafeName(JarName)
            If JarName = OldName Then Data("jar") = NewName
        End If
        Data("id") = NewName
        RewritePaths(Data, OldFolder, NewFolder)
        File.Delete(JsonAddress)
        File.WriteAllText(Target(Stage, NewName & ".json"), Data.ToString(), New UTF8Encoding(False))
        Dim OldJar = Target(Stage, OldName & ".jar"), NewJar = Target(Stage, NewName & ".jar")
        If File.Exists(OldJar) AndAlso Not PathUtils.IsSame(OldJar, NewJar) Then File.Move(OldJar, NewJar)
        Dim SetupAddress = Target(Stage, "PCL/Setup.ini"), Setup As New StringBuilder
        If File.Exists(SetupAddress) Then
            For Each Line In ReadSmallText(SetupAddress, 1024 * 1024).Split({vbCrLf, vbLf}, StringSplitOptions.None)
                Dim Separator = Line.IndexOf(":"c)
                If Separator > 0 AndAlso SetupKeys.Contains(Line.Substring(0, Separator)) Then Setup.AppendLine(Line.Replace(OldFolder & "\", NewFolder & "\"))
            Next
        End If
        Setup.AppendLine("VersionArgumentIndie:1").AppendLine("VersionArgumentIndieV2:True").AppendLine("IsStar:False").AppendLine("CustomInfo:测试副本 · 来自 " & OldName)
        Directory.CreateDirectory(Path.GetDirectoryName(SetupAddress))
        File.WriteAllText(SetupAddress, Setup.ToString(), New UTF8Encoding(False))
        Dim ConfigAddress = Target(Stage, "PCL/config.json")
        If File.Exists(ConfigAddress) Then
            Dim Config = JObject.Parse(ReadSmallText(ConfigAddress, 16 * 1024 * 1024)), Safe As New JObject
            For Each Entry In Config.Properties().Where(Function(Item) Item.Name.Equals("MinecraftInstallManifest", StringComparison.OrdinalIgnoreCase))
                Safe(Entry.Name) = Entry.Value.DeepClone()
            Next
            RewritePaths(Safe, OldFolder, NewFolder)
            File.WriteAllText(ConfigAddress, Safe.ToString(), New UTF8Encoding(False))
        End If
    End Sub
    Friend Shared Function CloneInstance(Instance As McInstance, NewName As String, Options As InstanceCloneOptions, Token As CancellationToken) As InstanceCloneResult
        Return Clone(Instance.PathVersion, GameRootForInstance(Instance), NewName, Options, Token)
    End Function
    Friend Shared Function Clone(VersionFolder As String, GameRoot As String, NewName As String, Options As InstanceCloneOptions, Token As CancellationToken,
        Optional AfterFileCopied As Action(Of Integer) = Nothing) As InstanceCloneResult
        SyncLock Gate
            AssertStopped(GameRoot)
            Token.ThrowIfCancellationRequested()
            VersionFolder = NormalizeRoot(VersionFolder) : GameRoot = NormalizeRoot(GameRoot)
            ValidateRoots(VersionFolder, GameRoot)
            NewName = InstanceResourceStore.SafeName(NewName)
            If NewName.Length > 100 OrElse NewName.IndexOfAny({"!"c, ";"c}) >= 0 OrElse Regex.IsMatch(NewName, "\A(?:COM0|LPT0|CLOCK\$)(?:\.|$)|.{2,}~\d", RegexOptions.IgnoreCase) Then Throw New IOException("测试版本名称包含 Minecraft 不支持的字符。")
            Options = If(Options, New InstanceCloneOptions)
            Dim Parent = Path.GetDirectoryName(VersionFolder), Destination = Path.Combine(Parent, NewName), Stage = Path.Combine(Parent, ".pcl-clone-" & Guid.NewGuid().ToString("N"))
            EnsureNoLinks(Destination)
            If File.Exists(Destination) OrElse Directory.Exists(Destination) OrElse PathUtils.IsSame(VersionFolder, Destination) Then Throw New IOException("同名版本已存在，请使用新的测试版本名称。")
            If ModpackUpdate.Pending(VersionFolder).Any() Then Throw New IOException("原版本有未完成的环境修改，请先完成恢复后再克隆。")
            Dim Entries = Inventory(VersionFolder, GameRoot, Options, Token), Before = Fingerprints(Entries, Token), Count As Integer, Bytes As Long
            Directory.CreateDirectory(Stage)
            Try
                For Each Pair In Entries
                    Token.ThrowIfCancellationRequested()
                    Dim Relative = Pair.Key.Substring(2), TargetFile = Target(Stage, Relative)
                    CopyFile(Pair.Value, TargetFile, Before(Pair.Key), Token)
                    Count += 1 : Bytes += New FileInfo(TargetFile).Length
                    AfterFileCopied?.Invoke(Count)
                Next
                Dim OriginalJson = Path.GetFileName(JsonFile(VersionFolder))
                PrepareVersion(Stage, VersionFolder, Destination, OriginalJson, NewName)
                Dim Origin As New JObject(New JProperty("schema", 1), New JProperty("sourceVersionFolder", VersionFolder), New JProperty("sourceGameRoot", GameRoot), New JProperty("sourceName", Path.GetFileName(VersionFolder)), New JProperty("cloneName", NewName), New JProperty("created", Date.UtcNow))
                File.WriteAllText(Target(Stage, OriginName), Origin.ToString(), New UTF8Encoding(False))
                AssertStopped(GameRoot)
                Verify(Before, Fingerprints(Inventory(VersionFolder, GameRoot, Options, Token), Token))
                Token.ThrowIfCancellationRequested()
                EnsureNoLinks(Parent) : EnsureNoLinks(Stage) : EnsureNoLinks(Destination)
                If File.Exists(Destination) OrElse Directory.Exists(Destination) Then Throw New IOException("目标名称已被其他操作占用。")
                Directory.Move(Stage, Destination)
                Return New InstanceCloneResult With {.Name = NewName, .VersionFolder = PathUtils.AddSlashSuffix(Destination), .FileCount = Count, .Bytes = Bytes}
            Finally
                DeleteStage(Parent, Stage)
            End Try
        End SyncLock
    End Function

    Private Shared Function ReadOrigin(CloneFolder As String) As JObject
        Dim Data = JObject.Parse(ReadSmallText(Target(CloneFolder, OriginName), 1024 * 1024))
        If CInt(Data("schema")) <> 1 OrElse Data("sourceVersionFolder") Is Nothing OrElse Data("sourceGameRoot") Is Nothing Then Throw New IOException("测试副本来源记录无效。")
        Dim Source = NormalizeRoot(Data("sourceVersionFolder").ToString()), Root = NormalizeRoot(Data("sourceGameRoot").ToString())
        ValidateRoots(Source, Root)
        If PathUtils.IsSame(Source, CloneFolder) OrElse Not PathUtils.IsSame(MinecraftRoot(Source), MinecraftRoot(CloneFolder)) OrElse Data("cloneName")?.ToString() <> Path.GetFileName(CloneFolder) Then Throw New IOException("副本已移动或重命名，无法自动迁移，请手动管理版本。")
        Return Data
    End Function
    Friend Shared Function SourceVersionFolder(CloneFolder As String) As String
        Return NormalizeRoot(ReadOrigin(NormalizeRoot(CloneFolder))("sourceVersionFolder").ToString())
    End Function
    Private Shared Function MigrationFiles(VersionFolder As String, GameRoot As String, Token As CancellationToken) As Dictionary(Of String, String)
        Dim All = Inventory(VersionFolder, GameRoot, New InstanceCloneOptions, Token)
        Return All.Where(Function(Pair) If(Pair.Key.StartsWith("g/", StringComparison.OrdinalIgnoreCase), EnvironmentHistory.IsGameFile(Pair.Key.Substring(2)),
            Not Pair.Key.Equals("v/PCL/Setup.ini", StringComparison.OrdinalIgnoreCase) AndAlso Not Pair.Key.Equals("v/PCL/Logo.png", StringComparison.OrdinalIgnoreCase))).ToDictionary(Function(Pair) Pair.Key, Function(Pair) Pair.Value, StringComparer.OrdinalIgnoreCase)
    End Function
    Private Shared Function MinecraftName(VersionFolder As String) As String
        Dim Data = JObject.Parse(ReadSmallText(JsonFile(VersionFolder), 16 * 1024 * 1024))
        '自有 jar 字段指向实例名称，不是 Minecraft 版本号。
        If Data("jar")?.ToString() = Path.GetFileName(VersionFolder) Then Data.Remove("jar")
        Dim JarMetadata As JObject = Nothing, JarAddress = Target(VersionFolder, Path.GetFileName(VersionFolder) & ".jar")
        If File.Exists(JarAddress) Then
            Try
                Using Archive = ZipFile.OpenRead(JarAddress)
                    Dim Entry = Archive.GetEntry("version.json")
                    If Entry IsNot Nothing AndAlso Entry.Length <= 1024 * 1024 Then
                        Using Reader As New StreamReader(Entry.Open())
                            JarMetadata = JObject.Parse(Reader.ReadToEnd())
                        End Using
                    End If
                End Using
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is Newtonsoft.Json.JsonException
            End Try
        End If
        Try
            Dim Version = New McVersion(Path.GetFileName(VersionFolder), Data, JarMetadata)
            If Version.VanillaName <> "Unknown" Then Return Version.VanillaName
        Catch
        End Try
        Dim ConfigAddress = Target(VersionFolder, "PCL/config.json")
        If File.Exists(ConfigAddress) Then
            Dim Config = JObject.Parse(ReadSmallText(ConfigAddress, 16 * 1024 * 1024)), Name = Config("MinecraftInstallManifest")?("Components")?("MinecraftVersion")?.ToString()
            If Not String.IsNullOrWhiteSpace(Name) AndAlso Name.Length <= 100 Then Return Name
        End If
        Return "未知版本"
    End Function
    Private Shared Function MapVersionKey(Key As String, OldFolder As String, NewFolder As String) As String
        If Not Key.StartsWith("v/", StringComparison.OrdinalIgnoreCase) Then Return Key
        If String.Equals(Key, "v/" & Path.GetFileName(JsonFile(OldFolder)), StringComparison.OrdinalIgnoreCase) Then Return "v/" & Path.GetFileName(JsonFile(NewFolder))
        If String.Equals(Key, "v/" & Path.GetFileName(OldFolder) & ".jar", StringComparison.OrdinalIgnoreCase) Then Return "v/" & Path.GetFileName(NewFolder) & ".jar"
        Return Key
    End Function
    Private Shared Function MigrationConfig(CloneConfig As String, SourceFolder As String) As JObject
        Dim SourceAddress = Target(SourceFolder, "PCL/config.json"), Result = If(File.Exists(SourceAddress), JObject.Parse(ReadSmallText(SourceAddress, 16 * 1024 * 1024)), New JObject)
        Dim Incoming = If(CloneConfig Is Nothing, New JObject, JObject.Parse(ReadSmallText(CloneConfig, 16 * 1024 * 1024)))
        For Each Existing In Result.Properties().Where(Function(Item) Item.Name.Equals("MinecraftInstallManifest", StringComparison.OrdinalIgnoreCase)).ToList()
            Existing.Remove()
        Next
        For Each Entry In Incoming.Properties().Where(Function(Item) Item.Name.Equals("MinecraftInstallManifest", StringComparison.OrdinalIgnoreCase))
            Result(Entry.Name) = Entry.Value.DeepClone()
        Next
        Return Result
    End Function
    Friend Shared Function PreviewMigration(CloneFolder As String, SourceGameRoot As String, Token As CancellationToken) As InstanceCloneMigrationPlan
        AssertStopped(CloneFolder)
        CloneFolder = NormalizeRoot(CloneFolder)
        Dim Origin = ReadOrigin(CloneFolder), Source = NormalizeRoot(Origin("sourceVersionFolder").ToString())
        SourceGameRoot = NormalizeRoot(SourceGameRoot)
        If Not PathUtils.IsSame(SourceGameRoot, Origin("sourceGameRoot").ToString()) Then Throw New IOException("原版本的隔离设置已改变，请手动检查游戏目录。")
        If ModpackUpdate.Pending(Source).Any() OrElse ModpackUpdate.Pending(CloneFolder).Any() Then Throw New IOException("版本有未完成的环境修改，请先恢复。")
        AssertStopped(SourceGameRoot, CloneFolder)
        Dim SourceEntries = MigrationFiles(Source, SourceGameRoot, Token), CloneEntries = MigrationFiles(CloneFolder, CloneFolder, Token)
        Dim Plan As New InstanceCloneMigrationPlan With {.SourceVersionFolder = Source, .SourceGameRoot = SourceGameRoot, .CloneVersionFolder = CloneFolder, .OldMinecraft = MinecraftName(Source), .NewMinecraft = MinecraftName(CloneFolder), .SourceFiles = Fingerprints(SourceEntries, Token), .CloneFiles = Fingerprints(CloneEntries, Token), .OriginHash = Digest(Target(CloneFolder, OriginName), Token)}
        Dim Incoming As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        For Each Pair In Plan.CloneFiles
            Incoming.Add(MapVersionKey(Pair.Key, CloneFolder, Source), Pair.Value)
        Next
        Dim SourceJson = "v/" & Path.GetFileName(JsonFile(Source)), CloneJson = JObject.Parse(ReadSmallText(JsonFile(CloneFolder), 16 * 1024 * 1024))
        CloneJson("id") = Path.GetFileName(Source)
        If CloneJson("jar")?.ToString() = Path.GetFileName(CloneFolder) Then CloneJson("jar") = Path.GetFileName(Source)
        RewritePaths(CloneJson, CloneFolder, Source)
        If JToken.DeepEquals(CloneJson, JObject.Parse(ReadSmallText(JsonFile(Source), 16 * 1024 * 1024))) Then Incoming(SourceJson) = Plan.SourceFiles(SourceJson)
        If CloneEntries.ContainsKey("v/PCL/config.json") OrElse SourceEntries.ContainsKey("v/PCL/config.json") Then
            Dim ConfigPayload As String = Nothing
            CloneEntries.TryGetValue("v/PCL/config.json", ConfigPayload)
            Dim MergedConfig = MigrationConfig(ConfigPayload, Source)
            If SourceEntries.ContainsKey("v/PCL/config.json") AndAlso JToken.DeepEquals(MergedConfig, JObject.Parse(ReadSmallText(SourceEntries("v/PCL/config.json"), 16 * 1024 * 1024))) Then
                Incoming("v/PCL/config.json") = Plan.SourceFiles("v/PCL/config.json")
            Else
                Using Hash = SHA1.Create()
                    Incoming("v/PCL/config.json") = "sha1:" & BitConverter.ToString(Hash.ComputeHash(Encoding.UTF8.GetBytes(MergedConfig.ToString()))).Replace("-", "").ToLowerInvariant()
                End Using
            End If
        End If
        For Each Key In Plan.SourceFiles.Keys.Union(Incoming.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(Function(Value) Value)
            Dim Before As String = Nothing, After As String = Nothing
            Plan.SourceFiles.TryGetValue(Key, Before) : Incoming.TryGetValue(Key, After)
            If Before = After Then Continue For
            Plan.Changes.Add(New InstanceCloneMigrationChange With {.Relative = Key, .Kind = If(Before Is Nothing, "新增", If(After Is Nothing, "删除", "修改"))})
        Next
        Return Plan
    End Function
    Friend Shared Function ApplyMigration(Plan As InstanceCloneMigrationPlan, Token As CancellationToken, Optional CaptureBefore As Func(Of String) = Nothing,
        Optional BeforeMinecraftChange As Action(Of String, String) = Nothing, Optional BeforeApply As Action(Of Integer) = Nothing) As String
        If Plan Is Nothing Then Throw New ArgumentNullException(NameOf(Plan))
        SyncLock Gate
            AssertStopped(Plan.SourceGameRoot, Plan.CloneVersionFolder)
            Dim Fresh = PreviewMigration(Plan.CloneVersionFolder, Plan.SourceGameRoot, Token)
            If Not PathUtils.IsSame(Fresh.SourceVersionFolder, Plan.SourceVersionFolder) OrElse Fresh.OriginHash <> Plan.OriginHash Then Throw New IOException("副本来源记录已变化，请重新预览。")
            Verify(Plan.SourceFiles, Fresh.SourceFiles) : Verify(Plan.CloneFiles, Fresh.CloneFiles)
            If Plan.OldMinecraft <> Fresh.OldMinecraft OrElse Plan.NewMinecraft <> Fresh.NewMinecraft Then Throw New IOException("Minecraft 版本已变化，请重新预览。")
            If Fresh.Changes.Count = 0 Then Return Nothing
            If Fresh.OldMinecraft = "未知版本" OrElse Fresh.NewMinecraft = "未知版本" Then Throw New IOException("无法确认 Minecraft 版本，自动迁移已停止。")
            If Fresh.OldMinecraft <> Fresh.NewMinecraft Then
                If BeforeMinecraftChange Is Nothing Then Throw New IOException("迁移会更改 Minecraft 版本，需要先完成原存档备份。")
                BeforeMinecraftChange(Fresh.OldMinecraft, Fresh.NewMinecraft)
            End If
            Token.ThrowIfCancellationRequested()
            Dim Undo = If(CaptureBefore Is Nothing, EnvironmentHistory.Capture(Fresh.SourceVersionFolder, Fresh.SourceGameRoot, "测试副本迁移前", Token), CaptureBefore())
            If String.IsNullOrEmpty(Undo) Then Throw New IOException("未能保存原环境还原点，迁移已停止。")
            Dim Parent = Path.GetDirectoryName(Fresh.CloneVersionFolder), Stage = Path.Combine(Parent, ".pcl-clone-" & Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(Stage)
            Try
                Dim Incoming As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase), CloneEntries = MigrationFiles(Fresh.CloneVersionFolder, Fresh.CloneVersionFolder, Token)
                For Each Pair In CloneEntries
                    Dim Key = MapVersionKey(Pair.Key, Fresh.CloneVersionFolder, Fresh.SourceVersionFolder), Payload = Target(Stage, Key.Substring(2))
                    CopyFile(Pair.Value, Payload, Fresh.CloneFiles(Pair.Key), Token)
                    Incoming.Add(Key, Payload)
                Next
                '迁移只重写启动描述，不复制隔离/账号配置和来源标记。
                Dim JsonKey = "v/" & Path.GetFileName(JsonFile(Fresh.SourceVersionFolder)), Data = JObject.Parse(ReadSmallText(Incoming(JsonKey), 16 * 1024 * 1024))
                Data("id") = Path.GetFileName(Fresh.SourceVersionFolder)
                If Data("jar")?.ToString() = Path.GetFileName(Fresh.CloneVersionFolder) Then Data("jar") = Path.GetFileName(Fresh.SourceVersionFolder)
                RewritePaths(Data, Fresh.CloneVersionFolder, Fresh.SourceVersionFolder)
                File.WriteAllText(Incoming(JsonKey), Data.ToString(), New UTF8Encoding(False))
                If Incoming.ContainsKey("v/PCL/config.json") Then File.WriteAllText(Incoming("v/PCL/config.json"), MigrationConfig(Incoming("v/PCL/config.json"), Fresh.SourceVersionFolder).ToString(), New UTF8Encoding(False))
                '移除安装清单只移除该字段，其他本地配置仍保留。
                If Not Incoming.ContainsKey("v/PCL/config.json") AndAlso Fresh.SourceFiles.ContainsKey("v/PCL/config.json") Then
                    Dim ConfigPayload = Target(Stage, "PCL/config.json")
                    Directory.CreateDirectory(Path.GetDirectoryName(ConfigPayload))
                    File.WriteAllText(ConfigPayload, MigrationConfig(Nothing, Fresh.SourceVersionFolder).ToString(), New UTF8Encoding(False))
                    Incoming.Add("v/PCL/config.json", ConfigPayload)
                End If
                Dim Operations As New List(Of Tuple(Of String, String, String))
                For Each Key In Fresh.SourceFiles.Keys.Union(Incoming.Keys, StringComparer.OrdinalIgnoreCase)
                    Dim Before As String = Nothing, Payload As String = Nothing
                    Fresh.SourceFiles.TryGetValue(Key, Before) : Incoming.TryGetValue(Key, Payload)
                    If Payload IsNot Nothing AndAlso Digest(Payload, Token) = Before Then Continue For
                    Dim Destination = Target(If(Key.StartsWith("v/"), Fresh.SourceVersionFolder, Fresh.SourceGameRoot), Key.Substring(2))
                    Operations.Add(Tuple.Create(Destination, Payload, Before))
                Next
                AssertStopped(Plan.SourceGameRoot, Plan.CloneVersionFolder)
                Verify(Fresh.SourceFiles, Fingerprints(MigrationFiles(Fresh.SourceVersionFolder, Fresh.SourceGameRoot, Token), Token))
                Verify(Fresh.CloneFiles, Fingerprints(MigrationFiles(Fresh.CloneVersionFolder, Fresh.CloneVersionFolder, Token), Token))
                ModpackUpdate.ApplyTransaction(Fresh.SourceGameRoot, Fresh.SourceVersionFolder, Operations, Token, Sub(Index)
                                                                                                                   Token.ThrowIfCancellationRequested()
                                                                                                                   BeforeApply?.Invoke(Index)
                                                                                                               End Sub)
                Return Undo
            Finally
                DeleteStage(Parent, Stage)
            End Try
        End SyncLock
    End Function
End Class

Friend NotInheritable Class InstanceClonerUi
    Friend Shared Event Changed()
    Friend Shared Property BeforeMinecraftChangeHandler As Action(Of McInstance, String, String, CancellationToken)
    Private Shared Busy As Integer
    Friend Shared ReadOnly Property IsBusy As Boolean
        Get
            Return Threading.Volatile.Read(Busy) <> 0
        End Get
    End Property
    Private Shared Function CanStart(Instance As McInstance) As Boolean
        If Instance Is Nothing OrElse HasRunningMinecraft OrElse QuickPlayWorlds.HasRunningSessions() OrElse McLaunchLoader.State = LoadState.Loading OrElse WorldBackups.IsBusy OrElse WorldBackups.HasActiveGame(InstanceCloner.GameRootForInstance(Instance)) OrElse EnvironmentHistoryUi.IsBusy OrElse ModpackUpdateFlow.IsUpdating OrElse McInstanceLock.IsAnyLocked OrElse PageInstanceMod.UpdatingInstanceModFolders.Any() Then
            Hint("请先关闭游戏并等待版本任务完成。", HintType.Red)
            Return False
        End If
        Return Interlocked.CompareExchange(Busy, 1, 0) = 0
    End Function
    Friend Shared Sub Start(Instance As McInstance, NewName As String, Options As InstanceCloneOptions)
        If Not CanStart(Instance) Then Return
        Dim StepLoader As New LoaderTask(Of String, String)("复制为独立测试版本", Sub(Task)
                                                                                       Dim Result = InstanceCloner.CloneInstance(Instance, NewName, Options, Task.CreateCancellationToken())
                                                                                       Task.Output = Result.VersionFolder
                                                                                       RunInUi(Sub() Hint("已创建测试副本：" & Result.Name, HintType.Green))
                                                                                   End Sub)
        Run(Instance, "克隆版本：" & Instance.Name, StepLoader)
    End Sub
    Friend Shared Sub Migrate(CloneInstance As McInstance)
        If Not CanStart(CloneInstance) Then Return
        Dim StepLoader As New LoaderTask(Of String, String)("预览并迁移测试环境", Sub(Task)
                                                                                     Dim Token = Task.CreateCancellationToken(), Source As New McInstance(InstanceCloner.SourceVersionFolder(CloneInstance.PathVersion))
                                                                                     Dim Plan = InstanceCloner.PreviewMigration(CloneInstance.PathVersion, InstanceCloner.GameRootForInstance(Source), Token)
                                                                                     If Plan.Changes.Count = 0 Then Throw New IOException("测试副本与原环境没有可迁移的变化。")
                                                                                     Dim Preview = String.Join(vbCrLf, Plan.Changes.Take(30).Select(Function(Item) Item.Kind & " · " & Item.Relative.Substring(2)))
                                                                                     If Plan.Changes.Count > 30 Then Preview &= vbCrLf & $"另有 {Plan.Changes.Count - 30} 项变更。"
                                                                                     If MyMsgBox("将测试环境迁移到原版本：" & Path.GetFileName(Plan.SourceVersionFolder) & vbCrLf & $"Minecraft：{Plan.OldMinecraft} → {Plan.NewMinecraft}" & vbCrLf & vbCrLf & Preview & vbCrLf & vbCrLf & "迁移前会保存原环境还原点；存档和截图保留原内容。", "迁移测试环境", "保存还原点并迁移", "取消", IsWarn:=True) <> 1 Then Throw New OperationCanceledException()
                                                                                     Dim Before As Action(Of String, String) = Nothing
                                                                                     If BeforeMinecraftChangeHandler IsNot Nothing Then Before = Sub(OldVersion, NewVersion) BeforeMinecraftChangeHandler.Invoke(Source, OldVersion, NewVersion, Token)
                                                                                     Task.Output = InstanceCloner.ApplyMigration(Plan, Token, Function() EnvironmentHistory.CaptureInstance(Source, "测试副本迁移前", Token), Before)
                                                                                     RunInUi(Sub()
                                                                                                 Source.ResetSettingsCache()
                                                                                                 Hint("已迁移测试环境；原环境可从还原点恢复。", HintType.Green)
                                                                                             End Sub)
                                                                                 End Sub)
        Run(CloneInstance, "迁移测试版本：" & CloneInstance.Name, StepLoader, True)
    End Sub
    Private Shared Sub Run(Instance As McInstance, Name As String, StepLoader As LoaderTask(Of String, String), Optional LockSource As Boolean = False)
        Dim Loader As New LoaderCombo(Of String)(Name, {StepLoader}) With {.OnStateChanged = AddressOf LoaderStateChangedHintOnly}
        Dim Abandoned As Boolean
        Dim Worker As New RedoableWorker(Sub()
                                            If Not Abandoned Then Loader.WaitForExit(Instance.PathVersion)
                                        End Sub)
        If Not McInstanceLock.TryAcquire(Worker, Instance.PathVersion) Then
            Interlocked.Exchange(Busy, 0)
            Return
        End If
        If LockSource Then
            Try
                If Not McInstanceLock.TryAcquire(Worker, InstanceCloner.SourceVersionFolder(Instance.PathVersion)) Then
                    Abandoned = True
                    Worker.Start()
                    Interlocked.Exchange(Busy, 0)
                    Return
                End If
            Catch ex As Exception
                Abandoned = True
                Worker.Start()
                Interlocked.Exchange(Busy, 0)
                Hint("无法读取测试副本来源：" & ex.Message, HintType.Red)
                Return
            End Try
        End If
        AddHandler Worker.Stopped, Sub()
                                       Interlocked.Exchange(Busy, 0)
                                       RunInUi(Sub()
                                                   LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
                                                   RaiseEvent Changed()
                                               End Sub)
                                   End Sub
        Worker.Start()
        LoaderTaskbarAdd(Loader)
        FrmMain.BtnExtraDownload.ShowRefresh()
    End Sub
End Class
