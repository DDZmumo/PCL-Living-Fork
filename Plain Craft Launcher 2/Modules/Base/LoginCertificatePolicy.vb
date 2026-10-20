Friend NotInheritable Class LoginCertificatePolicy
    Private Sub New()
    End Sub
    Friend Shared Function Accept(Sender As Object, Failure As Net.Security.SslPolicyErrors, ValidateLogin As Boolean) As Boolean
        If Failure = Net.Security.SslPolicyErrors.None Then Return True
        If ValidateLogin Then Return False
        Dim Host As String = Nothing
        If TypeOf Sender Is HttpWebRequest Then
            Host = DirectCast(Sender, HttpWebRequest).Address.DnsSafeHost
        ElseIf TypeOf Sender Is Uri Then
            Dim Address = DirectCast(Sender, Uri)
            If Address.IsAbsoluteUri AndAlso Address.Scheme = "https" Then Host = Address.DnsSafeHost
        ElseIf TypeOf Sender Is String Then
            Host = CStr(Sender)
        End If
        If String.IsNullOrWhiteSpace(Host) Then Return False
        Host = Host.TrimEnd("."c)
        If Uri.CheckHostName(Host) <> UriHostNameType.Dns Then Return False
        Return {"xboxlive.com", "minecraftservices.com"}.Any(Function(Domain)
            Return Host.Equals(Domain, StringComparison.OrdinalIgnoreCase) OrElse Host.EndsWith("." & Domain, StringComparison.OrdinalIgnoreCase)
        End Function)
    End Function
End Class
