Imports System
Imports System.IO

Friend NotInheritable Class MinecraftFolderPolicy
    Friend Shared Function PackFolder(LauncherFolder As String) As String
        Return Path.GetFullPath(Path.Combine(LauncherFolder, ".minecraft")).TrimEnd("\"c) & "\"
    End Function

    Private Shared Function Normalize(Value As String) As String
        If Value.StartsWith("\\?\UNC\", StringComparison.OrdinalIgnoreCase) Then
            Value = "\\" & Value.Substring(8)
        ElseIf Value.StartsWith("\\?\", StringComparison.Ordinal) Then
            Value = Value.Substring(4)
        End If
        Return Path.GetFullPath(Value.Replace("/", "\")).TrimEnd("\"c)
    End Function

    Friend Shared Function IsPackVersion(LauncherFolder As String, InstanceFolder As String) As Boolean
        If String.IsNullOrWhiteSpace(InstanceFolder) Then Return False
        Try
            Return String.Equals(Path.GetDirectoryName(Normalize(InstanceFolder)), Normalize(Path.Combine(PackFolder(LauncherFolder), "versions")), StringComparison.OrdinalIgnoreCase)
        Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is NotSupportedException
            Return False
        End Try
    End Function

End Class
