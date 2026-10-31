namespace KeyboardWtf.Services.Taste;

using System.Text.RegularExpressions;
using KeyboardWtf.Services.Qloo;

internal sealed class TasteOptionInput
{
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Attributes { get; set; } = new();
    public string EntityName { get; set; } = "";
    public bool? MeetsConstraints { get; set; }
}

internal sealed class TasteDecisionRequest
{
    public string Question { get; set; } = "";
    public string Domain { get; set; } = "general";
    public List<TasteOptionInput> Options { get; set; } = new();
    public List<string> Constraints { get; set; } = new();
    public string Context { get; set; } = "";
    public string Location { get; set; } = "";
}

/// <summary>
/// Generic personalized decision engine (no per-category prompts). Steps: classify Qloo relevance,
/// retrieve relevant taste memory, match option attributes, ground named entities and style words in
/// Qloo (/search, /v2/tags), score the user's own options with /v2/insights filter.results.entities
/// (or recommend when there are no options), combine with constraints into a ranked evidence brief.
/// Gemini makes and explains the final choice from this brief.
/// </summary>
internal sealed class TasteDecisionEngine
{
    internal const int MaxQlooCallsPerDecision = 8;
    internal const double QlooWeight = 1.5;
    private const int MaxOptions = 6;
    private const int MaxLiked = 4;
    private const int MaxStyles = 2;
    private const int MaxDisliked = 2;

    internal static readonly IReadOnlyDictionary<string, string> DomainTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["movies"] = "urn:entity:movie",
        ["tv"] = "urn:entity:tv_show",
        ["music"] = "urn:entity:artist",
        ["books"] = "urn:entity:book",
        ["podcasts"] = "urn:entity:podcast",
        ["games"] = "urn:entity:video_game",
        ["dining"] = "urn:entity:place",
        ["hotels"] = "urn:entity:place",
        ["travel"] = "urn:entity:destination",
        ["fashion"] = "urn:entity:brand",
        ["brands"] = "urn:entity:brand",
        ["people"] = "urn:entity:person",
    };

    internal static readonly IReadOnlySet<string> EntityAttributes = new HashSet<string>(StringComparer.Ordinal)
    {
        "entity", "brand", "title", "movie", "film", "show", "series", "artist", "band", "musician", "author", "book",
        "podcast", "game", "restaurant", "place", "venue", "hotel", "destination", "city", "designer", "person", "label",
    };

    internal static readonly IReadOnlySet<string> StyleAttributes = new HashSet<string>(StringComparer.Ordinal)
    {
        "style", "aesthetic", "genre", "vibe", "cuisine", "mood", "theme", "decor", "ambience",
    };

    private readonly TasteProfileStore _profile;
    private readonly QlooClient _qloo;
    private readonly Func<bool> _qlooEnabled;

    internal TasteDecisionEngine(TasteProfileStore profile, QlooClient qloo, Func<bool> qlooEnabled)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _qloo = qloo;
        _qlooEnabled = qlooEnabled ?? (() => true);
    }

    internal static string QlooTypeForDomain(string domain) =>
        DomainTypes.TryGetValue(TasteProfileStore.NormalizeDomain(domain), out var type) ? type : null;

    internal async Task<Dictionary<string, object>> DecideAsync(TasteDecisionRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        var domain = TasteProfileStore.NormalizeDomain(request.Domain);
        var entityType = QlooTypeForDomain(domain);
        var context = (request.Context ?? "").Trim();
        var prefs = _profile.Relevant(domain, context, 16);
        var options = (request.Options ?? new()).Where(o => !string.IsNullOrWhiteSpace(o?.Label)).Take(MaxOptions)
            .Select((o, i) => new OptionState(i, o)).ToList();
        var recommendMode = options.Count == 0;
        var warnings = new List<string>();
        var caveats = new List<string>();
        var signals = new List<(string Name, string Id, string Kind)>();
        List<Dictionary<string, object>> recommendations = null;
        var budget = new QlooCallBudget(MaxQlooCallsPerDecision);
        var grounded = false;
        string reason;

        foreach (var option in options) MatchPreferences(option, prefs);

        if (entityType is null)
        {
            reason = domain is "technology" or "general"
                ? "Not a cultural-taste decision, so Qloo was not called. Decide with ordinary reasoning plus any relevant taste memory."
                : $"The {domain} domain has no Qloo entity type, so Qloo was not called.";
        }
        else if (_qloo is null || !_qlooEnabled())
        {
            reason = "Qloo cultural intelligence is turned off in QLOO Settings; taste memory only.";
        }
        else if (!_qloo.IsConfigured)
        {
            reason = "No Qloo API key is configured, so this uses taste memory only. Add a key in QLOO Settings > Qloo cultural intelligence.";
            warnings.Add("Qloo not configured: NOT Qloo-grounded.");
        }
        else if (_qloo.IsLockedOut)
        {
            reason = "Qloo rejected the configured key earlier (401), so this uses taste memory only.";
            warnings.Add("Qloo key rejected: NOT Qloo-grounded.");
        }
        else
        {
            try
            {
                (grounded, reason, recommendations) = await QlooStageAsync(
                    entityType, prefs, options, recommendMode, request.Location, signals, caveats, budget, token);
            }
            catch (QlooException ex)
            {
                grounded = false;
                foreach (var option in options) { option.Affinity = null; option.Explain = ""; }
                recommendations = null;
                reason = $"Qloo was unavailable ({ex.Code}); the decision falls back to taste memory and ordinary reasoning.";
                warnings.Add($"Qloo unavailable ({ex.Code}): NOT Qloo-grounded.");
            }
        }

        // Combine: preference evidence + centred Qloo affinity; options that fail hard constraints rank last.
        var scored = options.Where(o => o.Affinity is not null).ToList();
        var mean = scored.Count > 0 ? scored.Average(o => o.Affinity.Value) : 0;
        foreach (var option in options)
        {
            if (option.Affinity is { } affinity && scored.Count >= 2)
            {
                option.Delta = Math.Round(affinity - mean, 3);
                option.Evidence.Add($"{(option.Delta >= 0 ? "+" : "−")} Qloo affinity {affinity:0.000} for {option.QlooName} ({(option.Delta >= 0 ? "+" : "")}{option.Delta:0.000} vs the options' average){(option.Explain.Length > 0 ? "; strongest links: " + option.Explain : "")}");
            }
            else if (option.Affinity is { } single)
                option.Evidence.Add($"· Qloo affinity {single:0.000} for {option.QlooName}; no other option could be scored, so it did not change the ranking.");
            if (option.Input.MeetsConstraints == false)
                option.Evidence.Add("− does not meet a stated requirement");
            option.Score = Math.Round(option.PrefScore + QlooWeight * option.Delta, 3);
        }
        var ranked = options
            .OrderBy(o => o.Input.MeetsConstraints == false)
            .ThenByDescending(o => o.Score)
            .ThenBy(o => o.Index)
            .ToList();
        for (var i = 0; i < ranked.Count; i++) ranked[i].Rank = i + 1;

        var confidence = Confidence(ranked, recommendations, grounded, signals.Count);
        var missing = new List<string>();
        if (prefs.Count == 0) missing.Add("No relevant taste memory yet; ask what the user usually likes in this area.");
        if (!recommendMode && options.All(o => o.Input.Attributes.Count == 0 && string.IsNullOrWhiteSpace(o.Input.Description)))
            missing.Add("Options have no attributes; describe them (colour, style, genre, cuisine, price) for a better comparison.");
        if (entityType is "urn:entity:place" && recommendMode && string.IsNullOrWhiteSpace(request.Location))
            missing.Add("A city or neighbourhood is needed for Qloo place recommendations.");

        var decisionId = "d-" + Guid.NewGuid().ToString("N")[..8];
        _profile.RecordDecision(new TasteDecisionRecord
        {
            Id = decisionId,
            Domain = domain,
            Context = context,
            Question = Limit(request.Question, 200),
            AtUtc = DateTimeOffset.UtcNow,
            Options = options.Select(o => new TasteDecisionOption
            {
                Label = o.Input.Label,
                EntityName = o.Input.EntityName ?? "",
                Attributes = o.Input.Attributes.Take(12).ToList(),
            }).Concat((recommendations ?? new()).Select(r => new TasteDecisionOption
            {
                Label = r["name"]?.ToString() ?? "",
                EntityName = r["name"]?.ToString() ?? "",
                Attributes = new List<string>(),
            })).ToList(),
            Recommended = ranked.FirstOrDefault()?.Input.Label ?? recommendations?.FirstOrDefault()?["name"]?.ToString() ?? "",
            QlooGrounded = grounded,
        });

        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["message"] = grounded ? "Taste brief ready (Qloo-grounded)." : "Taste brief ready (not Qloo-grounded).",
            ["decision_id"] = decisionId,
            ["mode"] = recommendMode ? "recommend" : "choose",
            ["domain"] = domain,
            ["qloo"] = new Dictionary<string, object>
            {
                ["relevant"] = entityType is not null,
                ["grounded"] = grounded,
                ["entity_type"] = entityType ?? "",
                ["reason"] = reason,
                ["calls"] = budget.Used,
                ["cache_hits"] = budget.CacheHits,
                ["signals"] = signals.Select(s => $"{s.Name} ({s.Kind})").ToArray(),
                ["warnings"] = warnings.ToArray(),
            },
            ["preferences_considered"] = prefs.Select(p => new Dictionary<string, object>
            {
                ["id"] = p.Id,
                ["statement"] = p.Statement,
                ["strength"] = p.Strength,
                ["recently_changed"] = p.RecentlyChanged,
            }).ToArray(),
            ["ranking"] = ranked.Select(o => new Dictionary<string, object>
            {
                ["rank"] = o.Rank,
                ["label"] = o.Input.Label,
                ["engine_score"] = o.Score,
                ["qloo_entity"] = o.QlooName ?? "",
                ["qloo_affinity"] = o.Affinity,
                ["qloo_vs_average"] = o.Affinity is not null && scored.Count >= 2 ? o.Delta : null,
                ["evidence"] = o.Evidence.ToArray(),
                ["meets_constraints"] = o.Input.MeetsConstraints != false,
            }).ToArray(),
            ["engine_top_choice"] = ranked.FirstOrDefault()?.Input.Label ?? recommendations?.FirstOrDefault()?["name"] ?? "",
            ["confidence"] = confidence,
            ["caveats"] = caveats.Concat(warnings).ToArray(),
            ["missing_information"] = missing.ToArray(),
            ["instruction"] = "Make the final call yourself from this brief and the user's request. Choose one option when the evidence allows; " +
                "say briefly which remembered preferences drove it. Mention Qloo evidence only when qloo.grounded is true and present it as Qloo's " +
                "cultural-affinity data, separate from your own judgement. Qloo does not judge colour, fit, size, material or visual appearance; judge those yourself. " +
                "If confidence is low, say so and ask one short question. After the user picks or rejects an option, call taste_feedback with this decision_id.",
        };
        if (recommendations is not null) result["recommendations"] = recommendations.ToArray();
        return result;
    }

    private async Task<(bool Grounded, string Reason, List<Dictionary<string, object>> Recommendations)> QlooStageAsync(
        string entityType,
        IReadOnlyList<TastePreferenceView> prefs,
        List<OptionState> options,
        bool recommendMode,
        string location,
        List<(string Name, string Id, string Kind)> signals,
        List<string> caveats,
        QlooCallBudget budget,
        CancellationToken token)
    {
        var liked = prefs.Where(p => p.Polarity > 0 && EntityAttributes.Contains(p.Attribute) && IsSafeTerm(p.Value, 6)).Take(MaxLiked).ToList();
        var styles = prefs.Where(p => p.Polarity > 0 && StyleAttributes.Contains(p.Attribute) && IsSafeTerm(p.Value, 3)).Take(MaxStyles).ToList();
        var disliked = recommendMode
            ? prefs.Where(p => p.Polarity < 0 && EntityAttributes.Contains(p.Attribute) && IsSafeTerm(p.Value, 6)).Take(MaxDisliked).ToList()
            : new List<TastePreferenceView>();
        if (liked.Count == 0 && styles.Count == 0)
        {
            caveats.Add("Cold start: no liked titles, brands, places or style words in this domain yet, so Qloo had nothing to score against.");
            return (false, "Cold start for Qloo in this domain; no Qloo calls were made.", null);
        }
        var lookups = recommendMode
            ? new List<OptionState>()
            : options.Where(o => IsSafeTerm(o.Input.EntityName, 6)).Take(4).ToList();
        if (!recommendMode && lookups.Count == 0)
        {
            caveats.Add("None of the options is a named brand, title or place, so Qloo could not score them; judged from taste memory and visible attributes.");
            return (false, "Options are not named cultural entities; Qloo was not called.", null);
        }

        foreach (var option in lookups)
        {
            var found = (await _qloo.SearchAsync(option.Input.EntityName, entityType, 1, budget, token)).FirstOrDefault();
            if (found is null) { caveats.Add($"Qloo could not resolve \"{option.Input.EntityName}\"."); continue; }
            option.QlooId = found.Id;
            option.QlooName = found.Name;
        }
        var optionIds = lookups.Where(o => o.QlooId is not null).Select(o => o.QlooId).ToHashSet(StringComparer.Ordinal);
        foreach (var preference in liked)
        {
            if (budget.Remaining <= 1) break; // keep one call for insights
            var found = (await _qloo.SearchAsync(preference.Value, entityType, 1, budget, token)).FirstOrDefault();
            if (found is null) { caveats.Add($"Qloo could not resolve your liked \"{preference.Value}\"."); continue; }
            if (optionIds.Contains(found.Id))
            {
                caveats.Add($"{found.Name} is one of the options, so it was not used as its own Qloo signal (it still counts as a direct taste-memory match).");
                continue;
            }
            if (!signals.Any(s => s.Id == found.Id)) signals.Add((found.Name, found.Id, "entity"));
        }
        foreach (var preference in styles)
        {
            if (budget.Remaining <= 1) break;
            var tags = await _qloo.SearchTagsAsync(preference.Value, 5, budget, token);
            var tag = tags.FirstOrDefault(t => t.ParentTypes.Contains(entityType));
            if (tag is null) { caveats.Add($"No Qloo tag for \"{preference.Value}\" applies to {entityType.Replace("urn:entity:", "")}."); continue; }
            signals.Add((tag.Name, tag.Id, "tag"));
        }
        var excluded = new HashSet<string>(signals.Where(s => s.Kind == "entity").Select(s => s.Id), StringComparer.Ordinal);
        foreach (var preference in disliked)
        {
            if (budget.Remaining <= 1) break;
            var found = (await _qloo.SearchAsync(preference.Value, entityType, 1, budget, token)).FirstOrDefault();
            if (found is not null) excluded.Add(found.Id);
        }

        var entitySignals = signals.Where(s => s.Kind == "entity").Select(s => s.Id).Distinct().ToArray();
        var tagSignals = signals.Where(s => s.Kind == "tag").Select(s => s.Id).Distinct().ToArray();
        if (entitySignals.Length == 0 && tagSignals.Length == 0)
            return (false, "None of your taste signals could be resolved in Qloo, so no insights call was made.", null);
        if (entityType == "urn:entity:destination" && entitySignals.Length == 0)
        {
            caveats.Add("Qloo destination insights need at least one liked destination.");
            return (false, "No liked destination resolved; Qloo destination insights skipped.", null);
        }
        var names = signals.ToDictionary(s => s.Id, s => s.Name, StringComparer.Ordinal);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["filter.type"] = entityType,
            ["feature.explainability"] = "true",
        };
        if (entitySignals.Length > 0) parameters["signal.interests.entities"] = string.Join(",", entitySignals.OrderBy(x => x, StringComparer.Ordinal));
        if (tagSignals.Length > 0) parameters["signal.interests.tags"] = string.Join(",", tagSignals.OrderBy(x => x, StringComparer.Ordinal));

        if (!recommendMode)
        {
            var candidates = options.Where(o => o.QlooId is not null).ToList();
            if (candidates.Count == 0)
                return (false, "Qloo could not resolve any option to a known entity.", null);
            var ids = candidates.Select(o => o.QlooId).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
            parameters["filter.results.entities"] = string.Join(",", ids);
            parameters["take"] = Math.Clamp(ids.Length, 1, 50).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var results = await _qloo.InsightsAsync(parameters, budget, token);
            var byId = results.ToDictionary(e => e.Id, StringComparer.Ordinal);
            foreach (var option in candidates)
            {
                if (!byId.TryGetValue(option.QlooId, out var entity) || entity.Affinity is null)
                {
                    caveats.Add($"Qloo returned no affinity for {option.QlooName}.");
                    continue;
                }
                option.Affinity = Math.Round(entity.Affinity.Value, 3);
                option.Explain = string.Join(", ", entity.Explainability
                    .Where(x => names.ContainsKey(x.EntityId) && x.Score > 0)
                    .OrderByDescending(x => x.Score).Take(2)
                    .Select(x => $"{names[x.EntityId]} {x.Score:0.00}"));
            }
            var count = options.Count(o => o.Affinity is not null);
            return count > 0
                ? (true, $"Qloo scored {count} of {options.Count} options against {signals.Count} taste signal(s) using filter.results.entities.", null)
                : (false, "Qloo answered but returned no affinity for these options.", null);
        }

        if (entityType == "urn:entity:place")
        {
            if (string.IsNullOrWhiteSpace(location))
                return (false, "Place recommendations need a city or neighbourhood; Qloo place insights skipped.", null);
            parameters["filter.location.query"] = Limit(location, 80);
        }
        if (excluded.Count > 0) parameters["filter.exclude.entities"] = string.Join(",", excluded.OrderBy(x => x, StringComparer.Ordinal));
        parameters["take"] = "6";
        var recommended = await _qloo.InsightsAsync(parameters, budget, token);
        var strongDislikes = prefs.Where(p => p.Polarity < 0 && !EntityAttributes.Contains(p.Attribute) && p.Strength != "tentative").ToList();
        var list = new List<Dictionary<string, object>>();
        foreach (var entity in recommended)
        {
            var text = entity.Name + " " + string.Join(" ", entity.TagNames) + " " + entity.Description;
            var clash = strongDislikes.FirstOrDefault(p => ValueMatches(p.Value, text));
            if (clash is not null) { caveats.Add($"Dropped {entity.Name}: it matches \"{clash.Value}\", which you dislike."); continue; }
            list.Add(new Dictionary<string, object>
            {
                ["name"] = entity.Name,
                ["qloo_affinity"] = entity.Affinity is { } a ? Math.Round(a, 3) : null,
                ["about"] = entity.Description ?? entity.Disambiguation ?? "",
                ["tags"] = entity.TagNames.Take(5).ToArray(),
                ["strongest_links"] = string.Join(", ", entity.Explainability
                    .Where(x => names.ContainsKey(x.EntityId) && x.Score > 0)
                    .OrderByDescending(x => x.Score).Take(2)
                    .Select(x => $"{names[x.EntityId]} {x.Score:0.00}")),
            });
            if (list.Count >= 5) break;
        }
        return list.Count > 0
            ? (true, $"Qloo recommended {list.Count} {entityType.Replace("urn:entity:", "")} entities from {signals.Count} taste signal(s), excluding {excluded.Count} already-liked or disliked entities.", list)
            : (false, "Qloo returned no recommendations that fit your taste memory.", list);
    }

    private static void MatchPreferences(OptionState option, IReadOnlyList<TastePreferenceView> prefs)
    {
        var text = string.Join(" ", new[] { option.Input.Label, option.Input.Description, option.Input.EntityName }
            .Concat(option.Input.Attributes ?? new()));
        foreach (var preference in prefs)
        {
            if (!ValueMatches(preference.Value, text)) continue;
            var direct = !string.IsNullOrWhiteSpace(option.Input.EntityName)
                && TasteProfileStore.Normalize(option.Input.EntityName) == TasteProfileStore.Normalize(preference.Value);
            var weight = preference.Polarity * preference.Confidence * (direct ? 2 : 1) * (preference.Context.Length > 0 ? 1.25 : 1);
            option.PrefScore = Math.Round(option.PrefScore + weight, 3);
            var verb = preference.Polarity > 0 ? "matches your liking of" : "conflicts with your dislike of";
            var context = preference.Context.Length > 0 ? $" for {preference.Context}" : "";
            option.Evidence.Add($"{(preference.Polarity > 0 ? "+" : "−")} {verb} {preference.Value}{context} ({preference.Strength}, {preference.Kind}, pref {preference.Id})");
        }
    }

    internal static bool ValueMatches(string value, string text)
    {
        var needle = TasteProfileStore.Normalize(value);
        var haystack = " " + TasteProfileStore.Normalize(text) + " ";
        if (needle.Length < 2) return false;
        if (haystack.Contains(" " + needle + " ", StringComparison.Ordinal)) return true;
        // Light plural/stem tolerance: "earth tones" vs "earth tone", "minimalism" vs "minimalist".
        var stem = needle.Length > 5 ? needle[..^2] : needle;
        return needle.Length > 5 && Regex.IsMatch(haystack, $@"\b{Regex.Escape(stem)}\w*\b");
    }

    private static string Confidence(List<OptionState> ranked, List<Dictionary<string, object>> recommendations, bool grounded, int signalCount)
    {
        if (recommendations is not null)
            return !grounded || recommendations.Count == 0 ? "low" : signalCount >= 2 ? "medium" : "low";
        if (ranked.Count == 0) return "low";
        var profileEvidence = ranked.Any(o => o.Evidence.Any(e => e.Contains("pref ", StringComparison.Ordinal)));
        var qlooEvidence = ranked.Any(o => o.Affinity is not null);
        if (ranked.Count == 1) return profileEvidence ? "medium" : "low";
        var margin = ranked[0].Score - ranked[1].Score;
        if (ranked[1].Input.MeetsConstraints == false && ranked[0].Input.MeetsConstraints != false)
            return profileEvidence || qlooEvidence ? "high" : "medium";
        if ((margin >= 0.6 && profileEvidence && qlooEvidence) || (margin >= 1.0 && profileEvidence)) return "high";
        if (margin >= 0.25) return "medium";
        return "low";
    }

    private static bool IsSafeTerm(string value, int maxWords)
    {
        var text = (value ?? "").Trim();
        return text.Length is > 0 and <= 60
            && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= maxWords
            && TasteProfileStore.SensitiveReason(text) is null
            && !text.Contains('@') && !Regex.IsMatch(text, @"\d{5,}");
    }

    private static string Limit(string text, int max)
    {
        var clean = (text ?? "").Trim();
        return clean.Length <= max ? clean : clean[..max];
    }

    private sealed class OptionState
    {
        internal OptionState(int index, TasteOptionInput input)
        {
            Index = index;
            Input = input;
            Input.Attributes ??= new();
        }

        internal int Index { get; }
        internal TasteOptionInput Input { get; }
        internal double PrefScore { get; set; }
        internal List<string> Evidence { get; } = new();
        internal string QlooId { get; set; }
        internal string QlooName { get; set; }
        internal double? Affinity { get; set; }
        internal double Delta { get; set; }
        internal string Explain { get; set; } = "";
        internal double Score { get; set; }
        internal int Rank { get; set; }
    }
}
