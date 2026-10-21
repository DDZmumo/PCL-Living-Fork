param(
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [switch]$NoObfuscation
)
$ErrorActionPreference='Stop'

function ConvertTo-FragmentExpression([byte[]]$Bytes) {
    $encoded=[Convert]::ToBase64String($Bytes)
    if($NoObfuscation){return 'Convert.FromBase64String("'+$encoded+'")'}
    $parts=@(for($offset=0;$offset -lt $encoded.Length;$offset+=16){$encoded.Substring($offset,[Math]::Min(16,$encoded.Length-$offset))})
    $permutation=@(0..($parts.Count-1) | Get-Random -Count $parts.Count)
    $stored=@($permutation | ForEach-Object {'"'+$parts[$_]+'"'})
    $order=@(0..($parts.Count-1) | ForEach-Object {[Array]::IndexOf($permutation,$_ )})
    return 'Assemble(New String() {'+($stored -join ',')+'}, New Integer() {'+($order -join ',')+'})'
}

function New-EncryptedFactory([string]$Value,[string]$Method) {
    [byte[]]$plain=[Text.Encoding]::UTF8.GetBytes($Value)
    [byte[]]$material=New-Object byte[] 64
    [byte[]]$shareA=New-Object byte[] 64
    [byte[]]$shareB=New-Object byte[] 64
    [byte[]]$encryptionKey=New-Object byte[] 32
    [byte[]]$authenticationKey=New-Object byte[] 32
    $rng=[Security.Cryptography.RandomNumberGenerator]::Create()
    $cipher=[Security.Cryptography.Aes]::Create()
    $authenticator=$null
    try {
        $rng.GetBytes($material);$rng.GetBytes($shareA)
        for($index=0;$index -lt 64;$index++){$shareB[$index]=$material[$index] -bxor $shareA[$index]}
        [Buffer]::BlockCopy($material,0,$encryptionKey,0,32)
        [Buffer]::BlockCopy($material,32,$authenticationKey,0,32)
        $cipher.KeySize=256;$cipher.Mode=[Security.Cryptography.CipherMode]::CBC;$cipher.Padding=[Security.Cryptography.PaddingMode]::PKCS7
        $cipher.Key=$encryptionKey;$cipher.GenerateIV()
        $encryptor=$cipher.CreateEncryptor()
        try{[byte[]]$encrypted=$encryptor.TransformFinalBlock($plain,0,$plain.Length)}finally{$encryptor.Dispose()}
        [byte[]]$body=New-Object byte[] (17+$encrypted.Length)
        $body[0]=1
        [Buffer]::BlockCopy($cipher.IV,0,$body,1,16)
        [Buffer]::BlockCopy($encrypted,0,$body,17,$encrypted.Length)
        $authenticator=[Security.Cryptography.HMACSHA256]::new($authenticationKey)
        [byte[]]$tag=$authenticator.ComputeHash($body)
        [byte[]]$blob=New-Object byte[] ($body.Length+32)
        [Buffer]::BlockCopy($body,0,$blob,0,$body.Length)
        [Buffer]::BlockCopy($tag,0,$blob,$body.Length,32)
        $blobExpression=ConvertTo-FragmentExpression $blob
        $aExpression=ConvertTo-FragmentExpression $shareA
        $bExpression=ConvertTo-FragmentExpression $shareB
        return "    Private Shared Function $Method() As String`r`n        Return Decode($blobExpression, $aExpression, $bExpression)`r`n    End Function"
    } finally {
        foreach($bytes in @($plain,$material,$shareA,$shareB,$encryptionKey,$authenticationKey)){if($null -ne $bytes){[Array]::Clear($bytes,0,$bytes.Length)}}
        if($null -ne $authenticator){$authenticator.Dispose()}
        $cipher.Dispose();$rng.Dispose()
    }
}

$assignments=@();$methods=@()
foreach($inputSetting in @(
    @{Variable='PCL_CURSEFORGE_API_KEY';Factory='CurseForgeFactory'},
    @{Variable='PCL_MS_CLIENT_ID';Factory='MicrosoftFactory'})) {
    $value=[Environment]::GetEnvironmentVariable($inputSetting.Variable)
    if([string]::IsNullOrEmpty($value)){continue}
    if($value.Contains("`r") -or $value.Contains("`n") -or $value.Contains([char]0)){throw "Invalid multiline build setting: $($inputSetting.Variable)"}
    $method=if($NoObfuscation){'Load'+$inputSetting.Factory}else{'k'+[Guid]::NewGuid().ToString('N')}
    $assignments+='        '+$inputSetting.Factory+' = AddressOf '+$method
    $methods+=New-EncryptedFactory $value $method
    $value=$null
}
$source="Imports System`r`n' Generated build-only credential material; contains no plaintext input.`r`nFriend NotInheritable Partial Class BuildCredentials`r`n    Shared Sub New()`r`n"+($assignments -join "`r`n")+"`r`n    End Sub`r`n"+($methods -join "`r`n")+"`r`nEnd Class`r`n"
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),$source,[Text.UTF8Encoding]::new($true))
