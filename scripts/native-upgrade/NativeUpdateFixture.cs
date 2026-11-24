using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
// Read-only trusted CI artifact descriptors; never execute/load Main or bridge.
if(args.Length<2)throw new ArgumentException("legacy|bridge file [native-payload-sha]");
try{
 string path=Path.GetFullPath(args[1]);using FileStream file=new(path,FileMode.Open,FileAccess.Read,FileShare.Read);using PEReader pe=new(file,PEStreamOptions.LeaveOpen);var reader=pe.GetMetadataReader();
 if(!reader.IsAssembly || pe.PEHeaders.CorHeader is null)throw new InvalidDataException("A real managed compatibility artifact is required.");
 string assembly=reader.GetString(reader.GetAssemblyDefinition().Name),product=FileVersionInfo.GetVersionInfo(path).ProductName??"";
 int token=pe.PEHeaders.CorHeader.EntryPointTokenOrRelativeVirtualAddress;if((token&unchecked((int)0xff000000))!=0x06000000)throw new InvalidDataException("No actual entry token.");
 var method=reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(token&0xffffff));var type=reader.GetTypeDefinition(method.GetDeclaringType());string entry=(reader.GetString(type.Namespace)+"."+reader.GetString(type.Name)).TrimStart('.')+"."+reader.GetString(method.Name);
 string Constant(string typeName,string fieldName){foreach(var handle in reader.TypeDefinitions){var def=reader.GetTypeDefinition(handle);if(reader.GetString(def.Name)!=typeName)continue;foreach(var fieldHandle in def.GetFields()){var field=reader.GetFieldDefinition(fieldHandle);if(reader.GetString(field.Name)!=fieldName)continue;var constant=reader.GetConstant(field.GetDefaultValue());if(constant.TypeCode!=ConstantTypeCode.String)throw new InvalidDataException("Unexpected descriptor constant.");return Encoding.Unicode.GetString(reader.GetBlobBytes(constant.Value));}}throw new InvalidDataException("Descriptor constant missing: "+fieldName);}
 if(assembly is not ("LivingPCL" or "Plain Craft Launcher 2"))throw new InvalidDataException("Wrong managed artifact identity.");
 string? embedded=null;object details;
 if(args[0]=="legacy"){
  if(entry!="PCL.Application.Main" || product is not ("LivingPCL" or "PCL Living Fork"))throw new InvalidDataException("Not a genuine old full product Main (a bridge or fake parent is forbidden).");
  string reg=Constant("ModSecret","RegFolder"),branch=Constant("ModSecret","VersionBranchMain"),version=Constant("ModBase","VersionBaseName");
  if(reg!="PCLDebug" || branch!="OpenSource")throw new InvalidDataException("Old profile/source branch differs from the isolated fixture.");details=new{versionBaseName=version,displayName=Constant("ModBase","LauncherDisplayName"),registryFolder=reg,sourceBranch=branch};
 }else if(args[0]=="bridge"){
  if(entry!="Program.Main" || product!="LivingPCL" || args.Length!=3)throw new InvalidDataException("Not the genuine canonical managed bridge shape.");
  var resource=reader.ManifestResources.Select(reader.GetManifestResource).Single(value=>value.Implementation.IsNil &&
      reader.GetString(value.Name) is "PCLLivingFork.Payload" or "PCLLivingFork.Payload.gz");
  var directory=pe.PEHeaders.CorHeader.ResourcesDirectory;var section=pe.PEHeaders.SectionHeaders.Single(value=>directory.RelativeVirtualAddress>=value.VirtualAddress&&directory.RelativeVirtualAddress<checked((long)value.VirtualAddress+value.SizeOfRawData));
  long at=checked((long)section.PointerToRawData+directory.RelativeVirtualAddress-section.VirtualAddress+resource.Offset);if(at<0||at>file.Length-4)throw new InvalidDataException("Payload offset out of range.");
  file.Position=at;Span<byte> prefix=stackalloc byte[4];file.ReadExactly(prefix);int length=BinaryPrimitives.ReadInt32LittleEndian(prefix);if(length<1||length>1024*1024*1024||checked(at+4+length)>file.Length)throw new InvalidDataException("Payload span out of range.");
  using var mapped=MemoryMappedFile.CreateFromFile(file,null,0,MemoryMappedFileAccess.Read,HandleInheritability.None,true);
  using var data=mapped.CreateViewStream(at+4,length,MemoryMappedFileAccess.Read);
  using var gzip=reader.GetString(resource.Name)=="PCLLivingFork.Payload.gz" ? new GZipStream(data,CompressionMode.Decompress) : null;
  Stream payload=(Stream?)gzip ?? data;
  using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
  byte[] buffer=new byte[64*1024];long embeddedBytes=0;int size;
  while((size=payload.Read(buffer,0,buffer.Length))>0){
   embeddedBytes+=size;
   if(embeddedBytes>1024L*1024*1024)throw new InvalidDataException("Expanded payload exceeds the size limit.");
   hash.AppendData(buffer,0,size);
  }
  embedded=Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();if(embedded!=args[2]||Constant("PayloadInfo","Sha256")!=embedded)throw new InvalidDataException("Canonical bridge does not contain the exact actual native Main payload/hash.");details=new{embeddedBytes};
 }else throw new ArgumentException("Unsupported descriptor mode.");
 file.Position=0;string sha=Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();Console.WriteLine(JsonSerializer.Serialize(new{physicalPath=path,sha256=sha,assembly,product,entry,embeddedPayloadSha256=embedded,details}));return 0;
}catch(Exception error){Console.Error.WriteLine(error.GetType().Name+": "+error.Message);return 1;}
