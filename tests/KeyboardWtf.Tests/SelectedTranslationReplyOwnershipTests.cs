using System.Reflection;
using System.Text.Json;
using KeyboardWtf.Services;
using NAudio.Wave;
using Xunit;

namespace KeyboardWtf.Tests;

// Replay provider frames against the actual publication boundary. These tests
// have no socket, microphone, credentials, foreground capture or clipboard.
[Collection("Live event integration")]
public sealed class SelectedTranslationReplyOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Translation_receipt_owns_a_pending_turn_until_provider_completion(bool prematureCompletion)
    {
        var ready = Signal();
        var release = Signal();
        await using var run = new EventRun(_ => TranslationReceipt(), async () =>
        {
            ready.TrySetResult(true);
            await release.Task;
        });
        try
        {
            run.Push(Tools("translation"));
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (prematureCompletion) run.Push(TurnComplete);
            Assert.True(run.TurnIsComplete);
            release.TrySetResult(true);
            await run.DrainAsync();
            Assert.True(run.TranslationOwnsReply);
            Assert.False(run.TurnIsComplete);
            run.Push(TurnComplete);
            Assert.True(run.TurnIsComplete);
        }
        finally { release.TrySetResult(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_translation_receipt_reserves_post_receipt_speech_even_when_already_complete(bool completed)
    {
        await using var run = new EventRun(_ => TranslationReceipt(completed));
        run.Push(Input("Translate the selection to French and copy it."));
        run.Push(Tools("translation"));
        await run.DrainAsync();

        Assert.True(run.TranslationOwnsReply);
        Assert.Equal(1, run.ReceiptBatches);
        run.Push(Speech("Translation has started. I will notify you."));

        Assert.Equal(0, run.Buffer.BufferedBytes);
        Assert.Equal(new[] { 0, 0, 1 }, run.Counts("PersonalProviderAudioParts"));
        Assert.Equal(new[] { 0, 0, 0 }, run.Counts("PersonalPlaybackParts"));
        Assert.Equal(new[] { 0, 0, 1 }, run.Counts("PersonalProviderOutputFrames"));
        Assert.Equal(new[] { 0, 0, 0 }, run.Counts("PersonalUiOutputFrames"));
        Assert.Equal("", run.AssistantTranscript);
    }

    [Theory]
    [InlineData("capture-failed")]
    [InlineData("clarification")]
    [InlineData("wrong-owner")]
    [InlineData("empty")]
    public async Task Failed_capture_and_clarification_receipts_do_not_hide_truthful_responses(string kind)
    {
        var receipt = kind switch
        {
            "capture-failed" => new Dictionary<string, object>
            {
                ["ok"] = false, ["started"] = false,
                ["message"] = "No current selection was found.",
            },
            "clarification" => new Dictionary<string, object>
            {
                ["ok"] = false, ["started"] = false,
                ["announcement_owner"] = "selected_text_translation_lifecycle",
                ["message"] = "Which target language?",
            },
            "wrong-owner" => new Dictionary<string, object>
            {
                ["ok"] = true, ["started"] = true,
                ["announcement_owner"] = "another_task",
            },
            _ => new Dictionary<string, object>(),
        };
        await using var run = new EventRun(_ => receipt);
        run.Push(Tools("translation"));
        await run.DrainAsync();
        Assert.False(run.TranslationOwnsReply);

        run.Push(Speech("Please select the current text and try again."));
        Assert.Equal(4800, run.Buffer.BufferedBytes);
        Assert.Equal(new[] { 0, 0, 1 }, run.Counts("PersonalPlaybackParts"));
        Assert.Equal(new[] { 0, 0, 1 }, run.Counts("PersonalUiOutputFrames"));
        Assert.NotEmpty(run.AssistantTranscript);
    }

    [Fact]
    public async Task Mixed_translation_and_unrelated_action_receipts_do_not_hide_the_other_action()
    {
        await using var run = new EventRun(name => name == "translation"
            ? TranslationReceipt()
            : new Dictionary<string, object> { ["ok"] = true, ["message"] = "Other action complete." });
        run.Push(Tools("translation", "unrelated"));
        await run.DrainAsync();

        Assert.Equal(1, run.ReceiptBatches);
        Assert.False(run.TranslationOwnsReply);
        run.Push(Speech("The other action is complete."));
        Assert.Equal(4800, run.Buffer.BufferedBytes);
    }

    [Fact]
    public async Task Turn_completion_releases_translation_reply_for_the_terminal_outcome_and_next_question()
    {
        await using var run = new EventRun(_ => TranslationReceipt(completed: true));
        run.Push(Input("Translate the selection to French and copy it."));
        run.Push(Tools("translation"));
        await run.DrainAsync();
        run.Push(Speech("I am working in the background."));
        Assert.Equal(0, run.Buffer.BufferedBytes);

        run.Push(TurnComplete);
        Assert.False(run.TranslationOwnsReply);
        // The trusted lifecycle sender starts a fresh text turn through this
        // same diagnostics boundary. Replay its provider outcome, not a socket.
        run.BeginReply();
        run.Push(Speech("Done."));
        Assert.Equal(4800, run.Buffer.BufferedBytes);
        Assert.Equal("Done.", run.AssistantTranscript);
        run.Push(TurnComplete);
        run.Buffer.ClearBuffer();

        run.Push(Input("What is two plus two?"));
        run.Push(Speech("Four."));
        Assert.Equal(4800, run.Buffer.BufferedBytes);
        Assert.Equal("Four.", run.AssistantTranscript);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Speech_interruption_or_a_new_input_releases_reply_without_cancelling_owned_translation(bool interrupted)
    {
        using var owner = new CancellationTokenSource();
        using var operation = new SelectedTextTranslationOperation("Translate and copy.", "French", owner.Token);
        var translating = Signal();
        var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var copies = 0;
        var announcements = 0;
        operation.Start("An immutable original source.", async (_, _, token) =>
        {
            translating.TrySetResult(true);
            return await finish.Task.WaitAsync(token);
        }, _ =>
        {
            Interlocked.Increment(ref copies);
            return new Dictionary<string, object> { ["ok"] = true };
        }, _ =>
        {
            Interlocked.Increment(ref announcements);
            return Task.CompletedTask;
        });
        await translating.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await using var run = new EventRun(_ => operation.Receipt());
        try
        {
            // No original input is needed here: Gemini can propose a tool
            // before its independent final input transcript arrives.
            run.Push(Tools("translation"));
            await run.DrainAsync();
            Assert.True(run.TranslationOwnsReply);
            if (interrupted)
                run.Push("""{"serverContent":{"interrupted":true}}""");
            else
                run.Push(TurnComplete);
            run.Push(Input("Can you hear me?"));

            Assert.False(run.TranslationOwnsReply);
            Assert.False(operation.IsCancelled);
            Assert.True(operation.IsRunning);
            Assert.False(owner.IsCancellationRequested);
            run.Push(Speech("Yes."));
            Assert.Equal(4800, run.Buffer.BufferedBytes);

            finish.TrySetResult("Une source originale immuable.");
            await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, copies);
            Assert.Equal(1, announcements);
            Assert.True((bool)operation.Receipt()["ok"]);
        }
        finally
        {
            finish.TrySetResult("Une source originale immuable.");
            await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task Late_fragment_of_the_original_input_does_not_release_its_translation_reply()
    {
        await using var run = new EventRun(_ => TranslationReceipt());
        run.Push(Input("Translate the selection to French"));
        run.Push(Tools("translation"));
        await run.DrainAsync();
        run.Push(Input("and copy it, please."));
        run.Push(Speech("Translation has started."));

        Assert.True(run.TranslationOwnsReply);
        Assert.Equal(0, run.Buffer.BufferedBytes);
    }

    [Fact]
    public async Task Tool_receipt_before_the_first_original_transcript_still_owns_its_reply()
    {
        await using var run = new EventRun(_ => TranslationReceipt());
        run.Push(Tools("translation"));
        await run.DrainAsync();
        run.Push(Input("Translate the selection to French and copy it."));
        run.Push(Speech("Translation has started."));
        Assert.True(run.TranslationOwnsReply);
        Assert.Equal(0, run.Buffer.BufferedBytes);
    }

    [Fact]
    public async Task Receipt_from_an_older_reply_generation_cannot_mute_a_new_interaction()
    {
        var entered = Signal();
        var release = Signal();
        await using var run = new EventRun(async _ =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return TranslationReceipt();
        });
        try
        {
            run.Push(Input("Translate and copy the selection."));
            run.Push(Tools("translation"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            run.Push(TurnComplete);
            run.Push(Input("Tell me a joke."));
            release.TrySetResult(true);
            await run.DrainAsync();

            Assert.False(run.TranslationOwnsReply);
            run.Push(Speech("A new answer."));
            Assert.Equal(4800, run.Buffer.BufferedBytes);
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task Tool_only_turn_completion_before_receipt_still_reserves_its_real_post_receipt_reply()
    {
        var entered = Signal();
        var release = Signal();
        await using var run = new EventRun(async _ =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return TranslationReceipt();
        });
        try
        {
            run.Push(Input("Translate and copy the selection."));
            run.Push(Tools("translation"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            run.Push(TurnComplete); // Not a newer user interaction.
            release.TrySetResult(true);
            await run.DrainAsync();

            Assert.True(run.TranslationOwnsReply);
            run.Push(Speech("I have started the translation."));
            Assert.Equal(0, run.Buffer.BufferedBytes);
            run.Push(TurnComplete);
            Assert.False(run.TranslationOwnsReply);
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task Silent_accepted_handoff_closes_routing_authority_while_the_owned_translation_continues()
    {
        using var owner = new CancellationTokenSource();
        using var operation = new SelectedTextTranslationOperation("Translate and copy.", "French", owner.Token);
        var entered = Signal();
        var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        operation.Start("An original source.", async (_, _, token) =>
        {
            entered.TrySetResult(true);
            return await finish.Task.WaitAsync(token);
        }, _ => new Dictionary<string, object> { ["ok"] = true }, _ => Task.CompletedTask);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await using var run = new EventRun(_ => operation.Receipt());
        try
        {
            run.Push(Input("Translate the selection and copy it."));
            run.Push(ToolWithId("first", "get_selected_text"));
            await run.DrainAsync();
            Assert.Equal(1, run.Executions);
            run.Push(TurnComplete);
            await run.DrainAsync();

            Assert.Equal(new[] { true }, run.TurnCompletions);
            Assert.True(operation.IsRunning);
            Assert.False(operation.IsCancelled);
            // The same local action in a genuinely new user request must not
            // receive the old captured/started receipt from ToolAttemptGuard.
            run.Push(Input("Translate this different selected text and copy it."));
            run.Push(ToolWithId("second", "get_selected_text"));
            await run.DrainAsync();
            Assert.Equal(2, run.Executions);
            Assert.True(operation.IsRunning);
            Assert.False(owner.IsCancellationRequested);
        }
        finally
        {
            finish.TrySetResult("Une source originale.");
            await operation.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task Replaced_physical_diagnostics_do_not_inherit_an_older_socket_reply_fence()
    {
        await using var run = new EventRun(_ => TranslationReceipt());
        run.Push(Tools("translation"));
        await run.DrainAsync();
        Assert.True(run.TranslationOwnsReply);

        run.ReplacePhysicalDiagnostics();
        Assert.False(run.TranslationOwnsReply);
        run.Push(Speech("The new connection is ready."));
        Assert.Equal(4800, run.Buffer.BufferedBytes);
        Assert.False(run.Session.IsCancellationRequested);
    }

    [Fact]
    public async Task Cancelled_receipt_is_excluded_before_reply_ownership_is_reserved()
    {
        var readyToSend = Signal();
        var send = Signal();
        await using var run = new EventRun(_ => TranslationReceipt(), async () =>
        {
            readyToSend.TrySetResult(true);
            await send.Task;
        });
        try
        {
            run.Push(Tools("translation"));
            await readyToSend.Task.WaitAsync(TimeSpan.FromSeconds(3));
            run.Push("""{"toolCallCancellation":{"ids":["translation"]}}""");
            send.TrySetResult(true);
            await run.DrainAsync();
            Assert.Equal(0, run.ReceiptBatches);
            Assert.False(run.TranslationOwnsReply);
            run.Push(Speech("The request was cancelled."));
            Assert.Equal(4800, run.Buffer.BufferedBytes);
        }
        finally { send.TrySetResult(true); }
    }

    [Fact]
    public async Task Fifty_local_translation_reply_cycles_never_publish_start_narration_or_leak_into_the_next_turn()
    {
        await using var run = new EventRun(_ => TranslationReceipt());
        for (var iteration = 0; iteration < 50; iteration++)
        {
            run.Push(Input("Translate the selected text to French and copy it."));
            run.Push(Tools($"translation-{iteration}"));
            await run.DrainAsync();
            run.Push(Speech("I have started it."));
            Assert.Equal(0, run.Buffer.BufferedBytes);
            Assert.True(run.TranslationOwnsReply);
            run.Push(TurnComplete);
            run.Push(Input("What is two plus two?"));
            run.Push(Speech("Four."));
            Assert.Equal(4800, run.Buffer.BufferedBytes);
            Assert.False(run.TranslationOwnsReply);
            run.Push(TurnComplete);
            run.Buffer.ClearBuffer();
        }
        Assert.Equal(50, run.ReceiptBatches);
    }

    private const string TurnComplete = """{"serverContent":{"turnComplete":true}}""";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Both_transport_protocols_keep_owned_handoff_silent_then_allow_the_next_question(bool personal)
    {
        await using var run = new EventRun(_ => TranslationReceipt(), personal: personal);
        run.Push(Input("Translate the selected text to French and copy it to clipboard."));
        run.Push(Tools("translation"));
        if (!personal) run.Push(TurnComplete); // Managed cross-frame batching remains intact.
        await run.DrainAsync();
        Assert.True(run.TranslationOwnsReply);
        run.Push(Speech("I started the translation and will notify you."));
        Assert.Equal(0, run.Buffer.BufferedBytes);
        Assert.Equal("", run.AssistantTranscript);
        run.Push(TurnComplete);
        await run.DrainAsync();
        Assert.False(run.TranslationOwnsReply);
        run.Push(Input("What is two plus two?"));
        await run.DrainAsync();
        run.Push(Speech("Four."));
        Assert.Equal(4800, run.Buffer.BufferedBytes);
        Assert.Equal("Four.", run.AssistantTranscript);
        Assert.Equal(1, run.Executions);
        Assert.Equal(1, run.ReceiptBatches);
    }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Dictionary<string, object> TranslationReceipt(bool completed = false) => new()
    {
        ["ok"] = true,
        ["started"] = true,
        ["completed"] = completed,
        ["announcement_owner"] = "selected_text_translation_lifecycle",
    };
    private static string Input(string text) => JsonSerializer.Serialize(new
    {
        serverContent = new { inputTranscription = new { text } },
    });
    private static string Tools(params string[] names) => JsonSerializer.Serialize(new
    {
        toolCall = new
        {
            functionCalls = names.Select(name => new { id = name, name, args = new { } }).ToArray(),
        },
    });
    private static string ToolWithId(string id, string name) => JsonSerializer.Serialize(new
    {
        toolCall = new { functionCalls = new[] { new { id, name, args = new { } } } },
    });
    private static string Speech(string text) => JsonSerializer.Serialize(new
    {
        serverContent = new
        {
            outputTranscription = new { text },
            modelTurn = new
            {
                parts = new[] { new { inlineData = new { data = Convert.ToBase64String(new byte[4800]) } } },
            },
        },
    });

    private sealed class EventRun : IAsyncDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly GeminiLiveConversationService _service = new(null, null, null);
        private readonly Type _diagnosticsType = typeof(GeminiLiveConversationService)
            .GetNestedType("ProviderSocketDiagnostics", BindingFlags.NonPublic)!;
        private readonly MethodInfo _handle = typeof(GeminiLiveConversationService)
            .GetMethod("HandleServerMessage", PrivateInstance)!;
        private readonly Func<string, Task<Dictionary<string, object>>> _execute;
        private readonly Func<Task> _beforeSend;
        private readonly bool _personal;
        private readonly List<LiveToolCallDispatcher> _dispatchers = [];
        private object _diagnostics;
        private LiveToolCallDispatcher _dispatcher;
        private int _sequence;
        public CancellationTokenSource Session { get; } = new();
        public BufferedWaveProvider Buffer { get; } = GeminiLiveConversationService.CreatePlaybackBuffer();
        public int ReceiptBatches { get; private set; }
        public int Executions { get; private set; }
        public List<bool> TurnCompletions { get; } = [];
        public bool TurnIsComplete => ((TaskCompletionSource<bool>)typeof(GeminiLiveConversationService)
            .GetField("_turnComplete", PrivateInstance)!.GetValue(_service)!).Task.IsCompleted;
        public bool TranslationOwnsReply => (bool)_diagnosticsType
            .GetProperty("TranslationOwnsReply")!.GetValue(_diagnostics)!;
        public string AssistantTranscript => typeof(GeminiLiveConversationService)
            .GetField("_currentAssistantTranscript", PrivateInstance)!.GetValue(_service)!.ToString();

        public EventRun(Func<string, Dictionary<string, object>> execute, Func<Task> beforeSend = null, bool personal = true)
            : this(name => Task.FromResult(execute(name)), beforeSend, personal) { }

        public EventRun(Func<string, Task<Dictionary<string, object>>> execute, Func<Task> beforeSend = null, bool personal = true)
        {
            _execute = execute;
            _beforeSend = beforeSend;
            _personal = personal;
            typeof(GeminiLiveConversationService).GetField("_sessionCts", PrivateInstance)!
                .SetValue(_service, Session);
            typeof(GeminiLiveConversationService).GetField("_playbackBuffer", PrivateInstance)!
                .SetValue(_service, Buffer);
            _service.SetUserTranscriptObserver((_, _) => { },
                userTurnCompletedObserver: completed => TurnCompletions.Add(completed));
            ReplacePhysicalDiagnostics();
        }

        public void ReplacePhysicalDiagnostics()
        {
            _diagnostics = Activator.CreateInstance(_diagnosticsType,
                "translation-reply-replay", ++_sequence, DateTimeOffset.UtcNow, 0, false)!;
            _diagnosticsType.GetProperty("UsesPersonalToolProtocol")!.SetValue(_diagnostics, _personal);
            var diagnostics = _diagnostics;
            _dispatcher = new LiveToolCallDispatcher(Session.Token,
                (name, _, _) =>
                {
                    Executions++;
                    return _execute(name);
                }, async (build, onSent, token) =>
                {
                    if (_beforeSend != null) await _beforeSend();
                    token.ThrowIfCancellationRequested();
                    if (build() != null)
                    {
                        ReceiptBatches++;
                        var receipts = _diagnosticsType.GetField("PersonalReceiptBatches")!;
                        receipts.SetValue(diagnostics, (int)receipts.GetValue(diagnostics)! + 1);
                    }
                    onSent();
                });
            _dispatchers.Add(_dispatcher);
            _diagnosticsType.GetProperty("ToolCalls")!.SetValue(diagnostics, _dispatcher);
        }

        public void Push(string frame) => _handle.Invoke(_service,
            [frame, Signal(), _diagnostics, Session.Token]);
        public void BeginReply() => _diagnosticsType.GetMethod("BeginReply")!.Invoke(_diagnostics, null);
        public int[] Counts(string name) => ((int[])_diagnosticsType.GetField(name)!.GetValue(_diagnostics)!).ToArray();
        public async Task DrainAsync() => await _dispatcher.Completion.WaitAsync(TimeSpan.FromSeconds(3));

        public async ValueTask DisposeAsync()
        {
            Session.Cancel();
            foreach (var dispatcher in _dispatchers) dispatcher.Dispose();
            await _service.StopAsync(showDone: false);
            _service.Dispose();
            Session.Dispose();
        }
    }
}
