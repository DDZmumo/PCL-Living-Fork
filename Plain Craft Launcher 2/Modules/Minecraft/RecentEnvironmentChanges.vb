Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Text.RegularExpressions

Friend NotInheritable Class RecentEnvironmentChange
    Public Kind As String
    Public Category As String
    Public Relative As String
    Public PreviousRelative As String
    Public BeforeVersion As String
    Public AfterVersion As String
    Public BeforeState As String
    Public AfterState As String
End Class

Friend NotInheritable Class RecentEnvironmentChangesReport
    Public HasBaseline As Boolean
    Public BaselineId As String
    Public BaselineCreatedUtc As Date?
    Public Message As String
    Public Changes As New List(Of RecentEnvironmentChange)
    Public Function ToAnalysisText(Optional MaximumEntries As Integer = 30) As String
        Dim Result As New StringBuilder
        Result.AppendLine("上次正常运行后改了什么")
        If Not HasBaseline Then
            Result.AppendLine(If(Message, "尚无正常运行记录，无法进行环境变更对照。"))
            Return Result.ToString().TrimEnd()
        End If
        Result.AppendLine("对照正常运行还原点：" & BaselineCreatedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") & "（" & BaselineId & "）")
        If Not String.IsNullOrWhiteSpace(Message) Then Result.AppendLine(Message)
        Result.AppendLine("以下是文件变更记录，仅表示时间上的相关性，不能据此确定崩溃原因。配置仅列出相对路径，不读取或显示具体值。")
        If Changes.Count = 0 Then
            Result.AppendLine("未发现已记录范围内的模组、配置、Minecraft 或加载器文件变化。")
        Else
            For Each Item In Changes.Take(Math.Max(0, Math.Min(MaximumEntries, 200)))
                Dim Detail = Item.Category & " · " & Item.Kind & " · " & Item.Relative.Substring(2)
                If Item.PreviousRelative IsNot Nothing AndAlso Item.PreviousRelative <> Item.Relative Then Detail &= "（原 " & Item.PreviousRelative.Substring(2) & "）"
                If Item.Category = "模组" Then
                    If Item.BeforeVersion IsNot Nothing OrElse Item.AfterVersion IsNot Nothing Then Detail &= "；版本 " & If(Item.BeforeVersion, "未知") & " → " & If(Item.AfterVersion, "未知")
                    If Item.BeforeState IsNot Nothing OrElse Item.AfterState IsNot Nothing Then Detail &= "；" & If(Item.BeforeState, "不存在") & " → " & If(Item.AfterState, "不存在")
                End If
                Result.AppendLine("- " & Detail)
            Next
            If Changes.Count > Math.Max(0, Math.Min(MaximumEntries, 200)) Then Result.AppendLine($"另有 {Changes.Count - Math.Max(0, Math.Min(MaximumEntries, 200))} 项变更。")
        End If
        Result.Append("可从还原点页面查看此时间点；恢复前会再次确认并保存当前环境。")
        Return Result.ToString()
    End Function
End Class

'只对比最近正常运行的环境清单。存档、截图、账号、地图标记不属于崩溃变更对照范围。
Friend NotInheritable Class RecentEnvironmentChanges
    Private Const MaximumFiles As Integer = 100000
    Private Const MaximumBytes As Long = 128L * 1024 * 1024 * 1024
    Private Shared Function IsGameFile(Relative As String, Optional Coverage As Integer = 2) As Boolean
        Dim Lower = Relative.ToLowerInvariant()
        If Regex.IsMatch(Path.GetFileName(Lower), "\A(?:accounts?|credentials?|access[_-]?tokens?|auth[_-]?tokens?|launcher_accounts|usercache|usernamecache|launcher_profiles)(?:\.|$)", RegexOptions.IgnoreCase) Then Return False
        Return EnvironmentHistory.IsGameFileCovered(Relative, Coverage)
    End Function
    Private Shared Function SafeLogical(Key As String, Optional Coverage As Integer = 2) As Boolean
        If Key Is Nothing OrElse Key.Length < 3 OrElse Key.Length > 500 Then Return False
        ModpackUpdate.RelativePath(Key.Substring(2))
        If Key.StartsWith("g/", StringComparison.Ordinal) Then Return IsGameFile(Key.Substring(2), Coverage)
        If Key.StartsWith("v/", StringComparison.Ordinal) Then
            If Regex.IsMatch(Path.GetFileName(Key), "\A(?:accounts?|credentials?|access[_-]?tokens?|auth[_-]?tokens?|launcher_accounts|usercache|usernamecache|launcher_profiles)(?:\.|$)", RegexOptions.IgnoreCase) Then Return False
            Return (Not Key.Substring(2).Contains("/") AndAlso {".json", ".jar"}.Contains(Path.GetExtension(Key).ToLowerInvariant())) OrElse {"v/PCL/Setup.ini", "v/PCL/config.json", "v/PCL/ModpackOrigin.json"}.Contains(Key)
        End If
        Return False
    End Function
    Private Shared Sub Walk(Root As String, Folder As String, Entries As Dictionary(Of String, String), Token As CancellationToken, Optional Depth As Integer = 0)
        Token.ThrowIfCancellationRequested()
        InstanceCloner.EnsureNoLinks(Folder)
        If Not Directory.Exists(Folder) Then Return
        If Depth > 64 Then Throw New IOException("环境目录嵌套过深，无法安全完成变更对照。")
        For Each Address In Directory.EnumerateFileSystemEntries(Folder)
            Token.ThrowIfCancellationRequested()
            InstanceResourceStore.EnsureWithin(Root, Address)
            InstanceCloner.EnsureNoLinks(Address)
            Dim Relative = Address.Substring(Root.Length + 1).Replace("\", "/")
            If Not IsGameFile(Relative) Then Continue For
            If Directory.Exists(Address) Then
                Walk(Root, Address, Entries, Token, Depth + 1)
            Else
                Entries.Add("g/" & Relative, Address)
                If Entries.Count > MaximumFiles Then Throw New IOException("环境文件超过 100000 个，已停止变更对照。")
            End If
        Next
    End Sub
    Private Shared Function CurrentFiles(VersionFolder As String, GameRoot As String, Token As CancellationToken, Coverage As Integer) As Dictionary(Of String, String)
        Dim Result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        For Each Name In EnvironmentHistory.FoldersForCoverage(Coverage)
            Walk(GameRoot, Path.Combine(GameRoot, Name), Result, Token)
        Next
        For Each Address In Directory.EnumerateFiles(GameRoot)
            Token.ThrowIfCancellationRequested()
            If IsGameFile(Path.GetFileName(Address)) Then
                InstanceCloner.EnsureNoLinks(Address)
                Result.Add("g/" & Path.GetFileName(Address), Address)
            End If
        Next
        For Each Address In Directory.EnumerateFiles(VersionFolder)
            Token.ThrowIfCancellationRequested()
            If SafeLogical("v/" & Path.GetFileName(Address)) Then
                InstanceCloner.EnsureNoLinks(Address)
                Result.Add("v/" & Path.GetFileName(Address), Address)
            End If
        Next
        For Each Name In {"PCL/Setup.ini", "PCL/config.json", "PCL/ModpackOrigin.json"}
            Dim Address = ModpackUpdate.Target(VersionFolder, Name)
            InstanceCloner.EnsureNoLinks(Address)
            If File.Exists(Address) Then Result.Add("v/" & Name, Address)
        Next
        If Result.Count > MaximumFiles Then Throw New IOException("环境文件过多，已停止变更对照。")
        Return Result
    End Function
    Private Shared Function Hashes(Entries As Dictionary(Of String, String), Token As CancellationToken) As Dictionary(Of String, String)
        Dim Result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase), Bytes As Long
        For Each Pair In Entries
            Token.ThrowIfCancellationRequested()
            Bytes += New FileInfo(Pair.Value).Length
            If Bytes > MaximumBytes Then Throw New IOException("环境文件超过 128 GiB，已停止变更对照。")
            Result.Add(Pair.Key, InstanceCloner.Digest(Pair.Value, Token))
        Next
        Return Result
    End Function
    Private Shared Function IsMod(Key As String) As Boolean
        Return Key.StartsWith("g/mods/", StringComparison.OrdinalIgnoreCase) OrElse Key.StartsWith("g/coremods/", StringComparison.OrdinalIgnoreCase)
    End Function
    Private Shared Function EnabledName(Key As String) As String
        Dim Value = Key
        Do While Value.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) OrElse Value.EndsWith(".old", StringComparison.OrdinalIgnoreCase)
            Value = Value.Substring(0, Value.LastIndexOf("."c))
        Loop
        Return Value
    End Function
    Private Shared Function ModState(Key As String) As String
        If Key Is Nothing Then Return Nothing
        If Key.EndsWith(".old", StringComparison.OrdinalIgnoreCase) Then Return "旧版本备份（停用）"
        If Key.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) Then Return "已禁用"
        Return If({".jar", ".zip", ".litemod"}.Contains(Path.GetExtension(Key).ToLowerInvariant()), "已启用", "其他模组文件")
    End Function
    Private Shared Function FileVersion(Key As String) As String
        If Key Is Nothing Then Return Nothing
        Dim Stem = Path.GetFileNameWithoutExtension(EnabledName(Key)), Match = Regex.Match(Stem, "[-_]v?(\d[^\s]*)\z", RegexOptions.IgnoreCase)
        Return If(Match.Success, Match.Groups(1).Value & "（文件名）", Nothing)
    End Function
    Private Shared Function ModFamily(Key As String) As String
        Dim Value = Path.GetFileNameWithoutExtension(EnabledName(Key))
        Return Regex.Replace(Value, "[-_]v?\d.*\z", "", RegexOptions.IgnoreCase)
    End Function
    Private Shared Function ReadVersion(Address As String, Token As CancellationToken) As String
        Try
            Token.ThrowIfCancellationRequested()
            Using Archive = ZipFile.OpenRead(Address)
                For Each Name In {"fabric.mod.json", "quilt.mod.json", "META-INF/mods.toml", "META-INF/neoforge.mods.toml", "mcmod.info"}
                    Token.ThrowIfCancellationRequested()
                    Dim Entry = Archive.GetEntry(Name)
                    If Entry Is Nothing OrElse Entry.Length > 1024 * 1024 Then Continue For
                    Dim Text As String
                    Using Reader As New StreamReader(Entry.Open())
                        Text = Reader.ReadToEnd()
                    End Using
                    Dim Version As String = Nothing
                    If Name.EndsWith(".toml", StringComparison.Ordinal) Then
                        Dim Match = Regex.Match(Text, "(?m)^\s*version\s*=\s*['""]([A-Za-z0-9._+\-]+)['""]")
                        If Match.Success Then Version = Match.Groups(1).Value
                    Else
                        Dim Data = JToken.Parse(Text)
                        If Name = "mcmod.info" Then
                            Dim Items = If(TryCast(Data, JArray), TryCast(Data("modList"), JArray))
                            Version = Items?.FirstOrDefault()?("version")?.ToString()
                        Else
                            Version = If(Name = "quilt.mod.json", Data("quilt_loader")?("version")?.ToString(), Data("version")?.ToString())
                        End If
                    End If
                    If Version IsNot Nothing AndAlso Regex.IsMatch(Version, "\A[A-Za-z0-9._+\-]{1,100}\z") Then Return Version
                Next
            End Using
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is Newtonsoft.Json.JsonException OrElse TypeOf ex Is UnauthorizedAccessException
            '不能解析的模组仍按文件变化显示，不猜测元数据。
        End Try
        Return Nothing
    End Function
    Private Shared Function Change(BeforeKey As String, AfterKey As String, Kind As String, Entries As Dictionary(Of String, String), Token As CancellationToken) As RecentEnvironmentChange
        Dim Key = If(AfterKey, BeforeKey), ModFile = IsMod(Key)
        Dim Category = If(ModFile, "模组", If(Key.StartsWith("v/PCL/"), "启动器版本配置", If(Key.StartsWith("v/"), "Minecraft / 加载器", "配置 / 环境资源")))
        Dim Result As New RecentEnvironmentChange With {.Relative = Key, .PreviousRelative = BeforeKey, .Kind = Kind, .Category = Category}
        If ModFile Then
            Result.BeforeState = ModState(BeforeKey) : Result.AfterState = ModState(AfterKey)
            Result.BeforeVersion = FileVersion(BeforeKey)
            If AfterKey IsNot Nothing Then Result.AfterVersion = If(ReadVersion(Entries(AfterKey), Token), FileVersion(AfterKey))
        End If
        Return Result
    End Function
    Friend Shared Function BuildInstance(Instance As McInstance, Token As CancellationToken) As RecentEnvironmentChangesReport
        Return Build(Instance.PathVersion, InstanceCloner.GameRootForInstance(Instance), Token)
    End Function
    Friend Shared Function Build(VersionFolder As String, GameRoot As String, Token As CancellationToken) As RecentEnvironmentChangesReport
        Token.ThrowIfCancellationRequested()
        VersionFolder = InstanceCloner.NormalizeRoot(VersionFolder) : GameRoot = InstanceCloner.NormalizeRoot(GameRoot)
        Dim Result As New RecentEnvironmentChangesReport, Point = EnvironmentHistory.Points(VersionFolder).FirstOrDefault(Function(Item) Item.Value(Of Boolean?)("healthy").GetValueOrDefault())
        If Point Is Nothing Then
            Result.Message = "尚无正常运行记录，无法判断上次正常运行后的变更。可在版本正常启动并退出后再次查看。"
            Return Result
        End If
        If Not PathUtils.IsSame(Point("gameRoot")?.ToString(), GameRoot) Then
            Result.Message = "最近正常运行记录使用了不同的游戏目录。当前隔离设置已变化，无法可靠对照。"
            Return Result
        End If
        Dim Id = Point("id")?.ToString()
        If Not Regex.IsMatch(If(Id, ""), "\A[a-f0-9]{32}\z") Then Throw New IOException("正常运行还原点编号无效。")
        Dim Files = TryCast(Point("files"), JObject)
        If Files Is Nothing OrElse Files.Count > MaximumFiles Then Throw New IOException("正常运行环境清单无效或过大。")
        Dim Baseline As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Dim Coverage = EnvironmentHistory.CoverageVersion(Point)
        If Coverage = 1 Then Result.Message = "此旧还原点未覆盖扩展环境目录，相关文件未纳入对照。"
        For Each Item In Files.Properties()
            Token.ThrowIfCancellationRequested()
            If Not SafeLogical(Item.Name, Coverage) Then Continue For
            Dim Hash = Item.Value.ToString()
            If Not Regex.IsMatch(Hash, "\Asha1:[a-f0-9]{40}\z") Then Throw New IOException("正常运行环境清单的校验值无效。")
            Baseline.Add(Item.Name, Hash)
        Next
        Dim Entries = CurrentFiles(VersionFolder, GameRoot, Token, Coverage), Current = Hashes(Entries, Token)
        Dim Removed As New HashSet(Of String)(Baseline.Keys.Where(Function(Key) Not Current.ContainsKey(Key)), StringComparer.OrdinalIgnoreCase)
        Dim Added As New HashSet(Of String)(Current.Keys.Where(Function(Key) Not Baseline.ContainsKey(Key)), StringComparer.OrdinalIgnoreCase)
        For Each OldKey In Removed.Where(Function(Key) IsMod(Key)).OrderBy(Function(Key) Key).ToArray()
            Token.ThrowIfCancellationRequested()
            Dim Matches = Added.Where(Function(Key) IsMod(Key) AndAlso String.Equals(EnabledName(Key), EnabledName(OldKey), StringComparison.OrdinalIgnoreCase)).ToList()
            If Matches.Count <> 1 Then Continue For
            Dim NewKey = Matches(0), Kind = If(ModState(OldKey) <> ModState(NewKey), If(ModState(NewKey) = "已启用", "启用", "禁用 / 保留旧版"), "重命名")
            If Baseline(OldKey) <> Current(NewKey) Then Kind &= "并更新内容"
            Result.Changes.Add(Change(OldKey, NewKey, Kind, Entries, Token))
            Removed.Remove(OldKey) : Added.Remove(NewKey)
        Next
        '文件名推测只能用于唯一配对；多份同族模组保留新增/删除，避免猜测错误版本关系。
        For Each OldKey In Removed.Where(Function(Key) IsMod(Key) AndAlso FileVersion(Key) IsNot Nothing).OrderBy(Function(Key) Key).ToArray()
            Token.ThrowIfCancellationRequested()
            Dim Family = ModFamily(OldKey), Matches = Added.Where(Function(Key) IsMod(Key) AndAlso String.Equals(ModFamily(Key), Family, StringComparison.OrdinalIgnoreCase)).ToList()
            If Family = "" OrElse Matches.Count <> 1 OrElse Removed.Where(Function(Key) IsMod(Key) AndAlso String.Equals(ModFamily(Key), Family, StringComparison.OrdinalIgnoreCase)).Count() <> 1 Then Continue For
            Dim NewKey = Matches(0)
            Result.Changes.Add(Change(OldKey, NewKey, "文件版本变化（按文件名配对）", Entries, Token))
            Removed.Remove(OldKey) : Added.Remove(NewKey)
        Next
        For Each Key In Baseline.Keys.Intersect(Current.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(Function(Value) Value)
            Token.ThrowIfCancellationRequested()
            If Baseline(Key) <> Current(Key) Then Result.Changes.Add(Change(Key, Key, If(IsMod(Key), "更新内容", "修改"), Entries, Token))
        Next
        For Each Key In Removed.OrderBy(Function(Value) Value)
            Result.Changes.Add(Change(Key, Nothing, "删除", Entries, Token))
        Next
        For Each Key In Added.OrderBy(Function(Value) Value)
            Result.Changes.Add(Change(Nothing, Key, "新增", Entries, Token))
        Next
        '扫描期间也可能有编辑器或游戏写入；不把不一致的快照呈现为完整结论。
        Dim Latest = Hashes(CurrentFiles(VersionFolder, GameRoot, Token, Coverage), Token)
        If Current.Count <> Latest.Count OrElse Current.Any(Function(Pair) Not Latest.ContainsKey(Pair.Key) OrElse Latest(Pair.Key) <> Pair.Value) Then Throw New IOException("环境在扫描期间发生变化，请稍后重新查看变更。")
        Result.HasBaseline = True : Result.BaselineId = Id : Result.BaselineCreatedUtc = CDate(Point("created")).ToUniversalTime()
        Return Result
    End Function
    Friend Shared Function AppendAnalysis(Analysis As String, Report As RecentEnvironmentChangesReport) As String
        If Report Is Nothing Then Return Analysis
        Return If(Analysis, "").TrimEnd() & vbCrLf & vbCrLf & Report.ToAnalysisText()
    End Function
End Class
