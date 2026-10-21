Imports System.IO
Imports System.IO.Compression
Imports System.Text.RegularExpressions

'概览只读取已安装版本和本地 Mod 元数据，不请求下载列表，也不改写安装清单。
Friend NotInheritable Class InstalledComponentSummary
    Friend NotInheritable Class Item
        Public ReadOnly Name As String
        Public ReadOnly Version As String
        Public ReadOnly Icon As String
        Friend Sub New(Name As String, Version As String, Icon As String)
            Me.Name = Name
            Me.Version = If(String.IsNullOrWhiteSpace(Version), "未知版本", Version)
            Me.Icon = Icon
        End Sub
    End Class

    Friend Shared Function Describe(Version As McVersion, Optional Components As McComponents = Nothing,
        Optional LocalMods As IDictionary(Of String, String) = Nothing) As List(Of Item)
        Dim Result As New List(Of Item)
        Result.Add(New Item("Minecraft", Version.VanillaName, "Grass"))
        For Each Loader In {ModLoaders.Forge, ModLoaders.NeoForge, ModLoaders.Fabric, ModLoaders.LiteLoader}
            Dim Value As String = Nothing
            If Not Version.ModLoaderVersions.TryGetValue(Loader, Value) Then Continue For
            Dim Recorded As String = Nothing
            If Components IsNot Nothing AndAlso Components.ModLoaderVersions.TryGetValue(Loader, Recorded) AndAlso Not String.IsNullOrWhiteSpace(Recorded) Then Value = Recorded
            Dim Icon = If(Loader = ModLoaders.Forge, "Anvil", If(Loader = ModLoaders.NeoForge, "NeoForge", If(Loader = ModLoaders.Fabric, "Fabric", "Egg")))
            Result.Add(New Item(Loader.ToString(), Value, Icon))
        Next
        Dim FabricApi As String = Nothing, OptiFabric As String = Nothing, OptiFine As String = Version.OptiFine
        If LocalMods IsNot Nothing Then
            LocalMods.TryGetValue("fabric-api", FabricApi)
            LocalMods.TryGetValue("optifabric", OptiFabric)
            Dim LocalOptiFine As String = Nothing
            If LocalMods.TryGetValue("optifine", LocalOptiFine) Then OptiFine = If(LocalOptiFine, If(Components?.OptiFineVersion, If(OptiFine, "未知版本")))
        ElseIf Components IsNot Nothing Then
            FabricApi = Components.FabricApiVersion
            OptiFabric = Components.OptiFabricVersion
            OptiFine = If(OptiFine, Components.OptiFineVersion)
        End If
        If FabricApi IsNot Nothing Then Result.Add(New Item("Fabric API", FabricApi, "Fabric"))
        If OptiFine IsNot Nothing Then Result.Add(New Item("OptiFine", OptiFine.Replace("HD_U_", ""), "GrassPath"))
        If OptiFabric IsNot Nothing Then Result.Add(New Item("OptiFabric", OptiFabric, "OptiFabric"))
        Return Result
    End Function

    Friend Shared Function ReadLocalMods(ModsFolder As String) As Dictionary(Of String, String)
        Dim Result As New Dictionary(Of String, String)(StringComparer.Ordinal)
        If Not Directory.Exists(ModsFolder) Then Return Result
        Dim Files As String()
        Try
            Files = Directory.EnumerateFiles(ModsFolder, "*.jar", SearchOption.TopDirectoryOnly).Take(1024).ToArray()
        Catch ex As IOException
            Return Result
        Catch ex As UnauthorizedAccessException
            Return Result
        End Try
        For Each FilePath In Files
            Try
                If New FileInfo(FilePath).Length > 256L * 1024 * 1024 Then Continue For
                Using Archive = ZipFile.OpenRead(FilePath)
                    Dim Metadata = Archive.GetEntry("fabric.mod.json")
                    If Metadata IsNot Nothing AndAlso Metadata.Length <= 1024 * 1024 Then
                        Using Stream = Metadata.Open(), Reader As New StreamReader(Stream)
                            Dim Json = JObject.Parse(Reader.ReadToEnd())
                            Dim Id = Json("id")?.ToString()
                            If Id = "fabric-api" OrElse Id = "optifabric" Then Result(Id) = If(Json("version")?.ToString(), "未知版本")
                        End Using
                    End If
                    If Archive.GetEntry("optifine/Config.class") IsNot Nothing OrElse Archive.GetEntry("net/optifine/Config.class") IsNot Nothing Then
                        Dim Match = Regex.Match(Path.GetFileNameWithoutExtension(FilePath), "HD_U_(.+)$", RegexOptions.IgnoreCase)
                        Result("optifine") = If(Match.Success, Match.Groups(1).Value, Nothing)
                    End If
                End Using
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is Newtonsoft.Json.JsonException
                '损坏、占用或无权限的 Mod 不阻止概览显示其他已安装信息。
            End Try
        Next
        Return Result
    End Function
End Class
