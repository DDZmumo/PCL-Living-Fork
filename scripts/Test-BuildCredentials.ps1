param([Parameter(Mandatory=$true)][string]$ExecutablePath,[switch]$KeylessBuild)
# 输入期望值从与构建相同的环境变量读取，只输出通过/失败，不输出任何凭据。
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$saved=@{}
foreach($name in @('PCL_CURSEFORGE_API_KEY','PCL_MS_CLIENT_ID')){$saved[$name]=[Environment]::GetEnvironmentVariable($name)}
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('LivingPclCredentials-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
Add-Type -TypeDefinition @'
using System;
using System.CodeDom.Compiler;
using System.Reflection;
using Microsoft.VisualBasic;
public static class LivingPclCredentialTests {
    public static Assembly Compile(string source, string generated) {
        using(var provider=new VBCodeProvider()) {
            var parameters=new CompilerParameters(new[]{"System.dll","System.Core.dll","System.Security.dll"});
            parameters.GenerateInMemory=true;
            parameters.CompilerOptions="/rootnamespace:PCL /optioninfer+";
            var result=provider.CompileAssemblyFromSource(parameters,source,generated);
            foreach(CompilerError error in result.Errors) if(!error.IsWarning) throw new Exception("Credential fixture compile error: "+error.ErrorText);
            return result.CompiledAssembly;
        }
    }
    public static bool Contains(byte[] bytes, byte[] value) {
        if(value.Length==0)return false;
        for(int i=0;i<=bytes.Length-value.Length;i++) {
            int j=0;while(j<value.Length&&bytes[i+j]==value[j])j++;
            if(j==value.Length)return true;
        }
        return false;
    }
    public static bool Equal(byte[] a, byte[] b) {
        if(a.Length!=b.Length)return false;
        for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;
        return true;
    }
}
'@
$flags=[Reflection.BindingFlags]'NonPublic,Static'
function Read-Credential($assembly,[string]$method) {return $assembly.GetType('PCL.BuildCredentials',$true).GetMethod($method,$flags).Invoke($null,$null)}
function Set-TestInputs($cf,$ms) {
    [Environment]::SetEnvironmentVariable('PCL_CURSEFORGE_API_KEY',$cf)
    [Environment]::SetEnvironmentVariable('PCL_MS_CLIENT_ID',$ms)
}
try {
    $assembly=[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ExecutablePath))
    $binary=[IO.File]::ReadAllBytes([IO.Path]::GetFullPath($ExecutablePath))
    Set-TestInputs $null $null
    $expectedCF=if($KeylessBuild){''}else{[string]$saved['PCL_CURSEFORGE_API_KEY']}
    $expectedMS=if($KeylessBuild){''}else{[string]$saved['PCL_MS_CLIENT_ID']}
    if((Read-Credential $assembly 'ReadCurseForgeKey') -cne $expectedCF -or
       (Read-Credential $assembly 'ReadMicrosoftClientId') -cne $expectedMS) {throw 'Embedded credential readback did not match build inputs'}
    foreach($value in $saved.Values) {
        if([string]::IsNullOrEmpty($value)){continue}
        foreach($needle in @([Text.Encoding]::UTF8.GetBytes($value),[Text.Encoding]::Unicode.GetBytes($value),[Text.Encoding]::ASCII.GetBytes([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($value))))) {
            if([LivingPclCredentialTests]::Contains($binary,$needle)){throw 'A build input is directly visible in the binary'}
        }
    }
    Set-TestInputs 'synthetic-runtime-cf' 'synthetic-runtime-ms'
    if((Read-Credential $assembly 'ReadCurseForgeKey') -cne 'synthetic-runtime-cf' -or (Read-Credential $assembly 'ReadMicrosoftClientId') -cne 'synthetic-runtime-ms'){throw 'Runtime override failed'}
    Set-TestInputs $null $null
    $foundation=[IO.File]::ReadAllText((Join-Path $root 'Plain Craft Launcher 2/Modules/Base/BuildCredentials.vb'))
    $empty=[LivingPclCredentialTests]::Compile($foundation,'')
    if((Read-Credential $empty 'ReadCurseForgeKey') -ne '' -or (Read-Credential $empty 'ReadMicrosoftClientId') -ne ''){throw 'Public source has a nonempty embedded fallback'}
    $fakeCF='synthetic-cf-'+[Guid]::NewGuid().ToString('N')+'-quote"-slash\-unicode-'+[char]0x266B
    $fakeMS='synthetic-ms-'+[Guid]::NewGuid().ToString('N')
    $generated=Join-Path $fixture 'credentials.g.vb'
    foreach($case in @(@($null,$null,$false),@($fakeCF,$null,$false),@($null,$fakeMS,$false),@($fakeCF,$fakeMS,$false),@($fakeCF,$fakeMS,$true))) {
        Set-TestInputs $case[0] $case[1]
        & (Join-Path $PSScriptRoot 'Write-BuildCredentials.ps1') -OutputPath $generated -NoObfuscation:([bool]$case[2])
        $text=[IO.File]::ReadAllText($generated)
        foreach($value in @($fakeCF,$fakeMS)){if($text.Contains($value)){throw 'Generated source contains a plaintext test input'}}
        $compiled=[LivingPclCredentialTests]::Compile($foundation,$text)
        Set-TestInputs $null $null
        for($repeat=0;$repeat -lt 2;$repeat++) {
            if((Read-Credential $compiled 'ReadCurseForgeKey') -cne [string]$case[0] -or (Read-Credential $compiled 'ReadMicrosoftClientId') -cne [string]$case[1]){throw 'Generated encrypted factory readback failed'}
        }
    }
    $decode=$assembly.GetType('PCL.BuildCredentials').GetMethod('Decode',$flags)
    [byte[]]$material=1..64
    [byte[]]$shareA=65..128
    [byte[]]$shareB=for($i=0;$i -lt 64;$i++){$shareA[$i] -bxor $material[$i]}
    function New-TestBlob([byte[]]$plain,[Security.Cryptography.PaddingMode]$padding=[Security.Cryptography.PaddingMode]::PKCS7) {
        $aes=[Security.Cryptography.Aes]::Create();$mac=$null
        try {
            $aes.Key=[byte[]]$material[0..31];$aes.IV=[byte[]](1..16);$aes.Mode=[Security.Cryptography.CipherMode]::CBC;$aes.Padding=$padding
            $transform=$aes.CreateEncryptor()
            try{[byte[]]$ciphertext=$transform.TransformFinalBlock($plain,0,$plain.Length)}finally{$transform.Dispose()}
            [byte[]]$body=@([byte]1)+$aes.IV+$ciphertext
            $mac=[Security.Cryptography.HMACSHA256]::new([byte[]]$material[32..63])
            return ,([byte[]]($body+$mac.ComputeHash($body)))
        }finally{if($null -ne $mac){$mac.Dispose()};$aes.Dispose()}
    }
    $blob=New-TestBlob ([Text.Encoding]::UTF8.GetBytes('synthetic-authenticated-value'))
    $a=[byte[]]$shareA.Clone();$b=[byte[]]$shareB.Clone()
    if($decode.Invoke($null,@($blob,$a,$b)) -cne 'synthetic-authenticated-value' -or @($a | Where-Object {$_ -ne 0}).Count -ne 0 -or @($b | Where-Object {$_ -ne 0}).Count -ne 0){throw 'Decode or successful buffer cleanup failed'}
    foreach($location in @(0,1,17,($blob.Length-1),-1,-2)) {
        $changed=[byte[]]$blob.Clone();$a=[byte[]]$shareA.Clone();$b=[byte[]]$shareB.Clone()
        if($location -ge 0){$changed[$location]=$changed[$location] -bxor 1}elseif($location -eq -1){$a[0]=$a[0] -bxor 1}else{$b[0]=$b[0] -bxor 1}
        $rejected=$false
        try{$decode.Invoke($null,@($changed,$a,$b)) | Out-Null}catch{$rejected=$_.Exception.InnerException -is [Security.Cryptography.CryptographicException]}
        if(-not $rejected -or @($a | Where-Object {$_ -ne 0}).Count -ne 0 -or @($b | Where-Object {$_ -ne 0}).Count -ne 0){throw 'Tampered material was accepted or failure cleanup did not run'}
    }
    $emptyBlob=New-TestBlob ([byte[]]@())
    if($emptyBlob.Length -ne 65 -or $decode.Invoke($null,@($emptyBlob,[byte[]]$shareA.Clone(),[byte[]]$shareB.Clone())) -ne ''){throw 'Minimum encrypted payload failed'}
    foreach($badBlob in @((New-TestBlob ([byte[]]@(255,254))),(New-TestBlob (New-Object byte[] 16) ([Security.Cryptography.PaddingMode]::None)))) {
        $a=[byte[]]$shareA.Clone();$b=[byte[]]$shareB.Clone();$rejected=$false
        try{$decode.Invoke($null,@($badBlob,$a,$b)) | Out-Null}catch{$rejected=$true}
        if(-not $rejected -or @($a | Where-Object {$_ -ne 0}).Count -ne 0 -or @($b | Where-Object {$_ -ne 0}).Count -ne 0){throw 'Authenticated invalid plaintext or padding was accepted'}
    }
    $fakeBuild=Join-Path $fixture 'FailBuild.exe'
    Add-Type -TypeDefinition 'public static class CredentialFailureBuild { public static int Main(string[] args) { return 23; } }' -OutputAssembly $fakeBuild -OutputType ConsoleApplication
    $sourcePath=Join-Path $root 'Plain Craft Launcher 2/Modules/ModSecret.vb'
    $before=[IO.File]::ReadAllBytes($sourcePath)
    $generatedRoot=Join-Path $root 'Plain Craft Launcher 2/obj/CredentialBuild'
    $existing=@(Get-ChildItem -LiteralPath $generatedRoot -Filter '*.g.vb' -Recurse -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
    foreach($badInput in @($false,$true)) {
        Set-TestInputs $(if($badInput){"invalid`ninput"}else{$fakeCF}) $fakeMS
        $failed=$false
        try{& (Join-Path $PSScriptRoot 'Build-Release.ps1') -MSBuildPath $fakeBuild}catch{$failed=$true}
        if(-not $failed){throw 'Expected build failure was ignored'}
        $after=@(Get-ChildItem -LiteralPath $generatedRoot -Filter '*.g.vb' -Recurse -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
        if(@($after | Where-Object {$_ -notin $existing}).Count -ne 0 -or -not [LivingPclCredentialTests]::Equal($before,[IO.File]::ReadAllBytes($sourcePath))){throw 'Failed build left credential source or changed tracked source'}
    }
    $lease=[IO.File]::Open((Join-Path $generatedRoot 'build.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        $blocked=$false
        try{& (Join-Path $PSScriptRoot 'Build-Release.ps1') -MSBuildPath $fakeBuild}catch{$blocked=$_.Exception.Message.Contains('build lock')}
        if(-not $blocked){throw 'Concurrent release build was not rejected'}
    }finally{$lease.Dispose()}
    Write-Output 'Build credential checks passed: encrypted readback, plaintext/Base64 absence, environment override, empty/single/dual inputs, mixed fragments, tamper/padding/UTF8 rejection, buffer cleanup, failed-build cleanup and concurrent-build lock.'
} finally {
    foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name])}
    $saved.Clear();$binary=$null
    $resolved=[IO.Path]::GetFullPath($fixture)
    $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if(-not $resolved.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -or -not [IO.Path]::GetFileName($resolved).StartsWith('LivingPclCredentials-')){throw 'Unsafe fixture cleanup path'}
    [IO.Directory]::Delete($resolved,$true)
}
