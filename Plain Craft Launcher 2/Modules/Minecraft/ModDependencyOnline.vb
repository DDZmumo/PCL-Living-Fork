Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

'Only supplements unread/incomplete local metadata. Platform project IDs never enter the local loader graph.
Friend NotInheritable Class ModDependencyOnline
    Private Sub New()
    End Sub

    Friend Class LookupOptions
        Public Request As Func(Of String, HttpMethod, String, CancellationToken, JToken)
        Public Fingerprint As Func(Of String, UInteger)
        Public Clock As Func(Of DateTime) = Function() DateTime.UtcNow
        Public Cache As LookupCache
        'A detail view may explicitly request an unread disabled file; startup leaves this unset.
        Public TargetFile As String
        Public TimeBudgetMs As Integer = 6000
        Public RequestTimeoutMs As Integer = 2000
        Public MaxRequests As Integer = 12
        Public MaxFiles As Integer = 64
        Public MaxSearchFiles As Integer = 3
        Public CandidatesPerPlatform As Integer = 3
        Public MaxFileBytes As Long = 128L * 1024 * 1024
        Public MaxTotalBytes As Long = 256L * 1024 * 1024
        Public CacheLifetime As TimeSpan = TimeSpan.FromHours(6)
        Public NegativeCacheLifetime As TimeSpan = TimeSpan.FromMinutes(10)
    End Class

    Friend Class LookupCache
        Private Class CacheEntry
            Public Data As JToken
            Public Expires As DateTime
            Public Created As DateTime
        End Class
        Private ReadOnly Entries As New Dictionary(Of String, CacheEntry)(StringComparer.Ordinal)
        Private ReadOnly Gate As New Object
        Friend Function Read(Key As String, Now As DateTime) As JToken
            SyncLock Gate
                Dim Value As CacheEntry = Nothing
                If Not Entries.TryGetValue(Key, Value) Then Return Nothing
                If Value.Expires <= Now Then
                    Entries.Remove(Key)
                    Return Nothing
                End If
                Return Value.Data.DeepClone()
            End SyncLock
        End Function
        Friend Sub Write(Key As String, Data As JToken, Now As DateTime, Lifetime As TimeSpan)
            SyncLock Gate
                For Each Expired In Entries.Where(Function(Pair) Pair.Value.Expires <= Now).Select(Function(Pair) Pair.Key).ToArray()
                    Entries.Remove(Expired)
                Next
                If Entries.Count >= 512 AndAlso Not Entries.ContainsKey(Key) Then Entries.Remove(Entries.OrderBy(Function(Pair) Pair.Value.Created).First().Key)
                Entries(Key) = New CacheEntry With {.Data = Data.DeepClone(), .Created = Now, .Expires = Now.Add(Lifetime)}
            End SyncLock
        End Sub
        Friend Sub Clear()
            SyncLock Gate
                Entries.Clear()
            End SyncLock
        End Sub
    End Class
    Private Shared ReadOnly SharedCache As New LookupCache

    Private Class FileData
        Public Info As ModDependencyInspector.OnlineFileInfo
        Public Md5 As String
        Public Length As Long
        Public FilteredLength As Long
        Public Fingerprint As UInteger
        Public HasFingerprint As Boolean
    End Class
    Private Class LookupBudgetException
        Inherits Exception
    End Class
    Private Class HttpFailure
        Inherits Exception
        Public Code As Integer
        Public RetrySeconds As Integer
        Public Sub New(Code As Integer, RetrySeconds As Integer)
            MyBase.New("HTTP " & Code)
            Me.Code = Code
            Me.RetrySeconds = RetrySeconds
        End Sub
    End Class
    Private Class Session
        Implements IDisposable
        Public ReadOnly Options As LookupOptions
        Public ReadOnly Report As ModDependencyInspector.InspectionReport
        Public ReadOnly Cache As LookupCache
        Public ReadOnly CallerToken As CancellationToken
        Public ReadOnly Deadline As CancellationTokenSource
        Public ReadOnly TargetFile As String
        Public ReadOnly Timer As Stopwatch = Stopwatch.StartNew()
        Private ReadOnly Blocked As New HashSet(Of String)(StringComparer.Ordinal)
        Public Sub New(Report As ModDependencyInspector.InspectionReport, Token As CancellationToken, Options As LookupOptions, TargetFile As String)
            Me.Report = Report
            Me.Options = Options
            Me.TargetFile = TargetFile
            CallerToken = Token
            Cache = If(Options.Cache, SharedCache)
            Deadline = CancellationTokenSource.CreateLinkedTokenSource(Token)
            Deadline.CancelAfter(Math.Max(100, Math.Min(10000, Options.TimeBudgetMs)))
        End Sub
        Public Function Now() As DateTime
            Return If(Options.Clock, Function() DateTime.UtcNow)().ToUniversalTime()
        End Function
        Public Sub Check()
            Deadline.Token.ThrowIfCancellationRequested()
            If Timer.ElapsedMilliseconds >= Math.Max(100, Math.Min(10000, Options.TimeBudgetMs)) Then Throw New OperationCanceledException(Deadline.Token)
        End Sub
        Public Function Ask(Platform As String, Url As String, Method As HttpMethod, Body As String) As JToken
            Check()
            If Not IsAllowedApi(Url) Then Throw New ArgumentException("Unsupported dependency API address.")
            If Blocked.Contains(Platform) Then Return Nothing
            If Cache.Read("cooldown|" & Platform, Now()) IsNot Nothing Then
                Note(Platform & " 暂缓请求；已保留缓存和本地结果。")
                Blocked.Add(Platform)
                Return Nothing
            End If
            If Report.OnlineRequests >= Math.Max(1, Math.Min(20, Options.MaxRequests)) Then Throw New LookupBudgetException()
            Report.OnlineRequests += 1
            Using RequestDeadline = CancellationTokenSource.CreateLinkedTokenSource(Deadline.Token)
                RequestDeadline.CancelAfter(Math.Max(50, Math.Min(3000, Options.RequestTimeoutMs)))
                Try
                    Dim Request = If(Options.Request, AddressOf DefaultRequest)
                    Dim Result = Request(Url, Method, Body, RequestDeadline.Token)
                    Check()
                    Return Result
                Catch ex As OperationCanceledException
                    If Deadline.IsCancellationRequested OrElse CallerToken.IsCancellationRequested Then Throw
                    Blocked.Add(Platform)
                    Cache.Write("cooldown|" & Platform, New JValue(True), Now(), TimeSpan.FromSeconds(30))
                    Note(Platform & " 查询超时；保留本地检查结果。")
                    Return Nothing
                Catch ex As HttpFailure
                    Blocked.Add(Platform)
                    Dim Seconds = If(ex.Code = 429, Math.Max(60, Math.Min(900, ex.RetrySeconds)), 30)
                    Cache.Write("cooldown|" & Platform, New JValue(True), Now(), TimeSpan.FromSeconds(Seconds))
                    Note(Platform & If(ex.Code = 429, " 请求过于频繁，已停止本轮查询。", " 查询失败（HTTP " & ex.Code & "）；保留本地结果。"))
                    Return Nothing
                Catch ex As Exception
                    Blocked.Add(Platform)
                    Cache.Write("cooldown|" & Platform, New JValue(True), Now(), TimeSpan.FromSeconds(30))
                    Note(Platform & " 暂不可用；保留本地检查结果。")
                    Return Nothing
                End Try
            End Using
        End Function
        Public Sub Note(Value As String)
            If Not Report.OnlineNotes.Contains(Value) Then Report.OnlineNotes.Add(Value)
        End Sub
        Public Function Lifetime(Positive As Boolean) As TimeSpan
            Dim Value = If(Positive, Options.CacheLifetime, Options.NegativeCacheLifetime)
            Return TimeSpan.FromSeconds(Math.Max(1, Math.Min(If(Positive, 86400, 3600), Value.TotalSeconds)))
        End Function
        Public Sub Dispose() Implements IDisposable.Dispose
            Deadline.Dispose()
        End Sub
    End Class

    Friend Shared Function InspectWithOnline(ModsFolder As String, Optional Context As ModDependencyInspector.InspectionContext = Nothing,
                                            Optional Token As CancellationToken = Nothing, Optional Options As LookupOptions = Nothing) As ModDependencyInspector.InspectionReport
        Return Enrich(ModDependencyInspector.Inspect(ModsFolder, Context, Token), ModsFolder, Token, Options)
    End Function

    Friend Shared Function Enrich(Report As ModDependencyInspector.InspectionReport, ModsFolder As String,
                                 Optional Token As CancellationToken = Nothing, Optional Options As LookupOptions = Nothing) As ModDependencyInspector.InspectionReport
        If Report Is Nothing Then Throw New ArgumentNullException(NameOf(Report))
        Dim Settings = If(Options, New LookupOptions())
        Dim TargetFile = ExplicitTarget(Settings.TargetFile, ModsFolder, Report)
        If Not Report.MetadataIncomplete AndAlso TargetFile Is Nothing Then Return Report
        Report.OnlineAttempted = True
        Report.OnlineCancelled = False
        Report.OnlineTimedOut = False
        Report.OnlineRequests = 0
        Report.OnlineFiles.Clear()
        Report.OnlineNotes.Clear()
        Using Work As New Session(Report, Token, Settings, TargetFile)
            Report.OnlineCheckedAtUtc = Work.Now()
            Try
                Work.Check()
                Dim Files = ReadFiles(ModsFolder, Work)
                If Not Files.Any(Function(Item) Item.Info.IsSupplementTarget) Then
                    Work.Note("没有找到适合联网补充的本地文件。")
                    Return Report
                End If
                Report.OnlineFiles.AddRange(Files.Select(Function(Item) Item.Info))
                MatchModrinth(Files, Work)
                MatchCurseForge(Files, Work)
                ResolveVersionProjects(Files, Work)
                ResolveProjects(Files, "Modrinth", Work)
                ResolveProjects(Files, "CurseForge", Work)
                SearchCandidates(Files, Work)
            Catch ex As OperationCanceledException
                Report.OnlineCancelled = Token.IsCancellationRequested
                Report.OnlineTimedOut = Not Report.OnlineCancelled
                Work.Note(If(Report.OnlineCancelled, "联网补充已取消，本地检查结果已保留。", "联网补充达到总时间预算，已停止后续请求。"))
            Catch ex As LookupBudgetException
                Work.Note("联网补充达到请求数量上限，已停止后续请求。")
            Catch ex As Exception
                Work.Note("联网补充未完成，本地检查结果已保留。")
            Finally
                MapRelations(Report)
            End Try
        End Using
        Return Report
    End Function

    Private Shared Function ReadFiles(ModsFolder As String, Work As Session) As List(Of FileData)
        Dim Result As New List(Of FileData), Paths As New List(Of String), Root = Path.GetFullPath(ModsFolder)
        If Not Directory.Exists(Root) OrElse (File.GetAttributes(Root) And FileAttributes.ReparsePoint) <> 0 Then Return Result
        Paths.AddRange(Directory.EnumerateFiles(Root, "*", SearchOption.TopDirectoryOnly).Where(AddressOf IsModFile))
        Dim Legacy = Work.Report.Context?.LegacyForgeVersionFolder
        If Not String.IsNullOrEmpty(Legacy) AndAlso Regex.IsMatch(Legacy, "^[0-9.]+$") Then
            Dim Folder = Path.Combine(Root, Legacy)
            If Directory.Exists(Folder) AndAlso (File.GetAttributes(Folder) And FileAttributes.ReparsePoint) = 0 Then Paths.AddRange(Directory.EnumerateFiles(Folder, "*", SearchOption.TopDirectoryOnly).Where(AddressOf IsModFile))
        End If
        Dim Targets = Paths.ToDictionary(Function(Value) Value,
            Function(Value) If(Work.TargetFile IsNot Nothing, String.Equals(Value, Work.TargetFile, StringComparison.OrdinalIgnoreCase),
                IsEnabledFile(Value) AndAlso NeedsSupplement(Value, Work.Report)), StringComparer.OrdinalIgnoreCase)
        If Not Targets.Values.Any(Function(Value) Value) Then Return Result
        Dim Total As Long
        For Each FullPath In Paths.OrderByDescending(Function(Value) Targets(Value)).ThenBy(Function(Value) Value, StringComparer.OrdinalIgnoreCase).Take(Math.Max(1, Math.Min(128, Work.Options.MaxFiles)))
            Work.Check()
            Try
                Dim Info As New FileInfo(FullPath)
                If (Info.Attributes And FileAttributes.ReparsePoint) <> 0 Then Continue For
                If Info.Length > Math.Max(1, Work.Options.MaxFileBytes) OrElse Info.Length > Math.Max(1, Work.Options.MaxTotalBytes) - Total Then
                    Work.Note("部分文件超过联网哈希读取上限，未据此判定缺失依赖。")
                    Continue For
                End If
                Total += Info.Length
                Result.Add(HashFile(Info, Targets(FullPath), Work))
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Work.Note("部分本地文件无法读取，未据此判定缺失依赖。")
            End Try
        Next
        If Paths.Count > Math.Max(1, Math.Min(128, Work.Options.MaxFiles)) Then Work.Note("联网补充达到文件数量上限；本次平台映射可能不完整。")
        Return Result
    End Function
    Private Shared Function IsModFile(Value As String) As Boolean
        If Value.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) OrElse Value.EndsWith(".old", StringComparison.OrdinalIgnoreCase) Then Value = Path.GetFileNameWithoutExtension(Value)
        Return {".jar", ".zip", ".litemod"}.Contains(Path.GetExtension(Value).ToLowerInvariant())
    End Function
    Private Shared Function IsEnabledFile(Value As String) As Boolean
        Return Not Value.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) AndAlso Not Value.EndsWith(".old", StringComparison.OrdinalIgnoreCase)
    End Function
    Private Shared Function ExplicitTarget(Value As String, ModsFolder As String, Report As ModDependencyInspector.InspectionReport) As String
        If String.IsNullOrWhiteSpace(Value) Then Return Nothing
        Try
            Dim FullPath = Path.GetFullPath(Value), Root = Path.GetFullPath(ModsFolder), Parent = Path.GetDirectoryName(FullPath)
            If Not File.Exists(FullPath) OrElse Not Directory.Exists(Root) OrElse Not IsModFile(FullPath) Then Return Nothing
            Dim Allowed = String.Equals(Parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            Dim Legacy = Report.Context?.LegacyForgeVersionFolder
            If Not Allowed AndAlso Not String.IsNullOrEmpty(Legacy) AndAlso Regex.IsMatch(Legacy, "^[0-9.]+$") Then
                Allowed = String.Equals(Parent, Path.GetFullPath(Path.Combine(Root, Legacy)), StringComparison.OrdinalIgnoreCase)
            End If
            If Not Allowed OrElse (File.GetAttributes(Root) And FileAttributes.ReparsePoint) <> 0 OrElse
                (File.GetAttributes(Parent) And FileAttributes.ReparsePoint) <> 0 OrElse (File.GetAttributes(FullPath) And FileAttributes.ReparsePoint) <> 0 Then Return Nothing
            Return If(NeedsSupplement(FullPath, Report), FullPath, Nothing)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
            TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException OrElse TypeOf ex Is Security.SecurityException
            Return Nothing
        End Try
    End Function
    Private Shared Function NeedsSupplement(FullPath As String, Report As ModDependencyInspector.InspectionReport) As Boolean
        Dim Mods = Report.Mods.Where(Function(Item) String.Equals(Item.RootPath, FullPath, StringComparison.OrdinalIgnoreCase)).ToList()
        If Mods.Count = 0 OrElse Mods.Any(Function(Item) Item.Format = "LiteLoader" OrElse Item.Format = "Forge (旧版)") Then Return True
        Dim Name = Path.GetFileName(FullPath)
        Return Report.Notices.Any(Function(Value) Value.StartsWith(Name & "：", StringComparison.OrdinalIgnoreCase) OrElse Value.StartsWith(Name & "!", StringComparison.OrdinalIgnoreCase))
    End Function
    Private Shared Function HashFile(Info As FileInfo, Target As Boolean, Work As Session) As FileData
        Dim Length = Info.Length, Written = Info.LastWriteTimeUtc, Result As New FileData With {.Length = Length,
            .Info = New ModDependencyInspector.OnlineFileInfo With {.FullPath = Info.FullName, .IsSupplementTarget = Target,
                .Enabled = Not Info.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) AndAlso Not Info.Name.EndsWith(".old", StringComparison.OrdinalIgnoreCase)}}
        Using Input As New FileStream(Info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read), Sha = SHA1.Create(), Md5 = Security.Cryptography.MD5.Create()
            Dim Buffer(65535) As Byte, Count As Integer
            Do
                Work.Check()
                Count = Input.Read(Buffer, 0, Buffer.Length)
                If Count = 0 Then Exit Do
                Sha.TransformBlock(Buffer, 0, Count, Buffer, 0)
                Md5.TransformBlock(Buffer, 0, Count, Buffer, 0)
                For Index = 0 To Count - 1
                    If Not IsWhitespace(Buffer(Index)) Then Result.FilteredLength += 1
                Next
            Loop
            Sha.TransformFinalBlock(New Byte() {}, 0, 0)
            Md5.TransformFinalBlock(New Byte() {}, 0, 0)
            Result.Info.Sha1 = Hex(Sha.Hash)
            Result.Md5 = Hex(Md5.Hash)
        End Using
        Info.Refresh()
        If Info.Length <> Length OrElse Info.LastWriteTimeUtc <> Written Then Throw New IOException("File changed during hashing.")
        Return Result
    End Function
    Private Shared Function Hex(Bytes As Byte()) As String
        Return BitConverter.ToString(Bytes).Replace("-", "").ToLowerInvariant()
    End Function
    Private Shared Function IsWhitespace(Value As Byte) As Boolean
        Return Value = 9 OrElse Value = 10 OrElse Value = 13 OrElse Value = 32
    End Function
    'Same seed/filter/MurmurHash2 as LocalResourceFile.CurseForgeHash, streamed without writing its INI cache.
    Private Shared Function Fingerprint(File As FileData, Work As Session) As UInteger
        If File.HasFingerprint Then Return File.Fingerprint
        If Work.Options.Fingerprint IsNot Nothing Then
            File.Fingerprint = Work.Options.Fingerprint(File.Info.FullPath)
        Else
            Dim Hash As UInteger = 1UI Xor CUInt(File.FilteredLength), Word As UInteger, Count As Integer
            Using Input = IO.File.OpenRead(File.Info.FullPath)
                Dim Buffer(65535) As Byte, Read As Integer
                Do
                    Work.Check()
                    Read = Input.Read(Buffer, 0, Buffer.Length)
                    If Read = 0 Then Exit Do
                    For Index = 0 To Read - 1
                        If IsWhitespace(Buffer(Index)) Then Continue For
                        Word = Word Or (CUInt(Buffer(Index)) << (8 * Count))
                        Count += 1
                        If Count <> 4 Then Continue For
                        Word = CUInt((CLng(Word) * &H5BD1E995L) And &HFFFFFFFFL)
                        Word = Word Xor (Word >> 24)
                        Word = CUInt((CLng(Word) * &H5BD1E995L) And &HFFFFFFFFL)
                        Hash = CUInt((CLng(Hash) * &H5BD1E995L) And &HFFFFFFFFL) Xor Word
                        Word = 0UI
                        Count = 0
                    Next
                Loop
            End Using
            If Count > 0 Then Hash = CUInt((CLng(Hash Xor Word) * &H5BD1E995L) And &HFFFFFFFFL)
            Hash = Hash Xor (Hash >> 13)
            Hash = CUInt((CLng(Hash) * &H5BD1E995L) And &HFFFFFFFFL)
            File.Fingerprint = Hash Xor (Hash >> 15)
        End If
        File.HasFingerprint = True
        Return File.Fingerprint
    End Function

    Private Shared Function CachedMatches(File As FileData, Platform As String, Work As Session) As Boolean
        Dim Value = Work.Cache.Read("file|" & Platform & "|" & File.Info.Sha1, Work.Now())
        If Value Is Nothing Then Return False
        File.Info.Matches.AddRange(Value.ToObject(Of List(Of ModDependencyInspector.OnlineProjectInfo))())
        Return True
    End Function
    Private Shared Sub StoreMatches(File As FileData, Platform As String, Work As Session)
        Dim Values = File.Info.Matches.Where(Function(Item) Item.Platform = Platform).ToList()
        Work.Cache.Write("file|" & Platform & "|" & File.Info.Sha1, JArray.FromObject(Values), Work.Now(), Work.Lifetime(Values.Count > 0))
    End Sub
    Private Shared Sub MatchModrinth(Files As List(Of FileData), Work As Session)
        Dim Pending = Files.Where(Function(Item) Not CachedMatches(Item, "Modrinth", Work)).ToList()
        If Pending.Count = 0 Then Return
        Dim Response = TryCast(Work.Ask("Modrinth", "https://api.modrinth.com/v2/version_files", HttpMethod.Post,
            New JObject(New JProperty("algorithm", "sha1"), New JProperty("hashes", New JArray(Pending.Select(Function(Item) Item.Info.Sha1).Distinct()))).ToString(Formatting.None)), JObject)
        If Response Is Nothing Then Return
        For Each File In Pending
            Dim Version = TryCast(Response(File.Info.Sha1), JObject)
            If Version IsNot Nothing AndAlso ValidModrinthId(S(Version("project_id"))) AndAlso ValidModrinthId(S(Version("id"))) AndAlso
                A(Version("files")).Any(Function(Item) S(Item("hashes")?("sha1")).Equals(File.Info.Sha1, StringComparison.OrdinalIgnoreCase) AndAlso
                    (Item("size") Is Nothing OrElse S(Item("size")) = File.Length.ToString(Globalization.CultureInfo.InvariantCulture))) Then
                Dim Match As New ModDependencyInspector.OnlineProjectInfo With {.Platform = "Modrinth", .ProjectId = S(Version("project_id")), .VersionId = S(Version("id")),
                    .ProjectName = S(Version("project_id")), .VersionName = Caption(S(Version("version_number"))), .IsExact = True, .MatchBasis = "SHA1 精确文件匹配",
                    .Website = "https://modrinth.com/mod/" & Uri.EscapeDataString(S(Version("project_id")))}
                For Each Requirement In A(Version("dependencies")).Take(64)
                    Dim Id = S(Requirement("project_id")), VersionId = S(Requirement("version_id")), Kind = S(Requirement("dependency_type"))
                    If Not {"required", "optional", "incompatible", "embedded"}.Contains(Kind) Then Continue For
                    Match.Dependencies.Add(New ModDependencyInspector.OnlineDependency With {.Platform = "Modrinth", .ProjectId = If(ValidModrinthId(Id), Id, ""),
                        .VersionId = If(ValidModrinthId(VersionId), VersionId, ""), .FileName = Caption(S(Requirement("file_name"))), .Kind = Kind})
                Next
                File.Info.Matches.Add(Match)
            End If
            StoreMatches(File, "Modrinth", Work)
        Next
    End Sub
    Private Shared Sub MatchCurseForge(Files As List(Of FileData), Work As Session)
        Dim Pending = Files.Where(Function(Item) Not CachedMatches(Item, "CurseForge", Work)).ToList()
        If Pending.Count = 0 Then Return
        For Each File In Pending
            Work.Check()
            Fingerprint(File, Work)
        Next
        Dim Response = Work.Ask("CurseForge", "https://api.curseforge.com/v1/fingerprints/432", HttpMethod.Post,
            New JObject(New JProperty("fingerprints", New JArray(Pending.Select(Function(Item) Item.Fingerprint).Distinct()))).ToString(Formatting.None))
        If Response Is Nothing Then Return
        For Each Raw In A(Response("data")?("exactMatches"))
            Dim Version = TryCast(Raw("file"), JObject), Id = S(Raw("id"))
            If Version Is Nothing OrElse Not ValidCurseForgeId(Id) OrElse S(Version("modId")) <> Id OrElse Not ValidCurseForgeId(S(Version("id"))) Then Continue For
            If Version("gameId") IsNot Nothing AndAlso S(Version("gameId")) <> "432" Then Continue For
            For Each File In Pending
                If S(Version("fileFingerprint")) <> File.Fingerprint.ToString(Globalization.CultureInfo.InvariantCulture) OrElse
                    (Version("fileLength") IsNot Nothing AndAlso S(Version("fileLength")) <> File.Length.ToString(Globalization.CultureInfo.InvariantCulture)) OrElse
                    Not A(Version("hashes")).Any(Function(Hash) (S(Hash("algo")) = "1" AndAlso S(Hash("value")).Equals(File.Info.Sha1, StringComparison.OrdinalIgnoreCase)) OrElse
                        (S(Hash("algo")) = "2" AndAlso S(Hash("value")).Equals(File.Md5, StringComparison.OrdinalIgnoreCase))) Then Continue For
                Dim Match As New ModDependencyInspector.OnlineProjectInfo With {.Platform = "CurseForge", .ProjectId = Id, .VersionId = S(Version("id")), .ProjectName = Id,
                    .VersionName = Caption(S(Version("displayName"))), .IsExact = True, .MatchBasis = "指纹及内容哈希精确匹配", .Website = "https://www.curseforge.com/projects/" & Id}
                For Each Requirement In A(Version("dependencies")).Take(64)
                    Dim DependencyId = S(Requirement("modId")), Kind As String
                    Select Case S(Requirement("relationType"))
                        Case "2" : Kind = "optional"
                        Case "3" : Kind = "required"
                        Case "5" : Kind = "incompatible"
                        Case "1", "6" : Kind = "embedded"
                        Case Else : Continue For
                    End Select
                    If ValidCurseForgeId(DependencyId) Then Match.Dependencies.Add(New ModDependencyInspector.OnlineDependency With {.Platform = "CurseForge", .ProjectId = DependencyId, .Kind = Kind})
                Next
                If Not File.Info.Matches.Any(Function(Item) Item.Platform = "CurseForge" AndAlso Item.ProjectId = Id AndAlso Item.VersionId = Match.VersionId) Then File.Info.Matches.Add(Match)
            Next
        Next
        For Each File In Pending
            StoreMatches(File, "CurseForge", Work)
        Next
    End Sub

    Private Shared Sub ResolveVersionProjects(Files As List(Of FileData), Work As Session)
        Dim Requirements = Files.SelectMany(Function(File) File.Info.Matches).Where(Function(Match) Match.Platform = "Modrinth").SelectMany(Function(Match) Match.Dependencies).
            Where(Function(Item) Item.ProjectId = "" AndAlso ValidModrinthId(Item.VersionId)).ToList()
        Dim Ids As New List(Of String)
        For Each Requirement In Requirements
            Dim Cached = Work.Cache.Read("version|Modrinth|" & Requirement.VersionId, Work.Now())
            If Cached IsNot Nothing Then
                Requirement.ProjectId = S(Cached)
            ElseIf Not Ids.Contains(Requirement.VersionId) Then
                Ids.Add(Requirement.VersionId)
            End If
        Next
        If Ids.Count = 0 Then Return
        Dim Response = Work.Ask("Modrinth", "https://api.modrinth.com/v2/versions?ids=" & JsonIds(Ids.Take(100)), HttpMethod.Get, Nothing)
        If Response Is Nothing Then Return
        For Each Version In A(Response)
            Dim Id = S(Version("id")), Project = S(Version("project_id"))
            If Not Ids.Contains(Id) OrElse Not ValidModrinthId(Project) Then Continue For
            Work.Cache.Write("version|Modrinth|" & Id, New JValue(Project), Work.Now(), Work.Lifetime(True))
            For Each Requirement In Requirements.Where(Function(Item) Item.VersionId = Id)
                Requirement.ProjectId = Project
            Next
        Next
    End Sub
    Private Shared Sub ResolveProjects(Files As List(Of FileData), Platform As String, Work As Session)
        Dim Matches = Files.SelectMany(Function(File) File.Info.Matches).Where(Function(Match) Match.Platform = Platform).ToList()
        Dim Requirements = Matches.SelectMany(Function(Match) Match.Dependencies).ToList()
        Dim Ids = Matches.Select(Function(Match) Match.ProjectId).Concat(Requirements.Select(Function(Item) Item.ProjectId)).Where(Function(Id) Id <> "").Distinct().Take(100).ToList()
        Dim Projects As New Dictionary(Of String, JObject)(StringComparer.Ordinal), Pending As New List(Of String)
        For Each Id In Ids
            Dim Cached = TryCast(Work.Cache.Read("project|" & Platform & "|" & Id, Work.Now()), JObject)
            If Cached IsNot Nothing Then
                Projects(Id) = Cached
            Else
                Pending.Add(Id)
            End If
        Next
        If Pending.Count > 0 Then
            Dim Response As JToken
            If Platform = "Modrinth" Then
                Response = Work.Ask(Platform, "https://api.modrinth.com/v2/projects?ids=" & JsonIds(Pending), HttpMethod.Get, Nothing)
            Else
                Response = Work.Ask(Platform, "https://api.curseforge.com/v1/mods", HttpMethod.Post,
                    New JObject(New JProperty("modIds", New JArray(Pending.Select(Function(Id) Integer.Parse(Id, Globalization.CultureInfo.InvariantCulture))))).ToString(Formatting.None))
                Response = Response?("data")
            End If
            For Each Project In A(Response)
                Dim Id = S(Project("id"))
                If Not Pending.Contains(Id) Then Continue For
                Dim Valid = If(Platform = "Modrinth", S(Project("project_type")) = "mod", IsCurseForgeModProject(Project))
                Dim Title = Caption(If(Platform = "Modrinth", S(Project("title")), S(Project("name"))))
                Dim Website = If(Platform = "Modrinth", "https://modrinth.com/mod/" & Uri.EscapeDataString(Id), If(IsCurseForgeWebsite(S(Project("links")?("websiteUrl"))), S(Project("links")("websiteUrl")), "https://www.curseforge.com/projects/" & Id))
                Dim Slim As New JObject(New JProperty("valid", Valid), New JProperty("title", Title), New JProperty("website", Website))
                Projects(Id) = Slim
                Work.Cache.Write("project|" & Platform & "|" & Id, Slim, Work.Now(), Work.Lifetime(True))
            Next
        End If
        For Each File In Files
            File.Info.Matches.RemoveAll(Function(Match) Match.Platform = Platform AndAlso Projects.ContainsKey(Match.ProjectId) AndAlso Not Projects(Match.ProjectId)("valid").Value(Of Boolean)())
        Next
        For Each Match In Matches
            Dim Project As JObject = Nothing
            If Projects.TryGetValue(Match.ProjectId, Project) AndAlso S(Project("title")) <> "" Then
                Match.ProjectName = S(Project("title"))
                Match.Website = S(Project("website"))
            End If
        Next
        For Each Requirement In Requirements
            Dim Project As JObject = Nothing
            If Projects.TryGetValue(Requirement.ProjectId, Project) AndAlso Project("valid").Value(Of Boolean)() Then
                Requirement.ProjectName = S(Project("title"))
                Requirement.Website = S(Project("website"))
            ElseIf Requirement.ProjectId <> "" Then
                Requirement.ProjectName = Requirement.ProjectId
                Requirement.Website = If(Platform = "Modrinth", "https://modrinth.com/mod/" & Uri.EscapeDataString(Requirement.ProjectId), "https://www.curseforge.com/projects/" & Requirement.ProjectId)
            End If
        Next
    End Sub
    Private Shared Sub SearchCandidates(Files As List(Of FileData), Work As Session)
        For Each File In Files.Where(Function(Item) Item.Info.IsSupplementTarget AndAlso Item.Info.Matches.Count = 0).Take(Math.Max(0, Math.Min(4, Work.Options.MaxSearchFiles)))
            Dim Query = SearchQuery(File.Info.FullPath)
            If Query.Length < 3 Then Continue For
            For Each Platform In {"Modrinth", "CurseForge"}
                Work.Check()
                Dim Key = "search|" & Platform & "|" & File.Info.Sha1 & "|" & Query, Cached = Work.Cache.Read(Key, Work.Now())
                If Cached IsNot Nothing Then
                    File.Info.Candidates.AddRange(Cached.ToObject(Of List(Of ModDependencyInspector.OnlineProjectInfo))())
                    Continue For
                End If
                Dim Response As JToken, Values As IEnumerable(Of JToken)
                If Platform = "Modrinth" Then
                    Response = Work.Ask(Platform, "https://api.modrinth.com/v2/search?query=" & Uri.EscapeDataString(Query) & "&limit=3&facets=" &
                        Uri.EscapeDataString("[[""project_type:mod""]]"), HttpMethod.Get, Nothing)
                    Values = A(Response?("hits"))
                Else
                    Response = Work.Ask(Platform, "https://api.curseforge.com/v1/mods/search?gameId=432&classId=6&pageSize=3&searchFilter=" & Uri.EscapeDataString(Query), HttpMethod.Get, Nothing)
                    Values = A(Response?("data"))
                End If
                If Response Is Nothing Then Continue For
                Dim Candidates As New List(Of ModDependencyInspector.OnlineProjectInfo)
                For Each Project In Values.Take(Math.Max(1, Math.Min(3, Work.Options.CandidatesPerPlatform)))
                    Dim Id = If(Platform = "Modrinth", S(Project("project_id")), S(Project("id")))
                    If If(Platform = "Modrinth", Not ValidModrinthId(Id) OrElse S(Project("project_type")) <> "mod", Not ValidCurseForgeId(Id) OrElse Not IsCurseForgeModProject(Project)) Then Continue For
                    Candidates.Add(New ModDependencyInspector.OnlineProjectInfo With {.Platform = Platform, .ProjectId = Id, .IsExact = False, .MatchBasis = "文件名搜索候选",
                        .ProjectName = Caption(If(Platform = "Modrinth", S(Project("title")), S(Project("name")))),
                        .Website = If(Platform = "Modrinth", "https://modrinth.com/mod/" & Uri.EscapeDataString(Id), If(IsCurseForgeWebsite(S(Project("links")?("websiteUrl"))), S(Project("links")("websiteUrl")), "https://www.curseforge.com/projects/" & Id))})
                Next
                File.Info.Candidates.AddRange(Candidates)
                Work.Cache.Write(Key, JArray.FromObject(Candidates), Work.Now(), Work.Lifetime(False))
            Next
        Next
    End Sub
    Private Shared Sub MapRelations(Report As ModDependencyInspector.InspectionReport)
        For Each File In Report.OnlineFiles
            File.Status = If(File.Matches.Count > 0, "已按内容精确匹配到下载源版本。", "未找到精确匹配；候选项目需要手动核对。")
            For Each Match In File.Matches.Where(Function(Item) Item.IsExact)
                For Each Requirement In Match.Dependencies
                    Requirement.MatchedPaths.Clear()
                    Requirement.EnabledPaths.Clear()
                    If Requirement.ProjectId <> "" Then
                        For Each Provider In Report.OnlineFiles.Where(Function(Item) Item.Matches.Any(Function(Project) Project.IsExact AndAlso Project.Platform = Requirement.Platform AndAlso
                            Project.ProjectId = Requirement.ProjectId AndAlso (Requirement.VersionId = "" OrElse Project.VersionId = Requirement.VersionId)))
                            Requirement.MatchedPaths.Add(Provider.FullPath)
                            If Provider.Enabled Then Requirement.EnabledPaths.Add(Provider.FullPath)
                        Next
                    End If
                    If Requirement.EnabledPaths.Count > 0 Then
                        Requirement.Status = "精确映射到启用文件：" & String.Join("、", Requirement.EnabledPaths.Select(Function(Value) Path.GetFileName(Value)))
                    ElseIf Requirement.MatchedPaths.Count > 0 Then
                        Requirement.Status = "仅映射到禁用文件，需核对：" & String.Join("、", Requirement.MatchedPaths.Select(Function(Value) Path.GetFileName(Value)))
                    Else
                        Requirement.Status = "未在本次精确匹配中找到，需核对；未据此判定缺失或版本错误"
                    End If
                Next
            Next
        Next
    End Sub

    Friend Shared Function SearchQuery(FullPath As String) As String
        Dim Value = Path.GetFileName(FullPath)
        If Value.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) OrElse Value.EndsWith(".old", StringComparison.OrdinalIgnoreCase) Then Value = Path.GetFileNameWithoutExtension(Value)
        Value = Path.GetFileNameWithoutExtension(Value)
        Value = Regex.Replace(Value, "(?i)(?:^|[-_+ \[\]])(?:fabric|forge|neoforge|quilt)(?=$|[-_+ \[\]])", " ")
        Value = Regex.Replace(Value, "(?i)[-_+ ]+(?:mc[-_ ]*)?v?\d+(?:\.\d+)+(?:[-_+][A-Za-z0-9.-]+)?$", "")
        Value = Regex.Replace(Value, "[^\p{L}0-9 ]", " ")
        Value = Regex.Replace(Value, "\s+", " ").Trim()
        If Value.Length > 64 Then Value = Value.Substring(0, 64).Trim()
        Return If(Regex.IsMatch(Value, "^\d+$"), "", Value)
    End Function
    Private Shared Function S(Value As JToken) As String
        Return If(Value Is Nothing OrElse Value.Type = JTokenType.Null, "", Value.ToString())
    End Function
    Private Shared Function Caption(Value As String) As String
        Value = Regex.Replace(If(Value, ""), "[\r\n\t]+", " ").Trim()
        Return If(Value.Length > 160, Value.Substring(0, 160) & "…", Value)
    End Function
    Private Shared Function A(Value As JToken) As IEnumerable(Of JToken)
        Return If(TypeOf Value Is JArray, CType(Value, JArray).AsEnumerable(), Enumerable.Empty(Of JToken)())
    End Function
    Private Shared Function ValidModrinthId(Value As String) As Boolean
        Return Regex.IsMatch(If(Value, ""), "^[A-Za-z0-9]{1,64}$")
    End Function
    Private Shared Function ValidCurseForgeId(Value As String) As Boolean
        Dim Number As Integer
        Return Integer.TryParse(Value, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture, Number) AndAlso Number > 0
    End Function
    Private Shared Function JsonIds(Ids As IEnumerable(Of String)) As String
        Return Uri.EscapeDataString(New JArray(Ids).ToString(Formatting.None))
    End Function
    Friend Shared Function IsAllowedApi(Url As String) As Boolean
        Dim Address As Uri = Nothing
        If Not Uri.TryCreate(Url, UriKind.Absolute, Address) OrElse Address.Scheme <> "https" OrElse Not Address.IsDefaultPort OrElse Address.UserInfo <> "" Then Return False
        Return (Address.Host.Equals("api.modrinth.com", StringComparison.OrdinalIgnoreCase) AndAlso Address.AbsolutePath.StartsWith("/v2/", StringComparison.Ordinal)) OrElse
               (Address.Host.Equals("api.curseforge.com", StringComparison.OrdinalIgnoreCase) AndAlso Address.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal))
    End Function
    Private Shared Function IsCurseForgeWebsite(Url As String) As Boolean
        Dim Address As Uri = Nothing
        Return Uri.TryCreate(Url, UriKind.Absolute, Address) AndAlso Address.Scheme = "https" AndAlso Address.IsDefaultPort AndAlso Address.UserInfo = "" AndAlso
            {"www.curseforge.com", "curseforge.com"}.Contains(Address.Host, StringComparer.OrdinalIgnoreCase) AndAlso Address.AbsolutePath.StartsWith("/minecraft/mc-mods/", StringComparison.OrdinalIgnoreCase)
    End Function
    Private Shared Function IsCurseForgeModProject(Project As JToken) As Boolean
        If Project("classId") IsNot Nothing AndAlso Project("classId").Type <> JTokenType.Null Then Return S(Project("classId")) = "6"
        Return IsCurseForgeWebsite(S(Project("links")?("websiteUrl")))
    End Function
    Private Shared Function DefaultRequest(Url As String, Method As HttpMethod, Body As String, Token As CancellationToken) As JToken
        If Not IsAllowedApi(Url) Then Throw New ArgumentException("Unsupported dependency API address.")
        'Keep credential attachment in the existing exact-host boundary. Never follow a redirect with signed headers.
        Using Handler As New HttpClientHandler With {.AllowAutoRedirect = False, .UseCookies = False, .AutomaticDecompression = DecompressionMethods.GZip Or DecompressionMethods.Deflate},
              Client As New HttpClient(Handler), Request As New HttpRequestMessage(Method, Url)
            Client.Timeout = Timeout.InfiniteTimeSpan
            If Body IsNot Nothing Then Request.Content = New StringContent(Body, Encoding.UTF8, "application/json")
            Dim SignedRequest = Request
            SecretHeadersSign(Url, SignedRequest)
            Using Response = Client.SendAsync(Request, HttpCompletionOption.ResponseHeadersRead, Token).GetAwaiter().GetResult()
                If Not Response.IsSuccessStatusCode Then
                    Dim RetrySeconds = 60
                    If Response.Headers.RetryAfter?.Delta.HasValue = True Then
                        RetrySeconds = CInt(Math.Min(900, Response.Headers.RetryAfter.Delta.Value.TotalSeconds))
                    ElseIf Response.Headers.RetryAfter?.Date.HasValue = True Then
                        RetrySeconds = CInt(Math.Min(900, (Response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow).TotalSeconds))
                    End If
                    Throw New HttpFailure(CInt(Response.StatusCode), RetrySeconds)
                End If
                Using Input = Response.Content.ReadAsStreamAsync().GetAwaiter().GetResult(), Buffer As New MemoryStream()
                    Dim Bytes(8191) As Byte, Count As Integer
                    Do
                        Count = Input.ReadAsync(Bytes, 0, Bytes.Length, Token).GetAwaiter().GetResult()
                        If Count = 0 Then Exit Do
                        If Buffer.Length + Count > 4L * 1024 * 1024 Then Throw New InvalidDataException("Dependency API response exceeds limit.")
                        Buffer.Write(Bytes, 0, Count)
                    Loop
                    Token.ThrowIfCancellationRequested()
                    Return JToken.Parse(New UTF8Encoding(False, True).GetString(Buffer.ToArray()))
                End Using
            End Using
        End Using
    End Function
End Class
