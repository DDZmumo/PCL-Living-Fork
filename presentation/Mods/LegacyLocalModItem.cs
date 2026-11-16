using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;
using PCLCS;
using PCLLF.Presentation.Controls;

namespace PCLLF.Presentation.Mods;

// LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 MyLocalModItem.xaml.vb;
// PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8 + LF resource/restore-point reuse.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 Pages/PageInstance/MyLocalModItem.xaml.cs
// (MyLocalCompItem; LICENSE Apache-2.0 / original PCL LICENCE) reviewed for C# structure.
// LF remains the behavior/data source; CE comp models/localization/update decisions not imported.
public sealed class LegacyLocalModItemView {
    public Func<Grid> Root = null!;
    public Func<FrameworkElement> LoaderWarningBorder = null!;
    public Func<TextBlock> Title = null!, Description = null!;
    public Func<TextBlock?> Subtitle = null!;
    public Func<Panel> Tags = null!;
    public Func<FrameworkElement> UpdateButton = null!, ResourceLogoElement = null!, PathLogoElement = null!;
    public Func<FrameworkElement> FavoriteButton = null!;
    public Action<bool> SetFavorite = null!;
    public Func<Image> ResourceLogo = null!;
    public Func<ColumnDefinition> PaddingRight = null!, TitleColumn = null!, SubtitleColumn = null!, ExtendColumn = null!;
    public Action<string?> SetPathLogo = null!, SetPathFallback = null!;
}
public sealed class LegacyLocalModItemHost<TMod,TProject,TVersion,TEntry,TInfo,TButton>
    where TMod : LocalResourceFileData<TProject,TVersion> where TProject : ResourceProjectModelData where TVersion : ResourceVersionModelData
    where TEntry : InstanceResourceEntryData where TInfo : InstanceResourceOnlineInfoData<TEntry,TVersion> where TButton : FrameworkElement {
    public Func<int> Uuid = null!, NameStyle = null!;
    public Func<TMod?> Entry = null!;
    public Action<TMod?> SetEntry = null!;
    // Only the platform project attached by the existing online matcher can be saved.
    public Func<bool> Favorite = null!;
    public Func<TProject?> FavoriteProject = null!;
    public Action<TProject> ToggleFavorite = null!;
    public Func<TEntry?> Resource = null!;
    public Action<TEntry?> SetResource = null!;
    public Func<JObject?> Point = null!;
    public Action<JObject?> SetPoint = null!;
    public Func<EnvironmentHistoryPagePointView> PointView = null!;
    public Func<TEntry,ImageSource?> Icon = null!;
    public Func<string> ImagePath = null!;
    public Func<string,ImageSource> Bitmap = null!;
    public Func<double,double> WpfSize = null!;
    public Func<bool> VisibleInForm = null!, IsLocalItemDirectlyOver = null!, TitleTrimmed = null!, SubtitleTrimmed = null!;
    public Func<bool> RaiseChanged = null!, RaiseCheck = null!;
    public Action<object,MouseButtonEventArgs> RaiseClick = null!;
    public Func<FrameworkElement?> ButtonStack = null!, SelectionBar = null!, DefaultSelectionBar = null!;
    public Action<FrameworkElement?> SetButtonStack = null!;
    public Func<bool> HasButtonHandler = null!;
    public Action<object,EventArgs> InvokeButtonHandler = null!;
    public Action ClearButtonHandler = null!, ResourceDetails = null!, ResourceUpdate = null!;
    public Func<UIElement,LegacyLocalModItem<TMod,TProject,TVersion,TEntry,TInfo,TButton>> Controller = null!;
    public Action<IEnumerable<TMod>> UpdateMods = null!;
    public Action<string> Website = null!;
    public Func<string,string,string,string,string,int> Message = null!;
    public Func<TimeSpan,bool,string> FormatTime = null!;

}
public sealed class LegacyLocalModItem<TMod,TProject,TVersion,TEntry,TInfo,TButton>
    where TMod : LocalResourceFileData<TProject,TVersion> where TProject : ResourceProjectModelData where TVersion : ResourceVersionModelData
    where TEntry : InstanceResourceEntryData where TInfo : InstanceResourceOnlineInfoData<TEntry,TVersion> where TButton : FrameworkElement {
    private readonly LegacyLocalModItemView view;
    private readonly LegacyLocalModItemHost<TMod,TProject,TVersion,TEntry,TInfo,TButton> host;
    private readonly ControlAnimations animation;
    private TInfo? resourceInfo;
    private string? title;
    private string loaderWarning = "";
    private object? loaderWarningOriginalTip;
    public string LoaderWarning => loaderWarning;
    public void RefreshLoaderWarning(Func<string> readMessage) => SetLoaderWarning(host.Entry() is null || !host.Entry()!.IsEnabled ? "" : readMessage());
    public void SetLoaderWarning(string? message) {
        message ??= "";
        if (message == loaderWarning) return;
        if (loaderWarning == "") loaderWarningOriginalTip = view.Root().ToolTip;
        loaderWarning = message;
        view.LoaderWarningBorder().Visibility = message == "" ? Visibility.Collapsed : Visibility.Visible;
        view.Root().ToolTip = message == "" ? loaderWarningOriginalTip : message;
    }
    private bool mouseDown, isChecked, enteredViewport;
    private Border? back, check;
    private Image? stateImage;
    private IEnumerable<TButton>? buttons;
    private static int swipeStart,swipeEnd;
    private static bool swiping,swipeState;
    private static StackPanel? swipePanel;
    private static FrameworkElement? swipeBar;
    public LegacyLocalModItem(LegacyLocalModItemView view,LegacyLocalModItemHost<TMod,TProject,TVersion,TEntry,TInfo,TButton> host,ControlAnimations animation) {this.view=view;this.host=host;this.animation=animation;}
    private bool Enabled => host.Entry() is null || host.Entry()!.IsEnabled;
    private static bool Same(string? a,string? b) => string.Equals(a??"",b??"",StringComparison.Ordinal);
    public string? Title {get=>title;set{string? raw=value;view.Title().TextDecorations=Enabled?null:TextDecorations.Strikethrough;if(Same(view.Title().Text,value))return;view.Title().Text=value;title=raw;}}
    public string SubTitle {get=>view.Subtitle()?.Text??"";set{if(Same(view.Subtitle()!.Text,value))return;view.Subtitle()!.Text=value;view.Subtitle()!.Visibility=Same(value,"")?Visibility.Collapsed:Visibility.Visible;}}
    public string? Description {get=>view.Description().Text;set{if(Same(view.Description().Text,value))return;view.Description().Text=value;}}
    public void SetTags(List<string> tags) {
        view.Tags().Children.Clear();view.Tags().Visibility=tags.Any()?Visibility.Visible:Visibility.Collapsed;
        foreach(string text in tags) {
            Border tag=new(){Background=new SolidColorBrush(Color.FromArgb(10,0,0,0)),Padding=new Thickness(3,1,3,1),CornerRadius=new CornerRadius(3),Margin=new Thickness(0,0,3,0),SnapsToDevicePixels=true,UseLayoutRounding=false};
            tag.Child=new TextBlock{Text=text,Foreground=new SolidColorBrush(Color.FromRgb(176,176,176)),FontSize=11};view.Tags().Children.Add(tag);
        }
    }
    public void BindRestorePoint(JObject value) { SetLoaderWarning(""); EnvironmentHistoryPagePointPresenter.Bind(value,()=>host.SetResource(null),()=>host.SetEntry(null),point=>host.SetPoint(point),Refresh); }
    public void BindResource(TEntry value,TInfo info) {SetLoaderWarning("");host.SetPoint(null);host.SetResource(value);resourceInfo=info;RefreshFavorite();RefreshResource();}
    private double FavoriteSlotWidth => view.FavoriteButton().Visibility == Visibility.Visible ? 30 : 0;
    public void RefreshFavorite() {
        TProject? project = host.FavoriteProject();
        bool canFavorite = project is not null && host.Point() is null &&
            ResourceFavoriteIdentity.TryCreate(project.Platform, project.Id, out _) && (project.Types & ResourceFavorite.SupportedTypes) != 0;
        double previousSlot = FavoriteSlotWidth;
        view.FavoriteButton().Visibility = canFavorite ? Visibility.Visible : Visibility.Collapsed;
        if (canFavorite) view.SetFavorite(host.Favorite());
        double slot = FavoriteSlotWidth;
        if (host.ButtonStack() is { } actions) actions.Margin = new Thickness(0,0,5+slot,0);
        // Add only the changed slot. A late platform match may arrive while the
        // native hover-width animation is running; its remaining deltas still apply.
        if (slot != previousSlot) view.PaddingRight().Width = new GridLength(Math.Max(0,view.PaddingRight().Width.Value+slot-previousSlot));
    }
    public void FavoriteClick() {
        if (host.Point() is not null) return;
        TProject? project = host.FavoriteProject();
        if (project is null || !ResourceFavoriteIdentity.TryCreate(project.Platform, project.Id, out _) ||
            (project.Types & ResourceFavorite.SupportedTypes) == 0) return;
        host.ToggleFavorite(project);
        RefreshFavorite();
    }
    private void RefreshResource() {
        TEntry resource=host.Resource()!;string? localName=resource.IsDirectory?resource.Name:System.IO.Path.GetFileNameWithoutExtension(resource.Name);
        Title=string.IsNullOrWhiteSpace(resourceInfo?.ProjectName)?localName:resourceInfo!.ProjectName;
        SubTitle=resourceInfo?.CurrentVersion is null?"":"  |  "+(resourceInfo.CurrentVersion.Version??resourceInfo.CurrentVersion.Display);
        Description=localName+": "+resource.Description;
        view.Root().ToolTip=resource.Name+"\r\n"+resource.Description+(resourceInfo is null?"":"\r\n"+resourceInfo.Status);
        view.UpdateButton().Visibility=resourceInfo is not null&&resourceInfo.HasUpdate?Visibility.Visible:Visibility.Collapsed;
        view.UpdateButton().ToolTip=resourceInfo is null?"":resourceInfo.Status+"\r\n点击以更新，右键查看资源详情。";
        SetTags(resourceInfo?.Tags??new List<string>());view.Title().SetResourceReference(TextBlock.ForegroundProperty,Checked?"ColorBrush2":"ColorBrush1");
        bool remote=Uri.TryCreate(resourceInfo?.IconUrl,UriKind.Absolute,out Uri? address)&&address.Scheme=="https"&&address.UserInfo=="";
        if(remote&&enteredViewport){view.ResourceLogoElement().Visibility=Visibility.Collapsed;view.ResourceLogo().Source=null;view.PathLogoElement().Visibility=Visibility.Visible;view.SetPathLogo(address!.AbsoluteUri);}
        else if(host.Icon(resource) is not null){view.PathLogoElement().Visibility=Visibility.Collapsed;view.ResourceLogoElement().Visibility=Visibility.Visible;view.ResourceLogo().Source=host.Icon(resource);}
        else{view.ResourceLogoElement().Visibility=Visibility.Collapsed;view.ResourceLogo().Source=null;view.PathLogoElement().Visibility=Visibility.Visible;view.SetPathLogo(host.ImagePath()+"Icons/NoIcon.png");}
    }
    public void MouseUp(object sender,MouseButtonEventArgs e) {if(mouseDown)Activate(sender,e);}
    public void Activate(object sender,MouseButtonEventArgs e) {if(!view.Root().IsEnabled)return;host.RaiseClick(sender,e);if(e.Handled)return;Logger.Info($"按下本地 Mod 列表项：{view.Title().Text}");}
    public void MouseDown(){if(!view.Root().IsMouseDirectlyOver)return;mouseDown=true;if(host.ButtonStack() is not null)host.ButtonStack()!.IsHitTestVisible=false;}
    public void MouseLeave(){mouseDown=false;if(host.ButtonStack() is not null)host.ButtonStack()!.IsHitTestVisible=true;}
    private static bool Swiping {get=>swiping;set{swiping=value;if(swipeBar is not null)swipeBar.IsHitTestVisible=!value;}}
    public void SwipeStart() {
        if(view.Root().Parent is null)return;
        if(swipeBar is not null)swipeBar.IsHitTestVisible=true;
        swipePanel=view.Root().Parent as StackPanel;swipeBar=host.SelectionBar()??host.DefaultSelectionBar();
        int index=((StackPanel)view.Root().Parent).Children.IndexOf(view.Root());swipeStart=index;swipeEnd=index;Swiping=true;swipeState=!Checked;
    }
    public void Swipe(object e) {
        if(view.Root().Parent is null||!ReferenceEquals(view.Root().Parent,swipePanel))return;
        if(Mouse.LeftButton!=MouseButtonState.Pressed||!host.IsLocalItemDirectlyOver()){Swiping=false;return;}
        if(!Swiping)return;
        UIElementCollection elements=((StackPanel)view.Root().Parent).Children;int index=elements.IndexOf(view.Root());
        swipeStart=Math.Min(swipeStart,index).Clamp(0,elements.Count-1);swipeEnd=Math.Max(swipeEnd,index).Clamp(0,elements.Count-1);
        if(swipeStart==swipeEnd)return;
        for(int i=swipeStart;i<=swipeEnd;i++){var item=host.Controller(elements[i]);item.InitLate(elements[i],(EventArgs)e);item.Checked=swipeState;}
    }
    public bool Checked {get=>isChecked;set{
        try {
            bool old=isChecked;if(value==isChecked)return;isChecked=value;
            if(view.Root().IsInitialized&&host.RaiseChanged()){isChecked=old;return;}
            if(value&&host.RaiseCheck())return;
            if(host.VisibleInForm()) {
                List<ControlAnimation> frames=new();
                if(Checked){double delta=32-RectCheck.ActualHeight;frames.Add(animation.Height(RectCheck,delta*.4,new(200,ease:ControlEase.OutFluent,power:ControlEasePower.Weak)));frames.Add(animation.Height(RectCheck,delta*.6,new(300,ease:ControlEase.OutBack,power:ControlEasePower.Weak)));frames.Add(animation.Opacity(RectCheck,1-RectCheck.Opacity,new(30)));RectCheck.VerticalAlignment=VerticalAlignment.Center;RectCheck.Margin=new Thickness(-3,0,0,0);frames.Add(animation.ColorResource(view.Title(),TextBlock.ForegroundProperty,Enabled?"ColorBrush2":"ColorBrush5",new(200)));}
                else{frames.Add(animation.Height(RectCheck,-RectCheck.ActualHeight,new(120,ease:ControlEase.InFluent,power:ControlEasePower.Weak)));frames.Add(animation.Opacity(RectCheck,-RectCheck.Opacity,new(70,40)));RectCheck.VerticalAlignment=VerticalAlignment.Center;frames.Add(animation.ColorResource(view.Title(),TextBlock.ForegroundProperty,view.Title().TextDecorations is null?"ColorBrush1":"ColorBrushGray4",new(120)));}
                animation.Start(frames,"MyLocalModItem Checked "+host.Uuid());
            } else {
                RectCheck.VerticalAlignment=VerticalAlignment.Center;RectCheck.Margin=new Thickness(-3,0,0,0);RectCheck.Height=Checked?32:0;RectCheck.Opacity=Checked?1:0;
                view.Title().SetResourceReference(TextBlock.ForegroundProperty,Checked?(Enabled?"ColorBrush2":"ColorBrush5"):(Enabled?"ColorBrush1":"ColorBrushGray4"));animation.Stop("MyLocalModItem Checked "+host.Uuid());
            }
        }catch(Exception error){Logger.Warn(error,"设置 Checked 失败");}
    }}
    public Border RectBack {get{
        if(back is null){Border rect=new(){Name="RectBack",CornerRadius=new CornerRadius(3),RenderTransform=new ScaleTransform(.8,.8),RenderTransformOrigin=new Point(.5,.5),BorderThickness=new Thickness(host.WpfSize(1)),SnapsToDevicePixels=true,IsHitTestVisible=false,Opacity=0};rect.SetResourceReference(Border.BackgroundProperty,"ColorBrush7");rect.SetResourceReference(Border.BorderBrushProperty,"ColorBrush6");Grid.SetColumnSpan(rect,999);Grid.SetRowSpan(rect,999);view.Root().Children.Insert(0,rect);back=rect;}return back;
    }}
    public IEnumerable<TButton>? Buttons {get=>buttons;set{
        buttons=value;if(host.ButtonStack() is not null){view.Root().Children.Remove(host.ButtonStack());host.SetButtonStack(null);}
        if(!value!.Any()){view.PaddingRight().Width=new GridLength(4+FavoriteSlotWidth);return;}
        host.SetButtonStack(new StackPanel{Opacity=0,Margin=new Thickness(0,0,5+FavoriteSlotWidth,0),SnapsToDevicePixels=false,Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center,UseLayoutRounding=false});
        Grid.SetColumnSpan(host.ButtonStack()!,10);Grid.SetRowSpan(host.ButtonStack()!,10);
        foreach(TButton button in value!){if(button.Height.Equals(double.NaN))button.Height=25;if(button.Width.Equals(double.NaN))button.Width=25;((StackPanel)host.ButtonStack()!).Children.Add(button);}view.Root().Children.Add(host.ButtonStack());
    }}
    public Border RectCheck {get{
        if(check is null){check=new Border{Width=5,Height=Checked?double.NaN:0,CornerRadius=new CornerRadius(2,2,2,2),VerticalAlignment=Checked?VerticalAlignment.Stretch:VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Left,UseLayoutRounding=false,SnapsToDevicePixels=false,Margin=Checked?new Thickness(-3,6,0,6):new Thickness(-3,0,0,0)};check.SetResourceReference(Border.BackgroundProperty,"ColorBrush3");Grid.SetRowSpan(check,10);view.Root().Children.Add(check);}return check;
    }}
    public string GetUpdateCompareDescription()=>LegacyLocalModText.Compare<TMod,TProject,TVersion>(host.Entry()!,host.FormatTime);
    public void FirstViewport(){enteredViewport=true;Refresh();}
    public void Refresh(){
        RefreshFavorite();
        if(host.Entry() is null || !host.Entry()!.IsEnabled)SetLoaderWarning("");
        if(host.Point() is not null){EnvironmentHistoryPagePointPresenter.Refresh(host.PointView());return;}
        if(host.Resource() is not null){RefreshResource();return;}
        if(!enteredViewport)return;
        TMod entry=host.Entry()!;
        if(entry.HasUpdate){view.UpdateButton().Visibility=Visibility.Visible;view.UpdateButton().ToolTip=GetUpdateCompareDescription()+"\r\n点击以更新，右键查看更新日志。";}else view.UpdateButton().Visibility=Visibility.Collapsed;
        string fileName=PathUtils.GetFileNameWithoutExtension(entry.EnabledName),description;
        if(host.NameStyle()==1){Title=fileName;SubTitle="";if(entry.Project is null)description=entry.Display;else{var titles=entry.Project.GetControlTitle(false);description=titles.Title+titles.SubTitle;}description=description.Replace("  |  "," / ");if(entry.Version is not null)description+=$" ({entry.Version})";}
        else{if(entry.Project is null){Title=entry.Display;SubTitle=entry.Version is null?"":"  |  "+entry.Version;}else{var titles=entry.Project.GetControlTitle(false);Title=titles.Title;SubTitle=titles.SubTitle+(entry.Version is null?"":"  |  "+entry.Version);}description=fileName;}
        if(entry.Project is not null)description+=": "+MeloongCore.Extensions.StringExtensions.ReplaceLineEndings(entry.Project.Description!,"");else if(entry.Description is not null)description+=": "+MeloongCore.Extensions.StringExtensions.ReplaceLineEndings(entry.Description,"");Description=description;
        view.Title().SetResourceReference(TextBlock.ForegroundProperty,Checked?(entry.IsEnabled?"ColorBrush2":"ColorBrush5"):(entry.IsEnabled?"ColorBrush1":"ColorBrushGray4"));
        if(entry.Project is not null)ResourcePresentation.ApplyLogo(entry.Project,view.SetPathLogo,view.SetPathFallback);else view.SetPathLogo(host.ImagePath()+"Icons/NoIcon.png");
        if(entry.IsEnabled){if(stateImage is not null){view.Root().Children.Remove(stateImage);stateImage=null;}}
        else{if(stateImage is null){stateImage=new Image{Width=20,Height=20,Margin=new Thickness(0,0,-5,-3),IsHitTestVisible=false,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom};RenderOptions.SetBitmapScalingMode(stateImage,BitmapScalingMode.HighQuality);Grid.SetColumn(stateImage,1);Grid.SetRow(stateImage,1);Grid.SetRowSpan(stateImage,2);view.Root().Children.Add(stateImage);}stateImage.Source=host.Bitmap(host.ImagePath()+"Icons/Disabled.png");}
        if(entry.Project is not null)SetTags(entry.Project.Tags);
    }
    public void RefreshColor(object sender,EventArgs e){
        InitLate(sender,e);int time=view.Root().IsMouseOver?120:180;List<ControlAnimation> frames=new();
        if(host.ButtonStack() is not null){
            if(view.Root().IsMouseOver){frames.Add(animation.Opacity(host.ButtonStack()!,1-host.ButtonStack()!.Opacity,new(time*.7,time*.3)));frames.Add(animation.Double(delta=>view.PaddingRight().Width=new GridLength(Math.Max(0,view.PaddingRight().Width.Value+delta)),5+FavoriteSlotWidth+Buttons!.Count()*25-view.PaddingRight().Width.Value,new(time*.3,time*.7)));}
            else{frames.Add(animation.Opacity(host.ButtonStack()!,-host.ButtonStack()!.Opacity,new(time*.4)));frames.Add(animation.Double(delta=>view.PaddingRight().Width=new GridLength(Math.Max(0,view.PaddingRight().Width.Value+delta)),4+FavoriteSlotWidth-view.PaddingRight().Width.Value,new(time*.4)));}
        }
        LegacyLocalModSelection.Append(animation,frames,RectBack,view.Root().IsMouseOver,Checked,mouseDown);animation.Start(frames,"LocalModItem Color "+host.Uuid());
    }
    public void InitLate(object sender,EventArgs e){if(host.HasButtonHandler()){host.InvokeButtonHandler(sender,e);host.ClearButtonHandler();}}
    public void ShowUpdateLog(){
        if(host.Point() is not null)return;if(host.Resource() is not null){host.ResourceDetails();return;}
        TMod entry=host.Entry()!;string? curse=entry.ChangelogUrls.FirstOrDefault(url=>url.Contains("curseforge.com")),modrinth=entry.ChangelogUrls.FirstOrDefault(url=>url.Contains("modrinth.com"));
        if(curse is null||modrinth is null)host.Website(entry.ChangelogUrls.First());else switch(host.Message("要在哪个网站上查看更新日志？","查看更新日志","Modrinth","CurseForge","取消")){case 1:host.Website(modrinth);break;case 2:host.Website(curse);break;}
    }
    public void UpdateClick(){
        if(host.Point() is not null)return;if(host.Resource() is not null){host.ResourceUpdate();return;}
        switch(host.Message($"是否要更新 {host.Entry()!.Display}？\r\n\r\n{GetUpdateCompareDescription()}","Mod 更新确认","更新","查看更新日志","取消")){case 1:host.UpdateMods(new[]{host.Entry()!});break;case 2:ShowUpdateLog();break;}
    }
    public void ReleaseResourceInteraction(){
        if(host.Resource() is null&&host.Point() is null)return;mouseDown=false;Swiping=false;
        if(ReferenceEquals(host.SelectionBar(),swipeBar)){swipeBar=null;swipePanel=null;}animation.Stop("MyLocalModItem Checked "+host.Uuid());animation.Stop("LocalModItem Color "+host.Uuid());
    }
    public void ResizeTitle(){
        int current=view.ExtendColumn().Width.IsStar?0:view.TitleColumn().Width.IsStar?2:1,next;
        switch(current){case 0:if(view.ExtendColumn().ActualWidth<.5)next=view.Subtitle()!.Visibility==Visibility.Collapsed?2:1;else return;break;case 1:if(view.SubtitleColumn().ActualWidth<.5)next=2;else if(!host.SubtitleTrimmed())next=0;else return;break;default:if(!host.TitleTrimmed())next=view.Subtitle()!.Visibility==Visibility.Collapsed?0:1;else return;break;}
        switch(next){case 0:view.TitleColumn().Width=GridLength.Auto;view.SubtitleColumn().Width=GridLength.Auto;view.ExtendColumn().Width=new GridLength(1,GridUnitType.Star);break;case 1:view.TitleColumn().Width=GridLength.Auto;view.SubtitleColumn().Width=new GridLength(1,GridUnitType.Star);view.ExtendColumn().Width=new GridLength(0,GridUnitType.Pixel);break;case 2:view.TitleColumn().Width=new GridLength(1,GridUnitType.Star);view.SubtitleColumn().Width=new GridLength(0,GridUnitType.Pixel);view.ExtendColumn().Width=new GridLength(0,GridUnitType.Pixel);break;}
    }
}
