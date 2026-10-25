Public Class WorldBackupSettings
    Private Owner As McInstance
    Private BindingOwner As Boolean
    Private RetentionDirty As Boolean

    Private Sub Init() Handles Me.Initialized
        CardSettings.SwapControl = PanSettings
    End Sub
    Friend Sub BindOwner(Value As McInstance)
        Dim PreserveInput = Value IsNot Nothing AndAlso Owner IsNot Nothing AndAlso PathUtils.IsSame(Value.PathVersion, Owner.PathVersion) AndAlso RetentionDirty AndAlso TextRetention.IsKeyboardFocusWithin
        BindingOwner = True
        Try
            Owner = Value
            IsEnabled = Value IsNot Nothing
            If Value Is Nothing Then
                CheckAuto.Checked = False
                TextRetention.Text = "5"
                RetentionDirty = False
                Return
            End If
            CheckAuto.Checked = Settings.Get(Of Boolean)("VersionWorldBackupAutoEnabled", Instance:=Value)
            If Not PreserveInput Then
                TextRetention.Text = Settings.Get(Of Integer)("VersionWorldBackupRetention", Instance:=Value).ToString()
                RetentionDirty = False
            End If
        Finally
            BindingOwner = False
        End Try
    End Sub
    Private Sub AutoChanged(Sender As Object, User As Boolean) Handles CheckAuto.Change
        If BindingOwner OrElse Not User OrElse Owner Is Nothing Then Return
        If Settings.Get(Of Boolean)("VersionWorldBackupAutoEnabled", Instance:=Owner) <> CheckAuto.Checked Then Settings.Set("VersionWorldBackupAutoEnabled", CheckAuto.Checked, Instance:=Owner)
    End Sub
    Private Sub RetentionEdited() Handles TextRetention.TextChanged
        If Not BindingOwner AndAlso Owner IsNot Nothing AndAlso TextRetention.IsKeyboardFocusWithin Then RetentionDirty = True
    End Sub
    Private Sub RetentionLostFocus() Handles TextRetention.LostKeyboardFocus
        CommitPending()
    End Sub
    Private Sub RetentionKey(Sender As Object, e As KeyEventArgs) Handles TextRetention.PreviewKeyDown
        If e.Key <> Key.Enter Then Return
        CommitPending()
        e.Handled = True
    End Sub
    Friend Function CommitPending() As Boolean
        If BindingOwner OrElse Owner Is Nothing OrElse Not RetentionDirty Then Return True
        RetentionDirty = False
        Dim Count As Integer
        Dim Valid = Integer.TryParse(TextRetention.Text, Count) AndAlso Count >= 1 AndAlso Count <= 50
        Dim Previous = Settings.Get(Of Integer)("VersionWorldBackupRetention", Instance:=Owner)
        If Valid AndAlso Count <> Previous Then Settings.Set("VersionWorldBackupRetention", Count, Instance:=Owner)
        BindingOwner = True
        Try
            TextRetention.Text = If(Valid, Count, Previous).ToString()
        Finally
            BindingOwner = False
        End Try
        If Not Valid Then Hint("每个世界保留的备份份数必须为 1 到 50。", HintType.Red)
        Return Valid
    End Function
End Class
