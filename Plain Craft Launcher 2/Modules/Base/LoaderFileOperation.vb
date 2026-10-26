'文件事务的独占锁必须覆盖实际负载及其 finally/回滚，不能仅跟随加载器的显示状态。
Friend NotInheritable Class LoaderFileOperation
    Friend Shared Function CreateWorkload(Loader As LoaderCombo(Of String), Input As String) As Action
        Dim Gate As New Object, Running As Integer, Closed As Boolean
        '也覆盖嵌套组合内不同输入/输出类型的同步文件任务。
        Loader.GuardFileWorkload(
            Function(Work)
                Return Sub()
                           SyncLock Gate
                               If Closed Then Throw New OperationCanceledException()
                               Running += 1
                           End SyncLock
                           Try
                               Work()
                           Finally
                               SyncLock Gate
                                   Running -= 1
                                   Threading.Monitor.PulseAll(Gate)
                               End SyncLock
                           End Try
                       End Sub
            End Function)
        Return Sub()
                   Try
                       SyncLock Loader.LockState
                           If Loader.State = LoadState.Canceled Then Throw New OperationCanceledException()
                           Loader.Start(Input)
                       End SyncLock
                       '取消发生在开始与等待之间时，不能由 WaitForExit 再次启动组合加载器。
                       Loader.WaitForExit(StartIfNeeded:=False)
                   Finally
                       SyncLock Gate
                           Closed = True
                           Do While Running <> 0
                               Threading.Monitor.Wait(Gate)
                           Loop
                       End SyncLock
                   End Try
               End Sub
    End Function
End Class
