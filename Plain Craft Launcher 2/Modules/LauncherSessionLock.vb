'与官方 PCL 共用账号时，必须先取得它已有的单例锁；仅使用 BCL，避免提前初始化设置。
Friend NotInheritable Class LauncherSessionLock
    Implements IDisposable

    Private ReadOnly Held As New List(Of System.Threading.Mutex)

    Private Sub New()
    End Sub

    Friend Shared Function TryAcquire(Names As IEnumerable(Of String), Timeout As Integer) As LauncherSessionLock
        Dim Lease As New LauncherSessionLock
        Try
            For Each Name In Names
                Dim Gate As New System.Threading.Mutex(False, Name)
                Dim Acquired As Boolean
                Try
                    Try
                        Acquired = Gate.WaitOne(Timeout)
                    Catch ex As System.Threading.AbandonedMutexException
                        Acquired = True
                    End Try
                Catch
                    Gate.Dispose()
                    Throw
                End Try
                If Not Acquired Then
                    Gate.Dispose()
                    Lease.Dispose()
                    Return Nothing
                End If
                Lease.Held.Add(Gate)
            Next
            Return Lease
        Catch
            Lease.Dispose()
            Throw
        End Try
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        For i = Held.Count - 1 To 0 Step -1
            Try
                Held(i).ReleaseMutex()
            Finally
                Held(i).Dispose()
            End Try
        Next
        Held.Clear()
    End Sub
End Class
