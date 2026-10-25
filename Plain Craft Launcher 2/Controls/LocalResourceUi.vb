'模组页与游戏资源页共用的原生选择栏图标和动画。
Friend Module LocalResourceUi
    '从原模组条目的选中/悬停反馈抽出，行与截图卡片共用相同时间和缓动。
    Friend Sub AppendItemSelection(Ani As List(Of AniData), Back As Border, Hovered As Boolean, Selected As Boolean, Pressed As Boolean, Optional TintBackground As Boolean = True)
        Dim Time As Integer = If(Hovered, 120, 180)
        If Hovered OrElse Selected Then
            If TintBackground Then Ani.Add(AaColor(Back, Border.BackgroundProperty, If(Pressed, "ColorBrush6", "ColorBrushBg1"), Time))
            Ani.Add(AaOpacity(Back, 1 - Back.Opacity, Time,, New AniEaseOutFluent))
            Ani.Add(AaScaleTransform(Back, If(Pressed, 0.996, 1) - CType(Back.RenderTransform, ScaleTransform).ScaleX, Time * 1.2,, New AniEaseOutFluent))
        Else
            Ani.AddRange({
                AaOpacity(Back, -Back.Opacity, Time),
                AaScaleTransform(Back, 0.996 - CType(Back.RenderTransform, ScaleTransform).ScaleX, Time,, New AniEaseOutFluent),
                AaScaleTransform(Back, -0.196, 1,,, True)
            })
        End If
    End Sub
    Friend Const PlayLogo As String = "M52.1,164.5c-1.4,0-3.1-0.5-4.2-1.3c-2.6-1.7-4-4.2-4-7V43.8c0-2.9,1.6-5.8,4.1-7c1.2-0.8,2.7-1.2,4.1-1.2c1.5,0,2.9,0.4,4.2,1.2L153.1,93c0,0,0.1,0,0.1,0.1c2.6,1.7,4,4.2,4,7c0,3-1.7,5.8-4.2,7.1l-96.8,56.2C55.1,164,53.5,164.5,52.1,164.5z M60.4,142.1l72.1-42.1L60.4,58.2V142.1z"
    Friend Sub ApplySelectionIcons(Update As MyIconTextButton, Delete As MyIconTextButton, Cancel As MyIconTextButton)
        Update.Logo = "M640 768H384l-32-32V509H213L190 454l298-298h45l298 298L810 509h-138v226z m-224-64h192V477l32-32h93L512 223 290 445H384l32 32zM352 831h320v64h-320z"
        Update.LogoScale = 1
        Delete.Logo = "M520.192 0C408.43 0 317.44 82.87 313.563 186.734H52.736c-29.038 0-52.663 21.943-52.663 49.079s23.625 49.152 52.663 49.152h58.075v550.473c0 103.35 75.118 187.757 167.717 187.757h472.43c92.599 0 167.716-83.894 167.716-187.757V285.477h52.59c29.038 0 52.59-21.943 52.663-49.08-0.073-27.135-23.625-49.151-52.663-49.151H726.235C723.237 83.017 631.955 0 520.192 0zM404.846 177.957c3.803-50.03 50.176-89.015 107.447-89.015 57.197 0 103.57 38.985 106.788 89.015H404.92zM284.379 933.669c-33.353 0-69.997-39.351-69.997-95.525v-549.01H833.39v549.522c0 56.247-36.645 95.525-69.998 95.525H284.379v-0.512z M357.23 800.695a48.274 48.274 0 0 0 47.616-49.006V471.7a48.274 48.274 0 0 0-47.543-49.08 48.274 48.274 0 0 0-47.69 49.006V751.69c0 27.282 20.846 49.006 47.617 49.006z m166.62 0a48.274 48.274 0 0 0 47.688-49.006V471.7a48.274 48.274 0 0 0-47.689-49.08 48.274 48.274 0 0 0-47.543 49.006V751.69c0 27.282 21.431 49.006 47.543 49.006z m142.92 0a48.274 48.274 0 0 0 47.543-49.006V471.7a48.274 48.274 0 0 0-47.543-49.08 48.274 48.274 0 0 0-47.616 49.006V751.69c0 27.282 20.773 49.006 47.543 49.006z"
        Delete.LogoScale = 0.96
        Cancel.Logo = "M867.648 951.296 512 595.648l-355.648 355.648c-11.52 11.52-30.272 11.52-41.856 0L72.64 909.44c-11.52-11.52-11.52-30.272 0-41.856L428.352 512 72.64 156.352c-11.52-11.52-11.52-30.272 0-41.856l41.856-41.856c11.52-11.52 30.272-11.52 41.856 0L512 428.288l355.648-355.648c11.52-11.52 30.272-11.52 41.856 0l41.856 41.856c11.52 11.52 11.52 30.272 0 41.856L595.648 512l355.648 355.648c11.52 11.52 11.52 30.272 0 41.856l-41.856 41.856C897.984 962.88 879.168 962.88 867.648 951.296L867.648 951.296z"
        Cancel.LogoScale = 0.8
    End Sub

    Friend Sub RefreshSelectionBar(Card As MyCard, Transform As TranslateTransform, Count As Integer, ByRef PreviousCount As Integer, AnimationName As String, Animate As Boolean)
        If Not Animate Then
            AniStop(AnimationName)
            PreviousCount = Count
            Card.Visibility = If(Count > 0, Visibility.Visible, Visibility.Collapsed)
            Card.Opacity = If(Count > 0, 1, 0)
            Transform.Y = If(Count > 0, -25, -10)
            Return
        End If
        If Count > 0 Then
            If PreviousCount >= Count Then
                PreviousCount = Count
                Return
            End If
            PreviousCount = Count
            Card.Visibility = Visibility.Visible
            AniStart({
                AaOpacity(Card, 1 - Card.Opacity, 60),
                AaTranslateY(Card, -27 - Transform.Y, 120, Ease:=New AniEaseOutFluent(AniEasePower.Weak)),
                AaTranslateY(Card, 3, 150, 120, Ease:=New AniEaseInoutFluent(AniEasePower.Weak)),
                AaTranslateY(Card, -1, 90, 270, Ease:=New AniEaseInoutFluent(AniEasePower.Weak))
            }, AnimationName)
        Else
            If PreviousCount = 0 Then Return
            PreviousCount = 0
            AniStart({
                AaOpacity(Card, -Card.Opacity, 90),
                AaTranslateY(Card, -10 - Transform.Y, 90, Ease:=New AniEaseInFluent(AniEasePower.Weak)),
                AaCode(Sub() Card.Visibility = Visibility.Collapsed, After:=True)
            }, AnimationName)
        End If
    End Sub
End Module
