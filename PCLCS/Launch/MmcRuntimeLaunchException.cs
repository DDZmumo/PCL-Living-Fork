namespace PCLCS;

// Original LF host implementation; governed by the repository LICENCE.
/// <summary>A refused managed-launch requirement, identified by a stable code.</summary>
public sealed class MmcRuntimeLaunchException(string code, string message) : InvalidOperationException(message) {
    public string Code { get; } = code;
}
