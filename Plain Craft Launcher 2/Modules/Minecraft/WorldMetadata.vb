Imports System.IO
Imports System.IO.Compression
Imports System.Text

Friend NotInheritable Class WorldMetadata
    Friend Name As String
    Friend LastPlayedUtc As Date
    Private Input As Stream
    Private Budget As Integer = 16 * 1024 * 1024

    Friend Shared Function Read(Folder As String) As WorldMetadata
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
                    Result.ReadData()
                    Return If(Result.LastPlayedUtc = Date.MinValue, Nothing, Result)
                End If
                Result.Skip(Kind, 0)
            Loop
        End Using
        Return Nothing
    End Function
    Private Sub ReadData()
        Do
            Dim Kind = ByteValue()
            If Kind = 0 Then Return
            Dim Key = StringValue()
            If Key = "LevelName" AndAlso Kind = 8 Then
                Name = StringValue()
            ElseIf Key = "LastPlayed" AndAlso Kind = 4 Then
                Dim Timestamp = LongValue()
                If Timestamp > 0 Then LastPlayedUtc = DateTimeOffset.FromUnixTimeMilliseconds(Timestamp).UtcDateTime
            Else
                Skip(Kind, 0)
            End If
            If Name IsNot Nothing AndAlso LastPlayedUtc <> Date.MinValue Then Return
        Loop
    End Sub
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
