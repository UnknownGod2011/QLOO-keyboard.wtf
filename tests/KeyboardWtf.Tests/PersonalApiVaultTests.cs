using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Services.Ai.Backends;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class PersonalApiVaultTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-personal-vault-tests", Guid.NewGuid().ToString("N"));
    private string VaultPath => Path.Combine(_directory, "personal-api.dat");
    private const string Canary = "synthetic-test-secret-never-a-real-key-1234";
    private PersonalApiVault NewVault() => new(VaultPath);

    [Fact]
    public void New_profile_defaults_to_managed_without_writing_or_importing_legacy_keys()
    {
        var view = NewVault().GetView();
        Assert.Equal(AiBackendPolicy.Managed, view.Policy);
        Assert.False(view.Configured);
        Assert.Equal(0, view.Revision);
        Assert.Empty(view.Keys);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void Qloo_profile_can_default_to_personal_only_without_creating_or_importing_data()
    {
        var view = new PersonalApiVault(VaultPath, defaultPolicy: AiBackendPolicy.PersonalOnly).GetView();
        Assert.Equal(AiBackendPolicy.PersonalOnly, view.Policy);
        Assert.False(view.Configured);
        Assert.Equal(0, view.Revision);
        Assert.Empty(view.Keys);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void Real_DPAPI_restart_preserves_policy_keys_and_opaque_metadata_only()
    {
        var vault = NewVault();
        var added = vault.Add(0, "My Gemini", Canary, PersonalKeyValidation.AuthenticationValidated);
        vault.SetPolicy(added.Revision, AiBackendPolicy.PersonalOnly);
        var restarted = NewVault();
        var view = restarted.GetView();
        Assert.Equal(AiBackendPolicy.PersonalOnly, view.Policy);
        Assert.Equal("••••1234", Assert.Single(view.Keys).Mask);
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(view));
        Assert.DoesNotContain(Canary, Encoding.UTF8.GetString(File.ReadAllBytes(VaultPath)));
        Assert.DoesNotContain(Canary, File.ReadAllText(VaultPath + ".configured"));
        using var lease = restarted.Acquire(restarted.ReadConfiguration());
        Assert.Equal(Canary, restarted.ReadSecretForTransport(lease));
        Assert.Equal(added.Keys[0].Id, lease.SlotId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("marker")]
    public void Existing_missing_or_corrupt_store_blocks_instead_of_defaulting_to_managed(string kind)
    {
        var vault = NewVault();
        vault.SetPolicy(0, AiBackendPolicy.PersonalOnly);
        if (kind == "missing") File.Delete(VaultPath);
        else if (kind == "marker") File.Delete(VaultPath + ".configured");
        else File.WriteAllBytes(VaultPath, [1, 2, 3]);
        Assert.Throws<PersonalApiConfigurationException>(() => NewVault().ReadConfiguration());
        if (kind == "corrupt") Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(VaultPath));
    }

    [Fact]
    public void Copying_encrypted_document_to_another_profile_does_not_import_it()
    {
        NewVault().Add(0, "primary", Canary, PersonalKeyValidation.AuthenticationValidated);
        var otherPath = Path.Combine(_directory, "other-profile.dat");
        File.Copy(VaultPath, otherPath);
        File.Copy(VaultPath + ".configured", otherPath + ".configured");
        Assert.Throws<PersonalApiConfigurationException>(() => new PersonalApiVault(otherPath).GetView());
    }

    [Fact]
    public void Protection_failure_and_atomic_write_failure_preserve_previous_durable_bytes()
    {
        var vault = NewVault();
        var saved = vault.Add(0, "stable", Canary, PersonalKeyValidation.AuthenticationValidated);
        var bytes = File.ReadAllBytes(VaultPath);
        var badProtection = new PersonalApiVault(VaultPath, new BrokenProtection());
        Assert.Throws<PersonalApiConfigurationException>(() => badProtection.SetPolicy(saved.Revision, AiBackendPolicy.PersonalOnly));
        Assert.Equal(bytes, File.ReadAllBytes(VaultPath));
        var badWrite = new PersonalApiVault(VaultPath, atomicWrite: (_, _) => throw new IOException(Canary));
        var error = Assert.Throws<PersonalApiConfigurationException>(() => badWrite.SetPolicy(saved.Revision, AiBackendPolicy.PersonalOnly));
        Assert.DoesNotContain(Canary, error.ToString());
        Assert.Equal(bytes, File.ReadAllBytes(VaultPath));
        Assert.Equal(AiBackendPolicy.Managed, NewVault().ReadConfiguration().Policy);
    }

    [Fact]
    public void Concurrent_additions_enforce_expected_revision_and_ten_key_ceiling()
    {
        var vault = NewVault();
        var first = vault.Add(0, "0", Canary, PersonalKeyValidation.AuthenticationValidated);
        var successes = 0;
        Parallel.For(1, 25, index =>
        {
            try
            {
                new PersonalApiVault(VaultPath).Add(first.Revision, index.ToString(), Canary + index,
                    PersonalKeyValidation.AuthenticationValidated);
                Interlocked.Increment(ref successes);
            }
            catch (InvalidOperationException) { }
        });
        Assert.Equal(1, successes);
        while (vault.GetView().Keys.Count < 10)
        {
            var current = vault.GetView();
            vault.Add(current.Revision, "added", Canary + Guid.NewGuid().ToString("N"), PersonalKeyValidation.Unknown);
        }
        var full = vault.GetView();
        Assert.Throws<InvalidOperationException>(() => vault.Add(full.Revision, "11", Canary + "extra", PersonalKeyValidation.Unknown));
        Assert.Equal(10, NewVault().GetView().Keys.Count);
    }

    [Fact]
    public void Duplicate_control_character_and_oversized_inputs_do_not_mutate_store()
    {
        var vault = NewVault();
        var saved = vault.Add(0, "safe", Canary, PersonalKeyValidation.AuthenticationValidated);
        Assert.Throws<InvalidOperationException>(() => vault.Add(saved.Revision, "same", Canary, PersonalKeyValidation.Unknown));
        foreach (var invalid in new[] { "short", Canary + "\r\nInjected: yes", Canary + " ", new string('x', 513) })
            Assert.Throws<ArgumentException>(() => vault.Add(saved.Revision, "label", invalid, PersonalKeyValidation.Unknown));
        Assert.Throws<ArgumentException>(() => vault.Add(saved.Revision, "unsafe\nlabel", Canary + "new", PersonalKeyValidation.Unknown));
        Assert.Equal(saved.Revision, vault.GetView().Revision);
    }

    [Fact]
    public void Active_lease_blocks_replace_disable_remove_and_reset_even_through_another_instance()
    {
        var vault = NewVault();
        var saved = vault.Add(0, "active", Canary, PersonalKeyValidation.AuthenticationValidated);
        var key = saved.Keys[0];
        var lease = vault.Acquire(vault.ReadConfiguration());
        var other = NewVault();
        Assert.Throws<InvalidOperationException>(() => other.Replace(saved.Revision, key.Id, key.Revision,
            Canary + "replacement", PersonalKeyValidation.AuthenticationValidated));
        Assert.Throws<InvalidOperationException>(() => other.Remove(saved.Revision, key.Id));
        Assert.Throws<InvalidOperationException>(() => other.SetEnabled(saved.Revision, key.Id, false));
        Assert.Throws<InvalidOperationException>(() => other.ResetExplicitly());
        var policy = other.SetPolicy(saved.Revision, AiBackendPolicy.PersonalOnly);
        Assert.Equal(Canary, vault.ReadSecretForTransport(lease));
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => vault.ReadSecretForTransport(lease));
        Assert.Empty(other.Remove(policy.Revision, key.Id).Keys);
    }

    [Fact]
    public void Pool_keeps_healthy_choice_and_uses_fixed_order_when_it_becomes_disabled()
    {
        var vault = NewVault();
        vault.Add(0, "first", Canary, PersonalKeyValidation.AuthenticationValidated);
        var saved = vault.Add(vault.GetView().Revision, "second", Canary + "2", PersonalKeyValidation.AuthenticationValidated);
        var config = vault.ReadConfiguration();
        using (var first = vault.Acquire(config)) Assert.Equal(saved.Keys[0].Id, first.SlotId);
        using (var again = vault.Acquire(config)) Assert.Equal(saved.Keys[0].Id, again.SlotId);
        vault.SetEnabled(saved.Revision, saved.Keys[0].Id, false);
        using var next = vault.Acquire(vault.ReadConfiguration());
        Assert.Equal(saved.Keys[1].Id, next.SlotId);
    }

    [Fact]
    public void Unknown_project_quota_cools_entire_pool_and_survives_restart()
    {
        var now = DateTimeOffset.UtcNow;
        var vault = new PersonalApiVault(VaultPath, utcNow: () => now);
        vault.Add(0, "one", Canary, PersonalKeyValidation.AuthenticationValidated);
        vault.Add(vault.GetView().Revision, "two", Canary + "2", PersonalKeyValidation.AuthenticationValidated);
        vault.CoolDownUnknownScopePool(TimeSpan.FromMinutes(1));
        var restarted = new PersonalApiVault(VaultPath, utcNow: () => now);
        Assert.ThrowsAny<InvalidOperationException>(() => restarted.Acquire(restarted.ReadConfiguration()));
        now = now.AddMinutes(2);
        using var after = restarted.Acquire(restarted.ReadConfiguration());
        Assert.NotEqual(Guid.Empty, after.SlotId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pool_cooldown_has_a_typed_base_compatible_failure_without_rotating_or_mutating_keys(bool pinnedLease)
    {
        var now = DateTimeOffset.UtcNow;
        var vault = new PersonalApiVault(VaultPath, utcNow: () => now);
        vault.Add(0, "first", Canary, PersonalKeyValidation.AuthenticationValidated);
        vault.Add(vault.GetView().Revision, "second", Canary + "2", PersonalKeyValidation.AuthenticationValidated);
        using var owned = vault.Acquire(vault.ReadConfiguration());
        var originalSlot = owned.SlotId;
        vault.CoolDownUnknownScopePool(TimeSpan.FromMinutes(1));
        var cooledView = vault.GetView();
        var cooledBytes = File.ReadAllBytes(VaultPath);

        var error = Record.Exception(() =>
        {
            if (pinnedLease) vault.EnsureDispatchAllowed(owned);
            else using (vault.Acquire(vault.ReadConfiguration())) { }
        });

        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Equal("PersonalApiCooldownException", error.GetType().Name);
        Assert.DoesNotContain(Canary, error.ToString());
        Assert.Equal(cooledBytes, File.ReadAllBytes(VaultPath));
        Assert.Equal(JsonSerializer.Serialize(cooledView), JsonSerializer.Serialize(vault.GetView()));
        now = now.AddMinutes(2);
        vault.EnsureDispatchAllowed(owned);
        using var next = vault.Acquire(vault.ReadConfiguration());
        Assert.Equal(originalSlot, next.SlotId);
    }

    [Fact]
    public void Failed_replacement_does_not_destroy_old_key_and_late_validation_cannot_resurrect_removed_key()
    {
        var vault = NewVault();
        var saved = vault.Add(0, "old", Canary, PersonalKeyValidation.AuthenticationValidated);
        var row = saved.Keys[0];
        Assert.Throws<InvalidOperationException>(() => vault.Replace(saved.Revision, row.Id, row.Revision,
            Canary + "bad", PersonalKeyValidation.Unavailable));
        Assert.Equal(saved.Revision, vault.GetView().Revision);
        vault.Remove(saved.Revision, row.Id);
        Assert.False(vault.ApplyValidation(saved.Revision, row.Id, row.Revision, PersonalKeyValidation.AuthenticationValidated));
        Assert.Empty(vault.GetView().Keys);
    }

    [Fact]
    public void Temporary_validation_failure_preserves_enabled_authenticated_key_but_invalid_key_is_disabled()
    {
        var vault = NewVault();
        var saved = vault.Add(0, "one", Canary, PersonalKeyValidation.AuthenticationValidated);
        var row = saved.Keys[0];
        Assert.True(vault.ApplyValidation(saved.Revision, row.Id, row.Revision, PersonalKeyValidation.Unavailable));
        Assert.True(vault.GetView().Keys[0].Enabled);
        using (var lease = vault.Acquire(vault.ReadConfiguration())) Assert.NotNull(lease);
        Assert.True(vault.ApplyValidation(vault.GetView().Revision, row.Id, row.Revision, PersonalKeyValidation.InvalidCredential));
        Assert.False(vault.GetView().Keys[0].Enabled);
        Assert.Throws<InvalidOperationException>(() => vault.Acquire(vault.ReadConfiguration()));
    }

    [Fact]
    public void Reset_changes_store_identity_and_cannot_rebind_old_operation_to_new_keys()
    {
        var vault = NewVault();
        vault.SetPolicy(0, AiBackendPolicy.PersonalOnly);
        vault.Add(1, "old", Canary, PersonalKeyValidation.AuthenticationValidated);
        var original = vault.ReadConfiguration();
        vault.ResetExplicitly();
        vault.Add(1, "new", Canary + "new", PersonalKeyValidation.AuthenticationValidated);
        Assert.Equal(original.Revision, vault.ReadConfiguration().Revision);
        Assert.NotEqual(original.StoreId, vault.ReadConfiguration().StoreId);
        Assert.Throws<InvalidOperationException>(() => vault.Acquire(original));
    }

    [Fact]
    public void First_write_failure_leaves_configured_marker_and_fails_closed_instead_of_silent_default()
    {
        var protection = new PersonalCurrentUserProtection();
        var vault = new PersonalApiVault(VaultPath, protection, (path, bytes) =>
        {
            if (path.EndsWith(".configured", StringComparison.Ordinal)) File.WriteAllBytes(path, bytes);
            else throw new IOException("Simulated crash boundary before encrypted document commit");
        });
        Assert.Throws<PersonalApiConfigurationException>(() => vault.SetPolicy(0, AiBackendPolicy.PersonalOnly));
        Assert.True(File.Exists(VaultPath + ".configured"));
        Assert.False(File.Exists(VaultPath));
        Assert.Throws<PersonalApiConfigurationException>(() => NewVault().ReadConfiguration());
        var recovered = NewVault().ResetExplicitly();
        Assert.Equal(AiBackendPolicy.Managed, recovered.Policy);
        Assert.True(recovered.Configured);
    }

    [Fact]
    public void Unsupported_schema_and_missing_required_fields_fail_closed_without_deleting_evidence()
    {
        NewVault().SetPolicy(0, AiBackendPolicy.PersonalOnly);
        var entropy = SHA256.HashData(Encoding.UTF8.GetBytes("keyboard.wtf/personal-api/v1/" + Path.GetFullPath(VaultPath).ToUpperInvariant()));
        var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(VaultPath), entropy, DataProtectionScope.CurrentUser);
        try
        {
            var document = System.Text.Json.Nodes.JsonNode.Parse(plaintext)!;
            document["Schema"] = 999;
            var ciphertext = ProtectedData.Protect(Encoding.UTF8.GetBytes(document.ToJsonString()), entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(VaultPath, ciphertext);
            Assert.Throws<PersonalApiConfigurationException>(() => NewVault().GetView());
            Assert.Equal(ciphertext, File.ReadAllBytes(VaultPath));
            document["Schema"] = 1;
            document.AsObject().Remove("Policy");
            File.WriteAllBytes(VaultPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(document.ToJsonString()), entropy, DataProtectionScope.CurrentUser));
            Assert.Throws<PersonalApiConfigurationException>(() => NewVault().GetView());
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    [Fact]
    public void Failed_or_unknown_addition_is_inactive_and_cannot_be_enabled_without_authentication()
    {
        var vault = NewVault();
        var saved = vault.Add(0, "unknown", Canary, PersonalKeyValidation.Unavailable);
        var key = saved.Keys[0];
        Assert.False(key.Enabled);
        Assert.Throws<InvalidOperationException>(() => vault.SetEnabled(saved.Revision, key.Id, true));
        Assert.Throws<InvalidOperationException>(() => vault.Acquire(vault.ReadConfiguration()));
        Assert.True(vault.ApplyValidation(saved.Revision, key.Id, key.Revision, PersonalKeyValidation.AuthenticationValidated));
        Assert.False(vault.GetView().Keys[0].Enabled); // Validation does not implicitly change the user's enable choice.
        vault.SetEnabled(vault.GetView().Revision, key.Id, true);
        using var lease = vault.Acquire(vault.ReadConfiguration());
        Assert.Equal(key.Id, lease.SlotId);
    }

    [Fact]
    public void Router_with_real_vault_personal_only_pins_key_and_never_needs_managed_account()
    {
        var vault = NewVault();
        vault.Add(0, "one", Canary, PersonalKeyValidation.AuthenticationValidated);
        vault.SetPolicy(vault.GetView().Revision, AiBackendPolicy.PersonalOnly);
        var router = new AiBackendRouter(vault, vault);
        using var operation = router.BeginOperation();
        Assert.Equal(AiBackendOwner.Personal, operation.Owner);
    }

    private sealed class BrokenProtection : IPersonalDataProtection
    {
        private readonly PersonalCurrentUserProtection _real = new();
        public byte[] Protect(byte[] plaintext, byte[] entropy) => throw new CryptographicException(Canary);
        public byte[] Unprotect(byte[] ciphertext, byte[] entropy) => _real.Unprotect(ciphertext, entropy);
    }

    public void Dispose()
    {
        // Only this test's generated temporary directory, never an installed profile.
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
