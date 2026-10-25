Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading

Friend Class InstanceKeybinding
    Friend Key As String
    Friend Name As String
    Friend Owners As String = "未知归属"
    Friend Value As String
    Friend Normalized As String
    Friend SharedCount As Integer
    Friend Saved As Boolean = True
End Class

Friend Module InstanceKeybindings
    Friend Function ReadBindings(GameFolder As String, Token As CancellationToken) As List(Of InstanceKeybinding)
        Dim FileName = Path.Combine(GameFolder, "options.txt"), Result As New List(Of InstanceKeybinding)
        If Not File.Exists(FileName) Then Return DefaultBindings()
        InstanceResourceStore.EnsureWithin(GameFolder, FileName)
        If New FileInfo(FileName).Length > 4 * 1024 * 1024 Then Throw New IOException("游戏设置文件过大。")
        Dim Text = File.ReadAllText(FileName, New UTF8Encoding(False, True))
        Dim Language = "zh_cn"
        For Each Line In Text.Replace(vbCrLf, vbLf).Split(ChrW(10))
            Token.ThrowIfCancellationRequested()
            Dim Separator = Line.IndexOf(":"c)
            If Separator < 1 Then Continue For
            Dim Key = Line.Substring(0, Separator), Value = Line.Substring(Separator + 1)
            If Key = "lang" Then Language = Value.ToLowerInvariant()
            If Not Key.StartsWith("key_", StringComparison.Ordinal) Then Continue For
            Dim Existing = Result.FirstOrDefault(Function(Item) Item.Key = Key)
            If Existing IsNot Nothing Then Result.Remove(Existing)
            Result.Add(New InstanceKeybinding With {.Key = Key, .Name = Key.Substring(4), .Value = Value, .Normalized = PersonalGameSettings.NormalizeBinding(Value), .Owners = If(PersonalGameSettings.VanillaBinding(Key), "Minecraft", "未知归属")})
        Next
        For Each DefaultEntry In DefaultBindings()
            Dim Existing = Result.FirstOrDefault(Function(Item) Item.Key = DefaultEntry.Key OrElse (DefaultEntry.Key = "key_key.swapOffhand" AndAlso Item.Key = "key_key.swapHands"))
            If Existing Is Nothing Then
                Result.Add(DefaultEntry)
            Else
                Existing.Name = DefaultEntry.Name
            End If
        Next
        Dim Targets = Result.Where(Function(Item) Not PersonalGameSettings.VanillaBinding(Item.Key)).ToDictionary(Function(Item) Item.Key.Substring(4), Function(Item) Item)
        Dim OwnerMap As New Dictionary(Of String, HashSet(Of String))(StringComparer.Ordinal)
        Dim Mods = Path.Combine(GameFolder, "mods")
        If Directory.Exists(Mods) Then
            For Each ModFile In Directory.EnumerateFiles(Mods, "*.jar")
                Token.ThrowIfCancellationRequested()
                Try
                    InstanceResourceStore.EnsureWithin(GameFolder, ModFile)
                    Using Archive = ZipFile.OpenRead(ModFile)
                        Dim ModName = Path.GetFileNameWithoutExtension(ModFile)
                        Dim Metadata = Archive.GetEntry("fabric.mod.json")
                        If Metadata IsNot Nothing AndAlso Metadata.Length < 1024 * 1024 Then
                            Using Reader As New StreamReader(Metadata.Open())
                                Dim Info = JObject.Parse(Reader.ReadToEnd())
                                ModName = If(Info("name")?.ToString(), ModName)
                            End Using
                        Else
                            Dim Toml = If(Archive.GetEntry("META-INF/neoforge.mods.toml"), Archive.GetEntry("META-INF/mods.toml"))
                            If Toml IsNot Nothing AndAlso Toml.Length < 1024 * 1024 Then
                                Using Reader As New StreamReader(Toml.Open())
                                    Dim Match = Regex.Match(Reader.ReadToEnd(), "(?m)^\s*displayName\s*=\s*""([^""]+)""")
                                    If Match.Success Then ModName = Match.Groups(1).Value
                                End Using
                            End If
                        End If
                        Dim Languages = Archive.Entries.Where(Function(Entry) Entry.FullName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) AndAlso Entry.Length < 4 * 1024 * 1024 AndAlso
                            (Entry.FullName.EndsWith("/lang/en_us.json", StringComparison.OrdinalIgnoreCase) OrElse Entry.FullName.EndsWith("/lang/" & Language & ".json", StringComparison.OrdinalIgnoreCase) OrElse Entry.FullName.EndsWith("/lang/en_us.lang", StringComparison.OrdinalIgnoreCase) OrElse Entry.FullName.EndsWith("/lang/" & Language & ".lang", StringComparison.OrdinalIgnoreCase))).OrderBy(Function(Entry) Entry.FullName.Contains("/lang/" & Language & ".")).ToList()
                        For Each Entry In Languages
                            Token.ThrowIfCancellationRequested()
                            Dim Values As New Dictionary(Of String, String)(StringComparer.Ordinal)
                            Using Reader As New StreamReader(Entry.Open())
                                Dim Content = Reader.ReadToEnd()
                                If Entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) Then
                                    For Each Pair In JObject.Parse(Content).Properties().Where(Function(Item) Item.Value.Type = JTokenType.String)
                                        Values(Pair.Name) = Pair.Value.ToString()
                                    Next
                                Else
                                    For Each Line In Content.Replace(vbCrLf, vbLf).Split(ChrW(10))
                                        Dim Separator = Line.IndexOf("="c)
                                        If Separator > 0 Then Values(Line.Substring(0, Separator)) = Line.Substring(Separator + 1)
                                    Next
                                End If
                            End Using
                            For Each Pair In Targets
                                Dim Title As String = Nothing
                                If Not Values.TryGetValue(Pair.Key, Title) Then Continue For
                                Pair.Value.Name = Title.ReplaceLineEndings(" ")
                                If Not OwnerMap.ContainsKey(Pair.Key) Then OwnerMap(Pair.Key) = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                                OwnerMap(Pair.Key).Add(ModName & "（" & Path.GetFileName(ModFile) & "）")
                            Next
                        Next
                    End Using
                Catch ex As OperationCanceledException
                    Throw
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is Newtonsoft.Json.JsonException
                    Logger.Warn(ex, "读取按键所属 Mod 的本地信息失败")
                End Try
            Next
        End If
        For Each Pair In OwnerMap
            Targets(Pair.Key).Owners = String.Join("、", Pair.Value.OrderBy(Function(Name) Name))
        Next
        For Each Group In Result.Where(Function(Item) Not String.IsNullOrEmpty(Item.Normalized) AndAlso Item.Normalized <> "key.keyboard.unknown").GroupBy(Function(Item) Item.Normalized, StringComparer.OrdinalIgnoreCase)
            For Each Item In Group
                Item.SharedCount = Group.Count()
            Next
        Next
        Return Result '“全部”保留游戏配置中的顺序；同键分组仅由占用视图执行。
    End Function
    Friend Sub Save(GameFolder As String, Key As String, Previous As String, Replacement As String)
        If HasRunningMinecraft Then Throw New IOException("请先关闭游戏，再修改按键配置。")
        If Not Key.StartsWith("key_", StringComparison.Ordinal) OrElse PersonalGameSettings.NormalizeBinding(Replacement) Is Nothing Then Throw New IOException("按键值无效。")
        Dim FileName = Path.Combine(GameFolder, "options.txt")
        InstanceResourceStore.EnsureWithin(GameFolder, FileName)
        If Not File.Exists(FileName) OrElse New FileInfo(FileName).Length > 4 * 1024 * 1024 Then Throw New IOException("请先运行一次游戏，生成按键配置。")
        SyncLock LocalGameData.Gate
            Dim Bytes = File.ReadAllBytes(FileName), Text = New UTF8Encoding(False, True).GetString(Bytes).TrimStart(ChrW(&HFEFF))
            Dim Newline = If(Text.Contains(vbCrLf), vbCrLf, vbLf), Lines = Text.Replace(vbCrLf, vbLf).Split(ChrW(10)).ToList()
            Dim Index = Lines.FindLastIndex(Function(Line) Line.StartsWith(Key & ":", StringComparison.Ordinal))
            If Index < 0 Then
                If Previous IsNot Nothing Then Throw New IOException("该按键配置已变化，请刷新后重试。")
                If Lines.Count > 0 AndAlso Lines.Last() = "" Then Lines.RemoveAt(Lines.Count - 1)
                Lines.Add(Key & ":" & Replacement)
                If Text.EndsWith(vbLf, StringComparison.Ordinal) Then Lines.Add("")
            ElseIf Previous Is Nothing OrElse Lines(Index).Substring(Key.Length + 1) <> Previous Then
                Throw New IOException("该按键配置已变化，请刷新后重试。")
            End If
            For Position = 0 To Lines.Count - 1
                If Lines(Position).StartsWith(Key & ":", StringComparison.Ordinal) Then Lines(Position) = Key & ":" & Replacement
            Next
            Dim Encoding As New UTF8Encoding(Bytes.Length >= 3 AndAlso Bytes(0) = &HEF AndAlso Bytes(1) = &HBB AndAlso Bytes(2) = &HBF)
            LocalGameData.AtomicWrite(FileName, Encoding.GetPreamble().Concat(Encoding.GetBytes(String.Join(Newline, Lines))).ToArray(), LocalGameData.Hash(Bytes), True)
            IniClearCache(FileName)
        End SyncLock
    End Sub
    Private Function DefaultBindings() As List(Of InstanceKeybinding)
        Dim Defaults As New Dictionary(Of String, String) From {
            {"attack", "mouse.left"}, {"use", "mouse.right"}, {"pickItem", "mouse.middle"},
            {"forward", "keyboard.w"}, {"back", "keyboard.s"}, {"left", "keyboard.a"}, {"right", "keyboard.d"},
            {"jump", "keyboard.space"}, {"sneak", "keyboard.left.shift"}, {"sprint", "keyboard.left.control"},
            {"drop", "keyboard.q"}, {"inventory", "keyboard.e"}, {"chat", "keyboard.t"}, {"playerlist", "keyboard.tab"},
            {"command", "keyboard.slash"}, {"screenshot", "keyboard.f2"}, {"togglePerspective", "keyboard.f5"},
            {"fullscreen", "keyboard.f11"}, {"swapOffhand", "keyboard.f"}, {"advancements", "keyboard.l"},
            {"saveToolbarActivator", "keyboard.c"}, {"loadToolbarActivator", "keyboard.x"}}
        Dim Names As New Dictionary(Of String, String) From {
            {"attack", "攻击/破坏"}, {"use", "使用物品/放置方块"}, {"pickItem", "选取方块"},
            {"forward", "前进"}, {"back", "后退"}, {"left", "向左"}, {"right", "向右"}, {"jump", "跳跃"},
            {"sneak", "潜行"}, {"sprint", "疾跑"}, {"drop", "丢弃物品"}, {"inventory", "打开物品栏"},
            {"chat", "聊天"}, {"playerlist", "玩家列表"}, {"command", "输入命令"}, {"screenshot", "截图"},
            {"togglePerspective", "切换视角"}, {"fullscreen", "切换全屏"}, {"swapOffhand", "交换副手物品"},
            {"advancements", "进度"}, {"saveToolbarActivator", "保存快捷栏"}, {"loadToolbarActivator", "加载快捷栏"}}
        Dim Result As New List(Of InstanceKeybinding)
        For Each Pair In Defaults
            Result.Add(New InstanceKeybinding With {.Key = "key_key." & Pair.Key, .Name = Names(Pair.Key), .Owners = "Minecraft", .Value = "key." & Pair.Value, .Normalized = "key." & Pair.Value, .Saved = False, .SharedCount = 1})
        Next
        For Index = 1 To 9
            Result.Add(New InstanceKeybinding With {.Key = "key_key.hotbar." & Index, .Name = "快捷栏 " & Index, .Owners = "Minecraft", .Value = "key.keyboard." & Index, .Normalized = "key.keyboard." & Index, .Saved = False, .SharedCount = 1})
        Next
        Return Result
    End Function
End Module
