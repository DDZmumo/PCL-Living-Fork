Public Class PageSetupModpack
    Private Refreshing As Boolean

    Private Sub PageLoaded() Handles Me.Loaded
        Refreshing = True
        AniControlEnabled += 1
        Try
            Dim Title = CardLauncher.MainTextBlock
            LabVersion.Text = "LivingPCL " & VersionBaseName
            SettingService.RefreshSettings(Me)
            CheckModpackMode.Checked = ModModpackMode.Enabled
            PanBack.ScrollToHome()
        Finally
            AniControlEnabled -= 1
            Refreshing = False
        End Try
    End Sub

    Private Sub ModeChanged(sender As Object, User As Boolean) Handles CheckModpackMode.Change
        ModModpackMode.Toggle(CheckModpackMode, User)
    End Sub

    Private Sub CacheChanged() Handles TextSystemCache.ValidatedTextChanged
        If Not Refreshing AndAlso AniControlEnabled = 0 Then Hint("缓存文件夹将在重启启动器后生效。", Log:=False)
    End Sub
End Class
