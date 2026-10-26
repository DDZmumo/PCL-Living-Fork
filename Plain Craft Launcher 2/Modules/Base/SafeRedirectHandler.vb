Imports System.Net
Imports System.Net.Http
Imports System.Threading
Imports System.Threading.Tasks

'每一跳都重新检查目的地；HttpClientHandler 的自动重定向不会清除 x-api-key。
Friend NotInheritable Class SafeRedirectHandler
    Inherits DelegatingHandler

    Public Sub New(Transport As HttpMessageHandler)
        MyBase.New(Transport)
    End Sub

    Protected Overrides Async Function SendAsync(Request As HttpRequestMessage, Cancellation As CancellationToken) As Task(Of HttpResponseMessage)
        Dim Current = Request
        Try
            For Hop = 0 To 10
                Cancellation.ThrowIfCancellationRequested()
                Dim Response = Await MyBase.SendAsync(Current, Cancellation).ConfigureAwait(False)
                Dim Status = CInt(Response.StatusCode)
                If Not {301, 302, 303, 307, 308}.Contains(Status) OrElse Response.Headers.Location Is Nothing Then Return Response
                Dim Following As HttpRequestMessage = Nothing
                Try
                    If Hop = 10 Then Throw New HttpRequestException("重定向次数过多。")
                    Dim Destination As New Uri(Current.RequestUri, Response.Headers.Location)
                    If Not {Uri.UriSchemeHttp, Uri.UriSchemeHttps}.Contains(Destination.Scheme) OrElse Destination.UserInfo <> "" OrElse
                        (Current.RequestUri.Scheme = Uri.UriSchemeHttps AndAlso Destination.Scheme <> Uri.UriSchemeHttps) Then
                        Throw New HttpRequestException("已拒绝不安全的重定向地址。")
                    End If
                    Following = Await Current.CloneAsync().ConfigureAwait(False)
                    Following.RequestUri = Destination
                    '沿用自动重定向的 Authorization 清理行为；API Key 仅能留在可信 API 主机。
                    Following.Headers.Authorization = Nothing
                    If Not BuildCredentials.IsCurseForgeRequest(Destination) Then Following.Headers.Remove("x-api-key")
                    If Current.RequestUri.Authority <> Destination.Authority Then
                        Following.Headers.Remove("Cookie")
                        Following.Headers.Remove("Proxy-Authorization")
                    End If
                    If ((Status = 301 OrElse Status = 302) AndAlso Current.Method = HttpMethod.Post) OrElse
                        (Status = 303 AndAlso Current.Method <> HttpMethod.Head) Then
                        Following.Method = HttpMethod.Get
                        Following.Content?.Dispose()
                        Following.Content = Nothing
                    End If
                Catch
                    Following?.Dispose()
                    Throw
                Finally
                    Response.Dispose()
                End Try
                If Current IsNot Request Then Current.Dispose()
                Current = Following
            Next
            Throw New HttpRequestException("重定向次数过多。")
        Finally
            If Current IsNot Request Then Current.Dispose()
        End Try
    End Function
End Class
