Imports System.Runtime.InteropServices

'单例检查失败时只使用 BCL 和 Win32，不初始化启动器配置或日志模块。
Friend NotInheritable Class LauncherWindowActivation
    Private Sub New()
    End Sub

    <DllImport("user32.dll", CharSet:=CharSet.Unicode, ExactSpelling:=True)>
    Private Shared Function FindWindowW(ClassName As String, WindowName As String) As IntPtr
    End Function

    <DllImport("user32.dll", ExactSpelling:=True, SetLastError:=True)>
    Private Shared Function PostMessageW(Window As IntPtr, Message As UInteger, WParam As IntPtr, LParam As IntPtr) As Boolean
    End Function

    <DllImport("user32.dll", ExactSpelling:=True)>
    Private Shared Function SetForegroundWindow(Window As IntPtr) As Boolean
    End Function

    Friend Shared Function TryActivateExistingWindow() As Boolean
        Return RequestActivation(Function(Caption) FindWindowW(Nothing, Caption),
            Function(Window, Message) PostMessageW(Window, Message, IntPtr.Zero, IntPtr.Zero),
            Sub(Window) SetForegroundWindow(Window), AddressOf Threading.Thread.Sleep)
    End Function

    '委托允许使用独立测试窗口和虚构句柄验证重试及协议，不操作用户的启动器。
    Friend Shared Function RequestActivation(Lookup As Func(Of String, IntPtr),
                                             Queue As Func(Of IntPtr, UInteger, Boolean),
                                             Foreground As Action(Of IntPtr),
                                             Pause As Action(Of Integer)) As Boolean
        For Attempt As Integer = 0 To 9
            For Each Caption As String In {"LivingPCL　", "Plain Craft Launcher　", "Plain Craft Launcher 2　"}
                Dim Window = Lookup(Caption)
                If Window = IntPtr.Zero Then Continue For
                '官方与 LivingPCL 的主窗口均通过 6402 恢复隐藏、最小化和任务栏状态。
                Dim Queued = Queue(Window, 6402UI)
                Foreground(Window) '保留官方先投递消息、随后请求前台的调用次序。
                Return Queued
            Next
            If Attempt < 9 Then Pause(200)
        Next
        Return False
    End Function
End Class
