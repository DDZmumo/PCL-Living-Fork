Imports System.Security.Cryptography

Partial Public Class Settings

    '已有 PCL 文件夹时复用兼容配置；新目录沿用正常初始化流程。
    Friend Shared Property UseExistingPclConfiguration As Boolean

    '账号数据单独迁移并重新加密，避免把官方密文直接交给当前设备标识解密。
    Friend Shared Function IsPclAccountSetting(Entry As Setting) As Boolean
        If Entry.Source <> Sources.Registry Then Return False
        If Entry.Key = "LoginType" OrElse Entry.Key = "CacheMsV2Migrated" Then Return True
        Return Entry.Encrypted AndAlso (Entry.Key.StartsWith("Login", StringComparison.Ordinal) OrElse
            Entry.Key.StartsWith("CacheMs", StringComparison.Ordinal) OrElse
            Entry.Key.StartsWith("CacheNide", StringComparison.Ordinal) OrElse
            Entry.Key.StartsWith("CacheAuth", StringComparison.Ordinal))
    End Function

    Friend Shared Function GetPclSourceIdentify(HardwareConfig As String, Seed As String) As String
        If String.IsNullOrEmpty(Seed) OrElse Seed.Length < 3 Then Return Nothing
        Dim Hardware = If(HardwareConfig, "Unknown").ToUpperInvariant().Trim("{"c, "}"c)
        Dim Hash = (Hardware & Seed).GetStableHashCode().ToString("X").PadLeft(16, "7"c)
        Return Hash.Substring(4, 4) & "-" & Hash.Substring(12, 4) & "-" & Hash.Substring(0, 4) & "-" & Hash.Substring(8, 4)
    End Function

    Private Shared Function DecodePclAccountValue(Entry As Setting, RawValue As String, DeviceIdentify As String) As String
        Try
            Dim Value = If(Entry.Encrypted, DesDecrypt(RawValue, "PCL" & DeviceIdentify), RawValue)
            If Entry.Encrypted AndAlso DesEncrypt(Value, "PCL" & DeviceIdentify) <> Convert.ToBase64String(Convert.FromBase64String(RawValue)) Then Return Nothing
            Dim CheckedValue = CTypeDynamic(Value, Entry.Type)
            If Entry.Type.IsEnum AndAlso Not [Enum].IsDefined(Entry.Type, CheckedValue) Then Return Nothing
            If Entry.Key = "LoginMsJson" Then
                Dim Accounts = JObject.Parse(Value)
                If Accounts.Properties().Any(Function(Item) Item.Value.Type <> JTokenType.String) Then Return Nothing
            ElseIf Entry.Key.EndsWith("ProfileJson", StringComparison.Ordinal) AndAlso Value <> "" Then
                JObject.Parse(Value)
            End If
            Return Value
        Catch
            '密文、令牌、账号名及解析异常均不得写入日志。
            Return Nothing
        End Try
    End Function

    '账号和设备种子使用官方位置；主题、次数、版本状态仍保持分支隔离。
    Private Shared Function RegistryFolderFor(Entry As Setting) As String
        Return If(IsPclAccountSetting(Entry) OrElse Entry.Key = "Identify", "PCL", RegFolder)
    End Function

    Friend Shared Function PclIdentifyCandidates(Seed As String) As List(Of String)
        Dim Result As New List(Of String)
        Using Hardware = Microsoft.Win32.Registry.LocalMachine.OpenSubKey("SYSTEM\HardwareConfig", False)
            Dim Current = GetPclSourceIdentify(Convert.ToString(Hardware?.GetValue("LastConfig", "Unknown"), Globalization.CultureInfo.InvariantCulture), Seed)
            If Current IsNot Nothing Then Result.Add(Current)
            If Hardware IsNot Nothing Then
                For Each Name In Hardware.GetSubKeyNames()
                    Dim HardwareGuid As Guid
                    If Not Guid.TryParse(Name, HardwareGuid) Then Continue For
                    Dim Historical = GetPclSourceIdentify(Name, Seed)
                    If Historical IsNot Nothing AndAlso Not Result.Contains(Historical) Then Result.Add(Historical)
                Next
            End If
        End Using
        Return Result
    End Function

    Private Shared Function DecodePclAccountWithKeys(Entry As Setting, RawValue As String, Keys As IEnumerable(Of String)) As String
        For Each Key In Keys.Distinct(StringComparer.Ordinal)
            Dim Value = DecodePclAccountValue(Entry, RawValue, Key)
            If Value IsNot Nothing Then Return Value
        Next
        Return Nothing
    End Function

    Private Shared Function AccountGroup(Key As String) As String
        If Key = "CacheMsV2Migrated" Then Return Nothing
        Return {"CacheMsV2", "CacheMs", "CacheNide", "CacheAuth", "LoginNide", "LoginAuth"}.
            FirstOrDefault(Function(Prefix) Key.StartsWith(Prefix, StringComparison.Ordinal))
    End Function

    Friend Class PclAccountMigrationPlan
        Public Values As New Dictionary(Of String, String)(StringComparer.Ordinal)
        Public DeleteLegacy As New List(Of String)
        Public CurrentIdentify As String
    End Class

    ''' <summary>
    ''' 将两边账号统一为官方位置、官方当前设备密钥；旧密钥只用于本次转换。
    ''' </summary>
    Friend Shared Function PlanPclAccountMigration(Official As IDictionary(Of String, String), Legacy As IDictionary(Of String, String),
                                                  OfficialKeys As IEnumerable(Of String), LegacyKeys As IEnumerable(Of String), CurrentIdentify As String) As PclAccountMigrationPlan
        Dim Plan As New PclAccountMigrationPlan With {.CurrentIdentify = CurrentIdentify}
        Dim NativePlain As New Dictionary(Of String, String)(StringComparer.Ordinal)
        Dim LegacyPlain As New Dictionary(Of String, String)(StringComparer.Ordinal)
        For Each Entry In Entries.Values.Where(AddressOf IsPclAccountSetting)
            If Official.ContainsKey(Entry.Key) Then
                Dim Value = DecodePclAccountWithKeys(Entry, Official(Entry.Key), OfficialKeys)
                If Value IsNot Nothing Then NativePlain(Entry.Key) = Value
            End If
            If Legacy.ContainsKey(Entry.Key) Then
                Dim Value = DecodePclAccountWithKeys(Entry, Legacy(Entry.Key), LegacyKeys)
                If Value IsNot Nothing Then LegacyPlain(Entry.Key) = Value
            End If
        Next
        '本地缓存整组转换；某个字段无法验证时保留整组原数据，不拼接不同账号。
        For Each Group In {"CacheMsV2", "CacheMs", "CacheNide", "CacheAuth", "LoginNide", "LoginAuth"}
            Dim GroupKeys = Legacy.Keys.Where(Function(Key) AccountGroup(Key) = Group AndAlso Entries.ContainsKey(Key) AndAlso IsPclAccountSetting(Entries(Key))).ToList()
            If Not GroupKeys.Any() Then Continue For
            If GroupKeys.Any(Function(Key) Not LegacyPlain.ContainsKey(Key)) Then
                For Each Key In GroupKeys
                    LegacyPlain.Remove(Key)
                Next
                Continue For
            End If
            For Each Entry In Entries.Values.Where(Function(Item) IsPclAccountSetting(Item) AndAlso AccountGroup(Item.Key) = Group)
                NativePlain(Entry.Key) = If(LegacyPlain.ContainsKey(Entry.Key), LegacyPlain(Entry.Key), Convert.ToString(Entry.DefaultValue, Globalization.CultureInfo.InvariantCulture))
            Next
        Next
        For Each Item In LegacyPlain.ToList()
            If AccountGroup(Item.Key) IsNot Nothing Then Continue For
            If Official.ContainsKey(Item.Key) AndAlso Not NativePlain.ContainsKey(Item.Key) Then
                LegacyPlain.Remove(Item.Key)
                Continue For
            End If
            Dim Value = Item.Value
            If NativePlain.ContainsKey(Item.Key) AndAlso Item.Key = "LoginMsJson" Then
                Dim Accounts = JObject.Parse(Value)
                For Each Account In JObject.Parse(NativePlain(Item.Key)).Properties()
                    If Accounts.Property(Account.Name, StringComparison.OrdinalIgnoreCase) Is Nothing Then Accounts.Add(Account.Name, Account.Value.DeepClone())
                Next
                Value = Accounts.ToString(Newtonsoft.Json.Formatting.None)
            ElseIf NativePlain.ContainsKey(Item.Key) AndAlso Item.Key = "LoginLegacyName" Then
                Value = String.Join("¨", (Value & "¨" & NativePlain(Item.Key)).Split("¨"c).Where(Function(Name) Name <> "").Distinct(StringComparer.OrdinalIgnoreCase))
            End If
            NativePlain(Item.Key) = Value
        Next
        For Each Item In NativePlain
            Dim Entry = Entries(Item.Key)
            Dim RawValue = If(Entry.Encrypted, DesEncrypt(Item.Value, "PCL" & CurrentIdentify), Item.Value)
            If Not Official.ContainsKey(Item.Key) OrElse Official(Item.Key) <> RawValue Then Plan.Values(Item.Key) = RawValue
        Next
        Plan.DeleteLegacy.AddRange(LegacyPlain.Keys)
        Return Plan
    End Function

    Friend Shared Sub ApplyPclAccountMigration(Plan As PclAccountMigrationPlan, Official As Microsoft.Win32.RegistryKey, Legacy As Microsoft.Win32.RegistryKey)
        '事务期间只在内存中保留回滚内容；成功后不留下旧密文文件或备用格式。
        Dim Original As New Dictionary(Of String, Object)(StringComparer.Ordinal)
        Dim OriginalKinds As New Dictionary(Of String, Microsoft.Win32.RegistryValueKind)(StringComparer.Ordinal)
        Dim Old As New Dictionary(Of String, Object)(StringComparer.Ordinal)
        Dim OldKinds As New Dictionary(Of String, Microsoft.Win32.RegistryValueKind)(StringComparer.Ordinal)
        For Each Key In Plan.Values.Keys
            Original(Key) = Official.GetValue(Key, Nothing)
            If Original(Key) IsNot Nothing Then OriginalKinds(Key) = Official.GetValueKind(Key)
        Next
        For Each Key In Plan.DeleteLegacy
            Old(Key) = Legacy?.GetValue(Key, Nothing)
            If Old(Key) IsNot Nothing Then OldKinds(Key) = Legacy.GetValueKind(Key)
        Next
        Try
            For Each Item In Plan.Values.OrderBy(Function(Value) If(Value.Key = "CacheMsV2Migrated", 1, 0))
                Official.SetValue(Item.Key, Item.Value, Microsoft.Win32.RegistryValueKind.String)
                If Convert.ToString(Official.GetValue(Item.Key), Globalization.CultureInfo.InvariantCulture) <> Item.Value OrElse
                    DecodePclAccountValue(Entries(Item.Key), Item.Value, Plan.CurrentIdentify) Is Nothing Then Throw New InvalidOperationException("账号格式转换校验失败")
            Next
            For Each Key In Plan.DeleteLegacy
                If Legacy IsNot Nothing Then
                    Legacy.DeleteValue(Key, False)
                    If Legacy.GetValue(Key, Nothing) IsNot Nothing Then Throw New InvalidOperationException("旧账号数据清理失败")
                End If
            Next
            For Each Key In Plan.Values.Keys.Concat(Plan.DeleteLegacy).Distinct()
                Entries(Key).ValueCache.Clear()
            Next
        Catch
            For Each Item In Original
                If Item.Value Is Nothing Then
                    Official.DeleteValue(Item.Key, False)
                Else
                    Official.SetValue(Item.Key, Item.Value, OriginalKinds(Item.Key))
                End If
            Next
            If Legacy IsNot Nothing Then
                For Each Item In Old
                    If Item.Value IsNot Nothing Then Legacy.SetValue(Item.Key, Item.Value, OldKinds(Item.Key))
                Next
            End If
            Throw
        End Try
    End Sub

    Friend Shared Sub ImportCompatiblePclAccounts()
        SyncLock SharedAccountSync
            ImportCompatiblePclAccountsCore()
        End SyncLock
    End Sub
    Private Shared Sub ImportCompatiblePclAccountsCore()
        If RegFolder = "PCL" Then Return
        Try
            Dim Native As New Dictionary(Of String, String)(StringComparer.Ordinal)
            Dim Old As New Dictionary(Of String, String)(StringComparer.Ordinal)
            Using Official = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Software\PCL"),
                  Legacy = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\" & RegFolder, True)
                For Each Entry In Entries.Values.Where(Function(Item) IsPclAccountSetting(Item) OrElse Item.Key = "Identify")
                    Dim Value = Official.GetValue(Entry.Key, Nothing)
                    If Value IsNot Nothing Then Native(Entry.Key) = Convert.ToString(Value, Globalization.CultureInfo.InvariantCulture)
                    Value = Legacy?.GetValue(Entry.Key, Nothing)
                    If Value IsNot Nothing Then Old(Entry.Key) = Convert.ToString(Value, Globalization.CultureInfo.InvariantCulture)
                Next
                If Not Native.ContainsKey("Identify") Then Return
                Dim OfficialKeys = PclIdentifyCandidates(Native("Identify"))
                Dim LegacyKeys = If(Old.ContainsKey("Identify"), PclIdentifyCandidates(Old("Identify")), New List(Of String))
                LegacyKeys.AddRange(OfficialKeys)
                LegacyKeys.Add("0000-0000-0000-0000")
                Dim Plan = PlanPclAccountMigration(Native, Old, OfficialKeys, LegacyKeys, Identify)
                ApplyPclAccountMigration(Plan, Official, Legacy)
                If Plan.Values.Any() OrElse Plan.DeleteLegacy.Any() Then Logger.Info($"已转换 {Plan.Values.Count} 项官方格式账号数据，并清理 {Plan.DeleteLegacy.Count} 项旧账号数据。")
            End Using
        Catch
            Logger.Warn("账号格式转换未完成，未通过校验的数据保持原样。")
        End Try
    End Sub

    Friend Shared Function IsCompatiblePclSetting(Entry As Setting) As Boolean
        If Entry.Source <> Sources.Registry OrElse Entry.Encrypted Then Return False
        Return Entry.Key.StartsWith("Launch", StringComparison.Ordinal) OrElse
            Entry.Key.StartsWith("Tool", StringComparison.Ordinal) OrElse
            Entry.Key.StartsWith("Ui", StringComparison.Ordinal) OrElse
            Entry.Key.StartsWith("SystemDebug", StringComparison.Ordinal) OrElse
            Entry.Key = "SystemSystemCache"
    End Function

    ''' <summary>
    ''' 合并文件夹时按路径去重，优先保留 LivingPCL 中的名称和顺序。
    ''' </summary>
    Friend Shared Function MergePclFolders(CurrentValue As String, PclValue As String) As String
        Dim Folders As New List(Of String)
        Dim PathsSeen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each Item In (If(CurrentValue, "") & "|" & If(PclValue, "")).Split("|"c)
            Dim Separator = Item.IndexOf(">"c)
            If Separator <= 0 OrElse Separator = Item.Length - 1 Then Continue For
            Dim FolderPath = Item.Substring(Separator + 1).Replace("/", "\")
            '无效或相对路径不能在不同启动目录间可靠迁移。
            If Not System.Text.RegularExpressions.Regex.IsMatch(FolderPath, "\A(?:[A-Za-z]:\\|\\\\[^\\]+\\[^\\]+(?:\\|$))") Then Continue For
            Try
                FolderPath = System.IO.Path.GetFullPath(FolderPath).TrimEnd("\"c) & "\"
                If PathsSeen.Add(FolderPath) Then Folders.Add(Item.Substring(0, Separator) & ">" & FolderPath)
            Catch ex As ArgumentException
                '仅跳过损坏的列表项，其余目录仍然可用。
            Catch ex As NotSupportedException
            Catch ex As PathTooLongException
            End Try
        Next
        Return String.Join("|", Folders)
    End Function

    Private Shared Function ReadRegistrySetting(Entry As Setting, DefaultValue As String) As String
        Dim CurrentValue As String = RegistryUtils.TryRead($"HKEY_CURRENT_USER\Software\{RegistryFolderFor(Entry)}", Entry.Key, Nothing)
        If Not UseExistingPclConfiguration OrElse RegFolder = "PCL" OrElse Not IsCompatiblePclSetting(Entry) Then Return If(CurrentValue, DefaultValue)
        Try
            If Entry.Key = "LaunchFolders" Then
                Using Local = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Software\" & RegFolder),
                      Source = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\PCL", False)
                    Return ImportFoldersOnce(Local, Convert.ToString(Source?.GetValue(Entry.Key, Nothing), Globalization.CultureInfo.InvariantCulture))
                End Using
            End If
            '普通偏好只读继承；账号另行解密迁移，设备标识、主题授权和更新状态不共享。
            Using Source = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\PCL", False)
                Dim RawValue = Source?.GetValue(Entry.Key, Nothing, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)
                If RawValue Is Nothing Then Return If(CurrentValue, DefaultValue)
                Dim SourceValue = Convert.ToString(RawValue, Globalization.CultureInfo.InvariantCulture)
                '先验证类型，旧版或损坏设置不得覆盖当前有效值。
                Dim CheckedValue = CTypeDynamic(SourceValue, Entry.Type)
                Return If(CurrentValue, SourceValue)
            End Using
        Catch ex As Exception
            Logger.Warn($"跳过不兼容的 PCL 设置：{Entry.Key}")
            Return If(CurrentValue, DefaultValue)
        End Try
    End Function

    Public Shared ReadOnly Entries As Dictionary(Of String, Setting) = (New List(Of Setting) From {
        New Setting("Identify", "", Source:=Sources.Registry),
        New Setting("WindowHeight", 550),
        New Setting("WindowWidth", 900),
        New Setting("AprilYear", 0, Source:=Sources.Registry, Encrypted:=True),
        New Setting("Potatoes", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("HintDownloadThread", False, Source:=Sources.Registry),
        New Setting("HintNotice", 0, Source:=Sources.Registry),
        New Setting("HintDownload", 0, Source:=Sources.Registry),
        New Setting("HintHide", False, Source:=Sources.Registry),
        New Setting("HintHandInstall", False, Source:=Sources.Registry),
        New Setting("HintBuy", False, Source:=Sources.Registry),
        New Setting("HintClearRubbish", 0, Source:=Sources.Registry),
        New Setting("HintUpdateMod", False, Source:=Sources.Registry),
        New Setting("HintCustomCommand", False, Source:=Sources.Registry),
        New Setting("HintCustomWarn", False, Source:=Sources.Registry),
        New Setting("HintMoreAdvancedSetup", False, Source:=Sources.Registry),
        New Setting("HintIndieSetup", False, Source:=Sources.Registry),
        New Setting("HintExportConfig", False, Source:=Sources.Registry),
        New Setting("HintSnapshot", False, Source:=Sources.Registry),
        New Setting("SystemEulaVersion", 0, Source:=Sources.Registry),
        New Setting("SystemCount", 0, Source:=Sources.Registry, Encrypted:=True),
        New Setting("SystemLaunchCount", 0, Source:=Sources.Registry, Encrypted:=True),
        New Setting("SystemLastVersionReg", 0, Source:=Sources.Registry, Encrypted:=True),
        New Setting("SystemHighestSavedBetaVersionReg", 0, Source:=Sources.Registry, Encrypted:=True),
        New Setting("SystemHighestBetaVersionReg", 0, Source:=Sources.Registry, Encrypted:=True),
        New Setting("SystemHighestAlphaVersionReg", 0, Source:=Sources.Registry, Encrypted:=True),
        New Setting("SystemHelpVersion", 0, Source:=Sources.Registry),
        New Setting("SystemDebugMode", False, Source:=Sources.Registry, OnChanged:=Sub() Logger.Instance.MinLevel = If(ModeDebug, LogLevel.Trace, LogLevel.Info)),
        New Setting("SystemDebugAnim", 9, Source:=Sources.Registry),
        New Setting("SystemDebugDelay", False, Source:=Sources.Registry),
        New Setting("SystemDebugSkipCopy", False, Source:=Sources.Registry),
        New Setting("SystemSystemCache", "", Source:=Sources.Registry),
        New Setting("SystemSystemUpdate", 3), '兼容旧配置键；本版本不提供官方更新入口。
        New Setting("SystemSystemActivity", 0),
        New Setting("CacheDrops", "", Source:=Sources.Registry),
        New Setting("CacheConfig", 0, Source:=Sources.Registry),
        New Setting("CacheExportConfig", "", Source:=Sources.Registry),
        New Setting("CacheSavedPageUrl", "", Source:=Sources.Registry),
        New Setting("CacheSavedPageVersion", "", Source:=Sources.Registry),
        New Setting("CacheMsOAuthRefresh", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheMsAccess", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheMsProfileJson", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheMsUuid", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheMsName", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheMsV2Migrated", False, Source:=Sources.Registry),
        New Setting("CacheMsV2OAuthRefresh", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheMsV2Access", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheMsV2ProfileJson", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheMsV2Uuid", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheMsV2Name", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheMsV2Expires", 0L, Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheNideAccess", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheNideClient", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheNideUuid", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheNideName", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheNideUsername", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheNidePass", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheNideServer", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheAuthAccess", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheAuthClient", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheAuthUuid", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheAuthName", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheAuthUsername", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheAuthPass", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheAuthServerServer", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheAuthServerName", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheAuthServerRegister", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("CacheDownloadFolder", "", Source:=Sources.Registry),
        New Setting("CacheJavaListVersion", 0, Source:=Sources.Registry),
        New Setting("LoginRemember", True, Source:=Sources.Registry, Encrypted:=True),
        New Setting("LoginLegacyName", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("LoginMsJson", "{}", Source:=Sources.Registry, Encrypted:=True), '{UserName: OAuthToken, ...}
        New Setting("LoginNideEmail", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("LoginNidePass", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("LoginAuthEmail", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("LoginAuthPass", "", Source:=Sources.Registry, Encrypted:=True),
        New Setting("LoginType", McLoginType.Legacy, Source:=Sources.Registry),
        New Setting("LoginPageType", 0),
        New Setting("LaunchSkinID", "", Source:=Sources.Registry, OnChanged:=Sub() PageLaunchLeft.SkinLegacy.Start()),
        New Setting("LaunchSkinType", 0, Source:=Sources.Registry, OnChanged:=AddressOf PageSetupLaunch.UpdateSkinType),
        New Setting("LaunchSkinSlim", False, Source:=Sources.Registry),
        New Setting("LaunchFolderSelect", ""),
        New Setting("LaunchFolders", "", Source:=Sources.Registry),
        New Setting("LaunchArgumentTitle", ""),
        New Setting("LaunchArgumentInfo", "PCL"),
        New Setting("LaunchArgumentJavaSelect", "", Source:=Sources.Registry),
        New Setting("LaunchArgumentJavaAll", "[]", Source:=Sources.Registry),
        New Setting("LaunchArgumentIndie", 0),
        New Setting("LaunchArgumentIndieV2", 4),
        New Setting("LaunchArgumentVisible", 5, Source:=Sources.Registry),
        New Setting("LaunchArgumentPriority", 1, Source:=Sources.Registry),
        New Setting("LaunchArgumentWindowWidth", 854),
        New Setting("LaunchArgumentWindowHeight", 480),
        New Setting("LaunchArgumentWindowType", 1),
        New Setting("LaunchArgumentRam", False, Source:=Sources.Registry),
        New Setting("LaunchAdvanceJvm", "-XX:-OmitStackTraceInFastThrow -Djdk.lang.Process.allowAmbiguousCommands=True -Dfml.ignoreInvalidMinecraftCertificates=True -Dfml.ignorePatchDiscrepancies=True"),
        New Setting("LaunchAdvanceGame", ""),
        New Setting("LaunchAdvanceRun", ""),
        New Setting("LaunchAdvanceRunWait", True),
        New Setting("LaunchAdvanceDisableJLW", False),
        New Setting("LaunchAdvanceDisableLUA", False),
        New Setting("LaunchAdvanceGraphicCard", True, Source:=Sources.Registry),
        New Setting("LaunchAdvanceGC", 4),
        New Setting("LaunchRamType", 0, OnChanged:=AddressOf PageSetupLaunch.UpdateRamType),
        New Setting("LaunchRamCustom", 15),
        New Setting("LinkLastAutoJoinInviteCode", "", Source:=Sources.Registry),
        New Setting("LinkLatencyMode", 0, Source:=Sources.Registry),
        New Setting("LinkCustomPeer", ""),
        New Setting("LinkEasyTierVersion", -1, Source:=Sources.Registry),
        New Setting("ToolHelpChinese", True, Source:=Sources.Registry),
        New Setting("ToolDownloadThread", 63, Source:=Sources.Registry),
        New Setting("ToolDownloadSpeed", 42, Source:=Sources.Registry, OnChanged:=AddressOf ModNet.UpdateNetTaskSpeedLimitHigh),
        New Setting("ToolDownloadSource", 1, Source:=Sources.Registry),
        New Setting("ToolDownloadVersion", 1, Source:=Sources.Registry),
        New Setting("ToolDownloadTranslate", 0, Source:=Sources.Registry),
        New Setting("ToolDownloadTranslateV2", 1, Source:=Sources.Registry),
        New Setting("ToolDownloadCert", False, Source:=Sources.Registry, OnChanged:=AddressOf ModNet.ShouldValidateSslCertificateOnLogin),
        New Setting("ToolDownloadMod", 2, Source:=Sources.Registry),
        New Setting("ToolModLocalNameStyle", 0, Source:=Sources.Registry),
        New Setting("ToolLauncherUpdateIgnoredTag", "", Source:=Sources.Registry),
        New Setting("UiLauncherTransparent", 600, OnChanged:=Sub(Value As Integer) If FrmMain IsNot Nothing Then FrmMain.Opacity = Value / 1000 + 0.4), '避免与 PCL1 设置冲突（UiLauncherOpacity）
        New Setting("UiLauncherHue", DefaultThemeHue),
        New Setting("UiLauncherSat", DefaultThemeSat),
        New Setting("UiLauncherDelta", DefaultThemeDelta),
        New Setting("UiLauncherLight", DefaultThemeLight),
        New Setting("UiLauncherTheme", 0, OnChanged:=AddressOf ThemeRefresh),
        New Setting("UiLauncherThemeHide", "0|1|2|3|4", Source:=Sources.Registry, Encrypted:=True),
        New Setting("UiLauncherThemeHide2", "0|1|2|3|4", Source:=Sources.Registry, Encrypted:=True),
        New Setting("UiLauncherLogo", False),
        New Setting("UiLauncherEmail", False),
        New Setting("UiBackgroundColorful", True, OnChanged:=Sub() ThemeRefresh()), '不传入参数
        New Setting("UiBackgroundOpacity", DefaultBackgroundOpacity, OnChanged:=AddressOf FormMain.UpdateBackgroundAndTitleBar),
        New Setting("UiBackgroundBlur", 0, OnChanged:=AddressOf FormMain.UpdateBackgroundAndTitleBar),
        New Setting("UiBackgroundSuit", 0, OnChanged:=AddressOf FormMain.UpdateBackgroundAndTitleBar),
        New Setting("UiCustomType", 0, OnChanged:=AddressOf PageSetupUI.OnMainPageTypeChanged),
        New Setting("UiCustomPreset", 0),
        New Setting("UiCustomNet", ""),
        New Setting("UiLogoType", 1, OnChanged:=AddressOf FormMain.UpdateBackgroundAndTitleBar),
        New Setting("UiLogoText", "", OnChanged:=AddressOf FormMain.UpdateBackgroundAndTitleBar),
        New Setting("UiLogoLeft", False, OnChanged:=AddressOf FormMain.UpdateBackgroundAndTitleBar),
        New Setting("UiMusicVolume", 500),
        New Setting("UiMusicStop", False),
        New Setting("UiMusicStart", False),
        New Setting("UiMusicRandom", True),
        New Setting("UiMusicAuto", True),
        New Setting("UiHiddenPageDownload", False, OnChanged:=AddressOf PageSetupUI.HiddenRefresh),
        New Setting("UiHiddenPageLink", False, OnChanged:=AddressOf PageSetupUI.HiddenRefresh),
        New Setting("UiHiddenPageSetup", False, OnChanged:=AddressOf PageSetupUI.HiddenRefresh),
        New Setting("UiHiddenPageOther", False, OnChanged:=AddressOf PageSetupUI.HiddenRefresh),
        New Setting("UiHiddenFunctionSelect", False, OnChanged:=AddressOf PageSetupUI.HiddenRefresh),
        New Setting("UiHiddenFunctionModUpdate", False, OnChanged:=AddressOf PageSetupUI.HiddenRefresh),
        New Setting("UiHiddenFunctionInstanceModify", False, OnChanged:=AddressOf PageSetupUI.HiddenRefresh),
        New Setting("UiHiddenFunctionHidden", False, OnChanged:=AddressOf PageSetupUI.HiddenRefresh),
        New Setting("UiHiddenSetupLaunch", False, OnChanged:=AddressOf PageSetupUI.HiddenRefresh),
        New Setting("UiHiddenSetupUi", False, OnChanged:=AddressOf PageSetupUI.HiddenRefresh),
        New Setting("UiHiddenSetupLink", False, OnChanged:=AddressOf PageSetupUI.HiddenRefresh),
        New Setting("UiHiddenSetupSystem", False, OnChanged:=AddressOf PageSetupUI.HiddenRefresh),
        New Setting("UiHiddenOtherHelp", False, OnChanged:=AddressOf PageSetupUI.HiddenRefresh),
        New Setting("UiHiddenOtherFeedback", False, OnChanged:=AddressOf PageSetupUI.HiddenRefresh),
        New Setting("UiHiddenOtherVote", False, OnChanged:=AddressOf PageSetupUI.HiddenRefresh),
        New Setting("UiHiddenOtherAbout", False, OnChanged:=AddressOf PageSetupUI.HiddenRefresh),
        New Setting("UiHiddenOtherTest", False, OnChanged:=AddressOf PageSetupUI.HiddenRefresh),
        New Setting("VersionAdvanceJvm", "", Source:=Sources.Instance),
        New Setting("VersionAdvanceGame", "", Source:=Sources.Instance),
        New Setting("VersionAdvanceAssets", 0, Source:=Sources.Instance),
        New Setting("VersionAdvanceAssetsV2", False, Source:=Sources.Instance),
        New Setting("VersionAdvanceRun", "", Source:=Sources.Instance),
        New Setting("VersionAdvanceRunWait", True, Source:=Sources.Instance),
        New Setting("VersionAdvanceDisableJLW", False, Source:=Sources.Instance),
        New Setting("VersionAdvanceDisableLUA", False, Source:=Sources.Instance),
        New Setting("VersionAdvanceDisableModUpdate", False, Source:=Sources.Instance),
        New Setting("VersionAdvanceGC", 0, Source:=Sources.Instance),
        New Setting("VersionRamType", 2, Source:=Sources.Instance, OnChanged:=AddressOf PageInstanceSetup.OnVersionRamTypeChanged),
        New Setting("VersionRamCustom", 15, Source:=Sources.Instance),
        New Setting("VersionRamOptimize", 0, Source:=Sources.Instance),
        New Setting("VersionArgumentTitle", "", Source:=Sources.Instance),
        New Setting("VersionArgumentInfo", "", Source:=Sources.Instance),
        New Setting("VersionArgumentIndie", -1, Source:=Sources.Instance),
        New Setting("VersionArgumentIndieV2", False, Source:=Sources.Instance),
        New Setting("VersionArgumentJavaSelect", "使用全局设置", Source:=Sources.Instance),
        New Setting("VersionArgumentJavaV2", 0, Source:=Sources.Instance),
        New Setting("VersionArgumentJavaRange", "", Source:=Sources.Instance),
        New Setting("VersionServerEnter", "", Source:=Sources.Instance),
        New Setting("VersionServerLogin", 0, Source:=Sources.Instance, OnChanged:=AddressOf PageInstanceSetup.OnVersionServerLoginChanged),
        New Setting("VersionServerNide", "", Source:=Sources.Instance),
        New Setting("VersionServerAuthRegister", "", Source:=Sources.Instance),
        New Setting("VersionServerAuthName", "", Source:=Sources.Instance),
        New Setting("VersionServerAuthServer", "", Source:=Sources.Instance)
    }).ToDictionary(Function(e) e.Key)

    Public Enum Sources
        Normal
        Registry
        Instance
    End Enum
    Public Class Setting
        Public Key As String
        Public Encrypted As Boolean
        Public DefaultValue As Object
        Public Source As Sources
        ''' <summary>
        ''' 当设置项的值实际被改变时触发，参数为新值。
        ''' </summary>
        Public OnChanged As Action(Of Object)
        ''' <summary>
        ''' 该设置的值的实际类型。
        ''' </summary>
        Public Type As Type
        Public Sub New(Key As String, Value As Object, Optional Source As Sources = Sources.Normal, Optional Encrypted As Boolean = False, Optional OnChanged As Action(Of Object) = Nothing)
            Try
                Me.Key = Key
                Me.DefaultValue = Value
                Me.Encrypted = Encrypted
                Me.Source = Source
                Me.Type = If(Value, "").GetType
                Me.OnChanged = OnChanged
            Catch ex As Exception
                Logger.Error(ex, "初始化设置项失败") '#5095 的 fallback
            End Try
        End Sub

        ''' <summary>
        ''' 读取过的设置的缓存，若从未读取过则为 Nothing。
        ''' 若为版本独立设置，键为版本路径；否则，键为空字符串。
        ''' </summary>
        Public ValueCache As New ConcurrentDictionary(Of String, Object)
        Public Function GetCache(Instance As McInstance)
            Dim Key As String = If(Source = Sources.Instance, Instance.PathVersion, "")
            Dim Result = Nothing
            Return If(ValueCache.TryGetValue(Key, Result), Result, Nothing)
        End Function
        Public Sub [Set](Value As Object, Instance As McInstance)
            Dim Key As String = If(Source = Sources.Instance, Instance.PathVersion, "")
            ValueCache(Key) = Value
        End Sub

        ''' <summary>
        ''' 立即将当前的 Value 写入对应的注册表或文件。
        ''' </summary>
        Public Sub Save(Optional Instance As McInstance = Nothing)
            Dim Value As String = GetCache(Instance)
            Logger.Trace($"保存设置：{Key} {If(Encrypted OrElse Key = "Identify", "", "= " & Value)}{If(Instance Is Nothing, "", $"（实例：{Instance?.PathVersion}）")}")
            If Encrypted Then
                Try
                    If Value Is Nothing Then Value = ""
                    Value = DesEncrypt(Value, "PCL" & Identify)
                Catch ex As Exception
                    Logger.Warn(ex, $"加密设置失败：{Key}")
                End Try
            End If
            Select Case Source
                Case Sources.Normal
                    WriteIni("Setup", Key, Value)
                Case Sources.Registry
                    Try
                        RegistryUtils.Write($"HKEY_CURRENT_USER\Software\{RegistryFolderFor(Me)}", Key, Value)
                        If Key = "LaunchFolders" Then RegistryUtils.Write($"HKEY_CURRENT_USER\Software\{RegFolder}", FolderImportMarker, Boolean.TrueString)
                    Catch ex As Exception
                        Logger.Warn(ex, $"写入注册表失败（{Key} → {Value}）", LogBehavior.Toast)
                    End Try
                Case Sources.Instance
                    If Instance Is Nothing Then Throw New Exception($"保存版本独立设置 {Key} 时未提供目标版本")
                    WriteIni(Instance.PathVersion & "PCL\Setup.ini", Key, Value)
            End Select
        End Sub

    End Class

    ''' <summary>
    ''' 改变某个设置项的值。
    ''' </summary>
    Public Shared Sub [Set](Key As String, Value As Object, Optional Instance As McInstance = Nothing)
        If Entries.ContainsKey(Key) AndAlso IsPclAccountSetting(Entries(Key)) Then
            Try
                WriteAccountValues(New Dictionary(Of String, Object) From {{Key, Value}})
            Catch
                Logger.Error("保存账号设置失败，原有数据已保留。", LogBehavior.Toast)
            End Try
            Return
        End If
        SetCore(Key, Value, Instance)
    End Sub
    Private Shared Sub SetCore(Key As String, Value As Object, Instance As McInstance)
        Dim Entry As Setting = Nothing
        If Not Entries.TryGetValue(Key, Entry) Then Throw New KeyNotFoundException("未找到设置项：" & Key)
        Try
            '判断是否一致
            Value = CTypeDynamic(Value, Entry.Type)
            If Entry.GetCache(Instance) Is Nothing Then [Get](Key, Instance)
            If Entry.GetCache(Instance) = Value Then Return
            '设置新值
            Entry.Set(Value, Instance)
            Entry.Save(Instance)
            '触发改变事件（必须在保存之后，以保证 VersionServerLogin 之类的在事件中读取到的是最新的值）
            If Entry.OnChanged IsNot Nothing Then Entry.OnChanged.Invoke(Value)
        Catch ex As Exception
            Logger.Error(ex, $"设置设置项时出错（{Key}）")
        End Try
    End Sub
    ''' <summary>
    ''' 写入某个未经加密的设置项。
    ''' 若该设置项经过了加密，则会抛出异常。
    ''' </summary>
    Public Shared Sub SetSafe(Key As String, Value As Object, Optional Instance As McInstance = Nothing)
        Dim Entry As Setting = Nothing
        If Not Entries.TryGetValue(Key, Entry) Then Throw New KeyNotFoundException("未找到设置项：" & Key)
        If Entry.Encrypted Then Throw New InvalidOperationException("禁止写入加密设置项：" & Key)
        [Set](Key, Value, Instance)
    End Sub

    ''' <summary>
    ''' 获取某个设置项的值。
    ''' </summary>
    Public Shared Function [Get](Key As String, Optional Instance As McInstance = Nothing)
        If Entries.ContainsKey(Key) AndAlso IsPclAccountSetting(Entries(Key)) Then
            SyncLock SharedAccountSync
                Return GetCore(Key, Instance)
            End SyncLock
        End If
        Return GetCore(Key, Instance)
    End Function
    Private Shared Function GetCore(Key As String, Instance As McInstance, Optional ReadRaw As Func(Of Setting, String) = Nothing, Optional DeviceIdentify As String = Nothing) As Object
        Dim Entry As Setting = Nothing
        If Not Entries.TryGetValue(Key, Entry) Then Throw New KeyNotFoundException("未找到设置项：" & Key)
        Dim Value = Entry.GetCache(Instance)
        If Value IsNot Nothing AndAlso Not IsPclAccountSetting(Entry) Then Return Value
        '对部分设置强制赋值
        Static ForcedSettings As New Dictionary(Of String, Object) From {
            {"UiHiddenPageLink", True},
            {"UiHiddenSetupLink", True}
        }
        Dim ForcedSetting As Object = Nothing
        If ForcedSettings.TryGetValue(Key, ForcedSetting) Then
            Entry.Set(ForcedSetting, Instance)
            Return ForcedSetting
        End If
        '正常读取
        Try
            Dim GotValue As String = Nothing '先用 String 储存，避免类型转换
            Dim EncryptionIdentify = If(DeviceIdentify, Identify)
            Dim DefaultValue As String = If(Entry.Encrypted, DesEncrypt(Entry.DefaultValue, "PCL" & EncryptionIdentify), Entry.DefaultValue)
            If ReadRaw IsNot Nothing Then
                GotValue = If(ReadRaw(Entry), DefaultValue)
            Else
                Select Case Entry.Source
                    Case Sources.Normal
                        GotValue = ReadIni("Setup", Key, DefaultValue)
                    Case Sources.Registry
                        GotValue = ReadRegistrySetting(Entry, DefaultValue)
                    Case Sources.Instance
                        If Instance Is Nothing Then
                            Throw New Exception($"读取版本设置 {Key} 时未提供目标版本")
                        Else
                            GotValue = ReadIni(Instance.PathVersion & "PCL\Setup.ini", Key, DefaultValue)
                        End If
                End Select
            End If
            If Entry.Encrypted Then
                If GotValue = DefaultValue Then
                    GotValue = Entry.DefaultValue
                Else
                    Try
                        GotValue = DesDecrypt(GotValue, "PCL" & EncryptionIdentify)
                    Catch
                        Logger.Warn($"解密设置失败，保留原始密文：{Key}")
                        GotValue = Entry.DefaultValue
                    End Try
                End If
            End If
            Entry.Set(CTypeDynamic(GotValue, Entry.Type), Instance)
        Catch ex As Exception
            Logger.Error(ex, $"读取设置失败：{Key}", LogBehavior.Toast)
            Entry.Set(CTypeDynamic(Entry.DefaultValue, Entry.Type), Instance)
        End Try
        Return Entry.GetCache(Instance)
    End Function
    Public Shared Function [Get](Of T)(Key As String, Optional Instance As McInstance = Nothing) As T
        Return [Get](Key, Instance)
    End Function
    ''' <summary>
    ''' 获取某个未经加密的设置项的值。
    ''' 若该设置项经过了加密，则会抛出异常。
    ''' </summary>
    Public Shared Function GetSafe(Key As String, Optional Instance As McInstance = Nothing)
        Dim Entry As Setting = Nothing
        If Not Entries.TryGetValue(Key, Entry) Then Throw New KeyNotFoundException("未找到设置项：" & Key)
        If Entry.Encrypted Then Throw New InvalidOperationException("禁止读取加密设置项：" & Key)
        Return [Get](Key, Instance)
    End Function

    ''' <summary>
    ''' 初始化某个设置项的值。
    ''' </summary>
    Public Shared Sub Reset(Key As String, Optional Instance As McInstance = Nothing)
        If Entries.ContainsKey(Key) AndAlso IsPclAccountSetting(Entries(Key)) Then
            WriteAccountValues(New Dictionary(Of String, Object) From {{Key, Entries(Key).DefaultValue}})
            Return
        End If
        ResetCore(Key, Instance)
    End Sub
    Private Shared Sub ResetCore(Key As String, Instance As McInstance)
        Dim Entry As Setting = Nothing
        If Not Entries.TryGetValue(Key, Entry) Then Throw New KeyNotFoundException("未找到设置项：" & Key)
        Try
            '判断是否一致
            If Entry.GetCache(Instance) Is Nothing Then [Get](Key, Instance)
            If Entry.GetCache(Instance) = Entry.DefaultValue AndAlso Not (Entry.Source = Sources.Registry AndAlso IsCompatiblePclSetting(Entry)) Then Return
            '设置新值
            Entry.Set(Entry.DefaultValue, Instance)
            Select Case Entry.Source
                Case Sources.Normal
                    DeleteIniKey("Setup", Key)
                Case Sources.Registry
                    Using Store = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Software\" & RegistryFolderFor(Entry))
                        ResetRegistryPreference(Store, Entry)
                    End Using
                Case Sources.Instance
                    If Instance Is Nothing Then Throw New Exception($"重置版本设置 {Key} 时未提供目标版本")
                    DeleteIniKey(Instance.PathVersion & "PCL\Setup.ini", Key)
            End Select
            '触发改变事件
            If Entry.OnChanged IsNot Nothing Then Entry.OnChanged.Invoke(Entry.DefaultValue)
        Catch ex As Exception
            Entry.ValueCache.Clear()
            Logger.Error(ex, $"重置设置项时出错（{Key}）")
        End Try
    End Sub

    ''' <summary>
    ''' 获取某个设置项的默认值。
    ''' </summary>
    Public Shared Function GetDefault(Key As String) As String
        Return Entries(Key).DefaultValue
    End Function

    ''' <summary>
    ''' 对应的注册表或文件是否已经储存了某个设置项。
    ''' 若改成了和默认值一样的，在 2.12.3- 会储存更改，在 2.12.4+ 不会储存更改。
    ''' </summary>
    Public Shared Function HasSaved(Key As String, Optional Instance As McInstance = Nothing) As Boolean
        Select Case Entries(Key).Source
            Case Sources.Normal
                Return HasIniKey("Setup", Key)
            Case Sources.Registry
                Try
                    If RegistryUtils.Has($"HKEY_CURRENT_USER\Software\{RegistryFolderFor(Entries(Key))}", Key) Then Return True
                    Return ReadRegistrySetting(Entries(Key), Nothing) IsNot Nothing
                Catch ex As Exception
                    Logger.Warn(ex, $"判断注册表 {Key} 是否存在时出错")
                    Return False
                End Try
            Case Else 'Source.Instance
                If Instance Is Nothing Then Throw New Exception($"判断版本设置 {Key} 是否存在时未提供目标版本")
                Return HasIniKey(Instance.PathVersion & "PCL\Setup.ini", Key)
        End Select
    End Function

#Region "DES 加密"

    Private Shared ReadOnly desInitialVector As Byte() = Encoding.UTF8.GetBytes("95168702")

    ''' <summary>
    ''' 使用 DES 对称加密算法加密字符串。
    ''' </summary>
    Private Shared Function DesEncrypt(sourceString As String, Optional key As String = Nothing) As String
        key = If(key Is Nothing, "@;$ Abv2", key.GetStableHashCode().ToString().EnsureLength("X"c, 8).Substring(0, 8))
        Dim btKey As Byte() = Encoding.UTF8.GetBytes(key)
        Using des As New DESCryptoServiceProvider()
            Using ms As New MemoryStream()
                Using cs As New CryptoStream(ms, des.CreateEncryptor(btKey, desInitialVector), CryptoStreamMode.Write)
                    Dim inData As Byte() = Encoding.UTF8.GetBytes(sourceString)
                    cs.Write(inData, 0, inData.Length)
                    cs.FlushFinalBlock()
                    Return Convert.ToBase64String(ms.ToArray())
                End Using
            End Using
        End Using
    End Function

    ''' <summary>
    ''' 使用 DES 对称加密算法解密字符串。
    ''' </summary>
    Private Shared Function DesDecrypt(encryptedString As String, Optional key As String = Nothing) As String
        key = If(key Is Nothing, "@;$ Abv2", key.GetStableHashCode().ToString().EnsureLength("X"c, 8).Substring(0, 8))
        Dim btKey As Byte() = Encoding.UTF8.GetBytes(key)
        Using des As New DESCryptoServiceProvider()
            Using ms As New MemoryStream()
                Using cs As New CryptoStream(ms, des.CreateDecryptor(btKey, desInitialVector), CryptoStreamMode.Write)
                    Dim inData As Byte() = Convert.FromBase64String(encryptedString)
                    cs.Write(inData, 0, inData.Length)
                    cs.FlushFinalBlock()
                    Return Encoding.UTF8.GetString(ms.ToArray())
                End Using
            End Using
        End Using
    End Function

#End Region

End Class
