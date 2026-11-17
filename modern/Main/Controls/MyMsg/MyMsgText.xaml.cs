using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCLLF.Presentation.Controls;
using PCLLF.Presentation.Dialogs;

namespace PCL;

// The original Grid/XAML and PCL-typed parts are retained; all modal business is the verified C027e controller.
public partial class MyMsgText : Grid {
    private readonly ModMain.MyMsgBoxConverter MyConverter;
    private readonly int Uuid = ModBase.GetUuid();
    private readonly IMessageTextController Native;
    public MyMsgText(ModMain.MyMsgBoxConverter Converter) {
        Native = new MessageTextController<ModMain.MyMsgBoxType, Validate>(new MessageTextParts {
            Root = this, Button1 = () => Btn1, Button2 = () => Btn2, Button3 = () => Btn3,
            Title = () => LabTitle, Line = () => ShapeLine, Position = () => TransformPos, Rotation = () => TransformRotate,
            Caption = () => PanCaption, CaptionText = () => LabCaption
        }, () => MyConverter, MessageDialogPlatform.CreateHost(), () => Uuid);
        // Same relative order as the two original Handles Me.Loaded handlers.
        SizeChanged += OnViewSizeChanged; Loaded += OnContentHostLoaded; Unloaded += OnContentHostUnloaded; Loaded += OnViewLoaded;
        try {
            InitializeComponent(); Native.RenameButtons(); MyConverter = Converter; Native.Initialize();
        } catch (Exception error) { Native.InitializationFailed(error); }
    }
    private void RefreshContentBounds() => Native.RefreshContentBounds();
    private void OnViewSizeChanged(object sender, SizeChangedEventArgs args) => RefreshContentBounds();
    private void OnContentHostLoaded(object sender, RoutedEventArgs args) => Native.ContentHostLoaded();
    private void OnContentHostUnloaded(object sender, RoutedEventArgs args) => Native.ContentHostUnloaded();
    private void OnViewLoaded(object sender, RoutedEventArgs args) => Native.Load();
    public void Btn1_Click() => Native.Button1Click();
    public void Btn2_Click() => Native.Button2Click();
    public void Btn3_Click() => Native.Button3Click();
    private void OnButton1Click(object sender, MouseButtonEventArgs args) => Btn1_Click();
    private void OnButton2Click(object sender, MouseButtonEventArgs args) => Btn2_Click();
    private void OnButton3Click(object sender, MouseButtonEventArgs args) => Btn3_Click();
    private void OnTitleDrag(object sender, MouseButtonEventArgs args) => Native.Drag(args);
}

// Actual legacy body classes, queue and radio interface only. No dialog state/model copies.
internal static class MessageDialogPlatform {
    internal static MessageDialogHost CreateHost() => new() {
        NewUuid = ModBase.GetUuid, MessagePanel = () => ModMain.FrmMain?.PanMsg, HasWaiting = () => ModMain.WaitingMyMsgBox.Any(),
        DragMove = () => ModMain.FrmMain.DragMove(), PixelToWpf = ModBase.GetWPFSize,
        Animations = () => ControlServices.Current.Animations,
        UpdateWidth = body => (body as ModLauncherUpdate.UpdatePromptView)?.PreferredWidth ?? (body as ReleaseNotesView)?.PreferredWidth ?? (body as ReleaseNotesPromptView)?.PreferredWidth,
        ModpackWidth = body => (body as ModpackDialogView)?.PreferredWidth ?? (body as MmcProjectionReviewView)?.PreferredWidth,
        DependenciesWidth = body => (body as ModDependencyCheckView)?.PreferredWidth,
        GraphWidth = body => (body as ModDependencyGraphView)?.PreferredWidth,
        ImpactsWidth = body => (body as ModDependencyImpactView)?.PreferredWidth,
        VersionSelection = VersionSelection, Radio = Radio
    };
    private static ModalSelectionPort VersionSelection(object body) {
        if (body is MmcPlatformOriginInputView platformInput)
            return new(() => platformInput.CanConfirm, handler => platformInput.ConfirmationChanged += (_, _) => handler());
        if (body is MmcPlatformReleaseSelectView release)
            return new(() => release.CanConfirm, handler => release.ConfirmationChanged += (_, _) => handler());
        if (body is MmcComponentEditorView editor)
            return new(() => editor.CanConfirm, handler => editor.ConfirmationChanged += (_, _) => handler());
        if (body is MmcComponentEditorPreviewView componentPreview)
            return new(() => componentPreview.CanConfirm, handler => componentPreview.ConfirmationChanged += (_, _) => handler());
        if (body is MmcPackageUpdatePreviewView package)
            return new(() => package.CanConfirm, handler => package.ConfirmationChanged += (_, _) => handler());
        if (body is ModpackUpdatePreviewView preview)
            return new(() => preview.CanConfirm, handler => preview.ConfirmationChanged += (_, _) => handler());
        if (body is not ModpackVersionSelectView actual) return null;
        return new(() => actual.Selected is not null, handler => actual.SelectionChanged += (_, _) => handler());
    }
    private static ModalRadioPort Radio(FrameworkElement element) {
        IMyRadio actual = (IMyRadio)element;
        return new(element, handler => actual.Check += (sender, args) => handler((IMyRadio)sender));
    }
}
