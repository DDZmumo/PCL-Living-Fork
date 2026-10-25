Imports System.IO
Imports System.IO.Compression
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading

'存档备份只保存在本机个人资料中，不放入实例或整合包导出目录。
Friend NotInheritable Class WorldBackups
    Private Shared ReadOnly Gate As New Object
    Private Shared Busy As Integer
    Private Const MaxFiles As Integer = 500000
    Private Const MaxManifest As Long = 64L * 1024 * 1024
    Friend Shared Property TestRoot As String
    Friend Shared Event Changed()
    Friend Shared ReadOnly Property IsBusy As Boolean
        Get
            Return Volatile.Read(Busy) <> 0 OrElse WorldBackupsUi.IsBusy
        End Get
    End Property
    Private NotInheritable Class BusyScope
        Implements IDisposable
        Friend Sub New()
            Interlocked.Increment(Busy)
        End Sub
        Public Sub Dispose() Implements IDisposable.Dispose
            Interlocked.Decrement(Busy)
        End Sub
    End Class

    Private Shared Function Full(Value As String) As String
        If String.IsNullOrWhiteSpace(Value) Then Throw New IOException("存档目录为空。")
        Dim Result = Path.GetFullPath(PathUtils.RemoveExtendedPrefix(Value))
        Return If(Result.Length = Path.GetPathRoot(Result).Length, Result, Result.TrimEnd("\"c, "/"c))
    End Function
    '同时检查根目录及祖先，避免目录本身为联接时绕过 EnsureWithin 的边界检查。
    Private Shared Sub NoLinks(Value As String)
        Dim Current = Path.GetFullPath(Value)
        Do While Not String.IsNullOrEmpty(Current)
            If (File.Exists(Current) OrElse Directory.Exists(Current)) AndAlso (File.GetAttributes(Current) And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("存档备份不支持链接文件或链接目录。")
            Dim Parent = Path.GetDirectoryName(Current.TrimEnd("\"c, "/"c))
            If String.Equals(Parent, Current, StringComparison.OrdinalIgnoreCase) Then Exit Do
            Current = Parent
        Loop
    End Sub
    Private Shared Function Safe(Root As String, Relative As String) As String
        If String.IsNullOrEmpty(Relative) OrElse Path.IsPathRooted(Relative) Then Throw New IOException("备份包含无效的相对路径。")
        For Each Part In Relative.Replace("\", "/").Split("/"c)
            SafePart(Part)
        Next
        Dim Result = Path.GetFullPath(Path.Combine(Root, Relative.Replace("/"c, Path.DirectorySeparatorChar)))
        If Not Result.StartsWith(Full(Root).TrimEnd("\"c, "/"c) & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) Then Throw New IOException("备份路径超出了当前目录。")
        NoLinks(Result)
        Return Result
    End Function
    Private Shared Sub SafePart(Value As String)
        If String.IsNullOrWhiteSpace(Value) OrElse Value = "." OrElse Value = ".." OrElse Value <> Value.TrimEnd() OrElse Value.EndsWith(".") OrElse Value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 OrElse Regex.IsMatch(Value, "\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase) Then Throw New IOException("备份包含无效的文件或存档名称。")
    End Sub
    Private Shared Sub WorldName(Value As String)
        SafePart(Value)
        If Path.GetFileName(Value) <> Value OrElse Value.StartsWith(".pcl-", StringComparison.OrdinalIgnoreCase) Then Throw New IOException("存档文件夹名称无效。")
    End Sub
    Private Shared Sub PointId(Value As String)
        If Value Is Nothing OrElse Not Regex.IsMatch(Value, "\A[a-f0-9]{32}\z") Then Throw New IOException("存档备份编号无效。")
    End Sub
    Private Shared Function DigestName(Value As String) As String
        Using Algorithm = SHA256.Create()
            Return BitConverter.ToString(Algorithm.ComputeHash(Encoding.UTF8.GetBytes(Value))).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Friend Shared Function Store(GameFolder As String) As String
        Dim Root = If(TestRoot, LocalGameData.Address("world-backups"))
        NoLinks(Root)
        Return Safe(Root, DigestName(Full(GameFolder).ToUpperInvariant()))
    End Function
    Private Shared Function PointFolder(GameFolder As String, Id As String) As String
        PointId(Id)
        Return Safe(Store(GameFolder), "points/" & Id)
    End Function
    Private Shared Function WorldFolder(GameFolder As String, Identifier As String) As String
        WorldName(Identifier)
        Return Safe(Full(GameFolder), "saves/" & Identifier)
    End Function
    Private Shared Function Retain(Value As Integer) As Integer
        If Value < 1 OrElse Value > 50 Then Throw New IOException("每个世界保留的备份份数必须为 1 到 50。")
        Return Value
    End Function
    Private Shared Sub WriteJson(FileName As String, Value As JObject)
        NoLinks(FileName)
        Directory.CreateDirectory(Path.GetDirectoryName(FileName))
        Dim Temporary = FileName & ".pcl-" & Guid.NewGuid().ToString("N")
        Try
            Dim Bytes = New UTF8Encoding(False).GetBytes(Value.ToString(Newtonsoft.Json.Formatting.None))
            Using Output As New FileStream(Temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                Output.Write(Bytes, 0, Bytes.Length)
                Output.Flush(True)
            End Using
            NoLinks(FileName)
            If File.Exists(FileName) Then File.Replace(Temporary, FileName, Nothing) Else File.Move(Temporary, FileName)
        Finally
            If File.Exists(Temporary) Then File.Delete(Temporary)
        End Try
    End Sub
    Private Shared Function ReadJson(FileName As String) As JObject
        NoLinks(FileName)
        If Not File.Exists(FileName) OrElse New FileInfo(FileName).Length > MaxManifest Then Throw New IOException("备份记录已丢失或过大。")
        Using Input As New StreamReader(FileName, New UTF8Encoding(False, True))
            Return JObject.Parse(Input.ReadToEnd())
        End Using
    End Function
    Private Shared Function Alive(Game As Process) As Boolean
        If Game Is Nothing Then Return True
        Try
            Return Not Game.HasExited
        Catch
            Return True
        End Try
    End Function
    '不能使用 HasRunningMinecraft：退出回调时它还可能包含当前已退出的 Watcher。
    Friend Shared Function HasActiveGame(GameFolder As String, Optional ExitedProcess As Process = Nothing) As Boolean
        Dim Target = Full(GameFolder)
        If ExitedProcess IsNot Nothing AndAlso Alive(ExitedProcess) Then Return True
        For Each Watch In McWatcherList.ToArray()
            If Watch Is Nothing Then Continue For
            If Watch.GameProcess IsNot Nothing AndAlso Not Alive(Watch.GameProcess) Then Continue For
            If Watch.GameProcess Is Nothing AndAlso (Watch.State = Watcher.MinecraftState.Ended OrElse Watch.State = Watcher.MinecraftState.Canceled) Then Continue For
            Try
                If Watch.Instance Is Nothing OrElse String.Equals(Full(Watch.Instance.PathIndie), Target, StringComparison.OrdinalIgnoreCase) Then Return True
            Catch
                Return True
            End Try
        Next
        For Each Name In {"world-backup-sessions", "quickplay-sessions", "options-sessions"}
            Dim Folder = LocalGameData.Address(Name)
            NoLinks(Folder)
            If Not Directory.Exists(Folder) Then Continue For
            For Each FileName In Directory.EnumerateFiles(Folder, "*.json")
                Try
                    Dim Entry = ReadJson(FileName)
                    If String.Equals(Full(Entry("gameFolder").ToString()), Target, StringComparison.OrdinalIgnoreCase) AndAlso LocalGameData.ProcessMatches(Entry("pid").ToObject(Of Integer)(), Entry("started").ToObject(Of Long)()) Then Return True
                Catch ex As Exception
                    Logger.Warn(ex, "无法确认存档所属游戏的运行状态")
                    Return True
                End Try
            Next
        Next
        Return False
    End Function
    Private Shared Sub RequireStopped(GameFolder As String, Optional ExitedProcess As Process = Nothing)
        If HasActiveGame(GameFolder, ExitedProcess) Then Throw New IOException("请先关闭使用此存档目录的游戏，再备份或恢复存档。")
        NoLinks(GameFolder)
    End Sub
    Private Shared Function SessionLock(World As String) As FileStream
        Dim FileName = Safe(World, "session.lock")
        If Not File.Exists(FileName) Then Return Nothing
        Try
            Return New FileStream(FileName, FileMode.Open, FileAccess.Read, FileShare.None)
        Catch ex As IOException
            Throw New IOException("该存档仍被其他程序使用，请关闭游戏后重试。", ex)
        End Try
    End Function
    Private Shared Function ScanTree(Folder As String, Token As CancellationToken, Optional Directories As List(Of String) = Nothing) As Dictionary(Of String, FileInfo)
        NoLinks(Folder)
        Dim Result As New Dictionary(Of String, FileInfo)(StringComparer.OrdinalIgnoreCase)
        Dim Pending As New Stack(Of String)
        Pending.Push(Folder)
        Do While Pending.Count > 0
            Token.ThrowIfCancellationRequested()
            Dim Current = Pending.Pop()
            NoLinks(Current)
            For Each Child In New DirectoryInfo(Current).GetFileSystemInfos()
                Token.ThrowIfCancellationRequested()
                Dim Relative = Child.FullName.Substring(Full(Folder).Length + 1).Replace("\", "/")
                Safe(Folder, Relative)
                If (Child.Attributes And FileAttributes.Directory) <> 0 Then
                    If Directories IsNot Nothing Then
                        If Directories.Count >= MaxFiles Then Throw New IOException("该存档包含过多文件夹，无法安全记录备份。")
                        Directories.Add(Relative)
                    End If
                    Pending.Push(Child.FullName)
                ElseIf Not String.Equals(Relative, "session.lock", StringComparison.OrdinalIgnoreCase) Then
                    If Result.Count >= MaxFiles Then Throw New IOException("该存档包含过多文件，无法安全记录备份。")
                    Result.Add(Relative, DirectCast(Child, FileInfo))
                End If
            Next
        Loop
        Return Result
    End Function
    Private Shared Function CopyHash(Input As Stream, Output As Stream, Token As CancellationToken, Optional ExpectedLength As Long = -1) As Tuple(Of Long, String)
        Dim Buffer(131071) As Byte, Total As Long = 0
        Using Algorithm = SHA256.Create()
            Do
                Token.ThrowIfCancellationRequested()
                Dim Count = Input.Read(Buffer, 0, Buffer.Length)
                If Count = 0 Then Exit Do
                Total += Count
                If ExpectedLength >= 0 AndAlso Total > ExpectedLength Then Throw New IOException("备份文件长度与记录不一致。")
                Algorithm.TransformBlock(Buffer, 0, Count, Buffer, 0)
                If Output IsNot Nothing Then Output.Write(Buffer, 0, Count)
            Loop
            Algorithm.TransformFinalBlock(New Byte() {}, 0, 0)
            If ExpectedLength >= 0 AndAlso Total <> ExpectedLength Then Throw New IOException("备份文件长度与记录不一致。")
            Return Tuple.Create(Total, Convert.ToBase64String(Algorithm.Hash))
        End Using
    End Function
    Private Shared Function Metadata(GameFolder As String, Id As String) As JObject
        Dim Folder = PointFolder(GameFolder, Id), Point = ReadJson(Safe(Folder, "manifest.json"))
        If CInt(Point("schema")) <> 1 OrElse Point("id")?.ToString() <> Id OrElse Not String.Equals(Full(Point("gameFolder").ToString()), Full(GameFolder), StringComparison.OrdinalIgnoreCase) Then Throw New IOException("备份记录与当前游戏目录不匹配。")
        WorldName(Point("world").ToString())
        Dim Created = Point("created").ToObject(Of Date)().ToUniversalTime()
        Dim Files = TryCast(Point("files"), JObject)
        If Files Is Nothing OrElse Files.Count > MaxFiles Then Throw New IOException("备份文件清单无效。")
        Dim Names As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase), Total As Long = 0
        For Each Pair In Files.Properties()
            Safe(Folder, Pair.Name)
            If Pair.Name.Contains("\") OrElse Not Names.Add(Pair.Name) OrElse String.Equals(Pair.Name, "session.lock", StringComparison.OrdinalIgnoreCase) Then Throw New IOException("备份含有重复或无效文件。")
            Dim Length = Pair.Value("length").ToObject(Of Long)()
            If Length < 0 OrElse Long.MaxValue - Total < Length OrElse Convert.FromBase64String(Pair.Value("sha256").ToString()).Length <> 32 Then Throw New IOException("备份文件校验记录无效。")
            Total += Length
        Next
        Dim Directories = TryCast(Point("directories"), JArray)
        If Directories Is Nothing OrElse Directories.Count > MaxFiles Then Throw New IOException("备份目录清单无效。")
        For Each Child In Directories
            Dim Relative = Child.ToString()
            Safe(Folder, Relative)
            If Relative.Contains("\") OrElse Not Names.Add(Relative) Then Throw New IOException("备份含有重复的目录或文件。")
        Next
        If Point("bytes").ToObject(Of Long)() <> Total Then Throw New IOException("备份总大小与文件清单不一致。")
        NoLinks(Safe(Folder, "world.zip"))
        If Not File.Exists(Safe(Folder, "world.zip")) Then Throw New IOException("备份数据已丢失。")
        Point("healthy") = False '沿用 PCL 的通用记录行，不把备份标记成环境正常运行记录。
        Point("description") = Point("world").ToString() & " · " & Point("reason").ToString() & " · " & StringUtils.FormatByteSize(Total)
        Return Point
    End Function
    Friend Shared Function Points(GameFolder As String) As List(Of JObject)
        SyncLock Gate
            Dim Folder = Safe(Store(GameFolder), "points"), Result As New List(Of JObject)
            If Not Directory.Exists(Folder) Then Return Result
            For Each Child In Directory.EnumerateDirectories(Folder)
                Dim Id = Path.GetFileName(Child)
                If Not Regex.IsMatch(Id, "\A[a-f0-9]{32}\z") Then Continue For
                Try
                    Result.Add(Metadata(GameFolder, Id))
                Catch ex As Exception
                    Logger.Warn(ex, "忽略不可读取的存档备份记录：" & Id)
                End Try
            Next
            Return Result.OrderByDescending(Function(Item) Item("created").ToObject(Of Date)()).ToList()
        End SyncLock
    End Function
    Private Shared Sub ReadArchive(Folder As String, Point As JObject, Token As CancellationToken, Optional Destination As String = Nothing)
        Dim Files = DirectCast(Point("files"), JObject), Seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        If Destination IsNot Nothing Then
            For Each Child In DirectCast(Point("directories"), JArray)
                Token.ThrowIfCancellationRequested()
                Directory.CreateDirectory(Safe(Destination, Child.ToString()))
            Next
        End If
        Using Stream As New FileStream(Safe(Folder, "world.zip"), FileMode.Open, FileAccess.Read, FileShare.Read), Archive As New ZipArchive(Stream, ZipArchiveMode.Read)
            If Archive.Entries.Count <> Files.Count Then Throw New IOException("备份压缩包与文件清单不一致。")
            For Each Entry In Archive.Entries
                Token.ThrowIfCancellationRequested()
                Safe(Folder, Entry.FullName)
                Dim UnixType = (Entry.ExternalAttributes >> 16) And &HF000
                If Entry.FullName.Contains("\") OrElse Not Seen.Add(Entry.FullName) OrElse UnixType = &HA000 OrElse (Entry.ExternalAttributes And CInt(FileAttributes.ReparsePoint)) <> 0 Then Throw New IOException("备份包含链接或重复文件。")
                Dim Expected = Files(Entry.FullName)
                If Expected Is Nothing OrElse Entry.Length <> Expected("length").ToObject(Of Long)() Then Throw New IOException("备份包含未记录的文件或大小异常。")
                Using Input = Entry.Open()
                    Dim Actual As Tuple(Of Long, String)
                    If Destination Is Nothing Then
                        Actual = CopyHash(Input, Nothing, Token, Entry.Length)
                    Else
                        Dim FileName = Safe(Destination, Entry.FullName)
                        Directory.CreateDirectory(Path.GetDirectoryName(FileName))
                        Using Output As New FileStream(FileName, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                            Actual = CopyHash(Input, Output, Token, Entry.Length)
                            Output.Flush(True)
                        End Using
                        File.SetLastWriteTimeUtc(FileName, Expected("writeUtc").ToObject(Of Date)().ToUniversalTime())
                    End If
                    If Actual.Item2 <> Expected("sha256").ToString() Then Throw New IOException("备份内容校验失败，尚未替换当前世界。")
                End Using
            Next
        End Using
    End Sub
    Private Shared Function CaptureCore(GameFolder As String, Identifier As String, Reason As String, Token As CancellationToken) As String
        Dim World = WorldFolder(GameFolder, Identifier)
        If Not Directory.Exists(World) Then Throw New IOException("该存档已移动或删除，请刷新列表。")
        Dim Root = Store(GameFolder), Id = Guid.NewGuid().ToString("N")
        Dim Stage = Safe(Root, ".pcl-backup-" & Id), Destination = PointFolder(GameFolder, Id)
        Try
            Token.ThrowIfCancellationRequested()
            Directory.CreateDirectory(Stage)
            Dim Files As New JObject, Total As Long = 0, Directories As New List(Of String)
            Using Held = SessionLock(World)
                Dim Before = ScanTree(World, Token, Directories)
                Using Stream As New FileStream(Safe(Stage, "world.zip"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
                    Using Archive As New ZipArchive(Stream, ZipArchiveMode.Create, True)
                        For Each Pair In Before.OrderBy(Function(Item) Item.Key, StringComparer.OrdinalIgnoreCase)
                            Token.ThrowIfCancellationRequested()
                            Dim FileName = Safe(World, Pair.Key), Stamp = Pair.Value.LastWriteTimeUtc, Length = Pair.Value.Length
                            Dim Entry = Archive.CreateEntry(Pair.Key, CompressionLevel.Optimal)
                            Using Input As New FileStream(FileName, FileMode.Open, FileAccess.Read, FileShare.Read), Output = Entry.Open()
                                Dim Actual = CopyHash(Input, Output, Token, Length)
                                Files.Add(Pair.Key, New JObject(New JProperty("length", Actual.Item1), New JProperty("sha256", Actual.Item2), New JProperty("writeUtc", Stamp)))
                                Total += Actual.Item1
                            End Using
                            Dim After As New FileInfo(FileName)
                            If After.Length <> Length OrElse After.LastWriteTimeUtc <> Stamp Then Throw New IOException("存档在备份期间发生变化，请关闭使用它的程序后重试。")
                        Next
                    End Using
                    Stream.Flush(True)
                End Using
                Dim AfterDirectories As New List(Of String), AfterTree = ScanTree(World, Token, AfterDirectories)
                If Not New HashSet(Of String)(Directories, StringComparer.OrdinalIgnoreCase).SetEquals(AfterDirectories) OrElse AfterTree.Count <> Before.Count OrElse Before.Any(Function(Pair) Not AfterTree.ContainsKey(Pair.Key) OrElse AfterTree(Pair.Key).Length <> Pair.Value.Length OrElse AfterTree(Pair.Key).LastWriteTimeUtc <> Pair.Value.LastWriteTimeUtc) Then Throw New IOException("存档在备份期间发生变化，未保存本次备份。")
            End Using
            Dim Label = Identifier
            Try
                Dim Info = WorldMetadata.ReadDetails(World)
                If Not String.IsNullOrWhiteSpace(Info?.Name) Then Label = Info.Name
            Catch
                '损坏的元数据仍应允许完整保存原始文件，便于后续恢复。
            End Try
            Dim Point As New JObject(New JProperty("schema", 1), New JProperty("id", Id), New JProperty("gameFolder", Full(GameFolder)), New JProperty("world", Identifier),
                New JProperty("label", Label), New JProperty("created", Date.UtcNow), New JProperty("reason", Reason), New JProperty("bytes", Total), New JProperty("archiveBytes", New FileInfo(Safe(Stage, "world.zip")).Length), New JProperty("files", Files), New JProperty("directories", New JArray(Directories)))
            WriteJson(Safe(Stage, "manifest.json"), Point)
            ReadArchive(Stage, Point, Token)
            Token.ThrowIfCancellationRequested()
            RequireStopped(GameFolder)
            Directory.CreateDirectory(Path.GetDirectoryName(Destination))
            NoLinks(Destination)
            Directory.Move(Stage, Destination)
            Return Id
        Finally
            If Directory.Exists(Stage) Then DeleteTree(Root, Stage)
        End Try
    End Function
    Private Shared Sub DeleteTree(Root As String, Folder As String)
        Dim Expected = Full(Folder)
        If Not Expected.StartsWith(Full(Root).TrimEnd("\"c, "/"c) & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) Then Throw New IOException("清理目录超出了存档备份范围。")
        NoLinks(Folder)
        '禁止递归删除穿过联接：每一层先检查，再仅删除已枚举的普通文件/目录。
        For Each FileName In Directory.GetFiles(Folder)
            NoLinks(FileName)
            File.Delete(FileName)
        Next
        For Each Child In Directory.GetDirectories(Folder)
            DeleteTree(Root, Child)
        Next
        Directory.Delete(Folder, False)
    End Sub
    Private Shared Sub Prune(GameFolder As String, Identifier As String, Count As Integer)
        For Each Point In Points(GameFolder).Where(Function(Item) String.Equals(Item("world").ToString(), Identifier, StringComparison.OrdinalIgnoreCase)).Skip(Retain(Count))
            Try
                DeleteTree(Store(GameFolder), PointFolder(GameFolder, Point("id").ToString()))
            Catch ex As Exception
                Logger.Warn(ex, "新备份已保存，但清理旧存档备份失败")
            End Try
        Next
    End Sub
    Friend Shared Function CaptureWorld(GameFolder As String, Identifier As String, Reason As String, Retention As Integer, Token As CancellationToken, Optional ExitedProcess As Process = Nothing) As String
        SyncLock Gate
            Using Scope As New BusyScope
                Retain(Retention)
                RequireStopped(GameFolder, ExitedProcess)
                RecoverCore(GameFolder, Token)
                Dim Id = CaptureCore(GameFolder, Identifier, Reason, Token)
                Prune(GameFolder, Identifier, Retention)
                NotifyChanged()
                Return Id
            End Using
        End SyncLock
    End Function
    Friend Shared Function CaptureAll(Instance As McInstance, Reason As String, Token As CancellationToken, Optional ExitedProcess As Process = Nothing) As List(Of String)
        If Instance Is Nothing Then Throw New IOException("请先选择游戏版本。")
        Return CaptureFolder(Instance.PathIndie, Reason, Settings.Get(Of Integer)("VersionWorldBackupRetention", Instance:=Instance), Token, ExitedProcess)
    End Function
    Friend Shared Function CaptureFolder(GameFolder As String, Reason As String, Retention As Integer, Token As CancellationToken, Optional ExitedProcess As Process = Nothing) As List(Of String)
        SyncLock Gate
            Using Scope As New BusyScope
                Retain(Retention)
                RequireStopped(GameFolder, ExitedProcess)
                RecoverCore(GameFolder, Token)
                Dim Root = Safe(GameFolder, "saves"), Result As New List(Of String)
                If Not Directory.Exists(Root) Then Return Result
                For Each Folder In Directory.EnumerateDirectories(Root).OrderBy(Function(Value) Value, StringComparer.OrdinalIgnoreCase)
                    Token.ThrowIfCancellationRequested()
                    Dim Identifier = Path.GetFileName(Folder)
                    If Identifier.StartsWith(".pcl-", StringComparison.OrdinalIgnoreCase) Then Continue For
                    NoLinks(Folder)
                    If Not File.Exists(Safe(Folder, "level.dat")) Then Continue For
                    RequireStopped(GameFolder, ExitedProcess)
                    Result.Add(CaptureCore(GameFolder, Identifier, Reason, Token))
                    Prune(GameFolder, Identifier, Retention)
                Next
                NotifyChanged()
                Return Result
            End Using
        End SyncLock
    End Function
    Private Shared Sub MatchTree(World As String, Point As JObject, Token As CancellationToken)
        Dim Files = DirectCast(Point("files"), JObject), Directories As New List(Of String), Tree = ScanTree(World, Token, Directories)
        If Tree.Count <> Files.Count OrElse Not New HashSet(Of String)(Directories, StringComparer.OrdinalIgnoreCase).SetEquals(DirectCast(Point("directories"), JArray).Select(Function(Item) Item.ToString())) Then Throw New IOException("当前世界已被其他程序修改，停止恢复以保留当前进度。")
        For Each Pair In Tree
            Dim Expected = Files(Pair.Key)
            If Expected Is Nothing Then Throw New IOException("当前世界已变化，停止恢复。")
            Using Input As New FileStream(Safe(World, Pair.Key), FileMode.Open, FileAccess.Read, FileShare.Read)
                If CopyHash(Input, Nothing, Token, Expected("length").ToObject(Of Long)()).Item2 <> Expected("sha256").ToString() Then Throw New IOException("当前世界已被其他程序修改，停止恢复以保留当前进度。")
            End Using
        Next
    End Sub
    Private Shared Function RestorePath(GameFolder As String, Transaction As String, Suffix As String) As String
        PointId(Transaction)
        Return Safe(GameFolder, "saves/.pcl-world-" & Transaction & Suffix)
    End Function
    Private Shared Function JournalPath(GameFolder As String, Transaction As String) As String
        PointId(Transaction)
        Return Safe(Store(GameFolder), "transactions/" & Transaction & ".json")
    End Function
    '中断后仅回滚仍与本次事务一致的内容；无法确认时原样保留所有目录。
    Private Shared Sub RecoverCore(GameFolder As String, Token As CancellationToken)
        Dim Root = Safe(Store(GameFolder), "transactions")
        If Not Directory.Exists(Root) Then Return
        For Each FileName In Directory.GetFiles(Root, "*.json")
            Token.ThrowIfCancellationRequested()
            Dim Transaction = Path.GetFileNameWithoutExtension(FileName)
            PointId(Transaction)
            Dim Journal = ReadJson(FileName)
            If Journal("ownerPid") IsNot Nothing AndAlso Journal("ownerStarted") IsNot Nothing AndAlso
               Journal("ownerPid").ToObject(Of Integer)() <> Process.GetCurrentProcess().Id AndAlso
               LocalGameData.ProcessMatches(Journal("ownerPid").ToObject(Of Integer)(), Journal("ownerStarted").ToObject(Of Long)()) Then
                Throw New IOException("另一个启动器仍在恢复该存档，请等待操作完成后再启动。")
            End If
            If Not String.Equals(Full(Journal("gameFolder").ToString()), Full(GameFolder), StringComparison.OrdinalIgnoreCase) Then Throw New IOException("未完成的恢复记录与当前目录不匹配。")
            Dim World = WorldFolder(GameFolder, Journal("world").ToString()), Stage = RestorePath(GameFolder, Transaction, ".stage"), Previous = RestorePath(GameFolder, Transaction, ".before")
            '恢复日志也要核对外部游戏的存档锁，不能只依赖本启动器的会话。
            Using Held = If(Directory.Exists(World), SessionLock(World), Nothing)
                RequireStopped(GameFolder)
            End Using
            Using Held = If(Directory.Exists(Previous), SessionLock(Previous), Nothing)
                RequireStopped(GameFolder)
            End Using
            If Journal("complete").ToObject(Of Boolean)() Then
                If Not Directory.Exists(World) Then Throw New IOException("恢复事务缺少目标世界，已保留恢复前目录：" & Previous)
                If Directory.Exists(Previous) Then DeleteTree(Safe(GameFolder, "saves"), Previous)
            ElseIf Directory.Exists(Previous) Then
                If Directory.Exists(World) Then
                    MatchTree(World, Metadata(GameFolder, Journal("point").ToString()), Token)
                    If Directory.Exists(Stage) Then Throw New IOException("恢复临时目录状态不一致，已保留所有存档。")
                    Directory.Move(World, Stage)
                End If
                Directory.Move(Previous, World)
            ElseIf Not Journal("existed").ToObject(Of Boolean)() AndAlso Directory.Exists(World) Then
                MatchTree(World, Metadata(GameFolder, Journal("point").ToString()), Token)
                If Directory.Exists(Stage) Then Throw New IOException("恢复临时目录状态不一致，已保留所有存档。")
                Directory.Move(World, Stage)
            End If
            If Directory.Exists(Stage) Then DeleteTree(Safe(GameFolder, "saves"), Stage)
            NoLinks(FileName)
            File.Delete(FileName)
        Next
    End Sub
    Friend Shared Sub RecoverPending(GameFolder As String, Token As CancellationToken)
        SyncLock Gate
            Using Scope As New BusyScope
                RequireStopped(GameFolder)
                RecoverCore(GameFolder, Token)
            End Using
        End SyncLock
    End Sub
    Friend Shared Sub RecoverBeforeLaunch(GameFolder As String, Token As CancellationToken)
        SyncLock Gate
            Dim Transactions = Safe(Store(GameFolder), "transactions")
            If Not Directory.Exists(Transactions) OrElse Not Directory.EnumerateFiles(Transactions, "*.json").Any() Then Return
            RecoverPending(GameFolder, Token)
        End SyncLock
    End Sub
    Friend Shared Function Restore(GameFolder As String, Id As String, Retention As Integer, Token As CancellationToken, Optional BeforeCommit As Action(Of Integer) = Nothing) As String
        SyncLock Gate
            Using Scope As New BusyScope
                Retain(Retention)
                RequireStopped(GameFolder)
                RecoverCore(GameFolder, Token)
                Dim Point = Metadata(GameFolder, Id), Identifier = Point("world").ToString(), World = WorldFolder(GameFolder, Identifier)
                If Not Directory.Exists(GameFolder) Then Throw New IOException("游戏目录已不存在，请先恢复或重新选择目录。")
                Dim Transaction = Guid.NewGuid().ToString("N"), Stage = RestorePath(GameFolder, Transaction, ".stage"), Previous = RestorePath(GameFolder, Transaction, ".before")
                Dim JournalFile = JournalPath(GameFolder, Transaction), Undo As String = Nothing
                Dim Moved As Boolean = False, Installed As Boolean = False, Completed As Boolean = False, JournalWritten As Boolean = False
                Try
                    Token.ThrowIfCancellationRequested()
                    Directory.CreateDirectory(Stage)
                    ReadArchive(PointFolder(GameFolder, Id), Point, Token, Stage)
                    Dim Existed = Directory.Exists(World)
                    If Existed Then Undo = CaptureCore(GameFolder, Identifier, "恢复前自动保存", Token)
                    Dim Journal As New JObject(New JProperty("gameFolder", Full(GameFolder)), New JProperty("world", Identifier), New JProperty("point", Id), New JProperty("undo", Undo), New JProperty("existed", Existed), New JProperty("complete", False))
                    Using Owner = Process.GetCurrentProcess()
                        Journal("ownerPid") = Owner.Id
                        Journal("ownerStarted") = Owner.StartTime.ToUniversalTime().Ticks
                    End Using
                    WriteJson(JournalFile, Journal)
                    JournalWritten = True
                    BeforeCommit?.Invoke(0)
                    Using Held = If(Existed, SessionLock(World), Nothing)
                        RequireStopped(GameFolder)
                        If Existed Then MatchTree(World, Metadata(GameFolder, Undo), Token)
                    End Using
                    'Windows 不允许移动包含本进程打开的文件句柄的目录。
                    '校验锁释放后立即提交；IsBusy 与实例锁阻止启动器在事务中启动游戏。
                    RequireStopped(GameFolder)
                    Token.ThrowIfCancellationRequested()
                    If Existed Then Directory.Move(World, Previous) : Moved = Existed
                    BeforeCommit?.Invoke(1)
                    Token.ThrowIfCancellationRequested()
                    Directory.Move(Stage, World)
                    Installed = True
                    BeforeCommit?.Invoke(2)
                    Token.ThrowIfCancellationRequested()
                    Journal("complete") = True
                    WriteJson(JournalFile, Journal)
                    Completed = True
                Catch ex As Exception
                    Try
                        If Installed Then
                            NoLinks(World)
                            Directory.Move(World, Stage)
                        End If
                        If Moved Then
                            NoLinks(Previous)
                            Directory.Move(Previous, World)
                        End If
                        If JournalWritten AndAlso File.Exists(JournalFile) Then File.Delete(JournalFile)
                        JournalWritten = False
                    Catch RollbackError As Exception
                        Throw New IOException("恢复失败且无法自动回滚。原世界和临时数据均已保留，请勿删除 saves 下的 .pcl-world 目录：" & Previous, New AggregateException(ex, RollbackError))
                    End Try
                    Throw
                Finally
                    If Not JournalWritten AndAlso Directory.Exists(Stage) Then DeleteTree(Safe(GameFolder, "saves"), Stage)
                End Try
                If Completed Then
                    '提交后的清理失败保留日志，下一次操作继续清理，不把成功恢复误报为失败。
                    Try
                        RecoverCore(GameFolder, CancellationToken.None)
                    Catch ex As Exception
                        Logger.Warn(ex, "存档已恢复，暂未清理恢复事务目录")
                    End Try
                    If Undo IsNot Nothing Then Prune(GameFolder, Identifier, Retention)
                    NotifyChanged()
                End If
                Return Undo
            End Using
        End SyncLock
    End Function
    Friend Shared Function DeletePoints(GameFolder As String, Ids As IEnumerable(Of String)) As String
        SyncLock Gate
            Using Scope As New BusyScope
                Dim Selected = Ids.Distinct(StringComparer.Ordinal).ToList(), Root = Store(GameFolder)
                If Selected.Count = 0 Then Return Nothing
                '先校验整个批次，不能因为后一个编号无效而部分删除。
                For Each Id In Selected
                    Metadata(GameFolder, Id)
                Next
                Dim Deleted = Safe(Root, "deleted/" & Guid.NewGuid().ToString("N")), Moved As New List(Of String)
                Try
                    Directory.CreateDirectory(Deleted)
                    For Each Id In Selected
                        Directory.Move(PointFolder(GameFolder, Id), Safe(Deleted, Id))
                        Moved.Add(Id)
                    Next
                Catch
                    For Each Id In Moved.AsEnumerable().Reverse()
                        Directory.Move(Safe(Deleted, Id), PointFolder(GameFolder, Id))
                    Next
                    Throw
                End Try
                NotifyChanged()
                Return Deleted
            End Using
        End SyncLock
    End Function
    Friend Shared Sub OpenFolder(GameFolder As String)
        Dim Folder = Store(GameFolder)
        If Directory.Exists(Folder) Then OpenExplorer(Folder) Else Hint("此游戏目录还没有存档备份。")
    End Sub
    Friend Shared Sub NotifyChanged()
        If ChangedEvent IsNot Nothing Then RunInUi(Sub() RaiseEvent Changed())
    End Sub

    '即使个人设置与 Quick Play 记录关闭，也记录由本启动器启动的活动游戏。
    Friend Shared Sub Track(Instance As McInstance, Game As Process)
        If Instance Is Nothing OrElse Game Is Nothing Then Return
        Dim Name = Game.Id.ToString() & "-" & Game.StartTime.ToUniversalTime().Ticks.ToString() & ".json"
        WriteJson(LocalGameData.Address("world-backup-sessions/" & Name), New JObject(New JProperty("gameFolder", Full(Instance.PathIndie)), New JProperty("versionPath", Instance.PathVersion), New JProperty("pid", Game.Id), New JProperty("started", Game.StartTime.ToUniversalTime().Ticks)))
    End Sub
    Friend Shared Sub OnProcessStopped(Instance As McInstance, Game As Process, NormalExit As Boolean)
        If Instance Is Nothing OrElse Game Is Nothing OrElse Alive(Game) Then Return
        Try
            If NormalExit AndAlso Game.ExitCode = 0 AndAlso Settings.Get(Of Boolean)("VersionWorldBackupAutoEnabled", Instance:=Instance) Then
                Dim Folder = LocalGameData.Address("world-backup-sessions"), GameFolder = Instance.PathIndie
                NoLinks(Folder)
                'PID 会被 Windows 重用，不能从所有同 PID 的旧记录中任意选择目录。
                Dim SessionFile = Safe(Folder, Game.Id.ToString() & "-" & Game.StartTime.ToUniversalTime().Ticks.ToString() & ".json")
                If File.Exists(SessionFile) Then GameFolder = ReadJson(SessionFile)("gameFolder").ToString()
                CaptureFolder(GameFolder, "正常退出后", Settings.Get(Of Integer)("VersionWorldBackupRetention", Instance:=Instance), CancellationToken.None, Game)
            End If
        Catch ex As Exception
            Logger.Warn(ex, "游戏退出后的存档自动备份失败")
            RunInUi(Sub() Hint("存档自动备份失败：" & ex.Message, HintType.Red))
        Finally
            Dim Folder = LocalGameData.Address("world-backup-sessions")
            Try
                NoLinks(Folder)
                If Directory.Exists(Folder) Then
                    For Each FileName In Directory.GetFiles(Folder, Game.Id.ToString() & "-*.json")
                        Dim Entry = ReadJson(FileName)
                        If Not LocalGameData.ProcessMatches(Entry("pid").ToObject(Of Integer)(), Entry("started").ToObject(Of Long)()) Then File.Delete(FileName)
                    Next
                End If
            Catch ex As Exception
                Logger.Warn(ex, "清理已退出的游戏备份会话失败")
            End Try
        End Try
    End Sub
    Friend Shared Sub OnNormalExit(Instance As McInstance, Game As Process)
        OnProcessStopped(Instance, Game, True)
    End Sub
    Friend Shared Function BeforeMinecraftChange(Instance As McInstance, OldMinecraft As String, NewMinecraft As String, Token As CancellationToken) As List(Of String)
        If Instance Is Nothing OrElse String.IsNullOrWhiteSpace(OldMinecraft) OrElse String.Equals(OldMinecraft, NewMinecraft, StringComparison.OrdinalIgnoreCase) OrElse Not Settings.Get(Of Boolean)("VersionWorldBackupAutoEnabled", Instance:=Instance) Then Return New List(Of String)
        Return CaptureAll(Instance, "更新 Minecraft 前（" & OldMinecraft & " → " & NewMinecraft & "）", Token)
    End Function
End Class

Friend NotInheritable Class WorldBackupsUi
    Private Shared Busy As Integer
    Friend Shared ReadOnly Property IsBusy As Boolean
        Get
            Return Volatile.Read(Busy) <> 0
        End Get
    End Property
    Friend Shared Sub Start(Instance As McInstance, Optional RestoreId As String = Nothing, Optional DeleteIds As IEnumerable(Of String) = Nothing, Optional WorldIdentifier As String = Nothing)
        If Instance Is Nothing OrElse WorldBackups.IsBusy OrElse WorldBackups.HasActiveGame(Instance.PathIndie) OrElse McLaunchLoader.State = LoadState.Loading OrElse ModpackUpdateFlow.IsUpdating OrElse EnvironmentHistoryUi.IsBusy OrElse McInstanceLock.IsAnyLocked OrElse PageInstanceMod.UpdatingInstanceModFolders.Any() Then
            Hint("请先关闭游戏并等待版本任务完成。", HintType.Red)
            Return
        End If
        If Interlocked.CompareExchange(Busy, 1, 0) <> 0 Then Return
        Dim Removing = DeleteIds IsNot Nothing, Ids = DeleteIds?.ToArray(), Restoring = RestoreId IsNot Nothing
        Dim StepLoader As New LoaderTask(Of String, String)(If(Removing, "移除存档备份", If(Restoring, "恢复世界", "备份世界")),
            Sub(Task)
                Dim Token = Task.CreateCancellationToken()
                If Removing Then
                    If MyMsgBox($"移除选中的 {Ids.Length} 份存档备份？{vbCrLf}备份将移动到本机备份目录的 deleted 文件夹。", "移除存档备份", "移除", "取消", IsWarn:=True) <> 1 Then Throw New OperationCanceledException()
                    Token.ThrowIfCancellationRequested()
                    Task.Output = WorldBackups.DeletePoints(Instance.PathIndie, Ids)
                ElseIf Restoring Then
                    Dim Point = WorldBackups.Points(Instance.PathIndie).FirstOrDefault(Function(Item) Item("id").ToString() = RestoreId)
                    If Point Is Nothing Then Throw New IOException("该备份已不存在，请刷新列表。")
                    Dim Text = $"将 {Point("label")} 恢复到 {Point("created").ToObject(Of Date)().ToLocalTime():yyyy-MM-dd HH:mm:ss}。{vbCrLf}{Point("description")}{vbCrLf}{vbCrLf}恢复前会自动备份当前世界。"
                    If Not PathUtils.IsSame(Instance.PathVersion, Instance.PathIndie) Then Text &= vbCrLf & "此版本使用共用目录，恢复结果也会影响使用该存档目录的其他版本。"
                    If MyMsgBox(Text, "恢复存档备份", "恢复", "取消", IsWarn:=True) <> 1 Then Throw New OperationCanceledException()
                    Task.Output = WorldBackups.Restore(Instance.PathIndie, RestoreId, Settings.Get(Of Integer)("VersionWorldBackupRetention", Instance:=Instance), Token)
                Else
                    Dim Saved As List(Of String)
                    If WorldIdentifier Is Nothing Then
                        Saved = WorldBackups.CaptureAll(Instance, "手动备份", Token)
                    Else
                        Saved = New List(Of String) From {WorldBackups.CaptureWorld(Instance.PathIndie, WorldIdentifier, "手动备份", Settings.Get(Of Integer)("VersionWorldBackupRetention", Instance:=Instance), Token)}
                    End If
                    Task.Output = Saved.Count.ToString()
                    If Saved.Count = 0 Then RunInUi(Sub() Hint("此游戏目录没有可备份的存档。"))
                End If
            End Sub)
        Dim Loader As New LoaderCombo(Of String)(If(Removing, "移除备份：", If(Restoring, "恢复世界：", "备份世界：")) & Instance.Name, {StepLoader}) With {.OnStateChanged = AddressOf LoaderStateChangedHintOnly}
        Dim Worker As New RedoableWorker(Sub() Loader.WaitForExit(Instance.PathVersion))
        If Not McInstanceLock.TryAcquire(Worker, Instance.PathVersion) Then
            Interlocked.Exchange(Busy, 0)
            Return
        End If
        AddHandler Worker.Stopped, Sub()
                                      Interlocked.Exchange(Busy, 0)
                                      WorldBackups.NotifyChanged()
                                  End Sub
        Worker.Start()
        LoaderTaskbarAdd(Loader)
        FrmMain.BtnExtraDownload.ShowRefresh()
    End Sub
End Class
