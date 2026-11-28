// SPDX-License-Identifier: MPL-2.0
using PCLCS;

namespace PCLLF.Native.Facade.Tests;

internal static class CredentialPersistenceChecks
{
    internal static void Run(FixtureEnvironment environment)
    {
        const string fixtureValue = "fixture-account-data";
        const string identify = "fixture-identify";
        const string existing = "previous-setting";
        var entry = new FixtureEntry();
        entry.Set(fixtureValue, null);
        var entries = new Dictionary<string, FixtureEntry> { [entry.Key] = entry };
        object gate = new();
        var messages = new List<string>();
        string stored = existing;
        bool registryFailed = false;
        int writes = 0;
        var host = new LegacySettingsHost<object> {
            Identify = () => throw new InvalidOperationException(fixtureValue),
            RegistryFolder = () => "Fixture",
            WriteRegistry = (_, _, value) => {
                writes++;
                if (registryFailed) throw new IOException(fixtureValue);
                stored = value!;
            },
            Log = (error, message, _, _) => messages.Add(message + error?.Message)
        };
        var engine = new LegacySettingsEngine<object, FixtureEntry, LegacyAccountMigrationPlan>(() => entries, () => gate, () => new(), host);

        CheckFailure();
        Native.Check(writes == 0 && stored == existing, "A failed encryption wrote anything to the credential store.");

        host.Identify = () => identify;
        registryFailed = true;
        CheckFailure();
        Native.Check(writes == 1 && stored == existing, "A failed credential write changed the persisted value.");

        registryFailed = false;
        engine.SaveEntry(entry);
        Native.Check(writes == 2 && stored != fixtureValue && LegacySettingsCrypto.Decrypt(stored, "PCL" + identify) == fixtureValue,
            "A successful write failed to persist readable ciphertext.");
        Native.Check(messages.All(message => !message.Contains(fixtureValue, StringComparison.Ordinal)), "Credential contents appeared in diagnostics.");
        environment.AssertNoForbiddenIo();

        void CheckFailure() {
            try { engine.SaveEntry(entry); }
            catch (InvalidOperationException error) {
                Native.Check(!error.ToString().Contains(fixtureValue, StringComparison.Ordinal), "Credential contents appeared in a reported error.");
                return;
            }
            throw new InvalidOperationException("Credential persistence did not report failure.");
        }
    }

    private sealed class FixtureEntry() : LegacySettingData<object>("LfSkinSites", "", true, null, (_, _) => { })
    {
        public override LegacySettingSource GetSourceCode() => LegacySettingSource.Registry;
        protected override string GetInstancePath(object instance) => throw new InvalidOperationException();
        public override void Save(object? instance = null) => throw new InvalidOperationException();
    }
}
