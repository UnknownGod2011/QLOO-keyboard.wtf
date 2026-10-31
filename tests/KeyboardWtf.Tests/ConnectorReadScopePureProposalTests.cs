namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using Xunit;

// Pure synthetic proposal tests: no controller, gateway, provider, clock or
// machine-timezone changes; no app, UI, audio, profile or external actions.
public sealed class ConnectorReadScopePureProposalTests
{
    private static readonly DateTimeOffset Clock = DateTimeOffset.Parse("2026-10-11T19:49:00Z");
    private const string Zone = "India Standard Time";
    private static ConnectorReadScope Today(string request = "Check today's professor responses.")
        => ConnectorReadScope.Freeze(request, Clock, Zone);

    [Theory]
    [InlineData("date:2026/10/19")]
    [InlineData("from:Sender (DATE:2026-10-19)")]
    [InlineData("from:Sender -date:2026/10/19")]
    public void Inferred_unsupported_date_filter_cannot_reach_the_native_reader(string query)
        => Assert.Throws<ArgumentException>(() => ConnectorReadScope.Freeze(
            "Find an email received on October 19", Clock, Zone).Apply(query));

    [Theory]
    [InlineData("subject:\"date:2026/10/19\"")]
    [InlineData("\"date:2026/10/19\"")]
    [InlineData("from:Sender after:1792348200 before:1792434600")]
    public void Supported_dates_and_quoted_terms_remain_unchanged(string query)
        => Assert.Equal(query, ConnectorReadScope.Freeze("Find an email", Clock, Zone).Apply(query));

    [Fact]
    public void Explicit_human_date_text_remains_literal()
    {
        const string query = "date:2026/10/19";
        Assert.Equal(query, ConnectorReadScope.Freeze("Search using: " + query, Clock, Zone).Apply(query));
    }

    [Theory]
    [InlineData("Only yesterday's professor responses", "Yesterday")]
    [InlineData("Only yesterday’s research replies", "Yesterday")]
    [InlineData("Actually, read yesterday's emails instead.", "Yesterday")]
    [InlineData("Find the professor's reply today.", "Today")]
    [InlineData("Check today's emails for a research response.", "Today")]
    [InlineData("Only today's messages", "Today")]
    [InlineData("Can you check today's emails?", "Today")]
    [InlineData("Read messages from professor@example.test yesterday", "Yesterday")]
    [InlineData("Did the professor reply yesterday?", "Yesterday")]
    [InlineData("Check whether the professor responded yesterday.", "Yesterday")]
    [InlineData("Only yesterday instead", "Yesterday")]
    [InlineData("Actually, yesterday", "Yesterday")]
    [InlineData("Use yesterday instead", "Yesterday")]
    [InlineData("Only today instead", "Today")]
    [InlineData("Actually, today", "Today")]
    public void Positive_relative_day_instructions_are_narrow_and_include_the_real_only_yesterday_style(string text, string expected)
        => Assert.Equal(expected, ConnectorReadScope.ParseRelativeDay(text).ToString());

    [Theory]
    [InlineData("Today, not yesterday.", "Ambiguous")]
    [InlineData("Read today's and yesterday's emails.", "Ambiguous")]
    [InlineData("Read today's emails. Read yesterday's emails.", "Ambiguous")]
    [InlineData("Do not read yesterday's responses.", "Ambiguous")]
    [InlineData("Do not, read yesterday's responses.", "Ambiguous")]
    [InlineData("No bodies and do not, read yesterday's responses.", "Ambiguous")]
    [InlineData("Only yesterday instead, if I approve.", "Ambiguous")]
    [InlineData("Do not use yesterday instead.", "Ambiguous")]
    [InlineData("Only yesterday instead, today.", "Ambiguous")]
    [InlineData("The email said \"Actually, yesterday\".", "None")]
    [InlineData("Read yesterday's emails only if I approve later.", "Ambiguous")]
    [InlineData("Read yesterday's emails after I approve.", "Ambiguous")]
    [InlineData("Only yesterday's responses; if I approve later.", "Ambiguous")]
    [InlineData("Read yesterday's responses. Wait until I confirm first.", "Ambiguous")]
    [InlineData("Only today's emails or last week's emails", "Ambiguous")]
    [InlineData("Read messages about yesterday.", "Ambiguous")]
    [InlineData("The email said yesterday's professor responses.", "Ambiguous")]
    [InlineData("The body contains \"Only yesterday's professor responses\".", "None")]
    [InlineData("Only 'yesterday' messages", "None")]
    [InlineData("Read messages matching “today”", "None")]
    [InlineData("Read messages at https://example.test/yesterday", "None")]
    [InlineData("Use exact query: subject:\"today\" from:yesterday@example.test", "None")]
    [InlineData("Only tomorrow's messages", "Unsupported")]
    [InlineData("Read last week's emails.", "Unsupported")]
    [InlineData("Only last Tuesday's replies", "Unsupported")]
    [InlineData("Only two days ago replies", "Unsupported")]
    public void Quotes_negative_choices_external_mentions_and_unsupported_days_do_not_grant_calendar_authority(string text, string expected)
        => Assert.Equal(expected, ConnectorReadScope.ParseRelativeDay(text).ToString());

    [Fact]
    public void Yesterday_correction_uses_original_local_clock_even_if_work_has_crossed_another_midnight()
    {
        var original = Today();
        var change = original.Refine("Only yesterday's professor responses", []);
        Assert.True(change.Accepted); Assert.Equal(ConnectorReadScopeChangeKind.Applied, change.Kind);
        Assert.Equal("2026-10-12", original.DateScope.LocalDay);
        Assert.Equal("2026-10-11", change.Scope.DateScope.LocalDay);
        Assert.Equal("yesterday", change.Scope.DateScope.RelativeDay);
        Assert.Equal(Clock, change.Scope.RequestUtc); Assert.Equal(Zone, change.Scope.TimeZoneId);
        Assert.Equal(original.OriginalRequest, change.Scope.OriginalRequest);
        Assert.Equal("(research) " + change.Scope.DateScope.Query, change.Scope.Apply("research"));
        Assert.Equal(change.Scope.Apply("research"), change.Scope.Apply(change.Scope.Apply("research")));
        var prompt = JsonSerializer.SerializeToElement(change.Scope.DateScope.Prompt);
        Assert.Equal("yesterday", prompt.GetProperty("relativeDay").GetString());
        Assert.Equal("2026-10-11", prompt.GetProperty("localDay").GetString());
    }

    [Theory]
    [InlineData("Only yesterday instead")]
    [InlineData("Actually, yesterday")]
    [InlineData("Use yesterday instead")]
    public void Current_read_context_accepts_clear_day_corrections_without_repeating_a_mail_noun(string instruction)
    {
        var original = Today(); var change = original.Refine(instruction, []);
        Assert.True(change.Accepted); Assert.Equal(ConnectorReadScopeChangeKind.Applied, change.Kind);
        Assert.Equal("2026-10-11", change.Scope.DateScope.LocalDay);
        Assert.Equal(original.OriginalRequest, change.Scope.OriginalRequest);
        Assert.Equal(original.RequestUtc, change.Scope.RequestUtc); Assert.Equal(original.TimeZoneId, change.Scope.TimeZoneId);
    }

    [Theory]
    [InlineData("Read today's and yesterday's emails.", "AmbiguousDay")]
    [InlineData("Today, not yesterday.", "AmbiguousDay")]
    [InlineData("Only tomorrow's messages", "UnsupportedDay")]
    [InlineData("Only last Tuesday's replies", "UnsupportedDay")]
    [InlineData("Only two days ago replies", "UnsupportedDay")]
    public void Rejected_day_refinement_returns_the_same_immutable_scope(string text, string kind)
    {
        var original = Today(); var change = original.Refine(text, []);
        Assert.False(change.Accepted); Assert.Equal(kind, change.Kind.ToString()); Assert.Same(original, change.Scope);
        Assert.Equal(original.Identity, change.Scope.Identity);
    }

    [Theory]
    [InlineData("Only yesterday's professor responses; do not read message bodies.")]
    [InlineData("Only yesterday's emails, no bodies.")]
    [InlineData("Do not read message bodies; only yesterday's professor responses.")]
    [InlineData("No bodies, only yesterday's professor responses.")]
    [InlineData("Do not read message bodies, only yesterday's professor responses.")]
    [InlineData("Only yesterday's responses, do not read message bodies.")]
    [InlineData("No bodies, only yesterday instead.")]
    public void Negative_body_constraints_do_not_negate_an_independent_positive_relative_day(string instruction)
    {
        var original = Today(); var change = original.Refine(instruction, []);
        Assert.True(change.Accepted); Assert.Equal(ConnectorReadScopeChangeKind.Applied, change.Kind);
        Assert.Equal("2026-10-11", change.Scope.DateScope.LocalDay); Assert.True(change.Scope.MetadataOnly);
        Assert.Equal(Clock, change.Scope.RequestUtc); Assert.Equal(original.OriginalRequest, change.Scope.OriginalRequest);
    }

    [Fact]
    public void Adding_no_bodies_cannot_hide_a_separate_unattached_negative_calendar_instruction()
    {
        var original = Today();
        var change = original.Refine("No bodies and do not, read yesterday's responses.", []);
        Assert.False(change.Accepted); Assert.Equal(ConnectorReadScopeChangeKind.AmbiguousDay, change.Kind);
        Assert.Same(original, change.Scope); Assert.Equal("2026-10-12", original.DateScope.LocalDay);
    }

    [Fact]
    public void A_new_literal_query_preserves_the_existing_day_and_does_not_silently_invent_date_filters()
    {
        var original = Today(); var change = original.Refine("Search using: from:new@example.test", []);
        Assert.False(change.Accepted); Assert.Equal(ConnectorReadScopeChangeKind.LiteralDayConflict, change.Kind);
        Assert.Same(original, change.Scope); Assert.Null(original.LiteralQuery);
    }

    [Fact]
    public void Compatible_literal_replacement_preserves_case_quoting_addresses_and_date_operators_exactly()
    {
        var original = Today();
        var literal = "from:Professor@example.test subject:\"Research Today\" " + original.DateScope.Query;
        var change = original.Refine("Search using: " + literal, []);
        Assert.True(change.Accepted); Assert.Equal(literal, change.Scope.LiteralQuery);
        Assert.Equal(literal, change.Scope.Apply(literal)); Assert.Equal(original.DateScope.Query, change.Scope.DateScope.Query);
        Assert.False(change.Scope.DateScope.CanInspectWholeDay);
        Assert.Throws<ArgumentException>(() => change.Scope.Apply(literal.ToLowerInvariant()));
        Assert.Null(original.LiteralQuery); Assert.Equal(Clock, change.Scope.RequestUtc);
    }

    [Fact]
    public void A_day_change_cannot_rewrite_an_existing_exact_query_or_silently_remove_it()
    {
        var baseScope = Today(); var literal = "from:p@example.test " + baseScope.DateScope.Query;
        var original = baseScope.Refine("Search using: " + literal, []).Scope;
        var change = original.Refine("Only yesterday's professor responses", ["Search using: " + literal]);
        Assert.False(change.Accepted); Assert.Equal(ConnectorReadScopeChangeKind.LiteralDayConflict, change.Kind);
        Assert.Same(original, change.Scope); Assert.Equal(literal, original.LiteralQuery);
    }

    [Fact]
    public void A_complete_compatible_day_and_literal_correction_is_validated_without_rewriting()
    {
        var original = Today();
        var yesterday = ConnectorMailDateScope.FromRelativeDay("Only yesterday's responses", Clock, Zone, -1);
        var literal = "from:p@example.test " + yesterday.Query;
        var change = original.Refine("Only yesterday's responses. Search using: " + literal, []);
        Assert.True(change.Accepted); Assert.Equal("2026-10-11", change.Scope.DateScope.LocalDay);
        Assert.Equal(literal, change.Scope.Apply(literal)); Assert.Equal(Clock, change.Scope.RequestUtc);
    }

    [Theory]
    [InlineData("subject:research OR after:1791743400 before:1791829800")]
    [InlineData("subject:research after:2026/10/12 before:2026/10/13")]
    [InlineData("subject:research -after:1791743400 before:1791829800")]
    [InlineData("subject:research after:1791743400 before:1791829800 newer_than:1d")]
    public void Incompatible_exact_filters_do_not_escape_the_compulsory_existing_local_day(string query)
    {
        var original = Today(); var change = original.Refine("Search using: " + query, []);
        Assert.False(change.Accepted); Assert.Equal(ConnectorReadScopeChangeKind.LiteralDayConflict, change.Kind);
        Assert.Same(original, change.Scope);
    }

    [Fact]
    public void A_literal_without_a_day_can_be_replaced_while_quoted_provider_text_cannot_replace_it()
    {
        var original = ConnectorReadScope.Freeze("Search using: subject:original", Clock, Zone);
        var change = original.Refine("Actually, search using: from:New@example.test subject:\"Today\"", []);
        Assert.True(change.Accepted); Assert.Null(change.Scope.DateScope);
        Assert.Equal("from:New@example.test subject:\"Today\"", change.Scope.LiteralQuery);
        var quoted = change.Scope.Refine("The email said \"Search using: subject:external\"", []);
        Assert.False(quoted.Accepted); Assert.Equal(ConnectorReadScopeChangeKind.InvalidQuery, quoted.Kind); Assert.Same(change.Scope, quoted.Scope);
    }

    [Theory]
    [InlineData("Only if I approve later, use exact query: subject:Daisy")]
    [InlineData("Do not use exact query: subject:Daisy")]
    [InlineData("Read 'query: subject:Daisy'")]
    public void Conditional_negative_and_quoted_literal_boundaries_cannot_replace_the_current_exact_query(string instruction)
    {
        var original = ConnectorReadScope.Freeze("Search using: subject:original", Clock, Zone);
        var change = original.Refine(instruction, []);
        Assert.False(change.Accepted); Assert.Equal(ConnectorReadScopeChangeKind.InvalidQuery, change.Kind);
        Assert.Same(original, change.Scope); Assert.Equal("subject:original", change.Scope.LiteralQuery);
    }

    [Theory]
    [InlineData("Summarize my email using exact query: subject:\"BioCyclic\"")]
    [InlineData("Explain this email using literal query: subject:\"BioCyclic\"")]
    [InlineData("Can you read the email using exact query: subject:\"BioCyclic\"")]
    public void Original_literal_authority_uses_the_existing_product_parser_without_an_imperative_prefix_narrowing(string original)
    {
        Assert.True(ConnectorReadReasoner.TryLiteralMailQuery(original, out var query, out _));
        var scope = ConnectorReadScope.Freeze(original, Clock, Zone);
        Assert.Equal(query, scope.LiteralQuery); Assert.Equal(original, scope.OriginalRequest);
        Assert.Throws<ArgumentException>(() => scope.Apply("subject:other"));
    }

    [Theory]
    [InlineData("Did the professor reply today?")]
    [InlineData("Check whether the professor responded today.")]
    public void Existing_originating_today_authority_is_not_dropped_by_narrower_guidance_grammar(string original)
    {
        var previous = ConnectorMailDateScope.Freeze(original, Clock, Zone);
        Assert.NotNull(previous);
        var scope = ConnectorReadScope.Freeze(original, Clock, Zone);
        Assert.Equal(previous.Query, scope.DateScope.Query); Assert.Equal(previous.RequestUtc, scope.DateScope.RequestUtc);
    }

    [Theory]
    [InlineData("Did the professor reply yesterday?")]
    [InlineData("Check whether the professor responded yesterday.")]
    public void Originating_yesterday_questions_cannot_silently_become_unscoped_reads(string original)
    {
        var scope = ConnectorReadScope.Freeze(original, Clock, Zone);
        Assert.NotNull(scope.DateScope); Assert.Equal("2026-10-11", scope.DateScope.LocalDay);
        Assert.Equal("yesterday", scope.DateScope.RelativeDay); Assert.Equal(Clock, scope.DateScope.RequestUtc);
        Assert.Equal("(research) " + scope.DateScope.Query, scope.Apply("research"));
    }

    [Fact]
    public void Originating_yesterday_is_preserved_but_unrecognized_refinement_cannot_replace_scope()
    {
        const string text = "Something happened yesterday.";
        Assert.Equal(ConnectorReadRelativeDay.Unrecognized, ConnectorReadScope.ParseRelativeDay(text));
        var frozen = ConnectorReadScope.Freeze(text, Clock, Zone);
        Assert.Equal("2026-10-11", frozen.DateScope.LocalDay);
        Assert.Equal(ConnectorMailDateScope.FromRelativeDay(text, Clock, Zone, -1).Query, frozen.DateScope.Query);
        var original = Today(); var change = original.Refine(text, []);
        Assert.False(change.Accepted); Assert.Equal(ConnectorReadScopeChangeKind.UnsupportedDay, change.Kind);
        Assert.Same(original, change.Scope); Assert.Equal("2026-10-12", change.Scope.DateScope.LocalDay);
    }

    [Fact]
    public void An_unrecognized_broader_original_today_retains_existing_authority_but_is_not_a_new_guide_scope()
    {
        const string text = "Could there be something in my inbox today?";
        Assert.Equal(ConnectorReadRelativeDay.Unrecognized, ConnectorReadScope.ParseRelativeDay(text));
        var original = ConnectorReadScope.Freeze(text, Clock, Zone);
        Assert.Equal(ConnectorMailDateScope.Freeze(text, Clock, Zone).Query, original.DateScope.Query);
        var yesterday = Today().Refine("Only yesterday's responses", []).Scope;
        var change = yesterday.Refine(text, []);
        Assert.False(change.Accepted); Assert.Equal(ConnectorReadScopeChangeKind.UnsupportedDay, change.Kind);
        Assert.Same(yesterday, change.Scope);
    }

    [Theory]
    [InlineData("subject:(research")]
    [InlineData("subject:research\tfrom:someone")]
    public void Malformed_literal_queries_are_rejected_before_they_can_become_effective(string query)
    {
        var original = ConnectorReadScope.Freeze("Search using: subject:original", Clock, Zone);
        var change = original.Refine("Search using: " + query, []);
        Assert.False(change.Accepted); Assert.Equal(ConnectorReadScopeChangeKind.InvalidQuery, change.Kind);
        Assert.Same(original, change.Scope);
    }

    [Fact]
    public void Scope_identity_is_stable_for_a_b_a_without_mutating_clock_request_or_budgets()
    {
        var today = Today(); var yesterday = today.Refine("Only yesterday's responses", []).Scope;
        var restored = yesterday.Refine("Only today's responses", ["Only yesterday's responses"]).Scope;
        Assert.NotEqual(today.Identity, yesterday.Identity); Assert.Equal(today.Identity, restored.Identity);
        Assert.Equal(Clock, restored.RequestUtc); Assert.Equal(today.OriginalRequest, restored.OriginalRequest);
        Assert.Equal(Zone, restored.TimeZoneId);
        // No budget, account, task, credential or write field exists in this value.
    }

    [Fact]
    public void Effective_body_permission_comes_only_from_the_existing_singleton_parser_and_prior_human_history()
    {
        var original = Today(); var restricted = original.Refine("Only headers; do not read message bodies.", []);
        Assert.True(restricted.Scope.MetadataOnly);
        var history = new[] { "Only headers; do not read message bodies." };
        var day = restricted.Scope.Refine("Only yesterday's responses", history);
        Assert.True(day.Scope.MetadataOnly);
        var restored = day.Scope.Refine("Actually, read the message bodies.", history.Append("Only yesterday's responses"));
        Assert.False(restored.Scope.MetadataOnly);
        Assert.Equal(ConnectorMailContentPermission.MetadataOnly(original.OriginalRequest,
            history.Append("Only yesterday's responses").Append("Actually, read the message bodies.")), restored.Scope.MetadataOnly);
        Assert.Equal(day.Scope.DateScope.Query, restored.Scope.DateScope.Query);
    }

    [Fact]
    public void Changing_the_day_does_not_widen_an_original_explicit_sender_filter_into_whole_day_preparation()
    {
        var original = Today("Check today's emails from:professor@example.test for responses.");
        Assert.False(original.DateScope.CanInspectWholeDay);
        var changed = original.Refine("Only yesterday's professor responses", []);
        Assert.True(changed.Accepted); Assert.False(changed.Scope.DateScope.CanInspectWholeDay);
    }

    [Theory]
    [InlineData("2026-03-09T12:00:00Z", "2026-03-08", 23)]
    [InlineData("2026-11-02T12:00:00Z", "2026-11-01", 25)]
    public void Yesterday_converts_both_local_midnights_instead_of_subtracting_twenty_four_utc_hours(string request, string expectedDay, int expectedHours)
    {
        var clock = DateTimeOffset.Parse(request); var zone = EasternSynthetic();
        var scope = ConnectorMailDateScope.FromLocalDay("Only yesterday's messages", clock, zone, -1);
        Assert.Equal(expectedDay, scope.LocalDay); Assert.Equal(clock, scope.RequestUtc);
        Assert.Equal(expectedHours * 3600L, scope.BeforeUnixSeconds - scope.AfterUnixSeconds);
        var start = DateTimeOffset.FromUnixTimeSeconds(scope.AfterUnixSeconds);
        var end = DateTimeOffset.FromUnixTimeSeconds(scope.BeforeUnixSeconds);
        Assert.Equal(TimeSpan.Zero, TimeZoneInfo.ConvertTime(start, zone).TimeOfDay);
        Assert.Equal(TimeSpan.Zero, TimeZoneInfo.ConvertTime(end, zone).TimeOfDay);
    }

    [Fact]
    public void A_non_hour_offset_is_preserved_at_both_midnights()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("J2-Nepal-Synthetic", TimeSpan.FromMinutes(345), "Synthetic Nepal", "Synthetic Nepal");
        var scope = ConnectorMailDateScope.FromLocalDay("Only yesterday's messages", DateTimeOffset.Parse("2026-10-11T18:30:00Z"), zone, -1);
        Assert.Equal("2026-10-11", scope.LocalDay);
        Assert.Equal(DateTimeOffset.Parse("2026-10-10T18:15:00Z").ToUnixTimeSeconds(), scope.AfterUnixSeconds);
        Assert.Equal(DateTimeOffset.Parse("2026-10-11T18:15:00Z").ToUnixTimeSeconds(), scope.BeforeUnixSeconds);
    }

    [Theory]
    [InlineData("2026-06-01T12:00:00Z")] // Invalid beginning midnight.
    [InlineData("2026-05-31T12:00:00Z")] // Invalid ending midnight.
    [InlineData("2026-09-01T12:00:00Z")] // Ambiguous beginning midnight.
    [InlineData("2026-08-31T12:00:00Z")] // Ambiguous ending midnight.
    public void Either_invalid_or_ambiguous_midnight_fails_closed_without_assuming_an_offset(string request)
        => Assert.Throws<ArgumentException>(() => ConnectorMailDateScope.FromLocalDay("Today messages", DateTimeOffset.Parse(request), MidnightTransitionSynthetic(), 0));

    [Theory]
    [InlineData(1)]
    [InlineData(-2)]
    public void The_factory_cannot_become_a_general_relative_date_framework(int offset)
        => Assert.Throws<ArgumentException>(() => ConnectorMailDateScope.FromLocalDay("Read messages", Clock, EasternSynthetic(), offset));

    [Fact]
    public void Missing_clock_unknown_zone_and_invalid_future_day_refinement_do_not_use_machine_defaults()
    {
        Assert.Throws<ArgumentException>(() => ConnectorMailDateScope.FromRelativeDay("Today messages", default, Zone, 0));
        Assert.Throws<ArgumentException>(() => ConnectorMailDateScope.FromRelativeDay("Today messages", Clock, "J2-No-Such-Timezone", 0));
        var original = ConnectorReadScope.Freeze("Find professor responses", Clock, "J2-No-Such-Timezone");
        var change = original.Refine("Only yesterday's responses", []);
        Assert.False(change.Accepted); Assert.Equal(ConnectorReadScopeChangeKind.InvalidLocalDay, change.Kind); Assert.Same(original, change.Scope);
    }

    [Fact]
    public void Existing_today_factory_and_query_idempotence_remain_compatible()
    {
        var existing = ConnectorMailDateScope.Freeze("Check today's emails", Clock, Zone);
        var factored = ConnectorMailDateScope.FromRelativeDay("Check today's emails", Clock, Zone, 0);
        Assert.Equal(existing, factored); Assert.Equal("today", existing.RelativeDay);
        Assert.Equal("after:1791743400 before:1791829800", existing.Query);
        var once = existing.Apply("research OR professor", false);
        Assert.Equal(once, existing.Apply(once, false));
        Assert.Equal("(research OR professor) " + existing.Query, once);
    }

    private static TimeZoneInfo EasternSynthetic()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2030, 12, 31), TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("J2-Eastern-Synthetic", TimeSpan.FromHours(-5), "Synthetic Eastern", "Synthetic Standard", "Synthetic Daylight", [rule]);
    }

    private static TimeZoneInfo MidnightTransitionSynthetic()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 6, 1);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 9, 1);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2030, 12, 31), TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("J2-Midnight-Synthetic", TimeSpan.Zero, "Synthetic Midnight", "Synthetic Standard", "Synthetic Daylight", [rule]);
    }
}
