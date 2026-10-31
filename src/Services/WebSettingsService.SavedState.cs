namespace KeyboardWtf.Services;

using System.Net;
using System.Text;

public sealed partial class WebSettingsService
{
    private SavedStateNotifications _savedStateNotifications;

    internal const long LargeSavedSetupWarningCharacters = 25_000;

    // Measure saved executable/identity data, not timestamps/IDs or the legacy
    // duplicate projection. Live digests remain bounded and unchanged.
    internal static long SavedSetupCharacters(
        IEnumerable<JarvisWorkflowSettings> workflows, IEnumerable<LearnedMappingEntry> mappings)
    {
        long count = 0;
        foreach (var workflow in workflows ?? [])
        {
            count += workflow.Name?.Length ?? 0;
            count += (workflow.Aliases ?? []).Sum(alias => (long)(alias?.Length ?? 0));
            if (workflow.Actions?.Count > 0)
                count += workflow.Actions.Sum(action => (long)(action.Kind?.Length ?? 0) + (action.Target?.Length ?? 0));
            else
                count += (long)(workflow.Apps?.Length ?? 0) + (workflow.Urls?.Length ?? 0) + (workflow.Folder?.Length ?? 0);
        }
        foreach (var mapping in mappings ?? [])
            count += (long)(mapping.Alias?.Length ?? 0) + (mapping.Target?.Length ?? 0) + (mapping.DisplayName?.Length ?? 0);
        return count;
    }

    internal sealed class SavedMappingView
    {
        public string Id { get; init; }
        public string Alias { get; init; }
        public string ReadableKind { get; init; }
        public string[] ResolvedTargets { get; init; }
        public string State { get; init; }
        public string WorkflowName { get; init; }
    }

    internal static SavedMappingView[] ProjectSavedMappings(
        IReadOnlyList<LearnedMappingEntry> mappings,
        IReadOnlyList<JarvisWorkflowSettings> workflows)
    {
        return mappings.Select(mapping =>
        {
            var workflow = mapping.Kind == LearnedMappingKind.Workflow
                ? workflows.FirstOrDefault(candidate =>
                    FuzzyMatcher.Normalize(candidate.Name) == FuzzyMatcher.Normalize(mapping.Target)
                    || (candidate.Aliases ?? []).Any(alias =>
                        FuzzyMatcher.Normalize(alias) == FuzzyMatcher.Normalize(mapping.Target)))
                : null;
            var targets = mapping.Kind == LearnedMappingKind.Workflow
                ? (workflow?.Actions ?? []).Select(action => action.Target).ToArray()
                : [mapping.Target];
            return new SavedMappingView
            {
                Id = mapping.Id,
                Alias = mapping.Alias,
                ReadableKind = mapping.Kind.ToString(),
                ResolvedTargets = targets,
                State = mapping.Kind == LearnedMappingKind.Workflow && workflow == null ? "missing"
                    : targets.Length == 0 || targets.Any(SettingsService.IsUnresolvedWorkflowTarget) ? "needs-repair"
                    : "active",
                WorkflowName = workflow?.Name,
            };
        }).ToArray();
    }

    private object SavedStateSnapshot()
    {
        // A revision acquired before the records never labels an old snapshot
        // with a newer invalidation. A concurrent successful save wakes the UI.
        var revision = _savedStateNotifications?.Revision ?? 0;
        var mappings = _learnedMappings?.Snapshot() ?? Array.Empty<LearnedMappingEntry>();
        var workflows = (_settings?.Current.JarvisWorkflows ?? []).ToArray();
        return new
        {
            revision,
            learnedMappings = mappings,
            savedStateMappings = ProjectSavedMappings(mappings, workflows),
            jarvisWorkflows = workflows,
            intentMemory = _intentMemory?.Snapshot() ?? new IntentMemorySnapshot(),
            largeSavedSetup = _settings?.Current.LargeSavedSetupWarningDismissed != true
                && SavedSetupCharacters(workflows, mappings) >= LargeSavedSetupWarningCharacters,
        };
    }

    internal static bool DeleteSavedMapping(
        SettingsService settings, LearnedMappingService mappings, string reference)
    {
        var mapping = mappings?.Snapshot().FirstOrDefault(entry =>
            string.Equals(entry.Id, reference, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Alias, reference, StringComparison.OrdinalIgnoreCase));
        if (mapping?.Kind != LearnedMappingKind.Workflow)
            return mappings?.Forget(reference) ?? false;
        var workflow = (settings?.Current.JarvisWorkflows ?? []).FirstOrDefault(candidate =>
            FuzzyMatcher.Normalize(candidate.Name) == FuzzyMatcher.Normalize(mapping.Target)
            || (candidate.Aliases ?? []).Any(alias =>
                FuzzyMatcher.Normalize(alias) == FuzzyMatcher.Normalize(mapping.Target)));
        var removed = false;
        if (workflow != null)
        {
            removed = settings.DeleteWorkflow(workflow.Name);
            mappings.ForgetWorkflowReferences(workflow.Name);
            foreach (var alias in workflow.Aliases ?? []) mappings.ForgetWorkflowReferences(alias);
        }
        return mappings.Forget(mapping.Id) || removed;
    }

    internal static int ClearOrdinarySavedMappings(LearnedMappingService mappings) =>
        Enum.GetValues<LearnedMappingKind>()
            .Where(kind => kind != LearnedMappingKind.Workflow)
            .Sum(kind => mappings?.Clear(kind) ?? 0);

    private async Task ServeSavedState(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (!await ValidateStateChangingRequestAsync(request, response)) return;
        response.Headers[HttpResponseHeader.CacheControl] = "no-store";
        await WriteJson(response, SavedStateSnapshot());
    }

    private async Task ServeSavedStateEvents(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (!await ValidateStateChangingRequestAsync(request, response)) return;
        var notifications = _savedStateNotifications;
        if (notifications == null) { response.StatusCode = 503; response.Close(); return; }
        var channel = notifications.Subscribe();
        try
        {
            var cancellation = _cts.Token;
            response.ContentType = "text/event-stream";
            response.Headers[HttpResponseHeader.CacheControl] = "no-store";
            response.SendChunked = true;
            var pendingRevision = channel.Reader.ReadAsync(cancellation).AsTask();
            while (!cancellation.IsCancellationRequested)
            {
                // Transport keepalive detects closed browser connections; it
                // never polls stores or sends record names, targets or memory.
                using var keepalive = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                var tick = Task.Delay(TimeSpan.FromSeconds(30), keepalive.Token);
                var completed = await Task.WhenAny(pendingRevision, tick);
                keepalive.Cancel();
                var frame = completed == pendingRevision
                    ? $"data: {await pendingRevision}\n\n"
                    : ": keepalive\n\n";
                await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(frame), cancellation);
                await response.OutputStream.FlushAsync(cancellation);
                if (completed == pendingRevision)
                    pendingRevision = channel.Reader.ReadAsync(cancellation).AsTask();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException
            or HttpListenerException or ObjectDisposedException or System.Threading.Channels.ChannelClosedException)
        {
            // Normal page close, process shutdown or stream disconnect.
        }
        finally
        {
            notifications.Unsubscribe(channel);
            try { response.Close(); }
            catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException) { }
        }
    }
}
