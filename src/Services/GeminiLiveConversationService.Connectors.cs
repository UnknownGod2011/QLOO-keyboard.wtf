namespace KeyboardWtf.Services;

using KeyboardWtf.Services.Connectors;

public sealed partial class GeminiLiveConversationService
{
    internal async Task<string> ReasonConnectorReadAsync(string taskId, string logicalSession,
        string instruction, string prompt, object schema, int maxTokens, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var owner = _backendOperation;
        if (!HasBoundSession || owner?.Owner is null || logicalSession != ConnectorLogicalLiveSessionId)
            throw new InvalidOperationException("No originating bound session owns this connector read.");
        using var operation = owner.CreateChild(token);
        using var scope = _paidAccount.EnterAiOperation(operation);
        // Reuse the existing structured reasoning route and settlement owner.
        // No global model default, Live setup, credential or credit policy changes.
        var response = await _paidAccount.GenerateAsync(prompt, instruction, maxTokens, schema, null, "desktop-structured", token);
        if (!response.Verified) throw new InvalidOperationException("The connector reasoning was not verified.");
        return response.Text;
    }

    // A secondary connector interpretation inherits this exact Live owner;
    // it never opens another session, rotates credentials or picks a new mode.
    internal async Task<string> InterpretConnectorMailCountAsync(ConnectorCountInterpretationInput input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var owner = _backendOperation;
        if (!HasBoundSession || owner?.Owner is null || input.LogicalLiveSessionId != ConnectorLogicalLiveSessionId)
            throw new InvalidOperationException("No originating bound session owns this connector interpretation.");
        using var operation = owner.CreateChild(token);
        using var scope = _paidAccount.EnterAiOperation(operation);
        AppLog.Info($"Connector count interpretation started: task={input.TaskId} logicalSession={input.LogicalLiveSessionId} backendOperation={operation.LogicalOperationId:N} owner={operation.Owner}");
        var response = await _paidAccount.GenerateAsync(ConnectorCountInterpretation.BuildPrompt(input),
            ConnectorCountInterpretation.Instruction, 512, ConnectorCountInterpretation.Schema, null,
            "connector-mail-count", token);
        if (!response.Verified) throw new InvalidOperationException("The connector interpretation was not verified.");
        AppLog.Info($"Connector count interpretation settled: task={input.TaskId} aiRequest={response.RequestId} owner={operation.Owner} verified=true");
        return response.Text;
    }
}
