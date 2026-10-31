namespace KeyboardWtf.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Qloo;
using KeyboardWtf.Services.Taste;
using Xunit;

// All payloads below are SYNTHETIC fixtures shaped like the documented Qloo responses
// (docs/qloo/API_EXAMPLES.md). No real Qloo response data is stored in this repository.
public sealed class QlooTasteTests : IDisposable
{
    private const string FakeKey = "test_key_0123456789abcdef";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qloo-tests-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    public QlooTasteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---------------- Qloo client ----------------

    [Fact]
    public async Task Search_uses_hackathon_base_url_header_auth_and_documented_parameters()
    {
        var handler = new FakeQloo();
        using var client = Client(handler);
        var results = await client.SearchAsync("Interstellar", "urn:entity:movie", 1, new QlooCallBudget(8), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("hackathon.api.qloo.com", request.Uri.Host);
        Assert.Equal("/search", request.Uri.AbsolutePath);
        Assert.Equal(FakeKey, request.ApiKey);
        Assert.DoesNotContain(FakeKey, request.Uri.ToString());
        Assert.Contains("query=Interstellar", request.Uri.Query);
        Assert.Contains("types=urn:entity:movie", request.Uri.Query);
        Assert.Contains("take=1", request.Uri.Query);
        Assert.Equal("ent-interstellar", Assert.Single(results).Id);
    }

    [Fact]
    public async Task Insights_candidate_scoring_parses_affinity_and_explainability_and_is_cached()
    {
        var handler = new FakeQloo();
        using var client = Client(handler);
        var parameters = new Dictionary<string, string>
        {
            ["filter.type"] = "urn:entity:movie",
            ["signal.interests.entities"] = "ent-interstellar",
            ["filter.results.entities"] = "ent-inception,ent-mammamia",
            ["take"] = "2",
            ["feature.explainability"] = "true",
        };
        var first = await client.InsightsAsync(parameters, new QlooCallBudget(8), CancellationToken.None);
        var budget = new QlooCallBudget(8);
        var second = await client.InsightsAsync(parameters, budget, CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal(1, budget.CacheHits);
        Assert.Equal(0, budget.Used);
        var inception = first.Single(e => e.Id == "ent-inception");
        Assert.Equal(0.89, inception.Affinity.Value, 3);
        Assert.Contains(inception.Explainability, x => x.EntityId == "ent-interstellar");
        Assert.Equal(2, second.Count);
        Assert.Contains("filter.results.entities=ent-inception,ent-mammamia", handler.Requests[0].Uri.Query);
    }

    [Fact]
    public async Task Concurrent_identical_requests_share_one_http_call()
    {
        var handler = new FakeQloo { Delay = TimeSpan.FromMilliseconds(150) };
        using var client = Client(handler);
        var a = client.SearchAsync("Arrival", "urn:entity:movie", 1, new QlooCallBudget(8), CancellationToken.None);
        var b = client.SearchAsync("Arrival", "urn:entity:movie", 1, new QlooCallBudget(8), CancellationToken.None);
        await Task.WhenAll(a, b);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Transient_503_is_retried_once_then_succeeds()
    {
        var handler = new FakeQloo { FailNext = 1, FailStatus = HttpStatusCode.ServiceUnavailable };
        using var client = Client(handler);
        var results = await client.SearchAsync("Arrival", "urn:entity:movie", 1, new QlooCallBudget(8), CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(results);
    }

    [Fact]
    public async Task Unauthorized_is_not_retried_and_locks_out_until_key_changes()
    {
        var handler = new FakeQloo { FailNext = 5, FailStatus = HttpStatusCode.Unauthorized };
        using var client = Client(handler);
        var error = await Assert.ThrowsAsync<QlooException>(() =>
            client.SearchAsync("Arrival", "urn:entity:movie", 1, new QlooCallBudget(8), CancellationToken.None));
        Assert.Equal("unauthorized", error.Code);
        Assert.Single(handler.Requests);
        Assert.True(client.IsLockedOut);
        await Assert.ThrowsAsync<QlooException>(() =>
            client.SearchAsync("Dune", "urn:entity:movie", 1, new QlooCallBudget(8), CancellationToken.None));
        Assert.Single(handler.Requests); // no network while locked out
    }

    [Fact]
    public async Task Per_decision_cap_and_daily_budget_stop_network_calls()
    {
        var handler = new FakeQloo();
        using var client = Client(handler, dailyBudget: 2);
        var budget = new QlooCallBudget(1);
        await client.SearchAsync("Arrival", "urn:entity:movie", 1, budget, CancellationToken.None);
        var cap = await Assert.ThrowsAsync<QlooException>(() =>
            client.SearchAsync("Dune", "urn:entity:movie", 1, budget, CancellationToken.None));
        Assert.Equal("request_cap", cap.Code);

        await client.SearchAsync("Dune", "urn:entity:movie", 1, new QlooCallBudget(8), CancellationToken.None);
        var daily = await Assert.ThrowsAsync<QlooException>(() =>
            client.SearchAsync("Moon", "urn:entity:movie", 1, new QlooCallBudget(8), CancellationToken.None));
        Assert.Equal("budget_exhausted", daily.Code);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, client.Usage().Requests);
    }

    [Theory]
    [InlineData("urn:entity:music", "signal.interests.entities", "x")]       // unsupported type
    [InlineData("urn:entity:destination", "signal.interests.tags", "t")]      // destination needs entity signal
    [InlineData("urn:entity:movie", "filter.location.query", "Paris")]        // location filter is for places
    [InlineData("urn:entity:movie", "signal.unknown", "x")]                   // undocumented parameter
    public void Insights_parameter_validation_rejects_invalid_combinations(string type, string name, string value)
    {
        var error = Assert.Throws<QlooException>(() => QlooClient.ValidateInsightsParameters(
            new Dictionary<string, string> { ["filter.type"] = type, [name] = value }));
        Assert.Equal("invalid_parameters", error.Code);
    }

    [Fact]
    public async Task Missing_key_makes_no_network_call()
    {
        var handler = new FakeQloo();
        using var client = new QlooClient(new QlooCredentialStore(Path.Combine(_dir, "none.dat"), () => null), _dir, handler,
            clock: () => _now, delay: (_, _) => Task.CompletedTask);
        var error = await Assert.ThrowsAsync<QlooException>(() =>
            client.SearchAsync("Arrival", "urn:entity:movie", 1, new QlooCallBudget(8), CancellationToken.None));
        Assert.Equal("missing_key", error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Credential_store_round_trips_with_dpapi_and_never_exposes_the_key_in_the_mask()
    {
        var store = new QlooCredentialStore(Path.Combine(_dir, "qloo.dat"), () => null);
        Assert.False(store.IsConfigured);
        store.Save(FakeKey);
        var reopened = new QlooCredentialStore(Path.Combine(_dir, "qloo.dat"), () => null);
        Assert.Equal(FakeKey, reopened.ReadKey());
        Assert.Equal("profile", reopened.Source);
        Assert.Equal("••••" + FakeKey[^4..], reopened.Mask());
        Assert.DoesNotContain(FakeKey, File.ReadAllText(Path.Combine(_dir, "qloo.dat")));
        Assert.Equal(FakeKey, QlooCredentialStore.ExtractKey("Qloo key: " + FakeKey + "\r\n"));
    }

    // ---------------- taste memory ----------------

    [Fact]
    public void Explicit_preferences_are_strong_and_inferred_ones_grow_with_repeated_evidence()
    {
        var store = Store();
        var explicitLike = store.Observe(Obs("fashion", "colour", "navy", source: "explicit_statement"));
        Assert.Equal("strong", explicitLike.Preference.Strength);

        var first = store.Observe(Obs("fashion", "style", "minimalist", source: "choice", decision: "d1"));
        var second = store.Observe(Obs("fashion", "style", "minimalist", source: "choice", decision: "d2"));
        var third = store.Observe(Obs("fashion", "style", "minimalist", source: "choice", decision: "d3"));
        Assert.True(second.Preference.Confidence > first.Preference.Confidence);
        Assert.True(third.Preference.Confidence > second.Preference.Confidence);
        Assert.Equal("inferred", third.Preference.Kind);

        var mention = store.Observe(Obs("music", "genre", "jazz", source: "inferred_mention"));
        Assert.Equal("tentative", mention.Preference.Strength);
    }

    [Fact]
    public void Contradiction_halves_old_evidence_instead_of_overwriting_and_correction_is_authoritative()
    {
        var store = Store();
        store.Observe(Obs("dining", "cuisine", "sushi", source: "explicit_statement", quote: "I love sushi"));
        var changed = store.Observe(Obs("dining", "cuisine", "sushi", polarity: -1, source: "explicit_statement", quote: "I'm off sushi now"));
        Assert.Equal(-1, changed.Preference.Polarity);
        Assert.True(changed.Preference.RecentlyChanged);
        Assert.True(changed.Preference.Confidence < 0.6); // weaker than a clean explicit dislike
        Assert.Equal(2, changed.Preference.Evidence);

        var corrected = store.Correct(changed.Preference.Id, polarity: 1);
        Assert.Equal(1, corrected.Polarity);
        Assert.Equal("strong", corrected.Strength);
    }

    [Fact]
    public void Contextual_and_temporary_preferences_never_overwrite_general_taste()
    {
        var store = Store();
        store.Observe(Obs("fashion", "style", "casual", source: "explicit_statement"));
        store.Observe(Obs("fashion", "style", "formal", source: "explicit_statement", context: "wedding"));
        store.Observe(new TasteObservationInput { Domain = "movies", Attribute = "genre", Value = "comedy", Source = "explicit_statement", Temporary = true, TemporaryDays = 2 });

        var everyday = store.Relevant("fashion", "");
        Assert.Contains(everyday, p => p.Value == "casual");
        Assert.DoesNotContain(everyday, p => p.Value == "formal");
        var wedding = store.Relevant("fashion", "my cousin's wedding");
        Assert.Contains(wedding, p => p.Value == "formal" && p.Kind == "contextual");
        Assert.Contains(wedding, p => p.Value == "casual");

        Assert.Contains(store.Relevant("movies", ""), p => p.Value == "comedy" && p.Kind == "temporary");
        _now = _now.AddDays(3);
        Assert.DoesNotContain(store.Relevant("movies", ""), p => p.Value == "comedy");
    }

    [Fact]
    public void Duplicate_observations_and_sensitive_traits_are_not_stored()
    {
        var store = Store();
        var a = store.Observe(Obs("music", "artist", "Radiohead", source: "explicit_statement", quote: "I like Radiohead"));
        var b = store.Observe(Obs("music", "artist", "Radiohead", source: "explicit_statement", quote: "I like Radiohead"));
        Assert.True(a.Stored);
        Assert.True(b.Duplicate);
        Assert.Equal(1, store.Snapshot().Single().Evidence);

        var refused = store.Observe(Obs("general", "religion", "a faith-based diet", source: "explicit_statement"));
        Assert.NotNull(refused.Refused);
        Assert.DoesNotContain(store.Snapshot(), p => p.Attribute == "religion");
    }

    [Fact]
    public void Taste_memory_persists_across_restarts()
    {
        var path = Path.Combine(_dir, "taste.json");
        var store = new TasteProfileStore(path, () => _now);
        store.Observe(Obs("movies", "entity", "Interstellar", source: "explicit_statement"));
        var restarted = new TasteProfileStore(path, () => _now);
        restarted.Load();
        Assert.Contains(restarted.Snapshot(), p => p.Value == "Interstellar" && p.Strength == "strong");
        Assert.Contains("Interstellar", restarted.BuildPromptDigest());
        Assert.True(restarted.Forget("Interstellar"));
        var again = new TasteProfileStore(path, () => _now);
        again.Load();
        Assert.Empty(again.Snapshot());
    }

    // ---------------- decision engine ----------------

    [Fact]
    public async Task Technical_questions_make_no_qloo_calls()
    {
        var handler = new FakeQloo();
        var (engine, _) = Engine(handler);
        var brief = await engine.DecideAsync(new TasteDecisionRequest
        {
            Question = "Which sorting algorithm should I use for nearly sorted data?",
            Domain = "technology",
            Options = { new TasteOptionInput { Label = "Insertion sort" }, new TasteOptionInput { Label = "Quicksort" } },
        }, CancellationToken.None);
        Assert.Empty(handler.Requests);
        Assert.False(Qloo(brief)["grounded"] is true);
        Assert.False(Qloo(brief)["relevant"] is true);
    }

    [Fact]
    public async Task Qloo_scores_the_users_own_options_and_changes_a_generic_tie()
    {
        var handler = new FakeQloo();
        var (engine, store) = Engine(handler);
        store.Observe(Obs("movies", "entity", "Interstellar", source: "explicit_statement"));
        var brief = await engine.DecideAsync(MovieChoice(), CancellationToken.None);

        Assert.True(Qloo(brief)["grounded"] is true);
        var ranking = (Dictionary<string, object>[])brief["ranking"];
        Assert.Equal("Inception", ranking[0]["label"]);
        Assert.Equal(0.89, (double)ranking[0]["qloo_affinity"], 3);
        Assert.Contains(handler.Requests, r => r.Uri.AbsolutePath == "/v2/insights"
            && r.Uri.Query.Contains("filter.results.entities=ent-inception,ent-mammamia")
            && r.Uri.Query.Contains("signal.interests.entities=ent-interstellar"));
        Assert.True((int)Qloo(brief)["calls"] <= TasteDecisionEngine.MaxQlooCallsPerDecision);
    }

    [Fact]
    public async Task Qloo_outage_falls_back_to_taste_memory_and_is_flagged()
    {
        var handler = new FakeQloo { FailNext = 99, FailStatus = HttpStatusCode.ServiceUnavailable };
        var (engine, store) = Engine(handler);
        store.Observe(Obs("movies", "entity", "Interstellar", source: "explicit_statement"));
        store.Observe(Obs("movies", "genre", "sci-fi", source: "explicit_statement"));
        var request = MovieChoice();
        request.Options[0].Attributes.Add("genre:sci-fi");
        var brief = await engine.DecideAsync(request, CancellationToken.None);

        Assert.False(Qloo(brief)["grounded"] is true);
        Assert.Contains("NOT Qloo-grounded", string.Join(" ", (string[])brief["caveats"]));
        var ranking = (Dictionary<string, object>[])brief["ranking"];
        Assert.Equal("Inception", ranking[0]["label"]); // still decided from taste memory
        Assert.Null(ranking[0]["qloo_affinity"]);
    }

    [Fact]
    public async Task Missing_key_still_decides_with_a_setup_message()
    {
        var handler = new FakeQloo();
        var store = Store();
        using var client = new QlooClient(new QlooCredentialStore(Path.Combine(_dir, "none.dat"), () => null), _dir, handler,
            clock: () => _now, delay: (_, _) => Task.CompletedTask);
        var engine = new TasteDecisionEngine(store, client, () => true);
        var brief = await engine.DecideAsync(MovieChoice(), CancellationToken.None);
        Assert.Empty(handler.Requests);
        Assert.Contains("No Qloo API key", (string)Qloo(brief)["reason"]);
    }

    [Fact]
    public async Task Feedback_on_a_choice_changes_the_next_decision()
    {
        var handler = new FakeQloo();
        var (_, store) = Engine(handler);
        var tools = new TasteJarvisTools(store, null, () => true);
        var shirts = new TasteDecisionRequest
        {
            Question = "Which shirt?",
            Domain = "fashion",
            Options =
            {
                new TasteOptionInput { Label = "A", Attributes = { "colour:olive", "style:minimalist" } },
                new TasteOptionInput { Label = "B", Attributes = { "colour:neon", "style:graphic" } },
            },
        };
        var before = await new TasteDecisionEngine(store, null, () => false).DecideAsync(shirts, CancellationToken.None);
        Assert.Equal("low", before["confidence"]);
        var decisionId = (string)before["decision_id"];

        var feedback = await tools.ExecuteAsync("taste_feedback", JsonSerializer.SerializeToElement(new
        {
            decision_id = decisionId,
            chosen_label = "A",
            rejected_labels = new[] { "B" },
        }), CancellationToken.None);
        Assert.True(feedback["ok"] is true);

        var after = await new TasteDecisionEngine(store, null, () => false).DecideAsync(shirts, CancellationToken.None);
        var ranking = (Dictionary<string, object>[])after["ranking"];
        Assert.Equal("A", ranking[0]["label"]);
        Assert.True((double)ranking[0]["engine_score"] > (double)ranking[1]["engine_score"]);
        Assert.Contains(store.Snapshot(), p => p.Value == "neon" && p.Polarity < 0);
    }

    [Fact]
    public async Task Disabled_toggle_refuses_taste_tools()
    {
        var tools = new TasteJarvisTools(Store(), null, () => false);
        var result = await tools.ExecuteAsync("taste_decide", JsonSerializer.SerializeToElement(new { question = "x", domain = "movies" }), CancellationToken.None);
        Assert.False(result["ok"] is true);
    }

    [Fact]
    public void Qloo_off_tool_list_is_the_original_and_qloo_on_adds_only_taste_tools()
    {
        var original = JsonSerializer.Serialize(GeminiLiveConversationService.BuildToolsWith(null));
        var empty = JsonSerializer.Serialize(GeminiLiveConversationService.BuildToolsWith(Array.Empty<object>()));
        Assert.Equal(original, empty);
        Assert.DoesNotContain("taste_", original);
        Assert.Contains("end_conversation", original);

        var tools = new TasteJarvisTools(Store(), null, () => true);
        var enhanced = JsonSerializer.Serialize(GeminiLiveConversationService.BuildToolsWith(tools.FunctionDeclarations()));
        Assert.StartsWith(original[..^3], enhanced); // original declarations unchanged and first
        foreach (var name in new[] { "taste_decide", "taste_observe", "taste_feedback", "taste_profile" })
            Assert.Contains($"\"name\":\"{name}\"", enhanced);
    }

    // ---------------- Gemini key pool import ----------------

    [Fact]
    public async Task Gemini_key_import_keeps_generating_keys_active_and_others_inactive()
    {
        var vault = new KeyboardWtf.Services.Ai.Backends.PersonalApiVault(Path.Combine(_dir, "personal-api-v1.dat"),
            defaultPolicy: KeyboardWtf.Services.Ai.Backends.AiBackendPolicy.PersonalOnly);
        var keys = Enumerable.Range(1, 12).Select(i => $"AQ.synthetic-key-{i:00}-0123456789abcdef").ToList();
        keys.Insert(3, keys[0]); // duplicate line
        var summary = await KeyboardWtf.Services.Ai.Backends.PersonalKeyImporter.ImportAsync(vault, keys,
            (key, _) => Task.FromResult(key.Contains("-02-") || key.Contains("-05-")
                ? KeyboardWtf.Services.Ai.Backends.PersonalKeyValidation.CapabilityUnavailable
                : KeyboardWtf.Services.Ai.Backends.PersonalKeyValidation.AuthenticationValidated));

        var view = vault.GetView();
        Assert.Equal(10, view.Keys.Count);                 // pool maximum
        Assert.Equal(10, summary.Added);
        Assert.Equal(8, summary.Active);
        Assert.Equal(2, summary.Inactive);
        Assert.Equal(8, view.Keys.Count(k => k.Enabled && k.AuthenticationValidated));
        Assert.All(view.Keys, k => Assert.DoesNotContain("synthetic", k.Mask));
    }

    // ---------------- helpers ----------------

    private QlooClient Client(FakeQloo handler, int dailyBudget = 150) =>
        new(new QlooCredentialStore(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".dat"), () => FakeKey),
            Path.Combine(_dir, Guid.NewGuid().ToString("N")), handler, clock: () => _now, dailyBudget: dailyBudget,
            delay: (_, _) => Task.CompletedTask);

    private TasteProfileStore Store() => new(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json"), () => _now);

    private (TasteDecisionEngine Engine, TasteProfileStore Store) Engine(FakeQloo handler)
    {
        var store = Store();
        var client = Client(handler);
        return (new TasteDecisionEngine(store, client, () => true), store);
    }

    private static TasteDecisionRequest MovieChoice() => new()
    {
        Question = "Inception or Mamma Mia tonight?",
        Domain = "movies",
        Options =
        {
            new TasteOptionInput { Label = "Inception", EntityName = "Inception" },
            new TasteOptionInput { Label = "Mamma Mia!", EntityName = "Mamma Mia!" },
        },
    };

    private static Dictionary<string, object> Qloo(Dictionary<string, object> brief) => (Dictionary<string, object>)brief["qloo"];

    private static TasteObservationInput Obs(string domain, string attribute, string value, int polarity = 1,
        string source = "explicit_statement", string context = null, string quote = null, string decision = null) => new()
    {
        Domain = domain,
        Attribute = attribute,
        Value = value,
        Polarity = polarity,
        Source = source,
        Context = context,
        Quote = quote,
        DecisionId = decision,
    };

    private sealed class FakeQloo : HttpMessageHandler
    {
        internal readonly List<(Uri Uri, string ApiKey)> Requests = new();
        internal int FailNext;
        internal HttpStatusCode FailStatus = HttpStatusCode.ServiceUnavailable;
        internal TimeSpan Delay = TimeSpan.Zero;

        private static readonly Dictionary<string, (string Id, string Name)> Entities = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Interstellar"] = ("ent-interstellar", "Interstellar"),
            ["Inception"] = ("ent-inception", "Inception"),
            ["Mamma Mia!"] = ("ent-mammamia", "Mamma Mia!"),
            ["Arrival"] = ("ent-arrival", "Arrival"),
            ["Dune"] = ("ent-dune", "Dune"),
            ["Moon"] = ("ent-moon", "Moon"),
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            lock (Requests)
                Requests.Add((request.RequestUri, request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.Single() : null));
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, token);
            if (FailNext > 0)
            {
                FailNext--;
                return new HttpResponseMessage(FailStatus) { Content = new StringContent("{}") };
            }
            var query = ParseQuery(request.RequestUri.Query);
            string body = request.RequestUri.AbsolutePath switch
            {
                "/search" => SearchBody(query.GetValueOrDefault("query")),
                "/v2/tags" => "{\"success\":true,\"results\":{\"tags\":[]}}",
                "/v2/insights" => InsightsBody(query),
                _ => "{}",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        private static string SearchBody(string text) =>
            text is not null && Entities.TryGetValue(text, out var e)
                ? JsonSerializer.Serialize(new { results = new[] { new { name = e.Name, entity_id = e.Id, types = new[] { "urn:entity:movie" }, popularity = 0.9 } } })
                : "{\"results\":[]}";

        private static string InsightsBody(Dictionary<string, string> query)
        {
            var ids = (query.GetValueOrDefault("filter.results.entities") ?? "ent-arrival,ent-dune").Split(',');
            var signal = (query.GetValueOrDefault("signal.interests.entities") ?? "").Split(',').FirstOrDefault() ?? "";
            var entities = ids.Select(id => new
            {
                name = Entities.Values.FirstOrDefault(v => v.Id == id).Name ?? id,
                entity_id = id,
                subtype = "urn:entity:movie",
                popularity = 0.9,
                query = new
                {
                    affinity = id == "ent-inception" ? 0.89 : id == "ent-mammamia" ? 0.69 : 0.8,
                    explainability = new Dictionary<string, object>
                    {
                        ["signal.interests.entities"] = new[] { new { entity_id = signal, score = 0.6 } },
                    },
                },
            }).ToArray();
            return JsonSerializer.Serialize(new { success = true, results = new { entities } });
        }

        private static Dictionary<string, string> ParseQuery(string query) =>
            query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");
    }
}
