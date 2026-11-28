using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCL;

// The original one-argument mode only reads trusted CI artifact metadata.
// The optional startup-fixture mode loads the actual bundled Core/Shared
// crypto leaf; it never loads Main/App/WPF, Settings or credential code.
// The production bounded bundle reader is linked once.
if(args.Length==1 && args[0]=="--self-test-notice-metadata") { StartupNoticeMetadata.VerifyPolicy(); return 0; }
bool startupSentinels=args.Length==4 && args[0]=="--startup-sentinels";
if(args.Length!=1 && !startupSentinels)throw new ArgumentException("Provide the actual published native Main EXE, or --startup-sentinels EXE fixtureId seed.");
try {
 string path=Path.GetFullPath(startupSentinels?args[1]:args[0]);var identity=ModernBundleIdentity.Read(path);
 if(identity.AssemblyName!="LivingPCL" || identity.Product!="PCL Living Fork")throw new InvalidDataException("Not the normal LF native product.");
 byte[] bytes=ModernBundleIdentity.ReadEntryAssembly(path,identity);
 using MemoryStream image=new(bytes,false);using PEReader pe=new(image);MetadataReader metadata=pe.GetMetadataReader();
 int token=pe.PEHeaders.CorHeader!.EntryPointTokenOrRelativeVirtualAddress;
 MethodDefinition main=metadata.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(token&0xffffff));
 TypeDefinition entryType=metadata.GetTypeDefinition(main.GetDeclaringType());
 string name=metadata.GetString(entryType.Namespace)+"."+metadata.GetString(entryType.Name),method=metadata.GetString(main.Name);
 // The current launcher dispatches its skin-service worker before entering WPF.
 // Historical native builds entered the same application through WPF's generated Main.
 if(name is not ("PCL.LauncherEntryPoint" or "PCL.Application") || method!="Main")throw new InvalidDataException("Actual entry is "+name+"."+method+"; a fixture/test host is not the product Main.");
 object Constant(string typeName,string fieldName) {
  foreach(TypeDefinitionHandle handle in metadata.TypeDefinitions) {
   TypeDefinition type=metadata.GetTypeDefinition(handle);if(metadata.GetString(type.Namespace)!="PCL" || metadata.GetString(type.Name)!=typeName)continue;
   foreach(FieldDefinitionHandle fieldHandle in type.GetFields()) {
    FieldDefinition field=metadata.GetFieldDefinition(fieldHandle);if(metadata.GetString(field.Name)!=fieldName)continue;
    ConstantHandle value=field.GetDefaultValue();if(value.IsNil)throw new InvalidDataException("Main field is not a constant: "+fieldName);
    Constant constant=metadata.GetConstant(value);byte[] raw=metadata.GetBlobBytes(constant.Value);
    return constant.TypeCode switch {ConstantTypeCode.String=>Encoding.Unicode.GetString(raw),ConstantTypeCode.Int32=>BinaryPrimitives.ReadInt32LittleEndian(raw),ConstantTypeCode.Boolean when raw.Length==1=>raw[0]!=0,_=>throw new InvalidDataException("Unexpected Main constant type.")};
   }
  }
  throw new InvalidDataException("Actual Main constant is missing: "+typeName+"."+fieldName);
 }
 string version=(string)Constant("ModBase","VersionBaseName"),display=(string)Constant("ModBase","LauncherDisplayName"),registry=(string)Constant("ModSecret","RegFolder"),branch=(string)Constant("ModSecret","VersionBranchMain");
 if(version!=identity.ProductVersion || display!="PCL Living Fork" || registry!="PCLDebug" || branch!="OpenSource")throw new InvalidDataException("Actual Main version/profile/source branch differs from this LF smoke contract.");
 bool HasType(string typeName)=>metadata.TypeDefinitions.Any(handle=>{TypeDefinition type=metadata.GetTypeDefinition(handle);return metadata.GetString(type.Namespace)=="PCL" && metadata.GetString(type.Name)==typeName;});
 bool hasNotes=HasType("ReleaseNotesView"),hasPrompt=HasType("ReleaseNotesPromptView");
 int buildType=(int)Constant("ModBase","BuildType");
 // Old native payloads without a Markdown renderer retain their original route.
 bool prerelease=hasNotes && (bool)Constant("ModBase","ReleaseBaselineIsPrerelease");
 var startupNotice=StartupNoticeMetadata.Describe(display,version,buildType,prerelease,hasPrompt,hasNotes);
 var resources=new Dictionary<string,HashSet<string>>(StringComparer.Ordinal);
 var startupModules=new Dictionary<string,byte[]>(StringComparer.Ordinal);
 foreach(ManifestResourceHandle handle in metadata.ManifestResources) {
  ManifestResource resource=metadata.GetManifestResource(handle);if(!resource.Implementation.IsNil)continue;
  string resourceName=metadata.GetString(resource.Name);if(resourceName!="PCL.Resources.resources" && !resourceName.EndsWith(".g.resources",StringComparison.Ordinal))continue;
  var directory=pe.PEHeaders.CorHeader.ResourcesDirectory;var block=pe.GetSectionData(directory.RelativeVirtualAddress);int offset=checked((int)resource.Offset);
  if(offset<0 || offset>directory.Size-4)throw new InvalidDataException("Invalid managed resource offset.");
  int length=BinaryPrimitives.ReadInt32LittleEndian(block.GetContent(offset,4).AsSpan());
  if(length<0 || checked((long)offset+4+length)>directory.Size)throw new InvalidDataException("Invalid managed resource span.");
  using MemoryStream raw=new(block.GetContent(offset+4,length).ToArray(),false);using ResourceReader reader=new(raw);
  var keys=new HashSet<string>(StringComparer.Ordinal);IDictionaryEnumerator enumerator=reader.GetEnumerator();while(enumerator.MoveNext()) {
   string key=(string)enumerator.Key;keys.Add(key);

  }
  resources.Add(resourceName,keys);
 }
 if(!resources.TryGetValue("PCL.Resources.resources",out var payloads))throw new InvalidDataException("Actual LF ResourceManager resource is missing.");
 if(!payloads.SetEquals(new[]{"Custom","ForgeInstaller","Help","libwebp64","Transformer"}))throw new InvalidDataException("Unexpected actual LF payload set.");
 foreach(string key in new[]{"PCLCS","PCLLF.Presentation","MeloongCore","Newtonsoft.Json"}) {
  byte[] module=ModernBundleIdentity.ReadBundledAssembly(path,key+".dll");
  if(module.Length<1 || module.Length>16*1024*1024)throw new InvalidDataException("Invalid actual bundled dependency.");
  if(startupSentinels && key!="PCLLF.Presentation")startupModules.Add(key,module);
 }

 var wpf=resources.Where(pair=>pair.Key.EndsWith(".g.resources",StringComparison.Ordinal)).Select(pair=>pair.Value).Single();
 var normalized=new HashSet<string>(wpf.Select(key=>key.ToLowerInvariant()),StringComparer.Ordinal);
 string[] required={"application.baml","formmain.baml","glass/glasslauncherview.baml","glass/glasspresentationresources.baml","themes/generic.baml","resources/font.ttf","images/branding/livingpcl.ico"};
 foreach(string key in required)if(!normalized.Contains(key))throw new InvalidDataException("Actual product BAML/font/brand resource missing: "+key);
 using FileStream file=File.OpenRead(path);string hash=Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
 if(startupSentinels) {
  if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Actual startup sentinel generation requires Windows; synthetic-only probes call the explicit private test seam.");
  Console.WriteLine(JsonSerializer.Serialize(StartupSentinels.Create(startupModules,args[2],args[3],hash)));
  return 0;
 }
 Console.WriteLine(JsonSerializer.Serialize(new{nativeIdentity=identity,entryType=name,entryMethod=method,physicalPath=path,sha256=hash,versionBaseName=version,displayName=display,registryFolder=registry,sourceBranch=branch,versionCode=Constant("ModBase","VersionCode"),buildType,startupNoticeTitle=startupNotice.Title,startupNoticeStyle=startupNotice.Style,startupNoticeSource=startupNotice.Source,verifiedWpfKeys=required}));
 return 0;
}catch(Exception error){Console.Error.WriteLine(error.GetType().Name+": "+error.Message);return 1;}


// Test support, not a second settings engine. Encryption/identity/roundtrip use
// the exact Core + Shared bytes embedded in the already-validated native Main.
internal static class StartupSentinels {
 internal static object Create(Dictionary<string,byte[]> modules,string fixtureId,string seed,string nativeSha,Func<Assembly,object?>? readHardware=null) {
  if(!Guid.TryParseExact(fixtureId,"N",out Guid owner) || owner.ToString("N")!=fixtureId || seed!="native-smoke-seed-"+fixtureId || nativeSha.Length!=64)throw new ArgumentException("Expected synthetic GUID-owned startup fixture inputs.");
  foreach(string name in new[]{"PCLCS","MeloongCore"})if(!modules.ContainsKey(name))throw new InvalidDataException("Actual crypto module missing: "+name);
  using var context=new StartupCoreContext(modules);
  Assembly shared=context.LoadExact("MeloongCore"),core=context.LoadExact("PCLCS");
  Type crypto=core.GetType("PCLCS.LegacySettingsCrypto",true)!;
  MethodInfo source=crypto.GetMethod("SourceIdentify",BindingFlags.Public|BindingFlags.Static,null,[typeof(string),typeof(string)],null)??throw new MissingMethodException("Actual SourceIdentify leaf changed.");
  MethodInfo encrypt=crypto.GetMethod("Encrypt",BindingFlags.Public|BindingFlags.Static,null,[typeof(string),typeof(string)],null)??throw new MissingMethodException("Actual Encrypt leaf changed.");
  MethodInfo decrypt=crypto.GetMethod("Decrypt",BindingFlags.Public|BindingFlags.Static,null,[typeof(string),typeof(string)],null)??throw new MissingMethodException("Actual Decrypt leaf changed.");
  string? hardware="Unknown";
  try {
   object? value=readHardware is null ? shared.GetType("MeloongCore.RegistryUtils",true)!.GetMethod("TryRead",BindingFlags.Public|BindingFlags.Static,null,[typeof(string),typeof(string),typeof(object)],null)!.Invoke(null,[@"HKEY_LOCAL_MACHINE\SYSTEM\HardwareConfig","LastConfig","Unknown"]) : readHardware(shared);
   hardware=Convert.ToString(value,CultureInfo.InvariantCulture);
  }catch { } // Same read-only hardware fallback as the real DeviceIdentify host.
  string identify=(string)source.Invoke(null,[hardware,seed])!;
  if(string.IsNullOrWhiteSpace(identify))throw new InvalidDataException("Actual source identity rejected the owned synthetic seed.");
  string Cipher(string plain) {
   string raw=(string)encrypt.Invoke(null,[plain,"PCL"+identify])!;
   if((string?)decrypt.Invoke(null,[raw,"PCL"+identify])!=plain)throw new InvalidDataException("Actual crypto leaf did not roundtrip its synthetic fixture input.");
   return raw;
  }
  var plain=new Dictionary<string,string>(StringComparer.Ordinal) {
   ["LoginMsJson"]="{}",["CacheMsOAuthRefresh"]="legacy-shadow-refresh-"+fixtureId,["CacheMsAccess"]="legacy-shadow-access-"+fixtureId,
   ["CacheMsProfileJson"]="{\"id\":\"00000000000000000000000000000001\",\"name\":\"SyntheticLegacy\"}",["CacheMsUuid"]="00000000000000000000000000000001",["CacheMsName"]="SyntheticLegacy",
   ["CacheMsV2Migrated"]="True",["CacheMsV2OAuthRefresh"]="v2-authoritative-refresh-"+fixtureId,["CacheMsV2Access"]="v2-authoritative-access-"+fixtureId,
   ["CacheMsV2ProfileJson"]="{\"id\":\"00000000000000000000000000000002\",\"name\":\"SyntheticCurrent\",\"future\":{\"keep\":true}}",["CacheMsV2Uuid"]="00000000000000000000000000000002",["CacheMsV2Name"]="SyntheticCurrent",["CacheMsV2Expires"]="9999999999"
  };
  var records=plain.Select(pair=>new {logicalName=pair.Key,physicalName=pair.Key+"_LF",kind="String",raw=pair.Key=="CacheMsV2Migrated"?pair.Value:Cipher(pair.Value)}).ToArray();
  return new {kind="PCLLF.NativeStartupSyntheticAccounts",schemaVersion=1,fixtureId,seed,nativeSha256=nativeSha,coreSha256=Convert.ToHexString(SHA256.HashData(modules["PCLCS"])).ToLowerInvariant(),sharedSha256=Convert.ToHexString(SHA256.HashData(modules["MeloongCore"])).ToLowerInvariant(),records,scope="Synthetic-only data generated by actual bundled Core crypto; no Main/App/WPF or credential factory invoked. Hardware value and derived device identity are never returned."};
 }
 private sealed class StartupCoreContext : AssemblyLoadContext,IDisposable {
  private readonly Dictionary<string,byte[]> modules;
  internal StartupCoreContext(Dictionary<string,byte[]> modules):base("PclLfNativeStartupCrypto-"+Guid.NewGuid().ToString("N"),true){this.modules=modules;}
  internal Assembly LoadExact(string name) {Assembly result=LoadFromAssemblyName(new AssemblyName(name));if(result.GetName().Name!=name)throw new InvalidDataException("Unexpected actual crypto assembly identity.");return result;}
  protected override Assembly? Load(AssemblyName name) {if(name.Name is not null && modules.TryGetValue(name.Name,out byte[]? bytes)){using MemoryStream input=new(bytes,false);return LoadFromStream(input);}return null;}
  public void Dispose()=>Unload();
 }
}
