using System.Text.RegularExpressions;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
internal static class MmcInstanceExportArgumentPolicy {
    private static readonly HashSet<string> AccountFields = new(new[] {
        "--username", "--uuid", "--accessToken", "--session", "--userProperties", "--clientId", "--clientToken", "--xuid"
    }, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RuntimeBindings = new(new[] {
        "--userType", "--gameDir", "--assetsDir", "--assetIndex"
    }, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ServerFields = new(new[] {
        "--server", "--port", "--quickPlayMultiplayer"
    }, StringComparer.OrdinalIgnoreCase);
    private static string Name(string token) { int split=token.IndexOf('='); return split<0?token:token[..split]; }

    internal static void Extra(IEnumerable<string> arguments,bool includeServers) {
        foreach(string token in arguments) {
            string field=Name(token);
            // Match the host's additive-argument boundary as well as every account field:
            // even a macro must not replace a live account/root binding through ExtraGame.
            if(AccountFields.Contains(field)||RuntimeBindings.Contains(field))
                throw new InvalidDataException("Extra game arguments cannot replace account or instance-root bindings.");
            if(!includeServers&&ServerFields.Contains(field))
                throw new InvalidDataException("Auto-join arguments require the explicit server-data export option.");
        }
    }
    internal static void Declared(IReadOnlyList<string> arguments,bool includeServers) {
        for(int index=0;index<arguments.Count;index++) {
            string token=arguments[index],field=Name(token);
            if(!includeServers&&ServerFields.Contains(field))
                throw new InvalidDataException("Declared server arguments require the explicit server-data export option.");
            if(!AccountFields.Contains(field))continue;
            int separator=token.IndexOf('=');
            string value=separator<0?(++index<arguments.Count?arguments[index]:""):token[(separator+1)..];
            if(!Regex.IsMatch(value,@"^\$\{[A-Za-z_][A-Za-z0-9_]*\}$"))
                throw new InvalidDataException("Literal account/session data cannot be exported in a component game argument.");
        }
    }
}
