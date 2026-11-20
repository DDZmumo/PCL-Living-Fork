param([Parameter(Mandatory=$true)][string]$ExecutablePath,[string]$PreviewDirectory)
# A separate fixture EXE owns AppContext.BaseDirectory, so real PCL settings are never written.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$ExecutablePath=[IO.Path]::GetFullPath($ExecutablePath)
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('PclSourceSettings-'+[Guid]::NewGuid().ToString('N'))
$framework=Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
$references=@('System.dll','System.Core.dll','System.Xaml.dll','System.Xml.dll',(Join-Path $framework 'WindowsBase.dll'),(Join-Path $framework 'PresentationFramework.dll'),(Join-Path $framework 'PresentationCore.dll'))
$source=@'
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
public static class SourceSettingsCheck {
 const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
 static Assembly asm;static Type settings;static IDictionary entries;static PropertyInfo animation;
 static object Call(Type t,string n,params object[] args){return t.GetMethods(F).Single(m=>m.Name==n&&!m.IsGenericMethod&&m.GetParameters().Length==args.Length).Invoke(null,args);}
 static void Require(bool value,string message){if(!value)throw new Exception(message);}
 static IDictionary Cache(){var entry=entries["ToolLauncherUpdateSource"];return (IDictionary)entry.GetType().GetField("ValueCache",F).GetValue(entry);}
 static object NewPage(){animation.SetValue(null,1,null);try{return Activator.CreateInstance(asm.GetType("PCL.PageSetupSystem"));}finally{animation.SetValue(null,0,null);}}
 static ComboBox Combo(object page){return (ComboBox)((FrameworkElement)page).FindName("ComboLauncherUpdateSource");}
 static string Preference(){return Call(asm.GetType("PCL.ModLauncherUpdate"),"PreferredUpdateSource").ToString();}
 static IDictionary EntryCache(string key){return (IDictionary)entries[key].GetType().GetField("ValueCache",F).GetValue(entries[key]);}
 static void ClearKey(string key){EntryCache(key).Remove("");Call(asm.GetType("PCL.ModBase"),"IniClearCache","Setup");}
 static FrameworkElement Detach(FrameworkElement value){var owner=LogicalTreeHelper.GetParent(value) as Panel;if(owner!=null)owner.Children.Remove(value);return value;}
 static double Lum(Color c){Func<byte,double> v=b=>{double n=b/255.0;return n<=0.04045?n/12.92:Math.Pow((n+0.055)/1.055,2.4);};return 0.2126*v(c.R)+0.7152*v(c.G)+0.0722*v(c.B);}
 static Color ColorOf(Brush value){Require(value is SolidColorBrush,"Expected a solid semantic brush");return ((SolidColorBrush)value).Color;}
 static double Contrast(Color a,Color b){return (Math.Max(Lum(a),Lum(b))+0.05)/(Math.Min(Lum(a),Lum(b))+0.05);}
 static void CheckDarkChrome(Application app,Window window,string root,string preview){
  var xml=new XmlDocument();xml.Load(Path.Combine(root,"Plain Craft Launcher 2/FormMain.xaml"));
  var ns=new XmlNamespaceManager(xml.NameTable);ns.AddNamespace("x","http://schemas.microsoft.com/winfx/2006/xaml");
  var node=(XmlElement)xml.SelectSingleNode("//*[@x:Name='PanTitle']",ns);
  node.SetAttribute("xmlns:local","clr-namespace:PCL;assembly=LivingPCL");node.SetAttribute("xmlns:x","http://schemas.microsoft.com/winfx/2006/xaml");node.SetAttribute("xmlns","http://schemas.microsoft.com/winfx/2006/xaml/presentation");
  Func<Grid> create=()=>{animation.SetValue(null,1,null);try{return (Grid)System.Windows.Markup.XamlReader.Parse(node.OuterXml.Replace("clr-namespace:PCL\"","clr-namespace:PCL;assembly=LivingPCL\""));}finally{animation.SetValue(null,0,null);}};
  var main=create();var inner=create();var stack=new StackPanel();stack.SetResourceReference(Panel.BackgroundProperty,"ColorBrushBackground");stack.Children.Add(main);stack.Children.Add(inner);
  foreach(var title in new[]{main,inner}){
   title.Margin=new Thickness(0,0,0,14);((FrameworkElement)title.FindName("BtnTitleSelect2")).Visibility=Visibility.Collapsed;
   var logo=(TextBlock)title.FindName("LabTitleLogo");logo.Text="PCL-LF";logo.Visibility=Visibility.Visible;((FrameworkElement)title.FindName("ShapeTitleLogo")).Visibility=Visibility.Collapsed;
  }
  var selected=(FrameworkElement)main.FindName("BtnTitleSelect3");((FrameworkElement)main.FindName("BtnTitleSelect0")).GetType().GetProperty("Checked").SetValue(main.FindName("BtnTitleSelect0"),false,null);selected.GetType().GetProperty("Checked").SetValue(selected,true,null);
  ((FrameworkElement)inner.FindName("PanTitleMain")).Visibility=Visibility.Collapsed;var innerPanel=(FrameworkElement)inner.FindName("PanTitleInner");innerPanel.Visibility=Visibility.Visible;innerPanel.Opacity=1;innerPanel.Margin=new Thickness(0);
  var label=(TextBlock)inner.FindName("LabTitleInner");label.Text="版本选择";
  var slider=(FrameworkElement)Activator.CreateInstance(asm.GetType("PCL.MySlider"));slider.Width=400;slider.Margin=new Thickness(25);stack.Children.Add(slider);
  var owner=new Border{Width=240,Height=30,IsEnabled=false,ToolTip="需要支持 Quick Play 的 Java 1.20 或更新版本，记录仅保存在此电脑。"};ToolTipService.SetShowOnDisabled(owner,true);stack.Children.Add(owner);
  window.Content=stack;window.Width=950;window.Height=440;window.UpdateLayout();window.Dispatcher.Invoke(new Action(()=>{}),System.Windows.Threading.DispatcherPriority.ApplicationIdle);
  EntryCache("UiLauncherColorMode")[""]=1;Call(asm.GetType("PCL.ModSecret"),"ThemeRefresh",0);
  main.Background=(Brush)Call(asm.GetType("PCL.ModSecret"),"CreateTitleBackground");inner.Background=(Brush)Call(asm.GetType("PCL.ModSecret"),"CreateTitleBackground");
  animation.SetValue(null,1,null);try{foreach(var title in new[]{main,inner})foreach(string name in new[]{"BtnTitleMin","BtnTitleClose","BtnTitleInner"}){var button=(FrameworkElement)title.FindName(name);button.GetType().GetMethod("RefreshAnim",F).Invoke(button,null);}}finally{animation.SetValue(null,0,null);}
  var tipType=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="MeloongCore.Wpf").GetType("MeloongCore.Wpf.ModernTooltipService");Require(tipType!=null,"Modern tooltip type not found");
  tipType.GetField("currentOwner",F).SetValue(null,owner);tipType.GetMethod("Show",F).Invoke(null,new object[]{owner,new Point(0,0)});tipType.GetMethod("HideImmediately",F).Invoke(null,null);
  var tip=(Border)tipType.GetField("popupCard",F).GetValue(null);Require(tip!=null,"Modern tooltip was not created");tipType.GetMethod("UpdateContent",F).Invoke(null,new object[]{owner,owner.ToolTip,null});tip.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent,tip));((FrameworkElement)tip.Child).RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent,tip.Child));
  if(tip.Parent is Panel)((Panel)tip.Parent).Children.Remove(tip);tip.Opacity=1;tip.RenderTransform=Transform.Identity;tip.Margin=new Thickness(16);stack.Children.Add(tip);
  var sliderPopup=(System.Windows.Controls.Primitives.Popup)slider.FindName("Popup");var sliderTip=(Border)sliderPopup.Child;((TextBlock)slider.FindName("TextHint")).Text="75%";
  sliderPopup.Child=null;sliderTip.HorizontalAlignment=HorizontalAlignment.Left;sliderTip.Margin=new Thickness(25,0,0,12);stack.Children.Add(sliderTip);
  window.UpdateLayout();window.Dispatcher.Invoke(new Action(()=>{}),System.Windows.Threading.DispatcherPriority.ApplicationIdle);Render(stack,preview,"dark-titlebars-and-popups.png");
  var colors=main.Background is GradientBrush?((GradientBrush)main.Background).GradientStops.Select(stop=>stop.Color).ToArray():new[]{ColorOf(main.Background)};
  var problems=new System.Collections.Generic.List<string>();
  if(!(main.Background is LinearGradientBrush)||colors.Length!=3||colors[0]==colors[2])problems.Add("Dark titlebar lost the original blue-hue gradient");
  foreach(Color color in colors){int max=Math.Max(color.R,Math.Max(color.G,color.B)),min=Math.Min(color.R,Math.Min(color.G,color.B));if(max-min>135||Lum(color)>0.08)problems.Add("Dark titlebar is too bright");if(color.B-color.R<45||color.B-color.G<25)problems.Add("Dark titlebar is not distinctly blue");}
  foreach(var title in new[]{main,inner})foreach(string name in new[]{"BtnTitleMin","BtnTitleClose","BtnTitleInner"}){var button=(FrameworkElement)title.FindName(name);var path=(System.Windows.Shapes.Path)button.FindName("Path");if(Contrast(ColorOf(path.Fill),colors[0])<4.5)problems.Add("Title icon has insufficient contrast: "+name);}
  if(Contrast(ColorOf(label.Foreground),colors[0])<4.5)problems.Add("Subpage title has insufficient contrast");
  if(ColorOf(tip.Background).R>100||ColorOf(((TextBlock)tip.Child).Foreground).R<170)problems.Add("Modern tooltip bypasses the dark theme");
  if(ColorOf(sliderTip.Background).R>100)problems.Add("Slider popup still has a white background");
  Require(problems.Count==0,string.Join("; ",problems));
  tipType.GetMethod("UpdateContent",F).Invoke(null,new object[]{owner,"替换后的悬停提示",null});window.UpdateLayout();window.Dispatcher.Invoke(new Action(()=>{}),System.Windows.Threading.DispatcherPriority.ApplicationIdle);
  Require(ColorOf(((TextBlock)tip.Child).Foreground).R>170,"Replacement modern-tooltip content lost its theme (card loaded="+tip.IsLoaded+", child loaded="+((TextBlock)tip.Child).IsLoaded+")");
  EntryCache("UiLauncherColorMode")[""]=0;Call(asm.GetType("PCL.ModSecret"),"ThemeRefresh",0);Require(Call(asm.GetType("PCL.ModSecret"),"CreateTitleBackground") is LinearGradientBrush,"Light titlebar lost its existing gradient");Require(ColorOf(tip.Background).R>240&&ColorOf(sliderTip.Background).R>240,"Popup surfaces did not return to light mode");
  Console.WriteLine("PASS: actual main/subpage title markup, blue-hue dark gradient, readable back/minimize/close icons, themed real ModernTooltipService popup and replacement content, slider tooltip and light-mode restoration.");
 }
 static void CheckChannelsAndTheme(Application app,Window window,string preview,string ini){
  var updater=asm.GetType("PCL.ModLauncherUpdate");var page=NewPage();var channel=(ComboBox)((FrameworkElement)page).FindName("ComboLauncherUpdateChannel");
  var version=(FrameworkElement)((FrameworkElement)page).FindName("LabLauncherVersion");
  Require(Grid.GetRow(version)==0&&Grid.GetRow(Combo(page))==2&&Grid.GetRow(channel)==4,"Version/source/channel rows are in the wrong order");
  Require(Convert.ToInt32(entries["ToolLauncherUpdateChannel"].GetType().GetField("DefaultValue").GetValue(entries["ToolLauncherUpdateChannel"]))==0&&channel.SelectedIndex==0,"Default update channel is not stable only");
  Require((string)Call(asm.GetType("PCL.SettingService"),"GetKey",channel)==null,"Channel selector can persist before confirmation");
  Require(channel.Items.Cast<ComboBoxItem>().All(item=>item.Content.ToString().Contains("接收")&&!item.Content.ToString().Contains("接受")),"Update channel wording is inconsistent");
  int confirmations=0;var confirm=page.GetType().GetField("ConfirmPrereleaseUpdates",F);
  confirm.SetValue(page,new Func<bool>(()=>{confirmations++;return false;}));channel.SelectedIndex=1;
  Require(confirmations==1&&channel.SelectedIndex==0&&Call(updater,"PreferredUpdateChannel").ToString()=="Stable"&&!File.ReadAllText(ini).Contains("ToolLauncherUpdateChannel:1"),"Cancelling the warning enabled prerelease updates");
  confirm.SetValue(page,new Func<bool>(()=>{confirmations++;return true;}));channel.SelectedIndex=1;
  Require(confirmations==2&&File.ReadAllText(ini).Contains("ToolLauncherUpdateChannel:1"),"Confirmed prerelease preference was not saved");
  ClearKey("ToolLauncherUpdateChannel");var reopened=NewPage();Require(((ComboBox)((FrameworkElement)reopened).FindName("ComboLauncherUpdateChannel")).SelectedIndex==1&&confirmations==2,"Reopening or rereading a saved channel asked for consent again");
  channel.SelectedIndex=0;Require(confirmations==2&&Call(updater,"PreferredUpdateChannel").ToString()=="Stable","Returning to stable updates asked for another warning");
  Call(settings,"Reset","ToolLauncherUpdateChannel",null);ClearKey("ToolLauncherUpdateChannel");Require(Call(updater,"PreferredUpdateChannel").ToString()=="Stable","Channel reset is not stable");
  animation.SetValue(null,1,null);var ui=(FrameworkElement)Activator.CreateInstance(asm.GetType("PCL.PageSetupUI"));Call(asm.GetType("PCL.SettingService"),"RefreshSettings",ui);animation.SetValue(null,0,null);
  var mode=(ComboBox)ui.FindName("ComboLauncherColorMode");Require(mode.Items.Count==2&&mode.SelectedIndex==0,"Light/dark preference control is absent or default changed");
  DependencyObject systemCard=channel;while(systemCard!=null&&systemCard.GetType().Name!="MyCard")systemCard=LogicalTreeHelper.GetParent(systemCard);
  page.GetType().GetMethod("Reload",F).Invoke(page,null);
  var group=new StackPanel();group.SetResourceReference(Panel.BackgroundProperty,"ColorBrushBackground");group.Children.Add(Detach((FrameworkElement)systemCard));group.Children.Add(Detach((FrameworkElement)ui.FindName("CardLauncher")));
  var stateRow=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(15)};
  var disabled=(FrameworkElement)Activator.CreateInstance(asm.GetType("PCL.MyButton"));disabled.GetType().GetProperty("Text").SetValue(disabled,"禁用按钮",null);disabled.IsEnabled=false;disabled.Width=120;disabled.Height=32;stateRow.Children.Add(disabled);
  var input=(TextBox)Activator.CreateInstance(asm.GetType("PCL.MyTextBox"));input.Text="输入框与文字";input.Width=180;input.Height=32;input.Margin=new Thickness(12,0,0,0);stateRow.Children.Add(input);
  var selected=(FrameworkElement)Activator.CreateInstance(asm.GetType("PCL.MyRadioButton"));selected.GetType().GetProperty("ColorType").SetValue(selected,Enum.ToObject(asm.GetType("PCL.MyRadioButton+ColorState"),1),null);selected.GetType().GetProperty("Text").SetValue(selected,"已选择",null);selected.GetType().GetProperty("Checked").SetValue(selected,true,null);selected.Margin=new Thickness(12,0,0,0);stateRow.Children.Add(selected);group.Children.Add(stateRow);
  var hint=(FrameworkElement)Activator.CreateInstance(asm.GetType("PCL.MyHint"));hint.GetType().GetProperty("Text").SetValue(hint,"预发布版本可能存在尚未发现的问题。",null);hint.GetType().GetProperty("Theme").SetValue(hint,Enum.ToObject(asm.GetType("PCL.MyHint+Themes"),2),null);hint.Margin=new Thickness(15);group.Children.Add(hint);
  window.Content=group;window.Width=800;window.Height=850;window.UpdateLayout();window.Dispatcher.Invoke(new Action(()=>{}),System.Windows.Threading.DispatcherPriority.ApplicationIdle);
  Call(asm.GetType("PCL.ModSecret"),"ThemeRefresh",0);window.UpdateLayout();Render(group,preview,"theme-settings-light.png");
  var card=ui.FindName("CardLauncher");var cardBorder=(Border)card.GetType().GetField("MainBorder",F).GetValue(card);
  Require(ColorOf(cardBorder.Background).R>240,"Light card surface changed");
  mode.SelectedIndex=1;window.UpdateLayout();window.Dispatcher.Invoke(new Action(()=>{}),System.Windows.Threading.DispatcherPriority.ApplicationIdle);
  Require((bool)asm.GetType("PCL.ModTheme").GetProperty("IsDarkTheme",F).GetValue(null,null)&&File.ReadAllText(ini).Contains("UiLauncherColorMode:1"),"Dark theme was not applied and saved");
  Require(ColorOf(cardBorder.Background).R<70&&ColorOf(input.Foreground).R>170,"An existing card or editable control retained light-theme colors");
  Color fg=ColorOf((Brush)app.Resources["ColorBrush1"]),bg=ColorOf((Brush)app.Resources["ColorBrushWhite"]);double contrast=(Math.Max(Lum(fg),Lum(bg))+0.05)/(Math.Min(Lum(fg),Lum(bg))+0.05);Require(contrast>=4.5,"Dark body text contrast is insufficient");
  Require(ColorOf(((Border)disabled.FindName("PanFore")).BorderBrush).R<170,"Disabled button kept a light border");
  var selectedText=(TextBlock)selected.FindName("LabText");Require(ColorOf(selectedText.Foreground).R<70,"Selected accent text is unreadable in dark mode");
  Require(ColorOf(((Border)hint).Background).R<100&&ColorOf(((TextBlock)hint.FindName("LabText")).Foreground).R>200,"Warning notice retained a bright panel or dark text");
  var popup=(System.Windows.Controls.Primitives.Popup)mode.Template.FindName("PART_Popup",mode);Require(ColorOf(((Border)((Grid)popup.Child).Children[0]).Background).R<80,"Drop-down popup retained a white background");
  var tooltip=new ToolTip{Content="深色工具提示"};tooltip.ApplyTemplate();Require(ColorOf(tooltip.Background).R<80&&ColorOf(tooltip.Foreground).R>170,"Native tooltip palette is incomplete");
  var menu=new ContextMenu();menu.ApplyTemplate();Require(ColorOf((Brush)app.Resources[SystemColors.MenuBrushKey]).R<80,"Native menu brush was not themed");
  var converter=Activator.CreateInstance(asm.GetType("PCL.ModMain+MyMsgBoxConverter"));converter.GetType().GetField("Title",F).SetValue(converter,"接收预发布版更新");converter.GetType().GetField("Text",F).SetValue(converter,"预发布版本可能存在尚未发现的问题。");converter.GetType().GetField("IsWarn",F).SetValue(converter,true);converter.GetType().GetField("Button1",F).SetValue(converter,"启用预发布更新");converter.GetType().GetField("Button2",F).SetValue(converter,"取消");
  var warning=(FrameworkElement)Activator.CreateInstance(asm.GetType("PCL.MyMsgText"),new object[]{converter});((RotateTransform)warning.FindName("TransformRotate")).Angle=0;((TranslateTransform)warning.FindName("TransformPos")).Y=0;warning.Margin=new Thickness(0);warning.HorizontalAlignment=HorizontalAlignment.Left;warning.VerticalAlignment=VerticalAlignment.Top;warning.Measure(new Size(640,360));warning.Arrange(new Rect(0,0,640,360));Require(ColorOf(((Border)warning.FindName("PanBorder")).Background).R<80,"Confirmation dialog retained a white panel");Render(warning,preview,"theme-prerelease-warning-dark.png");
  window.UpdateLayout();Render(group,preview,"theme-settings-dark.png");ClearKey("UiLauncherColorMode");Call(asm.GetType("PCL.ModSecret"),"ThemeRefresh",0);Require((bool)asm.GetType("PCL.ModTheme").GetProperty("IsDarkTheme",F).GetValue(null,null),"Dark preference could not be reread from disk");
  var opacity=ui.FindName("SliderPanelOpacity");Require(opacity!=null,"Unified panel opacity control is missing");int originalWindow=Convert.ToInt32(EntryCache("UiLauncherTransparent")[""]),originalImage=Convert.ToInt32(EntryCache("UiBackgroundOpacity")[""]);
  opacity.GetType().GetProperty("Value").SetValue(opacity,600,null);Require(ColorOf(cardBorder.Background).A==153&&ColorOf((Brush)app.Resources["ColorBrushBackgroundTransparentSidebar"]).A==153,"Unified panel opacity did not update both surfaces");
  Require(ColorOf(input.Foreground).A==255&&cardBorder.Opacity==1&&Convert.ToInt32(EntryCache("UiLauncherTransparent")[""])==originalWindow&&Convert.ToInt32(EntryCache("UiBackgroundOpacity")[""])==originalImage,"Panel opacity changed text, window opacity or the picture setting");
  ClearKey("UiPanelOpacity");Call(asm.GetType("PCL.ModSecret"),"ThemeRefresh",0);Require(ColorOf(cardBorder.Background).A==153,"Panel opacity did not persist or survive palette refresh");
  var rawBar=new System.Windows.Controls.Primitives.ScrollBar{Orientation=Orientation.Vertical,Maximum=100,ViewportSize=20,Value=40,Height=100};stateRow.Children.Add(rawBar);window.UpdateLayout();window.Dispatcher.Invoke(new Action(()=>{}),System.Windows.Threading.DispatcherPriority.ApplicationIdle);rawBar.ApplyTemplate();Require(rawBar.Template!=null&&rawBar.Template.FindName("PART_Track",rawBar)!=null&&ColorOf(rawBar.Foreground).R<180,"Framework scrollbars kept the white native style");
  var nativeScroll=new ScrollViewer{Width=120,Height=100,VerticalScrollBarVisibility=ScrollBarVisibility.Visible,HorizontalScrollBarVisibility=ScrollBarVisibility.Visible,Content=new Border{Width=360,Height=600}};stateRow.Children.Add(nativeScroll);window.UpdateLayout();window.Dispatcher.Invoke(new Action(()=>{}),System.Windows.Threading.DispatcherPriority.ApplicationIdle);nativeScroll.ApplyTemplate();
  foreach(string name in new[]{"PART_VerticalScrollBar","PART_HorizontalScrollBar"}){var bar=(System.Windows.Controls.Primitives.ScrollBar)nativeScroll.Template.FindName(name,nativeScroll);bar.ApplyTemplate();Require(bar.Style!=null&&bar.Template.FindName("PART_Track",bar)!=null&&ColorOf(bar.Foreground).R<180,"A ScrollViewer template retained an unthemed native scrollbar");}
  nativeScroll.ScrollToBottom();window.UpdateLayout();Require(nativeScroll.VerticalOffset>100,"Themed framework scrollbar lost scrolling behavior");
  Call(settings,"Reset","UiPanelOpacity",null);opacity.GetType().GetProperty("Value").SetValue(opacity,960,null);
  for(int i=0;i<3;i++){mode.SelectedIndex=0;Require(ColorOf(cardBorder.Background).R>240,"An existing card did not return to light mode");mode.SelectedIndex=1;Require(ColorOf(cardBorder.Background).R<70,"Repeated dark switching retained stale brushes");}
  mode.SelectedIndex=0;Call(settings,"Reset","UiLauncherColorMode",null);Require(ColorOf(cardBorder.Background).R<70,"Theme reset did not restore the default dark colors");
  Console.WriteLine("PASS: reordered version/source rows; stable-only default; warning confirm/cancel without premature persistence; saved channels reopen silently; native light/dark cards, text, disabled/selected states, dropdown, tooltip, warning modal, contrast and repeated switching.");
 }
 static void Clear(){Cache().Remove("");Call(asm.GetType("PCL.ModBase"),"IniClearCache","Setup");}
 static void Render(FrameworkElement element,string directory,string name){
  if(string.IsNullOrEmpty(directory))return;Directory.CreateDirectory(directory);element.UpdateLayout();
  int width=(int)Math.Ceiling(element.ActualWidth),height=(int)Math.Ceiling(element.ActualHeight);
  var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(element);
  var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(directory,name)))encoder.Save(file);
 }
 [STAThread] public static int Main(string[] args){Application app=null;Window window=null;
  try{
   AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling",false);AppContext.SetSwitch("Switch.System.IO.BlockLongPaths",false);
   string exe=args[0],root=args[1],preview=args.Length>2?args[2]:"";
   string basis=AppContext.BaseDirectory;Require(Path.GetFileName(basis.TrimEnd('\\')).StartsWith("PclSourceSettings-"),"Settings fixture has unsafe base directory");
   foreach(var file in new[]{"MeloongCore/Shared/bin/MeloongCore.dll","MeloongCore/Wpf/bin/MeloongCore.Wpf.dll","PCLCS/bin/PCLCS.dll","Plain Craft Launcher 2/Resources/Newtonsoft.Json.dll","Plain Craft Launcher 2/Resources/ThrottleDebounce.dll"})Assembly.LoadFrom(Path.Combine(root,file));
   asm=Assembly.LoadFrom(exe);settings=asm.GetType("PCL.Settings");entries=(IDictionary)settings.GetField("Entries",F).GetValue(null);
   foreach(DictionaryEntry pair in entries){var entry=pair.Value;var cache=(IDictionary)entry.GetType().GetField("ValueCache",F).GetValue(entry);cache[""]=entry.GetType().GetField("DefaultValue",F).GetValue(entry);}
   var identify=entries["Identify"];((IDictionary)identify.GetType().GetField("ValueCache",F).GetValue(identify))[""]="source-settings-fixture";
   typeof(Application).GetField("_resourceAssembly",F).SetValue(null,asm);app=(Application)Activator.CreateInstance(asm.GetType("PCL.Application"),true);
   app.GetType().GetMethod("InitializeComponent").Invoke(app,null);
   typeof(Application).GetField("_startupUri",F).SetValue(app,null);
   app.Startup-=(StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler),app,app.GetType().GetMethod("Application_Startup",F));
   app.DispatcherUnhandledException-=(System.Windows.Threading.DispatcherUnhandledExceptionEventHandler)Delegate.CreateDelegate(typeof(System.Windows.Threading.DispatcherUnhandledExceptionEventHandler),app,app.GetType().GetMethod("Application_DispatcherUnhandledException",F));
   app.ShutdownMode=ShutdownMode.OnExplicitShutdown;animation=asm.GetType("PCL.ModAnimation").GetProperty("AniControlEnabled",F);
   Call(asm.GetType("PCL.ModSecret"),"ThemeRefresh",0);
   var entrySource=entries["ToolLauncherUpdateSource"];Require(Convert.ToInt32(entrySource.GetType().GetField("DefaultValue").GetValue(entrySource))==0,"First-use source is not CNB");
   var page=NewPage();var combo=Combo(page);Require(combo.Items.Count==2&&combo.SelectedIndex==0&&Preference()=="Cnb","Default source is not displayed as CNB");
   Require((string)Call(asm.GetType("PCL.SettingService"),"GetKey",combo)=="ToolLauncherUpdateSource","Native source control is not bound to the persistent preference");
   DependencyObject current=combo;while(current!=null&&current.GetType().Name!="MyCard")current=LogicalTreeHelper.GetParent(current);
   var card=(FrameworkElement)current;Require(card!=null,"Launcher card not found");((Panel)LogicalTreeHelper.GetParent(card)).Children.Remove(card);
   card.Margin=new Thickness(15);card.VerticalAlignment=VerticalAlignment.Top;window=new Window{Content=card,Width=760,Height=380,Left=-30000,Top=-30000,ShowInTaskbar=false,ShowActivated=false};window.Show();window.UpdateLayout();
   Render(card,preview,"launcher-source-cnb.png");
   combo.SelectedIndex=1;window.UpdateLayout();
   string ini=Path.Combine(basis,"PCL","Setup.ini");Require(File.Exists(ini)&&File.ReadAllText(ini).Contains("ToolLauncherUpdateSource:1"),"GitHub preference was not persisted in the isolated Setup.ini");
   Clear();Require(Preference()=="GitHub","Saved preference was not reread from disk");
   var reopened=NewPage();Require(Combo(reopened).SelectedIndex==1&&Preference()=="GitHub","Reopening settings overwrote the saved GitHub choice");
   Render(card,preview,"launcher-source-github.png");
   var updater=asm.GetType("PCL.ModLauncherUpdate");var sourceType=asm.GetType("PCL.ModLauncherUpdate+UpdateSource");
   var prompt=(FrameworkElement)Activator.CreateInstance(asm.GetType("PCL.ModLauncherUpdate+UpdatePromptView"),F,null,new object[]{"v7.8.9",Enum.ToObject(sourceType,1)},null);
   var promptPicker=(ComboBox)prompt.GetType().GetField("SourcePicker",F).GetValue(prompt);
   Require(promptPicker.SelectedIndex==1,"Update prompt did not inherit the default source");
   promptPicker.SelectedIndex=0;Require(prompt.GetType().GetProperty("SelectedSource",F).GetValue(prompt,null).ToString()=="Cnb","Temporary update choice was ignored");
   Require(Preference()=="GitHub"&&File.ReadAllText(ini).Contains("ToolLauncherUpdateSource:1"),"Per-attempt update choice overwrote the saved source");
   window.Content=prompt;prompt.Margin=new Thickness(25);window.Height=260;window.UpdateLayout();Require(promptPicker.ActualHeight>=28,"Source picker has no usable click area");Render(prompt,preview,"launcher-update-prompt.png");
   Require(promptPicker.Style!=null&&promptPicker.Template!=null,"Update source picker did not receive the native PCL style");
   window.Content=null;prompt.Margin=new Thickness(0);
   var converter=Activator.CreateInstance(asm.GetType("PCL.ModMain+MyMsgBoxConverter"));
   converter.GetType().GetField("Title",F).SetValue(converter,"发现 PCL Living Fork 新版本");
   converter.GetType().GetField("Text",F).SetValue(converter,"请选择本次更新的下载源。");
   converter.GetType().GetField("Button1",F).SetValue(converter,"下载并更新");converter.GetType().GetField("Button2",F).SetValue(converter,"暂不更新");
   converter.GetType().GetField("ContentFactory",F).SetValue(converter,new Func<FrameworkElement>(()=>prompt));
   var modal=(FrameworkElement)Activator.CreateInstance(asm.GetType("PCL.MyMsgText"),new object[]{converter});
   ((RotateTransform)modal.FindName("TransformRotate")).Angle=0;((TranslateTransform)modal.FindName("TransformPos")).Y=0;
   modal.HorizontalAlignment=HorizontalAlignment.Left;modal.VerticalAlignment=VerticalAlignment.Top;modal.Margin=new Thickness(0);
   modal.Measure(new Size(650,400));modal.Arrange(new Rect(0,0,650,400));Render(modal,preview,"launcher-update-dialog.png");
   var checker=Activator.CreateInstance(updater.GetMethod("CreateUpdateDownloadTask",F).GetParameters()[2].ParameterType);
   var work=Call(updater,"CreateUpdateDownloadTask","https://cnb.cool/ddzmumo/PCL-Living-Fork/-/releases/download/v7.8.9/PCL-Living-Fork-7.8.9.exe",Path.Combine(basis,"fixture-update.exe"),checker);
   Require(work.GetType().GetField("Name",F).GetValue(work).ToString().Contains("v7.8.9"),"Native update task lost the selected version");
   var children=(IList)work.GetType().GetField("Loaders",F).GetValue(work);
   Require(children.Count==1&&children[0].GetType().Name=="LoaderDownload","Launcher update was not wired to native download progress");
   Call(asm.GetType("PCL.SettingService"),"ResetSettings",combo);Clear();Call(asm.GetType("PCL.SettingService"),"RefreshSettings",combo);
   Require(combo.SelectedIndex==0&&Preference()=="Cnb","Reset did not restore the CNB default");
   Clear();Require(Combo(NewPage()).SelectedIndex==0,"Reset was not persisted");
   CheckChannelsAndTheme(app,window,preview,ini);
   CheckDarkChrome(app,window,root,preview);
   Console.WriteLine("PASS: native CNB/GitHub selector, default CNB, isolated disk persistence, reopening/reset, per-attempt popup selection without persistence, native update download task; no real launcher/settings touched.");return 0;
  }catch(Exception error){Console.Error.WriteLine(error.GetType().FullName+": "+error.Message);return 1;}finally{if(window!=null)window.Close();if(app!=null)app.Shutdown();}
 }
}
'@
try{
 [IO.Directory]::CreateDirectory($fixture)|Out-Null
 $path=Join-Path $fixture 'Check.cs';[IO.File]::WriteAllText($path,$source)
 $output=Join-Path $fixture 'SettingsCheck.exe';$compiler=Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
 $arguments=@('/nologo','/target:exe',('/out:'+$output),('/lib:'+$framework))+@($references|ForEach-Object {'/reference:'+$_})
 & $compiler @arguments $path
 if($LASTEXITCODE -ne 0){throw 'Settings fixture compilation failed'}
 Copy-Item -LiteralPath ($ExecutablePath+'.config') -Destination ($output+'.config')
 & $output $ExecutablePath $root $PreviewDirectory
 if($LASTEXITCODE -ne 0){throw 'Launcher update settings validation failed'}
}finally{
 $full=[IO.Path]::GetFullPath($fixture);$prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '^PclSourceSettings-[a-f0-9]{32}$'){throw 'Unsafe settings fixture cleanup'}
 if([IO.Directory]::Exists($full)){[IO.Directory]::Delete($full,$true)}
}
