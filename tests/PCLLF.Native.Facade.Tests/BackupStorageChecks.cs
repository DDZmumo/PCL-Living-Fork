using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
using PCL;
using PCLCS;
using static PCLLF.Native.Facade.Tests.BackupResourceChecks;

namespace PCLLF.Native.Facade.Tests;

internal static class BackupStorageChecks {
    internal static void Run(Scope scope) {
        var profileProperty=Native.Property(Native.Type("PCL.LocalGameData"),"TestRoot");
        var worldProperty=Native.Property(Native.Type("PCL.WorldBackups"),"TestRoot");
        var historyProperty=Native.Property(Native.Type("PCL.EnvironmentHistory"),"TestRoot");
        object? oldProfile=profileProperty.GetValue(null),oldWorld=worldProperty.GetValue(null),oldHistory=historyProperty.GetValue(null);
        string root=Path.Combine(scope.Root,"storage-location"),profile=Path.Combine(root,"profile"),target=Path.Combine(root,"selected-folder");
        Directory.CreateDirectory(profile);Directory.CreateDirectory(target);
        try {
            profileProperty.SetValue(null,profile);worldProperty.SetValue(null,null);historyProperty.SetValue(null,null);
            PageSetupSystem page=new(); Native.Call(typeof(PageSetupSystem),"RefreshBackupStorage",page);
            var label=Part<MyTextBox>(page,"TextBackupStoragePath");var button=Part<MyButton>(page,"BtnBackupStorageChoose");
            Native.Check(label.Text==profile && label.IsReadOnly && button.Text=="选择" && button.IsEnabled,"Settings did not reuse the native path textbox and picker button.");
            int confirmations=0;
            Native.Set(page,"SelectBackupFolder",new Func<string>(()=>""));
            Native.Set(page,"ConfirmBackupMove",new Func<string,string,bool>((_,_)=>{confirmations++;return false;}));
            void Click()=>Native.Call(typeof(MyButton),"OnClick",button,button,new MouseButtonEventArgs(InputManager.Current.PrimaryMouseDevice,0,MouseButton.Left));
            Click();Native.Check(confirmations==0 && !File.Exists(Path.Combine(profile,"backup-storage.json")),"Canceled folder picker changed the location.");
            Native.Set(page,"SelectBackupFolder",new Func<string>(()=>target));Click();
            Native.Check(confirmations==1 && !File.Exists(Path.Combine(profile,"backup-storage.json")),"Rejected migration confirmation changed the location.");
            string mc=Path.Combine(root,"minecraft"),version=Path.Combine(mc,"versions/example");
            var owner=Instance(version);string game=owner.PathIndie;
            Put(Path.Combine(game,"saves/World/level.dat"),"world-before-move");Put(Path.Combine(game,"mods/example.jar"),"mod-before-move");
            string worldId=Capture(game,"World");
            string historyId=(string)Native.Static("PCL.EnvironmentHistory","Capture",version,game,"before move",None,null,"")!;
            Put(Path.Combine(profile,"personal-options.json"),"personal-kept");
            var result=(BackupStorageMoveResult)Native.Static("PCL.BackupStorage","MoveTo",target,None,null,null)!;
            Native.Check(result.CleanupComplete && result.Files>0 && Points(game).Any(point=>(string?)point["id"]==worldId),"Actual world backups did not follow the committed selected root.");
            var history=(List<JObject>)Native.Static("PCL.EnvironmentHistory","Points",version)!;
            Native.Check(history.Any(point=>(string?)point["id"]==historyId) && new BackupStorageLocation(profile).Root==target,"History points or persisted storage root were lost.");
            string worldStore=(string)World("Store",game)!,historyStore=(string)Native.Static("PCL.EnvironmentHistory","Store",version)!;
            Native.Check(worldStore.StartsWith(Path.Combine(target,"world-backups"),StringComparison.OrdinalIgnoreCase) && historyStore.StartsWith(Path.Combine(target,"environment-history"),StringComparison.OrdinalIgnoreCase),"Engines still resolve their former default storage.");
            Native.Check(!Directory.Exists(Path.Combine(profile,"world-backups")) && !Directory.Exists(Path.Combine(profile,"environment-history")) && File.ReadAllText(Path.Combine(profile,"personal-options.json"))=="personal-kept","Migration retained old bulk data or moved unrelated personal settings.");
            Put(Path.Combine(game,"saves/World/level.dat"),"new-world");Restore(game,worldId);
            Native.Check(File.ReadAllText(Path.Combine(game,"saves/World/level.dat"))=="world-before-move","World restoration cannot consume the relocated archive.");
            Put(Path.Combine(game,"mods/example.jar"),"new-mod");
            Native.Static("PCL.EnvironmentHistory","Restore",version,game,mc,historyId,None,null,null);
            Native.Check(File.ReadAllText(Path.Combine(game,"mods/example.jar"))=="mod-before-move","Environment restoration cannot consume relocated content objects.");
            string next=Path.Combine(root,"second-choice");Directory.CreateDirectory(next);
            bool completed=false;BackupStorageMoveResult? asyncResult=null;Exception? asyncError=null;bool wasCanceled=false;
            var callback=new Action<BackupStorageMoveResult,Exception,bool>((r,e,c)=>{asyncResult=r;asyncError=e;wasCanceled=c;completed=true;});
            Native.Check((bool)Native.Static("PCL.BackupStorage","StartMove",next,callback)!,"Native storage file task did not start.");
            VisualAssetChecks.Pump(()=>completed,"Storage migration did not complete through its actual loader/worker lifecycle.",30000);
            Native.Check(asyncError is null && !wasCanceled && asyncResult?.Root==next && !(bool)Native.Property(Native.Type("PCL.BackupStorage"),"IsChanging").GetValue(null)!,
                "Native storage file task failed or retained its global busy flag: "+asyncError);
            target=next;
            Native.Call(typeof(PageSetupSystem),"RefreshBackupStorage",page);Native.Check(label.Text==target,"Settings did not refresh to the selected root.");
            string? preview=Environment.GetEnvironmentVariable("PCLLF_PERSONAL_OPTIONS_PREVIEW_DIR");
            if(!string.IsNullOrWhiteSpace(preview)) {
                Directory.CreateDirectory(preview);var stack=Part<StackPanel>(page,"PanMain");foreach(MyCard card in stack.Children.OfType<MyCard>())_=card.MainTextBlock;
                page.Measure(new Size(850,650));page.Arrange(new Rect(0,0,850,650));page.UpdateLayout();
                RenderTargetBitmap bitmap=new(850,650,96,96,PixelFormats.Pbgra32);bitmap.Render(page);PngBitmapEncoder encoder=new();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using FileStream file=File.Create(Path.Combine(preview,"backup-storage-settings.png"));encoder.Save(file);
            }
        } finally {profileProperty.SetValue(null,oldProfile);worldProperty.SetValue(null,oldWorld);historyProperty.SetValue(null,oldHistory);}
        Console.WriteLine("PASS backup storage: real native settings button/picker cancellation, both actual engines relocate, persisted root, preserved personal settings and successful world/environment restoration from the chosen folder.");
    }
}
