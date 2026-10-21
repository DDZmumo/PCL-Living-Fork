Imports System.IO
Imports System.IO.Compression

'仅本项目导出的隔离标记生效；其他整合包保留原有的默认隔离行为。
Friend NotInheritable Class ExportedVersionIsolation
    Friend Const MarkerKey As String = "LivingExportVersionIsolation"
    Private Const VersionFoldersKey As String = "LivingExportVersionFolders"

    Friend NotInheritable Class Choice
        Friend ReadOnly Enabled As Boolean
        Friend ReadOnly VersionFolders As List(Of String)
        Friend Sub New(Enabled As Boolean, Optional ExtraVersionFolders As IEnumerable(Of String) = Nothing)
            Me.Enabled = Enabled
            VersionFolders = New List(Of String) From {"PCL"}
            For Each Folder In If(ExtraVersionFolders, Enumerable.Empty(Of String)())
                Dim Relative = NormalizeRelative(Folder).TrimEnd("\"c)
                If Not VersionFolders.Contains(Relative, StringComparer.OrdinalIgnoreCase) Then VersionFolders.Add(Relative)
            Next
        End Sub
    End Class

    Friend Shared Sub ConfigureExport(OverridesFolder As String, Enabled As Boolean, Optional VersionFolders As IEnumerable(Of String) = Nothing)
        Dim Policy As New Choice(Enabled, VersionFolders)
        Dim SetupFile = Path.Combine(OverridesFolder, "PCL", "Setup.ini")
        WriteIni(SetupFile, MarkerKey, Enabled)
        WriteIni(SetupFile, VersionFoldersKey, String.Join("|", Policy.VersionFolders))
        ConfigureTarget(OverridesFolder, Policy)
    End Sub

    Friend Shared Function ReadArchiveChoice(Archive As ZipArchive, ArchiveBaseFolder As String) As Choice
        Dim Name = (ArchiveBaseFolder & "overrides/PCL/Setup.ini").Replace("\", "/")
        Dim Entry = Archive.Entries.FirstOrDefault(Function(Item) Item.FullName.Replace("\", "/").Equals(Name, StringComparison.OrdinalIgnoreCase))
        If Entry Is Nothing Then Return Nothing
        If Entry.Length > 1024L * 1024 Then Throw New InvalidDataException("整合包的版本配置文件过大。")
        Dim Values As New Dictionary(Of String, String)(StringComparer.Ordinal)
        Using Reader As New StreamReader(Entry.Open())
            Do Until Reader.EndOfStream
                Dim Line = Reader.ReadLine()
                Dim Separator = Line.IndexOf(":"c)
                If Separator > 0 Then Values(Line.Substring(0, Separator)) = Line.Substring(Separator + 1)
            Loop
        End Using
        Dim Raw As String = Nothing, Enabled As Boolean
        If Not Values.TryGetValue(MarkerKey, Raw) OrElse Not Boolean.TryParse(Raw, Enabled) Then Return Nothing
        Dim Folders As String = Nothing
        Values.TryGetValue(VersionFoldersKey, Folders)
        Return New Choice(Enabled, If(Folders, "PCL").Split("|"c))
    End Function

    Friend Shared Sub ConfigureTarget(VersionFolder As String, Policy As Choice)
        Dim SetupFile = Path.Combine(VersionFolder, "PCL", "Setup.ini")
        WriteIni(SetupFile, "VersionArgumentIndie", If(Policy.Enabled, 1, 2))
        WriteIni(SetupFile, "VersionArgumentIndieV2", Policy.Enabled)
        IniClearCache(SetupFile)
        Dim Folder = PathUtils.AddSlashSuffix(VersionFolder)
        For Each Key In {"VersionArgumentIndie", "VersionArgumentIndieV2"}
            Settings.Entries(Key).ValueCache.Remove(Folder)
        Next
    End Sub

    Friend Shared Function GameFolder(VersionFolder As String, MinecraftFolder As String, Policy As Choice) As String
        Return If(Policy Is Nothing OrElse Policy.Enabled, VersionFolder, MinecraftFolder)
    End Function

    Private Shared Function NormalizeRelative(Value As String) As String
        If String.IsNullOrWhiteSpace(Value) OrElse Path.IsPathRooted(Value) OrElse Value.StartsWith("/") Then Throw New InvalidDataException("整合包中包含无效的相对文件路径。")
        Dim Parts = Value.Replace("/", "\").TrimEnd("\"c).Split("\"c)
        If Parts.Any(Function(Part) Part = ".." OrElse Part = "." OrElse Part = "" OrElse Part.Contains(":")) Then Throw New InvalidDataException("整合包的文件路径超出了游戏目录。")
        Return String.Join("\", Parts)
    End Function

    Friend Shared Function OverrideTarget(Relative As String, VersionFolder As String, GameFolder As String, Policy As Choice) As String
        Relative = NormalizeRelative(Relative)
        Dim IsVersionFile = Policy.VersionFolders.Any(Function(Folder) Relative.Equals(Folder, StringComparison.OrdinalIgnoreCase) OrElse Relative.StartsWith(Folder & "\", StringComparison.OrdinalIgnoreCase))
        If Not Policy.Enabled AndAlso Not IsVersionFile AndAlso {"versions", "libraries", "assets"}.Contains(Relative.Split("\"c)(0), StringComparer.OrdinalIgnoreCase) Then
            Throw New InvalidDataException("整合包不能向共享游戏目录的版本、支持库或资源缓存目录写入附加文件。")
        End If
        Return Path.Combine(If(Policy.Enabled OrElse IsVersionFile, VersionFolder, GameFolder), Relative)
    End Function

    Friend Shared Sub CopyOverrides(OverridesFolder As String, VersionFolder As String, GameFolder As String, Policy As Choice)
        If Directory.Exists(OverridesFolder) Then
            Dim Prefix = Path.GetFullPath(OverridesFolder).TrimEnd("\"c) & "\"
            For Each FileName In Directory.EnumerateFiles(Prefix, "*", SearchOption.AllDirectories)
                Dim Relative = FileName.Substring(Prefix.Length)
                FileUtils.Copy(FileName, OverrideTarget(Relative, VersionFolder, GameFolder, Policy))
            Next
        End If
        ConfigureTarget(VersionFolder, Policy)
        WriteIni(Path.Combine(VersionFolder, "PCL", "Setup.ini"), "IsStar", False)
    End Sub
End Class
