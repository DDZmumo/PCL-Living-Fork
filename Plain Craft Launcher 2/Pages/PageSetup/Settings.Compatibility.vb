Partial Public Class Settings
    Private Const SharedPreferencesMarker As String = "LivingPclSharedPreferencesMigrated"

    Private Shared Function NormalizeFolderList(Value As String) As String
        Try
            Dim Result As New List(Of String)
            Dim Seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each Item In If(Value, "").Split("|"c)
                If Item = "" Then Continue For
                Dim Separator = Item.IndexOf(">"c)
                If Separator <= 0 OrElse Separator = Item.Length - 1 Then Return Nothing
                Dim Location = Item.Substring(Separator + 1).Replace("/", "\")
                If Not System.Text.RegularExpressions.Regex.IsMatch(Location, "\A(?:[A-Za-z]:\\|\\\\[^\\]+\\[^\\]+(?:\\|$))") Then Return Nothing
                Location = System.IO.Path.GetFullPath(Location).TrimEnd("\"c) & "\"
                If Seen.Add(Location) Then Result.Add(Item.Substring(0, Separator) & ">" & Location)
            Next
            Return String.Join("|", Result)
        Catch
            Return Nothing
        End Try
    End Function

    '只接受官方同名设置能够消费的值，不把可转换但越界的旧数据写进共享存储。
    Friend Shared Function NormalizeSharedPclPreference(Entry As Setting, Value As String) As String
        Try
            If Entry.Key = "LaunchFolders" Then Return NormalizeFolderList(Value)
            Dim CheckedValue = CTypeDynamic(Value, Entry.Type)
            If Entry.Type = GetType(Integer) Then
                Dim Number As Integer
                If Not Integer.TryParse(Value, Globalization.NumberStyles.Integer, Globalization.CultureInfo.InvariantCulture, Number) Then Return Nothing
                Dim Maximum As Integer
                Select Case Entry.Key
                    Case "ToolDownloadThread" : Maximum = 255
                    Case "ToolDownloadSpeed" : Maximum = 42
                    Case "SystemDebugAnim" : Maximum = 30
                    Case "LaunchSkinType", "ToolDownloadTranslateV2" : Maximum = 4
                    Case "LaunchArgumentVisible" : Maximum = 5
                    Case "ToolModLocalNameStyle" : Maximum = 1
                    Case "LaunchArgumentPriority", "ToolDownloadSource", "ToolDownloadVersion", "ToolDownloadMod", "ToolDownloadTranslate" : Maximum = 2
                    Case Else : Return Nothing
                End Select
                If Number < 0 OrElse Number > Maximum Then Return Nothing
            ElseIf Entry.Key = "LaunchArgumentJavaAll" Then
                For Each Item In JArray.Parse(Value)
                    If Item.Type <> JTokenType.Object OrElse Item("Path")?.Type <> JTokenType.String OrElse String.IsNullOrWhiteSpace(CStr(Item("Path"))) Then Return Nothing
                Next
            ElseIf Entry.Key = "LaunchArgumentJavaSelect" AndAlso Value <> "" Then
                Dim Item = JObject.Parse(Value)
                If Item("Path")?.Type <> JTokenType.String OrElse String.IsNullOrWhiteSpace(CStr(Item("Path"))) Then Return Nothing
            End If
            Return Convert.ToString(CheckedValue, Globalization.CultureInfo.InvariantCulture)
        Catch
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' 将旧 LivingPCL 注册表中的兼容偏好迁移到官方 PCL 注册表。
    ''' 官方已有值（包括已清空的目录列表）始终优先。旧目录可能只是自动导入的历史快照，
    ''' 不能据此覆盖官方后来添加、改名或删除的目录。仅补全缺失键，保留旧设置供核对。
    ''' 只在首次迁移时执行；失败时恢复本次写入，旧设置始终保留。
    ''' </summary>
    Friend Shared Sub MigrateSharedPclPreferences(OldStore As Microsoft.Win32.RegistryKey, PclStore As Microsoft.Win32.RegistryKey)
        If OldStore Is Nothing OrElse PclStore Is Nothing Then Return
        If String.Equals(Convert.ToString(OldStore.GetValue(SharedPreferencesMarker, "False")), Boolean.TrueString, StringComparison.OrdinalIgnoreCase) Then Return

        Dim Changes As New List(Of Tuple(Of String, Object, Microsoft.Win32.RegistryValueKind))
        Try
            For Each Entry In Entries.Values.Where(Function(Item) IsCompatiblePclSetting(Item))
                Dim Existing = PclStore.GetValue(Entry.Key, Nothing, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)
                If Existing IsNot Nothing Then Continue For
                Dim Raw = OldStore.GetValue(Entry.Key, Nothing, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)
                If Raw Is Nothing OrElse Not TypeOf Raw Is String Then Continue For
                Dim Value = NormalizeSharedPclPreference(Entry, CStr(Raw))
                If Value Is Nothing Then Continue For
                Dim ExistingKind = If(Existing Is Nothing, Microsoft.Win32.RegistryValueKind.String, PclStore.GetValueKind(Entry.Key))
                Changes.Add(Tuple.Create(Entry.Key, Existing, ExistingKind))
                PclStore.SetValue(Entry.Key, Value, Microsoft.Win32.RegistryValueKind.String)
                If Not Object.Equals(PclStore.GetValue(Entry.Key, Nothing, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames), Value) Then Throw New IOException("共享偏好保存校验失败")
            Next
            OldStore.SetValue(SharedPreferencesMarker, Boolean.TrueString, Microsoft.Win32.RegistryValueKind.String)
            If Not Object.Equals(OldStore.GetValue(SharedPreferencesMarker, Nothing), Boolean.TrueString) Then Throw New IOException("共享偏好迁移标记校验失败")
        Catch
            For Index As Integer = Changes.Count - 1 To 0 Step -1
                Dim Change = Changes(Index)
                Try
                    If Change.Item2 Is Nothing Then
                        PclStore.DeleteValue(Change.Item1, False)
                    Else
                        PclStore.SetValue(Change.Item1, Change.Item2, Change.Item3)
                    End If
                Catch
                    '尽力回滚。旧版注册表保留以便用户手动恢复。
                End Try
            Next
            Throw
        End Try
    End Sub

    Friend Shared Sub ImportLegacyPclPreferences()
        If RegFolder = "PCL" Then Return
        Try
            Using OldStore = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\" & RegFolder, True),
                  PclStore = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Software\PCL")
                MigrateSharedPclPreferences(OldStore, PclStore)
            End Using
        Catch
            '不因偏好迁移失败而阻止启动，也不在日志中输出配置内容。
            '旧版数据保持原样，下次启动会重试。
        End Try
    End Sub

    Friend Shared Sub ResetRegistryPreference(Store As Microsoft.Win32.RegistryKey, Entry As Setting)
        If Not IsCompatiblePclSetting(Entry) Then
            Store.DeleteValue(Entry.Key, False)
            Return
        End If
        Dim Value = Convert.ToString(Entry.DefaultValue, Globalization.CultureInfo.InvariantCulture)
        Store.SetValue(Entry.Key, Value, Microsoft.Win32.RegistryValueKind.String)
        If Convert.ToString(Store.GetValue(Entry.Key, Nothing)) <> Value Then Throw New IOException("默认设置保存校验失败")
    End Sub
End Class
