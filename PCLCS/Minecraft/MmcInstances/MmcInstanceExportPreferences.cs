using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using PCLLF.MMCC.Core;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
internal sealed record MmcExportPreferences(ImmutableDictionary<string,string> Values, ImmutableArray<string> ExtraGame,
    bool Fullscreen, bool IncludeServers, ImmutableArray<string> Transformations) {
    internal string Encode() => string.Join("\n", Values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => pair.Key + "=" + Quote(pair.Value))) + "\n";
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
}
internal static class MmcInstanceExportPreferences {
    internal static MmcExportPreferences Capture(MmcConfiguration package, MmcClonePreferences source, MmcInstanceExportRequest request) {
        var ini = LegacyBaseOperations.ParseIni(source.Ini); var values = new Dictionary<string,string>(StringComparer.Ordinal) {
            ["ConfigVersion"]="1.3", ["InstanceType"]="OneSix", ["name"]=request.DisplayName,
            ["OverrideCommands"]="false", ["OverrideEnv"]="false", ["OverrideJavaLocation"]="false"
        };
        var transformations = ImmutableArray.CreateBuilder<string>();
        // An active OverrideEnv/Env mapping is a live launch requirement. Portable export has no verified
        // encoding round-trip for it yet, so it is refused explicitly instead of being written as
        // OverrideEnv=false and silently dropped from the exported instance.
        if (!package.EnvironmentOverride.IsEmpty)
            throw new InvalidDataException("An active OverrideEnv/Env mapping has no verified portable export encoding; the requirement is retained rather than silently omitted.");
        // The console and PermGen groups have no equivalent in this launcher, so this export cannot carry them
        // either. They are recorded as an explicit adjustment instead of disappearing from the exported
        // instance unannounced; the receiver is told the requirement was dropped and never that it was
        // converted into a Metaspace or a launcher-visibility setting.
        if (MmcDisplayDifference.ConsoleDifference(package.Values, ReadFlag(package.Values, "OverrideConsole"), "instance.cfg") is not null)
            transformations.Add("An active package console preference is not portable and was omitted; it is not a launcher-visibility setting.");
        if (MmcDisplayDifference.PermanentGenerationDifference(package.Values, ReadFlag(package.Values, "OverrideMemory"), "instance.cfg") is not null)
            transformations.Add("A declared PermGen size is not portable and was omitted; it was not converted into a Metaspace option.");
        string Mode(string key) {
            string mode=ini.GetValueOrDefault(key,"InheritGlobal");
            if (mode is not ("InheritGlobal" or "UsePackage" or "Custom")) throw new InvalidDataException("Unknown managed preference mode: "+key);
            return mode;
        }
        string Need(IReadOnlyDictionary<string,string> dictionary,string key) => dictionary.TryGetValue(key,out string? value)?value:throw new InvalidDataException("Portable preference requires "+key);
        string javaMode=Mode("MmcJavaLocationMode");
        bool machineJava = javaMode=="UsePackage" && !string.IsNullOrWhiteSpace(package.Values.GetValueOrDefault("JavaPath")) ||
            javaMode=="Custom" && (ini.GetValueOrDefault("VersionArgumentJavaV2","0")!="0" ||
                ini.GetValueOrDefault("VersionArgumentJavaSelect","").StartsWith("{",StringComparison.Ordinal) || source.Config["InstanceForcedJava"] is JObject);
        if (machineJava) {
            if (!request.Options.DiscardMachineJavaSelection) throw new InvalidDataException("A machine Java identity/range cannot be exported implicitly; explicitly choose portable declared Java policy.");
            transformations.Add("Machine-specific Java selection omitted; component compatibleJavaMajors remains authoritative.");
        }
        string jvmMode=Mode("MmcJvmArgsMode");
        values["OverrideJavaArgs"]=(jvmMode!="InheritGlobal").ToString().ToLowerInvariant();
        if (jvmMode!="InheritGlobal") {
            string arguments=jvmMode=="UsePackage"?package.Values.GetValueOrDefault("JvmArgs",""):Need(ini,"VersionAdvanceJvm");
            PortableArguments(MmcArgumentTokenizer.Tokenize(arguments)); values["JvmArgs"]=arguments;
        }
        string memoryMode=Mode("MmcMemoryMode"); values["OverrideMemory"]=(memoryMode!="InheritGlobal").ToString().ToLowerInvariant();
        if (memoryMode!="InheritGlobal") {
            int minimum,maximum;
            if (memoryMode=="UsePackage") { minimum=Number(Need(package.Values,"MinMemAlloc"),0,1048576); maximum=Number(Need(package.Values,"MaxMemAlloc"),16,1048576); }
            else if (ini.GetValueOrDefault("VersionRamType") == "1") { minimum=0; maximum=checked((int)Math.Round(InstanceRamPolicy.Manual(Number(Need(ini,"VersionRamCustom"),0,4096))*1024)); }
            else {
                minimum=request.Options.PortableMinimumMemoryMiB??throw new InvalidDataException("Automatic hardware-dependent memory needs an explicit portable MiB choice.");
                maximum=request.Options.PortableMaximumMemoryMiB??throw new InvalidDataException("Automatic hardware-dependent memory needs an explicit portable MiB choice.");
                transformations.Add("Hardware-dependent memory policy frozen to the explicitly selected portable MiB values.");
            }
            if (minimum<0 || minimum>maximum || maximum is <16 or >1048576) throw new InvalidDataException("Portable memory bounds are invalid.");
            values["MinMemAlloc"]=minimum.ToString(CultureInfo.InvariantCulture); values["MaxMemAlloc"]=maximum.ToString(CultureInfo.InvariantCulture);
        }
        string windowMode=Mode("MmcWindowMode"); bool fullscreen=false; values["OverrideWindow"]=(windowMode!="InheritGlobal").ToString().ToLowerInvariant();
        if (windowMode!="InheritGlobal") {
            bool own=windowMode=="Custom"; IReadOnlyDictionary<string,string> dictionary=own?ini:package.Values;
            values["MinecraftWinWidth"]=Number(Need(dictionary,own?"MmcWindowWidth":"MinecraftWinWidth"),1,32768).ToString(CultureInfo.InvariantCulture);
            values["MinecraftWinHeight"]=Number(Need(dictionary,own?"MmcWindowHeight":"MinecraftWinHeight"),1,32768).ToString(CultureInfo.InvariantCulture);
            values["LaunchMaximized"]=Flag(dictionary.GetValueOrDefault(own?"MmcWindowMaximized":"LaunchMaximized","false")).ToString().ToLowerInvariant();
            fullscreen=own && Flag(ini.GetValueOrDefault("MmcWindowFullscreen","false"));
        }
        if (!string.IsNullOrWhiteSpace(ini.GetValueOrDefault("VersionAdvanceRun"))) throw new InvalidDataException("An active host custom command is not a portable MMC launch definition.");
        var game=ini.TryGetValue("VersionAdvanceGame",out string? extra)?MmcArgumentTokenizer.Tokenize(extra):ImmutableArray<string>.Empty;
        PortableArguments(game);
        MmcInstanceExportArgumentPolicy.Extra(game,request.Options.IncludeServers);
        string server=ini.TryGetValue("VersionServerEnter",out string? ownServer)?ownServer??"":
            Flag(package.Values.GetValueOrDefault("JoinServerOnLaunch","false"))?package.Values.GetValueOrDefault("JoinServerOnLaunchAddress",""):"";
        if (!request.Options.IncludeServers && !string.IsNullOrWhiteSpace(server)) transformations.Add("Server auto-join data omitted by the explicit default player-data exclusion policy.");
        if (request.Options.IncludeServers) {
            values["JoinServerOnLaunch"]="false";
            if (!string.IsNullOrWhiteSpace(server)) { values["JoinServerOnLaunch"]="true"; values["JoinServerOnLaunchAddress"]=server; }
        }
        if (jvmMode=="Custom") transformations.Add("Instance JVM override becomes the new package override, preserving an explicit empty value.");
        return new(values.ToImmutableDictionary(StringComparer.Ordinal),game,fullscreen,request.Options.IncludeServers,transformations.ToImmutable());
    }
    internal static void PortableArguments(IEnumerable<string> tokens) {
        foreach (string token in tokens) {
            if (token.Contains('\0') || token.StartsWith('@') || Regex.IsMatch(token,@"(?:[A-Za-z]:[\\/]|\\\\|file:/)",RegexOptions.IgnoreCase) ||
                Regex.IsMatch(token,@"(?i)(?:access[_-]?token|client[_-]?token|password|authorization)=(?!\$\{)[^\s]+"))
                throw new InvalidDataException("A launch token contains an unbound machine path or literal credential.");
        }
    }
    private static bool Flag(string value) => value.ToLowerInvariant() switch {"true" or "1"=>true,"false" or "0"=>false,_=>throw new InvalidDataException("Invalid portable Boolean.")};
    /// <summary>Reads an already-validated cfg Boolean; the reader has refused an unparsable value already.</summary>
    private static bool ReadFlag(IReadOnlyDictionary<string,string> values,string key) =>
        values.TryGetValue(key,out string? value) && (value.Equals("true",StringComparison.OrdinalIgnoreCase) || value=="1");
    private static int Number(string value,int minimum,int maximum) => int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out int result)&&result>=minimum&&result<=maximum?result:throw new InvalidDataException("Invalid portable numeric preference.");
}
