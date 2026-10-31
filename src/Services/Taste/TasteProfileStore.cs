namespace KeyboardWtf.Services.Taste;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

public enum TastePreferenceKind { Explicit, Inferred, Contextual, Temporary }

public sealed class TasteObservation
{
    public double Weight { get; set; }
    public int Polarity { get; set; }
    public DateTimeOffset AtUtc { get; set; }
    public string Source { get; set; } = "";
    public string Quote { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string DecisionId { get; set; } = "";
}

public sealed class TastePreference
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Domain { get; set; } = "general";
    public string Attribute { get; set; } = "style";
    public string Value { get; set; } = "";
    public string Context { get; set; } = "";
    public TastePreferenceKind Kind { get; set; } = TastePreferenceKind.Inferred;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    public bool RecentlyChanged { get; set; }
    public List<TasteObservation> Observations { get; set; } = new();
}

public sealed class TasteObservationInput
{
    public string Domain { get; set; }
    public string Attribute { get; set; }
    public string Value { get; set; }
    public int Polarity { get; set; } = 1;
    /// <summary>explicit_statement, choice, rejection, inferred_mention, correction.</summary>
    public string Source { get; set; } = "explicit_statement";
    public string Context { get; set; }
    public bool Temporary { get; set; }
    public int TemporaryDays { get; set; } = 14;
    public string Quote { get; set; }
    public string DecisionId { get; set; }
}

public sealed class TasteObserveResult
{
    public bool Stored { get; init; }
    public bool Duplicate { get; init; }
    public string Refused { get; init; }
    public TastePreferenceView Preference { get; init; }
}

/// <summary>Read model with derived polarity/confidence; safe to serialize to the settings page.</summary>
public sealed class TastePreferenceView
{
    public string Id { get; init; }
    public string Domain { get; init; }
    public string Attribute { get; init; }
    public string Value { get; init; }
    public string Context { get; init; }
    public string Kind { get; init; }
    public int Polarity { get; init; }
    public double Confidence { get; init; }
    public string Strength { get; init; }
    public int Evidence { get; init; }
    public bool RecentlyChanged { get; init; }
    public string Statement { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public DateTimeOffset? ExpiresUtc { get; init; }
    public string LastQuote { get; init; }
}

public sealed class TasteDecisionOption
{
    public string Label { get; set; } = "";
    public string EntityName { get; set; } = "";
    public List<string> Attributes { get; set; } = new();
}

public sealed class TasteDecisionRecord
{
    public string Id { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Context { get; set; } = "";
    public string Question { get; set; } = "";
    public DateTimeOffset AtUtc { get; set; }
    public List<TasteDecisionOption> Options { get; set; } = new();
    public string Recommended { get; set; } = "";
    public bool QlooGrounded { get; set; }
    public List<string> FeedbackApplied { get; set; } = new();
}

/// <summary>
/// Persistent adaptive taste memory (%LOCALAPPDATA%\QLOO\taste-profile.json).
/// Evidence-weighted preferences with confidence, recency decay, contradiction handling,
/// contextual/temporary separation, duplicate suppression and a sensitive-attribute guard.
/// </summary>
public sealed class TasteProfileStore
{
    public const int MaxPreferences = 300;
    public const int MaxDecisions = 25;
    internal const double ConfidenceRate = 1.2;
    internal static readonly IReadOnlyDictionary<string, double> SourceWeights = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["explicit_statement"] = 1.0,
        ["correction"] = 1.0,
        ["choice"] = 0.6,
        ["rejection"] = 0.6,
        ["inferred_mention"] = 0.35,
    };

    internal static readonly string[] Domains =
    [
        "movies", "tv", "music", "books", "podcasts", "games", "fashion", "brands", "dining", "travel",
        "hotels", "lifestyle", "people", "technology", "general",
    ];

    // Never stored: special-category or identifying data (privacy rule, docs/qloo/INTEGRATION_DESIGN.md).
    private static readonly Regex Sensitive = new(
        @"\b(health|medical|medication|diagnos\w*|disease|illness|disab\w*|pregnan\w*|mental|therapy|religio\w*|church|mosque|temple|faith|politic\w*|democrat\w*|republican\w*|party affiliation|vote\w*|sexual\w*|gay|lesbian|bisexual|transgender|ethnic\w*|race|racial|caste|immigra\w*|visa status|citizenship|criminal|arrest\w*|salary|income|debt|credit score|bank account|password|ssn|social security|passport|phone number|email address|home address)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _clock;
    private ProfileDocument _document = new();

    public TasteProfileStore(string path = null, Func<DateTimeOffset> clock = null)
    {
        ProfilePath = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(SettingsService.AppDataDir, "taste-profile.json")
            : path;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string ProfilePath { get; }

    public event Action Changed;

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(ProfilePath)) { _document = new ProfileDocument(); return; }
                _document = JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(ProfilePath), JsonOptions) ?? new ProfileDocument();
                _document.Preferences ??= new();
                _document.Decisions ??= new();
                _document.Preferences.RemoveAll(p => string.IsNullOrWhiteSpace(p.Value) || p.Observations is null || p.Observations.Count == 0);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                AppLog.Warning("Taste profile could not be read; starting empty without overwriting the file.");
                _document = new ProfileDocument { ReadOnlyAfterFailure = true };
            }
        }
    }

    internal static string SensitiveReason(params string[] values)
    {
        var text = string.Join(" ", values.Where(v => !string.IsNullOrWhiteSpace(v)));
        return Sensitive.IsMatch(text)
            ? "Not stored: QLOO never saves health, religion, politics, sexuality, ethnicity, finances, identifiers or similar sensitive traits."
            : null;
    }

    public TasteObserveResult Observe(TasteObservationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var value = Clean(input.Value, 80);
        if (value.Length == 0)
            return new TasteObserveResult { Refused = "Nothing specific enough to remember." };
        var attribute = CleanKey(input.Attribute, "style");
        var domain = NormalizeDomain(input.Domain);
        var context = Clean(input.Context, 40).ToLowerInvariant();
        var refusal = SensitiveReason(domain, attribute, value, context);
        if (refusal is not null)
            return new TasteObserveResult { Refused = refusal };
        var source = SourceWeights.ContainsKey(input.Source ?? "") ? input.Source : "inferred_mention";
        var polarity = source == "rejection" ? -1 : input.Polarity < 0 ? -1 : 1;
        var now = _clock();
        var quote = Clean(input.Quote, 160);
        var temporary = input.Temporary;
        var fingerprint = Fingerprint(domain, attribute, value, context, temporary, polarity, quote, input.DecisionId, source);

        lock (_lock)
        {
            if (_document.ReadOnlyAfterFailure)
                return new TasteObserveResult { Refused = "The taste profile file could not be read, so nothing was changed. Fix or delete taste-profile.json." };
            PruneExpiredLocked(now);
            var preference = _document.Preferences.FirstOrDefault(p =>
                p.Domain == domain && p.Attribute == attribute && Normalize(p.Value) == Normalize(value)
                && p.Context == context && (p.Kind == TastePreferenceKind.Temporary) == temporary);
            if (preference is not null && preference.Observations.Any(o =>
                    o.Fingerprint == fingerprint && now - o.AtUtc < TimeSpan.FromHours(24)))
                return new TasteObserveResult { Duplicate = true, Preference = View(preference, now) };

            if (preference is null)
            {
                preference = new TastePreference
                {
                    Domain = domain,
                    Attribute = attribute,
                    Value = value,
                    Context = context,
                    CreatedUtc = now,
                };
                _document.Preferences.Add(preference);
            }

            // Contradiction: a fresh explicit statement or correction halves older opposite evidence
            // instead of deleting it, so a single remark cannot silently erase long-held taste.
            if (source is "explicit_statement" or "correction")
            {
                var opposite = preference.Observations.Where(o => o.Polarity != polarity).ToArray();
                foreach (var o in opposite) o.Weight *= 0.5;
                preference.RecentlyChanged = opposite.Length > 0;
            }
            preference.Observations.Add(new TasteObservation
            {
                Weight = SourceWeights[source],
                Polarity = polarity,
                AtUtc = now,
                Source = source,
                Quote = quote,
                Fingerprint = fingerprint,
                DecisionId = Clean(input.DecisionId, 40),
            });
            if (preference.Observations.Count > 40)
                preference.Observations.RemoveRange(0, preference.Observations.Count - 40);
            preference.Kind = temporary
                ? TastePreferenceKind.Temporary
                : context.Length > 0
                    ? TastePreferenceKind.Contextual
                    : preference.Observations.Any(o => o.Source is "explicit_statement" or "correction")
                        ? TastePreferenceKind.Explicit
                        : TastePreferenceKind.Inferred;
            preference.ExpiresUtc = temporary ? now.AddDays(Math.Clamp(input.TemporaryDays, 1, 90)) : null;
            preference.UpdatedUtc = now;
            TrimLocked(now);
            SaveLocked();
            return new TasteObserveResult { Stored = true, Preference = View(preference, now) };
        }
    }

    public bool Forget(string idOrValue)
    {
        var needle = (idOrValue ?? "").Trim();
        if (needle.Length == 0) return false;
        lock (_lock)
        {
            var removed = _document.Preferences.RemoveAll(p =>
                string.Equals(p.Id, needle, StringComparison.OrdinalIgnoreCase)
                || Normalize(p.Value) == Normalize(needle)) > 0;
            if (removed) SaveLocked();
            return removed;
        }
    }

    /// <summary>User correction from Settings or voice: replaces value/polarity/context, keeps an audit observation.</summary>
    public TastePreferenceView Correct(string id, string newValue = null, int? polarity = null, string context = null)
    {
        lock (_lock)
        {
            var preference = _document.Preferences.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (preference is null) return null;
            var now = _clock();
            if (!string.IsNullOrWhiteSpace(newValue))
            {
                var value = Clean(newValue, 80);
                if (SensitiveReason(value) is not null) return null;
                preference.Value = value;
            }
            if (context is not null) preference.Context = Clean(context, 40).ToLowerInvariant();
            if (polarity is { } wanted)
            {
                var sign = wanted < 0 ? -1 : 1;
                // A user correction is authoritative: older contrary evidence is retired.
                preference.Observations.RemoveAll(o => o.Polarity != sign);
            }
            preference.Observations.Add(new TasteObservation
            {
                Weight = SourceWeights["correction"],
                Polarity = polarity is { } p ? (p < 0 ? -1 : 1) : Math.Sign(Net(preference, now)) is 0 ? 1 : Math.Sign(Net(preference, now)),
                AtUtc = now,
                Source = "correction",
                Quote = "Corrected by the user",
                Fingerprint = Guid.NewGuid().ToString("N"),
            });
            preference.Kind = preference.Context.Length > 0 ? TastePreferenceKind.Contextual : TastePreferenceKind.Explicit;
            preference.RecentlyChanged = true;
            preference.UpdatedUtc = now;
            SaveLocked();
            return View(preference, now);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _document = new ProfileDocument();
            SaveLocked();
        }
    }

    public IReadOnlyList<TastePreferenceView> Snapshot()
    {
        lock (_lock)
        {
            var now = _clock();
            return _document.Preferences
                .Where(p => !IsExpired(p, now))
                .Select(p => View(p, now))
                .OrderByDescending(v => v.Confidence)
                .ThenByDescending(v => v.UpdatedUtc)
                .ToArray();
        }
    }

    /// <summary>Preferences that apply to this domain/context: same domain, cross-domain lifestyle/general,
    /// and contextual ones only when the context matches. Expired temporary preferences are skipped.</summary>
    public IReadOnlyList<TastePreferenceView> Relevant(string domain, string context, int max = 16)
    {
        var wantedDomain = NormalizeDomain(domain);
        var wantedContext = Clean(context, 40).ToLowerInvariant();
        lock (_lock)
        {
            var now = _clock();
            return _document.Preferences
                .Where(p => !IsExpired(p, now))
                .Where(p => p.Domain == wantedDomain || p.Domain is "lifestyle" or "general"
                    || (wantedDomain is "fashion" && p.Domain == "brands") || (wantedDomain is "brands" && p.Domain == "fashion")
                    || (wantedDomain is "movies" && p.Domain == "tv") || (wantedDomain is "tv" && p.Domain == "movies"))
                .Where(p => p.Context.Length == 0 || (wantedContext.Length > 0
                    && (wantedContext.Contains(p.Context, StringComparison.Ordinal) || p.Context.Contains(wantedContext, StringComparison.Ordinal))))
                .Select(p => View(p, now))
                .Where(v => v.Confidence >= 0.05)
                .OrderByDescending(v => v.Context.Length > 0)
                .ThenByDescending(v => v.Confidence)
                .Take(Math.Clamp(max, 1, 40))
                .ToArray();
        }
    }

    public string BuildPromptDigest(int maxEntries = 12, int maxChars = 900)
    {
        var lines = Snapshot().Take(maxEntries).Select(v => "- " + v.Statement).ToArray();
        if (lines.Length == 0) return "No taste preferences learned yet.";
        var text = string.Join("\n", lines);
        return text.Length <= maxChars ? text : text[..maxChars] + "…";
    }

    public void RecordDecision(TasteDecisionRecord record)
    {
        if (record is null || string.IsNullOrWhiteSpace(record.Id)) return;
        lock (_lock)
        {
            if (_document.ReadOnlyAfterFailure) return;
            _document.Decisions.RemoveAll(d => d.Id == record.Id);
            _document.Decisions.Add(record);
            if (_document.Decisions.Count > MaxDecisions)
                _document.Decisions.RemoveRange(0, _document.Decisions.Count - MaxDecisions);
            SaveLocked();
        }
    }

    public TasteDecisionRecord FindDecision(string id)
    {
        lock (_lock)
        {
            var record = string.IsNullOrWhiteSpace(id)
                ? _document.Decisions.LastOrDefault()
                : _document.Decisions.FirstOrDefault(d => d.Id == id.Trim()) ?? _document.Decisions.LastOrDefault();
            return record is null ? null : JsonSerializer.Deserialize<TasteDecisionRecord>(JsonSerializer.Serialize(record, JsonOptions), JsonOptions);
        }
    }

    public void MarkFeedback(string decisionId, string label)
    {
        lock (_lock)
        {
            var record = _document.Decisions.FirstOrDefault(d => d.Id == decisionId);
            if (record is null) return;
            record.FeedbackApplied.Add(label);
            SaveLocked();
        }
    }

    // ---------------- maths ----------------
    internal static double HalfLifeDays(TastePreference p) => p.Kind switch
    {
        TastePreferenceKind.Explicit => 365,
        TastePreferenceKind.Temporary => 30,
        _ => 120,
    };

    internal static double Net(TastePreference p, DateTimeOffset now)
    {
        var halfLife = HalfLifeDays(p);
        return p.Observations.Sum(o =>
        {
            var ageDays = Math.Max(0, (now - o.AtUtc).TotalDays);
            return o.Polarity * o.Weight * Math.Pow(0.5, ageDays / halfLife);
        });
    }

    internal static double ConfidenceFromNet(double net) => 1 - Math.Exp(-ConfidenceRate * Math.Abs(net));

    internal static string StrengthLabel(double confidence) =>
        confidence >= 0.6 ? "strong" : confidence >= 0.4 ? "moderate" : "tentative";

    private static bool IsExpired(TastePreference p, DateTimeOffset now) => p.ExpiresUtc is { } expires && expires <= now;

    private static TastePreferenceView View(TastePreference p, DateTimeOffset now)
    {
        var net = Net(p, now);
        var confidence = Math.Round(ConfidenceFromNet(net), 2);
        var polarity = net < 0 ? -1 : 1;
        var strength = StrengthLabel(confidence);
        var verb = polarity < 0 ? "dislikes" : "likes";
        var where = p.Context.Length > 0 ? $" for {p.Context}" : "";
        var temp = p.Kind == TastePreferenceKind.Temporary ? " (temporary)" : "";
        return new TastePreferenceView
        {
            Id = p.Id,
            Domain = p.Domain,
            Attribute = p.Attribute,
            Value = p.Value,
            Context = p.Context,
            Kind = p.Kind.ToString().ToLowerInvariant(),
            Polarity = polarity,
            Confidence = confidence,
            Strength = strength,
            Evidence = p.Observations.Count,
            RecentlyChanged = p.RecentlyChanged,
            Statement = $"{verb} {p.Value}{where} ({p.Domain} {p.Attribute}; {strength}, {p.Kind.ToString().ToLowerInvariant()}, {p.Observations.Count} observation{(p.Observations.Count == 1 ? "" : "s")}){temp}",
            UpdatedUtc = p.UpdatedUtc,
            ExpiresUtc = p.ExpiresUtc,
            LastQuote = p.Observations.LastOrDefault(o => o.Quote.Length > 0)?.Quote ?? "",
        };
    }

    private void PruneExpiredLocked(DateTimeOffset now) => _document.Preferences.RemoveAll(p => IsExpired(p, now));

    private void TrimLocked(DateTimeOffset now)
    {
        if (_document.Preferences.Count <= MaxPreferences) return;
        var weakest = _document.Preferences
            .Where(p => p.Kind != TastePreferenceKind.Explicit)
            .OrderBy(p => ConfidenceFromNet(Net(p, now)))
            .ThenBy(p => p.UpdatedUtc)
            .Take(_document.Preferences.Count - MaxPreferences)
            .ToHashSet();
        _document.Preferences.RemoveAll(weakest.Contains);
    }

    private void SaveLocked()
    {
        var directory = Path.GetDirectoryName(ProfilePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = ProfilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_document, JsonOptions));
        File.Move(temporary, ProfilePath, overwrite: true);
        SavedStateNotifications.Notify(Changed);
    }

    internal static string NormalizeDomain(string domain)
    {
        var d = (domain ?? "").Trim().ToLowerInvariant();
        d = d switch
        {
            "movie" or "film" or "films" or "cinema" => "movies",
            "tv_show" or "tv shows" or "television" or "series" => "tv",
            "artist" or "artists" or "songs" => "music",
            "book" or "reading" => "books",
            "podcast" => "podcasts",
            "video games" or "video_game" or "gaming" or "game" => "games",
            "clothing" or "clothes" or "apparel" or "style" or "shopping" => "fashion",
            "brand" or "products" or "product" => "brands",
            "food" or "restaurants" or "restaurant" or "cuisine" or "drinks" => "dining",
            "destination" or "destinations" or "trips" or "trip" => "travel",
            "hotel" or "venues" or "venue" => "hotels",
            "aesthetics" or "home" or "decor" => "lifestyle",
            "person" or "celebrities" => "people",
            "tech" or "technical" or "work" or "career" or "opportunities" or "hackathon" => "technology",
            _ => d,
        };
        return Domains.Contains(d) ? d : "general";
    }

    internal static string Normalize(string value) =>
        Regex.Replace((value ?? "").ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", " ").Trim();

    private static string CleanKey(string value, string fallback)
    {
        var clean = Regex.Replace((value ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9 _\-]", "");
        clean = clean.Replace(' ', '_');
        return clean.Length is > 0 and <= 32 ? clean : fallback;
    }

    private static string Clean(string value, int max)
    {
        var clean = string.Join(" ", (value ?? "").ReplaceLineEndings(" ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        clean = new string(clean.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length <= max ? clean : clean[..max].Trim();
    }

    private static string Fingerprint(params object[] parts)
    {
        var text = string.Join("|", parts.Select(p => Normalize(p?.ToString() ?? "")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24];
    }

    private sealed class ProfileDocument
    {
        public int Schema { get; set; } = 1;
        public List<TastePreference> Preferences { get; set; } = new();
        public List<TasteDecisionRecord> Decisions { get; set; } = new();
        [JsonIgnore] public bool ReadOnlyAfterFailure { get; set; }
    }
}
