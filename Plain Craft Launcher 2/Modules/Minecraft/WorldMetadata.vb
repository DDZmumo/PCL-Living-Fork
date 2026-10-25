Imports System.IO
Imports System.IO.Compression
Imports System.Text

Friend NotInheritable Class WorldMetadata
    Friend Name As String
    Friend LastPlayedUtc As Date
    Friend VersionName As String
    Friend DataVersion As Integer?
    Friend GameMode As Integer?
    Friend Difficulty As Integer?
    Friend Hardcore As Boolean?
    Friend AllowCommands As Boolean?
    Friend WasModded As Boolean?
    Friend Snapshot As Boolean?
    Friend Seed As Long?
    Friend SpawnX As Integer?, SpawnY As Integer?, SpawnZ As Integer?
    Friend DayTime As Long?
    Friend Time As Long?
    Private Input As Stream
    Private Budget As Integer = 16 * 1024 * 1024

    Friend Shared Function Read(Folder As String) As WorldMetadata
        Return ReadCore(Folder, False)
    End Function
    Friend Shared Function ReadDetails(Folder As String) As WorldMetadata
        Return ReadCore(Folder, True)
    End Function
    Private Shared Function ReadCore(Folder As String, Details As Boolean) As WorldMetadata
        Dim FileName = Path.Combine(Folder, "level.dat")
        If Not File.Exists(FileName) OrElse New FileInfo(FileName).Length > 8 * 1024 * 1024 Then Return Nothing
        InstanceResourceStore.EnsureWithin(Folder, FileName)
        Using File As New FileStream(FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete), Uncompressed As New GZipStream(File, CompressionMode.Decompress)
            Dim Result As New WorldMetadata With {.Input = Uncompressed}
            If Result.ByteValue() <> 10 Then Return Nothing
            Result.StringValue()
            Do
                Dim Kind = Result.ByteValue()
                If Kind = 0 Then Exit Do
                Dim Key = Result.StringValue()
                If Kind = 10 AndAlso Key = "Data" Then
                    Result.ReadData(Details)
                    Return If(Not Details AndAlso Result.LastPlayedUtc = Date.MinValue, Nothing, Result)
                End If
                Result.Skip(Kind, 0)
            Loop
        End Using
        Return Nothing
    End Function
    Private Sub ReadData(Details As Boolean)
        Do
            Dim Kind = ByteValue()
            If Kind = 0 Then Return
            Dim Key = StringValue()
            If Key = "LevelName" AndAlso Kind = 8 Then
                Name = StringValue()
            ElseIf Key = "LastPlayed" AndAlso Kind = 4 Then
                Dim Timestamp = LongValue()
                If Timestamp > 0 Then LastPlayedUtc = DateTimeOffset.FromUnixTimeMilliseconds(Timestamp).UtcDateTime
            ElseIf Details AndAlso Kind = 3 AndAlso {"GameType", "Difficulty", "DataVersion", "SpawnX", "SpawnY", "SpawnZ"}.Contains(Key) Then
                Dim Number = IntValue()
                Select Case Key
                    Case "GameType" : GameMode = Number
                    Case "Difficulty" : Difficulty = Number
                    Case "DataVersion" : DataVersion = Number
                    Case "SpawnX" : SpawnX = Number
                    Case "SpawnY" : SpawnY = Number
                    Case "SpawnZ" : SpawnZ = Number
                End Select
            ElseIf Details AndAlso Kind = 1 AndAlso {"hardcore", "allowCommands", "WasModded", "Difficulty"}.Contains(Key) Then
                Dim Number = ByteValue()
                Select Case Key
                    Case "hardcore" : Hardcore = Number <> 0
                    Case "allowCommands" : AllowCommands = Number <> 0
                    Case "WasModded" : WasModded = Number <> 0
                    Case "Difficulty" : Difficulty = Number
                End Select
            ElseIf Details AndAlso Kind = 4 AndAlso {"RandomSeed", "Time", "DayTime"}.Contains(Key) Then
                Dim Number = LongValue()
                Select Case Key
                    Case "RandomSeed" : If Not Seed.HasValue Then Seed = Number
                    Case "Time" : Time = Number
                    Case "DayTime" : DayTime = Number
                End Select
            ElseIf Details AndAlso Kind = 10 AndAlso (Key = "Version" OrElse Key = "WorldGenSettings") Then
                ReadExtra(Key)
            Else
                Skip(Kind, 0)
            End If
            If Not Details AndAlso Name IsNot Nothing AndAlso LastPlayedUtc <> Date.MinValue Then Return
        Loop
    End Sub
    Private Sub ReadExtra(Section As String)
        Do
            Dim Kind = ByteValue()
            If Kind = 0 Then Return
            Dim Key = StringValue()
            If Section = "Version" AndAlso Key = "Name" AndAlso Kind = 8 Then
                VersionName = StringValue()
            ElseIf Section = "Version" AndAlso Key = "Snapshot" AndAlso Kind = 1 Then
                Snapshot = ByteValue() <> 0
            ElseIf Section = "WorldGenSettings" AndAlso Key = "seed" AndAlso Kind = 4 Then
                Seed = LongValue()
            Else
                Skip(Kind, 1)
            End If
        Loop
    End Sub
    Friend Shared Function Describe(Folder As String) As String
        Dim Lines As New List(Of String) From {"存档文件夹：" & New DirectoryInfo(Folder).Name}
        Try
            Dim Data = ReadDetails(Folder)
            If Data Is Nothing Then
                Lines.Add("未找到可读取的 level.dat 元数据。")
            Else
                If Not String.IsNullOrWhiteSpace(Data.Name) Then Lines.Insert(0, "世界名称：" & Data.Name)
                If Not String.IsNullOrWhiteSpace(Data.VersionName) Then Lines.Add("游戏版本：" & Data.VersionName & If(Data.Snapshot.GetValueOrDefault(), "（快照）", ""))
                If Data.DataVersion.HasValue Then Lines.Add("数据版本：" & Data.DataVersion.Value)
                If Data.LastPlayedUtc <> Date.MinValue Then Lines.Add("最后游玩：" & Data.LastPlayedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
                If Data.GameMode.HasValue Then
                    Dim Names = {"生存", "创造", "冒险", "旁观"}
                    Lines.Add("游戏模式：" & If(Data.GameMode.Value >= 0 AndAlso Data.GameMode.Value < Names.Length, Names(Data.GameMode.Value), "未知（" & Data.GameMode.Value & "）"))
                End If
                If Data.Difficulty.HasValue Then
                    Dim Names = {"和平", "简单", "普通", "困难"}
                    Lines.Add("难度：" & If(Data.Difficulty.Value >= 0 AndAlso Data.Difficulty.Value < Names.Length, Names(Data.Difficulty.Value), "未知（" & Data.Difficulty.Value & "）"))
                End If
                If Data.Hardcore.HasValue Then Lines.Add("极限模式：" & If(Data.Hardcore.Value, "开启", "关闭"))
                If Data.AllowCommands.HasValue Then Lines.Add("允许作弊：" & If(Data.AllowCommands.Value, "开启", "关闭"))
                If Data.WasModded.HasValue Then Lines.Add("模组环境记录：" & If(Data.WasModded.Value, "使用过", "未标记"))
                If Data.Seed.HasValue Then Lines.Add("世界种子：" & Data.Seed.Value)
                If Data.SpawnX.HasValue AndAlso Data.SpawnY.HasValue AndAlso Data.SpawnZ.HasValue Then Lines.Add($"世界出生点：{Data.SpawnX.Value}, {Data.SpawnY.Value}, {Data.SpawnZ.Value}")
                If Data.DayTime.HasValue AndAlso Data.DayTime.Value >= 0 Then Lines.Add("游戏日：第 " & (Data.DayTime.Value \ 24000) & " 天")
                If Data.Time.HasValue Then Lines.Add("世界累计刻数：" & Data.Time.Value)
            End If
        Catch ex As Exception
            Logger.Warn(ex, "读取本地存档详情失败")
            Lines.Add("无法读取 level.dat；文件可能正在写入、已损坏或格式不受支持。")
        End Try
        Lines.Add(vbCrLf & "位置：" & Path.GetFullPath(Folder))
        Return String.Join(vbCrLf, Lines)
    End Function
    Private Function ByteValue() As Integer
        Budget -= 1
        If Budget < 0 Then Throw New InvalidDataException("存档元数据超过读取限制。")
        Dim Value = Input.ReadByte()
        If Value < 0 Then Throw New EndOfStreamException
        Return Value
    End Function
    Private Function IntValue() As Integer
        Dim Value As UInteger = 0
        For Index = 0 To 3
            Value = (Value << 8) Or CUInt(ByteValue())
        Next
        Return BitConverter.ToInt32(BitConverter.GetBytes(Value), 0)
    End Function
    Private Function LongValue() As Long
        Dim Value As ULong = 0
        For Index = 0 To 7
            Value = (Value << 8) Or CULng(ByteValue())
        Next
        Return BitConverter.ToInt64(BitConverter.GetBytes(Value), 0)
    End Function
    Private Function StringValue() As String
        Dim Length = (ByteValue() << 8) Or ByteValue()
        Dim Bytes(Math.Max(0, Length - 1)) As Byte
        For Index = 0 To Length - 1
            Bytes(Index) = CByte(ByteValue())
        Next
        Return If(Length = 0, "", Encoding.UTF8.GetString(Bytes))
    End Function
    Private Sub Discard(Count As Long)
        If Count < 0 OrElse Count > Budget Then Throw New InvalidDataException("存档元数据长度无效。")
        For Index As Long = 1 To Count
            ByteValue()
        Next
    End Sub
    Private Sub Skip(Kind As Integer, Depth As Integer)
        If Depth > 64 Then Throw New InvalidDataException("存档元数据层级过深。")
        Select Case Kind
            Case 0 : Return
            Case 1 : Discard(1)
            Case 2 : Discard(2)
            Case 3, 5 : Discard(4)
            Case 4, 6 : Discard(8)
            Case 7 : Discard(IntValue())
            Case 8 : StringValue()
            Case 9
                Dim Element = ByteValue(), Count = IntValue()
                If Count < 0 OrElse Count > 100000 Then Throw New InvalidDataException("存档元数据列表长度无效。")
                For Index = 1 To Count
                    Skip(Element, Depth + 1)
                Next
            Case 10
                Do
                    Dim Element = ByteValue()
                    If Element = 0 Then Exit Do
                    StringValue()
                    Skip(Element, Depth + 1)
                Loop
            Case 11 : Discard(CLng(IntValue()) * 4)
            Case 12 : Discard(CLng(IntValue()) * 8)
            Case Else : Throw New InvalidDataException("未知的存档元数据类型。")
        End Select
    End Sub
End Class
