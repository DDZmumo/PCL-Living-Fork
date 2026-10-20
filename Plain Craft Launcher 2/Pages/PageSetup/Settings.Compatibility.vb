Partial Public Class Settings
    Private Const FolderImportMarker As String = "LivingPclFoldersImported"

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

    Friend Shared Function ImportFoldersOnce(Store As Microsoft.Win32.RegistryKey, SourceValue As String) As String
        Dim Original = Store.GetValue("LaunchFolders", Nothing)
        Dim Current = If(Original Is Nothing, Nothing, Convert.ToString(Original, Globalization.CultureInfo.InvariantCulture))
        If Boolean.TrueString.Equals(Convert.ToString(Store.GetValue(FolderImportMarker, "False")), StringComparison.OrdinalIgnoreCase) Then Return If(Current, "")
        If Not ValidFolderList(Current) OrElse Not ValidFolderList(SourceValue) Then Return If(Current, "")
        Dim Merged = MergePclFolders(Current, SourceValue)
        Try
            Store.SetValue("LaunchFolders", Merged, Microsoft.Win32.RegistryValueKind.String)
            If Convert.ToString(Store.GetValue("LaunchFolders", Nothing)) <> Merged Then Throw New IOException("游戏目录导入校验失败")
            Store.SetValue(FolderImportMarker, Boolean.TrueString, Microsoft.Win32.RegistryValueKind.String)
        Catch
            If Original Is Nothing Then
                Store.DeleteValue("LaunchFolders", False)
            Else
                Store.SetValue("LaunchFolders", Original)
            End If
            Throw
        End Try
        Return Merged
    End Function

    Friend Shared Sub ResetRegistryPreference(Store As Microsoft.Win32.RegistryKey, Entry As Setting)
        If Not IsCompatiblePclSetting(Entry) Then
            Store.DeleteValue(Entry.Key, False)
            Return
        End If
        Dim Value = Convert.ToString(Entry.DefaultValue, Globalization.CultureInfo.InvariantCulture)
        Store.SetValue(Entry.Key, Value, Microsoft.Win32.RegistryValueKind.String)
        If Convert.ToString(Store.GetValue(Entry.Key, Nothing)) <> Value Then Throw New IOException("默认设置保存校验失败")
        If Entry.Key = "LaunchFolders" Then Store.SetValue(FolderImportMarker, Boolean.TrueString, Microsoft.Win32.RegistryValueKind.String)
    End Sub
End Class
