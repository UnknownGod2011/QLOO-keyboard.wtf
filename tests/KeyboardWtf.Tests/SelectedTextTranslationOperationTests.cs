using KeyboardWtf.Services;
using KeyboardWtf.Services.Ai.Backends;
using System.Collections.Concurrent;
using KeyboardWtf.Models;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class SelectedTextTranslationOperationTests
{
    [Fact]
    public async Task Temporary_503_retries_only_the_failed_part_before_one_verified_copy()
    {
        using var operation = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
        var calls = new List<string>(); var copies = 0; var states = new List<string>();
        var first = new string('a', 50_000);
        operation.Start(first + "b", (text, _, _) =>
        {
            calls.Add(text);
            if (calls.Count == 2) throw new PersonalAiException(PersonalAiFailure.Provider) { HttpStatusCode = 503 };
            return Task.FromResult(text == first ? "Première partie" : "Fin");
        }, text => { copies++; Assert.Contains("Fin", text); return new() { ["ok"] = true, ["clipboard_verified"] = true }; },
            _ => Task.CompletedTask, states.Add);
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { first, "b", "b" }, calls);
        Assert.Equal(1, copies);
        Assert.True(operation.Receipt()["ok"] is true);
        Assert.Contains(states, value => value?.Contains("retrying", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Null(states.Last());
    }

    [Theory]
    [InlineData(503, 2)]
    [InlineData(429, 1)]
    [InlineData(401, 1)]
    [InlineData(400, 1)]
    public async Task Temporary_translation_recovery_is_bounded_and_does_not_publish_failure(int status, int expectedCalls)
    {
        using var operation = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
        var calls = 0; var copies = 0;
        operation.Start("source", (_, _, _) =>
        { calls++; throw new PersonalAiException(status == 429 ? PersonalAiFailure.Quota : PersonalAiFailure.Provider) { HttpStatusCode = status }; },
            _ => { copies++; return new() { ["ok"] = true }; }, _ => Task.CompletedTask);
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expectedCalls, calls);
        Assert.Equal(0, copies);
        Assert.True(operation.Receipt()["ok"] is false);
    }

    [Fact]
    public async Task Cancel_during_temporary_translation_recovery_prevents_another_request()
    {
        using var operation = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
        var calls = 0; var copies = 0; var retrySeen = false;
        operation.Start("source", (_, _, _) =>
        { calls++; throw new PersonalAiException(PersonalAiFailure.Provider) { HttpStatusCode = 503 }; },
            _ => { copies++; return new() { ["ok"] = true }; }, _ => Task.CompletedTask,
            status => { if (status?.Contains("retrying", StringComparison.OrdinalIgnoreCase) == true) { retrySeen = true; operation.Cancel(); } });
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(retrySeen);
        Assert.True(operation.IsCancelled);
        Assert.Equal(1, calls);
        Assert.Equal(0, copies);
    }

    [Theory]
    [InlineData("Thanks. Do me a favor: translate the selected text to French and copy it.")]
    [InlineData("Hi Jarvis, could you translate it to French and copy it to my clipboard?")]
    [InlineData("I selected some text. Would you translate it to French, then put the translation on the clipboard?")]
    [InlineData("Can you translate all of the selected text into French and copy the entire translation to clipboard?")]
    [InlineData("Hello there! Please translate the text I selected into French. Can you copy the result to my clipboard?")]
    public void Capture_boundary_resolves_transform_clauses_without_a_whole_sentence_template(string request)
    {
        Assert.True(SelectedTextTranslationOperation.TryResolveClipboardTranslationIntent(request, out var language, out var recognized));
        Assert.True(recognized);
        Assert.Equal("French", language);
    }

    [Theory]
    [InlineData("Translate the selected text to French.")]
    [InlineData("Explain how to translate selected text to French and copy it.")]
    [InlineData("How do I translate selected text to French and copy it?")]
    [InlineData("Can I translate selected text to French and copy it?")]
    [InlineData("Say translate selected text to French and copy it.")]
    [InlineData("When I ask, translate selected text to French and copy it.")]
    [InlineData("Do not translate selected text to French and copy it.")]
    [InlineData("'Translate selected text to French and copy it.'")]
    [InlineData("Translate the judges' names from the selected text to French and copy it.")]
    [InlineData("Translate the selected text and my notes to French and copy it.")]
    [InlineData("Translate the selected text to French and copy it into Word.")]
    [InlineData("Translate the selected text to French and copy only the judges names.")]
    [InlineData("Translate the selected text to French and copy a summary.")]
    [InlineData("Translate the selected text to French and copy it to clipboard, then submit it.")]
    [InlineData("Open a website then translate the selected text to French and copy it.")]
    public void Capture_boundary_cannot_authorize_missing_effects_or_a_different_source(string request)
    {
        Assert.False(SelectedTextTranslationOperation.TryResolveClipboardTranslationIntent(request, out _, out var recognized));
        Assert.False(recognized);
    }

    [Theory]
    [InlineData("French or German")]
    [InlineData("French and German")]
    [InlineData("Martian")]
    public void Capture_boundary_reports_target_ambiguity_instead_of_starting_or_copying(string target)
    {
        Assert.False(SelectedTextTranslationOperation.TryResolveClipboardTranslationIntent(
            $"Thanks. Translate the selected text to {target} and copy it.", out var language, out var recognized));
        Assert.True(recognized);
        Assert.Equal("", language);
    }

    [Theory]
    [InlineData("Translate the current selected text to French and copy it to my clipboard.")]
    [InlineData("Translate the currently selected text to French and copy it to my clipboard.")]
    [InlineData("Translate the fresh selection to French and copy it to my clipboard.")]
    [InlineData("Translate the new selection to French and copy it to my clipboard.")]
    [InlineData("Translate my newly selected text to French and copy it to my clipboard.")]
    [InlineData("Translate the different highlighted text to French and copy it to my clipboard.")]
    [InlineData("Hi Jarvis, can you please translate the current selected text to French and copy it to my clipboard.")]
    public void Existing_fresh_source_modifiers_create_owned_translation(string request)
    {
        Assert.True(SelectedTextTranslationOperation.TryParseRequest(request, false, out var language));
        Assert.Equal("French", language);
    }

    [Theory]
    [InlineData("Translate the current selected text to French.")]
    [InlineData("Do not translate the current selected text to French and copy it to clipboard.")]
    [InlineData("Explain how to translate the current selected text to French and copy it to clipboard.")]
    [InlineData("Translate the current selected text to French and copy it to clipboard and submit it.")]
    public void Fresh_source_modifiers_do_not_grant_missing_or_extra_effects(string request) =>
        Assert.False(SelectedTextTranslationOperation.TryParseRequest(request, false, out _));

    [Theory]
    [InlineData("Translate the selected text to French and copy it to my clipboard.", false, "French", "French")]
    [InlineData("Translate the selected text to French and copy it to my clipboard.", false, "", "French")]
    [InlineData("Hi Jarvis, can you please translate the selected text to French and copy it to my clipboard.", false, "fr", "French")]
    [InlineData("I have selected this page. Could you translate it to French and copy it to my clipboard?", false, "French", "French")]
    [InlineData("I have selected some text can you please translate it to French and copy it to my clipboard", false, "", "French")]
    [InlineData("I've highlighted the text; please translate it into Spanish. Copy the result to my clipboard.", false, "Spanish", "Spanish")]
    [InlineData("Please translate all the text I selected to French, and copy it to my clipboard.", false, "French", "French")]
    [InlineData("I need you to translate the selected page to German and copy the translation to clipboard now.", false, "German", "German")]
    [InlineData("Translate the text that I have selected into French. Can you please copy the result to the clipboard?", false, "", "French")]
    [InlineData("Translate that earlier Specimen document selection to French then copy the result to clipboard.", false, "French", "French")]
    [InlineData("Translate it to French and copy it to clipboard.", true, "", "French")]
    [InlineData("Could you translate that into Brazilian Portuguese and copy the complete translation into my clipboard please?", true, "Brazilian Portuguese", "Brazilian Portuguese")]
    public void Structured_intent_binds_natural_source_capture_and_copy_to_user_words(
        string request, bool hasOriginal, string proposedLanguage, string expected)
    {
        Assert.True(SelectedTextTranslationOperation.TryParseStructuredRequest(request, hasOriginal,
            proposedLanguage, true, out var language, out var recognized));
        Assert.True(recognized);
        Assert.Equal(expected, language);
    }

    [Theory]
    [InlineData("Translate the selected text to French and copy it to clipboard.", false)]
    [InlineData("Translate the selected text to French.", true)]
    [InlineData("Analyze the selected text and copy it to clipboard.", true)]
    [InlineData("Translate it to French and copy it to clipboard.", true)]
    [InlineData("Translate the clipboard text to French and copy it to clipboard.", true)]
    [InlineData("Translate the judges' names from the selected text to French and copy it to clipboard.", true)]
    [InlineData("Translate the selected text and my notes to French and copy it to clipboard.", true)]
    [InlineData("Explain how to translate the selected text to French and copy it to clipboard.", true)]
    [InlineData("I have selected this page. Explain how to translate it to French and copy it to clipboard.", true)]
    [InlineData("When I ask later, translate the selected text to French and copy it to clipboard.", true)]
    [InlineData("Do not translate the selected text to French and copy it to clipboard.", true)]
    [InlineData("Translate the selected text to French but don't copy it to clipboard.", true)]
    [InlineData("\"Translate the selected text to French and copy it to clipboard.\"", true)]
    [InlineData("'Translate the selected text to French and copy it to clipboard.'", true)]
    [InlineData("Translate the selected text to French and copy it to clipboard and submit it.", true)]
    [InlineData("Open a website then translate the selected text to French and copy it to clipboard.", true)]
    public void Structured_arguments_cannot_grant_missing_or_unauthorized_effects(string request, bool copyRequested)
    {
        Assert.False(SelectedTextTranslationOperation.TryParseStructuredRequest(request, false,
            "French", copyRequested, out var language, out var recognized));
        Assert.False(recognized);
        Assert.Equal("", language);
    }

    [Theory]
    [InlineData("French or German", "French")]
    [InlineData("Martian", "Martian")]
    [InlineData("French", "German")]
    [InlineData("French", "Martian")]
    [InlineData("French and send it", "French")]
    public void Authorized_transform_with_unclear_or_mismatched_target_is_recognized_but_not_started(
        string requestedLanguage, string proposedLanguage)
    {
        var request = $"Translate the selected text to {requestedLanguage} and copy it to clipboard.";
        Assert.False(SelectedTextTranslationOperation.TryParseStructuredRequest(request, false,
            proposedLanguage, true, out var language, out var recognized));
        Assert.True(recognized);
        Assert.Equal("", language);
    }

    [Fact]
    public void Structured_target_is_derived_only_from_the_translation_clause_not_pronoun_language_codes()
    {
        Assert.True(SelectedTextTranslationOperation.TryParseStructuredRequest(
            "I have selected the text. Translate it to French and copy it to clipboard.", false,
            "", true, out var language, out var recognized));
        Assert.True(recognized);
        Assert.Equal("French", language); // "it" must not be read as the Italian language code.
    }

    [Theory]
    [InlineData("french", "French")]
    [InlineData("frnehc", "French")]
    [InlineData("spnaish", "Spanish")]
    [InlineData("greman", "German")]
    [InlineData("fr", "French")]
    [InlineData("fra", "French")]
    [InlineData("Brazilian Portuguese", "Brazilian Portuguese")]
    public void Language_only_canonicalization_preserves_unambiguous_names(string supplied, string expected)
    {
        Assert.True(SelectedTextTranslationOperation.TryCanonicalLanguage(supplied, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("Martian")]
    [InlineData("f")]
    [InlineData("my project")]
    [InlineData("French and submit")]
    [InlineData("French or German")]
    [InlineData("not French")]
    public void Unknown_language_is_not_a_guessed_action_target(string supplied) =>
        Assert.False(SelectedTextTranslationOperation.TryCanonicalLanguage(supplied, out _));

    [Fact]
    public void Address_normalization_uses_configured_name_without_rewriting_raw_request()
    {
        var previous = KeyboardWtfState.AssistantName;
        try
        {
            KeyboardWtfState.AssistantName = "Ada+";
            const string request = "Hello Ada+, can you please translate the selection to French and copy it to clipboard?";
            Assert.True(SelectedTextTranslationOperation.TryParseRequest(request, false, out var language));
            Assert.Equal("French", language);
            Assert.Equal("Hi Jarvis, translate it", SelectedTextTranslationOperation.NormalizeRequest("Hi Jarvis, translate it"));
            Assert.Equal("Don't translate it", SelectedTextTranslationOperation.NormalizeRequest("Ada+, Don't translate it"));
        }
        finally { KeyboardWtfState.AssistantName = previous; }
    }

    [Fact]
    public async Task Fifty_natural_requests_each_create_one_task_one_copy_and_clear_real_status()
    {
        var wrappers = new[] { "", "Jarvis, ", "Hi Jarvis, can you please ", "Hello Jarvis! Could you ", "please " };
        for (var index = 0; index < 50; index++)
        {
            var request = wrappers[index % wrappers.Length] + "translate the selected text to french and copy it to my clipboard.";
            Assert.True(SelectedTextTranslationOperation.TryParseRequest(request, false, out var language));
            using var operation = new SelectedTextTranslationOperation(request, language, CancellationToken.None);
            var source = $"SOURCE-{index}-CURRENT";
            var states = new ConcurrentQueue<string>();
            var copies = 0;
            var announcements = 0;
            operation.Start(source, (text, target, _) =>
            {
                Assert.Equal(source, text);
                Assert.Equal("French", target);
                return Task.FromResult($"RESULT-{index}-CURRENT");
            }, output =>
            {
                Assert.Equal($"RESULT-{index}-CURRENT", output);
                copies++;
                return new() { ["ok"] = true, ["clipboard_verified"] = true };
            }, _ => { announcements++; return Task.CompletedTask; }, states.Enqueue);
            await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(request, operation.Request);
            Assert.Equal(1, copies);
            Assert.Equal(1, announcements);
            Assert.Equal(new[] { "Translating...", "Copying...", null }, states.ToArray());
            Assert.Equal(true, operation.Receipt()["completed"]);
        }
    }

    [Fact]
    public async Task Complete_large_unicode_source_is_translated_in_order_and_copied_once()
    {
        using var operation = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
        var source = string.Concat(Enumerable.Repeat("\r\nमराठी 日本語 👋\n", 5000));
        var observed = new List<string>();
        var generations = 0;
        var copies = 0;
        var states = new ConcurrentQueue<string>();
        operation.Start(source, (text, target, _) =>
        {
            observed.Add(text);
            Assert.True(text.Length <= SelectedTextTranslationOperation.TranslationPartCharacters);
            Assert.Equal("French", target);
            generations++;
            return Task.FromResult("Le texte complet.\n");
        }, text =>
        {
            Assert.Equal(string.Concat(Enumerable.Repeat("Le texte complet.\n", generations)), text);
            copies++;
            return new() { ["ok"] = true, ["clipboard_verified"] = true };
        }, _ => Task.CompletedTask, states.Enqueue);
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(generations > 1);
        Assert.Equal(source, string.Concat(observed));
        Assert.Equal(1, copies);
        Assert.Equal(true, operation.Receipt()["clipboard_verified"]);
        Assert.Equal("Translating...", states.First());
        Assert.Equal("Copying...", states.ToArray()[^2]);
        Assert.Null(states.Last());
    }

    [Fact]
    public void Parts_preserve_all_characters_and_unicode_and_paragraph_boundaries()
    {
        var source = new string('x', 49999) + "😀\r\n" + new string('z', 90000);
        var parts = SelectedTextTranslationOperation.SplitSource(source, 50000);
        Assert.Equal(source, string.Concat(parts));
        Assert.All(parts, part => { Assert.False(char.IsLowSurrogate(part[0])); Assert.False(char.IsHighSurrogate(part[^1])); });
        Assert.Equal(new[] { "a\n", "bc" }, SelectedTextTranslationOperation.SplitSource("a\nbc", 3));
    }

    [Fact]
    public void Long_paragraph_parts_prefer_a_word_boundary_without_losing_source()
    {
        var source = new string('x', 49990) + " countryside continued";
        var parts = SelectedTextTranslationOperation.SplitSource(source, 50000);
        Assert.Equal(source, string.Concat(parts));
        Assert.EndsWith(" ", parts[0]);
        Assert.StartsWith("countryside", parts[1]);
    }

    [Theory]
    [InlineData(" ", "Premier second")]
    [InlineData("\n\n", "Premier\n\nsecond")]
    public async Task Joining_translated_parts_preserves_the_source_boundary(string boundary, string expected)
    {
        using var operation = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
        var source = new string('x', 50000 - boundary.Length) + boundary + "second source";
        var calls = 0;
        var copied = "";
        operation.Start(source, (_, _, _) => Task.FromResult(++calls == 1 ? "Premier" : "second"),
            output => { copied = output; return new() { ["ok"] = true }; }, _ => Task.CompletedTask);
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(expected, copied);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Whitespace_only_part_is_preserved_without_an_invalid_provider_request()
    {
        using var operation = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
        var source = new string('x', 50000) + "\n \t";
        var calls = 0;
        var copied = "";
        operation.Start(source, (part, _, _) =>
        {
            Assert.False(string.IsNullOrWhiteSpace(part));
            calls++;
            return Task.FromResult("Texte");
        }, output => { copied = output; return new() { ["ok"] = true }; }, _ => Task.CompletedTask);
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, calls);
        Assert.Equal("Texte\n \t", copied);
        Assert.Equal(source.Length, operation.Receipt()["translated_source_characters"]);
        Assert.Equal(true, operation.Receipt()["ok"]);
    }

    [Fact]
    public async Task Failed_later_part_retains_completed_progress_without_copying_or_reporting_complete()
    {
        using var operation = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
        var calls = 0; var copies = 0;
        operation.Start(new string('x', 100001), (_, _, _) => ++calls == 1
            ? Task.FromResult("Une traduction complète de la première partie.")
            : throw new HttpRequestException("Provider unavailable"), _ => { copies++; return new() { ["ok"] = true }; }, _ => Task.CompletedTask);
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, calls);
        Assert.Equal(0, copies);
        Assert.Equal(false, operation.Receipt()["ok"]);
        Assert.Equal(true, operation.Receipt()["partial_translation_retained"]);
        Assert.Contains("première partie", operation.RetainedPartialTranslation);
    }

    [Fact]
    public async Task Available_partial_capture_is_useful_without_claiming_whole_document_coverage()
    {
        using var operation = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
        var copied = "";
        operation.Start("Captured paragraph", (_, _, _) => Task.FromResult("Paragraphe capturé"),
            text => { copied = text; return new() { ["ok"] = true, ["clipboard_verified"] = true }; },
            _ => Task.CompletedTask, sourceComplete: false);
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Paragraphe capturé", copied);
        Assert.Equal(true, operation.Receipt()["ok"]);
        Assert.Equal(false, operation.Receipt()["source_capture_complete"]);
        Assert.Contains("captured portion", operation.Receipt()["message"].ToString());
    }

    [Fact]
    public async Task Only_output_exhaustion_subdivides_and_does_not_repeat_completed_parts()
    {
        using var operation = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
        var calls = new List<string>(); var copied = "";
        operation.Start("abcd", (text, _, _) =>
        {
            calls.Add(text);
            return text == "abcd" ? throw new KeyboardWtf.Services.Ai.Backends.PersonalAiException(
                KeyboardWtf.Services.Ai.Backends.PersonalAiFailure.Truncated) : Task.FromResult(text.ToUpperInvariant());
        }, text => { copied = text; return new() { ["ok"] = true }; }, _ => Task.CompletedTask);
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { "abcd", "ab", "cd" }, calls);
        Assert.Equal("ABCD", copied);
        Assert.Equal(true, operation.Receipt()["ok"]);
    }

    [Fact]
    public async Task Status_observer_follows_the_real_translation_and_copy_lifecycle()
    {
        using var operation = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
        var states = new ConcurrentQueue<string>();
        var copies = 0;
        var announcements = 0;
        operation.Start("source", (_, _, _) =>
        {
            Assert.Equal("Translating...", states.Last());
            return Task.FromResult("translated result");
        }, _ =>
        {
            Assert.Equal("Copying...", states.Last());
            copies++;
            return new() { ["ok"] = true, ["clipboard_verified"] = true };
        }, _ => { announcements++; return Task.CompletedTask; }, states.Enqueue);
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { "Translating...", "Copying...", null }, states.ToArray());
        Assert.Equal(1, copies);
        Assert.Equal(1, announcements);
        Assert.Equal(true, operation.Receipt()["clipboard_verified"]);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("provider")]
    [InlineData("clipboard")]
    [InlineData("notification")]
    public async Task Every_terminal_path_clears_status(string failure)
    {
        using var operation = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
        var states = new ConcurrentQueue<string>();
        operation.Start("source", (_, _, _) => failure == "provider"
                ? throw new InvalidOperationException("provider failed")
                : Task.FromResult(failure == "empty" ? "" : "result"),
            _ => failure == "clipboard" ? throw new InvalidOperationException("copy failed") : new() { ["ok"] = true },
            _ => failure == "notification" ? throw new InvalidOperationException("notification failed") : Task.CompletedTask,
            states.Enqueue);
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(states.Last());
        Assert.Equal(failure == "notification", operation.Receipt()["ok"]);
        Assert.Equal(failure is "clipboard" or "notification", states.Contains("Copying..."));
    }

    [Theory]
    [InlineData("explicit")]
    [InlineData("session")]
    [InlineData("dispose")]
    public async Task Cancellation_clears_status_immediately_even_if_generation_ignores_it(string cancellation)
    {
        using var session = new CancellationTokenSource();
        using var operation = new SelectedTextTranslationOperation("request", "French", session.Token);
        var states = new ConcurrentQueue<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generation = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var copies = 0;
        operation.Start("source", (_, _, _) => { entered.SetResult(); return generation.Task; },
            _ => { copies++; return new() { ["ok"] = true }; }, _ => Task.CompletedTask, states.Enqueue);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (cancellation == "explicit") Assert.True(operation.Cancel());
        else if (cancellation == "session") session.Cancel();
        else operation.Dispose();
        Assert.Null(states.Last());
        Assert.False(operation.Completion.IsCompleted);
        generation.SetResult("late result");
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, copies);
        Assert.DoesNotContain("Copying...", states);
        Assert.Null(states.Last());
    }

    [Fact]
    public async Task Status_observer_faults_cannot_change_generation_clipboard_or_notification()
    {
        using var operation = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
        var generations = 0;
        var copies = 0;
        var announcements = 0;
        operation.Start("source", (_, _, _) => { generations++; return Task.FromResult("result"); },
            _ => { copies++; return new() { ["ok"] = true, ["clipboard_verified"] = true }; },
            _ => { announcements++; return Task.CompletedTask; }, _ => throw new InvalidOperationException("UI fault"));
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, generations);
        Assert.Equal(1, copies);
        Assert.Equal(1, announcements);
        Assert.Equal(true, operation.Receipt()["clipboard_verified"]);
        Assert.False(operation.Cancel());
    }

    [Fact]
    public async Task Dispose_before_start_clears_status_without_throwing_or_starting_work()
    {
        using var operation = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
        operation.Dispose();
        // Finish the existing disposal continuation's action synchronously:
        // this ordering must not depend on which task the scheduler runs first.
        var cancellation = (CancellationTokenSource)typeof(SelectedTextTranslationOperation)
            .GetField("_cancellation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .GetValue(operation);
        cancellation.Dispose();
        var states = new ConcurrentQueue<string>();
        var generations = 0;
        var copies = 0;
        var startFailure = Record.Exception(() => operation.Start("source",
            (_, _, _) => { generations++; return Task.FromResult("result"); },
            _ => { copies++; return new() { ["ok"] = true }; }, _ => Task.CompletedTask, states.Enqueue));
        Assert.Null(startFailure);
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(states.Last());
        Assert.Equal(0, generations);
        Assert.Equal(0, copies);
        Assert.Equal(false, operation.Receipt()["ok"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_or_session_cancel_before_start_clears_status_without_starting_work(bool explicitCancel)
    {
        using var session = new CancellationTokenSource();
        using var operation = new SelectedTextTranslationOperation("request", "French", session.Token);
        if (explicitCancel) Assert.True(operation.Cancel());
        else session.Cancel();
        var states = new ConcurrentQueue<string>();
        var generations = 0;
        var copies = 0;
        operation.Start("source", (_, _, _) => { generations++; return Task.FromResult("result"); },
            _ => { copies++; return new() { ["ok"] = true }; }, _ => Task.CompletedTask, states.Enqueue);
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(states.Last());
        Assert.DoesNotContain("Translating...", states);
        Assert.Equal(0, generations);
        Assert.Equal(0, copies);
    }

    [Fact]
    public async Task Cancellation_between_generation_and_publication_cannot_resurrect_copying_status()
    {
        using var operation = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
        var states = new ConcurrentQueue<string>();
        var copies = 0;
        operation.Start("source", (_, _, _) => Task.FromResult("result"),
            _ => { copies++; return new() { ["ok"] = true }; }, _ => Task.CompletedTask, state =>
            {
                states.Enqueue(state);
                if (state == "Copying...") Assert.True(operation.Cancel());
            });
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, copies);
        Assert.Equal(false, operation.Receipt()["ok"]);
        var allStates = states.ToArray();
        var firstClear = Array.FindIndex(allStates, state => state == null);
        Assert.True(firstClear >= 0);
        Assert.All(allStates.Skip(firstClear), Assert.Null);
    }

    [Theory]
    [InlineData("Translate the selected text to French and copy it to my clipboard.", false, "French")]
    [InlineData("Now translate the entire original selection into French and copy to the clipboard.", true, "French")]
    [InlineData("Could you please translate it to French and copy the complete translation to my clipboard?", true, "French")]
    [InlineData("Translate this whole highlighted text into Brazilian Portuguese and copy it to clipboard.", false, "Brazilian Portuguese")]
    [InlineData("Translate it, the entire original selected text, to French and copy it to my clipboard.", true, "French")]
    [InlineData("Now translate the selection to French and copy the translation to clipboard.", true, "French")]
    [InlineData("Hi Jarvis, can you please translate the selected text to French and copy it to my clipboard.", false, "French")]
    [InlineData("Jarvis, please translate the selection to spanish and copy it to clipboard.", false, "Spanish")]
    [InlineData("Hello Jarvis! Could you translate the selected text to german and copy it to the clipboard?", false, "German")]
    [InlineData("Hey Jarvis, translate the selected text to frnehc and copy it to clipboard.", false, "French")]
    [InlineData("Please translate the selected text to fr and copy it to clipboard.", false, "French")]
    public void Explicit_translation_copy_request_owns_one_complete_source(string request, bool hasOriginal, string language)
    {
        Assert.True(SelectedTextTranslationOperation.TryParseRequest(request, hasOriginal, out var actual));
        Assert.Equal(language, actual);
    }

    [Theory]
    [InlineData("Translate it to French and copy it to clipboard.", false)]
    [InlineData("Don't translate the selected text to French and copy it to clipboard.", true)]
    [InlineData("Explain how to translate the selected text to French and copy it to clipboard.", true)]
    [InlineData("Translate the selected text to French and fill the form.", true)]
    [InlineData("Translate the selected text to French and copy it to clipboard then submit the form.", true)]
    [InlineData("Translate the selected text to French and copy it to clipboard tomorrow.", true)]
    [InlineData("Translate the selected text to French without copying.", true)]
    [InlineData("Translate it, my previous answer, to French and copy it to clipboard.", true)]
    [InlineData("Translate it, the original selected text, to French and copy it to clipboard.", false)]
    [InlineData("Translate the selected text to French and copy it.", true)]
    [InlineData("Hi Jarvis, don't translate the selected text to French and copy it to clipboard.", true)]
    [InlineData("Jarvis, explain how to translate the selected text to French and copy it to clipboard.", true)]
    [InlineData("Hi Jarvis, translate the selected text to French and copy it to clipboard then submit the form.", true)]
    public void Other_or_ambiguous_requests_do_not_start_the_task(string request, bool hasOriginal) =>
        Assert.False(SelectedTextTranslationOperation.TryParseRequest(request, hasOriginal, out _));

    [Theory]
    [InlineData("Cancel.", true)]
    [InlineData("Cancel the translation.", true)]
    [InlineData("Please stop translating now.", true)]
    [InlineData("Hi Jarvis, how are you?", false)]
    [InlineData("Stop speaking.", false)]
    [InlineData("Don't cancel the translation.", false)]
    [InlineData("What does cancel the translation mean?", false)]
    [InlineData("Cancel BA.", false)]
    public void Only_explicit_task_control_cancels_translation(string request, bool expected) =>
        Assert.Equal(expected, SelectedTextTranslationOperation.IsExplicitCancellation(request));

    [Fact]
    public async Task Provider_call_cancellation_does_not_cancel_the_session_owned_generation_or_copy()
    {
        using var session = new CancellationTokenSource();
        using var proposal = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        using var operation = new SelectedTextTranslationOperation("request", "French", session.Token);
        var generation = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var copies = new List<string>();
        var announcements = 0;
        operation.Start(new string('x', 32808), async (source, language, token) =>
        {
            Assert.Equal(32808, source.Length);
            Assert.Equal("French", language);
            entered.SetResult();
            return await generation.Task.WaitAsync(token);
        }, text => { copies.Add(text); return new() { ["ok"] = true, ["clipboard_verified"] = true }; },
        _ => { announcements++; return Task.CompletedTask; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        proposal.Cancel(); // Actual provider toolCallCancellation has this ownership.
        Assert.True(operation.IsRunning);
        Assert.Equal(false, operation.Receipt()["completed"]);
        generation.SetResult("Début\nmilieu\nfin");
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Début\nmilieu\nfin", Assert.Single(copies));
        Assert.Equal(1, announcements);
        Assert.Equal(true, operation.Receipt()["completed"]);
        Assert.Equal(true, operation.Receipt()["clipboard_verified"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_cancel_or_session_end_prevents_late_copy_even_when_provider_ignores_cancellation(bool explicitCancel)
    {
        using var session = new CancellationTokenSource();
        using var operation = new SelectedTextTranslationOperation("request", "French", session.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generation = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var copies = 0;
        var announcements = 0;
        operation.Start("source", (_, _, _) => { entered.SetResult(); return generation.Task; },
            _ => { copies++; return new() { ["ok"] = true }; },
            _ => { announcements++; return Task.CompletedTask; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (explicitCancel) Assert.True(operation.Cancel());
        else session.Cancel();
        generation.SetResult("Late output");
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, copies);
        Assert.Equal(0, announcements);
        Assert.Equal(false, operation.Receipt()["ok"]);
    }

    [Fact]
    public async Task Truncated_provider_output_never_reaches_the_clipboard()
    {
        using var operation = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
        var copies = 0;
        var announcements = 0;
        operation.Start("source", (_, _, _) => throw new PersonalAiException(PersonalAiFailure.Truncated),
            _ => { copies++; return new() { ["ok"] = true }; },
            _ => { announcements++; return Task.CompletedTask; });
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, copies);
        Assert.Equal(1, announcements);
        Assert.Equal(false, operation.Receipt()["ok"]);
        Assert.Equal(true, operation.Receipt()["clipboard_unchanged"]);
    }

    [Fact]
    public async Task Translation_result_keeps_the_existing_ask_first_clipboard_boundary()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var settings = new SettingsService();
        settings.Current.JarvisPermissionMode = JarvisPermissionMode.AlwaysAsk;
        using var automation = new JarvisAutomationService(null, settings,
            new JarvisActionHistoryService(Path.Combine(directory, "history.json")), null, () => { });
        using var operation = new SelectedTextTranslationOperation("request", "French", CancellationToken.None);
        operation.Start("source", (_, _, _) => Task.FromResult("translated result"),
            output => automation.ExecuteAsync("copy_text", System.Text.Json.JsonSerializer.SerializeToElement(new { text = output }),
                CancellationToken.None).GetAwaiter().GetResult(), _ => Task.CompletedTask);
        await operation.Completion;
        var result = operation.Receipt();
        Assert.Equal(true, result["confirmation_required"]);
        Assert.False(result.ContainsKey("clipboard_verified"));
        Assert.Contains("confirm or cancel", result["message"].ToString());
        Directory.Delete(directory, recursive: true);
    }
}
