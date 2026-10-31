using System.Net;
using System.Net.Http;
using System.Text.Json;
using KeyboardWtf.Services.Ai.Backends;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class PersonalApiSettingsTests : IDisposable
{
    private const string Canary = "synthetic-native-settings-secret-1234";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-personal-settings-" + Guid.NewGuid());
    private readonly PersonalApiVault _vault;
    public PersonalApiSettingsTests() => _vault = new(Path.Combine(_directory, "personal.bin"));

    [Fact]
    public async Task Browser_action_only_launches_native_entry_and_returns_masked_view()
    {
        var dialog = new Entry();
        using var validator = Validator();
        var controller = new PersonalApiSettingsController(_vault, validator, dialog);
        var result = await controller.ExecuteAsync("{\"action\":\"add\",\"revision\":0}", default);
        Assert.True(result.Ok);
        Assert.Equal(1, dialog.Calls);
        Assert.Null(dialog.Last.Secret);
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(result));
        Assert.Equal("••••1234", Assert.Single(result.State.Keys).Mask);
        Assert.Contains("have not been tested", result.Message);
    }

    [Fact]
    public void Personal_key_view_uses_the_settings_page_validated_field()
    {
        _vault.Add(0, "fixture", Canary, PersonalKeyValidation.AuthenticationValidated);
        var json = JsonSerializer.Serialize(_vault.GetView(),
            new JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        using var document = JsonDocument.Parse(json);
        var key = document.RootElement.GetProperty("keys")[0];

        Assert.True(key.GetProperty("validated").GetBoolean());
        Assert.False(key.TryGetProperty("authenticationValidated", out _));
        Assert.DoesNotContain(Canary, json);
    }

    [Fact]
    public async Task Qloo_settings_cannot_select_managed_and_reset_keeps_personal_only()
    {
        var vault = new PersonalApiVault(Path.Combine(_directory, "qloo-personal.bin"),
            defaultPolicy: AiBackendPolicy.PersonalOnly);
        vault.Add(0, "QLOO test", Canary, PersonalKeyValidation.AuthenticationValidated);
        var dialog = new Entry { ConfirmReset = true };
        using var validator = Validator();
        var controller = new PersonalApiSettingsController(vault, validator, dialog, forcePersonalOnly: true);

        var reset = await controller.ExecuteAsync(JsonSerializer.Serialize(new
            { action = "reset", revision = vault.GetView().Revision }), default);
        Assert.True(reset.Ok);
        Assert.Equal(AiBackendPolicy.PersonalOnly, reset.State.Policy);
        Assert.Empty(reset.State.Keys);

        var changePolicy = await controller.ExecuteAsync(JsonSerializer.Serialize(new
            { action = "policy", revision = reset.State.Revision, policy = (int)AiBackendPolicy.Managed }), default);
        Assert.False(changePolicy.Ok);
        Assert.Equal(AiBackendPolicy.PersonalOnly, vault.GetView().Policy);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("apiKey")]
    [InlineData("secret")]
    [InlineData("label")]
    public async Task Browser_request_cannot_submit_key_or_arbitrary_extra_fields(string field)
    {
        var dialog = new Entry();
        using var validator = Validator();
        var controller = new PersonalApiSettingsController(_vault, validator, dialog);
        var result = await controller.ExecuteAsync(JsonSerializer.Serialize(new Dictionary<string, object>
            { ["action"] = "add", ["revision"] = 0, [field] = Canary }), default);
        Assert.False(result.Ok);
        Assert.Equal(0, dialog.Calls);
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(result));
        Assert.Empty(_vault.GetView().Keys);
    }

    [Fact]
    public async Task Cancelled_dialog_does_not_validate_or_save()
    {
        var dialog = new Entry { Cancel = true };
        using var validator = new PersonalGeminiKeyValidator(new Handler((_, _) => throw new Xunit.Sdk.XunitException("No validation expected")));
        var controller = new PersonalApiSettingsController(_vault, validator, dialog);
        Assert.False((await controller.ExecuteAsync("{\"action\":\"add\"}", default)).Ok);
        Assert.False(_vault.GetView().Configured);
    }

    [Fact]
    public async Task Unknown_addition_inactive_failed_replacement_retains_original()
    {
        var dialog = new Entry();
        using var validator = Validator(503);
        var controller = new PersonalApiSettingsController(_vault, validator, dialog);
        var added = await controller.ExecuteAsync("{\"action\":\"add\"}", default);
        Assert.True(added.Ok);
        var row = Assert.Single(added.State.Keys);
        Assert.False(row.Enabled);
        Assert.Contains("inactive", added.Message);
        dialog.Secret = "synthetic-replacement-must-not-save-5678";
        var replaced = await controller.ExecuteAsync(Action("replace", added.State, row), default);
        Assert.False(replaced.Ok);
        using var lease = _vault.AcquireForValidation(added.State.Revision, row.Id, row.Revision);
        Assert.Equal(Canary, _vault.ReadSecretForTransport(lease));
    }

    [Fact]
    public async Task Settings_policy_applies_to_new_work_only_and_secret_free_state_survives_restart()
    {
        var state = _vault.Add(0, "fixture", Canary, PersonalKeyValidation.AuthenticationValidated);
        state = _vault.SetPolicy(state.Revision, AiBackendPolicy.PersonalOnly);
        var router = new AiBackendRouter(_vault, _vault);
        using var operation = router.BeginOperation();
        using var validator = Validator();
        var controller = new PersonalApiSettingsController(_vault, validator, new Entry());
        var result = await controller.ExecuteAsync(JsonSerializer.Serialize(new { action = "policy", revision = state.Revision,
            policy = (int)AiBackendPolicy.Managed }), default);
        Assert.True(result.Ok);
        Assert.Equal(AiBackendOwner.Personal, operation.Owner);
        var restarted = new PersonalApiVault(Path.Combine(_directory, "personal.bin"));
        Assert.Equal(AiBackendPolicy.Managed, restarted.ReadConfiguration().Policy);
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(controller.GetState()));
    }

    [Fact]
    public async Task Late_validation_is_discarded_after_policy_revision_changes()
    {
        var state = _vault.Add(0, "fixture", Canary, PersonalKeyValidation.AuthenticationValidated);
        var row = state.Keys[0];
        using var validator = new PersonalGeminiKeyValidator(new Handler((_, _) =>
        {
            _vault.SetPolicy(state.Revision, AiBackendPolicy.PersonalOnly);
            return Reply(401);
        }));
        var controller = new PersonalApiSettingsController(_vault, validator, new Entry());
        var result = await controller.ExecuteAsync(Action("validate", state, row), default);
        Assert.False(result.Ok);
        Assert.True(_vault.GetView().Keys[0].Enabled);
        Assert.Contains("stale", result.Message);
    }

    [Fact]
    public async Task Native_reset_requires_confirmation_and_does_not_delete_other_profile_files()
    {
        var state = _vault.Add(0, "fixture", Canary, PersonalKeyValidation.AuthenticationValidated);
        _vault.SetPolicy(state.Revision, AiBackendPolicy.PersonalOnly);
        var unrelated = Path.Combine(_directory, "settings.json");
        File.WriteAllText(unrelated, "user data fixture");
        var dialog = new Entry();
        using var validator = Validator();
        var controller = new PersonalApiSettingsController(_vault, validator, dialog);
        Assert.False((await controller.ExecuteAsync("{\"action\":\"reset\"}", default)).Ok);
        Assert.Equal(AiBackendPolicy.PersonalOnly, _vault.ReadConfiguration().Policy);
        dialog.ConfirmReset = true;
        Assert.True((await controller.ExecuteAsync("{\"action\":\"reset\"}", default)).Ok);
        Assert.Equal(AiBackendPolicy.Managed, _vault.ReadConfiguration().Policy);
        Assert.Empty(_vault.GetView().Keys);
        Assert.Equal("user data fixture", File.ReadAllText(unrelated));
    }

    [Fact]
    public async Task Concurrent_dialog_requests_fail_without_queueing_or_duplicate_additions()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var validator = new PersonalGeminiKeyValidator(new Handler(async (_, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return await Reply(200);
        }));
        var dialog = new Entry();
        var controller = new PersonalApiSettingsController(_vault, validator, dialog);
        var first = controller.ExecuteAsync("{\"action\":\"add\"}", default);
        await entered.Task;
        Assert.False((await controller.ExecuteAsync("{\"action\":\"add\"}", default)).Ok);
        Assert.Equal(1, dialog.Calls);
        release.SetResult();
        Assert.True((await first).Ok);
        Assert.Single(_vault.GetView().Keys);
    }

    [Fact]
    public async Task Pending_native_dialog_can_be_revealed_without_creating_another_or_holding_the_lease_after_cancel()
    {
        using var validator = new PersonalGeminiKeyValidator(new Handler((_, _) =>
            throw new Xunit.Sdk.XunitException("No validation expected")));
        var dialog = new PendingEntry();
        var controller = new PersonalApiSettingsController(_vault, validator, dialog);
        using var cancelled = new CancellationTokenSource();
        var first = controller.ExecuteAsync("{\"action\":\"add\"}", cancelled.Token);
        await dialog.Entered.Task;

        var reveal = await controller.ExecuteAsync("{\"action\":\"reveal\"}", default);
        Assert.True(reveal.Ok);
        Assert.Equal(1, dialog.Reveals);
        Assert.Equal(1, dialog.Calls);
        Assert.Contains("in progress", (await controller.ExecuteAsync("{\"action\":\"add\"}", default)).Message);
        Assert.Equal(1, dialog.Calls);

        cancelled.Cancel();
        Assert.False((await first).Ok);
        Assert.False((await controller.ExecuteAsync("{\"action\":\"reveal\"}", default)).Ok);
        Assert.False((await controller.ExecuteAsync("{\"action\":\"add\"}", default)).Ok);
        Assert.Equal(2, dialog.Calls);
        Assert.Empty(_vault.GetView().Keys);
    }

    [Fact]
    public async Task Reveal_without_pending_dialog_does_not_change_settings()
    {
        var dialog = new Entry();
        using var validator = Validator();
        var controller = new PersonalApiSettingsController(_vault, validator, dialog);
        var result = await controller.ExecuteAsync("{\"action\":\"reveal\"}", default);
        Assert.False(result.Ok);
        Assert.Equal(0, dialog.Calls);
        Assert.Equal(0, _vault.GetView().Revision);
    }

    [Fact]
    public async Task Unavailable_validation_does_not_leave_settings_action_occupied()
    {
        using var validator = Validator(503);
        var controller = new PersonalApiSettingsController(_vault, validator, new Entry());
        var added = await controller.ExecuteAsync("{\"action\":\"add\"}", default);
        Assert.True(added.Ok);
        Assert.False(Assert.Single(added.State.Keys).Enabled);

        var next = await controller.ExecuteAsync(JsonSerializer.Serialize(new
            { action = "policy", revision = added.State.Revision, policy = (int)AiBackendPolicy.Managed }), default);
        Assert.True(next.Ok);
    }

    private static string Action(string action, PersonalApiView state, PersonalKeyView row) => JsonSerializer.Serialize(new
        { action, revision = state.Revision, id = row.Id, keyRevision = row.Revision });
    private static PersonalGeminiKeyValidator Validator(int status = 200) => new(new Handler((_, _) => Reply(status)));
    private static Task<HttpResponseMessage> Reply(int status) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent(JsonSerializer.Serialize(new { name = "models/" + PersonalGeminiModels.Text,
            supportedGenerationMethods = new[] { "generateContent" } })) });
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class Entry : IPersonalKeyEntryDialog
    {
        public string Secret = Canary;
        public bool Cancel, ConfirmReset;
        public int Calls;
        public PersonalKeyEntry Last;
        public Task<PersonalKeyEntry> ShowAsync(bool replacing, CancellationToken token)
        {
            Calls++;
            Last = Cancel ? null : new PersonalKeyEntry("Native fixture", Secret);
            return Task.FromResult(Last);
        }
        public Task<bool> ConfirmResetAsync(CancellationToken token) => Task.FromResult(ConfirmReset);
        public bool RevealPending() => false;
    }

    private sealed class PendingEntry : IPersonalKeyEntryDialog
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls, Reveals;
        private bool _pending;

        public async Task<PersonalKeyEntry> ShowAsync(bool replacing, CancellationToken token)
        {
            Calls++;
            if (Calls > 1) return null;
            _pending = true;
            Entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { _pending = false; }
            return null;
        }

        public Task<bool> ConfirmResetAsync(CancellationToken token) => Task.FromResult(false);
        public bool RevealPending()
        {
            if (!_pending) return false;
            Reveals++;
            return true;
        }
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
