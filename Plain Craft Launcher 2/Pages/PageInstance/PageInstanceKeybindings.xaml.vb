Public Class PageInstanceKeybindings
    Implements IRefreshable

    Private ReadOnly BindingsLoader As New LoaderTask(Of String, List(Of InstanceKeybinding))("读取版本按键", AddressOf ReadBindings)
    Private Entries As New List(Of InstanceKeybinding)
    Private Owner As McInstance
    Private GameFolder As String
    Private Captured As InstanceKeybinding
    Private KeyboardOwner As FormMain
    Private ConflictsOnly As Boolean
    Private CaptureBarShownCount As Integer
    Private ReadOnly KeyButtons As New Dictionary(Of String, MyButton)(StringComparer.Ordinal)

    Private Sub LoaderInit() Handles Me.Initialized
        DisabledPageAnimControls.Add(CardCapture)
        BtnClear.Logo = Logo.IconButtonDelete
        BtnCancel.Logo = Logo.IconButtonCross
        BtnFilterAll.LabText.Margin = New Thickness(-2, 0, 8, 0)
        BtnFilterConflicts.LabText.Margin = New Thickness(-2, 0, 8, 0)
        PageLoaderInit(Load, PanLoad, PanBack, Nothing, BindingsLoader, AddressOf BindingsLoaded, AutoRun:=False)
    End Sub
    Private Sub PageLoaded() Handles Me.Loaded
        PanBack.ScrollToHome()
        RefreshBindings()
        If FrmMain IsNot Nothing AndAlso KeyboardOwner Is Nothing Then
            KeyboardOwner = FrmMain
            AddHandler KeyboardOwner.PreviewKeyDown, AddressOf CaptureKey
            AddHandler KeyboardOwner.PreviewMouseDown, AddressOf HandleCapturedMouse
        End If
    End Sub
    Private Sub PageUnloaded() Handles Me.Unloaded
        BindingsLoader.Cancel()
        CancelCapture()
        If KeyboardOwner IsNot Nothing Then
            RemoveHandler KeyboardOwner.PreviewKeyDown, AddressOf CaptureKey
            RemoveHandler KeyboardOwner.PreviewMouseDown, AddressOf HandleCapturedMouse
            KeyboardOwner = Nothing
        End If
    End Sub
    Public Sub RefreshBindings() Implements IRefreshable.Refresh
        CancelCapture()
        Owner = PageInstanceLeft.Instance
        If Owner Is Nothing OrElse Not ModModpackMode.AllowsInstance(Owner.PathVersion) Then Return
        GameFolder = Owner.PathIndie
        BindingsLoader.Start(GameFolder, IsForceRestart:=True)
    End Sub
    Private Sub ReadBindings(Loader As LoaderTask(Of String, List(Of InstanceKeybinding)))
        Loader.Output = InstanceKeybindings.ReadBindings(Loader.Input, Loader.CreateCancellationToken())
    End Sub
    Private Sub BindingsLoaded()
        Entries = BindingsLoader.Output
        If Owner IsNot Nothing AndAlso Owner.Version.Valid AndAlso Owner.Version.Vanilla.Major < 13 Then
            For Each Entry In Entries.Where(Function(Item) Not Item.Saved)
                Entry.Value = PersonalGameSettings.BindingForVersion(Entry.Value, False)
            Next
        End If
        CardBindings.ToolTip = If(Entries.Any(Function(Item) Not Item.Saved), "尚未保存的原版按键显示默认键位。模组按键需在游戏内保存设置后才可识别。", Nothing)
        RenderList()
    End Sub
    Private Sub FilterChanged() Handles SearchBox.TextChanged
        RenderList()
    End Sub
    Private Sub FilterChecked(Sender As MyRadioButton, User As Boolean) Handles BtnFilterAll.Check, BtnFilterConflicts.Check
        ConflictsOnly = Sender Is BtnFilterConflicts
        RenderList()
    End Sub
    Private Sub RenderList()
        If PanBindings Is Nothing Then Return
        PanBindings.Children.Clear()
        KeyButtons.Clear()
        Dim Query = SearchBox.Text.Trim()
        Dim Matching = Entries.Where(Function(Item) Query = "" OrElse (Item.Name & " " & Item.Owners & " " & Item.Key & " " & DisplayKey(Item.Normalized)).IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0).ToList()
        Dim Visible = Matching.Where(Function(Item) Not ConflictsOnly OrElse Item.SharedCount > 1).ToList()
        BtnFilterAll.Text = If(Query = "", "全部", "搜索结果") & $" ({Matching.Count})"
        BtnFilterConflicts.Text = $"同键占用 ({Matching.Where(Function(Item) Item.SharedCount > 1).Count()})"
        LabEmpty.Visibility = If(Visible.Count > 0, Visibility.Collapsed, Visibility.Visible)
        LabEmpty.Text = If(Entries.Count = 0, "暂无按键记录。请先启动一次该版本的游戏并保存设置。", "没有找到符合条件的按键。")
        Dim Rendered As New HashSet(Of String)(StringComparer.Ordinal)
        For Each Entry In Visible
            If Not ConflictsOnly OrElse Entry.SharedCount <= 1 OrElse Entry.Normalized = "key.keyboard.unknown" Then
                PanBindings.Children.Add(CreateBindingRow(Entry))
                Continue For
            End If
            If Not Rendered.Add(Entry.Normalized) Then Continue For
            Dim Members = Visible.Where(Function(Item) Item.Normalized = Entry.Normalized).ToList()
            Dim Total = Math.Max(Entry.SharedCount, Entries.Where(Function(Item) Item.Normalized = Entry.Normalized).Count())
            Dim Group As New MyCard With {.Title = DisplayKey(Entry.Normalized) & $" 键位 · 与 {Total - 1} 项共用", .Margin = New Thickness(0, 8, 0, 12), .Tag = Entry.Normalized}
            Dim Rows As New StackPanel With {.Margin = New Thickness(15, 36, 15, 12)}
            For Each Member In Members
                Rows.Children.Add(CreateBindingRow(Member))
            Next
            Dim Header = Group.MainTextBlock
            Group.Children.Add(Rows)
            PanBindings.Children.Add(Group)
        Next
    End Sub
    Private Function CreateBindingRow(Entry As InstanceKeybinding) As Grid
            Dim Row As New Grid With {.Height = 44, .Margin = New Thickness(5, 2, 4, 4)}
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
            Dim Labels As New StackPanel With {.VerticalAlignment = VerticalAlignment.Center, .Margin = New Thickness(0, 0, 18, 0)}
            Labels.Children.Add(New TextBlock With {.Text = Entry.Name, .FontSize = 13.5, .TextTrimming = TextTrimming.CharacterEllipsis})
            Dim Detail = If(Entry.Owners = "Minecraft", "", Entry.Owners)
            If Detail <> "" Then
                Dim Description As New TextBlock With {.Text = Detail, .FontSize = 12, .TextTrimming = TextTrimming.CharacterEllipsis, .Margin = New Thickness(0, 2, 0, 0)}
                Description.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4")
                Labels.Children.Add(Description)
            End If
            Row.Children.Add(Labels)
            Dim Button As New MyButton With {.Text = DisplayKey(Entry.Normalized), .Width = 132, .Height = 32, .Padding = New Thickness(10, 0, 10, 0), .VerticalAlignment = VerticalAlignment.Center,
                .ColorType = If(Entry.SharedCount > 1, MyButton.ColorState.Red, MyButton.ColorState.Normal), .Tag = Entry,
                .ToolTip = Entry.Key & vbCrLf & Entry.Owners & If(Not Entry.Saved, vbCrLf & "原版默认键位", "")}
            Grid.SetColumn(Button, 1)
            AddHandler Button.Click, Sub() BeginCapture(Entry)
            KeyButtons.Add(Entry.Key, Button)
            Row.Children.Add(Button)
        Return Row
    End Function
    Private Function DisplayKey(Value As String) As String
        If String.IsNullOrEmpty(Value) OrElse Value = "key.keyboard.unknown" Then Return "未绑定"
        Dim Parts = Value.Split(":"c), Name As String
        Select Case Parts(0)
            Case "key.mouse.left" : Name = "鼠标左键"
            Case "key.mouse.right" : Name = "鼠标右键"
            Case "key.mouse.middle" : Name = "鼠标中键"
            Case "key.mouse.4" : Name = "鼠标侧键 1"
            Case "key.mouse.5" : Name = "鼠标侧键 2"
            Case "key.keyboard.left.control" : Name = "左 Ctrl"
            Case "key.keyboard.right.control" : Name = "右 Ctrl"
            Case "key.keyboard.left.shift" : Name = "左 Shift"
            Case "key.keyboard.right.shift" : Name = "右 Shift"
            Case "key.keyboard.left.alt" : Name = "左 Alt"
            Case "key.keyboard.right.alt" : Name = "右 Alt"
            Case "key.keyboard.space" : Name = "空格"
            Case "key.keyboard.enter" : Name = "回车"
            Case "key.keyboard.backspace" : Name = "退格"
            Case "key.keyboard.up" : Name = "↑"
            Case "key.keyboard.down" : Name = "↓"
            Case "key.keyboard.left" : Name = "←"
            Case "key.keyboard.right" : Name = "→"
            Case Else : Name = Parts(0).Replace("key.keyboard.keypad.", "小键盘 ").Replace("key.keyboard.", "").Replace("key.mouse.", "鼠标 ").ToUpperInvariant()
        End Select
        Dim Modifiers = Parts.Skip(1).Where(Function(Part) Part <> "NONE" AndAlso Part <> "UNIVERSAL").Select(Function(Part) Part.Replace("CONTROL", "Ctrl").Replace("SHIFT", "Shift").Replace("ALT", "Alt")).ToList()
        Return If(Modifiers.Count = 0, Name, String.Join(" + ", Modifiers) & " + " & Name)
    End Function
    Private Sub BeginCapture(Entry As InstanceKeybinding)
        If HasRunningMinecraft OrElse Owner Is Nothing OrElse McInstanceLock.IsLocked(Owner) Then
            Hint("请先关闭游戏并等待版本修改完成，再编辑按键。", HintType.Red)
            Return
        End If
        Captured = Entry
        LabCapture.Text = Entry.Name & "：按下新按键，Esc 取消"
        UpdateCaptureUi()
    End Sub
    Private Sub CancelCapture() Handles BtnCancel.Click
        Captured = Nothing
        UpdateCaptureUi()
    End Sub
    Private Sub UpdateCaptureUi()
        For Each Button In KeyButtons.Values
            Dim Entry = DirectCast(Button.Tag, InstanceKeybinding), Active = Entry Is Captured
            Button.Text = If(Active, "请按键…", DisplayKey(Entry.Normalized))
            Button.ColorType = If(Active, MyButton.ColorState.Highlight, If(Entry.SharedCount > 1, MyButton.ColorState.Red, MyButton.ColorState.Normal))
        Next
        LocalResourceUi.RefreshSelectionBar(CardCapture, TransCapture, If(Captured Is Nothing, 0, 1), CaptureBarShownCount, "Keybinding Capture " & PageUuid, IsLoaded AndAlso AniControlEnabled = 0)
        CardBindings.Margin = New Thickness(0, 0, 0, If(Captured Is Nothing, 15, 95))
    End Sub
    Private Sub ClearBinding() Handles BtnClear.Click
        If Captured IsNot Nothing Then SaveBinding("key.keyboard.unknown")
    End Sub
    Private Sub CaptureKey(Sender As Object, e As KeyEventArgs)
        If Captured Is Nothing OrElse FrmMain?.PageRight IsNot Me Then Return
        e.Handled = True
        Dim Pressed = If(e.Key = Key.System, e.SystemKey, If(e.Key = Key.ImeProcessed, e.ImeProcessedKey, e.Key))
        If Pressed = Key.Escape Then
            CancelCapture()
            Return
        End If
        Dim Name = KeyboardName(Pressed)
        If Name IsNot Nothing Then SaveBinding("key.keyboard." & Name) Else Hint("暂不支持这个按键，请选择其他键。", HintType.Red)
    End Sub
    Private Sub HandleCapturedMouse(Sender As Object, e As MouseButtonEventArgs)
        If Captured Is Nothing OrElse FrmMain?.PageRight IsNot Me Then Return
        '保留取消和清空按钮的正常点击操作。
        Dim Source = TryCast(e.OriginalSource, DependencyObject)
        If Source IsNot Nothing AndAlso (BtnCancel.IsMouseOver OrElse BtnClear.IsMouseOver) Then Return
        e.Handled = True
        Select Case e.ChangedButton
            Case MouseButton.Left : SaveBinding("key.mouse.left")
            Case MouseButton.Right : SaveBinding("key.mouse.right")
            Case MouseButton.Middle : SaveBinding("key.mouse.middle")
            Case MouseButton.XButton1 : SaveBinding("key.mouse.4")
            Case MouseButton.XButton2 : SaveBinding("key.mouse.5")
        End Select
    End Sub
    Private Sub SaveBinding(Value As String)
        Dim Entry = Captured
        If Entry Is Nothing OrElse Owner Is Nothing Then Return
        '确认弹窗也位于主窗口内，先停止捕获，避免弹窗点击被当成下一次绑定。
        CancelCapture()
        Try
            If HasRunningMinecraft OrElse McInstanceLock.IsLocked(Owner) Then Throw New IO.IOException("请先关闭游戏并等待版本修改完成。")
            If Not Owner.Version.Valid Then Throw New IO.IOException("无法确定此版本的按键格式，请在游戏内修改。")
            Dim Separator = Entry.Value.IndexOf(":"c)
            If Separator > 0 AndAlso Value <> "key.keyboard.unknown" Then Value &= Entry.Value.Substring(Separator)
            Dim Existing = Entries.Where(Function(Item) Item IsNot Entry AndAlso Item.Normalized = Value AndAlso Value <> "key.keyboard.unknown").ToList()
            If Existing.Count > 0 AndAlso MyMsgBox("这个键已用于：" & vbCrLf & String.Join(vbCrLf, Existing.Take(8).Select(Function(Item) Item.Name & " · " & Item.Owners)) & vbCrLf & "仍然使用这个键吗？", "同键占用", "仍然绑定", "取消", IsWarn:=True) <> 1 Then Return
            Dim Encoded = PersonalGameSettings.BindingForVersion(Value, Owner.Version.Valid AndAlso Owner.Version.Vanilla.Major >= 13)
            If Encoded Is Nothing Then Throw New IO.IOException("此游戏版本无法使用这个按键。")
            InstanceKeybindings.Save(GameFolder, Entry.Key, If(Entry.Saved, Entry.Value, Nothing), Encoded)
            Try
                PersonalGameSettings.CaptureEditedBinding(Owner, Entry.Key, Encoded)
            Catch ex As Exception
                Logger.Warn(ex, "按键已保存，但更新个人同步档案失败")
            End Try
            CancelCapture()
            Hint("按键已保存。", HintType.Green)
            RefreshBindings()
        Catch ex As Exception
            CancelCapture()
            Logger.Error(ex, "修改按键失败", LogBehavior.Toast)
        End Try
    End Sub
    Private Function KeyboardName(Value As Key) As String
        If Value >= Key.A AndAlso Value <= Key.Z Then Return Value.ToString().ToLowerInvariant()
        If Value >= Key.D0 AndAlso Value <= Key.D9 Then Return (CInt(Value) - CInt(Key.D0)).ToString()
        If Value >= Key.NumPad0 AndAlso Value <= Key.NumPad9 Then Return "keypad." & (CInt(Value) - CInt(Key.NumPad0))
        If Value >= Key.F1 AndAlso Value <= Key.F24 Then Return Value.ToString().ToLowerInvariant()
        Select Case Value
            Case Key.Space : Return "space"
            Case Key.Return : Return "enter"
            Case Key.Tab : Return "tab"
            Case Key.Back : Return "backspace"
            Case Key.LeftCtrl : Return "left.control"
            Case Key.RightCtrl : Return "right.control"
            Case Key.LeftShift : Return "left.shift"
            Case Key.RightShift : Return "right.shift"
            Case Key.LeftAlt : Return "left.alt"
            Case Key.RightAlt : Return "right.alt"
            Case Key.Left, Key.Right, Key.Up, Key.Down, Key.Home, Key.End, Key.Insert, Key.Delete : Return Value.ToString().ToLowerInvariant()
            Case Key.PageUp : Return "page.up"
            Case Key.PageDown : Return "page.down"
            Case Key.OemMinus : Return "minus"
            Case Key.OemPlus : Return "equal"
            Case Key.Oem1 : Return "semicolon"
            Case Key.Oem2 : Return "slash"
            Case Key.Oem3 : Return "grave.accent"
            Case Key.Oem4 : Return "left.bracket"
            Case Key.Oem5 : Return "backslash"
            Case Key.Oem6 : Return "right.bracket"
            Case Key.Oem7 : Return "apostrophe"
            Case Key.OemComma : Return "comma"
            Case Key.OemPeriod : Return "period"
            Case Key.Capital : Return "caps.lock"
            Case Key.NumLock : Return "num.lock"
            Case Key.Scroll : Return "scroll.lock"
            Case Key.LWin : Return "left.win"
            Case Key.RWin : Return "right.win"
            Case Key.Apps : Return "menu"
            Case Key.Pause : Return "pause"
            Case Key.Snapshot : Return "print.screen"
            Case Key.Add : Return "keypad.add"
            Case Key.Subtract : Return "keypad.subtract"
            Case Key.Multiply : Return "keypad.multiply"
            Case Key.Divide : Return "keypad.divide"
            Case Key.Decimal : Return "keypad.decimal"
        End Select
        Return Nothing
    End Function
    Private Sub RetryLoad() Handles Load.Click
        If BindingsLoader.State = LoadState.Failed Then RefreshBindings()
    End Sub
End Class
