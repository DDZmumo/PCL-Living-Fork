# Explicit retained GTNH game acceptance

Register `MmcGtnhLaunchAcceptance.TryRun(args, out code)` before the default group parser. Do not add it to the CI group dictionary.

Run from the actual Native facade output:

```powershell
PCLLF.Native.Facade.Tests.exe --mmc-gtnh-launch-acceptance --case-root <completed-owned-java8-or-java21-case> --java-executable <installed-java.exe>
```

The case must have matching retained run/case owner markers, a successful official-package import receipt, fixture nonce and ended import-child receipt. The runner validates the actual published identity, generation, source recompile and owned input digests before changing acceptance-only preferences or starting Java. It does not extract a ZIP or download launcher inputs. The default memory is 6144 MiB maximum and 1024 MiB initial; the manual slider is derived through the product policy, optional JVM tokens are preserved, and required component JVM arguments remain in the real bound plan.

The host uses a fresh retained GUID profile and synthetic offline identity. Only account and launcher presentation boundaries are isolated; the game, HWND enumeration, Main executor, classpath inspector, sessions, durable pins and exit cleanup are genuine. Complete argv frames stay in memory; console files pass through the actual runtime redactor. `window-observed.json` proves only a native window, not a main menu or a playable world. Confirm those through real UI inspection.

The parent remains alive without a timeout kill or automatic restart. Evidence stays under `artifacts/mmc-gtnh-launch-acceptance/<timestamp-guid>`; `owner.json` identifies the profile, nonce and case, and each launch records process/session/Java/hash/command-length evidence. The game is left open for menu/world inspection.

The agent that owns the long-running command must remain active until the game and host have exited. Hand off the evidence path and UI work without ending that command owner's agent. If the host disappears without an exit receipt, treat that run as interrupted, not a successful game exit or an inferred Minecraft crash. The root agent should own subsequent real-game commands.

An interrupted retained run can be reconciled explicitly with:

```powershell
PCLLF.Native.Facade.Tests.exe --mmc-gtnh-reconcile-owned-run --case-root <same completed import case> --launch-root <interrupted launch evidence root>
```

This validates the prior owner/session chain and invokes the actual process-identity inspector and session/pin cleanup. Active or uncertain processes remain protected. It neither kills nor launches a game. Keep the interrupted logs and the reconciliation result. Build this runner with its normal project reference for actual game acceptance, so the host's complete runtime dependency graph is present.

After the real Java process and session cleanup have exited, write `control.json` in that evidence root:

```json
{"Nonce":"<owner nonce>","CommandId":"<new 32-hex GUID>","Action":"repair-and-relaunch"}
```

Actions are `repair`, `relaunch`, `repair-and-relaunch`, and `finish`. Active games refuse these commands; no action kills a game or changes a world. Each handled command is retained. Repair uses the real dedicated backend and existing locked originals, then records the new authorized generation. Close the game normally before `finish`; the profile and all acceptance evidence remain retained.
