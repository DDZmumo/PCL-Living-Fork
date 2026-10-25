Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

'只读本地元数据。这里的“确定错误”不包含无法解析的版本、未知 Java、候选嵌套 Jar 或未读完整的声明。
'不使用联网资源缓存：ResourceVersion.Dependencies 是平台工程 ID，不能用作加载器的 Mod ID。
Friend NotInheritable Class ModDependencyInspector
    Private Sub New()
    End Sub

    Friend Enum MatchState
        Unknown
        Satisfied
        Unsatisfied
    End Enum

    Friend Enum DependencyKind
        Required
        [Optional]
        Recommended
        Suggested
        Incompatible
        Conflict
    End Enum

    Friend Enum IssueKind
        MissingRequired
        DuplicateId
        VersionMismatch
        Incompatible
        Recommendation
    End Enum

    Friend Class InspectionContext
        Public MinecraftVersion As String
        Public JavaVersion As String
        Public Side As String = "client"
        'False means that installed loaders are unknown, rather than that no loader is installed.
        Public LoaderKnown As Boolean
        Public LoaderVersions As New Dictionary(Of String, String)(StringComparer.Ordinal)
        Public AdditionalBuiltins As New Dictionary(Of String, String)(StringComparer.Ordinal)
        'Only legacy Forge loads the mods/<Minecraft version> directory.
        Public LegacyForgeVersionFolder As String
    End Class

    Friend Class ModInfo
        Public Id As String
        Public Group As String
        Public Name As String
        Public Version As String
        Public RootPath As String
        Public MetadataPath As String
        Public Format As String
        Public Side As String = "*"
        Public Enabled As Boolean
        Public IsNested As Boolean
        Public IsBuiltin As Boolean
        Public IsCandidate As Boolean
        Public Provides As New Dictionary(Of String, String)(StringComparer.Ordinal)
        Public Dependencies As New List(Of Dependency)
        Public ReadOnly Property Key As String
            Get
                Return If(RootPath, "") & "!" & If(MetadataPath, "") & ":" & Id
            End Get
        End Property
        Public ReadOnly Property Display As String
            Get
                Return If(String.IsNullOrWhiteSpace(Name), Id, Name) & " [" & Id & "]"
            End Get
        End Property
    End Class

    Friend Class Dependency
        Public Id As String
        Public Versions As JToken = New JValue("*")
        Public Syntax As String = "fabric"
        Public Kind As DependencyKind
        Public Side As String = "*"
        Public Reason As String
        Public Children As New List(Of Dependency)
        Public Join As String = "any"
        Public Unless As Dependency
        Public IsUnknown As Boolean
        'Fabric suggests/recommends are informational. Forge/Quilt optional dependencies validate versions if present.
        Public EnforceWhenPresent As Boolean
        Public ReadOnly Property Display As String
            Get
                If Children.Count > 0 Then Return "(" & String.Join(If(Join = "all", " 且 ", " 或 "), Children.Select(Function(Child) Child.Display)) & ")"
                Dim Range = If(Versions Is Nothing, "*", If(Versions.Type = JTokenType.String, Versions.ToString(), Versions.ToString(Formatting.None)))
                Return If(Id, "未知依赖") & If(Range = "*" OrElse Range = "", "", " " & Range)
            End Get
        End Property
    End Class

    Friend Class DependencyEdge
        Public Owner As ModInfo
        Public Requirement As Dependency
        Public Providers As New List(Of ModInfo)
        Public State As MatchState
        Public Evaluated As Boolean
    End Class

    Friend Class InspectionIssue
        Public Kind As IssueKind
        Public Owner As ModInfo
        Public Requirement As Dependency
        Public RelatedMods As New List(Of ModInfo)
        Public IsError As Boolean
        Public Message As String
    End Class

    Friend Class RemovalImpact
        Public [Mod] As ModInfo
        Public Requirements As New List(Of Dependency)
        Public IsDirect As Boolean
        Public IsDefinite As Boolean
        Public Reason As String
    End Class

    'Platform project IDs stay separate from loader Mod IDs, including after an exact content match.
    Friend Class OnlineProjectInfo
        Public Platform As String
        Public ProjectId As String
        Public VersionId As String
        Public ProjectName As String
        Public VersionName As String
        Public Website As String
        Public MatchBasis As String
        Public IsExact As Boolean
        Public Dependencies As New List(Of OnlineDependency)
    End Class
    Friend Class OnlineDependency
        Public Platform As String
        Public ProjectId As String
        Public VersionId As String
        Public ProjectName As String
        Public Website As String
        Public FileName As String
        Public Kind As String
        Public MatchedPaths As New List(Of String)
        Public EnabledPaths As New List(Of String)
        Public Status As String = "尚未映射到本地文件，需核对"
    End Class
    Friend Class OnlineFileInfo
        Public FullPath As String
        Public Sha1 As String
        Public Enabled As Boolean
        Public IsSupplementTarget As Boolean
        Public Matches As New List(Of OnlineProjectInfo)
        Public Candidates As New List(Of OnlineProjectInfo)
        Public Status As String
    End Class

    Friend Class InspectionReport
        Public Context As InspectionContext
        Public Mods As New List(Of ModInfo)
        Public Builtins As New List(Of ModInfo)
        Public Edges As New List(Of DependencyEdge)
        Public Issues As New List(Of InspectionIssue)
        Public Notices As New List(Of String)
        Public MetadataIncomplete As Boolean
        Public OnlineAttempted As Boolean
        Public OnlineCancelled As Boolean
        Public OnlineTimedOut As Boolean
        Public OnlineRequests As Integer
        Public OnlineCheckedAtUtc As DateTime
        Public OnlineFiles As New List(Of OnlineFileInfo)
        Public OnlineNotes As New List(Of String)
        Public ReverseDependencies As New Dictionary(Of String, List(Of DependencyEdge))(StringComparer.Ordinal)
        Public ReadOnly Property HasErrors As Boolean
            Get
                Return Issues.Any(Function(Issue) Issue.IsError)
            End Get
        End Property
        Public Function ToSummary(Optional MaxIssues As Integer = 30) As String
            Dim Lines As New List(Of String) From {
                "已检查 " & Mods.Where(Function(Item) Item.Enabled AndAlso Not Item.IsNested).Select(Function(Item) Item.RootPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() &
                " 个启用文件，识别 " & Mods.Count & " 个模组（包含禁用文件与内嵌模组）。",
                "确定问题：" & Issues.Where(Function(Issue) Issue.IsError).Count() & "；待确认或可选提示：" & Issues.Where(Function(Issue) Not Issue.IsError).Count() & "。"}
            For Each Issue In Issues.OrderByDescending(Function(Item) Item.IsError).Take(Math.Max(1, MaxIssues))
                Lines.Add(If(Issue.IsError, "• ", "· [提示] ") & Issue.Message)
            Next
            If Issues.Count > Math.Max(1, MaxIssues) Then Lines.Add("另有 " & (Issues.Count - Math.Max(1, MaxIssues)) & " 项，请在模组依赖详情中查看。")
            If MetadataIncomplete Then Lines.Add("部分文件的元数据未读完整；缺失依赖仅供确认，未据此判定启动错误。")
            If Notices.Count > 0 Then
                Lines.Add("")
                Lines.AddRange(Notices.Take(5))
                If Notices.Count > 5 Then Lines.Add("另有 " & (Notices.Count - 5) & " 条读取说明。")
            End If
            If OnlineAttempted Then
                Lines.Add("")
                Lines.Add("下载源补充：精确匹配 " & OnlineFiles.Where(Function(Item) Item.IsSupplementTarget AndAlso Item.Matches.Any()).Count() & " 个文件；使用 " & OnlineRequests & " 次请求。")
                Lines.AddRange(OnlineNotes.Take(4))
                Lines.Add("平台项目依赖仅作补充；候选和未映射项目未用于判定缺失或版本错误。")
            End If
            Lines.Add("")
            Lines.Add(If(OnlineAttempted, "以本地可靠声明为准；平台补充不能代替加载器对运行环境和内嵌版本的判断。",
                         "仅检查本地声明；运行时条件、加载器选择的内嵌版本与未声明依赖仍由游戏加载器判断。"))
            Return String.Join(vbCrLf, Lines)
        End Function
        Public Function DescribeFile(FullPath As String) As String
            Return ModDependencyInspector.DescribeFile(Me, FullPath)
        End Function
        Public Function DescribeOnlineFile(FullPath As String) As String
            If Not OnlineAttempted Then Return ""
            Dim Target = Path.GetFullPath(FullPath), Lines As New List(Of String)
            Dim Info = OnlineFiles.FirstOrDefault(Function(Item) String.Equals(Item.FullPath, Target, StringComparison.OrdinalIgnoreCase))
            If Info IsNot Nothing Then
                Lines.Add("下载源补充（本地可靠声明优先；" & OnlineCheckedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") & "）：")
                For Each Match In Info.Matches
                    Lines.Add("• " & Match.Platform & " · " & Match.ProjectName & " · " & Match.VersionName & "（" & Match.MatchBasis & "）")
                    If Not String.IsNullOrEmpty(Match.Website) Then Lines.Add("  " & Match.Website)
                    If Match.Dependencies.Count = 0 Then Lines.Add("  该平台版本没有声明项目依赖。")
                    For Each Requirement In Match.Dependencies.Take(40)
                        Dim Caption = If(Requirement.ProjectName, If(Requirement.ProjectId, If(Requirement.FileName, "未提供项目 ID")))
                        Lines.Add("  • [" & OnlineKind(Requirement.Kind) & "] " & Caption & "：" & Requirement.Status)
                        If Not String.IsNullOrEmpty(Requirement.Website) Then Lines.Add("    " & Requirement.Website)
                    Next
                Next
                If Info.Matches.Count = 0 Then Lines.Add(If(Info.Status, "没有找到精确文件匹配。"))
                If Info.Candidates.Count > 0 Then
                    Lines.Add("按文件名搜索的候选（未选定、不能据此确认依赖）：")
                    For Each Candidate In Info.Candidates.Take(6)
                        Lines.Add("  • " & Candidate.Platform & " · " & Candidate.ProjectName & vbCrLf & "    " & Candidate.Website)
                    Next
                End If
            End If
            Dim Reverse As New HashSet(Of String)(StringComparer.Ordinal)
            For Each Owner In OnlineFiles.Where(Function(Item) Not String.Equals(Item.FullPath, Target, StringComparison.OrdinalIgnoreCase))
                For Each Match In Owner.Matches
                    For Each Requirement In Match.Dependencies.Where(Function(Item) Item.MatchedPaths.Contains(Target, StringComparer.OrdinalIgnoreCase))
                        Reverse.Add("  • " & Path.GetFileName(Owner.FullPath) & " · " & Match.Platform & " [" & OnlineKind(Requirement.Kind) & "]")
                    Next
                Next
            Next
            If Reverse.Count > 0 Then
                Lines.Add("按平台精确匹配，被这些文件依赖（仍需核对运行环境）：")
                Lines.AddRange(Reverse.Take(40))
            End If
            If OnlineCancelled Then Lines.Add("联网补充已取消，本地检查结果已保留。")
            If OnlineTimedOut Then Lines.Add("联网补充达到时间预算，本地检查结果已保留。")
            Return String.Join(vbCrLf, Lines)
        End Function
        Public Function DescribeOnlineImpacts(RemovedPaths As IEnumerable(Of String)) As String
            If Not OnlineAttempted Then Return ""
            Dim Removed As New HashSet(Of String)(RemovedPaths.Select(Function(Value) Path.GetFullPath(Value)), StringComparer.OrdinalIgnoreCase)
            Dim Lines As New HashSet(Of String)(StringComparer.Ordinal)
            For Each Owner In OnlineFiles.Where(Function(Item) Item.Enabled AndAlso Item.IsSupplementTarget AndAlso Not Removed.Contains(Item.FullPath))
                For Each Match In Owner.Matches.Where(Function(Item) Item.IsExact)
                    For Each Requirement In Match.Dependencies.Where(Function(Item) Item.Kind = "required" AndAlso Item.EnabledPaths.Count > 0)
                        If Not Requirement.EnabledPaths.All(Function(Value) Removed.Contains(Value)) Then Continue For
                        Lines.Add("• " & Path.GetFileName(Owner.FullPath) & "：按 " & Match.Platform & " 版本声明，可能失去项目依赖 " &
                                  If(Requirement.ProjectName, Requirement.ProjectId) & "（平台补充，需核对）。")
                    Next
                Next
            Next
            If Lines.Count = 0 Then Return ""
            Return "下载源声明的补充影响：" & vbCrLf & String.Join(vbCrLf, Lines.Take(20))
        End Function
        Private Shared Function OnlineKind(Kind As String) As String
            Select Case Kind
                Case "required" : Return "必需"
                Case "optional" : Return "可选"
                Case "embedded" : Return "内嵌"
                Case "incompatible" : Return "不兼容"
                Case Else : Return "其他声明"
            End Select
        End Function
    End Class

    Private Const MaxMetadataBytes As Integer = 1024 * 1024
    Private Const MaxNestedBytes As Long = 64L * 1024 * 1024
    Private Const MaxNestedDepth As Integer = 4
    Private Const MaxNestedCount As Integer = 256

    'Does not write settings, resolve online projects, or read account files.
    Friend Shared Function ContextForInstance(Instance As McInstance, Optional JavaVersion As String = Nothing) As InspectionContext
        If Instance Is Nothing Then Throw New ArgumentNullException(NameOf(Instance))
        Dim Result As New InspectionContext With {.MinecraftVersion = If(Instance.Version.Reliable, Instance.Version.VanillaName, Nothing), .JavaVersion = JavaVersion, .LoaderKnown = True}
        For Each Pair In Instance.Version.ModLoaderVersions
            Result.LoaderVersions(CanonicalLoaderId(Pair.Key.ToString())) = Pair.Value
        Next
        'McVersion represents Quilt under the Fabric flag; its Fabric compatibility version is NOT the Quilt version.
        Dim Libraries = TryCast(Instance.JsonObject?("libraries"), JArray)
        If Libraries IsNot Nothing Then
            For Each Library As JObject In Libraries.OfType(Of JObject)()
                Dim Coordinate = Text(Library("name"))
                If Not Coordinate.StartsWith("org.quiltmc:quilt-loader:", StringComparison.Ordinal) Then Continue For
                Result.LoaderVersions.Remove("fabricloader")
                Result.LoaderVersions("quilt_loader") = Coordinate.Substring("org.quiltmc:quilt-loader:".Length).Split(":"c)(0)
                Result.LoaderVersions("fabricloader") = Nothing
            Next
        End If
        Dim ForgeVersion As String = Nothing
        If Result.LoaderVersions.TryGetValue("forge", ForgeVersion) AndAlso Regex.IsMatch(If(Result.MinecraftVersion, ""), "^1\.(?:[0-9]|1[0-2])(?:\.|$)") Then
            Result.LegacyForgeVersionFolder = Result.MinecraftVersion
        End If
        If Result.LoaderVersions.ContainsKey("neoforge") AndAlso Result.MinecraftVersion = "1.20.1" Then
            Result.LoaderVersions("forge") = Result.LoaderVersions("neoforge")
        End If
        If Instance.Version.HasOptiFine AndAlso Not Instance.Version.OptiFineAsMod Then Result.AdditionalBuiltins("optifine") = Instance.Version.OptiFine
        Return Result
    End Function

    Friend Shared Function Inspect(ModsFolder As String, Optional Context As InspectionContext = Nothing,
                                   Optional Token As CancellationToken = Nothing) As InspectionReport
        Dim Result As New InspectionReport With {.Context = If(Context, New InspectionContext())}
        Result.Builtins = CreateBuiltins(Result.Context)
        If Not Directory.Exists(ModsFolder) Then Return Result
        Dim Roots As New List(Of String) From {Path.GetFullPath(ModsFolder)}
        If Not String.IsNullOrEmpty(Result.Context.LegacyForgeVersionFolder) AndAlso
            Regex.IsMatch(Result.Context.LegacyForgeVersionFolder, "^[0-9.]+$") Then
            Dim Legacy = Path.Combine(ModsFolder, Result.Context.LegacyForgeVersionFolder)
            If Directory.Exists(Legacy) Then Roots.Add(Path.GetFullPath(Legacy))
        End If
        For Each Folder In Roots
            Token.ThrowIfCancellationRequested()
            Dim Files As String()
            Try
                If (File.GetAttributes(Folder) And FileAttributes.ReparsePoint) <> 0 Then
                    Incomplete(Result, "跳过链接目录：" & Path.GetFileName(Folder))
                    Continue For
                End If
                Files = Directory.EnumerateFiles(Folder, "*", SearchOption.TopDirectoryOnly).Where(Function(Item) IsModFile(Item)).OrderBy(Function(Item) Item, StringComparer.OrdinalIgnoreCase).ToArray()
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Incomplete(Result, "无法枚举模组目录：" & ex.Message)
                Continue For
            End Try
            For Each FullPath In Files
                Token.ThrowIfCancellationRequested()
                Dim Enabled = Not FullPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) AndAlso Not FullPath.EndsWith(".old", StringComparison.OrdinalIgnoreCase)
                Try
                    If (File.GetAttributes(FullPath) And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("跳过链接文件")
                    Using Stream As New FileStream(FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete),
                          Archive As New ZipArchive(Stream, ZipArchiveMode.Read, False)
                        Dim Budget As New ReadBudget
                        ReadArchive(Archive, Path.GetFullPath(FullPath), "", Enabled, False, 0, Result, Budget, Token)
                    End Using
                Catch ex As OperationCanceledException
                    Throw
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse
                    TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is FormatException OrElse TypeOf ex Is DecoderFallbackException
                    If Enabled Then
                        Incomplete(Result, Path.GetFileName(FullPath) & "：无法完整读取元数据（" & ex.Message & "）。")
                    Else
                        Result.Notices.Add(Path.GetFileName(FullPath) & "：已禁用，无法读取元数据。")
                    End If
                End Try
            Next
        Next
        BuildReport(Result, Token)
        Return Result
    End Function

    Private Class ReadBudget
        Public Bytes As Long
        Public Count As Integer
    End Class

    Private Shared Function IsModFile(FullPath As String) As Boolean
        Dim Name = FullPath
        If Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) OrElse Name.EndsWith(".old", StringComparison.OrdinalIgnoreCase) Then Name = Path.GetFileNameWithoutExtension(Name)
        Return {".jar", ".zip", ".litemod"}.Contains(Path.GetExtension(Name).ToLowerInvariant())
    End Function

    Private Shared Sub Incomplete(Report As InspectionReport, Message As String)
        Report.MetadataIncomplete = True
        If Not Report.Notices.Contains(Message) Then Report.Notices.Add(Message)
    End Sub

    Private Shared Function ReadText(Entry As ZipArchiveEntry) As String
        If Entry.Length > MaxMetadataBytes Then Throw New InvalidDataException("元数据超过 1 MiB")
        Using Stream = Entry.Open(), Reader As New StreamReader(Stream, New UTF8Encoding(False, True), True)
            Dim Buffer(4095) As Char, Content As New StringBuilder(), Count As Integer
            Do
                Count = Reader.Read(Buffer, 0, Buffer.Length)
                Content.Append(Buffer, 0, Count)
                If Content.Length > MaxMetadataBytes Then Throw New InvalidDataException("元数据超过 1 MiB")
            Loop While Count > 0
            Return Content.ToString()
        End Using
    End Function

    Private Shared Sub ReadArchive(Archive As ZipArchive, RootPath As String, NestedPath As String, Enabled As Boolean,
                                  Nested As Boolean, Depth As Integer, Report As InspectionReport, Budget As ReadBudget, Token As CancellationToken)
        Token.ThrowIfCancellationRequested()
        Dim Choices As New List(Of String)
        Dim Loaders = Report.Context.LoaderVersions
        If Loaders.ContainsKey("quilt_loader") Then Choices.Add("quilt.mod.json")
        If Loaders.ContainsKey("fabricloader") OrElse Loaders.ContainsKey("quilt_loader") Then Choices.Add("fabric.mod.json")
        If Loaders.ContainsKey("neoforge") Then Choices.Add("META-INF/neoforge.mods.toml")
        If Loaders.ContainsKey("forge") OrElse Loaders.ContainsKey("neoforge") Then Choices.Add("META-INF/mods.toml")
        Choices.AddRange({"quilt.mod.json", "fabric.mod.json", "META-INF/neoforge.mods.toml", "META-INF/mods.toml", "mcmod.info", "litemod.json"})
        Dim Metadata = Choices.Distinct(StringComparer.Ordinal).Select(Function(Name) Archive.GetEntry(Name)).FirstOrDefault(Function(Entry) Entry IsNot Nothing)
        If Metadata Is Nothing Then
            If Enabled Then Incomplete(Report, Path.GetFileName(RootPath) & If(Nested, "!" & NestedPath, "") & "：没有可读取的本地模组声明。")
            Return
        End If
        Dim Items As New List(Of ModInfo), NestedJars As New List(Of String), Content = ReadText(Metadata)
        Select Case Metadata.FullName
            Case "fabric.mod.json"
                Dim Json = JObject.Parse(Content)
                If Text(Json("schemaVersion")) <> "1" AndAlso Enabled Then Incomplete(Report, Path.GetFileName(RootPath) & "：Fabric 元数据并非受支持的 schemaVersion 1。")
                For Each Name In {"depends", "recommends", "suggests", "breaks", "conflicts"}
                    If Json(Name) IsNot Nothing AndAlso Not TypeOf Json(Name) Is JObject AndAlso Enabled Then Incomplete(Report, Path.GetFileName(RootPath) & "：无法完整读取 " & Name & " 声明。")
                Next
                Dim Item = NewMod(Json("id"), Json("name"), Json("version"), RootPath, NestedPath & Metadata.FullName, "Fabric", Enabled, Nested)
                Item.Side = ReadSide(Json("environment"))
                For Each AliasId In ArrayValues(Json("provides"))
                    If AliasId.Type = JTokenType.String Then Item.Provides(Text(AliasId)) = Item.Version
                Next
                ReadFabricDependencies(Item, Json)
                Items.Add(Item)
                For Each Jar In ArrayValues(Json("jars"))
                    Dim EntryName = If(Jar.Type = JTokenType.Object, Text(Jar("file")), "")
                    If EntryName <> "" Then NestedJars.Add(EntryName)
                Next
            Case "quilt.mod.json"
                Dim Json = JObject.Parse(Content), Loader = TryCast(Json("quilt_loader"), JObject)
                If Loader Is Nothing Then Throw New FormatException("缺少 quilt_loader")
                If Text(Json("schema_version")) <> "1" AndAlso Enabled Then Incomplete(Report, Path.GetFileName(RootPath) & "：Quilt 元数据并非受支持的 schema_version 1。")
                For Each Name In {"depends", "breaks", "provides", "jars"}
                    If Loader(Name) IsNot Nothing AndAlso Not TypeOf Loader(Name) Is JArray AndAlso Enabled Then Incomplete(Report, Path.GetFileName(RootPath) & "：无法完整读取 " & Name & " 声明。")
                Next
                Dim Item = NewMod(Loader("id"), Loader("metadata")?("name"), Loader("version"), RootPath, NestedPath & Metadata.FullName, "Quilt", Enabled, Nested)
                Item.Group = Text(Loader("group"))
                Item.Side = ReadSide(Json("minecraft")?("environment"))
                For Each AliasValue In ArrayValues(Loader("provides"))
                    Dim AliasId = If(AliasValue.Type = JTokenType.String, Text(AliasValue), Text(AliasValue("id")))
                    If AliasId = "" Then Continue For
                    Dim AliasVersion = If(AliasValue.Type = JTokenType.Object AndAlso AliasValue("version") IsNot Nothing, Text(AliasValue("version")), Item.Version)
                    Item.Provides(AliasId) = AliasVersion
                    If Not AliasId.Contains(":") AndAlso Item.Group <> "" Then Item.Provides(Item.Group & ":" & AliasId) = AliasVersion
                    If AliasId.Contains(":") Then Item.Provides(AliasId.Substring(AliasId.LastIndexOf(":"c) + 1)) = AliasVersion
                Next
                For Each Value In ArrayValues(Loader("depends"))
                    Item.Dependencies.Add(ReadQuiltDependency(Value, DependencyKind.Required, True))
                Next
                For Each Value In ArrayValues(Loader("breaks"))
                    Item.Dependencies.Add(ReadQuiltDependency(Value, DependencyKind.Incompatible, False))
                Next
                Items.Add(Item)
                For Each Jar In ArrayValues(Loader("jars"))
                    If Jar.Type = JTokenType.String Then NestedJars.Add(Text(Jar))
                Next
            Case "META-INF/mods.toml", "META-INF/neoforge.mods.toml"
                Dim IsNeo = Metadata.FullName.Contains("neoforge") OrElse Loaders.ContainsKey("neoforge")
                ReadForge(Content, Archive, RootPath, NestedPath & Metadata.FullName, Enabled, Nested, IsNeo, Items, Report)
                Dim JarJar = Archive.GetEntry("META-INF/jarjar/metadata.json")
                If JarJar IsNot Nothing Then
                    For Each Jar In ArrayValues(JObject.Parse(ReadText(JarJar))("jars"))
                        Dim EntryName = Text(Jar("path"))
                        If EntryName <> "" Then NestedJars.Add(EntryName)
                    Next
                End If
            Case "mcmod.info"
                ReadLegacy(JToken.Parse(Content), RootPath, NestedPath & Metadata.FullName, Enabled, Nested, Items, Report)
            Case "litemod.json"
                Dim Json = JObject.Parse(Content)
                Items.Add(NewMod(Json("name"), Json("displayName"), Json("version"), RootPath, NestedPath & Metadata.FullName, "LiteLoader", Enabled, Nested))
                If Enabled Then Incomplete(Report, Path.GetFileName(RootPath) & "：LiteLoader 依赖可能在运行时代码中声明。")
        End Select
        If Items.Count = 0 Then Throw New FormatException("声明中未找到模组 ID")
        Report.Mods.AddRange(Items)
        For Each EntryName In NestedJars.Distinct(StringComparer.Ordinal)
            Token.ThrowIfCancellationRequested()
            Dim Parts = EntryName.Replace("\"c, "/"c).Split("/"c)
            If EntryName.StartsWith("/", StringComparison.Ordinal) OrElse Parts.Any(Function(Part) Part = ".." OrElse Part = "") Then
                If Enabled Then Incomplete(Report, Path.GetFileName(RootPath) & "：跳过不安全的内嵌路径。")
                Continue For
            End If
            Dim Jar = Archive.GetEntry(EntryName)
            If Jar Is Nothing Then
                If Enabled Then Incomplete(Report, Path.GetFileName(RootPath) & "：声明的内嵌文件不存在：" & EntryName)
                Continue For
            End If
            If Depth >= MaxNestedDepth OrElse Budget.Count >= MaxNestedCount OrElse Jar.Length > MaxNestedBytes - Budget.Bytes Then
                If Enabled Then Incomplete(Report, Path.GetFileName(RootPath) & "：内嵌模组达到读取上限。")
                Continue For
            End If
            Budget.Bytes += Jar.Length
            Budget.Count += 1
            Try
                Using Buffer As New MemoryStream()
                    Using Input = Jar.Open()
                        Dim Bytes(8191) As Byte, Count As Integer, Remaining = Jar.Length
                        Do
                            Count = Input.Read(Bytes, 0, CInt(Math.Min(Bytes.Length, Remaining + 1)))
                            If Count > Remaining Then Throw New InvalidDataException("内嵌文件长度与声明不符")
                            Buffer.Write(Bytes, 0, Count)
                            Remaining -= Count
                        Loop While Count > 0
                    End Using
                    Buffer.Position = 0
                    Using Inner As New ZipArchive(Buffer, ZipArchiveMode.Read, True)
                        ReadArchive(Inner, RootPath, NestedPath & EntryName & "!", Enabled, True, Depth + 1, Report, Budget, Token)
                    End Using
                End Using
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is FormatException OrElse TypeOf ex Is DecoderFallbackException
                If Enabled Then Incomplete(Report, Path.GetFileName(RootPath) & "!" & EntryName & "：无法完整读取内嵌模组。")
            End Try
        Next
    End Sub

    Private Shared Function NewMod(Id As JToken, Name As JToken, Version As JToken, RootPath As String, MetadataPath As String,
                                  Format As String, Enabled As Boolean, Nested As Boolean) As ModInfo
        Dim ModId = Text(Id)
        If Not Regex.IsMatch(ModId, "^[A-Za-z0-9_.-]+$") Then Throw New FormatException("模组 ID 无效")
        Return New ModInfo With {.Id = ModId, .Name = If(Text(Name) = "", ModId, Text(Name)), .Version = Text(Version),
            .RootPath = RootPath, .MetadataPath = MetadataPath, .Format = Format, .Enabled = Enabled, .IsNested = Nested}
    End Function

    Private Shared Function Text(Value As JToken) As String
        Return If(Value Is Nothing OrElse Value.Type = JTokenType.Null, "", Value.ToString())
    End Function
    Private Shared Function ArrayValues(Value As JToken) As IEnumerable(Of JToken)
        Return If(TypeOf Value Is JArray, CType(Value, JArray).AsEnumerable(), Enumerable.Empty(Of JToken)())
    End Function
    Private Shared Function ReadSide(Value As JToken) As String
        If TypeOf Value Is JArray Then
            Dim Values = CType(Value, JArray).Select(Function(Item) Text(Item)).ToList()
            If Values.Contains("*") OrElse (Values.Contains("client") AndAlso Values.Contains("server")) Then Return "*"
            Return String.Join(",", Values)
        End If
        Return If(Text(Value) = "", "*", Text(Value)).ToLowerInvariant().Replace("dedicated_server", "server")
    End Function
    Private Shared Function SideApplies(Side As String, Context As InspectionContext) As Boolean
        Return Side = "*" OrElse Side = "" OrElse Side = "both" OrElse Side.Split(","c).Contains(If(Context.Side, "client").ToLowerInvariant())
    End Function
    Private Shared Sub ReadFabricDependencies(Item As ModInfo, Json As JObject)
        Dim Kinds = New Dictionary(Of String, DependencyKind) From {{"depends", DependencyKind.Required}, {"recommends", DependencyKind.Recommended},
            {"suggests", DependencyKind.Suggested}, {"breaks", DependencyKind.Incompatible}, {"conflicts", DependencyKind.Conflict}}
        For Each Pair In Kinds
            Dim Values = TryCast(Json(Pair.Key), JObject)
            If Values Is Nothing Then Continue For
            For Each Entry In Values.Properties()
                Item.Dependencies.Add(New Dependency With {.Id = CanonicalId(Entry.Name), .Versions = Entry.Value.DeepClone(), .Kind = Pair.Value})
            Next
        Next
    End Sub
    Private Shared Function ReadQuiltDependency(Value As JToken, Kind As DependencyKind, Any As Boolean) As Dependency
        Dim Result As New Dependency With {.Kind = Kind, .Join = If(Any, "any", "all")}
        If TypeOf Value Is JArray Then
            For Each Child In CType(Value, JArray)
                Result.Children.Add(ReadQuiltDependency(Child, Kind, Any))
            Next
            Result.IsUnknown = Result.Children.Count = 0
        ElseIf Value.Type = JTokenType.String Then
            Result.Id = CanonicalId(Text(Value))
        ElseIf TypeOf Value Is JObject Then
            Result.Id = CanonicalId(Text(Value("id")))
            Result.Versions = If(Value("versions")?.DeepClone(), New JValue("*"))
            Result.Reason = Text(Value("reason"))
            If Text(Value("optional")).Equals("true", StringComparison.OrdinalIgnoreCase) Then
                Result.Kind = DependencyKind.Optional
                Result.EnforceWhenPresent = True
            End If
            If Value("unless") IsNot Nothing Then Result.Unless = ReadQuiltDependency(Value("unless"), DependencyKind.Required, True)
        Else
            Result.IsUnknown = True
        End If
        If Result.Children.Count = 0 AndAlso String.IsNullOrEmpty(Result.Id) Then Result.IsUnknown = True
        Return Result
    End Function

    Private Class TomlSection
        Public Name As String
        Public Values As New Dictionary(Of String, String)(StringComparer.Ordinal)
    End Class

    'A bounded TOML subset reader, with quoted/comment/multiline state. Descriptions must never become dependency declarations.
    'Only complete scalar assignments in recognised sections are used; unsupported values remain unknown.
    Private Shared Function ReadToml(Content As String) As List(Of TomlSection)
        Dim Sections As New List(Of TomlSection), Current As New TomlSection With {.Name = ""}, Statement As New StringBuilder()
        Sections.Add(Current)
        Dim Quote As Char = ChrW(0), Triple As Boolean, Escape As Boolean, Square As Integer, Curly As Integer, Comment As Boolean, Index As Integer
        While Index <= Content.Length
            Dim Ch = If(Index = Content.Length, ChrW(10), Content(Index))
            If Comment Then
                If Ch <> ChrW(10) Then
                    Index += 1
                    Continue While
                End If
                Comment = False
            End If
            If Quote <> ChrW(0) Then
                Statement.Append(Ch)
                If Escape Then
                    Escape = False
                ElseIf Ch = "\"c AndAlso Quote = """"c Then
                    Escape = True
                ElseIf Ch = Quote Then
                    If Triple Then
                        If Index + 2 < Content.Length AndAlso Content(Index + 1) = Quote AndAlso Content(Index + 2) = Quote Then
                            Statement.Append(Quote, 2)
                            Index += 2
                            Quote = ChrW(0)
                            Triple = False
                        End If
                    Else
                        Quote = ChrW(0)
                    End If
                End If
            ElseIf Ch = "#"c Then
                Comment = True
            ElseIf Ch = """"c OrElse Ch = "'"c Then
                Quote = Ch
                Triple = Index + 2 < Content.Length AndAlso Content(Index + 1) = Ch AndAlso Content(Index + 2) = Ch
                Statement.Append(Ch)
                If Triple Then
                    Statement.Append(Ch, 2)
                    Index += 2
                End If
            Else
                If Ch = "["c Then Square += 1
                If Ch = "]"c Then Square -= 1
                If Ch = "{"c Then Curly += 1
                If Ch = "}"c Then Curly -= 1
                If Ch = ChrW(10) AndAlso Square <= 0 AndAlso Curly <= 0 Then
                    Dim Line = Statement.ToString().Trim()
                    Statement.Clear()
                    If Line.StartsWith("[", StringComparison.Ordinal) Then
                        Dim Header = Regex.Match(Line, "^\[{1,2}\s*([A-Za-z0-9_.-]+)\s*\]{1,2}$")
                        Current = New TomlSection With {.Name = If(Header.Success, Header.Groups(1).Value, "?")}
                        Sections.Add(Current)
                    ElseIf Line <> "" Then
                        Dim Assignment = Regex.Match(Line, "^([A-Za-z0-9_.-]+)\s*=\s*(.+)$", RegexOptions.Singleline)
                        If Assignment.Success Then Current.Values(Assignment.Groups(1).Value) = Assignment.Groups(2).Value.Trim()
                    End If
                    Square = 0
                    Curly = 0
                Else
                    Statement.Append(Ch)
                End If
            End If
            Index += 1
        End While
        If Quote <> ChrW(0) OrElse Square <> 0 OrElse Curly <> 0 Then Throw New FormatException("TOML 字符串或容器未结束")
        Return Sections
    End Function
    Private Shared Function Scalar(Section As TomlSection, Key As String, Optional DefaultValue As String = "") As String
        Dim Raw As String = Nothing
        If Not Section.Values.TryGetValue(Key, Raw) Then Return DefaultValue
        If Raw.StartsWith("""" & """" & """", StringComparison.Ordinal) OrElse Raw.StartsWith("'''", StringComparison.Ordinal) Then Return DefaultValue
        If Raw.Length >= 2 AndAlso Raw(0) = "'"c AndAlso Raw.Last() = "'"c Then Return Raw.Substring(1, Raw.Length - 2)
        If Raw.Length >= 2 AndAlso Raw(0) = """"c AndAlso Raw.Last() = """"c Then
            Try
                Return JsonConvert.DeserializeObject(Of String)(Raw)
            Catch ex As JsonException
                Return DefaultValue
            End Try
        End If
        If Raw = "true" OrElse Raw = "false" Then Return Raw
        Return DefaultValue
    End Function
    Private Shared Sub ReadForge(Content As String, Archive As ZipArchive, RootPath As String, MetadataPath As String, Enabled As Boolean,
                                 Nested As Boolean, Neo As Boolean, Items As List(Of ModInfo), Report As InspectionReport)
        Dim Sections = ReadToml(Content), GlobalSection = Sections(0)
        If Enabled AndAlso Sections.Any(Function(Section) Section.Name = "?") Then Incomplete(Report, Path.GetFileName(RootPath) & "：存在未识别的 TOML 表名，部分声明未解析。")
        Dim ManifestVersion As String = ""
        Dim Manifest = Archive.GetEntry("META-INF/MANIFEST.MF")
        If Manifest IsNot Nothing Then
            Dim ManifestText = Regex.Replace(ReadText(Manifest), "\r?\n ", "")
            Dim Match = Regex.Match(ManifestText, "(?m)^Implementation-Version:\s*([^\r\n]+)")
            If Match.Success Then ManifestVersion = Match.Groups(1).Value.Trim()
        End If
        For Each Section In Sections.Where(Function(Entry) Entry.Name = "mods")
            Dim Version = Scalar(Section, "version")
            If Version = "$" & "{file.jarVersion}" Then Version = ManifestVersion
            Dim Item = NewMod(New JValue(Scalar(Section, "modId")), New JValue(Scalar(Section, "displayName")), New JValue(Version), RootPath, MetadataPath, If(Neo, "NeoForge", "Forge"), Enabled, Nested)
            If Scalar(GlobalSection, "clientSideOnly") = "true" Then Item.Side = "client"
            Items.Add(Item)
        Next
        For Each Section In Sections.Where(Function(Entry) Entry.Name.StartsWith("dependencies.", StringComparison.Ordinal))
            Dim OwnerId = Section.Name.Substring("dependencies.".Length), Item = Items.FirstOrDefault(Function(Entry) Entry.Id = OwnerId)
            If Item Is Nothing Then
                If Enabled Then Incomplete(Report, Path.GetFileName(RootPath) & "：依赖声明所属 ID 不存在：" & OwnerId)
                Continue For
            End If
            Dim Mandatory = Scalar(Section, "mandatory")
            Dim KindText = Scalar(Section, "type", If(Neo AndAlso Mandatory = "", "required", ""))
            Dim Kind As DependencyKind, Unknown = False
            Select Case KindText
                Case "required" : Kind = DependencyKind.Required
                Case "optional" : Kind = DependencyKind.Optional
                Case "incompatible" : Kind = DependencyKind.Incompatible
                Case "discouraged" : Kind = DependencyKind.Conflict
                Case Else
                    If Mandatory = "true" Then
                        Kind = DependencyKind.Required
                    ElseIf Mandatory = "false" Then
                        Kind = DependencyKind.Optional
                    Else
                        Kind = DependencyKind.Required
                        Unknown = True
                    End If
            End Select
            Dim Id = Scalar(Section, "modId")
            Item.Dependencies.Add(New Dependency With {.Id = CanonicalId(Id), .Versions = New JValue(Scalar(Section, "versionRange", "*")),
                .Syntax = "maven", .Kind = Kind, .Side = ReadSide(New JValue(Scalar(Section, "side", "*"))), .Reason = Scalar(Section, "reason"),
                .EnforceWhenPresent = Kind = DependencyKind.Optional, .IsUnknown = Unknown OrElse Id = ""})
        Next
        For Each Item In Items
            Dim Features = Sections.FirstOrDefault(Function(Entry) Entry.Name = "features." & Item.Id)
            If Features IsNot Nothing AndAlso Features.Values.ContainsKey("java_version") Then
                Item.Dependencies.Add(New Dependency With {.Id = "java", .Versions = New JValue(Scalar(Features, "java_version")), .Syntax = "maven", .Kind = DependencyKind.Required})
            End If
            'Forge FML uses the Forge major version; NeoForge's language loader version is a separate numbering scheme.
            Dim Language = Scalar(GlobalSection, "modLoader"), Range = Scalar(GlobalSection, "loaderVersion")
            If Not Neo AndAlso {"javafml", "lowcodefml"}.Contains(Language) AndAlso Range <> "" Then
                Item.Dependencies.Add(New Dependency With {.Id = Language, .Versions = New JValue(Range), .Syntax = "maven", .Kind = DependencyKind.Required, .Reason = "FML 语言加载器版本"})
            End If
        Next
    End Sub
    Private Shared Sub ReadLegacy(Json As JToken, RootPath As String, MetadataPath As String, Enabled As Boolean, Nested As Boolean,
                                  Items As List(Of ModInfo), Report As InspectionReport)
        Dim Values = If(TypeOf Json Is JArray, Json, Json("modList"))
        For Each Value As JObject In ArrayValues(Values).OfType(Of JObject)()
            Dim Item = NewMod(Value("modid"), Value("name"), Value("version"), RootPath, MetadataPath, "Forge (旧版)", Enabled, Nested)
            For Each Required In ArrayValues(Value("requiredMods"))
                Dim Raw = Text(Required), At = Raw.IndexOf("@"c)
                Item.Dependencies.Add(New Dependency With {.Id = If(At < 0, Raw, Raw.Substring(0, At)), .Versions = New JValue(If(At < 0, "*", Raw.Substring(At + 1))),
                    .Kind = DependencyKind.Required, .Syntax = "maven"})
            Next
            Items.Add(Item)
        Next
        'Old Forge may add/override dependencies through @Mod, which mcmod.info cannot prove.
        If Enabled Then Incomplete(Report, Path.GetFileName(RootPath) & "：旧版 Forge 还可能通过 @Mod 注解声明依赖。")
    End Sub

    Private Shared Function CanonicalId(Id As String) As String
        'Mod IDs are exact identifiers. In particular, "fabric" is the old Fabric API ID, not Fabric Loader.
        Return If(Id, "")
    End Function
    Private Shared Function CanonicalLoaderId(Id As String) As String
        Select Case If(Id, "").ToLowerInvariant()
            Case "fabric", "fabric-loader", "fabric_loader", "fabricloader" : Return "fabricloader"
            Case "quilt", "quilt-loader", "quiltloader", "quilt_loader" : Return "quilt_loader"
            Case "liteloader" : Return "liteloader"
            Case "neoforge", "forge", "minecraft", "java" : Return Id.ToLowerInvariant()
            Case Else : Return If(Id, "")
        End Select
    End Function
    Private Shared Function CreateBuiltins(Context As InspectionContext) As List(Of ModInfo)
        Dim Values As New Dictionary(Of String, String)(StringComparer.Ordinal) From {{"minecraft", Context.MinecraftVersion}, {"java", NormalizeJava(Context.JavaVersion)}}
        If Not Context.LoaderKnown AndAlso Context.LoaderVersions.Count = 0 Then
            For Each Id In {"forge", "neoforge", "fabricloader", "quilt_loader", "liteloader"}
                Values(Id) = Nothing
            Next
        End If
        For Each Pair In Context.LoaderVersions
            Values(CanonicalLoaderId(Pair.Key)) = Pair.Value
        Next
        If Values.ContainsKey("forge") OrElse Values.ContainsKey("neoforge") Then
            Values("fml") = Nothing
            Values("javafml") = Nothing
            Values("lowcodefml") = Nothing
            Dim Forge As String = Nothing
            If Values.TryGetValue("forge", Forge) AndAlso Regex.IsMatch(If(Forge, ""), "^\d+(?:\.\d+)*$") Then
                Values("javafml") = Forge.Split("."c)(0)
                Values("lowcodefml") = Forge.Split("."c)(0)
            End If
        End If
        For Each Pair In Context.AdditionalBuiltins
            Values(CanonicalId(Pair.Key)) = Pair.Value
        Next
        Return Values.Select(Function(Pair) New ModInfo With {.Id = Pair.Key, .Name = Pair.Key, .Version = Pair.Value, .IsBuiltin = True, .Enabled = True}).ToList()
    End Function
    Private Shared Function NormalizeJava(Value As String) As String
        If String.IsNullOrEmpty(Value) Then Return Value
        If Value.StartsWith("1.", StringComparison.Ordinal) Then Value = Value.Substring(2)
        'Java 8 update syntax is numeric; preserve unknown suffixes instead of guessing EA/release ordering.
        Return Regex.Replace(Value, "^(\d+(?:\.\d+)*)_(\d+)$", "$1.$2")
    End Function
    Private Shared Function Identities(Item As ModInfo) As Dictionary(Of String, String)
        Dim Result As New Dictionary(Of String, String)(Item.Provides, StringComparer.Ordinal)
        Result(Item.Id) = Item.Version
        If Not String.IsNullOrEmpty(Item.Group) Then Result(Item.Group & ":" & Item.Id) = Item.Version
        Return Result
    End Function
    Private Shared Function ActiveMods(Report As InspectionReport) As List(Of ModInfo)
        Return Report.Mods.Where(Function(Item) Item.Enabled AndAlso SideApplies(Item.Side, Report.Context)).ToList()
    End Function
    Private Shared Function ProviderMap(Mods As IEnumerable(Of ModInfo)) As Dictionary(Of String, List(Of ModInfo))
        Dim Result As New Dictionary(Of String, List(Of ModInfo))(StringComparer.Ordinal)
        For Each Item In Mods
            For Each Id In Identities(Item).Keys
                If Not Result.ContainsKey(Id) Then Result(Id) = New List(Of ModInfo)
                Result(Id).Add(Item)
            Next
        Next
        'A top-level Fabric/Quilt mod fixes that ID's version; an embedded copy is only a selectable fallback.
        For Each Id In Result.Keys.ToArray()
            Dim Direct = Result(Id).Where(Function(Item) Not Item.IsNested).ToList()
            If Direct.Count > 0 Then Result(Id) = Direct
        Next
        Return Result
    End Function

    Private Class Resolution
        Public State As MatchState
        Public Missing As Boolean
        Public Providers As New List(Of ModInfo)
    End Class
    Private Shared Function Resolve(Requirement As Dependency, Providers As Dictionary(Of String, List(Of ModInfo)), Context As InspectionContext,
                                    Optional Depth As Integer = 0) As Resolution
        If Depth > 32 OrElse Requirement.IsUnknown Then Return New Resolution With {.State = MatchState.Unknown}
        If Not SideApplies(Requirement.Side, Context) Then Return New Resolution With {.State = MatchState.Satisfied}
        Dim Result As New Resolution
        If Requirement.Children.Count > 0 Then
            Dim Children = Requirement.Children.Select(Function(Child) Resolve(Child, Providers, Context, Depth + 1)).ToList()
            Result.State = Combine(Children.Select(Function(Child) Child.State), Requirement.Join = "all")
            Result.Missing = Children.All(Function(Child) Child.Missing)
            Result.Providers = Children.SelectMany(Function(Child) Child.Providers).Distinct().ToList()
        Else
            Dim Candidates As List(Of ModInfo) = Nothing
            If Not Providers.TryGetValue(If(Requirement.Id, ""), Candidates) OrElse Candidates.Count = 0 Then
                Result.State = MatchState.Unsatisfied
                Result.Missing = True
            Else
                Result.Providers = Candidates.ToList()
                Result.State = Combine(Candidates.Select(Function(Item) MatchVersions(Identities(Item)(Requirement.Id), Requirement.Versions, Requirement.Syntax)), False)
            End If
        End If
        If Requirement.Unless IsNot Nothing Then
            Dim UnlessResult = Resolve(Requirement.Unless, Providers, Context, Depth + 1)
            Dim Negative = Requirement.Kind = DependencyKind.Incompatible OrElse Requirement.Kind = DependencyKind.Conflict
            If UnlessResult.State = MatchState.Satisfied Then
                Result.State = If(Negative, MatchState.Unsatisfied, MatchState.Satisfied)
                Result.Missing = False
                Result.Providers = Result.Providers.Concat(UnlessResult.Providers).Distinct().ToList()
            ElseIf UnlessResult.State = MatchState.Unknown AndAlso ((Negative AndAlso Result.State = MatchState.Satisfied) OrElse (Not Negative AndAlso Result.State = MatchState.Unsatisfied)) Then
                Result.State = MatchState.Unknown
            End If
        End If
        If Requirement.Kind = DependencyKind.Optional AndAlso Result.Missing Then Result.State = MatchState.Satisfied
        Return Result
    End Function
    Private Shared Function Combine(States As IEnumerable(Of MatchState), All As Boolean) As MatchState
        Dim Values = States.ToArray()
        If Values.Length = 0 Then Return MatchState.Unknown
        If All Then
            If Values.Contains(MatchState.Unsatisfied) Then Return MatchState.Unsatisfied
            Return If(Values.Contains(MatchState.Unknown), MatchState.Unknown, MatchState.Satisfied)
        End If
        If Values.Contains(MatchState.Satisfied) Then Return MatchState.Satisfied
        Return If(Values.Contains(MatchState.Unknown), MatchState.Unknown, MatchState.Unsatisfied)
    End Function
    Private Shared Sub BuildReport(Report As InspectionReport, Token As CancellationToken)
        Dim Active = ActiveMods(Report), Map = ProviderMap(Active.Concat(Report.Builtins))
        For Each Group In Active.Where(Function(Item) Not Item.IsNested).GroupBy(Function(Item) Item.Id, StringComparer.Ordinal).Where(Function(Items) Items.Count() > 1)
            Report.Issues.Add(New InspectionIssue With {.Kind = IssueKind.DuplicateId, .Owner = Group.First(), .RelatedMods = Group.ToList(), .IsError = True,
                .Message = "重复模组 ID " & Group.Key & "：" & String.Join("、", Group.Select(Function(Item) Path.GetFileName(Item.RootPath)))})
        Next
        Dim RequiredKeys As New HashSet(Of String)(Active.Where(Function(Item) Not Item.IsNested).Select(Function(Item) Item.Key), StringComparer.Ordinal)
        'An embedded candidate is checked as required only when a required chain uniquely selects it.
        Dim Changed = True
        While Changed
            Changed = False
            For Each Item In Active.Where(Function(Entry) RequiredKeys.Contains(Entry.Key))
                Token.ThrowIfCancellationRequested()
                For Each Requirement In Item.Dependencies.Where(Function(Entry) Entry.Kind = DependencyKind.Required AndAlso SideApplies(Entry.Side, Report.Context))
                    Dim Value = Resolve(Requirement, Map, Report.Context)
                    Dim Candidates = Value.Providers.Where(Function(Entry) Entry.IsNested).ToList()
                    If Value.Providers.Count = 1 AndAlso Candidates.Count = 1 AndAlso RequiredKeys.Add(Candidates(0).Key) Then Changed = True
                Next
            Next
        End While
        For Each Item In Active
            Item.IsCandidate = Item.IsNested AndAlso Not RequiredKeys.Contains(Item.Key)
        Next
        For Each Item In Active
            Token.ThrowIfCancellationRequested()
            For Each Requirement In Item.Dependencies
                Dim Value = Resolve(Requirement, Map, Report.Context)
                Dim Edge As New DependencyEdge With {.Owner = Item, .Requirement = Requirement, .State = Value.State, .Providers = Value.Providers,
                    .Evaluated = RequiredKeys.Contains(Item.Key) AndAlso SideApplies(Requirement.Side, Report.Context)}
                Report.Edges.Add(Edge)
                For Each Provider In Value.Providers
                    If Not Report.ReverseDependencies.ContainsKey(Provider.Key) Then Report.ReverseDependencies(Provider.Key) = New List(Of DependencyEdge)
                    Report.ReverseDependencies(Provider.Key).Add(Edge)
                Next
                If Edge.Evaluated Then AddIssue(Report, Edge, Value)
            Next
        Next
        If Active.Any(Function(Item) Item.IsCandidate) Then Report.Notices.Add("内嵌模组的候选版本由加载器选择；未把未选中的候选自身依赖判为启动错误。")
    End Sub
    Private Shared Sub AddIssue(Report As InspectionReport, Edge As DependencyEdge, Value As Resolution)
        Dim Requirement = Edge.Requirement, Kind As IssueKind, IsError = False, Detail As String = Nothing
        Select Case Requirement.Kind
            Case DependencyKind.Required, DependencyKind.Optional
                If Value.State <> MatchState.Unsatisfied Then Return
                If Requirement.Kind = DependencyKind.Optional AndAlso Not Requirement.EnforceWhenPresent Then Return
                Kind = If(Value.Missing, IssueKind.MissingRequired, IssueKind.VersionMismatch)
                IsError = Not Report.MetadataIncomplete OrElse (Not Value.Missing AndAlso Value.Providers.All(Function(Item) Not Item.IsNested))
                Detail = If(Value.Missing, "缺少必需依赖：", "版本不匹配：") & Edge.Owner.Display & " 需要 " & Requirement.Display
            Case DependencyKind.Incompatible, DependencyKind.Conflict
                If Value.State <> MatchState.Satisfied OrElse Value.Missing OrElse Not SideApplies(Requirement.Side, Report.Context) Then Return
                Kind = IssueKind.Incompatible
                IsError = Requirement.Kind = DependencyKind.Incompatible AndAlso Value.Providers.All(Function(Item) Not Item.IsCandidate)
                Detail = If(Value.Providers.Any(Function(Item) Item.IsCandidate), "内嵌候选可能不兼容：", "已声明不兼容：") & Edge.Owner.Display & " 与 " & Requirement.Display
            Case DependencyKind.Recommended
                If Value.State <> MatchState.Unsatisfied Then Return
                Kind = IssueKind.Recommendation
                Detail = "建议依赖未满足：" & Edge.Owner.Display & " 建议安装 " & Requirement.Display
            Case Else
                Return
        End Select
        If Value.Providers.Count > 0 Then Detail &= "；本地：" & String.Join("、", Value.Providers.Select(Function(Item) Item.Display & " " & If(String.IsNullOrEmpty(Item.Version), "未知版本", Item.Version)))
        Dim Disabled = Report.Mods.Where(Function(Item) Not Item.Enabled AndAlso DependencyMentions(Requirement, Identities(Item).Keys)).ToList()
        If Value.Missing AndAlso Disabled.Count > 0 Then Detail &= "；已禁用：" & String.Join("、", Disabled.Select(Function(Item) Path.GetFileName(Item.RootPath)).Distinct())
        If Not String.IsNullOrEmpty(Requirement.Reason) Then Detail &= "（" & Requirement.Reason & "）"
        Report.Issues.Add(New InspectionIssue With {.Kind = Kind, .Owner = Edge.Owner, .Requirement = Requirement, .RelatedMods = Value.Providers, .IsError = IsError, .Message = Detail})
    End Sub

    Friend Shared Function GetImpacts(Report As InspectionReport, RemovedPaths As IEnumerable(Of String)) As List(Of RemovalImpact)
        If Report Is Nothing Then Throw New ArgumentNullException(NameOf(Report))
        Dim Removed As New HashSet(Of String)(RemovedPaths.Select(Function(Name) Path.GetFullPath(Name)), StringComparer.OrdinalIgnoreCase)
        Dim Active = ActiveMods(Report), Original = ProviderMap(Active.Concat(Report.Builtins))
        Dim Unavailable As New HashSet(Of String)(Active.Where(Function(Item) Removed.Contains(Item.RootPath)).Select(Function(Item) Item.Key), StringComparer.Ordinal)
        Dim Result As New List(Of RemovalImpact), Changed = True, Round As Integer
        While Changed
            Changed = False
            Dim Map = ProviderMap(Active.Where(Function(Item) Not Unavailable.Contains(Item.Key)).Concat(Report.Builtins))
            Dim Additions As New List(Of RemovalImpact)
            For Each Item In Active.Where(Function(Entry) Not Removed.Contains(Entry.RootPath) AndAlso Not Unavailable.Contains(Entry.Key) AndAlso Not Entry.IsCandidate)
                Dim Failed As New List(Of Dependency)
                For Each Requirement In Item.Dependencies.Where(Function(Entry) Entry.Kind = DependencyKind.Required AndAlso SideApplies(Entry.Side, Report.Context))
                    If Resolve(Requirement, Original, Report.Context).State = MatchState.Unsatisfied Then Continue For
                    If Resolve(Requirement, Map, Report.Context).State = MatchState.Unsatisfied Then Failed.Add(Requirement)
                Next
                If Failed.Count = 0 Then Continue For
                Additions.Add(New RemovalImpact With {.Mod = Item, .Requirements = Failed, .IsDirect = Round = 0,
                    .IsDefinite = Not Report.MetadataIncomplete AndAlso Failed.All(Function(Requirement) Resolve(Requirement, Original, Report.Context).State = MatchState.Satisfied),
                    .Reason = If(Round = 0, "将失去必需依赖：", "间接受影响，必需依赖将无法加载：") & String.Join("；", Failed.Select(Function(Requirement) Requirement.Display))})
            Next
            For Each Impact In Additions
                Result.Add(Impact)
                Unavailable.Add(Impact.Mod.Key)
                Changed = True
            Next
            Round += 1
        End While
        Return Result
    End Function
    Friend Shared Function DescribeImpacts(Report As InspectionReport, RemovedPaths As IEnumerable(Of String)) As String
        Dim Paths = RemovedPaths.ToArray(), Impacts = GetImpacts(Report, Paths), Online = Report.DescribeOnlineImpacts(Paths)
        If Impacts.Count = 0 Then Return Online
        Dim Lines As New List(Of String) From {"以下模组依赖本次操作涉及的文件："}
        For Each Impact In Impacts.Take(30)
            Lines.Add("• " & Impact.Mod.Display & "（" & Path.GetFileName(Impact.Mod.RootPath) & "）" & If(Impact.IsDefinite, "", " [待确认]") & vbCrLf & "  " & Impact.Reason)
        Next
        If Impacts.Count > 30 Then Lines.Add("另有 " & (Impacts.Count - 30) & " 个模组受影响。")
        If Online <> "" Then Lines.Add(vbCrLf & Online)
        Lines.Add("仍可继续操作；如需一起禁用或删除，请先返回模组管理选择这些文件。")
        Return String.Join(vbCrLf, Lines)
    End Function
    Private Shared Function DependencyMentions(Requirement As Dependency, Ids As IEnumerable(Of String)) As Boolean
        Dim Names = New HashSet(Of String)(Ids, StringComparer.Ordinal)
        If Names.Contains(If(Requirement.Id, "")) Then Return True
        If Requirement.Unless IsNot Nothing AndAlso DependencyMentions(Requirement.Unless, Names) Then Return True
        Return Requirement.Children.Any(Function(Child) DependencyMentions(Child, Names))
    End Function
    Private Shared Function DescribeFile(Report As InspectionReport, FullPath As String) As String
        Dim Target = Path.GetFullPath(FullPath), Items = Report.Mods.Where(Function(Item) String.Equals(Item.RootPath, Target, StringComparison.OrdinalIgnoreCase)).ToList()
        Dim Lines As New List(Of String) From {Path.GetFileName(Target)}
        If Items.Count = 0 Then
            Lines.Add("未识别到支持的本地模组元数据，无法确定依赖关系。")
            Lines.AddRange(Report.Notices.Where(Function(Notice) Notice.Contains(Path.GetFileName(Target))).Take(4))
            Dim Online = Report.DescribeOnlineFile(Target)
            If Online <> "" Then Lines.Add(vbCrLf & Online)
            Return String.Join(vbCrLf, Lines)
        End If
        For Each Item In Items
            Lines.Add("")
            Lines.Add(Item.Display & " · " & If(String.IsNullOrEmpty(Item.Version), "未知版本", Item.Version) & " · " & Item.Format & If(Item.Enabled, "", " · 已禁用") & If(Item.IsNested, " · 内嵌", ""))
            If Item.Side <> "*" Then Lines.Add("运行环境：" & Item.Side)
            Lines.Add("需要的模组：")
            If Item.Dependencies.Count = 0 Then Lines.Add("  元数据未声明依赖。")
            For Each Requirement In Item.Dependencies.Take(60)
                Dim Edge = Report.Edges.FirstOrDefault(Function(Entry) Entry.Owner Is Item AndAlso Entry.Requirement Is Requirement)
                Dim State = If(Edge Is Nothing, "未参与本次启动检查", If(Edge.State = MatchState.Satisfied, "已满足", If(Edge.State = MatchState.Unsatisfied, "未满足", "版本待确认")))
                Dim KindText = New Dictionary(Of DependencyKind, String) From {{DependencyKind.Required, "必需"}, {DependencyKind.Optional, "可选"}, {DependencyKind.Recommended, "建议"},
                    {DependencyKind.Suggested, "可选提示"}, {DependencyKind.Incompatible, "不兼容"}, {DependencyKind.Conflict, "冲突提示"}}
                If Requirement.Kind = DependencyKind.Incompatible OrElse Requirement.Kind = DependencyKind.Conflict Then State = If(Edge IsNot Nothing AndAlso Edge.State = MatchState.Satisfied, "检测到匹配项", "未检测到确定匹配项")
                Lines.Add("  • [" & KindText(Requirement.Kind) & "] " & Requirement.Display & "（" & State & If(Requirement.Side = "*", "", "；" & Requirement.Side) & "）")
            Next
            Dim Reverse = Report.Edges.Where(Function(Edge) Edge.Owner.RootPath <> Item.RootPath AndAlso DependencyMentions(Edge.Requirement, Identities(Item).Keys)).ToList()
            Lines.Add("被这些模组依赖：")
            If Reverse.Count = 0 Then Lines.Add("  未发现其他已启用模组声明依赖此 ID。")
            For Each Edge In Reverse.Take(60)
                Lines.Add("  • " & Edge.Owner.Display & "（" & Path.GetFileName(Edge.Owner.RootPath) & "）" & If(Edge.Requirement.Kind = DependencyKind.Required, " · 必需", " · 可选或兼容声明"))
            Next
        Next
        Lines.Add("")
        Lines.Add("本地依赖来自模组元数据；未声明的运行时依赖无法在此确认。")
        Dim Supplement = Report.DescribeOnlineFile(Target)
        If Supplement <> "" Then Lines.Add(vbCrLf & Supplement)
        Return String.Join(vbCrLf, Lines)
    End Function

    'Three-state version comparison. Unknown is deliberate and must never become a startup error.
    Friend Shared Function MatchVersion(Actual As String, Requirement As String, Optional Syntax As String = "fabric") As MatchState
        Return MatchVersions(Actual, New JValue(Requirement), Syntax)
    End Function
    Private Shared Function MatchVersions(Actual As String, Requirement As JToken, Syntax As String, Optional Depth As Integer = 0) As MatchState
        If Depth > 32 OrElse Requirement Is Nothing Then Return MatchState.Unknown
        If TypeOf Requirement Is JArray Then Return Combine(CType(Requirement, JArray).Select(Function(Value) MatchVersions(Actual, Value, Syntax, Depth + 1)), False)
        If TypeOf Requirement Is JObject Then
            Dim Any = Requirement("any"), All = Requirement("all")
            If (Any Is Nothing) = (All Is Nothing) Then Return MatchState.Unknown
            Dim Values = If(Any, All)
            If Not TypeOf Values Is JArray Then Return MatchState.Unknown
            Return Combine(CType(Values, JArray).Select(Function(Value) MatchVersions(Actual, Value, Syntax, Depth + 1)), All IsNot Nothing)
        End If
        If Requirement.Type <> JTokenType.String Then Return MatchState.Unknown
        Dim Range = Requirement.ToString().Trim()
        If Range = "" OrElse Range = "*" Then Return MatchState.Satisfied
        If String.IsNullOrWhiteSpace(Actual) OrElse Actual = "未知版本" OrElse Actual.Equals("unknown", StringComparison.OrdinalIgnoreCase) OrElse Actual.Contains("$" & "{") OrElse Range.Contains("$" & "{") Then Return MatchState.Unknown
        If Syntax = "maven" Then Return MatchMaven(Actual, Range)
        If Range.Contains("||") Then Return Combine(Range.Split(New String() {"||"}, StringSplitOptions.None).Select(Function(Value) MatchVersion(Actual, Value, Syntax)), False)
        Range = Regex.Replace(Range, "([><=~^]+)\s+", "$1")
        Dim Tokens = Regex.Split(Range, "\s+").Where(Function(Value) Value <> "").ToArray()
        If Tokens.Any(Function(Value) Not Regex.IsMatch(Value, "^(?:>=|<=|>|<|=|~|\^)?(?:[A-Za-z0-9][A-Za-z0-9._:+*-]*|\*)$")) Then Return MatchState.Unknown
        Return Combine(Tokens.Select(Function(Value) MatchPredicate(Actual, Value)), True)
    End Function
    Private Class NumericVersion
        Public Parts As Long()
        Public Prerelease As String
    End Class
    Private Shared Function ParseVersion(Value As String) As NumericVersion
        Dim Match = Regex.Match(If(Value, ""), "^(\d+(?:\.\d+)*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$")
        If Not Match.Success Then Return Nothing
        Dim Parts As New List(Of Long)
        For Each Part In Match.Groups(1).Value.Split("."c)
            Dim Number As Long
            If Not Long.TryParse(Part, NumberStyles.None, CultureInfo.InvariantCulture, Number) Then Return Nothing
            Parts.Add(Number)
        Next
        Return New NumericVersion With {.Parts = Parts.ToArray(), .Prerelease = Match.Groups(2).Value}
    End Function
    Private Shared Function CompareVersions(Left As NumericVersion, Right As NumericVersion) As Integer
        For Index = 0 To Math.Max(Left.Parts.Length, Right.Parts.Length) - 1
            Dim Comparison = If(Index < Left.Parts.Length, Left.Parts(Index), 0L).CompareTo(If(Index < Right.Parts.Length, Right.Parts(Index), 0L))
            If Comparison <> 0 Then Return Comparison
        Next
        If Left.Prerelease = Right.Prerelease Then Return 0
        If Left.Prerelease = "" Then Return 1
        If Right.Prerelease = "" Then Return -1
        Dim LeftParts = Left.Prerelease.Split("."c), RightParts = Right.Prerelease.Split("."c)
        For Index = 0 To Math.Min(LeftParts.Length, RightParts.Length) - 1
            Dim LeftNumber As Long, RightNumber As Long, LeftNumeric = Long.TryParse(LeftParts(Index), LeftNumber), RightNumeric = Long.TryParse(RightParts(Index), RightNumber)
            Dim Comparison As Integer
            If LeftNumeric AndAlso RightNumeric Then
                Comparison = LeftNumber.CompareTo(RightNumber)
            ElseIf LeftNumeric <> RightNumeric Then
                Comparison = If(LeftNumeric, -1, 1)
            Else
                Comparison = StringComparer.Ordinal.Compare(LeftParts(Index), RightParts(Index))
            End If
            If Comparison <> 0 Then Return Comparison
        Next
        Return LeftParts.Length.CompareTo(RightParts.Length)
    End Function
    Private Shared Function MatchPredicate(Actual As String, Predicate As String) As MatchState
        If Predicate = "*" OrElse Predicate = "x" OrElse Predicate = "X" Then Return MatchState.Satisfied
        Dim Match = Regex.Match(Predicate, "^(>=|<=|>|<|=|~|\^)?(.+)$")
        If Not Match.Success Then Return MatchState.Unknown
        Dim OperatorText = Match.Groups(1).Value, Expected = Match.Groups(2).Value, Current = ParseVersion(Actual)
        If Regex.IsMatch(Expected, "^(?:\d+\.)*(?:\*|[xX])$") Then
            If OperatorText <> "" AndAlso OperatorText <> "=" Then Return MatchState.Unknown
            If Current Is Nothing Then Return MatchState.Unknown
            Dim Prefix = Expected.Split("."c).TakeWhile(Function(Part) Part <> "*" AndAlso Part <> "x" AndAlso Part <> "X").ToArray()
            For Index = 0 To Prefix.Length - 1
                Dim Number As Long
                If Not Long.TryParse(Prefix(Index), Number) Then Return MatchState.Unknown
                If If(Index < Current.Parts.Length, Current.Parts(Index), 0L) <> Number Then Return MatchState.Unsatisfied
            Next
            Return MatchState.Satisfied
        End If
        Dim Target = ParseVersion(Expected)
        If Current Is Nothing OrElse Target Is Nothing Then
            If OperatorText <> "" AndAlso OperatorText <> "=" Then Return MatchState.Unknown
            If Not Regex.IsMatch(Expected, "^[A-Za-z0-9][A-Za-z0-9._:+-]*$") OrElse Expected.EndsWith("+", StringComparison.Ordinal) OrElse
                {"latest", "latest.release", "latest.integration"}.Contains(Expected.ToLowerInvariant()) Then Return MatchState.Unknown
            Return If(String.Equals(Actual, Expected, StringComparison.Ordinal), MatchState.Satisfied, MatchState.Unsatisfied)
        End If
        Dim Comparison = CompareVersions(Current, Target)
        Select Case OperatorText
            Case "", "=" : Return If(Comparison = 0, MatchState.Satisfied, MatchState.Unsatisfied)
            Case ">" : Return If(Comparison > 0, MatchState.Satisfied, MatchState.Unsatisfied)
            Case ">=" : Return If(Comparison >= 0, MatchState.Satisfied, MatchState.Unsatisfied)
            Case "<" : Return If(Comparison < 0, MatchState.Satisfied, MatchState.Unsatisfied)
            Case "<=" : Return If(Comparison <= 0, MatchState.Satisfied, MatchState.Unsatisfied)
            Case "~", "^"
                Dim Upper = CType(Target.Parts.Clone(), Long()), Bump As Integer
                If OperatorText = "~" Then
                    Bump = If(Upper.Length > 1, 1, 0)
                Else
                    Bump = 0
                    While Bump < Upper.Length - 1 AndAlso Upper(Bump) = 0
                        Bump += 1
                    End While
                End If
                If Upper(Bump) = Long.MaxValue Then Return MatchState.Unknown
                Upper(Bump) += 1
                For Index = Bump + 1 To Upper.Length - 1
                    Upper(Index) = 0
                Next
                Dim UpperVersion As New NumericVersion With {.Parts = Upper, .Prerelease = ""}
                Return If(Comparison >= 0 AndAlso CompareVersions(Current, UpperVersion) < 0, MatchState.Satisfied, MatchState.Unsatisfied)
            Case Else : Return MatchState.Unknown
        End Select
    End Function
    Private Shared Function MatchMaven(Actual As String, Range As String) As MatchState
        'Maven's bare version is a soft preference, not an exact constraint.
        If Not Range.StartsWith("[", StringComparison.Ordinal) AndAlso Not Range.StartsWith("(", StringComparison.Ordinal) Then Return MatchState.Unknown
        Dim Intervals = Regex.Matches(Range, "[\[\(][^\[\]\(\)]*[\]\)]")
        If Intervals.Count = 0 OrElse Regex.Replace(Range, "[\[\(][^\[\]\(\)]*[\]\)]", "").Trim(" "c, ","c) <> "" Then Return MatchState.Unknown
        Dim Values As New List(Of MatchState), Current = ParseVersion(Actual)
        For Each Interval As Match In Intervals
            Dim Raw = Interval.Value, Body = Raw.Substring(1, Raw.Length - 2), Parts = Body.Split(","c)
            If Parts.Length = 1 Then
                If Raw(0) <> "["c OrElse Raw.Last() <> "]"c Then
                    Values.Add(MatchState.Unknown)
                Else
                    Dim Target = ParseVersion(Parts(0).Trim())
                    If Current Is Nothing OrElse Target Is Nothing OrElse Not Regex.IsMatch(Actual, "^\d+(?:\.\d+)*$") OrElse Not Regex.IsMatch(Parts(0).Trim(), "^\d+(?:\.\d+)*$") Then
                        Values.Add(If(String.Equals(Actual, Parts(0).Trim(), StringComparison.Ordinal), MatchState.Satisfied, MatchState.Unknown))
                    Else
                        Values.Add(If(CompareVersions(Current, Target) = 0, MatchState.Satisfied, MatchState.Unsatisfied))
                    End If
                End If
                Continue For
            End If
            'Maven qualifier ordering differs from SemVer; ordered ranges use purely numeric versions only.
            If Parts.Length <> 2 OrElse Current Is Nothing OrElse Not Regex.IsMatch(Actual, "^\d+(?:\.\d+)*$") Then
                Values.Add(MatchState.Unknown)
                Continue For
            End If
            Dim Bounds As New List(Of MatchState)
            For Index = 0 To 1
                Dim Bound = Parts(Index).Trim()
                If Bound = "" Then
                    Bounds.Add(MatchState.Satisfied)
                    Continue For
                End If
                Dim Target = ParseVersion(Bound)
                If Target Is Nothing OrElse Not Regex.IsMatch(Bound, "^\d+(?:\.\d+)*$") Then
                    Bounds.Add(MatchState.Unknown)
                    Continue For
                End If
                Dim Compare = CompareVersions(Current, Target)
                Dim Fits = If(Index = 0, Compare > 0 OrElse (Compare = 0 AndAlso Raw(0) = "["c), Compare < 0 OrElse (Compare = 0 AndAlso Raw.Last() = "]"c))
                Bounds.Add(If(Fits, MatchState.Satisfied, MatchState.Unsatisfied))
            Next
            Values.Add(Combine(Bounds, True))
        Next
        Return Combine(Values, False)
    End Function
End Class
