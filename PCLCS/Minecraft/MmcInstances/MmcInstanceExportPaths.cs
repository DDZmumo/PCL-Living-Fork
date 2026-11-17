namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
internal static class MmcInstanceExportPaths {
    internal static string Output(string value,string sourceRoot) {
        if(string.IsNullOrWhiteSpace(value)||!Path.IsPathFullyQualified(value)||value.StartsWith("\\\\",StringComparison.Ordinal)||value.StartsWith("//",StringComparison.Ordinal))throw new IOException("Export needs an explicit local ZIP file path.");
        string target=Path.GetFullPath(value),source=Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        if(!Path.GetExtension(target).Equals(".zip",StringComparison.OrdinalIgnoreCase))throw new IOException("Export target must be a ZIP file.");
        if(target.Equals(source,StringComparison.OrdinalIgnoreCase)||target.StartsWith(source+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Export output cannot be inside its source instance.");
        string parent=Path.GetDirectoryName(target)??throw new IOException("Export output has no parent directory.");
        string volume=Path.GetPathRoot(parent)!;
        if(new DriveInfo(volume).DriveType==DriveType.Network)throw new IOException("Export output cannot use a mapped network volume.");
        string current=volume;
        foreach(string part in parent[volume.Length..].Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries)) {
            Leaf(part);current=Path.Combine(current,part);var attr=MmcInstancePaths.Attributes(current);
            if(attr is null||(attr.Value&FileAttributes.Directory)==0||(attr.Value&FileAttributes.ReparsePoint)!=0)throw new IOException("Export parent must exist as a local non-link directory.");
        }
        Leaf(Path.GetFileName(target));
        parent=MmcCanonicalPaths.ForTrustedRoot(parent);target=Path.Combine(parent,Path.GetFileName(target));
        if(target.StartsWith(source+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Export physical output aliases its source instance.");
        if(MmcInstancePaths.Attributes(target) is not null)throw new IOException("Export target already exists; it was preserved.");
        return target;
    }
    private static void Leaf(string value) {
        if(value.Length is 0 or >255||value is "." or ".."||value.EndsWith('.')||value.EndsWith(' ')||value.Any(c=>c<32||"<>:\"/\\|?*".Contains(c)))throw new IOException("Invalid export path segment.");
        string stem=value.Split('.')[0];
        if(new[]{"CON","PRN","AUX","NUL","CONIN$","CONOUT$"}.Contains(stem,StringComparer.OrdinalIgnoreCase)||stem.Length==4&&(stem.StartsWith("COM",StringComparison.OrdinalIgnoreCase)||stem.StartsWith("LPT",StringComparison.OrdinalIgnoreCase))&&(stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3'))throw new IOException("Export path contains a reserved device name.");
    }
}
