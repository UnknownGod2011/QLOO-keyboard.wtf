namespace KeyboardWtf.Services.Connectors;

using System.Text.Json;

internal sealed partial class JarvisConnectorController
{
    // This constrains an existing resource proposal; it does not interpret a
    // new command or grant an old task authority. Explicit human URLs retain
    // their ordinary navigation path. Generic Gmail home/search URLs do too.
    private bool TryResolveProposedMailOpen(ConnectorHumanRequestSnapshot human, string proposedUrl,
        out ConnectorSessionResult context, out ConnectorResourceReference[] references, out string rejection,
        ConnectorSessionResult currentCandidates = null, JsonElement proposedRead = default)
    {
        context = null; references = null; rejection = null;
        Uri uri = null;
        string resourceId;
        if (proposedRead.ValueKind == JsonValueKind.Object && Text(proposedRead, "capability_id") is "mail_read" or "mail_thread")
        {
            // Only an explicit human opening supplies navigation authority.
            // A read proposal may select an observed ID, never execute its old task.
            if (!ConnectorFollowupIntent.IsExactOpen(human.Text)) return false;
            try
            {
                var arguments = ParseArguments(Text(proposedRead, "arguments_json"));
                if (arguments.Count != 1 || !arguments.TryGetValue("id", out var id)
                    || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())) return false;
                resourceId = id.GetString();
            }
            catch (Exception error) when (error is ArgumentException or JsonException) { return false; }
        }
        else
        {
            if (string.IsNullOrEmpty(proposedUrl) || human.Text?.Contains(proposedUrl, StringComparison.Ordinal) == true
                || !Uri.TryCreate(proposedUrl, UriKind.Absolute, out uri) || uri.Scheme != "https"
                || uri.Host != "mail.google.com" || !uri.IsDefaultPort || uri.UserInfo.Length > 0
                || !uri.AbsolutePath.StartsWith("/mail/", StringComparison.Ordinal)) return false;
            var parts = uri.Fragment.TrimStart('#').Split('/');
            if (parts.Length != 2 || parts[0] is not ("thread" or "all" or "inbox" or "sent") || parts[1].Length == 0) return false;
            resourceId = parts[1];
        }
        lock (_sync)
        {
            PruneRetainedResults();
            // No connector result means this is ordinary browser navigation.
            if (!_sessionResults.Values.Any(value => value.Provider == "gmail")) return false;
            var candidates = _sessionResults.Values.Where(value => value.Provider == "gmail" && RetainedRecoveryCurrent(value))
                .SelectMany(value => value.MailThreadReferences()
                    .Where(thread => thread.ThreadId == resourceId || thread.MessageIds.Contains(resourceId, StringComparer.Ordinal))
                    .Select(thread => (Context: value, Thread: thread)))
                .GroupBy(value => (value.Context.AccountId, value.Thread.ThreadId)).Select(group => group.Last()).ToArray();
            if (candidates.Length != 1 || candidates[0].Thread.NativeWebUrl is null)
            {
                rejection = "The proposed Gmail link does not identify one verified conversation and account in this session. Use the retained result or request a fresh read; no guessed link was opened.";
                return true;
            }
            var selected = candidates[0];
            var current = currentCandidates ?? LatestMatchingOpenResult(human.Text);
            var observed = current?.MailThreadReferences().SingleOrDefault(thread => thread.ThreadId == selected.Thread.ThreadId);
            if (observed is null || current.AccountId != selected.Context.AccountId)
            {
                rejection = "The proposed conversation is not in the current retained result. No older or guessed conversation was opened.";
                return true;
            }
            // No ID: ordinary ambiguity rules still apply. A validated ID in
            // the current positive result is a model selection, just as for a
            // native thread read; it supplies neither account nor URL authority.
            if (!current.TryResolveExactOpen(ConnectorOpenSelection.Single, out var resolved, out _))
                current = current.SelectObservedMailThread(observed.ThreadId);
            if (current is null || !current.TryResolveExactOpen(ConnectorOpenSelection.Single, out resolved, out _)
                || resolved.Length != 1 || resolved[0].Url != observed.NativeWebUrl)
            {
                rejection = "The proposed conversation conflicts with the selected or unverified result. No different conversation was opened.";
                return true;
            }
            static string Account(Uri value) => value.Query.TrimStart('?').Split('&')
                .FirstOrDefault(part => part.StartsWith("authuser=", StringComparison.Ordinal))?[9..];
            var account = uri is null ? null : Account(uri);
            if (account is not null && !int.TryParse(account, out _)
                && !string.Equals(Uri.UnescapeDataString(account), Uri.UnescapeDataString(Account(new Uri(resolved[0].Url)) ?? ""), StringComparison.OrdinalIgnoreCase))
            {
                rejection = "The proposed Gmail account differs from the verified result. No other account or guessed link was opened.";
                return true;
            }
            context = current; references = resolved;
            return true;
        }
    }
}
