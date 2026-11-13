// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Immutable;

namespace PCLLF.MMCC.Core;

/// <summary>
/// Read-time validation of an active package command (<c>PreLoadCommand</c>／<c>PreLaunchCommand</c>／
/// <c>PostExitCommand</c>). Only the obligations a reader can decide from the text alone are enforced here;
/// every runtime obligation (an undefined launch variable, an input that changed, a non-zero exit code) is
/// enforced at the stage boundary instead, because it needs this launch's live values.
/// </summary>
internal static class MmcCommandReader {
    /// <summary>Existing instance command budget; the same limit the player's own command uses.</summary>
    internal const int MaximumLength = 16384;

    /// <summary>
    /// Launch variables that carry account, session or launcher credential material. A package command may
    /// never bind them: the requirement cannot be honoured, so it is refused rather than bound.
    /// </summary>
    private static readonly ImmutableHashSet<string> CredentialVariables = ImmutableHashSet.Create(StringComparer.Ordinal,
        "auth_player_name", "auth_uuid", "auth_access_token", "access_token", "auth_session", "user_type",
        "user_properties", "auth_xuid", "clientid", "client_id", "client_token");

    internal static void Validate(string command, string field, string source) {
        if (command.Contains('\0'))
            throw new MmcInputException("MMC_CFG_COMMAND", source, $"The active command {field} contains NUL.");
        if (command.Length > MaximumLength)
            throw new MmcInputException("MMC_CFG_COMMAND", source, $"The active command {field} exceeds the {MaximumLength}-character instance command budget.");
        foreach (string name in References(command))
            if (CredentialVariables.Contains(name))
                throw new MmcInputException("MMC_CFG_COMMAND_CREDENTIAL", source,
                    $"The active command {field} references the credential variable {name}; a package command never binds account or session credentials.");
    }

    /// <summary>
    /// The variable names a command text references. Both accepted spellings are recognised: <c>${name}</c>
    /// and the bare <c>$INST_DIR</c>／<c>$INST_MC_DIR</c> forms, which keep their word-boundary rule so a
    /// longer identifier is not mistaken for them.
    /// </summary>
    private static System.Collections.Generic.List<string> References(string command) {
        var result = new System.Collections.Generic.List<string>();
        for (int index = 0; index < command.Length; index++) {
            if (command[index] != '$' || index + 1 == command.Length) continue;
            if (command[index + 1] == '{') {
                int end = command.IndexOf('}', index + 2);
                if (end < 0) continue;
                result.Add(command[(index + 2)..end]);
                index = end;
                continue;
            }
            foreach (string bare in new[] { "INST_MC_DIR", "INST_DIR" }) {
                int stop = index + bare.Length + 1;
                if (stop <= command.Length && command.AsSpan(index + 1, bare.Length).SequenceEqual(bare) &&
                    (stop == command.Length || !char.IsAsciiLetterOrDigit(command[stop]) && command[stop] != '_')) {
                    result.Add(bare); index = stop - 1; break;
                }
            }
        }
        return result;
    }
}
