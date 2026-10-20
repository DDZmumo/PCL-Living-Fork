Partial Public Class Settings
    Private Const SharedPreferencesMarker As String = "LivingPclSharedPreferencesMigrated"

    Private Shared Function ValidFolderList(Value As String) As Boolean
        Try
            For Each Item In If(Value, "").Split("|"c)
                If Item = "" Then Continue For
                Dim Separator = Item.IndexOf(">"c)
                If Separator <= 0 OrElse Separator = Item.Length - 1 Then Return False
                Dim Location = Item.Substring(Separator + 1).Replace("/", "\")
                If Not System.Text.RegularExpressions.Regex.IsMatch(Location, "\A(?:[A-Za-z]:\\|\\\\[^\\]+\\[^\\]+(?:\\|$))") Then Return False
                System.IO.Path.GetFullPath(Location)
            Next
            Return True
        Catch
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 将旧 LivingPCL 注册表中的兼容偏好迁移到官方 PCL 注册表。
    ''' 避免覆盖官方已有的通用偏好；游戏目录例外：旧 LivingPCL 中的列表（包括空列表）
    ''' 是用户实际确认过的状态，必须优先保留，不能重新合并已删除的目录。
    ''' 只在首次迁移时执行；失败时恢复本次写入，旧设置始终保留。
    ''' </summary>
    Friend Shared Sub MigrateSharedPclPreferences(OldStore As Microsoft.Win32.RegistryKey, PclStore As Microsoft.Win32.RegistryKey)
        If OldStore Is Nothing OrElse PclStore Is Nothing Then Return
        If String.Equals(Convert.ToString(OldStore.GetValue(SharedPreferencesMarker, "False")), Boolean.TrueString, StringComparison.OrdinalIgnoreCase) Then Return

        Dim Changes As New List(Of Tuple(Of String, Object, Microsoft.Win32.RegistryValueKind))
        Try
            For Each Entry In Entries.Values.Where(Function(Item) IsCompatiblePclSetting(Item))
                Dim Raw = OldStore.GetValue(Entry.Key, Nothing, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)
                If Raw Is Nothing OrElse Not TypeOf Raw Is String Then Continue For
                Dim Value = CStr(Raw)
                Try
                    If Entry.Key = "LaunchFolders" Then
                        If Not ValidFolderList(Value) Then Continue For
                    Else
                        CTypeDynamic(Value, Entry.Type)
                    End If
                Catch
                    Continue For '旧版或损坏的值不能写回官方设置
                End Try

                Dim Existing = PclStore.GetValue(Entry.Key, Nothing, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)
                If Existing IsNot Nothing AndAlso Entry.Key <> "LaunchFolders" Then Continue For
                If Object.Equals(Existing, Value) Then Continue For
                Dim ExistingKind = If(Existing Is Nothing, Microsoft.Win32.RegistryValueKind.String, PclStore.GetValueKind(Entry.Key))
                Changes.Add(Tuple.Create(Entry.Key, Existing, ExistingKind))
                PclStore.SetValue(Entry.Key, Value, Microsoft.Win32.RegistryValueKind.String)
            Next
            OldStore.SetValue(SharedPreferencesMarker, Boolean.TrueString, Microsoft.Win32.RegistryValueKind.String)
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
