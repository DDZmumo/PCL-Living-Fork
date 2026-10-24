Imports System
Imports System.ComponentModel
Imports System.Runtime.InteropServices
Imports System.Security.Principal

'算法依据官方 PCL 2.14.0.0 的静态还原：启用权限后依次执行内存列表命令 2、3、4。
'原生 ABI 已修正，并在结束或失败时恢复原权限状态；不依赖启动器配置或日志模块。
Friend NotInheritable Class NativeMemoryOptimizer
    Private Const SystemMemoryListInformation As Integer = 80
    Private Const SePrivilegeEnabled As UInteger = 2UI

    <StructLayout(LayoutKind.Sequential, Pack:=4)>
    Private Structure PrivilegeToken
        Public PrivilegeCount As UInteger
        Public Luid As Long
        Public Attributes As UInteger
    End Structure

    Private Sub New()
    End Sub

    Friend Shared Sub Run()
        Try
            ExecuteCommands(AddressOf AcquireProfilePrivilege,
                Function(Command)
                    Dim NativeCommand As Integer = Command
                    Return NtSetSystemInformation(SystemMemoryListInformation, NativeCommand, 4UI)
                End Function)
        Catch ex As DllNotFoundException
            Throw New InvalidOperationException("当前系统缺少内存优化所需的系统组件。", ex)
        Catch ex As EntryPointNotFoundException
            Throw New InvalidOperationException("当前系统不支持内存优化所需的系统接口。", ex)
        End Try
    End Sub

    '测试可注入虚构权限 lease 和命令结果，无需修改真实权限或系统内存。
    Friend Shared Sub ExecuteCommands(Acquire As Func(Of IDisposable), Apply As Func(Of Integer, Integer))
        If Acquire Is Nothing Then Throw New ArgumentNullException(NameOf(Acquire), "权限获取函数不能为空。")
        If Apply Is Nothing Then Throw New ArgumentNullException(NameOf(Apply), "内存优化命令函数不能为空。")
        Using Lease As IDisposable = Acquire()
            If Lease Is Nothing Then Throw New InvalidOperationException("未能获取内存优化权限。")
            For Each Command As Integer In New Integer() {2, 3, 4}
                Dim Status As Integer = Apply(Command)
                If Status <> 0 Then
                    Throw New InvalidOperationException($"内存优化操作 {Command} 失败（NTSTATUS：0x{Status:X8}）。")
                End If
            Next
        End Using
    End Sub

    '仅打开当前令牌，不启用权限或修改系统内存；与权限获取使用同一入口，便于只读回归。
    Friend Shared Function OpenPrivilegeIdentity() As WindowsIdentity
        'WindowsPrincipal.IsInRole 会复制主令牌来检查组成员；缺少 Duplicate 时会在清理前报“拒绝访问”。
        Return WindowsIdentity.GetCurrent(TokenAccessLevels.Query Or TokenAccessLevels.Duplicate Or TokenAccessLevels.AdjustPrivileges)
    End Function

    Private Shared Function AcquireProfilePrivilege() As IDisposable
        Dim Identity As WindowsIdentity = OpenPrivilegeIdentity()
        Dim Lease As ProfilePrivilegeLease = Nothing
        Try
            If Not New WindowsPrincipal(Identity).IsInRole(WindowsBuiltInRole.Administrator) Then
                Throw New InvalidOperationException("内存优化功能需要管理员权限。")
            End If
            Dim State As New PrivilegeToken With {.PrivilegeCount = 1UI, .Attributes = SePrivilegeEnabled}
            If Not LookupPrivilegeValueW(Nothing, "SeProfileSingleProcessPrivilege", State.Luid) Then
                Dim ErrorCode As Integer = Marshal.GetLastWin32Error()
                Throw New Win32Exception(ErrorCode, $"获取内存优化权限标识失败（错误代码：{ErrorCode}）。")
            End If
            Lease = New ProfilePrivilegeLease(Identity)
            Lease.Enable(State)
            Return Lease
        Catch
            If Lease Is Nothing Then
                Identity.Dispose()
            Else
                Lease.Dispose()
            End If
            Throw
        End Try
    End Function

    Private NotInheritable Class ProfilePrivilegeLease
        Implements IDisposable

        Private Identity As WindowsIdentity
        Private PreviousState As PrivilegeToken
        Private RestoreRequired As Boolean
        Private IsDisposed As Boolean

        Friend Sub New(Identity As WindowsIdentity)
            Me.Identity = Identity
        End Sub

        Friend Sub Enable(ByRef State As PrivilegeToken)
            Dim ReturnLength As UInteger = 0UI
            Dim Adjusted As Boolean = AdjustTokenPrivilegesCapture(Identity.Token, False, State,
                CUInt(Marshal.SizeOf(GetType(PrivilegeToken))), PreviousState, ReturnLength)
            Dim ErrorCode As Integer = Marshal.GetLastWin32Error()
            RestoreRequired = Adjusted AndAlso PreviousState.PrivilegeCount <> 0UI
            'AdjustTokenPrivileges 返回 True 时仍可能报告 ERROR_NOT_ALL_ASSIGNED。
            If Not Adjusted OrElse ErrorCode <> 0 Then
                Throw New Win32Exception(ErrorCode, $"启用内存优化权限失败（错误代码：{ErrorCode}）。")
            End If
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If IsDisposed Then Return
            IsDisposed = True
            Try
                If RestoreRequired Then
                    Dim Restored As Boolean = AdjustTokenPrivilegesRestore(Identity.Token, False, PreviousState,
                        0UI, IntPtr.Zero, IntPtr.Zero)
                    Dim ErrorCode As Integer = Marshal.GetLastWin32Error()
                    If Not Restored OrElse ErrorCode <> 0 Then
                        Throw New Win32Exception(ErrorCode, $"恢复内存优化前的权限状态失败（错误代码：{ErrorCode}）。")
                    End If
                End If
            Finally
                Identity.Dispose()
                Identity = Nothing
            End Try
        End Sub
    End Class

    <DllImport("advapi32.dll", CharSet:=CharSet.Unicode, ExactSpelling:=True, SetLastError:=True)>
    Private Shared Function LookupPrivilegeValueW(SystemName As String, Name As String, ByRef Luid As Long) As Boolean
    End Function

    <DllImport("advapi32.dll", EntryPoint:="AdjustTokenPrivileges", ExactSpelling:=True, SetLastError:=True)>
    Private Shared Function AdjustTokenPrivilegesCapture(Token As IntPtr, DisableAllPrivileges As Boolean,
        ByRef NewState As PrivilegeToken, BufferLength As UInteger, ByRef PreviousState As PrivilegeToken,
        ByRef ReturnLength As UInteger) As Boolean
    End Function

    <DllImport("advapi32.dll", EntryPoint:="AdjustTokenPrivileges", ExactSpelling:=True, SetLastError:=True)>
    Private Shared Function AdjustTokenPrivilegesRestore(Token As IntPtr, DisableAllPrivileges As Boolean,
        ByRef NewState As PrivilegeToken, BufferLength As UInteger, PreviousState As IntPtr,
        ReturnLength As IntPtr) As Boolean
    End Function

    <DllImport("ntdll.dll", ExactSpelling:=True)>
    Private Shared Function NtSetSystemInformation(InformationClass As Integer, ByRef Command As Integer,
        InformationLength As UInteger) As Integer
    End Function
End Class

'父进程和 --memory 辅助进程共用结果约定；测试只需注入委托，不启动提权进程。
Friend NotInheritable Class MemoryOptimizationOperation
    Friend Const FailedExitCode As Integer = -1

    Private Sub New()
    End Sub

    Friend Shared Function Execute(IsElevated As Boolean, ReadAvailable As Func(Of Long), Optimize As Action,
                                   RunElevated As Func(Of Integer)) As Long
        If IsElevated Then Return MeasureReleased(ReadAvailable, Optimize)
        If RunElevated Is Nothing Then Throw New ArgumentNullException(NameOf(RunElevated))
        Dim ExitCode As Integer
        Try
            ExitCode = RunElevated()
        Catch ex As Win32Exception When ex.NativeErrorCode = 1223 'ERROR_CANCELLED：用户取消 UAC。
            Throw New OperationCanceledException("已取消授予内存优化所需的管理员权限。", ex)
        End Try
        If ExitCode < 0 Then
            Throw New InvalidOperationException($"内存优化辅助进程执行失败（退出代码：{ExitCode}）。")
        End If
        Return CLng(ExitCode) * 1024L
    End Function

    Friend Shared Function ExecuteHelper(ReadAvailable As Func(Of Long), Optimize As Action) As Integer
        '进程退出码保留非负 Int32 表示 KiB，避免大内存机器上的溢出被误判为失败。
        Return CInt(Math.Min(CLng(Integer.MaxValue), MeasureReleased(ReadAvailable, Optimize) \ 1024L))
    End Function

    Private Shared Function MeasureReleased(ReadAvailable As Func(Of Long), Optimize As Action) As Long
        If ReadAvailable Is Nothing Then Throw New ArgumentNullException(NameOf(ReadAvailable))
        If Optimize Is Nothing Then Throw New ArgumentNullException(NameOf(Optimize))
        Dim Before As Long = ReadAvailable()
        Optimize()
        Return Math.Max(0L, ReadAvailable() - Before)
    End Function
End Class
