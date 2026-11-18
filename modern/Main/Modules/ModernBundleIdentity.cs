#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCL;

// Read-only format adaptation from dotnet/runtime v10.0.14,
// 6aa72daa24f66c01c3d7b6a32519e9d21396deec:
// src/installer/managed/Microsoft.NET.HostModel/Bundle/{Bundler,Manifest,FileEntry}.cs
// and src/native/corehost/bundle/{header.cpp,header.h,file_entry.cpp,reader.h}.
// .NET Foundation portions (format/constants) are MIT;
// see modern/ThirdParty/dotnet-runtime-bundle/LICENSE.TXT and source-manifest.json.
// Only our unsigned Windows-x64, NET10, uncompressed v6 publish format is supported.
// No extraction, loading or execution of candidate DLLs is performed.
internal static class ModernBundleIdentity
{
    private const long MaxBundleBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxHostBytes = 16 * 1024 * 1024;
    private const int MaxManifestBytes = 4 * 1024 * 1024;
    private const int MaxEntryBytes = 64 * 1024 * 1024;
    private const int MaxConfigBytes = 128 * 1024;
    private const int MaxEntries = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    // HostModel Bundler.BundleHeaderPlaceholder[8..], not an invented payload marker.
    private static readonly byte[] Signature = {
        0x8b,0x12,0x02,0xb9,0x6a,0x61,0x20,0x38,0x72,0x7b,0x93,0x02,0x14,0xd7,0xa0,0x32,
        0x13,0xf5,0xb9,0xe6,0xef,0xae,0x33,0x18,0xee,0x3b,0x2d,0xce,0x24,0xb3,0x6a,0xae
    };
    internal sealed record Identity(string AssemblyName, string Product, Version Version, string? FileVersion, string? ProductVersion, string EntryPath, long HeaderOffset, long EntryOffset, long EntrySize);
    private sealed record Entry(long Offset, long Size, byte Type, string Path);
    internal static bool IsLivingExecutable(string path, string displayName)
    {
        try
        {
            Identity identity = Read(path);
            // Product is read from the actual bundled entry assembly, not a
            // native shell caption (which is not portable metadata).
            return AllowedAssembly(identity.AssemblyName) && AllowedProduct(identity.Product, displayName);
        }
        catch (Exception error) when (error is InvalidDataException or BadImageFormatException or EndOfStreamException or OverflowException or DecoderFallbackException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }
    private static bool AllowedAssembly(string value) => value == "LivingPCL" || value == "Plain Craft Launcher 2";
    private static bool AllowedProduct(string? value, string displayName) => value == "LivingPCL" || value == displayName;
    internal static Identity Read(string path)
    {
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < 1024 || file.Length > MaxBundleBytes) throw Bad("Bundle file length is outside the supported bound.");
        long hostEnd;
        using (PEReader pe = new(file, PEStreamOptions.LeaveOpen))
        {
            PEHeaders headers = pe.PEHeaders;
            if (pe.HasMetadata || headers.PEHeader is null || headers.CoffHeader.Machine != Machine.Amd64 ||
                headers.PEHeader.Magic != PEMagic.PE32Plus || headers.CoffHeader.NumberOfSections is < 1 or > 96)
                throw Bad("A real Windows-x64 native apphost is required.");
            if (headers.PEHeader.CertificateTableDirectory.Size != 0) throw Bad("Signed/trailing-certificate bundles are not this publish format.");
            hostEnd = headers.PEHeader.SizeOfHeaders;
            if (hostEnd < 1 || hostEnd > MaxHostBytes) throw Bad("Invalid host header span.");
            foreach (SectionHeader section in headers.SectionHeaders)
            {
                if (section.PointerToRawData < 0 || section.SizeOfRawData < 0) throw Bad("Negative PE section span.");
                hostEnd = Math.Max(hostEnd, checked((long)section.PointerToRawData + section.SizeOfRawData));
            }
        }
        if (hostEnd > MaxHostBytes || hostEnd >= file.Length) throw Bad("Invalid apphost image span.");
        byte[] host = ReadBytes(file, 0, checked((int)hostEnd));
        int marker = -1;
        for (int i = 8; i <= host.Length - Signature.Length; i++)
        {
            if (!host.AsSpan(i, Signature.Length).SequenceEqual(Signature)) continue;
            if (marker >= 0) throw Bad("Duplicate bundle marker in the apphost image.");
            marker = i;
        }
        if (marker < 8) throw Bad("The actual apphost bundle marker is absent.");
        long headerOffset = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(host.AsSpan(marker - 8, 8));
        if (headerOffset < hostEnd || headerOffset > file.Length - 64 || file.Length - headerOffset > MaxManifestBytes) throw Bad("Invalid bundle header offset/size.");
        file.Position = headerOffset;
        using BinaryReader reader = new(file, StrictUtf8, leaveOpen: true);
        if (reader.ReadUInt32() != 6 || reader.ReadUInt32() != 0) throw Bad("Only bundle version 6.0 is supported.");
        int count = reader.ReadInt32();
        if (count is < 1 or > MaxEntries) throw Bad("Invalid bundle entry count.");
        string bundleId = ReadString(reader, 12);
        if (!Regex.IsMatch(bundleId, "\\A[A-Za-z0-9_-]{12}\\z")) throw Bad("Invalid deterministic bundle ID.");
        long depsOffset = reader.ReadInt64(), depsSize = reader.ReadInt64();
        long configOffset = reader.ReadInt64(), configSize = reader.ReadInt64();
        if (reader.ReadUInt64() != 0) throw Bad("Extraction compatibility flags are not this publish format.");
        var entries = new List<Entry>(count);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            long offset = reader.ReadInt64(), size = reader.ReadInt64(), compressed = reader.ReadInt64();
            byte type = reader.ReadByte();
            string relative = ReadString(reader, 1024);
            if (compressed != 0) throw Bad("Compressed bundles are explicitly unsupported.");
            if (type > 5 || !ValidPath(relative) || !paths.Add(relative)) throw Bad("Invalid, duplicate or traversing bundle path/type.");
            if (size <= 0 || offset < hostEnd || checked(offset + size) > headerOffset) throw Bad("Invalid payload range.");
            entries.Add(new Entry(offset, size, type, relative));
        }
        if (file.Position != file.Length) throw Bad("Truncated or unexpected trailing manifest bytes.");
        long previousEnd = hostEnd;
        foreach (Entry entry in entries.OrderBy(value => value.Offset))
        {
            if (entry.Offset < previousEnd) throw Bad("Overlapping payload spans.");
            previousEnd = checked(entry.Offset + entry.Size);
        }
        Entry[] candidates = entries.Where(value => value.Type == 1 && (value.Path == "LivingPCL.dll" || value.Path == "Plain Craft Launcher 2.dll")).ToArray();
        if (candidates.Length != 1) throw Bad("Exactly one original LF entry assembly is required.");
        Entry app = candidates[0];
        string baseName = app.Path[..^4];
        Entry deps = Unique(entries, baseName + ".deps.json", 3);
        Entry config = Unique(entries, baseName + ".runtimeconfig.json", 4);
        if (deps.Offset != depsOffset || deps.Size != depsSize || config.Offset != configOffset || config.Size != configSize) throw Bad("Header configuration locations do not match their entries.");
        if (config.Size > MaxConfigBytes) throw Bad("Runtime configuration is too large.");
        using (JsonDocument runtime = JsonDocument.Parse(ReadBytes(file, config.Offset, checked((int)config.Size)), new JsonDocumentOptions { MaxDepth = 16 }))
        {
            JsonElement options = runtime.RootElement.GetProperty("runtimeOptions");
            if (!options.TryGetProperty("tfm", out JsonElement tfm) || tfm.GetString() != "net10.0" || options.TryGetProperty("framework", out _))
                throw Bad("The payload must declare the supported NET10 runtime.");
            bool selfContained = options.TryGetProperty("includedFrameworks", out JsonElement included);
            bool dependent = options.TryGetProperty("frameworks", out JsonElement required);
            if (selfContained == dependent) throw Bad("The payload must declare exactly one runtime deployment mode.");
            JsonElement frameworks = selfContained ? included : required;
            if (frameworks.ValueKind != JsonValueKind.Array ||
                !frameworks.EnumerateArray().Any(value => value.TryGetProperty("name", out JsonElement name) && name.GetString() == "Microsoft.NETCore.App" &&
                    value.TryGetProperty("version", out JsonElement version) && Version.TryParse(version.GetString(), out Version? parsed) && parsed.Major == 10))
                throw Bad("The payload requires NET10.");
            if (dependent)
            {
                // Slim LF is a WindowsDesktop product, not an arbitrary console
                // apphost or an undeclared runtime. Keep the PE/entry checks below.
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonElement framework in frameworks.EnumerateArray())
                {
                    if (!framework.TryGetProperty("name", out JsonElement name) || !names.Add(name.GetString() ?? "") ||
                        !framework.TryGetProperty("version", out JsonElement version) || !Version.TryParse(version.GetString(), out Version? parsed) ||
                        parsed.Major != 10 || parsed.Minor != 0) throw Bad("Unsupported slim runtime dependency.");
                }
                if (!names.SetEquals(new[] { "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App" }))
                    throw Bad("Slim LF requires the NET10 WindowsDesktop runtime.");
            }
        }
        // The actual apphost binding string lies in its original PE image, never
        // inferred from the renamed on-disk executable or a VersionInfo caption.
        byte[] binding = StrictUtf8.GetBytes(app.Path + "\0");
        if (host.AsSpan().IndexOf(binding) < 0) throw Bad("The apphost does not bind this entry DLL.");
        if (app.Size > MaxEntryBytes) throw Bad("Entry assembly exceeds the supported bound.");
        using MemoryStream image = new(ReadPayload(file, app.Offset, app.Size), writable: false);
        using PEReader entryPe = new(image);
        if (!entryPe.HasMetadata || entryPe.PEHeaders.CorHeader is null || (entryPe.PEHeaders.CorHeader.Flags & CorFlags.NativeEntryPoint) != 0)
            throw Bad("The entry must be a real managed assembly.");
        MetadataReader metadata = entryPe.GetMetadataReader();
        if (!metadata.IsAssembly) throw Bad("The entry is not an assembly.");
        AssemblyDefinition assembly = metadata.GetAssemblyDefinition();
        string identity = metadata.GetString(assembly.Name);
        if (!AllowedAssembly(identity) || app.Path != identity + ".dll" || metadata.GetString(metadata.GetModuleDefinition().Name) != app.Path) throw Bad("The entry identity/path does not belong to LF.");
        int token = entryPe.PEHeaders.CorHeader.EntryPointTokenOrRelativeVirtualAddress;
        if ((token & unchecked((int)0xff000000)) != 0x06000000 || (token & 0xffffff) < 1 || (token & 0xffffff) > metadata.MethodDefinitions.Count)
            throw Bad("The actual entry DLL has no managed application entry point.");
        MethodDefinition main = metadata.GetMethodDefinition(System.Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle(token & 0xffffff));
        if ((main.Attributes & MethodAttributes.Static) == 0 || main.RelativeVirtualAddress == 0) throw Bad("Invalid managed entry point.");
        string? product = null, fileVersion = null, productVersion = null;
        bool framework10 = false;
        bool frameworkSeen = false;
        foreach (CustomAttributeHandle handle in assembly.GetCustomAttributes())
        {
            CustomAttribute attribute = metadata.GetCustomAttribute(handle);
            string attributeName = AttributeName(metadata, attribute);
            if (attributeName == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute") throw Bad("Reference-only entry is not runnable.");
            if (attributeName is not ("System.Reflection.AssemblyProductAttribute" or "System.Reflection.AssemblyFileVersionAttribute" or "System.Reflection.AssemblyInformationalVersionAttribute" or "System.Runtime.Versioning.TargetFrameworkAttribute")) continue;
            BlobReader blob = metadata.GetBlobReader(attribute.Value);
            if (blob.RemainingBytes is < 4 or > 512 || blob.ReadUInt16() != 1) throw Bad("Invalid entry identity attribute.");
            string? value = blob.ReadSerializedString();
            if (blob.RemainingBytes < 2) throw Bad("Truncated entry identity attribute data.");
            ushort named = blob.ReadUInt16();
            if (attributeName != "System.Runtime.Versioning.TargetFrameworkAttribute")
            {
                if (value is null || named != 0 || blob.RemainingBytes != 0) throw Bad("Invalid product/version metadata.");
                if (attributeName == "System.Reflection.AssemblyProductAttribute")
                {
                    if (product is not null) throw Bad("Duplicate product metadata.");
                    product = value;
                }
                else if (attributeName == "System.Reflection.AssemblyFileVersionAttribute")
                {
                    if (fileVersion is not null) throw Bad("Duplicate file version metadata.");
                    fileVersion = value;
                }
                else
                {
                    if (productVersion is not null) throw Bad("Duplicate product version metadata.");
                    productVersion = value;
                }
            }
            else
            {
                if (frameworkSeen) throw Bad("Duplicate target framework metadata.");
                frameworkSeen = true;
                // The actual SDK-generated TargetFramework attribute includes
                // only its optional FrameworkDisplayName string property.
                if (named > 1) throw Bad("Unexpected framework attribute properties.");
                if (named == 1 && (blob.RemainingBytes < 4 || blob.ReadByte() != 0x54 || blob.ReadByte() != 0x0e || blob.ReadSerializedString() != "FrameworkDisplayName"))
                    throw Bad("Unexpected framework attribute property.");
                if (named == 1) _ = blob.ReadSerializedString();
                if (blob.RemainingBytes != 0) throw Bad("Trailing framework attribute data.");
                framework10 = value == ".NETCoreApp,Version=v10.0";
            }
        }
        if (product is null || !framework10) throw Bad("Missing actual NET10 entry product metadata.");
        return new Identity(identity, product, assembly.Version, fileVersion, productVersion, app.Path, headerOffset, app.Offset, app.Size);
    }
    internal static byte[] ReadEntryAssembly(string path, Identity identity)
    {
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadPayload(file, identity.EntryOffset, identity.EntrySize);
    }
    internal static byte[] ReadBundledAssembly(string path, string modulePath)
    {
        Identity identity = Read(path);
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using BinaryReader reader = new(file, StrictUtf8, leaveOpen: true);
        file.Position = identity.HeaderOffset;
        _ = reader.ReadUInt64();
        int count = reader.ReadInt32();
        _ = ReadString(reader, 12);
        file.Position += 40;
        for (int index = 0; index < count; index++)
        {
            long offset = reader.ReadInt64(), size = reader.ReadInt64();
            _ = reader.ReadInt64();
            byte type = reader.ReadByte(); string name = ReadString(reader, 1024);
            if (type == 1 && name == modulePath) return ReadPayload(file, offset, size);
        }
        throw Bad("The requested managed bundle module is absent.");
    }
    private static byte[] ReadPayload(FileStream file, long offset, long size)
    {
        if (size <= 0 || size > MaxEntryBytes) throw Bad("Invalid bounded assembly payload size.");
        return ReadBytes(file, offset, checked((int)size));
    }
    private static Entry Unique(List<Entry> entries, string path, byte type)
    {
        Entry[] found = entries.Where(value => value.Path == path && value.Type == type).ToArray();
        if (found.Length != 1) throw Bad("Missing or duplicate application configuration.");
        return found[0];
    }
    private static bool ValidPath(string path) => path.Length > 0 && !path.StartsWith('/') && !path.Contains('\\') && !path.Any(value => char.IsControl(value) || "<>:\"|?*".Contains(value)) &&
        path.Split('/').All(value => value.Length > 0 && value is not ("." or "..") && !value.EndsWith(' ') && !value.EndsWith('.'));
    private static string ReadString(BinaryReader reader, int maximum)
    {
        uint length = 0;
        int count = 0;
        byte value;
        do
        {
            if (count == 5) throw Bad("Invalid string length prefix.");
            value = reader.ReadByte();
            if (count == 4 && (value & 0xf8) != 0) throw Bad("Overflowed string length.");
            length |= checked((uint)(value & 0x7f) << (7 * count));
            count++;
        } while ((value & 0x80) != 0);
        if (length == 0 || length > maximum || (count > 1 && value == 0)) throw Bad("Invalid bounded UTF8 path/string length.");
        byte[] bytes = reader.ReadBytes(checked((int)length));
        if (bytes.Length != length) throw new EndOfStreamException();
        return StrictUtf8.GetString(bytes);
    }
    private static byte[] ReadBytes(FileStream stream, long offset, int size)
    {
        stream.Position = offset;
        byte[] bytes = new byte[size];
        stream.ReadExactly(bytes);
        return bytes;
    }
    private static string AttributeName(MetadataReader reader, CustomAttribute attribute)
    {
        if (attribute.Constructor.Kind != HandleKind.MemberReference) return "";
        EntityHandle parent = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
        if (parent.Kind != HandleKind.TypeReference) return "";
        TypeReference type = reader.GetTypeReference((TypeReferenceHandle)parent);
        return reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
    }
    private static InvalidDataException Bad(string reason) => new("Unsupported or invalid LF native update payload: " + reason);
}
