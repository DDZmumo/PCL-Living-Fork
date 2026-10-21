Imports System.Collections.Generic
Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions

''' <summary>
''' 版本图标共享配置使用官方 PCL 的资源地址，显示地址由 MyBitmap 按当前程序集解析。
''' 只迁移两边均包含的内置方块图片，不改动自定义文件、网络图片或其他设置。
''' </summary>
Friend NotInheritable Class MinecraftLogoCompatibility
    Private Sub New()
    End Sub

    Friend Const OfficialImageRoot As String = "pack://application:,,,/Plain Craft Launcher 2;component/Images/"
    Private Shared ReadOnly BuiltInFiles As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
        {"Anvil.png", "Anvil.png"}, {"CobbleStone.png", "CobbleStone.png"},
        {"CommandBlock.png", "CommandBlock.png"}, {"Egg.png", "Egg.png"},
        {"Fabric.png", "Fabric.png"}, {"GoldBlock.png", "GoldBlock.png"},
        {"Grass.png", "Grass.png"}, {"GrassPath.png", "GrassPath.png"},
        {"NeoForge.png", "NeoForge.png"}, {"OptiFabric.png", "OptiFabric.png"},
        {"RedstoneBlock.png", "RedstoneBlock.png"},
        {"RedstoneLampOff.png", "RedstoneLampOff.png"}, {"RedstoneLampOn.png", "RedstoneLampOn.png"}}
    Private Shared ReadOnly BuiltInAddress As New Regex(
        "\A(?:pack://application:,,,/(?:(?:Plain(?: |%20)Craft(?: |%20)Launcher(?: |%20)2|LivingPCL);component/)?|/(?:Plain(?: |%20)Craft(?: |%20)Launcher(?: |%20)2|LivingPCL);component/)images/blocks/(?<File>[a-z0-9]+\.png)\z",
        RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)

    Friend Shared Function ToStorageUri(Value As String) As String
        If String.IsNullOrEmpty(Value) Then Return Value
        Dim Match = BuiltInAddress.Match(Value)
        Dim FileName As String = Nothing
        If Not Match.Success OrElse Not BuiltInFiles.TryGetValue(Match.Groups("File").Value, FileName) Then Return Value
        Return OfficialImageRoot & "Blocks/" & FileName
    End Function

    ''' <summary>
    ''' 缓存命中时也要修复版本 Setup.ini；PCL.ini 的卡片缓存只保存版本名，不保存图标。
    ''' </summary>
    Friend Shared Function ReadVersionLogo(SetupFile As String, Optional DefaultValue As String = "") As String
        Dim Original = ReadIni(SetupFile, "Logo", Nothing)
        If Original Is Nothing Then Return DefaultValue
        Dim SharedValue = ToStorageUri(Original)
        If Not String.Equals(Original, SharedValue, StringComparison.Ordinal) Then WriteIni(SetupFile, "Logo", SharedValue)
        Return SharedValue
    End Function

    Friend Shared Sub WriteVersionLogo(SetupFile As String, Value As String)
        WriteIni(SetupFile, "Logo", ToStorageUri(Value))
    End Sub

    '共享目录修复只打开已存在的版本配置，不加载版本、不创建游戏目录。
    Friend Shared Function RepairFolders(Folders As IEnumerable(Of String)) As Integer
        Dim Seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim Repaired As Integer
        For Each Folder In Folders
            Try
                If String.IsNullOrWhiteSpace(Folder) OrElse Not Path.IsPathRooted(Folder) Then Continue For
                Dim Root = Path.GetFullPath(Folder).TrimEnd("\"c, "/"c)
                If Not Seen.Add(Root) OrElse IsLink(Root) Then Continue For
                Dim Versions = Path.Combine(Root, "versions")
                If Not Directory.Exists(Versions) OrElse IsLink(Versions) Then Continue For
                For Each Version In Directory.EnumerateDirectories(Versions)
                    Try
                        Dim ConfigFolder = Path.Combine(Version, "PCL")
                        If IsLink(Version) OrElse Not Directory.Exists(ConfigFolder) OrElse IsLink(ConfigFolder) Then Continue For
                        If RepairFile(Path.Combine(ConfigFolder, "Setup.ini")) Then Repaired += 1
                    Catch ex As Exception When IsUnavailable(ex)
                        '某个版本不可用不影响其他版本。
                    End Try
                Next
            Catch ex As Exception When IsUnavailable(ex)
                '已删除、离线或无权限的共享目录不弹出错误，也不修改目录列表。
            End Try
        Next
        Return Repaired
    End Function

    Private Shared Function IsLink(FileName As String) As Boolean
        Return (File.GetAttributes(FileName) And FileAttributes.ReparsePoint) <> 0
    End Function

    Private Shared Function IsUnavailable(ex As Exception) As Boolean
        Return TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
            TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException OrElse TypeOf ex Is System.Security.SecurityException
    End Function

    Private Shared Function RepairFile(SetupFile As String) As Boolean
        If Not File.Exists(SetupFile) OrElse IsLink(SetupFile) Then Return False
        Dim Temporary As String = Nothing
        Try
            '禁止并发写入原文件；原子替换时保留读取句柄，避免留下半份配置。
            Using Source As New FileStream(SetupFile, FileMode.Open, FileAccess.Read, FileShare.Read Or FileShare.Delete)
                If Source.Length > 4L * 1024 * 1024 Then Return False
                Dim Original As Byte()
                Using Buffer As New MemoryStream()
                    Source.CopyTo(Buffer)
                    Original = Buffer.ToArray()
                End Using
                Dim Text As String, Encoding As Encoding
                Using Buffer As New MemoryStream(Original), Reader As New StreamReader(Buffer, New UTF8Encoding(False, True), True)
                    Text = Reader.ReadToEnd()
                    Encoding = Reader.CurrentEncoding
                End Using
                Dim Preamble = Encoding.GetPreamble()
                Dim OriginalBody = Encoding.GetBytes(Text)
                Dim WithPreamble = Preamble.Concat(OriginalBody).ToArray()
                Dim Prefix = If(Original.SequenceEqual(WithPreamble), Preamble, New Byte() {})
                If Not Original.SequenceEqual(Prefix.Concat(OriginalBody)) Then Return False
                Dim Updated = Regex.Replace(Text, "(?m)^(Logo:)([^\r\n]*)", Function(Match) Match.Groups(1).Value & ToStorageUri(Match.Groups(2).Value))
                If Updated = Text Then Return False
                Temporary = SetupFile & ".livingpcl-icons-" & Guid.NewGuid().ToString("N") & ".tmp"
                Using Destination As New FileStream(Temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                    Dim Data = Prefix.Concat(Encoding.GetBytes(Updated)).ToArray()
                    Destination.Write(Data, 0, Data.Length)
                    Destination.Flush(True)
                End Using
                File.Replace(Temporary, SetupFile, Nothing)
                IniClearCache(SetupFile)
                Return True
            End Using
        Finally
            If Temporary IsNot Nothing AndAlso File.Exists(Temporary) Then
                Try
                    File.Delete(Temporary)
                Catch ex As Exception When IsUnavailable(ex)
                End Try
            End If
        End Try
    End Function
End Class
