Imports System
Imports System.Security.Cryptography
Imports System.Text

'发布凭据仅由构建生成的临时 partial class 提供；直接编译开源源码时为空。
'这是客户端字符串保护，不能替代服务器端保密或阻止运行时提取。
Friend NotInheritable Partial Class BuildCredentials
    Private Shared ReadOnly CurseForgeFactory As Func(Of String)
    Private Shared ReadOnly MicrosoftFactory As Func(Of String)

    Private Sub New()
    End Sub

    Friend Shared Function ReadCurseForgeKey() As String
        Return Resolve("PCL_CURSEFORGE_API_KEY", CurseForgeFactory)
    End Function

    Friend Shared Function IsCurseForgeRequest(Address As Uri) As Boolean
        Return Address IsNot Nothing AndAlso Address.IsAbsoluteUri AndAlso Address.Scheme = Uri.UriSchemeHttps AndAlso
            Address.Port = 443 AndAlso Address.UserInfo = "" AndAlso String.Equals(Address.DnsSafeHost.TrimEnd("."c), "api.curseforge.com", StringComparison.OrdinalIgnoreCase)
    End Function

    Friend Shared Function ReadMicrosoftClientId() As String
        Return Resolve("PCL_MS_CLIENT_ID", MicrosoftFactory)
    End Function

    Private Shared Function Resolve(Variable As String, Factory As Func(Of String)) As String
        Dim OverrideValue = Environment.GetEnvironmentVariable(Variable)
        If OverrideValue IsNot Nothing Then Return OverrideValue
        If Factory Is Nothing Then Return ""
        Return Factory.Invoke()
    End Function

    Private Shared Function Assemble(Parts As String(), Order As Integer()) As Byte()
        If Parts Is Nothing OrElse Order Is Nothing OrElse Parts.Length <> Order.Length Then Throw New CryptographicException("Invalid embedded credential")
        Dim Seen(Parts.Length - 1) As Boolean
        Dim Encoded As New StringBuilder
        For Each Index In Order
            If Index < 0 OrElse Index >= Parts.Length OrElse Seen(Index) Then Throw New CryptographicException("Invalid embedded credential")
            Seen(Index) = True
            Encoded.Append(Parts(Index))
        Next
        Return Convert.FromBase64String(Encoded.ToString())
    End Function

    'v1: version(1) + IV(16) + AES-256-CBC/PKCS7 ciphertext + HMAC-SHA256(32)。
    '独立的 AES/HMAC 密钥经两个随机份额重建；先认证后解密。
    Friend Shared Function Decode(Blob As Byte(), ShareA As Byte(), ShareB As Byte()) As String
        Dim Material(63) As Byte
        Dim EncryptionKey(31) As Byte
        Dim AuthenticationKey(31) As Byte
        Dim Plain As Byte() = Nothing
        Try
            If Blob Is Nothing OrElse Blob.Length < 65 OrElse Blob(0) <> 1 OrElse (Blob.Length - 49) Mod 16 <> 0 OrElse
                ShareA Is Nothing OrElse ShareA.Length <> 64 OrElse ShareB Is Nothing OrElse ShareB.Length <> 64 Then Throw New CryptographicException("Invalid embedded credential")
            For Index As Integer = 0 To 63
                Material(Index) = ShareA(Index) Xor ShareB(Index)
            Next
            Buffer.BlockCopy(Material, 0, EncryptionKey, 0, 32)
            Buffer.BlockCopy(Material, 32, AuthenticationKey, 0, 32)
            Using Authenticator As New HMACSHA256(AuthenticationKey)
                Dim Tag = Authenticator.ComputeHash(Blob, 0, Blob.Length - 32)
                Dim Difference As Integer = 0
                For Index As Integer = 0 To 31
                    Difference = Difference Or (Tag(Index) Xor Blob(Blob.Length - 32 + Index))
                Next
                Array.Clear(Tag, 0, Tag.Length)
                If Difference <> 0 Then Throw New CryptographicException("Invalid embedded credential")
            End Using
            Dim IV(15) As Byte
            Buffer.BlockCopy(Blob, 1, IV, 0, 16)
            Using Cipher = Aes.Create()
                Cipher.KeySize = 256
                Cipher.Mode = CipherMode.CBC
                Cipher.Padding = PaddingMode.PKCS7
                Cipher.Key = EncryptionKey
                Cipher.IV = IV
                Using Decryptor = Cipher.CreateDecryptor()
                    Plain = Decryptor.TransformFinalBlock(Blob, 17, Blob.Length - 49)
                    Return New UTF8Encoding(False, True).GetString(Plain)
                End Using
            End Using
        Finally
            For Each Bytes In New Byte()() {Material, EncryptionKey, AuthenticationKey, Plain, ShareA, ShareB}
                If Bytes IsNot Nothing Then Array.Clear(Bytes, 0, Bytes.Length)
            Next
        End Try
    End Function
End Class
