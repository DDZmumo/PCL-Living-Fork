Partial Public Class PageInstanceOverall
    Private ComponentRefreshId As Integer
    Private ReadOnly ComponentsLoader As New LoaderTask(Of ComponentRequest, ComponentResult)("读取已安装组件", AddressOf ReadInstalledComponents)

    Private Class ComponentRequest
        Public Target As McInstance
        Public Version As McVersion
        Public GameFolder As String
        Public Revision As Integer
    End Class

    Private Class ComponentResult
        Public Request As ComponentRequest
        Public Items As List(Of InstalledComponentSummary.Item)
    End Class

    Private Sub ComponentsLoaderInit() Handles Me.Initialized
        PageLoaderInit(LoadComponents, PanComponentsLoad, PanBack, Nothing, ComponentsLoader, AddressOf InstalledComponentsLoaded, AutoRun:=False)
    End Sub

    Private Sub RefreshInstalledComponents()
        Dim RefreshId = Threading.Interlocked.Increment(ComponentRefreshId)
        If Not ModModpackMode.Enabled OrElse PageInstanceLeft.Instance Is Nothing Then
            ComponentsLoader.Cancel()
            PanInstalledComponents.Children.Clear()
            Return
        End If
        Dim Target = PageInstanceLeft.Instance
        '仅使用已保存的隔离路径；读取概览不触发 PathIndie 的自动迁移或写入。
        Dim GameFolder As String = Nothing
        If Settings.HasSaved("VersionArgumentIndieV2", Target) Then GameFolder = If(Settings.Get(Of Boolean)("VersionArgumentIndieV2", Target), Target.PathVersion, McFolderSelected)
        '由页面加载器等完整结果就绪后再显示概览，不先展示缺少 API 的临时列表。
        ComponentsLoader.Start(New ComponentRequest With {.Target = Target, .Version = Target.Version, .GameFolder = GameFolder, .Revision = RefreshId}, IsForceRestart:=True)
    End Sub

    Private Sub ReadInstalledComponents(Loader As LoaderTask(Of ComponentRequest, ComponentResult))
        Dim Request = Loader.Input
        Dim Items As List(Of InstalledComponentSummary.Item)
        Try
            Dim Components = Configs.McInstallManifest.Get(Request.Target.Config)?.Components
            Dim LocalMods = If(Request.GameFolder Is Nothing, Nothing, InstalledComponentSummary.ReadLocalMods(IO.Path.Combine(Request.GameFolder, "mods")))
            Items = InstalledComponentSummary.Describe(Request.Version, Components, LocalMods)
        Catch ex As Exception
            If ex.IsCanceled Then Throw
            Logger.Warn(ex, "读取已安装组件信息失败")
            Items = InstalledComponentSummary.Describe(Request.Version)
        End Try
        If Loader.IsCanceled Then Return
        Loader.Output = New ComponentResult With {.Request = Request, .Items = Items}
    End Sub

    Private Sub InstalledComponentsLoaded(Loader As LoaderTask(Of ComponentRequest, ComponentResult))
        Dim Result = Loader.Output
        If Result Is Nothing OrElse Result.Request.Revision <> ComponentRefreshId OrElse Not ModModpackMode.Enabled OrElse PageInstanceLeft.Instance IsNot Result.Request.Target Then Return
        ShowInstalledComponents(Result.Items)
    End Sub

    Friend Sub ShowInstalledComponents(Items As IEnumerable(Of InstalledComponentSummary.Item))
        PanInstalledComponents.Children.Clear()
        For Each Item In Items
            Dim Row As New Grid With {.Height = 18, .Margin = New Thickness(132, 11, 20, 0), .VerticalAlignment = VerticalAlignment.Top}
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            Dim Icon As New MyImage With {.Source = PathImage & "Blocks/" & Item.Icon & ".png", .Width = 18, .Height = 18, .Stretch = Stretch.Uniform, .Margin = New Thickness(0, 0, 7, 0)}
            RenderOptions.SetBitmapScalingMode(Icon, BitmapScalingMode.Linear)
            Row.Children.Add(Icon)
            Dim Label As New TextBlock With {.Text = Item.Version, .TextTrimming = TextTrimming.CharacterEllipsis, .VerticalAlignment = VerticalAlignment.Center}
            Grid.SetColumn(Label, 1)
            Row.Children.Add(Label)
            Dim Card As New MyCard With {.Title = Item.Name, .Height = 40, .Margin = New Thickness(0, 0, 0, 12), .IsHitTestVisible = False}
            Card.Children.Add(Row)
            Dim Title = Card.MainTextBlock
            PanInstalledComponents.Children.Add(Card)
        Next
    End Sub
End Class
