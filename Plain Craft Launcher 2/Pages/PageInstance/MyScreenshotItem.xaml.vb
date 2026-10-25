Imports System.Threading
Imports System.Threading.Tasks

Public Class MyScreenshotItem
    Private Shared ReadOnly DecodeGate As New SemaphoreSlim(2)
    Friend Entry As InstanceResourceEntry
    Private Revision As Integer
    Private InViewport As Boolean
    Private Ready As Boolean
    Private Loading As Boolean
    Private Pressed As Boolean
    Private ReadOnly AnimationId As Integer = GetUuid()
    Friend Event Changed(Sender As MyScreenshotItem)
    Friend Event ClickRequested(Sender As MyScreenshotItem, Count As Integer)
    Public Property Checked As Boolean
        Get
            Return Check.Checked
        End Get
        Set(Value As Boolean)
            Check.Checked = Value
        End Set
    End Property
    Friend Sub Bind(Value As InstanceResourceEntry)
        Dim Changed = Entry Is Nothing OrElse Entry.FullPath <> Value.FullPath OrElse Entry.ModifiedUtc <> Value.ModifiedUtc OrElse Entry.Length <> Value.Length
        Entry = Value
        LabName.Text = Value.Name
        LabInfo.Text = Value.Description
        ToolTip = Value.Name
        If Changed Then ReleaseThumbnail()
    End Sub
    Private Sub CheckChanged() Handles Check.Change
        RefreshSelection()
        RaiseEvent Changed(Me)
    End Sub
    Private Sub RefreshSelection() Handles Me.MouseEnter
        Dim Target = If(Checked OrElse IsMouseOver, 1.0, 0.0)
        If IsLoaded AndAlso AniControlEnabled = 0 Then
            Dim Ani As New List(Of AniData)
            LocalResourceUi.AppendItemSelection(Ani, SelectionBorder, IsMouseOver, Checked, Pressed, False)
            AniStart(Ani, "Screenshot Selection " & AnimationId)
        Else
            SelectionBorder.Opacity = Target
            SelectionBorder.RenderTransform = New ScaleTransform(If(Target = 1, 1, 0.8), If(Target = 1, 1, 0.8))
        End If
    End Sub
    Private Sub MouseClick(Sender As Object, e As MouseButtonEventArgs) Handles Me.MouseLeftButtonDown
        If Check.IsMouseOver Then Return
        Pressed = True
        RefreshSelection()
        e.Handled = True
        RaiseEvent ClickRequested(Me, e.ClickCount)
    End Sub
    Private Sub ReleasePointer() Handles Me.MouseLeftButtonUp, Me.MouseLeave
        Pressed = False
        RefreshSelection()
    End Sub
    Friend Sub UpdateViewport(Viewport As FrameworkElement)
        If Entry Is Nothing OrElse Not IsLoaded Then Return
        Dim Position = TranslatePoint(New Point(), Viewport)
        Dim Visible = Position.Y + ActualHeight >= -100 AndAlso Position.Y <= Viewport.ActualHeight + 100
        InViewport = Visible
        If Not Visible Then
            If Ready OrElse Loading Then ReleaseThumbnail()
            Return
        End If
        If Not Ready AndAlso Not Loading Then LoadThumbnail()
    End Sub
    Private Async Sub LoadThumbnail()
        If Entry Is Nothing Then Return
        Loading = True
        Dim Current = Entry, Request = Revision
        Try
            Await DecodeGate.WaitAsync().ConfigureAwait(False)
            Dim Bitmap As ImageSource
            Try
                Bitmap = Await Task.Run(Function() InstanceResourceStore.LoadImage(Current.FullPath, 640)).ConfigureAwait(False)
            Finally
                DecodeGate.Release()
            End Try
            RunInUi(Sub()
                        If Request <> Revision OrElse Not InViewport Then Return
                        Thumbnail.Source = Bitmap
                        Loading = False
                        Ready = True
                    End Sub)
        Catch ex As Exception
            RunInUi(Sub()
                        If Request <> Revision Then Return
                        Loading = False
                        Ready = True
                    End Sub)
            Logger.Warn(ex, "加载截图缩略图失败")
        End Try
    End Sub
    Friend Sub ReleaseThumbnail() Handles Me.Unloaded
        Interlocked.Increment(Revision)
        AniStop("Screenshot Selection " & AnimationId)
        Thumbnail.Source = Nothing
        Ready = False
        Loading = False
    End Sub
End Class
