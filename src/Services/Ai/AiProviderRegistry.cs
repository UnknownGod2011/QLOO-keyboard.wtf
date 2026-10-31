namespace KeyboardWtf.Services.Ai;

using System.Collections.Generic;
using KeyboardWtf.Models;

/// <summary>
/// QLOO exposes Gemini Personal API only. Connector authorization is separate
/// from inference and is not represented as an alternate AI provider.
/// </summary>
internal static class AiProviderRegistry
{
    private static readonly Dictionary<AiProvider, IAiProvider> _providers = new();
    private static IAiProvider _fallback;

    /// <summary>
    /// Called once from app startup.
    /// </summary>
    public static void Initialize()
    {
        _providers.Clear();
        _fallback = new GeminiProvider();
        _providers[AiProvider.Gemini] = _fallback;

        AppLog.Info($"AI providers registered: {_providers.Count} total, {CountImplemented()} implemented");
    }

    public static IReadOnlyCollection<IAiProvider> All => _providers.Values;

    public static IAiProvider Get(AiProvider id) =>
        _providers.TryGetValue(AiProvider.Gemini, out var provider) ? provider : _fallback;

    public static IAiProvider Current => Get(KeyboardWtfState.SelectedAiProvider);

    internal static IDisposable BeginBackendScope(CancellationToken token = default) =>
        EmptyScope.Instance;

    private sealed class EmptyScope : IDisposable
    {
        internal static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }

    private static int CountImplemented()
    {
        var n = 0;
        foreach (var p in _providers.Values) if (p.IsImplemented) n++;
        return n;
    }
}
