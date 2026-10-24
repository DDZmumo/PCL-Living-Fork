Imports System.IO
Imports System.Text
Imports System.Globalization

Friend Class PersonalOptionsSession
    Friend Id As String = Guid.NewGuid().ToString("N")
    Friend VersionPath As String
    Friend GameFolder As String
    Friend Baseline As JObject
    Friend Keys As New HashSet(Of String)(StringComparer.Ordinal)
    Friend Tracked As Boolean
End Class

Friend Module PersonalGameSettings
    Private Const ProfileName As String = "personal-options.json"
    Private ReadOnly BasicKeys As New HashSet(Of String)({"attack", "use", "pickItem", "forward", "back", "left", "right", "jump", "sneak", "sprint", "drop", "inventory", "chat", "playerlist", "command", "screenshot", "togglePerspective", "smoothCamera", "fullscreen", "spectatorOutlines", "advancements", "swapOffhand", "saveToolbarActivator", "loadToolbarActivator"}.Select(Function(Name) "key_key." & Name).Concat(Enumerable.Range(1, 9).Select(Function(Index) "key_key.hotbar." & Index)), StringComparer.Ordinal)
    Private ReadOnly MouseKeys As New HashSet(Of String)({"mouseSensitivity", "invertYMouse", "mouseWheelSensitivity", "discrete_mouse_scroll", "rawMouseInput", "touchscreen", "toggleSprint", "toggleCrouch"}, StringComparer.Ordinal)
    Private ReadOnly ViewKeys As New HashSet(Of String)({"fov", "fovEffectScale", "screenEffectScale", "bobView"}, StringComparer.Ordinal)
    Private ReadOnly AccessibilityKeys As New HashSet(Of String)({"showSubtitles", "autoJump", "highContrast", "darknessEffectScale", "hideLightningFlashes", "notificationDisplayTime"}, StringComparer.Ordinal)
    Private ReadOnly LegacyKeys As Dictionary(Of Integer, String) = CreateLegacyKeys()

    Friend Function Enabled(Instance As McInstance) As Boolean
        Return Instance IsNot Nothing AndAlso Settings.Get(Of Boolean)("LaunchPersonalOptionsSync") AndAlso Settings.Get(Of Integer)("VersionPersonalOptionsSync", Instance:=Instance) = 0
    End Function
    Private Function CanonicalKey(Key As String) As String
        Return If(Key = "key_key.swapHands", "key_key.swapOffhand", Key)
    End Function
    Private Function Selected(Key As String) As Boolean
        If Key.StartsWith("key_", StringComparison.Ordinal) Then Return Settings.Get(Of Boolean)("LaunchPersonalOptionsKeys") AndAlso BasicKeys.Contains(CanonicalKey(Key))
        If MouseKeys.Contains(Key) Then Return Settings.Get(Of Boolean)("LaunchPersonalOptionsMouse")
        If ViewKeys.Contains(Key) Then Return Settings.Get(Of Boolean)("LaunchPersonalOptionsView")
        If AccessibilityKeys.Contains(Key) Then Return Settings.Get(Of Boolean)("LaunchPersonalOptionsAccessibility")
        Return Key.StartsWith("soundCategory_", StringComparison.Ordinal) AndAlso Settings.Get(Of Boolean)("LaunchPersonalOptionsAudio")
    End Function
    Friend Function Parse(Text As String) As Dictionary(Of String, String)
        Dim Values As New Dictionary(Of String, String)(StringComparer.Ordinal)
        For Each Line In Text.Replace(vbCrLf, vbLf).Split(ChrW(10))
            Dim Separator = Line.IndexOf(":"c)
            If Separator <= 0 Then Continue For
            Dim Key = CanonicalKey(Line.Substring(0, Separator))
            Values(Key) = Line.Substring(Separator + 1)
        Next
        Return Values
    End Function
    Private Function ReadOptions(FileName As String) As Byte()
        If Not File.Exists(FileName) Then Return Nothing
        If New FileInfo(FileName).Length > 4 * 1024 * 1024 OrElse (File.GetAttributes(FileName) And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("不读取过大或链接形式的 options.txt。")
        Return File.ReadAllBytes(FileName)
    End Function
    Private Function Decode(Bytes As Byte()) As String
        Return New UTF8Encoding(False, True).GetString(Bytes).TrimStart(ChrW(&HFEFF))
    End Function
    Private Function Modern(Instance As McInstance) As Boolean
        Return Instance.Version.Valid AndAlso Instance.Version.Vanilla.Major >= 13
    End Function
    Private Function CanonicalValue(Key As String, Value As String) As String
        If Value.Length > 4096 OrElse Value.IndexOfAny({ChrW(0), ChrW(10), ChrW(13)}) >= 0 Then Return Nothing
        If Key = "fov" Then
            Dim FovNumber As Double
            If Not Double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, FovNumber) OrElse Double.IsNaN(FovNumber) OrElse Double.IsInfinity(FovNumber) Then Return Nothing
            If FovNumber >= -1 AndAlso FovNumber <= 1 Then FovNumber = 70 + FovNumber * 40
            Return If(FovNumber >= 30 AndAlso FovNumber <= 110, FovNumber.ToString("0.########", CultureInfo.InvariantCulture), Nothing)
        End If
        If Not Key.StartsWith("key_", StringComparison.Ordinal) Then Return Value
        Dim Number As Integer
        If Not Integer.TryParse(Value, Number) Then Return If(Value.StartsWith("key.keyboard.") OrElse Value.StartsWith("key.mouse."), Value, Nothing)
        If Number <= -1 AndAlso Number >= -100 Then
            Dim Button = Number + 100
            Return "key.mouse." & If(Button = 0, "left", If(Button = 1, "right", If(Button = 2, "middle", (Button + 1).ToString(CultureInfo.InvariantCulture))))
        End If
        Dim Name As String = Nothing
        Return If(LegacyKeys.TryGetValue(Number, Name), "key.keyboard." & Name, Nothing)
    End Function
    Private Function ForVersion(Key As String, Value As String, IsModern As Boolean) As String
        Value = CanonicalValue(Key, Value)
        If Value Is Nothing OrElse IsModern OrElse Not Key.StartsWith("key_", StringComparison.Ordinal) Then Return Value
        If Value.StartsWith("key.mouse.") Then
            Dim Button = Value.Substring(10), Number As Integer
            If Button = "left" Then Return "-100"
            If Button = "right" Then Return "-99"
            If Button = "middle" Then Return "-98"
            If Integer.TryParse(Button, Number) AndAlso Number >= 4 AndAlso Number <= 100 Then Return (Number - 101).ToString(CultureInfo.InvariantCulture)
            Return Nothing
        End If
        Dim Pair = LegacyKeys.FirstOrDefault(Function(Item) "key.keyboard." & Item.Value = Value)
        Return If(Pair.Value Is Nothing, Nothing, Pair.Key.ToString(CultureInfo.InvariantCulture))
    End Function
    Friend Function MergeText(Text As String, Values As JObject, IsModern As Boolean, Keys As ISet(Of String), Optional FovDegrees As Boolean = True) As String
        Dim Newline = If(Text.Contains(vbCrLf), vbCrLf, vbLf)
        Dim FinalNewline = Text.EndsWith(vbLf, StringComparison.Ordinal)
        Dim Lines = Text.Replace(vbCrLf, vbLf).Split(ChrW(10)).ToList()
        Dim Present = Parse(Text), Applied As New HashSet(Of String)(StringComparer.Ordinal)
        Dim Replacements As New Dictionary(Of String, String)(StringComparer.Ordinal)
        For Each Pair In Values.Properties()
            Dim Key = CanonicalKey(Pair.Name)
            If Not Keys.Contains(Key) OrElse Not Selected(Key) Then Continue For
            '版本专用设置只有在目标已存在该键时才同步；不注入 Mod 或未知配置。
            If Not Present.ContainsKey(Key) AndAlso Not BasicKeys.Contains(Key) AndAlso Not {"mouseSensitivity", "invertYMouse", "fov", "showSubtitles"}.Contains(Key) Then Continue For
            Dim Converted = ForVersion(Key, Pair.Value.ToString(), IsModern)
            If Key = "fov" AndAlso Converted IsNot Nothing Then
                Dim Existing As String = Nothing, Numeric As Double
                Dim UsesDegrees = FovDegrees
                If Present.TryGetValue(Key, Existing) AndAlso Double.TryParse(Existing, NumberStyles.Float, CultureInfo.InvariantCulture, Numeric) Then UsesDegrees = Math.Abs(Numeric) > 1
                Dim Degrees = Double.Parse(Converted, CultureInfo.InvariantCulture)
                Converted = If(UsesDegrees, Math.Round(Degrees).ToString(CultureInfo.InvariantCulture), ((Degrees - 70) / 40).ToString("0.########", CultureInfo.InvariantCulture))
            End If
            If Converted IsNot Nothing Then Replacements(Key) = Converted
        Next
        For Index = 0 To Lines.Count - 1
            Dim Separator = Lines(Index).IndexOf(":"c)
            If Separator <= 0 Then Continue For
            Dim Key = CanonicalKey(Lines(Index).Substring(0, Separator)), Value As String = Nothing
            If Not Replacements.TryGetValue(Key, Value) Then Continue For
            Lines(Index) = Lines(Index).Substring(0, Separator + 1) & Value
            Applied.Add(Key)
        Next
        For Each Pair In Replacements.Where(Function(Item) Not Applied.Contains(Item.Key))
            If Lines.Count > 0 AndAlso Lines.Last() = "" Then Lines.RemoveAt(Lines.Count - 1)
            Lines.Add(If(Pair.Key = "key_key.swapOffhand" AndAlso Not IsModern, "key_key.swapHands", Pair.Key) & ":" & Pair.Value)
        Next
        Dim Result = String.Join(Newline, Lines)
        Return Result & If(FinalNewline AndAlso Not Result.EndsWith(Newline, StringComparison.Ordinal), Newline, "")
    End Function
    Friend Function HasProfile() As Boolean
        SyncLock LocalGameData.Gate
            Dim Values = TryCast(LocalGameData.ReadJson(ProfileName)("values"), JObject)
            Return Values IsNot Nothing AndAlso Values.Count > 0
        End SyncLock
    End Function
    Friend Sub ImportFrom(Instance As McInstance)
        If Instance Is Nothing Then Throw New IOException("请先选择一个已经运行过的游戏版本。")
        If HasRunningMinecraft Then Throw New IOException("请先关闭游戏，再导入个人设置。")
        Dim Bytes = ReadOptions(Path.Combine(Instance.PathIndie, "options.txt"))
        If Bytes Is Nothing Then Throw New IOException("该版本尚未生成 options.txt，请先启动一次游戏并保存设置。")
        Dim Values As New JObject
        For Each Pair In Parse(Decode(Bytes)).Where(Function(Item) Selected(Item.Key))
            Dim Value = CanonicalValue(Pair.Key, Pair.Value)
            If Value IsNot Nothing Then Values(Pair.Key) = Value
        Next
        If Values.Count = 0 Then Throw New IOException("该版本没有可导入的个人设置。")
        SyncLock LocalGameData.Gate
            LocalGameData.WriteJson(ProfileName, New JObject(New JProperty("version", 1), New JProperty("values", Values)))
        End SyncLock
    End Sub
    Friend Function Prepare(Instance As McInstance) As PersonalOptionsSession
        If Not Enabled(Instance) OrElse HasRunningMinecraft Then Return Nothing
        RecoverExitedSessions()
        Dim Folder = Instance.PathIndie, FileName = Path.Combine(Folder, "options.txt")
        If HasActiveSession(Folder) Then Return Nothing
        Dim Bytes = ReadOptions(FileName)
        '首次启动仍交给游戏/默认配置 Mod 生成文件，避免抢先创建文件阻止作者的默认配置生效。
        If Bytes Is Nothing Then Return Nothing
        SyncLock LocalGameData.Gate
            Dim Values = TryCast(LocalGameData.ReadJson(ProfileName)("values"), JObject)
            If Values Is Nothing OrElse Values.Count = 0 Then Return Nothing
            Dim Keys As New HashSet(Of String)(Values.Properties().Where(Function(Pair) Selected(Pair.Name)).Select(Function(Pair) Pair.Name), StringComparer.Ordinal)
            If Not Instance.Version.Valid Then Keys.RemoveWhere(Function(Key) Key.StartsWith("key_", StringComparison.Ordinal))
            Dim Text = Decode(Bytes), Merged = MergeText(Text, Values, Modern(Instance), Keys, Instance.Version.Valid AndAlso Instance.Version.Vanilla.Major >= 19)
            If Text <> Merged Then
                Dim Encoded = New UTF8Encoding(Bytes.Length >= 3 AndAlso Bytes(0) = &HEF AndAlso Bytes(1) = &HBB AndAlso Bytes(2) = &HBF)
                Dim Body = Encoded.GetPreamble().Concat(Encoded.GetBytes(Merged)).ToArray()
                LocalGameData.AtomicWrite(FileName, Body, LocalGameData.Hash(Bytes), True)
                IniClearCache(FileName)
            End If
            Dim Baseline As New JObject
            For Each Pair In Parse(Merged).Where(Function(Item) Keys.Contains(Item.Key))
                Dim Value = CanonicalValue(Pair.Key, Pair.Value)
                If Value IsNot Nothing Then Baseline(Pair.Key) = Value
            Next
            Return New PersonalOptionsSession With {.VersionPath = Instance.PathVersion, .GameFolder = Folder, .Baseline = Baseline, .Keys = Keys}
        End SyncLock
    End Function
    Friend Sub Track(Session As PersonalOptionsSession, Game As Process)
        If Session Is Nothing Then Return
        SyncLock LocalGameData.Gate
            LocalGameData.WriteJson("options-sessions/" & Session.Id & ".json", New JObject(New JProperty("versionPath", Session.VersionPath), New JProperty("gameFolder", Session.GameFolder), New JProperty("pid", Game.Id), New JProperty("started", Game.StartTime.ToUniversalTime().Ticks), New JProperty("baseline", Session.Baseline), New JProperty("keys", New JArray(Session.Keys))))
            Session.Tracked = True
        End SyncLock
    End Sub
    Friend Function NormalizeBinding(Value As String) As String
        If Value Is Nothing Then Return Nothing
        Dim Parts = Value.Split(":"c), Main = CanonicalValue("key_key.forward", Parts(0))
        If Main Is Nothing Then Return Nothing
        Return Main & If(Parts.Length > 1, ":" & String.Join(":", Parts.Skip(1)).ToUpperInvariant(), "")
    End Function
    Friend Function BindingForVersion(Value As String, IsModern As Boolean) As String
        If Value Is Nothing Then Return Nothing
        Dim Parts = Value.Split(":"c), Main = ForVersion("key_key.forward", Parts(0), IsModern)
        Return If(Main Is Nothing, Nothing, Main & If(Parts.Length > 1, ":" & String.Join(":", Parts.Skip(1)), ""))
    End Function
    Friend Function VanillaBinding(Key As String) As Boolean
        Return BasicKeys.Contains(CanonicalKey(Key))
    End Function
    Friend Sub CaptureEditedBinding(Instance As McInstance, Key As String, Value As String)
        If Not Enabled(Instance) OrElse Not Selected(Key) Then Return
        SyncLock LocalGameData.Gate
            Dim Profile = LocalGameData.ReadJson(ProfileName), Values = TryCast(Profile("values"), JObject)
            If Values Is Nothing Then Return
            Dim Canonical = CanonicalValue(Key, Value)
            If Canonical Is Nothing Then Return
            Values(CanonicalKey(Key)) = Canonical
            LocalGameData.WriteJson(ProfileName, Profile)
        End SyncLock
    End Sub
    Friend Sub Finish(Session As PersonalOptionsSession)
        If Session Is Nothing Then Return
        SyncLock LocalGameData.Gate
            Dim Journal = LocalGameData.Address("options-sessions/" & Session.Id & ".json")
            If Session.Tracked AndAlso Not File.Exists(Journal) Then Return
            Capture(Session)
            If File.Exists(Journal) Then File.Delete(Journal)
        End SyncLock
    End Sub
    Private Sub Capture(Session As PersonalOptionsSession)
        Dim Instance As New McInstance(Session.VersionPath)
        If Not Enabled(Instance) Then Return
        Dim Bytes = ReadOptions(Path.Combine(Session.GameFolder, "options.txt"))
        If Bytes Is Nothing Then Return
        Dim Current = Parse(Decode(Bytes))
        SyncLock LocalGameData.Gate
            Dim Profile = LocalGameData.ReadJson(ProfileName), Values = TryCast(Profile("values"), JObject)
            If Values Is Nothing Then Return
            For Each Key In Session.Keys.Where(Function(Value) Selected(Value))
                Dim Raw As String = Nothing
                If Not Current.TryGetValue(Key, Raw) Then Continue For
                Dim Value = CanonicalValue(Key, Raw)
                If Value IsNot Nothing AndAlso Session.Baseline(Key) IsNot Nothing AndAlso Value <> Session.Baseline(Key).ToString() Then Values(Key) = Value
            Next
            LocalGameData.WriteJson(ProfileName, Profile)
        End SyncLock
    End Sub
    Friend Sub RecoverExitedSessions()
        Dim Folder = LocalGameData.Address("options-sessions")
        If Not Directory.Exists(Folder) Then Return
        SyncLock LocalGameData.Gate
        For Each FileName In Directory.EnumerateFiles(Folder, "*.json")
            Try
                Dim Json = LocalGameData.ReadJson("options-sessions/" & Path.GetFileName(FileName))
                If LocalGameData.ProcessMatches(Json("pid").ToObject(Of Integer)(), Json("started").ToObject(Of Long)()) Then Continue For
                Dim Session As New PersonalOptionsSession With {.Id = Path.GetFileNameWithoutExtension(FileName), .VersionPath = Json("versionPath").ToString(), .GameFolder = Json("gameFolder").ToString(), .Baseline = DirectCast(Json("baseline"), JObject), .Keys = New HashSet(Of String)(Json("keys").Select(Function(Item) Item.ToString()), StringComparer.Ordinal), .Tracked = True}
                Finish(Session)
            Catch ex As Exception
                Logger.Warn(ex, "恢复个人游戏设置同步会话失败")
            End Try
        Next
        End SyncLock
    End Sub
    Private Function HasActiveSession(GameFolder As String) As Boolean
        Dim Folder = LocalGameData.Address("options-sessions")
        If Not Directory.Exists(Folder) Then Return False
        SyncLock LocalGameData.Gate
            For Each FileName In Directory.EnumerateFiles(Folder, "*.json")
                Dim Json = LocalGameData.ReadJson("options-sessions/" & Path.GetFileName(FileName))
                If PathUtils.IsSame(Json("gameFolder").ToString(), GameFolder) AndAlso LocalGameData.ProcessMatches(Json("pid").ToObject(Of Integer)(), Json("started").ToObject(Of Long)()) Then Return True
            Next
        End SyncLock
        Return False
    End Function
    Private Function CreateLegacyKeys() As Dictionary(Of Integer, String)
        Dim Values As New Dictionary(Of Integer, String) From {{0, "unknown"}, {1, "escape"}, {14, "backspace"}, {15, "tab"}, {28, "enter"}, {29, "left.control"}, {42, "left.shift"}, {54, "right.shift"}, {56, "left.alt"}, {57, "space"}, {58, "caps.lock"}, {69, "num.lock"}, {70, "scroll.lock"}, {87, "f11"}, {88, "f12"}, {156, "keypad.enter"}, {157, "right.control"}, {181, "keypad.divide"}, {183, "print.screen"}, {184, "right.alt"}, {197, "pause"}, {199, "home"}, {200, "up"}, {201, "page.up"}, {203, "left"}, {205, "right"}, {207, "end"}, {208, "down"}, {209, "page.down"}, {210, "insert"}, {211, "delete"}, {219, "left.win"}, {220, "right.win"}, {221, "menu"}, {12, "minus"}, {13, "equal"}, {26, "left.bracket"}, {27, "right.bracket"}, {43, "backslash"}, {39, "semicolon"}, {40, "apostrophe"}, {41, "grave.accent"}, {51, "comma"}, {52, "period"}, {53, "slash"}, {55, "keypad.multiply"}, {74, "keypad.subtract"}, {78, "keypad.add"}, {83, "keypad.decimal"}}
        For Index = 0 To 9
            Values(2 + Index) = If(Index = 9, "0", (Index + 1).ToString())
        Next
        For Each Row In {Tuple.Create(16, "qwertyuiop"), Tuple.Create(30, "asdfghjkl"), Tuple.Create(44, "zxcvbnm")}
            For Index = 0 To Row.Item2.Length - 1
                Values(Row.Item1 + Index) = Row.Item2(Index).ToString()
            Next
        Next
        For Index = 1 To 10
            Values(58 + Index) = "f" & Index
        Next
        For Each Pair In New Dictionary(Of Integer, String) From {{71, "7"}, {72, "8"}, {73, "9"}, {75, "4"}, {76, "5"}, {77, "6"}, {79, "1"}, {80, "2"}, {81, "3"}, {82, "0"}}
            Values(Pair.Key) = "keypad." & Pair.Value
        Next
        Return Values
    End Function
End Module
