// SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;

namespace PCLLF.MMCC.Core;

public static class MmcJavaPolicyEvaluator {
    public static MmcJavaPolicy Create(IEnumerable<int> declaredMajors, string? recommendedRuntimeName,
        IReadOnlyList<MmcJavaConstraint>? constraints = null) {
        ArgumentNullException.ThrowIfNull(declaredMajors);
        var declared = declaredMajors.Distinct().ToImmutableArray();
        if (declared.Any(m => m < 1)) throw new ArgumentException("Java majors must be positive.", nameof(declaredMajors));
        var hard = (constraints ?? []).ToImmutableArray();
        bool invalid = hard.Any(c => c is null || string.IsNullOrEmpty(c.Source) || c.MinimumMajor is < 1 || c.MaximumMajor is < 1 ||
            c.MinimumMajor > c.MaximumMajor || !c.AllowedMajors.IsDefault && (c.AllowedMajors.IsEmpty || c.AllowedMajors.Any(m => m < 1)));
        if (invalid) return new(MmcJavaPolicyKind.Unsatisfiable, [], recommendedRuntimeName, hard);
        int lower = hard.Select(c => c.MinimumMajor ?? 1).DefaultIfEmpty(1).Max();
        int upper = hard.Select(c => c.MaximumMajor ?? int.MaxValue).DefaultIfEmpty(int.MaxValue).Min();
        if (lower > upper) return new(MmcJavaPolicyKind.Unsatisfiable, [], recommendedRuntimeName, hard);
        bool Allows(int value) => hard.All(c => (c.MinimumMajor is null || value >= c.MinimumMajor) &&
            (c.MaximumMajor is null || value <= c.MaximumMajor) && (c.AllowedMajors.IsDefault || c.AllowedMajors.Contains(value)));
        if (!declared.IsEmpty) {
            var allowed = declared.Where(Allows).ToImmutableArray();
            return new(allowed.IsEmpty ? MmcJavaPolicyKind.Unsatisfiable : MmcJavaPolicyKind.DeclaredCandidates, allowed, recommendedRuntimeName, hard);
        }
        var finite = hard.FirstOrDefault(c => !c.AllowedMajors.IsDefault);
        if (finite is not null && !finite.AllowedMajors.Any(Allows)) return new(MmcJavaPolicyKind.Unsatisfiable, [], recommendedRuntimeName, hard);
        return new(MmcJavaPolicyKind.Unspecified, [], recommendedRuntimeName, hard);
    }

    public static MmcJavaSelection FilterInstalled(MmcJavaPolicy policy, IEnumerable<MmcJavaCandidate> candidates) {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(candidates);
        if (policy.Kind == MmcJavaPolicyKind.Unsatisfiable) return new(MmcJavaSelectionState.Unsatisfiable, []);
        var compatible = candidates.Where(c => policy.Allows(c.Major)).ToImmutableArray();
        return new(compatible.IsEmpty ? MmcJavaSelectionState.NoInstalledCandidate : MmcJavaSelectionState.CompatibleCandidates, compatible);
    }
}
