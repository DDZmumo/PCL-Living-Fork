Imports System.IO
Imports System.Text
Imports System.Security.Cryptography

'本机个人游戏资料，位于整合包和共享游戏配置之外。
Friend NotInheritable Class LocalGameData
    Friend Shared ReadOnly Gate As New Object
    Friend Shared Property TestRoot As String
    Friend Shared ReadOnly Property Root As String
        Get
            If TestRoot IsNot Nothing Then Return Path.GetFullPath(TestRoot)
            Dim Base = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            If String.IsNullOrWhiteSpace(Base) Then Throw New IOException("无法确定当前用户的本机资料目录。")
            Return Path.Combine(Base, "PCL Living Fork", "GameData")
        End Get
    End Property
    Friend Shared Function Address(Name As String) As String
        Dim Result = Path.Combine(Root, Name)
        InstanceResourceStore.EnsureWithin(Root, Result)
        Return Result
    End Function
    Friend Shared Function ReadJson(Name As String) As JObject
        Dim FileName = Address(Name)
        If Not File.Exists(FileName) Then Return New JObject
        If New FileInfo(FileName).Length > 4 * 1024 * 1024 Then Throw New IOException("本机游戏资料文件过大。")
        Return JObject.Parse(File.ReadAllText(FileName, New UTF8Encoding(False, True)))
    End Function
    Friend Shared Sub WriteJson(Name As String, Value As JObject)
        Dim FileName = Address(Name)
        AtomicWrite(FileName, New UTF8Encoding(False).GetBytes(Value.ToString()), Nothing, True)
    End Sub
    Friend Shared Function Hash(Bytes As Byte()) As String
        Using Algorithm = SHA256.Create()
            Return Convert.ToBase64String(Algorithm.ComputeHash(Bytes))
        End Using
    End Function
    Friend Shared Sub AtomicWrite(FileName As String, Bytes As Byte(), ExpectedHash As String, Backup As Boolean)
        If File.Exists(FileName) AndAlso (File.GetAttributes(FileName) And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("不修改链接形式的游戏设置文件。")
        If Backup AndAlso File.Exists(FileName & ".pcl-backup") AndAlso (File.GetAttributes(FileName & ".pcl-backup") And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("备份位置是链接，已停止修改游戏设置。")
        Directory.CreateDirectory(Path.GetDirectoryName(FileName))
        Dim Temporary = FileName & ".pcl-write-" & Guid.NewGuid().ToString("N")
        Try
            Using Output As New FileStream(Temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                Output.Write(Bytes, 0, Bytes.Length)
                Output.Flush(True)
            End Using
            If ExpectedHash IsNot Nothing AndAlso (Not File.Exists(FileName) OrElse Hash(File.ReadAllBytes(FileName)) <> ExpectedHash) Then Throw New IOException("游戏设置已被其他程序修改，本次同步已停止。")
            If File.Exists(FileName) Then
                File.Replace(Temporary, FileName, If(Backup, FileName & ".pcl-backup", Nothing))
            Else
                File.Move(Temporary, FileName)
            End If
        Finally
            If File.Exists(Temporary) Then File.Delete(Temporary)
        End Try
    End Sub
    Friend Shared Function ProcessMatches(Id As Integer, StartedUtc As Long) As Boolean
        Try
            Using Value = Process.GetProcessById(Id)
                Return Not Value.HasExited AndAlso Value.StartTime.ToUniversalTime().Ticks = StartedUtc
            End Using
        Catch ex As ArgumentException
            Return False
        Catch
            '无法确定进程状态时保留会话，不尝试导入其正在写入的设置。
            Return True
        End Try
    End Function
End Class
