Partial Public Class Settings
    Private Shared ReadOnly SharedAccountSync As New Object

    Friend Shared Function PlanLegacyMicrosoftUpgrade(Raw As IDictionary(Of String, String), DeviceIdentify As String) As Dictionary(Of String, String)
        Dim Plan As New Dictionary(Of String, String)(StringComparer.Ordinal)
        Const Marker As String = "CacheMsV2Migrated"
        If Raw.ContainsKey(Marker) Then
            Dim Value = DecodePclAccountValue(Entries(Marker), Raw(Marker), DeviceIdentify)
            If Value Is Nothing OrElse CBool(Value) Then Return Plan
        End If
        Dim Fields = {"OAuthRefresh", "Access", "ProfileJson", "Uuid", "Name"}
        '退出登录也会明确保存空值。存在任何 V2 原始键时，不得用旧缓存复活或覆盖它。
        If Fields.Concat({"Expires"}).Any(Function(Name) Raw.ContainsKey("CacheMsV2" & Name)) Then
            Plan(Marker) = Boolean.TrueString
            Return Plan
        End If
        Dim Legacy As New Dictionary(Of String, String)(StringComparer.Ordinal)
        For Each Field In Fields
            Dim Key = "CacheMs" & Field
            Dim Value = ""
            If Raw.ContainsKey(Key) Then
                Value = DecodePclAccountValue(Entries(Key), Raw(Key), DeviceIdentify)
                If Value Is Nothing Then Return Plan
            End If
            Legacy(Field) = Value
        Next
        If String.IsNullOrWhiteSpace(Legacy("OAuthRefresh")) Then Return Plan
        For Each Field In Fields
            Plan("CacheMsV2" & Field) = DesEncrypt(Legacy(Field), "PCL" & DeviceIdentify)
        Next
        Plan("CacheMsV2Expires") = DesEncrypt("0", "PCL" & DeviceIdentify)
        Plan(Marker) = Boolean.TrueString
        Return Plan
    End Function

    Friend Shared Sub EnsureMicrosoftCacheUpgrade()
        SyncLock SharedAccountSync
            Using Store = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Software\PCL")
                Dim Raw As New Dictionary(Of String, String)(StringComparer.Ordinal)
                For Each Key In Entries.Keys.Where(Function(Name) Name.StartsWith("CacheMs", StringComparison.Ordinal))
                    Dim Value = Store.GetValue(Key, Nothing)
                    If Value IsNot Nothing Then Raw(Key) = Convert.ToString(Value, Globalization.CultureInfo.InvariantCulture)
                Next
                Dim Plan As New PclAccountMigrationPlan With {.CurrentIdentify = Identify, .Values = PlanLegacyMicrosoftUpgrade(Raw, Identify)}
                If Plan.Values.Any() Then ApplyPclAccountMigration(Plan, Store, Nothing)
            End Using
        End SyncLock
    End Sub

    Friend Shared Function AccountSnapshot(Prefix As String) As Dictionary(Of String, Object)
        SyncLock SharedAccountSync
            Return Entries.Values.Where(Function(Item) IsPclAccountSetting(Item) AndAlso Item.Key.StartsWith(Prefix, StringComparison.Ordinal)).
                ToDictionary(Function(Item) Item.Key, Function(Item) GetCore(Item.Key, Nothing), StringComparer.Ordinal)
        End SyncLock
    End Function

    Private Shared Sub WriteAccountValues(Values As IDictionary(Of String, Object))
        SyncLock SharedAccountSync
            Dim Plan As New PclAccountMigrationPlan With {.CurrentIdentify = Identify}
            For Each Item In Values
                Dim Entry As Setting = Nothing
                If Not Entries.TryGetValue(Item.Key, Entry) OrElse Not IsPclAccountSetting(Entry) Then Throw New ArgumentException("不支持的账号设置项")
                Dim Typed = CTypeDynamic(Item.Value, Entry.Type)
                Dim Plain = If(Entry.Type.IsEnum, Convert.ToInt32(Typed, Globalization.CultureInfo.InvariantCulture).ToString(Globalization.CultureInfo.InvariantCulture), Convert.ToString(Typed, Globalization.CultureInfo.InvariantCulture))
                Dim Raw = If(Entry.Encrypted, DesEncrypt(Plain, "PCL" & Identify), Plain)
                If DecodePclAccountValue(Entry, Raw, Identify) Is Nothing Then Throw New ArgumentException("账号设置格式不正确")
                Plan.Values(Item.Key) = Raw
            Next
            Using Store = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Software\PCL")
                ApplyPclAccountMigration(Plan, Store, Nothing)
            End Using
        End SyncLock
    End Sub

    Public Shared Function UpdateMicrosoftAccounts(Change As Action(Of JObject)) As JObject
        SyncLock SharedAccountSync
            Dim Accounts = JObject.Parse(CStr(GetCore("LoginMsJson", Nothing)))
            Change(Accounts)
            WriteAccountValues(New Dictionary(Of String, Object) From {{"LoginMsJson", Accounts.ToString(Newtonsoft.Json.Formatting.None)}})
            Return Accounts
        End SyncLock
    End Function

    Public Shared Sub SaveMicrosoftSession(Values As IDictionary(Of String, Object), PreviousName As String, Optional Canceled As Func(Of Boolean) = Nothing)
        SyncLock SharedAccountSync
            If Canceled IsNot Nothing AndAlso Canceled() Then Throw New OperationCanceledException
            Dim Accounts = JObject.Parse(CStr(GetCore("LoginMsJson", Nothing)))
            Dim Name = CStr(Values("CacheMsV2Name"))
            For Each AccountProperty In Accounts.Properties().Where(Function(Item) Item.Name.Equals(PreviousName, StringComparison.OrdinalIgnoreCase) OrElse Item.Name.Equals(Name, StringComparison.OrdinalIgnoreCase)).ToList()
                AccountProperty.Remove()
            Next
            Accounts(Name) = CStr(Values("CacheMsV2OAuthRefresh"))
            Dim Batch = New Dictionary(Of String, Object)(Values, StringComparer.Ordinal)
            Batch("LoginMsJson") = Accounts.ToString(Newtonsoft.Json.Formatting.None)
            Batch("CacheMsV2Migrated") = True
            WriteAccountValues(Batch)
        End SyncLock
    End Sub

    Public Shared Sub SaveAccountGroup(Values As IDictionary(Of String, Object), Optional Canceled As Func(Of Boolean) = Nothing)
        SyncLock SharedAccountSync
            If Canceled IsNot Nothing AndAlso Canceled() Then Throw New OperationCanceledException
            WriteAccountValues(Values)
        End SyncLock
    End Sub

    Public Shared Function UpdateLegacyNames(Change As Action(Of List(Of String))) As List(Of String)
        SyncLock SharedAccountSync
            Dim Names = CStr(GetCore("LoginLegacyName", Nothing)).Split("¨"c).Where(Function(Name) Name <> "").ToList()
            Change(Names)
            Names = Names.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            WriteAccountValues(New Dictionary(Of String, Object) From {{"LoginLegacyName", String.Join("¨", Names)}})
            Return Names
        End SyncLock
    End Function

    Public Shared Function UpdateServerAccounts(Token As String, Name As String, Password As String, Remove As Boolean) As Dictionary(Of String, String)
        If Token <> "Auth" AndAlso Token <> "Nide" Then Throw New ArgumentException("未知认证类型")
        SyncLock SharedAccountSync
            Dim Emails = CStr(GetCore("Login" & Token & "Email", Nothing))
            Dim Passwords = CStr(GetCore("Login" & Token & "Pass", Nothing))
            Dim Names = If(Emails = "", New String() {}, Emails.Split("¨"c))
            Dim Passes = If(Passwords = "", New String() {}, Passwords.Split("¨"c))
            If Names.Length <> Passes.Length Then Throw New InvalidOperationException("已有账号记录格式异常，未修改原数据。")
            Dim Result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            If Not Remove Then
                If Name.Contains("¨") OrElse Password.Contains("¨") Then Throw New ArgumentException("原版账号记录不支持分隔字符 ¨。")
                Result(Name) = Password
            End If
            For i = 0 To Names.Length - 1
                If Not Names(i).Equals(Name, StringComparison.OrdinalIgnoreCase) Then Result(Names(i)) = Passes(i)
            Next
            WriteAccountValues(New Dictionary(Of String, Object) From {
                {"Login" & Token & "Email", String.Join("¨", Result.Keys)}, {"Login" & Token & "Pass", String.Join("¨", Result.Values)}})
            Return Result
        End SyncLock
    End Function
End Class
