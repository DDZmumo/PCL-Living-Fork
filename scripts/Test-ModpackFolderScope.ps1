$ErrorActionPreference='Stop'
# Executes the production folder-loader body using synthetic settings and GUID temporary directories.
$projectRoot=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName Microsoft.VisualBasic
$source=[IO.File]::ReadAllText((Join-Path $projectRoot 'Plain Craft Launcher 2/Modules/Minecraft/ModMinecraft.vb'))
$start=$source.IndexOf('    Private Sub McFolderListLoadSub()')
$end=$source.IndexOf("`n    End Sub",$start)
if($start -lt 0 -or $end -lt $start){throw 'Folder loader seam not found'}
$body=$source.Substring($start,$end-$start)+"`n    End Sub"
$stub=@'
Imports System
Imports System.IO
Imports System.Linq
Imports System.Collections.Generic
Imports System.Runtime.CompilerServices
Public Module Fixture
 Public McFolderList As New List(Of McFolder)
 Public Messages As Integer
 Public Class McFolder
  Public Name As String
  Public Location As String
  Public Type As Types
  Public Enum Types
   Vanilla
   RenamedVanilla
   Custom
  End Enum
 End Class
 Public Sub Run()
  McFolderListLoadSub()
 End Sub
 Public Sub Hint(text As String, kind As HintType)
 End Sub
 Public Enum HintType
  Red
 End Enum
 Public Function MyMsgBox(text As String, title As String, Optional IsWarn As Boolean = False) As Integer
  Messages += 1
  Return 1
 End Function
 Public Sub CheckPermissionWithException(folder As String)
  If Not Directory.Exists(folder) Then Throw New DirectoryNotFoundException("Folder does not exist")
 End Sub
 Public Sub McFolderLauncherProfilesJsonCreate(folder As String)
 End Sub
 Public Function RandomInteger(a As Integer,b As Integer) As Integer
  Return 0
 End Function
__BODY__
End Module
Public Module Paths
 Public Base As String
 Public AppData As String
End Module
Public Module Settings
 Public Folders As String
 Public Writes As Integer
 Public Reads As Integer
 Public Function [Get](Of T)(key As String) As T
  If key="LaunchFolders" Then Reads+=1 : Return CType(CObj(Folders),T)
  Return CType(CObj(False),T)
 End Function
 Public Sub [Set](key As String, value As Object)
  If key="LaunchFolders" Then Folders=CStr(value) : Writes+=1
 End Sub
End Module
Public Module ModModpackMode
 Public Enabled As Boolean = True
 Friend Function CreatePackFolderList() As List(Of Fixture.McFolder)
  Return New List(Of Fixture.McFolder) From {New Fixture.McFolder With {.Name="Pack",.Location=MinecraftFolderPolicy.PackFolder(Paths.Base),.Type=Fixture.McFolder.Types.Vanilla}}
 End Function
End Module
Public Module DirectoryUtils
 Public Function Exists(path As String) As Boolean
  Return Directory.Exists(path)
 End Function
 Public Function GetInfo(path As String) As DirectoryInfo
  Return New DirectoryInfo(path)
 End Function
 Public Sub Create(path As String)
  Directory.CreateDirectory(path)
 End Sub
End Module
Public Module PathUtils
 Public Function AddSlashSuffix(path As String) As String
  Return path.TrimEnd("\"c) & "\"
 End Function
 Public Function ForCompare(path As String) As String
  Return IO.Path.GetFullPath(path)
 End Function
 Public Function IsSame(a As String,b As String) As Boolean
  Return String.Equals(a,b,StringComparison.OrdinalIgnoreCase)
 End Function
End Module
Public Module Logger
 Public Sub Warn(ex As Exception,text As String)
 End Sub
 Public Sub [Error](ex As Exception,text As String)
  Throw New Exception(text,ex)
 End Sub
 Public Sub Info(text As String)
 End Sub
End Module
Public Module Extensions
 <Extension> Public Function Split(text As String,sep As Char,remove As Boolean) As String()
  Return text.Split(New Char(){sep},If(remove,StringSplitOptions.RemoveEmptyEntries,StringSplitOptions.None))
 End Function
 <Extension> Public Function EndsWithF(text As String,suffix As String) As Boolean
  Return text.EndsWith(suffix,StringComparison.OrdinalIgnoreCase)
 End Function
 <Extension> Public Function BeforeFirst(text As String,sep As String) As String
  Return text.Substring(0,text.IndexOf(sep))
 End Function
 <Extension> Public Function AfterFirst(text As String,sep As String) As String
  Return text.Substring(text.IndexOf(sep)+sep.Length)
 End Function
 <Extension> Public Function Join(items As IEnumerable(Of String),sep As String) As String
  Return String.Join(sep,items)
 End Function
 <Extension> Public Function GetDisplay(ex As Exception,detail As Boolean) As String
  Return ex.Message
 End Function
End Module
'@
$code=$stub.Replace('__BODY__',$body)
$options=[Collections.Generic.Dictionary[string,string]]::new()
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$compilerPath=(& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\Roslyn\vbc.exe' | Select-Object -First 1)
if(-not $compilerPath -or -not [IO.File]::Exists($compilerPath)){throw 'Visual Studio VB compiler was not found'}
$options['CompilerDirectoryPath']=[IO.Path]::GetDirectoryName($compilerPath)
$provider=[Microsoft.VisualBasic.VBCodeProvider]::new($options)
$parameters=[CodeDom.Compiler.CompilerParameters]::new(@('System.dll','System.Core.dll'))
$parameters.GenerateInMemory=$true
$parameters.CompilerOptions='/optioninfer+ /imports:Microsoft.VisualBasic,System.Threading'
$policy=Get-Content -LiteralPath (Join-Path $projectRoot 'Plain Craft Launcher 2/Modules/Minecraft/MinecraftFolderPolicy.vb') -Raw
$compiled=$provider.CompileAssemblyFromSource($parameters,[string[]]@($code,$policy))
$errors=@($compiled.Errors | Where-Object {-not $_.IsWarning})
if($errors.Count){throw (($errors | ForEach-Object {$_.ErrorText}) -join '; ')}
$asm=$compiled.CompiledAssembly
$root=Join-Path ([IO.Path]::GetTempPath()) ('LivingPCL-FolderScope-'+[Guid]::NewGuid().ToString('N'))
try{
 $other=Join-Path $root 'other-launcher';$old=Join-Path $root 'deleted-pack/.minecraft';$keep=Join-Path $root 'keep/.minecraft'
 foreach($path in @($other,$old,$keep)){[IO.Directory]::CreateDirectory($path) | Out-Null}
 [IO.Directory]::Delete($old,$false)
 $asm.GetType('Paths').GetField('Base').SetValue($null,($other+'\'))
 $asm.GetType('Paths').GetField('AppData').SetValue($null,((Join-Path $root 'appdata')+'\'))
 $asm.GetType('Settings').GetField('Folders').SetValue($null,('Pack>'+$old+'\|Keep>'+$keep+'\'))
 $original=$asm.GetType('Settings').GetField('Folders').GetValue($null)
 for($launch=0;$launch -lt 2;$launch++){
  $asm.GetType('Fixture').GetMethod('Run').Invoke($null,@()) | Out-Null
  if($asm.GetType('Fixture').GetField('Messages').GetValue($null) -ne 0){throw 'Pack launcher prompted about a shared folder'}
  if($asm.GetType('Settings').GetField('Reads').GetValue($null) -ne 0 -or $asm.GetType('Settings').GetField('Writes').GetValue($null) -ne 0){throw 'Pack launcher accessed or registered in the shared folder catalog'}
  if($asm.GetType('Settings').GetField('Folders').GetValue($null) -cne $original){throw 'Pack launcher changed the shared folder catalog'}
  $folders=$asm.GetType('Fixture').GetField('McFolderList').GetValue($null)
  if($folders.Count -ne 1 -or $folders[0].Location -ne ($other+'\.minecraft\')){throw 'Pack folder scope escaped its launcher directory'}
 }
 # Ordinary mode deliberately retains the official shared-folder failure behavior.
 $asm.GetType('ModModpackMode').GetField('Enabled').SetValue($null,$false)
 $asm.GetType('Fixture').GetMethod('Run').Invoke($null,@()) | Out-Null
 if($asm.GetType('Fixture').GetField('Messages').GetValue($null) -ne 1){throw 'Ordinary mode no longer follows official invalid-folder handling'}
 Write-Output 'Folder scope passed: first and repeated pack startup made zero shared-catalog reads/writes or dialogs; ordinary-mode handling preserved.'

}finally{
 $full=[IO.Path]::GetFullPath($root);$prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '\ALivingPCL-FolderScope-[0-9a-f]{32}\z'){throw 'Unsafe fixture root'}
 if([IO.Directory]::Exists($full)){[IO.Directory]::Delete($full,$true)}
 $provider.Dispose()
}
