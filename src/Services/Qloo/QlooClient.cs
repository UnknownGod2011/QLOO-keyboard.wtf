namespace KeyboardWtf.Services.Qloo;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed record QlooEntity(
    string Id,
    string Name,
    string Type,
    double? Popularity,
    double? Affinity,
    string Disambiguation,
    string Description,
    IReadOnlyList<string> TagNames,
    IReadOnlyList<(string EntityId, double Score)> Explainability);

internal sealed record QlooTag(string Id, string Name, string Type, IReadOnlyList<string> ParentTypes);

internal sealed class QlooException : Exception
{
    internal QlooException(string code, string message, int? status = null) : base(message)
    {
        Code = code;
        Status = status;
    }

    /// <summary>missing_key, disabled, unauthorized, unsupported_type, bad_request, rate_limited,
    /// unavailable, budget_exhausted, request_cap, invalid_parameters.</summary>
    internal string Code { get; }
    internal int? Status { get; }
}

/// <summary>Per-decision call cap. Cache hits do not consume it.</summary>
internal sealed class QlooCallBudget
{
    private int _used;
    internal QlooCallBudget(int max) => Max = Math.Max(0, max);
    internal int Max { get; }
    internal int Used => Volatile.Read(ref _used);
    internal int Remaining => Math.Max(0, Max - Used);
    internal int CacheHits;
    internal readonly List<string> Log = new();

    internal bool TryConsume()
    {
        while (true)
        {
            var used = Volatile.Read(ref _used);
            if (used >= Max) return false;
            if (Interlocked.CompareExchange(ref _used, used + 1, used) == used) return true;
        }
    }
}

internal sealed class QlooUsageSnapshot
{
    public string Day { get; set; } = "";
    public int Requests { get; set; }
    public int CacheHits { get; set; }
    public int Failures { get; set; }
    public int DailyBudget { get; set; }
    public string LastError { get; set; } = "";
    public DateTimeOffset? LastRequestUtc { get; set; }
}

/// <summary>
/// Minimal, documented Qloo client (hackathon base URL, X-Api-Key header, GET + query string).
/// Endpoints: /search, /v2/tags, /v2/insights. See docs/qloo/INSIGHTS_API.md.
/// Quota protection: private on-disk cache, in-flight de-duplication, per-decision cap,
/// persisted daily budget, 8 s timeout, one retry only for 429/5xx/timeout, 401 lockout.
/// </summary>
internal sealed class QlooClient : IDisposable
{
    internal static readonly Uri DefaultBaseUrl = new("https://hackathon.api.qloo.com/");
    internal static readonly IReadOnlySet<string> SupportedTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "urn:entity:artist", "urn:entity:book", "urn:entity:brand", "urn:entity:destination", "urn:entity:movie",
        "urn:entity:person", "urn:entity:place", "urn:entity:podcast", "urn:entity:tv_show", "urn:entity:video_game",
    };

    // Insights parameters this client may send (a subset of the Entity Type Parameter Guide).
    private static readonly IReadOnlySet<string> InsightsParameters = new HashSet<string>(StringComparer.Ordinal)
    {
        "filter.type", "signal.interests.entities", "signal.interests.tags", "filter.results.entities",
        "filter.exclude.entities", "filter.location.query", "take", "page", "feature.explainability",
    };

    internal static readonly TimeSpan SearchTtl = TimeSpan.FromDays(30);
    internal static readonly TimeSpan TagsTtl = TimeSpan.FromDays(30);
    internal static readonly TimeSpan InsightsTtl = TimeSpan.FromDays(7);
    private const int MaxCacheEntries = 600;
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = false };
    private readonly QlooCredentialStore _credentials;
    private readonly HttpClient _http;
    private readonly Uri _baseUrl;
    private readonly TimeSpan _timeout;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly string _cachePath;
    private readonly string _usagePath;
    private readonly object _cacheGate = new();
    private readonly object _usageGate = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inFlight = new(StringComparer.Ordinal);
    private Dictionary<string, CacheEntry> _cache;
    private QlooUsageSnapshot _usage;
    private long _unauthorizedRevision = -1;

    internal QlooClient(
        QlooCredentialStore credentials,
        string dataDirectory = null,
        HttpMessageHandler handler = null,
        TimeSpan? timeout = null,
        Func<DateTimeOffset> clock = null,
        int dailyBudget = 150,
        Uri baseUrl = null,
        Func<TimeSpan, CancellationToken, Task> delay = null)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _baseUrl = baseUrl ?? BaseUrlFromEnvironment() ?? DefaultBaseUrl;
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        DailyBudget = Math.Clamp(dailyBudget, 1, 5000);
        var directory = string.IsNullOrWhiteSpace(dataDirectory) ? SettingsService.AppDataDir : dataDirectory;
        _cachePath = Path.Combine(directory, "qloo-cache.json");
        _usagePath = Path.Combine(directory, "qloo-usage.json");
    }

    internal int DailyBudget { get; set; }

    internal QlooCredentialStore Credentials => _credentials;

    internal bool IsConfigured => _credentials.IsConfigured;

    /// <summary>True after a 401 for the current key; cleared when the key changes.</summary>
    internal bool IsLockedOut => Interlocked.Read(ref _unauthorizedRevision) == _credentials.Revision;

    internal QlooUsageSnapshot Usage()
    {
        lock (_usageGate)
        {
            var usage = LoadUsageLocked();
            return new QlooUsageSnapshot
            {
                Day = usage.Day,
                Requests = usage.Requests,
                CacheHits = usage.CacheHits,
                Failures = usage.Failures,
                DailyBudget = DailyBudget,
                LastError = usage.LastError,
                LastRequestUtc = usage.LastRequestUtc,
            };
        }
    }

    internal async Task<IReadOnlyList<QlooEntity>> SearchAsync(
        string query, string type, int take, QlooCallBudget budget, CancellationToken token)
    {
        var text = (query ?? "").Trim();
        if (text.Length is 0 or > 120)
            throw new QlooException("invalid_parameters", "Search text must be 1-120 characters.");
        if (!SupportedTypes.Contains(type ?? ""))
            throw new QlooException("unsupported_type", $"Unsupported Qloo entity type: {type}.");
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["query"] = text,
            ["take"] = Math.Clamp(take, 1, 10).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["types"] = type,
        };
        var body = await GetAsync("search", parameters, SearchTtl, budget, token, notFoundIsEmpty: true);
        if (body is null) return Array.Empty<QlooEntity>();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var results = root.TryGetProperty("results", out var r) ? r : default;
        if (results.ValueKind == JsonValueKind.Object && results.TryGetProperty("entities", out var nested))
            results = nested;
        return results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Select(ParseEntity).Where(e => e is not null).ToArray()
            : Array.Empty<QlooEntity>();
    }

    internal async Task<IReadOnlyList<QlooTag>> SearchTagsAsync(
        string query, int take, QlooCallBudget budget, CancellationToken token)
    {
        var text = (query ?? "").Trim();
        if (text.Length is 0 or > 60)
            throw new QlooException("invalid_parameters", "Tag search text must be 1-60 characters.");
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["filter.query"] = text,
            ["take"] = Math.Clamp(take, 1, 50).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var body = await GetAsync("v2/tags", parameters, TagsTtl, budget, token, notFoundIsEmpty: true);
        if (body is null) return Array.Empty<QlooTag>();
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("results", out var results)
            || !results.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
            return Array.Empty<QlooTag>();
        var list = new List<QlooTag>();
        foreach (var tag in tags.EnumerateArray())
        {
            var id = Str(tag, "id") ?? Str(tag, "tag_id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var parents = tag.TryGetProperty("parents", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray().Select(x => Str(x, "type")).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                : Array.Empty<string>();
            list.Add(new QlooTag(id, Str(tag, "name") ?? id, Str(tag, "type") ?? Str(tag, "subtype") ?? "", parents));
        }
        return list;
    }

    internal async Task<IReadOnlyList<QlooEntity>> InsightsAsync(
        IReadOnlyDictionary<string, string> parameters, QlooCallBudget budget, CancellationToken token)
    {
        var validated = ValidateInsightsParameters(parameters);
        var body = await GetAsync("v2/insights", validated, InsightsTtl, budget, token, notFoundIsEmpty: false);
        if (body is null) return Array.Empty<QlooEntity>();
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("results", out var results)
            || !results.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Array)
            return Array.Empty<QlooEntity>();
        return entities.EnumerateArray().Select(ParseEntity).Where(e => e is not null).ToArray();
    }

    /// <summary>Rejects undocumented/unsupported combinations before any network call.</summary>
    internal static SortedDictionary<string, string> ValidateInsightsParameters(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters is null || !parameters.TryGetValue("filter.type", out var type) || !SupportedTypes.Contains(type ?? ""))
            throw new QlooException("invalid_parameters", "filter.type must be one of the supported hackathon entity types.");
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, raw) in parameters)
        {
            if (!InsightsParameters.Contains(name))
                throw new QlooException("invalid_parameters", $"Parameter {name} is not used by this client.");
            var value = (raw ?? "").Trim();
            if (value.Length == 0) continue;
            if (name == "take" && (!int.TryParse(value, out var take) || take is < 1 or > 50))
                throw new QlooException("invalid_parameters", "take must be 1-50.");
            if (name == "page" && (!int.TryParse(value, out var page) || page is < 1 or > 100))
                throw new QlooException("invalid_parameters", "page must be 1-100.");
            if (name == "filter.location.query" && type is not ("urn:entity:place" or "urn:entity:destination"))
                throw new QlooException("invalid_parameters", "filter.location.query applies to places and destinations only.");
            result[name] = value;
        }
        if (type == "urn:entity:destination" && !result.ContainsKey("signal.interests.entities"))
            throw new QlooException("invalid_parameters", "Destination insights require signal.interests.entities.");
        if (!result.Keys.Any(k => k.StartsWith("signal.", StringComparison.Ordinal) || k is "filter.results.entities" or "filter.location.query"))
            throw new QlooException("invalid_parameters", "At least one signal or filter is required.");
        return result;
    }

    internal static string BuildQueryString(IEnumerable<KeyValuePair<string, string>> parameters) =>
        string.Join("&", parameters
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => Uri.EscapeDataString(p.Key) + "=" + EscapeValue(p.Value)));

    // Comma-separated ID lists and URNs stay readable; everything else is escaped.
    private static string EscapeValue(string value) =>
        Uri.EscapeDataString(value ?? "").Replace("%2C", ",").Replace("%3A", ":");

    private async Task<string> GetAsync(
        string path,
        IDictionary<string, string> parameters,
        TimeSpan ttl,
        QlooCallBudget budget,
        CancellationToken token,
        bool notFoundIsEmpty)
    {
        var relative = path + "?" + BuildQueryString(parameters);
        if (TryGetCached(relative, out var cached))
        {
            if (budget is not null) Interlocked.Increment(ref budget.CacheHits);
            budget?.Log.Add("cache " + path);
            RecordUsage(cacheHit: true);
            return cached;
        }

        var key = _credentials.ReadKey();
        if (key is null)
            throw new QlooException("missing_key", "No Qloo API key is configured. Add one in QLOO Settings > Qloo cultural intelligence.");
        if (IsLockedOut)
            throw new QlooException("unauthorized", "Qloo rejected the configured key (401). Re-enter the key in QLOO Settings.");

        var lazy = _inFlight.GetOrAdd(relative, _ => new Lazy<Task<string>>(
            () => SendWithPolicyAsync(relative, key, ttl, budget, token, notFoundIsEmpty)));
        try
        {
            return await lazy.Value.WaitAsync(token);
        }
        finally
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompleted)
                _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(relative, lazy));
        }
    }

    private async Task<string> SendWithPolicyAsync(
        string relative, string key, TimeSpan ttl, QlooCallBudget budget, CancellationToken token, bool notFoundIsEmpty)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                if (budget is not null && !budget.TryConsume())
                    throw new QlooException("request_cap", "Per-decision Qloo call cap reached.");
                if (!TryReserveDaily())
                    throw new QlooException("budget_exhausted", "Today's Qloo request budget is used up; results use your taste memory only.");
                budget?.Log.Add("GET /" + relative.Split('?')[0]);
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                HttpStatusCode? status = null;
                TimeSpan? retryAfter = null;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(_timeout);
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUrl, relative));
                    request.Headers.Add("X-Api-Key", key);
                    request.Headers.Accept.ParseAdd("application/json");
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    status = response.StatusCode;
                    retryAfter = response.Headers.RetryAfter?.Delta;
                    var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    AppLog.Info($"Qloo GET /{relative.Split('?')[0]} status={(int)response.StatusCode} ms={elapsed} attempt={attempt + 1}");
                    if (response.IsSuccessStatusCode)
                    {
                        var body = await ReadBoundedAsync(response.Content, deadline.Token);
                        StoreCached(relative, body, ttl);
                        return body;
                    }
                    if (response.StatusCode == HttpStatusCode.NotFound && notFoundIsEmpty)
                        return null;
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        Interlocked.Exchange(ref _unauthorizedRevision, _credentials.Revision);
                        throw Fail("unauthorized", "Qloo rejected the key (401). Check that it is a hackathon key for https://hackathon.api.qloo.com.", 401);
                    }
                    if (response.StatusCode == HttpStatusCode.Forbidden)
                        throw Fail("unsupported_type", "Qloo refused this entity type (403).", 403);
                    if (response.StatusCode == HttpStatusCode.BadRequest)
                        throw Fail("bad_request", "Qloo rejected the request parameters (400).", 400);
                    if ((int)response.StatusCode != 429 && (int)response.StatusCode < 500)
                        throw Fail("bad_request", $"Qloo returned HTTP {(int)response.StatusCode}.", (int)response.StatusCode);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    status = null; // timeout: retryable below
                    AppLog.Warning($"Qloo GET /{relative.Split('?')[0]} timed out after {_timeout.TotalSeconds:0} s");
                }
                catch (HttpRequestException)
                {
                    status = null; // network: retryable below
                }

                // Only 429, 5xx, timeouts and network errors reach here.
                if (attempt >= 1)
                {
                    var code = status == (HttpStatusCode)429 ? "rate_limited" : "unavailable";
                    throw Fail(code, status is null
                        ? "Qloo did not respond in time."
                        : $"Qloo is temporarily unavailable (HTTP {(int)status}).", status is null ? null : (int)status);
                }
                var wait = retryAfter is { } ra && ra > TimeSpan.Zero && ra <= TimeSpan.FromSeconds(3)
                    ? ra
                    : TimeSpan.FromMilliseconds(600 + Random.Shared.Next(0, 300));
                await _delay(wait, token);
            }
        }
        catch (QlooException ex) when (ex.Code is not ("request_cap" or "budget_exhausted"))
        {
            RecordFailure(ex.Code);
            throw;
        }
    }

    private QlooException Fail(string code, string message, int? status) => new(code, message, status);

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxResponseBytes)
                throw new QlooException("unavailable", "Qloo response was too large.");
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static QlooEntity ParseEntity(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var id = Str(element, "entity_id") ?? Str(element, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var type = Str(element, "subtype");
        if (string.IsNullOrWhiteSpace(type) && element.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array)
            type = types.EnumerateArray().Select(t => t.ValueKind == JsonValueKind.String ? t.GetString() : null).FirstOrDefault(t => t is not null);
        double? affinity = null;
        var explain = new List<(string, double)>();
        if (element.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.Object)
        {
            if (query.TryGetProperty("affinity", out var a) && a.ValueKind == JsonValueKind.Number) affinity = a.GetDouble();
            if (query.TryGetProperty("explainability", out var ex) && ex.ValueKind == JsonValueKind.Object)
            {
                foreach (var signal in ex.EnumerateObject())
                {
                    if (signal.Value.ValueKind != JsonValueKind.Array) continue;
                    foreach (var item in signal.Value.EnumerateArray())
                    {
                        var entityId = Str(item, "entity_id") ?? Str(item, "tag_id") ?? Str(item, "id");
                        if (entityId is not null && item.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number)
                            explain.Add((entityId, s.GetDouble()));
                    }
                }
            }
        }
        double? popularity = element.TryGetProperty("popularity", out var pop) && pop.ValueKind == JsonValueKind.Number
            ? pop.GetDouble() : null;
        string description = null;
        if (element.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            description = Str(props, "description") ?? Str(props, "short_description");
        var tagNames = element.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? tags.EnumerateArray().Select(t => Str(t, "name")).Where(n => !string.IsNullOrWhiteSpace(n)).Take(12).ToArray()
            : Array.Empty<string>();
        return new QlooEntity(id, Str(element, "name") ?? id, type ?? "", popularity, affinity,
            Str(element, "disambiguation"), Trim(description, 280), tagNames, explain);
    }

    private static string Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Trim(string text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Length <= max ? text : text[..(max - 1)] + "…";

    private static Uri BaseUrlFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("QLOO_API_BASE_URL");
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri : null;
    }

    // ---------------- cache ----------------
    private bool TryGetCached(string relative, out string body)
    {
        lock (_cacheGate)
        {
            EnsureCacheLoadedLocked();
            if (_cache.TryGetValue(relative, out var entry) && entry.ExpiresUtc > _clock())
            {
                body = entry.Body;
                return true;
            }
            body = null;
            return false;
        }
    }

    private void StoreCached(string relative, string body, TimeSpan ttl)
    {
        lock (_cacheGate)
        {
            EnsureCacheLoadedLocked();
            var now = _clock();
            _cache[relative] = new CacheEntry { ExpiresUtc = now + ttl, StoredUtc = now, Body = body };
            foreach (var expired in _cache.Where(p => p.Value.ExpiresUtc <= now).Select(p => p.Key).ToArray())
                _cache.Remove(expired);
            while (_cache.Count > MaxCacheEntries)
                _cache.Remove(_cache.OrderBy(p => p.Value.StoredUtc).First().Key);
            TryWriteAtomic(_cachePath, JsonSerializer.Serialize(_cache, FileJson));
        }
    }

    private void EnsureCacheLoadedLocked()
    {
        if (_cache is not null) return;
        _cache = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_cachePath))
                _cache = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(_cachePath), FileJson)
                    ?? new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Warning("Qloo cache could not be read; starting with an empty private cache.");
        }
    }

    internal int CachedEntryCount
    {
        get { lock (_cacheGate) { EnsureCacheLoadedLocked(); return _cache.Count; } }
    }

    internal void ClearCache()
    {
        lock (_cacheGate)
        {
            _cache = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
            TryWriteAtomic(_cachePath, "{}");
        }
    }

    // ---------------- usage / daily budget ----------------
    private bool TryReserveDaily()
    {
        lock (_usageGate)
        {
            var usage = LoadUsageLocked();
            if (usage.Requests >= DailyBudget) return false;
            usage.Requests++;
            usage.LastRequestUtc = _clock();
            SaveUsageLocked(usage);
            return true;
        }
    }

    private void RecordUsage(bool cacheHit)
    {
        lock (_usageGate)
        {
            var usage = LoadUsageLocked();
            if (cacheHit) usage.CacheHits++;
            SaveUsageLocked(usage);
        }
    }

    private void RecordFailure(string code)
    {
        lock (_usageGate)
        {
            var usage = LoadUsageLocked();
            usage.Failures++;
            usage.LastError = code;
            SaveUsageLocked(usage);
        }
    }

    private QlooUsageSnapshot LoadUsageLocked()
    {
        var today = _clock().UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (_usage is null)
        {
            try
            {
                if (File.Exists(_usagePath))
                    _usage = JsonSerializer.Deserialize<QlooUsageSnapshot>(File.ReadAllText(_usagePath), FileJson);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            _usage ??= new QlooUsageSnapshot();
        }
        if (_usage.Day != today)
            _usage = new QlooUsageSnapshot { Day = today, LastError = _usage.LastError };
        return _usage;
    }

    private void SaveUsageLocked(QlooUsageSnapshot usage)
    {
        usage.DailyBudget = DailyBudget;
        TryWriteAtomic(_usagePath, JsonSerializer.Serialize(usage, FileJson));
    }

    private static void TryWriteAtomic(string path, string json)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("Qloo cache/usage file could not be written; continuing in memory.");
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed class CacheEntry
    {
        [JsonPropertyName("e")] public DateTimeOffset ExpiresUtc { get; set; }
        [JsonPropertyName("s")] public DateTimeOffset StoredUtc { get; set; }
        [JsonPropertyName("b")] public string Body { get; set; }
    }
}
