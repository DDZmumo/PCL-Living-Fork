Public Class PageInstanceClone
    Implements IRefreshable
    Private Owner As McInstance

    Private Sub PageLoaded() Handles Me.Loaded
        PanBack.ScrollToHome()
        AddHandler InstanceClonerUi.Changed, AddressOf Refresh
        Refresh()
    End Sub
    Private Sub PageUnloaded() Handles Me.Unloaded
        RemoveHandler InstanceClonerUi.Changed, AddressOf Refresh
    End Sub
    Public Sub Refresh() Implements IRefreshable.Refresh
        Dim Previous = Owner
        Owner = PageInstanceLeft.Instance
        If Owner Is Nothing Then Return
        LabSource.Text = "当前版本：" & Owner.Name
        TextName.ValidateRules.Clear()
        TextName.ValidateRules.Add(New ValidateFolderName(Path.GetDirectoryName(Owner.PathVersion.TrimEnd("\"c))))
        If Previous IsNot Owner OrElse String.IsNullOrWhiteSpace(TextName.Text) Then
            TextName.Text = Owner.Name.Substring(0, Math.Min(Owner.Name.Length, 85)) & " - 测试"
        End If
        CardOrigin.Visibility = Visibility.Collapsed
        Try
            Dim Source = InstanceCloner.SourceVersionFolder(Owner.PathVersion)
            LabOrigin.Text = "来源版本：" & Path.GetFileName(Source) & vbCrLf & "迁移会先展示模组、配置和游戏组件的变化，并保存原环境还原点。原存档、截图和服务器列表保持当前内容。"
            CardOrigin.Visibility = Visibility.Visible
        Catch ex As IO.FileNotFoundException
        Catch ex As IO.DirectoryNotFoundException
        Catch ex As Exception
            LabOrigin.Text = "副本来源记录不可用，无法自动迁移。请在资源管理器中检查版本目录。"
            CardOrigin.Visibility = Visibility.Visible
        End Try
        BtnMigrate.IsEnabled = Not InstanceClonerUi.IsBusy AndAlso LabOrigin.Text.StartsWith("来源版本：", StringComparison.Ordinal)
        ValidateName()
    End Sub
    Private Sub NameChanged() Handles TextName.TextChanged
        ValidateName()
    End Sub
    Private Sub ValidateName()
        If TextName Is Nothing OrElse BtnClone Is Nothing OrElse Owner Is Nothing Then Return
        Dim ErrorText As String = Nothing
        Try
            InstanceResourceStore.SafeName(TextName.Text)
            Dim NativeError = ModValidate.Validate(TextName.Text, TextName.ValidateRules)
            If NativeError <> "" Then ErrorText = NativeError
            Dim Address = Path.Combine(Path.GetDirectoryName(Owner.PathVersion.TrimEnd("\"c)), TextName.Text)
            If IO.Directory.Exists(Address) OrElse IO.File.Exists(Address) Then ErrorText = "同名版本已存在，请使用新的测试版本名称。"
        Catch ex As IO.IOException
            ErrorText = ex.Message
        End Try
        LabValidation.Text = If(ErrorText, "")
        LabValidation.Visibility = If(ErrorText Is Nothing, Visibility.Collapsed, Visibility.Visible)
        BtnClone.IsEnabled = ErrorText Is Nothing AndAlso Not InstanceClonerUi.IsBusy
    End Sub
    Private Sub CloneVersion() Handles BtnClone.Click
        ValidateName()
        If Not BtnClone.IsEnabled OrElse Owner IsNot PageInstanceLeft.Instance Then Return
        InstanceClonerUi.Start(Owner, TextName.Text, New InstanceCloneOptions With {.CopySaves = CheckSaves.Checked, .CopyScreenshots = CheckScreenshots.Checked, .CopyServers = CheckServers.Checked})
        UpdateTaskState()
    End Sub
    Private Sub MigrateVersion() Handles BtnMigrate.Click
        If Owner Is Nothing OrElse Owner IsNot PageInstanceLeft.Instance Then Return
        InstanceClonerUi.Migrate(Owner)
        UpdateTaskState()
    End Sub
    Private Sub UpdateTaskState()
        If InstanceClonerUi.IsBusy Then
            BtnClone.IsEnabled = False
            BtnMigrate.IsEnabled = False
        Else
            Refresh()
        End If
    End Sub
End Class
