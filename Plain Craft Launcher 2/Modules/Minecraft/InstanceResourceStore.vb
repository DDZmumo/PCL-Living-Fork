Imports System.IO
Imports System.IO.Compression
Imports System.Threading
Imports System.Text.RegularExpressions
Imports System.Windows.Media.Imaging

Friend Enum InstanceResourceKind
    Saves
    Screenshots
    ResourcePacks
    ShaderPacks
    Schematics
End Enum

Friend NotInheritable Class InstanceResourceEntry
    Public Name As String
    Public FullPath As String
    Public Description As String
    Public IsDirectory As Boolean
    Public ModifiedUtc As Date
    Public CreatedUtc As Date
    Public Length As Long
    Public Icon As ImageSource
End Class

'只管理指定资源目录；读取不创建目录，写入不覆盖同名资源，删除可在本页恢复。
Friend NotInheritable Class InstanceResourceStore
    Friend Shared Function Title(Kind As InstanceResourceKind) As String
        Dim Names As String() = {"存档", "截图", "资源包", "光影包", "投影/原理图"}
        Return Names(CInt(Kind))
    End Function

    Friend Shared Function FolderName(Kind As InstanceResourceKind) As String
        Dim Folders As String() = {"saves", "screenshots", "resourcepacks", "shaderpacks", "schematics"}
        Return Folders(CInt(Kind))
    End Function

    Friend Shared Function Root(GameFolder As String, Kind As InstanceResourceKind) As String
        Return Path.Combine(GameFolder, FolderName(Kind))
    End Function

    Private Shared Function Normalize(Value As String) As String
        Return Path.GetFullPath(PathUtils.RemoveExtendedPrefix(Value)).TrimEnd("\"c, "/"c)
    End Function

    Friend Shared Sub EnsureWithin(Root As String, FileName As String, Optional AllowRoot As Boolean = False)
        Dim Parent = Normalize(Root), Full = Normalize(FileName)
        If AllowRoot AndAlso Full.Equals(Parent, StringComparison.OrdinalIgnoreCase) Then Return
        If Not Full.StartsWith(Parent & "\", StringComparison.OrdinalIgnoreCase) Then Throw New IOException("资源路径超出了当前目录。")
        Dim Current = Full
        Do While Not Current.Equals(Parent, StringComparison.OrdinalIgnoreCase)
            If (File.Exists(Current) OrElse Directory.Exists(Current)) AndAlso (File.GetAttributes(Current) And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("请通过资源管理器管理链接文件或链接目录。")
            Current = Path.GetDirectoryName(Current)
        Loop
    End Sub

    Friend Shared Function SafeName(Value As String) As String
        If String.IsNullOrWhiteSpace(Value) OrElse Value <> Value.Trim() OrElse Value.EndsWith(".") OrElse Value.Length > 120 OrElse Value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 OrElse Value = "." OrElse Value = ".." Then Throw New IOException("文件名无效，请使用不含路径和特殊字符的名称。")
        If Regex.IsMatch(Value, "\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase) Then Throw New IOException("此名称为系统保留名称。")
        If Value.StartsWith(".pcl-", StringComparison.OrdinalIgnoreCase) Then Throw New IOException("此名称为启动器临时目录保留。")
        Return Value
    End Function

    Friend Shared Sub DeleteStage(Root As String, Stage As String)
        If Not Directory.Exists(Stage) Then Return
        EnsureWithin(Root, Stage)
        If Not Regex.IsMatch(Path.GetFileName(Stage), "\A\.pcl-import-[0-9a-f]{32}\z") Then Throw New IOException("无效的导入临时目录。")
        Directory.Delete(Stage, True)
    End Sub

    Friend Shared Sub ValidatePackage(FileName As String, Kind As InstanceResourceKind)
        Using Archive = ZipFile.OpenRead(FileName)
            If Kind = InstanceResourceKind.ResourcePacks AndAlso Archive.GetEntry("pack.mcmeta") Is Nothing Then Throw New IOException("资源包缺少 pack.mcmeta，未替换原文件。")
            If Kind = InstanceResourceKind.ShaderPacks AndAlso Not Archive.Entries.Any(Function(Entry) Entry.FullName.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase)) Then Throw New IOException("光影包缺少 shaders 目录，未替换原文件。")
        End Using
    End Sub

    Private Shared Function AcceptFile(Kind As InstanceResourceKind, FileName As String) As Boolean
        Dim Extension = Path.GetExtension(FileName).ToLowerInvariant()
        Select Case Kind
            Case InstanceResourceKind.Screenshots
                Return {".png", ".jpg", ".jpeg", ".bmp"}.Contains(Extension)
            Case InstanceResourceKind.ResourcePacks, InstanceResourceKind.ShaderPacks
                Return Extension = ".zip"
            Case InstanceResourceKind.Schematics
                Return {".litematic", ".schem", ".schematic", ".nbt"}.Contains(Extension)
            Case Else
                Return False
        End Select
    End Function

    Friend Shared Function Scan(Root As String, Folder As String, Kind As InstanceResourceKind, Token As CancellationToken) As List(Of InstanceResourceEntry)
        EnsureWithin(Root, Folder, True)
        Dim Result As New List(Of InstanceResourceEntry)
        If Not Directory.Exists(Folder) Then Return Result
        Dim Children = New DirectoryInfo(Folder).GetFileSystemInfos()
        Dim PreviewCount As Integer
        For Each Child In Children.OrderByDescending(Function(Item) Item.LastWriteTimeUtc)
            Token.ThrowIfCancellationRequested()
            Try
                If Child.Name.StartsWith(".pcl-", StringComparison.OrdinalIgnoreCase) OrElse (Child.Attributes And FileAttributes.ReparsePoint) <> 0 Then Continue For
                Dim IsDirectory = (Child.Attributes And FileAttributes.Directory) <> 0
                If IsDirectory Then
                    If Kind = InstanceResourceKind.Screenshots Then Continue For
                ElseIf Not AcceptFile(Kind, Child.Name) Then
                    Continue For
                End If
                Dim Entry As New InstanceResourceEntry With {.Name = Child.Name, .FullPath = Child.FullName, .IsDirectory = IsDirectory, .ModifiedUtc = Child.LastWriteTimeUtc,
                    .CreatedUtc = Child.CreationTimeUtc, .Length = If(IsDirectory, 0, DirectCast(Child, FileInfo).Length)}
                Dim Detail = If(IsDirectory, "文件夹", FormatSize(Entry.Length))
                If Kind = InstanceResourceKind.Saves Then
                    Detail = If(File.Exists(Path.Combine(Child.FullName, "level.dat")), "Minecraft 存档", "未找到 level.dat，存档可能不完整")
                    Entry.Icon = LoadImage(Path.Combine(Child.FullName, "icon.png"), 64)
                ElseIf Kind = InstanceResourceKind.ResourcePacks Then
                    ReadPackInfo(Entry, Detail)
                ElseIf Kind = InstanceResourceKind.Screenshots AndAlso PreviewCount < 80 Then
                    Entry.Icon = LoadImage(Child.FullName, 64)
                    PreviewCount += 1
                End If
                Entry.Description = Detail & " · " & Child.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                Result.Add(Entry)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                '扫描期间被移走或无权限的资源不阻止其他条目显示。
            End Try
        Next
        If Kind <> InstanceResourceKind.Saves AndAlso Kind <> InstanceResourceKind.Screenshots Then Return Result.OrderByDescending(Function(Item) Item.IsDirectory).ThenBy(Function(Item) Item.Name, StringComparer.OrdinalIgnoreCase).ToList()
        Return Result
    End Function

    Private Shared Function FormatSize(Size As Long) As String
        If Size < 1024 Then Return Size & " B"
        Dim Unit As Integer = Math.Min(3, CInt(Math.Floor(Math.Log(Size, 1024))))
        Dim Units As String() = {" B", " KB", " MB", " GB"}
        Return (Size / Math.Pow(1024, Unit)).ToString("0.#") & Units(Unit)
    End Function

    Private Shared Sub ReadPackInfo(Entry As InstanceResourceEntry, ByRef Detail As String)
        Try
            Dim Metadata As String = Nothing
            If Entry.IsDirectory Then
                Dim FileName = Path.Combine(Entry.FullPath, "pack.mcmeta")
                If File.Exists(FileName) AndAlso New FileInfo(FileName).Length <= 1024 * 1024 Then Metadata = File.ReadAllText(FileName)
                Entry.Icon = LoadImage(Path.Combine(Entry.FullPath, "pack.png"), 64)
            Else
                Using Stream As New FileStream(Entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete), Archive As New ZipArchive(Stream, ZipArchiveMode.Read)
                    Dim Json = Archive.GetEntry("pack.mcmeta")
                    If Json IsNot Nothing AndAlso Json.Length <= 1024 * 1024 Then
                        Using Reader As New StreamReader(Json.Open())
                            Metadata = Reader.ReadToEnd()
                        End Using
                    End If
                    Dim Icon = Archive.GetEntry("pack.png")
                    If Icon IsNot Nothing AndAlso Icon.Length <= 4 * 1024 * 1024 Then
                        Using Input = Icon.Open(), Buffer As New MemoryStream()
                            Input.CopyTo(Buffer)
                            Buffer.Position = 0
                            Entry.Icon = DecodeImage(Buffer, 64)
                        End Using
                    End If
                End Using
            End If
            If Metadata IsNot Nothing Then
                Dim Pack = JObject.Parse(Metadata)("pack")
                Dim Description = Pack?("description")
                Dim Text = If(Description?.Type = JTokenType.String, Description?.ToString(), Description?("text")?.ToString())
                If Not String.IsNullOrWhiteSpace(Text) Then Detail = Text.ReplaceLineEndings(" ").Substring(0, Math.Min(180, Text.ReplaceLineEndings(" ").Length))
                If Pack?("pack_format") IsNot Nothing Then Detail &= " · 资源格式 " & Pack("pack_format").ToString()
            End If
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is Newtonsoft.Json.JsonException OrElse TypeOf ex Is NotSupportedException OrElse TypeOf ex Is ArgumentException
            '损坏的资源包仍在列表中供用户管理。
        End Try
    End Sub

    Friend Shared Function LoadImage(FileName As String, Width As Integer) As BitmapSource
        Try
            If Not File.Exists(FileName) OrElse New FileInfo(FileName).Length > 64L * 1024 * 1024 Then Return Nothing
            Using Stream As New FileStream(FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete)
                Return DecodeImage(Stream, Width)
            End Using
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is NotSupportedException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is FormatException
            Return Nothing
        End Try
    End Function

    Private Shared Function DecodeImage(Stream As Stream, Width As Integer) As BitmapSource
        Dim Image As New BitmapImage()
        Image.BeginInit()
        Image.CacheOption = BitmapCacheOption.OnLoad
        Image.DecodePixelWidth = Width
        Image.StreamSource = Stream
        Image.EndInit()
        Image.Freeze()
        Return Image
    End Function

    Friend Shared Sub ValidateEntry(Root As String, Entry As InstanceResourceEntry)
        EnsureWithin(Root, Entry.FullPath)
        If Not File.Exists(Entry.FullPath) AndAlso Not Directory.Exists(Entry.FullPath) Then Throw New IOException("资源已被移动或删除，请刷新列表后重试。")
        '目录内容变化不应让整个存档失去可操作性；目录用创建时间识别替换，文件检查修改时间和大小。
        If Entry.IsDirectory Then
            If Directory.GetCreationTimeUtc(Entry.FullPath) <> Entry.CreatedUtc Then Throw New IOException("资源已变化，请刷新列表后重试。")
        ElseIf File.GetLastWriteTimeUtc(Entry.FullPath) <> Entry.ModifiedUtc OrElse New FileInfo(Entry.FullPath).Length <> Entry.Length Then
            Throw New IOException("资源已变化，请刷新列表后重试。")
        End If
    End Sub

    Friend Shared Function Rename(Root As String, Entry As InstanceResourceEntry, NewName As String) As String
        ValidateEntry(Root, Entry)
        Dim Destination = Path.Combine(Path.GetDirectoryName(Entry.FullPath), SafeName(NewName))
        EnsureWithin(Root, Destination)
        If File.Exists(Destination) OrElse Directory.Exists(Destination) Then Throw New IOException("已存在同名资源，未覆盖原文件。")
        If Entry.IsDirectory Then Directory.Move(Entry.FullPath, Destination) Else File.Move(Entry.FullPath, Destination)
        Return Destination
    End Function

    Private Shared Sub CopyStream(Input As Stream, Output As Stream, Token As CancellationToken)
        Dim Buffer(65535) As Byte
        Do
            Token.ThrowIfCancellationRequested()
            Dim Count = Input.Read(Buffer, 0, Buffer.Length)
            If Count = 0 Then Exit Do
            Output.Write(Buffer, 0, Count)
        Loop
    End Sub

    Private Shared Iterator Function TreeFiles(Folder As String, Token As CancellationToken) As IEnumerable(Of String)
        Dim Pending As New Stack(Of String)
        Pending.Push(Folder)
        Do While Pending.Count > 0
            Token.ThrowIfCancellationRequested()
            Dim Current = Pending.Pop()
            If (File.GetAttributes(Current) And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("资源中包含目录链接，请先通过资源管理器处理。")
            For Each Entry In New DirectoryInfo(Current).EnumerateFileSystemInfos()
                Token.ThrowIfCancellationRequested()
                If (Entry.Attributes And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("资源中包含链接文件，请先通过资源管理器处理。")
                If (Entry.Attributes And FileAttributes.Directory) <> 0 Then
                    Pending.Push(Entry.FullName)
                Else
                    Yield Entry.FullName
                End If
            Next
        Loop
    End Function

    Private Shared Sub AtomicCopy(Source As String, Destination As String, Replace As Boolean, Token As CancellationToken)
        Dim Temporary = Destination & ".pcl-copy-" & Guid.NewGuid().ToString("N")
        Try
            Directory.CreateDirectory(Path.GetDirectoryName(Destination))
            Using Input As New FileStream(Source, FileMode.Open, FileAccess.Read, FileShare.Read Or FileShare.Delete), Output As New FileStream(Temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                CopyStream(Input, Output, Token)
                Output.Flush(True)
            End Using
            Token.ThrowIfCancellationRequested()
            If Replace AndAlso File.Exists(Destination) Then File.Replace(Temporary, Destination, Nothing) Else File.Move(Temporary, Destination)
        Finally
            If File.Exists(Temporary) Then File.Delete(Temporary)
        End Try
    End Sub

    Friend Shared Function ImportFile(Root As String, Folder As String, Kind As InstanceResourceKind, Source As String, Token As CancellationToken) As String
        If Kind = InstanceResourceKind.Saves OrElse Not AcceptFile(Kind, Source) Then Throw New IOException("文件格式不受此资源页面支持。")
        EnsureWithin(Root, Folder, True)
        Dim Destination = Path.Combine(Folder, SafeName(Path.GetFileName(Source)))
        EnsureWithin(Root, Destination)
        If File.Exists(Destination) OrElse Directory.Exists(Destination) Then Throw New IOException("已存在同名资源，未覆盖原文件。")
        AtomicCopy(Source, Destination, False, Token)
        Return Destination
    End Function

    Friend Shared Function ImportWorld(Root As String, Source As String, Name As String, Token As CancellationToken) As String
        Dim Destination = Path.Combine(Root, SafeName(Name))
        EnsureWithin(Root, Destination)
        If Directory.Exists(Destination) OrElse File.Exists(Destination) Then Throw New IOException("已存在同名存档，未覆盖原存档。")
        Directory.CreateDirectory(Root)
        Dim Stage = Path.Combine(Root, ".pcl-import-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(Stage)
        Try
            Using Archive = ZipFile.OpenRead(Source)
                For Each Entry In Archive.Entries
                    Token.ThrowIfCancellationRequested()
                    Dim Relative = Entry.FullName.Replace("/", "\")
                    If Path.IsPathRooted(Relative) OrElse Relative.Split("\"c).Any(Function(Part) Part = ".." OrElse Part.Contains(":")) OrElse ((Entry.ExternalAttributes >> 16) And &HF000) = &HA000 Then Throw New IOException("存档压缩包包含越界路径或链接。")
                    If Relative = "" Then Continue For
                    Dim Target = Path.GetFullPath(Path.Combine(Stage, Relative))
                    EnsureWithin(Stage, Target)
                    If Relative.EndsWith("\") Then
                        Directory.CreateDirectory(Target)
                    Else
                        Directory.CreateDirectory(Path.GetDirectoryName(Target))
                        Using Input = Entry.Open(), Output As New FileStream(Target, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                            CopyStream(Input, Output, Token)
                        End Using
                    End If
                Next
            End Using
            Dim World As String = Nothing
            If File.Exists(Path.Combine(Stage, "level.dat")) Then
                World = Stage
            Else
                Dim Candidates = Directory.GetDirectories(Stage).Where(Function(Folder) File.Exists(Path.Combine(Folder, "level.dat"))).ToArray()
                If Candidates.Length = 1 Then World = Candidates(0)
            End If
            If World Is Nothing Then Throw New IOException("压缩包中没有找到单个 Minecraft 存档（level.dat）。")
            Token.ThrowIfCancellationRequested()
            Directory.Move(World, Destination)
            Return Destination
        Finally
            DeleteStage(Root, Stage)
        End Try
    End Function

    Friend Shared Function ImportFolder(Root As String, Folder As String, Kind As InstanceResourceKind, Source As String, Name As String, Token As CancellationToken) As String
        EnsureWithin(Root, Folder, True)
        Dim Destination = Path.Combine(Folder, SafeName(Name))
        EnsureWithin(Root, Destination)
        If Directory.Exists(Destination) OrElse File.Exists(Destination) Then Throw New IOException("已存在同名资源，未覆盖原文件夹。")
        If Normalize(Folder).Equals(Normalize(Source), StringComparison.OrdinalIgnoreCase) OrElse Normalize(Folder).StartsWith(Normalize(Source) & "\", StringComparison.OrdinalIgnoreCase) Then Throw New IOException("不能把文件夹导入到它自身内部。")
        Select Case Kind
            Case InstanceResourceKind.Saves
                If Not File.Exists(Path.Combine(Source, "level.dat")) Then Throw New IOException("该文件夹中没有找到 Minecraft 存档 level.dat。")
            Case InstanceResourceKind.ResourcePacks
                If Not File.Exists(Path.Combine(Source, "pack.mcmeta")) Then Throw New IOException("该文件夹中没有找到资源包 pack.mcmeta。")
            Case InstanceResourceKind.ShaderPacks
                If Not Directory.Exists(Path.Combine(Source, "shaders")) Then Throw New IOException("该文件夹中没有找到光影包 shaders 目录。")
            Case InstanceResourceKind.Screenshots
                Throw New IOException("请使用从文件导入来添加截图。")
        End Select
        Directory.CreateDirectory(Folder)
        Dim Stage = Path.Combine(Folder, ".pcl-import-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(Stage)
        Try
            Dim Prefix = Normalize(Source) & "\"
            For Each FileName In TreeFiles(Source, Token)
                Dim Target = Path.Combine(Stage, FileName.Substring(Prefix.Length))
                EnsureWithin(Stage, Target)
                AtomicCopy(FileName, Target, False, Token)
            Next
            Token.ThrowIfCancellationRequested()
            Directory.Move(Stage, Destination)
            Return Destination
        Finally
            DeleteStage(Root, Stage)
        End Try
    End Function

    Friend Shared Sub Export(Root As String, Entry As InstanceResourceEntry, Destination As String, Token As CancellationToken)
        ExportCore(Root, Entry, Destination, Token, True)
    End Sub
    Private Shared Sub ExportCore(Root As String, Entry As InstanceResourceEntry, Destination As String, Token As CancellationToken, Overwrite As Boolean)
        ValidateEntry(Root, Entry)
        If Normalize(Entry.FullPath).Equals(Normalize(Destination), StringComparison.OrdinalIgnoreCase) Then Throw New IOException("导出位置与原文件相同。")
        If Not Entry.IsDirectory Then
            AtomicCopy(Entry.FullPath, Destination, Overwrite, Token)
            Return
        End If
        If Normalize(Destination).StartsWith(Normalize(Entry.FullPath) & "\", StringComparison.OrdinalIgnoreCase) Then Throw New IOException("请将压缩包保存到待导出的文件夹之外。")
        Dim Temporary = Destination & ".pcl-export-" & Guid.NewGuid().ToString("N")
        Try
            Using Output As New FileStream(Temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None), Archive As New ZipArchive(Output, ZipArchiveMode.Create)
                Dim Prefix = Normalize(Entry.FullPath) & "\"
                For Each FileName In TreeFiles(Entry.FullPath, Token)
                    EnsureWithin(Entry.FullPath, FileName)
                    Token.ThrowIfCancellationRequested()
                    Dim ZipEntry = Archive.CreateEntry(FileName.Substring(Prefix.Length).Replace("\", "/"), CompressionLevel.Fastest)
                    Using Input As New FileStream(FileName, FileMode.Open, FileAccess.Read, FileShare.Read Or FileShare.Delete), Target = ZipEntry.Open()
                        CopyStream(Input, Target, Token)
                    End Using
                Next
            End Using
            Token.ThrowIfCancellationRequested()
            If File.Exists(Destination) AndAlso Overwrite Then File.Replace(Temporary, Destination, Nothing) Else File.Move(Temporary, Destination)
        Finally
            If File.Exists(Temporary) Then File.Delete(Temporary)
        End Try
    End Sub

    Friend Shared Sub ExportMany(Root As String, Items As IEnumerable(Of InstanceResourceEntry), Folder As String, Token As CancellationToken)
        Dim List = Items.ToList()
        Dim Destinations As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each Entry In List
            ValidateEntry(Root, Entry)
            Dim Destination = Path.Combine(Folder, If(Entry.IsDirectory, Entry.Name & ".zip", Entry.Name))
            If Not Destinations.Add(Destination) OrElse File.Exists(Destination) OrElse Directory.Exists(Destination) Then Throw New IOException("导出目标存在同名资源，请选择其他文件夹。")
            If Entry.IsDirectory AndAlso Normalize(Destination).StartsWith(Normalize(Entry.FullPath) & "\", StringComparison.OrdinalIgnoreCase) Then Throw New IOException("不能导出到所选资源的文件夹内。")
        Next
        For Each Entry In List
            Token.ThrowIfCancellationRequested()
            ExportCore(Root, Entry, Path.Combine(Folder, If(Entry.IsDirectory, Entry.Name & ".zip", Entry.Name)), Token, False)
        Next
    End Sub

    Friend Shared Sub RecycleMany(Root As String, Items As IEnumerable(Of InstanceResourceEntry), RecycleRoot As String, Token As CancellationToken)
        Dim List = Items.ToList()
        For Each Entry In List
            ValidateEntry(Root, Entry)
        Next
        For Each Entry In List
            Token.ThrowIfCancellationRequested()
            MoveToRecycle(Root, Entry, RecycleRoot)
        Next
    End Sub

    Friend Shared Function MoveToRecycle(Root As String, Entry As InstanceResourceEntry, RecycleRoot As String) As String
        ValidateEntry(Root, Entry)
        Dim Box = Path.Combine(RecycleRoot, Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(Box)
        Dim Payload = Path.Combine(Box, "content")
        Dim Metadata = Path.Combine(Box, "resource.json")
        Try
            File.WriteAllText(Metadata, New JObject(New JProperty("root", Normalize(Root)), New JProperty("name", Path.GetFileName(Entry.FullPath)), New JProperty("folder", Path.GetDirectoryName(Entry.FullPath)), New JProperty("directory", Entry.IsDirectory)).ToString())
            If Entry.IsDirectory Then Directory.Move(Entry.FullPath, Payload) Else File.Move(Entry.FullPath, Payload)
            Return Box
        Catch
            If Not File.Exists(Payload) AndAlso Not Directory.Exists(Payload) Then
                If File.Exists(Metadata) Then File.Delete(Metadata)
                Directory.Delete(Box, False)
            End If
            Throw
        End Try
    End Function

    Friend Shared Function RestoreLast(Root As String, RecycleRoot As String) As String
        If Not Directory.Exists(RecycleRoot) Then Throw New IOException("没有可恢复的删除记录。")
        For Each Box In New DirectoryInfo(RecycleRoot).GetDirectories().OrderByDescending(Function(Item) Item.CreationTimeUtc)
            If Not Regex.IsMatch(Box.Name, "\A[0-9a-f]{32}\z") OrElse (Box.Attributes And FileAttributes.ReparsePoint) <> 0 Then Continue For
            Dim Metadata = Path.Combine(Box.FullName, "resource.json"), Payload = Path.Combine(Box.FullName, "content")
            If Not File.Exists(Metadata) OrElse New FileInfo(Metadata).Length > 65536 OrElse (Not File.Exists(Payload) AndAlso Not Directory.Exists(Payload)) Then Continue For
            EnsureWithin(RecycleRoot, Payload)
            EnsureWithin(RecycleRoot, Metadata)
            Dim Json = JObject.Parse(File.ReadAllText(Metadata))
            If Not Normalize(Root).Equals(Json("root")?.ToString(), StringComparison.OrdinalIgnoreCase) Then Continue For
            Dim Destination = Path.Combine(Json("folder").ToString(), SafeName(Json("name").ToString()))
            EnsureWithin(Root, Destination)
            If File.Exists(Destination) OrElse Directory.Exists(Destination) Then Throw New IOException("原位置已有同名资源，请先重命名该资源，再恢复删除。")
            Directory.CreateDirectory(Path.GetDirectoryName(Destination))
            If Directory.Exists(Payload) Then Directory.Move(Payload, Destination) Else File.Move(Payload, Destination)
            File.Delete(Metadata)
            Directory.Delete(Box.FullName, False)
            Return Destination
        Next
        Throw New IOException("当前资源目录没有可恢复的删除记录。")
    End Function
End Class
