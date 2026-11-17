namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
public sealed partial class PackUpdateEngine<TFile, TPackage, TChange, TPlan, TNetFile>
    where TFile : PackUpdateFileData where TPackage : PackUpdatePackageData<TFile>
    where TChange : PackUpdateChangeData<TFile> where TPlan : PackUpdatePlanData<TPackage, TChange> {
    /// <summary>Only returns validated operations. Managed callers must publish through MMC's
    /// common admission/configuration/file transaction, never the old execution boundary.</summary>
    internal IReadOnlyList<Tuple<string, string?, string?>> ManagedFileOperations(TPlan plan, CancellationToken token) {
        ValidateChoices(plan, token);
        return BuildFileOperations(plan, token).ToArray();
    }
}
