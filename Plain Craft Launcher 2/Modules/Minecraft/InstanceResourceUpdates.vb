Imports System.IO
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Threading

Friend Class InstanceResourceOnlineInfo
    Public Entry As InstanceResourceEntry
    Public Sha1 As String
    Public ProjectName As String
    Public Website As String
    Public IconUrl As String
    Public Tags As New List(Of String)
    Public CurrentVersion As ResourceVersion
    Public UpdateVersion As ResourceVersion
    Public Status As String = "未匹配到在线项目"
    Public ReadOnly Property HasUpdate As Boolean
        Get
            Return UpdateVersion IsNot Nothing
        End Get
    End Property
End Class

'联网匹配只接受文件哈希；版本与下载仍使用原项目的 ResourceVersion / LoaderDownload。
Friend NotInheritable Class InstanceResourceUpdates
    Friend Shared Function Supported(Kind As InstanceResourceKind) As Boolean
        Return Kind = InstanceResourceKind.ResourcePacks OrElse Kind = InstanceResourceKind.ShaderPacks
    End Function
    Friend Shared Function FileHash(FileName As String, Optional Md5 As Boolean = False) As String
        Using Input = File.OpenRead(FileName), Algorithm As HashAlgorithm = If(Md5, DirectCast(Security.Cryptography.MD5.Create(), HashAlgorithm), SHA1.Create())
            Return BitConverter.ToString(Algorithm.ComputeHash(Input)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Shared Function ExpectedType(Kind As InstanceResourceKind) As ResourceTypes
        Return If(Kind = InstanceResourceKind.ResourcePacks, ResourceTypes.ResourcePack, ResourceTypes.Shader)
    End Function
    Private Shared Function Strings(Data As JToken) As List(Of String)
        Return If(TryCast(Data, JArray), New JArray).Select(Function(Item) Item.ToString().ToLowerInvariant()).Distinct().OrderBy(Function(Item) Item).ToList()
    End Function
    Private Shared Function ValidFile(Version As ResourceVersion) As Boolean
        If Version Is Nothing OrElse Not Version.DownloadAvailable OrElse Version.Size <= 0 OrElse
            Not System.Text.RegularExpressions.Regex.IsMatch(If(Version.Hash, ""), "\A(?:[0-9a-f]{40}|[0-9a-f]{32})\z") Then Return False
        Try
            InstanceResourceStore.SafeName(Version.FileName)
            If Not Version.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) Then Return False
            Return Version.DownloadUrls.Count > 0 AndAlso Version.DownloadUrls.All(Function(Value)
                                                Dim Address As Uri = Nothing
                                                Return Uri.TryCreate(Value, UriKind.Absolute, Address) AndAlso Address.Scheme = "https" AndAlso Address.UserInfo = ""
                                            End Function)
        Catch
            Return False
        End Try
    End Function
    Private Shared Sub Consider(Info As InstanceResourceOnlineInfo, Current As ResourceVersion, Candidate As ResourceVersion)
        If Not ValidFile(Candidate) OrElse Current Is Nothing OrElse Candidate.ReleaseDate <= Current.ReleaseDate OrElse Candidate.Hash = Current.Hash Then Return
        If Current.ReleaseType = ResourceVersion.ReleaseTypes.Release AndAlso Candidate.ReleaseType <> ResourceVersion.ReleaseTypes.Release Then Return
        If Current.ReleaseType = ResourceVersion.ReleaseTypes.Beta AndAlso Candidate.ReleaseType = ResourceVersion.ReleaseTypes.Alpha Then Return
        If Info.UpdateVersion Is Nothing OrElse Candidate.ReleaseDate > Info.UpdateVersion.ReleaseDate Then Info.UpdateVersion = Candidate
    End Sub
    Private Shared Function ModrinthFile(Data As JObject, Kind As InstanceResourceKind, Optional Hash As String = Nothing) As ResourceVersion
        Dim Files = If(TryCast(Data("files"), JArray), New JArray).OfType(Of JObject)().ToList()
        Dim Selected = If(Hash Is Nothing, Files.FirstOrDefault(Function(Item) Item("primary")?.ToObject(Of Boolean)() = True), Files.FirstOrDefault(Function(Item) Item("hashes")?("sha1")?.ToString() = Hash))
        If Selected Is Nothing AndAlso Hash Is Nothing AndAlso Files.Count = 1 Then Selected = Files(0)
        If Selected Is Nothing Then Return Nothing
        Dim Copy = DirectCast(Data.DeepClone(), JObject)
        Copy("files") = New JArray(Selected.DeepClone())
        Return ResourceVersion.FromPlatformJson(Copy, ExpectedType(Kind))
    End Function

    Friend Shared Function Check(Root As String, Items As IEnumerable(Of InstanceResourceEntry), Kind As InstanceResourceKind, GameVersion As String, Token As CancellationToken,
        Optional Request As Func(Of String, HttpMethod, String, JToken) = Nothing, Optional Fingerprint As Func(Of String, UInteger) = Nothing) As List(Of InstanceResourceOnlineInfo)
        If Not Supported(Kind) OrElse String.IsNullOrWhiteSpace(GameVersion) Then Return New List(Of InstanceResourceOnlineInfo)
        If Request Is Nothing Then Request = Function(Url, Method, Body) DlModRequest(Url, Method, Body, If(Body Is Nothing, Nothing, "application/json"))
        If Fingerprint Is Nothing Then Fingerprint = Function(Path) New LocalResourceFile(Path).CurseForgeHash
        Dim Result As New List(Of InstanceResourceOnlineInfo)
        For Each Entry In Items.Where(Function(Item) Not Item.IsDirectory)
            Token.ThrowIfCancellationRequested()
            InstanceResourceStore.ValidateEntry(Root, Entry)
            Result.Add(New InstanceResourceOnlineInfo With {.Entry = Entry, .Sha1 = FileHash(Entry.FullPath)})
        Next
        If Result.Count = 0 Then Return Result
        Dim Failed As New List(Of String)
        '分批查询，避免资源较多时超过平台请求大小。
        For Offset = 0 To Result.Count - 1 Step 100
            Dim Batch = Result.Skip(Offset).Take(100).ToList()
            Token.ThrowIfCancellationRequested()
            Try
                CheckModrinth(Batch, Kind, GameVersion, Token, Request)
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                Failed.Add("Modrinth")
                Logger.Warn(ex, "检查本地资源的 Modrinth 更新失败")
            End Try
            Token.ThrowIfCancellationRequested()
            Try
                CheckCurseForge(Batch, Kind, GameVersion, Token, Request, Fingerprint)
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                Failed.Add("CurseForge")
                Logger.Warn(ex, "检查本地资源的 CurseForge 更新失败")
            End Try
        Next
        For Each Info In Result
            If Info.HasUpdate Then
                Info.Status = "可更新至 " & If(Info.UpdateVersion.Version, Info.UpdateVersion.Display)
            ElseIf Info.CurrentVersion IsNot Nothing Then
                Info.Status = "未发现适用于 Minecraft " & GameVersion & " 的更新"
            End If
            If Failed.Any() Then Info.Status &= "（" & String.Join("、", Failed.Distinct()) & " 查询失败，可重试）"
        Next
        Token.ThrowIfCancellationRequested()
        Return Result
    End Function

    Private Shared Sub CheckModrinth(Infos As List(Of InstanceResourceOnlineInfo), Kind As InstanceResourceKind, GameVersion As String, Token As CancellationToken, Request As Func(Of String, HttpMethod, String, JToken))
        Dim Versions = DirectCast(Request("https://api.modrinth.com/v2/version_files", HttpMethod.Post,
            New JObject(New JProperty("algorithm", "sha1"), New JProperty("hashes", New JArray(Infos.Select(Function(Item) Item.Sha1).Distinct()))).ToString()), JObject)
        Dim Ids = Versions.Properties().Select(Function(Item) Item.Value("project_id")?.ToString()).Where(Function(Id) Not String.IsNullOrEmpty(Id)).Distinct().ToList()
        If Ids.Count = 0 Then Return
        Token.ThrowIfCancellationRequested()
        Dim Projects = DirectCast(Request("https://api.modrinth.com/v2/projects?ids=" & Uri.EscapeDataString(New JArray(Ids).ToString(Newtonsoft.Json.Formatting.None)), HttpMethod.Get, Nothing), JArray).
            OfType(Of JObject)().ToDictionary(Function(Item) Item("id").ToString())
        Dim Matched As New Dictionary(Of InstanceResourceOnlineInfo, JObject)
        For Each Info In Infos
            Dim Version = TryCast(Versions(Info.Sha1), JObject)
            If Version Is Nothing Then Continue For
            Dim Project As JObject = Nothing
            If Not Projects.TryGetValue(Version("project_id").ToString(), Project) OrElse Project("project_type")?.ToString() <> If(Kind = InstanceResourceKind.ResourcePacks, "resourcepack", "shader") Then Continue For
            Dim Current = ModrinthFile(Version, Kind, Info.Sha1)
            If Current Is Nothing Then Continue For
            Info.CurrentVersion = Current
            Info.ProjectName = Project("title")?.ToString()
            Info.IconUrl = Project("icon_url")?.ToString()
            Info.Website = "https://modrinth.com/" & If(Kind = InstanceResourceKind.ResourcePacks, "resourcepack/", "shader/") & Uri.EscapeDataString(Project("id").ToString())
            '副文件可能是不同分辨率或附加资源，不自动切换到主文件。
            Dim Files = DirectCast(Version("files"), JArray)
            If Files.Count > 1 AndAlso Not Files.Any(Function(Item) Item("hashes")?("sha1")?.ToString() = Info.Sha1 AndAlso Item("primary")?.ToObject(Of Boolean)() = True) Then Continue For
            Matched.Add(Info, Version)
        Next
        If Matched.Count = 0 Then Return
        Token.ThrowIfCancellationRequested()
        Dim Queries As New JArray(Matched.Select(Function(Pair) New JObject(New JProperty("hash", Pair.Key.Sha1), New JProperty("loaders", New JArray(Strings(Pair.Value("loaders")))), New JProperty("game_versions", New JArray(GameVersion)),
            New JProperty("version_types", If(Pair.Key.CurrentVersion.ReleaseType = ResourceVersion.ReleaseTypes.Release, New JArray("release"), If(Pair.Key.CurrentVersion.ReleaseType = ResourceVersion.ReleaseTypes.Beta, New JArray("release", "beta"), New JArray("release", "beta", "alpha")))))))
        Dim Updates = DirectCast(Request("https://api.modrinth.com/v2/version_files/update_individual", HttpMethod.Post, New JObject(New JProperty("algorithm", "sha1"), New JProperty("hashes", Queries)).ToString()), JObject)
        For Each Pair In Matched
            Dim Updated = TryCast(Updates(Pair.Key.Sha1), JObject)
            If Updated Is Nothing OrElse Updated("project_id")?.ToString() <> Pair.Value("project_id")?.ToString() OrElse Not Strings(Updated("game_versions")).Contains(GameVersion.ToLowerInvariant()) OrElse
                Not Strings(Updated("loaders")).SequenceEqual(Strings(Pair.Value("loaders"))) Then Continue For
            Consider(Pair.Key, Pair.Key.CurrentVersion, ModrinthFile(Updated, Kind))
        Next
    End Sub

    Private Shared Sub CheckCurseForge(Infos As List(Of InstanceResourceOnlineInfo), Kind As InstanceResourceKind, GameVersion As String, Token As CancellationToken,
        Request As Func(Of String, HttpMethod, String, JToken), Fingerprint As Func(Of String, UInteger))
        Dim Fingerprints = Infos.ToDictionary(Function(Item) Item, Function(Item) Fingerprint(Item.Entry.FullPath))
        Token.ThrowIfCancellationRequested()
        Dim Matches = DirectCast(Request("https://api.curseforge.com/v1/fingerprints/432", HttpMethod.Post, New JObject(New JProperty("fingerprints", New JArray(Fingerprints.Values.Distinct()))).ToString())("data")("exactMatches"), JArray)
        Dim Ids = Matches.Select(Function(Item) Item("id").ToObject(Of Integer)()).Distinct().ToList()
        If Ids.Count = 0 Then Return
        Token.ThrowIfCancellationRequested()
        Dim Projects = DirectCast(Request("https://api.curseforge.com/v1/mods", HttpMethod.Post, New JObject(New JProperty("modIds", New JArray(Ids))).ToString())("data"), JArray).
            OfType(Of JObject)().ToDictionary(Function(Item) Item("id").ToString())
        For Each Match As JObject In Matches
            Token.ThrowIfCancellationRequested()
            Dim Project As JObject = Nothing
            If Not Projects.TryGetValue(Match("id").ToString(), Project) OrElse Project("isAvailable")?.ToObject(Of Boolean)() = False OrElse Project("allowModDistribution")?.ToObject(Of Boolean)() = False Then Continue For
            Dim Website = Project("links")?("websiteUrl")?.ToString()
            Dim Address As Uri = Nothing
            If Not Uri.TryCreate(Website, UriKind.Absolute, Address) OrElse Address.Scheme <> "https" OrElse Not Address.IsDefaultPort OrElse Address.UserInfo <> "" OrElse Not {"www.curseforge.com", "curseforge.com"}.Contains(Address.Host, StringComparer.OrdinalIgnoreCase) Then Continue For
            Dim ExpectedPath = If(Kind = InstanceResourceKind.ShaderPacks, "/minecraft/shaders/", "/minecraft/texture-packs/")
            If Not Address.AbsolutePath.StartsWith(ExpectedPath, StringComparison.OrdinalIgnoreCase) AndAlso Not (Kind = InstanceResourceKind.ResourcePacks AndAlso Address.AbsolutePath.StartsWith("/minecraft/resourcepacks/", StringComparison.OrdinalIgnoreCase)) Then Continue For
            Dim RawCurrent = DirectCast(Match("file"), JObject)
            Dim MatchedInfos = Infos.Where(Function(Info) Fingerprints(Info) = RawCurrent("fileFingerprint").ToObject(Of UInteger)() AndAlso
                DirectCast(RawCurrent("hashes"), JArray).Any(Function(Hash) If(Hash("algo").ToObject(Of Integer)() = 1, Info.Sha1, If(Hash("algo").ToObject(Of Integer)() = 2, FileHash(Info.Entry.FullPath, True), "")) = Hash("value").ToString().ToLowerInvariant())).ToList()
            If MatchedInfos.Count = 0 Then Continue For
            Dim Current = ResourceVersion.FromPlatformJson(RawCurrent, ExpectedType(Kind))
            For Each Info In MatchedInfos
                If Info.CurrentVersion Is Nothing Then
                    Info.CurrentVersion = Current
                    Info.ProjectName = Project("name")?.ToString()
                    Dim ProjectLogo = TryCast(Project("logo"), JObject)
                    Info.IconUrl = ProjectLogo?("thumbnailUrl")?.ToString()
                    If String.IsNullOrWhiteSpace(Info.IconUrl) Then Info.IconUrl = ProjectLogo?("url")?.ToString()
                    Info.Tags = If(TryCast(Project("categories"), JArray), New JArray).Select(Function(Category) Category("name")?.ToString()).Where(Function(Name) Not String.IsNullOrWhiteSpace(Name)).Take(3).ToList()
                    Info.Website = Website
                End If
            Next
            Dim Index As Integer = 0
            Do
                Token.ThrowIfCancellationRequested()
                Dim Page = Request($"https://api.curseforge.com/v1/mods/{Match("id")}/files?gameVersion={Uri.EscapeDataString(GameVersion)}&pageSize=50&index={Index}", HttpMethod.Get, Nothing)
                Dim Files = DirectCast(Page("data"), JArray)
                For Each Raw As JObject In Files
                    If Raw("modId")?.ToString() <> Match("id").ToString() OrElse Raw("isAvailable")?.ToObject(Of Boolean)() = False OrElse String.IsNullOrWhiteSpace(Raw("downloadUrl")?.ToString()) OrElse Not Strings(Raw("gameVersions")).Contains(GameVersion.ToLowerInvariant()) Then Continue For
                    Dim Candidate = ResourceVersion.FromPlatformJson(Raw, ExpectedType(Kind))
                    For Each Info In MatchedInfos
                        Consider(Info, Current, Candidate)
                    Next
                Next
                Index += Files.Count
                If Files.Count < 50 OrElse Index >= If(Page("pagination")?("totalCount")?.ToObject(Of Integer)(), Index) Then Exit Do
            Loop While Index < 10000
        Next
    End Sub

    Friend Shared Sub Install(Root As String, Infos As IEnumerable(Of InstanceResourceOnlineInfo), Recycle As String, Kind As InstanceResourceKind, GameVersion As String, Token As CancellationToken,
        Optional Download As Action(Of List(Of NetFile), CancellationToken) = Nothing)
        Dim Items = Infos.ToList()
        If Not Supported(Kind) OrElse Items.Count = 0 Then Return
        Dim Stage = Path.Combine(Root, ".pcl-import-" & Guid.NewGuid().ToString("N"))
        InstanceResourceStore.EnsureWithin(Root, Stage)
        Directory.CreateDirectory(Stage)
        Try
            Dim Downloads As New List(Of NetFile)
            For Index = 0 To Items.Count - 1
                Dim Info = Items(Index)
                InstanceResourceStore.ValidateEntry(Root, Info.Entry)
                If Info.Entry.IsDirectory OrElse Not Info.HasUpdate OrElse Not ValidFile(Info.UpdateVersion) OrElse FileHash(Info.Entry.FullPath) <> Info.Sha1 Then Throw New IOException("资源已变化，请重新检查更新。")
                Downloads.Add(Info.UpdateVersion.ToNetFile(Path.Combine(Stage, Index & ".zip"), ResourceVersion.DownloadReason.Update, GameVersion))
            Next
            If Download Is Nothing Then Download = Sub(Files, Cancellation)
                                                     Dim Loader As New LoaderDownload("下载资源更新", Files)
                                                     Using Registration = Cancellation.Register(AddressOf Loader.Cancel)
                                                         Loader.WaitForExit(c:=Cancellation)
                                                     End Using
                                                 End Sub
            Download(Downloads, Token)
            '所有文件下载并校验后再开始替换；保留原文件名，避免游戏中已选资源包失效。
            For Index = 0 To Items.Count - 1
                Token.ThrowIfCancellationRequested()
                Dim Info = Items(Index), FileName = Path.Combine(Stage, Index & ".zip")
                If Not File.Exists(FileName) OrElse New FileInfo(FileName).Length <> Info.UpdateVersion.Size OrElse FileHash(FileName, Info.UpdateVersion.Hash.Length = 32) <> Info.UpdateVersion.Hash Then Throw New IOException("资源更新文件校验失败，原文件已保留。")
                InstanceResourceStore.ValidatePackage(FileName, Kind)
            Next
            For Index = 0 To Items.Count - 1
                Token.ThrowIfCancellationRequested()
                Dim Info = Items(Index)
                InstanceResourceStore.ValidateEntry(Root, Info.Entry)
                If FileHash(Info.Entry.FullPath) <> Info.Sha1 Then Throw New IOException("原资源已变化，已停止替换。")
                Dim Box = InstanceResourceStore.MoveToRecycle(Root, Info.Entry, Recycle)
                Try
                    File.Move(Path.Combine(Stage, Index & ".zip"), Info.Entry.FullPath)
                Catch
                    If Not File.Exists(Info.Entry.FullPath) Then
                        File.Move(Path.Combine(Box, "content"), Info.Entry.FullPath)
                        File.Delete(Path.Combine(Box, "resource.json"))
                        Directory.Delete(Box, False)
                    End If
                    Throw
                End Try
            Next
        Finally
            InstanceResourceStore.DeleteStage(Root, Stage)
        End Try
    End Sub
End Class
