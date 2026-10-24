Imports System.IO
Imports System.IO.Compression
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text.RegularExpressions

'整合包作者清单独立于 PCL 的游戏组件清单；不从整个游戏目录猜测文件归属。
Friend Class PackUpdateFile
    Public Relative As String
    Public Hash As String
    Public Identity As String
    Public Urls As New List(Of String)
    Public Payload As String
    Public OptionalFile As Boolean
End Class

Friend Class PackUpdatePackage
    Public Name As String
    Public Version As String
    Public Minecraft As String
    Public Loaders As New Dictionary(Of ModLoaders, String)
    Public Files As New Dictionary(Of String, PackUpdateFile)(StringComparer.OrdinalIgnoreCase)
    Public Source As JObject
End Class

Friend Class PackUpdateChange
    Public Relative As String
    Public Before As String
    Public Incoming As PackUpdateFile
    Public Conflict As Boolean
    Public Reason As String
End Class

Friend Class PackUpdatePlan
    Public Root As String
    Public Package As PackUpdatePackage
    Public Changes As New List(Of PackUpdateChange)
    Public Baseline As JObject
End Class

Friend NotInheritable Class ModpackUpdate
    Friend Const ManifestName As String = "PCL\ModpackOrigin.json"

    Friend Shared Function RelativePath(Value As String) As String
        If String.IsNullOrWhiteSpace(Value) OrElse Path.IsPathRooted(Value) Then Throw New IOException("整合包包含无效路径。")
        Value = Value.Replace("\", "/")
        For Each Part In Value.Split("/"c)
            If Part = "" OrElse Part = "." OrElse Part = ".." OrElse Part.EndsWith(".") OrElse Part.EndsWith(" ") OrElse
                Part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 OrElse Regex.IsMatch(Part, "\A(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase) Then Throw New IOException("整合包包含无效路径：" & Value)
        Next
        Return Value
    End Function
    Friend Shared Function Target(Root As String, Relative As String) As String
        Dim Result = Path.Combine(Root, RelativePath(Relative).Replace("/"c, Path.DirectorySeparatorChar))
        InstanceResourceStore.EnsureWithin(Root, Result)
        If Directory.Exists(Result) Then Throw New IOException("文件位置已被文件夹占用：" & Relative)
        Return Result
    End Function
    Friend Shared Function Digest(FileName As String, Optional Algorithm As String = "sha1") As String
        If Not File.Exists(FileName) Then Return Nothing
        Using Input = File.OpenRead(FileName), Hasher As HashAlgorithm = If(Algorithm = "md5", DirectCast(MD5.Create(), HashAlgorithm), If(Algorithm = "sha512", DirectCast(SHA512.Create(), HashAlgorithm), SHA1.Create()))
            Return Algorithm & ":" & BitConverter.ToString(Hasher.ComputeHash(Input)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Shared Function Matches(FileName As String, Expected As String) As Boolean
        Return Expected IsNot Nothing AndAlso Digest(FileName, Expected.Split(":"c)(0)) = Expected
    End Function
    Private Shared Function ValidHash(Algorithm As String, Value As String) As String
        Dim Length = If(Algorithm = "md5", 32, If(Algorithm = "sha512", 128, 40))
        If Not Regex.IsMatch(If(Value, ""), "\A[0-9a-fA-F]{" & Length & "}\z") Then Throw New IOException("整合包文件缺少有效校验值。")
        Return Algorithm & ":" & Value.ToLowerInvariant()
    End Function
    Private Shared Function IsPrivate(Relative As String) As Boolean
        Dim Lower = Relative.ToLowerInvariant()
        Return Lower.StartsWith("pcl/") OrElse Lower.StartsWith(".pcl-") OrElse Lower = ".pclignore"
    End Function
    Private Shared Function IsPersonal(Relative As String) As Boolean
        Dim Lower = Relative.ToLowerInvariant()
        Return Lower = "options.txt" OrElse Lower.StartsWith("optionsof") OrElse Lower.StartsWith("optionsshaders") OrElse
            Lower = "servers.dat" OrElse Lower = "servers.dat_old" OrElse
            {"saves/", "screenshots/", "xaerowaypoints/", "xaeroworldmap/", "journeymap/", "voxelmap/", "schematics/"}.Any(Function(Prefix) Lower.StartsWith(Prefix)) OrElse
            Lower.Contains("waypoint") OrElse Lower.StartsWith("config/xaero") OrElse Lower.StartsWith("config/journeymap") OrElse Lower.StartsWith("config/voxelmap")
    End Function
    Private Shared Function PersonalAreaExists(Root As String, Relative As String) As Boolean
        Dim Parts = Relative.Split("/"c)
        If Parts.Length < 2 Then Return False
        Dim Prefix = Parts(0).ToLowerInvariant()
        Dim Area = If(Prefix = "saves" AndAlso Parts.Length > 2, Parts(0) & "/" & Parts(1), Parts(0))
        Dim Folder = Path.Combine(Root, Area.Replace("/"c, Path.DirectorySeparatorChar))
        InstanceResourceStore.EnsureWithin(Root, Folder)
        Return IsPersonal(Relative) AndAlso Directory.Exists(Folder)
    End Function
    Private Shared Function JsonEntry(Entry As ZipArchiveEntry) As JObject
        If Entry.Length > 16 * 1024 * 1024 Then Throw New IOException("整合包清单过大。")
        Using Reader As New StreamReader(Entry.Open(), System.Text.Encoding.UTF8)
            Return JObject.Parse(Reader.ReadToEnd())
        End Using
    End Function

    Friend Shared Function ReadPackage(ArchivePath As String, Stage As String, Token As CancellationToken,
        Optional Request As Func(Of String, HttpMethod, String, JToken) = Nothing) As PackUpdatePackage
        If Request Is Nothing Then Request = Function(Url, Method, Body) DlModRequest(Url, Method, Body, If(Body Is Nothing, Nothing, "application/json"))
        Dim Result As New PackUpdatePackage
        Using Archive = ZipFile.OpenRead(ArchivePath)
            Dim Entry = Archive.Entries.FirstOrDefault(Function(Item) Item.FullName = "modrinth.index.json" OrElse Item.FullName = "manifest.json")
            If Entry Is Nothing Then
                Dim Entries = Archive.Entries.Where(Function(Item) Item.FullName.Split("/"c).Length = 2 AndAlso {"modrinth.index.json", "manifest.json"}.Contains(Item.Name)).ToList()
                If Entries.Count <> 1 Then Throw New IOException("请选择 CurseForge 或 Modrinth 格式的整合包文件。")
                Entry = Entries(0)
            End If
            Dim Prefix = Entry.FullName.Substring(0, Entry.FullName.Length - Entry.Name.Length), Json = JsonEntry(Entry)
            Result.Name = If(Json("name")?.ToString(), "整合包")
            Result.Version = If(Json("versionId")?.ToString(), If(Json("version")?.ToString(), ""))
            Dim OverrideFolders As New List(Of String)
            If Entry.Name = "modrinth.index.json" Then
                Result.Minecraft = Json("dependencies")?("minecraft")?.ToString()
                For Each Pair In New Dictionary(Of String, ModLoaders) From {{"fabric-loader", ModLoaders.Fabric}, {"forge", ModLoaders.Forge}, {"neoforge", ModLoaders.NeoForge}}
                    If Json("dependencies")?(Pair.Key) IsNot Nothing Then Result.Loaders(Pair.Value) = Json("dependencies")(Pair.Key).ToString()
                Next
                For Each Dependency As JProperty In CType(Json("dependencies"), JObject).Properties()
                    If Not {"minecraft", "fabric-loader", "forge", "neoforge"}.Contains(Dependency.Name) Then Throw New IOException("暂不支持此整合包加载器：" & Dependency.Name)
                Next
                For Each Item In If(TryCast(Json("files"), JArray), New JArray)
                    Token.ThrowIfCancellationRequested()
                    If Item("env")?("client")?.ToString() = "unsupported" Then Continue For
                    Dim Value As New PackUpdateFile With {.Relative = RelativePath(Item("path").ToString()), .Hash = ValidHash("sha1", Item("hashes")?("sha1")?.ToString()), .OptionalFile = Item("env")?("client")?.ToString() = "optional",
                        .Urls = CType(Item("downloads"), JArray).Select(Function(Url) Url.ToString()).ToList()}
                    For Each Url In Value.Urls
                        Dim Match = Regex.Match(Url, "\Ahttps://cdn\.modrinth\.com/data/([A-Za-z0-9]+)/versions/")
                        If Match.Success Then Value.Identity = "modrinth:" & Match.Groups(1).Value : Exit For
                    Next
                    AddFile(Result, Value)
                Next
                OverrideFolders.Add(Prefix & "overrides/")
                OverrideFolders.Add(Prefix & "client-overrides/")
            Else
                Result.Minecraft = Json("minecraft")?("version")?.ToString()
                For Each Item In If(TryCast(Json("minecraft")?("modLoaders"), JArray), New JArray)
                    Dim Id = Item("id").ToString(), Split = Id.IndexOf("-"c)
                    If Split < 1 Then Throw New IOException("无效的整合包加载器。")
                    Dim Kind As ModLoaders
                    Select Case Id.Substring(0, Split)
                        Case "forge" : Kind = ModLoaders.Forge
                        Case "neoforge" : Kind = ModLoaders.NeoForge
                        Case "fabric" : Kind = ModLoaders.Fabric
                        Case Else : Throw New IOException("暂不支持此整合包加载器：" & Id)
                    End Select
                    Result.Loaders(Kind) = Id.Substring(Split + 1)
                Next
                Dim Files = If(TryCast(Json("files"), JArray), New JArray).ToList()
                If Files.Any() Then
                    Dim Response = CType(Request("https://api.curseforge.com/v1/mods/files", HttpMethod.Post,
                        New JObject(New JProperty("fileIds", New JArray(Files.Select(Function(Item) CInt(Item("fileID")))))).ToString())("data"), JArray)
                    For Each Item In Files
                        Token.ThrowIfCancellationRequested()
                        Dim Data = Response.FirstOrDefault(Function(Value) Value("id").ToString() = Item("fileID").ToString())
                        If Data Is Nothing OrElse Data("modId").ToString() <> Item("projectID").ToString() Then Throw New IOException("整合包所需文件不可用，未修改当前版本。")
                        Dim Version = ResourceVersion.FromPlatformJson(CType(Data, JObject), ResourceTypes.Mod)
                        If Not Version.DownloadAvailable Then Throw New IOException("整合包所需文件不允许下载：" & Version.FileName)
                        Dim Modules = If(TryCast(Data("modules"), JArray), New JArray).Select(Function(ModuleItem) ModuleItem("name").ToString()).ToList()
                        Dim Folder = If(Version.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase), "mods", If(Modules.Contains("shaders"), "shaderpacks", If(Modules.Contains("pack.mcmeta"), "resourcepacks", "mods")))
                        AddFile(Result, New PackUpdateFile With {.Relative = RelativePath(Folder & "/" & Version.FileName), .Hash = ValidHash(If(Version.Hash?.Length = 32, "md5", "sha1"), Version.Hash),
                            .Identity = "curseforge:" & Item("projectID").ToString(), .OptionalFile = Item("required") IsNot Nothing AndAlso Not CBool(Item("required")), .Urls = Version.DownloadUrls.ToList()})
                    Next
                End If
                Dim OverridePath = If(Json("overrides")?.ToString(), "overrides").TrimEnd("/"c)
                OverrideFolders.Add(Prefix & RelativePath(OverridePath) & "/")
            End If
            If String.IsNullOrWhiteSpace(Result.Minecraft) Then Throw New IOException("整合包缺少 Minecraft 版本。")
            Dim Seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase), Total As Long
            For Each Folder In OverrideFolders
                Seen.Clear()
                For Each Item In Archive.Entries.Where(Function(Value) Value.FullName.StartsWith(Folder, StringComparison.Ordinal) AndAlso Not Value.FullName.EndsWith("/"))
                    Token.ThrowIfCancellationRequested()
                    Dim Relative = RelativePath(Item.FullName.Substring(Folder.Length))
                    If IsPrivate(Relative) Then Continue For
                    If Not Seen.Add(Relative) Then Throw New IOException("整合包包含重复路径：" & Relative)
                    Total += Item.Length
                    If Total > 20L * 1024 * 1024 * 1024 OrElse Item.Length > 2L * 1024 * 1024 * 1024 Then Throw New IOException("整合包覆写文件过大。")
                    Dim Payload = Target(Stage, "payload/" & Guid.NewGuid().ToString("N"))
                    Directory.CreateDirectory(Path.GetDirectoryName(Payload))
                    Using Input = Item.Open(), Output = File.Create(Payload)
                        Input.CopyTo(Output)
                    End Using
                    Result.Files(Relative) = New PackUpdateFile With {.Relative = Relative, .Payload = Payload, .Hash = Digest(Payload)}
                Next
            Next
        End Using
        Return Result
    End Function
    Private Shared Sub AddFile(Package As PackUpdatePackage, Value As PackUpdateFile)
        If IsPrivate(Value.Relative) Then Return
        For Each Url In Value.Urls
            Dim Address As Uri = Nothing
            If Not Uri.TryCreate(Url, UriKind.Absolute, Address) OrElse Address.Scheme <> "https" OrElse Address.UserInfo <> "" Then Throw New IOException("整合包包含无效下载地址。")
        Next
        If Package.Files.ContainsKey(Value.Relative) Then Throw New IOException("整合包包含重复路径：" & Value.Relative)
        Package.Files.Add(Value.Relative, Value)
    End Sub
    Friend Shared Function Manifest(Package As PackUpdatePackage) As JObject
        Dim Files As New JObject
        For Each Item In Package.Files.Values
            Files(Item.Relative) = New JObject(New JProperty("hash", Item.Hash), New JProperty("identity", Item.Identity))
        Next
        Return New JObject(New JProperty("schema", 1), New JProperty("name", Package.Name), New JProperty("version", Package.Version),
            New JProperty("minecraft", Package.Minecraft), New JProperty("loaders", JObject.FromObject(Package.Loaders)), New JProperty("files", Files), New JProperty("source", Package.Source))
    End Function
    Friend Shared Function ReadManifest(VersionFolder As String) As JObject
        Dim FileName = Target(VersionFolder, ManifestName)
        If Not File.Exists(FileName) Then Return Nothing
        If New FileInfo(FileName).Length > 16 * 1024 * 1024 Then Throw New IOException("整合包作者清单过大。")
        Dim Result = JObject.Parse(File.ReadAllText(FileName))
        If CInt(Result("schema")) <> 1 OrElse TypeOf Result("files") IsNot JObject Then Throw New IOException("不支持此版本的整合包作者清单。")
        Return Result
    End Function
    Friend Shared Function Detect(VersionFolder As String) As JObject
        Dim Recorded = ReadManifest(VersionFolder)
        If Recorded IsNot Nothing Then Return Recorded
        For Each Name In {"modrinth.index.json", "manifest.json"}
            Dim Address = Target(VersionFolder, Name)
            If Not File.Exists(Address) Then Continue For
            If New FileInfo(Address).Length > 16 * 1024 * 1024 Then Throw New IOException("整合包清单过大。")
            Dim Data = JObject.Parse(File.ReadAllText(Address))
            Dim Mc = If(Name = "modrinth.index.json", Data("dependencies")?("minecraft")?.ToString(), Data("minecraft")?("version")?.ToString())
            If String.IsNullOrWhiteSpace(Mc) OrElse TypeOf Data("files") IsNot JArray Then Continue For
            Return New JObject(New JProperty("name", If(Data("name")?.ToString(), "整合包")), New JProperty("version", If(Data("versionId")?.ToString(), Data("version")?.ToString())),
                New JProperty("minecraft", Mc), New JProperty("legacy", True))
        Next
        Return Nothing
    End Function

    Friend Shared Function Plan(Root As String, Baseline As JObject, Package As PackUpdatePackage) As PackUpdatePlan
        If Baseline Is Nothing Then Throw New IOException("此版本没有作者文件清单，请先选择当前使用的原版整合包建立记录。")
        Dim Result As New PackUpdatePlan With {.Root = Root, .Package = Package, .Baseline = Baseline}
        Dim Old = CType(Baseline("files"), JObject).Properties().ToDictionary(Function(Item) Item.Name, Function(Item) Item.Value, StringComparer.OrdinalIgnoreCase)
        For Each Relative In Old.Keys.Union(Package.Files.Keys, StringComparer.OrdinalIgnoreCase)
            If IsPrivate(Relative) Then Continue For
            Dim FileName = Target(Root, Relative), Previous As JToken = Nothing, Incoming As PackUpdateFile = Nothing
            Old.TryGetValue(Relative, Previous)
            Package.Files.TryGetValue(Relative, Incoming)
            Dim Before = Digest(FileName), OldHash = Previous?("hash")?.ToString()
            If Incoming IsNot Nothing AndAlso Incoming.OptionalFile AndAlso Previous Is Nothing AndAlso Before Is Nothing Then
                Result.Changes.Add(New PackUpdateChange With {.Relative = Relative, .Incoming = Incoming, .Conflict = True, .Reason = "保留未安装的可选组件"})
                Continue For
            End If
            '上游未变化时不恢复玩家删除、禁用或修改的文件。
            If Incoming IsNot Nothing AndAlso (Incoming.Hash = OldHash OrElse Matches(FileName, Incoming.Hash)) Then Continue For
            If Incoming Is Nothing AndAlso Before Is Nothing Then Continue For
            Dim Conflict = (Before IsNot Nothing AndAlso IsPersonal(Relative)) OrElse PersonalAreaExists(Root, Relative) OrElse
                (Previous IsNot Nothing AndAlso Not Matches(FileName, OldHash)) OrElse (Previous Is Nothing AndAlso Before IsNot Nothing)
            Result.Changes.Add(New PackUpdateChange With {.Relative = Relative, .Before = Before, .Incoming = Incoming, .Conflict = Conflict,
                .Reason = If(Conflict, If(Before Is Nothing, "保留玩家删除或禁用", "保留个人文件或修改"), If(Incoming Is Nothing, "移除作者已删除的文件", If(Before Is Nothing, "新增", "更新")))})
        Next
        '同一项目改了文件名时，不能一边保留个人版本，一边安装另一个版本。
        Dim ProtectedIds As New HashSet(Of String)(StringComparer.Ordinal)
        For Each Change In Result.Changes.Where(Function(Item) Item.Conflict)
            Dim Id = If(Old.ContainsKey(Change.Relative), Old(Change.Relative)("identity")?.ToString(), Change.Incoming?.Identity)
            If Not String.IsNullOrEmpty(Id) Then ProtectedIds.Add(Id)
        Next
        For Each Change In Result.Changes.Where(Function(Item) Item.Incoming IsNot Nothing AndAlso ProtectedIds.Contains(If(Item.Incoming.Identity, "")))
            Change.Conflict = True
            Change.Reason = "保留此项目的个人版本，跳过同项目新文件"
        Next
        Return Result
    End Function

    Friend Shared Sub DownloadChanges(Plan As PackUpdatePlan, Stage As String, Token As CancellationToken,
        Optional Download As Action(Of List(Of NetFile), CancellationToken) = Nothing)
        Dim Files As New List(Of NetFile)
        For Each Change In Plan.Changes.Where(Function(Item) Not Item.Conflict AndAlso Item.Incoming IsNot Nothing)
            Dim Value = Change.Incoming
            If Value.Payload IsNot Nothing Then Continue For
            Value.Payload = Target(Stage, "download/" & Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(Path.GetDirectoryName(Value.Payload))
            Files.Add(New NetFile(Value.Urls, Value.Payload, New FileChecker With {.Hash = Value.Hash.Split(":"c)(1)}, True))
        Next
        If Download Is Nothing Then Download = AddressOf DownloadFiles
        If Files.Any() Then Download(Files, Token)
        For Each Change In Plan.Changes.Where(Function(Item) Not Item.Conflict AndAlso Item.Incoming IsNot Nothing)
            Token.ThrowIfCancellationRequested()
            If Not Matches(Change.Incoming.Payload, Change.Incoming.Hash) Then Throw New IOException("整合包更新文件校验失败：" & Change.Relative)
        Next
        PreserveModCollisions(Plan)
    End Sub
    Private Shared Function ModIds(FileName As String) As HashSet(Of String)
        Dim Result As New HashSet(Of String)(StringComparer.Ordinal)
        Try
            Using Archive = ZipFile.OpenRead(FileName)
                For Each Name In {"fabric.mod.json", "quilt.mod.json", "META-INF/mods.toml", "META-INF/neoforge.mods.toml", "mcmod.info"}
                    Dim Entry = Archive.GetEntry(Name)
                    If Entry Is Nothing OrElse Entry.Length > 1024 * 1024 Then Continue For
                    Using Reader As New StreamReader(Entry.Open())
                        Dim Text = Reader.ReadToEnd()
                        If Name.EndsWith(".toml") Then
                            For Each Match As System.Text.RegularExpressions.Match In Regex.Matches(Text, "(?m)^\s*modId\s*=\s*['"" ]*([a-zA-Z0-9_-]+)")
                                Result.Add(Match.Groups(1).Value)
                            Next
                        Else
                            Dim Data = JToken.Parse(Text)
                            If Name = "mcmod.info" Then
                                Dim Items = If(TryCast(Data, JArray), TryCast(Data("modList"), JArray))
                                For Each Item In If(Items, New JArray)
                                    If Item("modid") IsNot Nothing Then Result.Add(Item("modid").ToString())
                                Next
                            Else
                                Dim Id = If(Name = "quilt.mod.json", Data("quilt_loader")?("id")?.ToString(), Data("id")?.ToString())
                                If Not String.IsNullOrEmpty(Id) Then Result.Add(Id)
                            End If
                        End If
                    End Using
                Next
            End Using
        Catch ex As InvalidDataException
        Catch ex As Newtonsoft.Json.JsonException
        End Try
        Return Result
    End Function
    Private Shared Sub PreserveModCollisions(Plan As PackUpdatePlan)
        Dim Folder = Path.Combine(Plan.Root, "mods")
        InstanceResourceStore.EnsureWithin(Plan.Root, Folder)
        If Not Directory.Exists(Folder) Then Return
        Dim Retained As New Dictionary(Of String, HashSet(Of String))(StringComparer.OrdinalIgnoreCase)
        For Each FileName In Directory.GetFiles(Folder)
            InstanceResourceStore.EnsureWithin(Plan.Root, FileName)
            If Not {".jar", ".disabled", ".old"}.Contains(Path.GetExtension(FileName).ToLowerInvariant()) Then Continue For
            Dim Relative = "mods/" & Path.GetFileName(FileName)
            If Plan.Changes.Any(Function(Item) Not Item.Conflict AndAlso Item.Relative.Equals(Relative, StringComparison.OrdinalIgnoreCase)) Then Continue For
            Retained(Relative) = ModIds(FileName)
        Next
        For Each Change In Plan.Changes.Where(Function(Item) Not Item.Conflict AndAlso Item.Incoming IsNot Nothing AndAlso Item.Relative.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
            Dim Ids = ModIds(Change.Incoming.Payload)
            Dim Collision = Retained.FirstOrDefault(Function(Item) Item.Value.Overlaps(Ids))
            If Collision.Value Is Nothing Then Continue For
            Change.Conflict = True
            Change.Reason = "保留个人模组 " & Collision.Key & "，跳过重复模组"
            '若本次也计划删除同项目旧作者文件，一并保留，避免只完成了一半升级。
            For Each Other In Plan.Changes.Where(Function(Item) Item.Incoming Is Nothing AndAlso Not Item.Conflict AndAlso Item.Relative.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
                If ModIds(Target(Plan.Root, Other.Relative)).Overlaps(Ids) Then
                    Other.Conflict = True
                    Other.Reason = "此模组的新版与个人模组冲突，保留旧版"
                End If
            Next
        Next
    End Sub
    Friend Shared Sub DownloadFiles(Files As List(Of NetFile), Token As CancellationToken)
        Dim Loader As New LoaderDownload("下载整合包更新文件", Files)
        Using Registration = Token.Register(AddressOf Loader.Cancel)
            Loader.WaitForExit(c:=Token)
        End Using
    End Sub

    '先持久化备份和事务日志；提交出错或进程中断时可从日志恢复。提交期间不接受半途取消。
    Friend Shared Function Commit(Plan As PackUpdatePlan, VersionFolder As String, Token As CancellationToken,
        Optional ExtraFiles As Dictionary(Of String, String) = Nothing, Optional BeforeApply As Action(Of Integer) = Nothing) As String
        Token.ThrowIfCancellationRequested()
        Dim Operations As New List(Of Tuple(Of String, String, String))
        For Each Change In Plan.Changes.Where(Function(Item) Not Item.Conflict)
            Dim FileName = Target(Plan.Root, Change.Relative)
            If Digest(FileName) <> Change.Before Then Throw New IOException("文件已被其他程序修改，请重新检查更新：" & Change.Relative)
            Operations.Add(Tuple.Create(FileName, Change.Incoming?.Payload, Change.Before))
        Next
        If ExtraFiles IsNot Nothing Then
            For Each Pair In ExtraFiles
                Operations.Add(Tuple.Create(Target(VersionFolder, Pair.Key), Pair.Value, Digest(Target(VersionFolder, Pair.Key))))
            Next
        End If
        Dim Box = Target(VersionFolder, "PCL/PackUpdateBackups/" & Guid.NewGuid().ToString("N") & "/transaction.json")
        Directory.CreateDirectory(Path.GetDirectoryName(Box))
        Dim StatePayload = Path.Combine(Path.GetDirectoryName(Box), "next-manifest.json")
        Dim NextManifest = Manifest(Plan.Package)
        For Each Change In Plan.Changes.Where(Function(Item) Item.Conflict)
            Dim Previous = Plan.Baseline("files")(Change.Relative)
            If Previous IsNot Nothing Then NextManifest("files")(Change.Relative) = Previous.DeepClone()
        Next
        File.WriteAllText(StatePayload, NextManifest.ToString())
        Dim StatePath = Target(VersionFolder, ManifestName)
        Operations.Add(Tuple.Create(StatePath, StatePayload, Digest(StatePath)))
        Return ApplyTransaction(Plan.Root, VersionFolder, Operations, Token, BeforeApply, Box)
    End Function
    Friend Shared Function ApplyTransaction(GameRoot As String, VersionFolder As String, Operations As List(Of Tuple(Of String, String, String)), Token As CancellationToken,
        Optional BeforeApply As Action(Of Integer) = Nothing, Optional Box As String = Nothing) As String
        Token.ThrowIfCancellationRequested()
        If Box Is Nothing Then Box = Target(VersionFolder, "PCL/PackUpdateBackups/" & Guid.NewGuid().ToString("N") & "/transaction.json")
        Directory.CreateDirectory(Path.GetDirectoryName(Box))
        For Each Op In Operations
            If PathUtils.IsParentOf(VersionFolder, Op.Item1) Then
                InstanceResourceStore.EnsureWithin(VersionFolder, Op.Item1)
            Else
                InstanceResourceStore.EnsureWithin(GameRoot, Op.Item1)
            End If
            If Digest(Op.Item1) <> Op.Item3 Then Throw New IOException("文件已变化，请重新检查环境。")
        Next
        Dim Log As New JObject(New JProperty("complete", False), New JProperty("gameRoot", Path.GetFullPath(GameRoot)), New JProperty("files", New JArray))
        Dim LogFiles = CType(Log("files"), JArray)
        For Index = 0 To Operations.Count - 1
            Dim Op = Operations(Index), Backup = Path.Combine(Path.GetDirectoryName(Box), Index & ".bak")
            If Op.Item3 IsNot Nothing Then File.Copy(Op.Item1, Backup, False)
            LogFiles.Add(New JObject(New JProperty("target", Op.Item1), New JProperty("backup", If(Op.Item3 Is Nothing, Nothing, Backup)), New JProperty("before", Op.Item3), New JProperty("after", If(Op.Item2 Is Nothing, Nothing, Digest(Op.Item2)))))
        Next
        File.WriteAllText(Box, Log.ToString())
        Dim Applied As New List(Of Integer)
        Try
            For Index = 0 To Operations.Count - 1
                BeforeApply?.Invoke(Index)
                Dim Op = Operations(Index)
                If Digest(Op.Item1) <> Op.Item3 Then Throw New IOException("提交期间文件发生变化，已停止更新。")
                Applied.Add(Index)
                If Op.Item2 Is Nothing Then
                    File.Delete(Op.Item1)
                Else
                    ReplaceFrom(Op.Item2, Op.Item1)
                End If
            Next
            Log("complete") = True
            LocalGameData.AtomicWrite(Box, System.Text.Encoding.UTF8.GetBytes(Log.ToString()), Nothing, False)
        Catch
            For Each Index In Applied.AsEnumerable().Reverse()
                Dim Item = LogFiles(Index), Destination = Item("target").ToString()
                If Item("backup").Type = JTokenType.Null Then
                    If Digest(Destination) = Item("after")?.ToString() Then File.Delete(Destination)
                Else
                    ReplaceFrom(Item("backup").ToString(), Destination)
                End If
            Next
            Log("complete") = True
            Log("rolledBack") = True
            File.WriteAllText(Box, Log.ToString())
            Throw
        End Try
        Return Path.GetDirectoryName(Box)
    End Function
    Private Shared Sub ReplaceFrom(Source As String, Destination As String)
        Directory.CreateDirectory(Path.GetDirectoryName(Destination))
        Dim Temporary = Destination & ".pcl-update-" & Guid.NewGuid().ToString("N")
        Try
            File.Copy(Source, Temporary, False)
            If File.Exists(Destination) Then File.Replace(Temporary, Destination, Nothing) Else File.Move(Temporary, Destination)
        Finally
            If File.Exists(Temporary) Then File.Delete(Temporary)
        End Try
    End Sub

    Friend Shared Function Pending(VersionFolder As String) As List(Of String)
        Dim Folder = Path.Combine(VersionFolder, "PCL", "PackUpdateBackups")
        InstanceResourceStore.EnsureWithin(VersionFolder, Folder)
        Dim Result As New List(Of String)
        If Not Directory.Exists(Folder) Then Return Result
        For Each Child In Directory.GetDirectories(Folder)
            InstanceResourceStore.EnsureWithin(VersionFolder, Child)
            Dim LogFile = Path.Combine(Child, "transaction.json")
            InstanceResourceStore.EnsureWithin(VersionFolder, LogFile)
            If File.Exists(LogFile) AndAlso New FileInfo(LogFile).Length <= 16 * 1024 * 1024 AndAlso Not CBool(JObject.Parse(File.ReadAllText(LogFile))("complete")) Then Result.Add(LogFile)
        Next
        Return Result
    End Function
    Friend Shared Sub Recover(VersionFolder As String, GameRoot As String)
        For Each LogFile In Pending(VersionFolder)
            Dim Log = JObject.Parse(File.ReadAllText(LogFile)), Items = CType(Log("files"), JArray)
            If Not PathUtils.IsSame(Log("gameRoot").ToString(), GameRoot) Then Throw New IOException("更新备份对应的游戏目录已经变化，请手动检查备份。")
            For Each Item In Items
                Dim Destination = Item("target").ToString()
                If PathUtils.IsParentOf(VersionFolder, Destination) Then
                    InstanceResourceStore.EnsureWithin(VersionFolder, Destination)
                Else
                    InstanceResourceStore.EnsureWithin(GameRoot, Destination)
                End If
                Dim Current = Digest(Destination), Before = If(Item("before").Type = JTokenType.Null, Nothing, Item("before").ToString()), After = If(Item("after").Type = JTokenType.Null, Nothing, Item("after").ToString())
                If Current <> Before AndAlso Current <> After Then Throw New IOException("中断后文件又发生变化，已保留现场：" & Destination)
                If Item("backup").Type <> JTokenType.Null Then
                    Dim Backup = Item("backup").ToString()
                    InstanceResourceStore.EnsureWithin(Path.GetDirectoryName(LogFile), Backup)
                    If Digest(Backup) <> Before Then Throw New IOException("更新备份校验失败。")
                End If
            Next
            For Each Item In Items.Reverse()
                Dim Destination = Item("target").ToString()
                If Item("backup").Type = JTokenType.Null Then
                    If File.Exists(Destination) Then File.Delete(Destination)
                Else
                    ReplaceFrom(Item("backup").ToString(), Destination)
                End If
            Next
            Log("complete") = True
            Log("rolledBack") = True
            LocalGameData.AtomicWrite(LogFile, System.Text.Encoding.UTF8.GetBytes(Log.ToString()), Nothing, False)
        Next
    End Sub

    Friend Shared Sub RecordInstalled(ArchivePath As String, VersionFolder As String, Optional Source As JObject = Nothing)
        Dim Stage = RequestTaskTempFolder()
        Try
            Dim Package = ReadPackage(ArchivePath, Stage, CancellationToken.None)
            Package.Source = Source
            Dim Root = New McInstance(VersionFolder).PathIndie
            '首次安装只登记实际安装且与原包一致的文件；未选可选项不归入作者文件。
            For Each Relative In Package.Files.Keys.ToList()
                If Not Matches(Target(Root, Relative), Package.Files(Relative).Hash) Then Package.Files.Remove(Relative)
            Next
            LocalGameData.AtomicWrite(Target(VersionFolder, ManifestName), System.Text.Encoding.UTF8.GetBytes(Manifest(Package).ToString()), Nothing, False)
        Catch ex As Exception
            Logger.Warn(ex, "整合包已安装，但记录作者文件清单失败；更新时可重新提供原始整合包")
        End Try
    End Sub
End Class
