namespace KeyboardWtf.Services.Ai.Backends;

internal enum AiBackendPolicy { Managed, PersonalOnly, ManagedThenPersonal }
internal enum AiBackendOwner { Managed, Personal }
internal enum PersonalAiProvider { Gemini }
internal enum AiCapability { Text, StructuredPlanning, BrowserPlanning, Images, AudioWriting, Live }

// Deliberately contains neither credentials nor conversation content.
internal sealed record AiBackendConfiguration(
    long Revision,
    AiBackendPolicy Policy,
    PersonalAiProvider Provider = PersonalAiProvider.Gemini,
    Guid StoreId = default);

internal interface IAiBackendConfigurationSource
{
    // A damaged previously configured store must throw, never return Managed defaults.
    AiBackendConfiguration ReadConfiguration();
}

internal interface IPersonalCredentialLease : IDisposable
{
    Guid SlotId { get; }
    long CredentialRevision { get; }
}

internal interface IPersonalCredentialPool
{
    // Implementations pin a credential until all operation/transport owners release it.
    // A stale revision fails closed instead of silently using newly configured keys.
    IPersonalCredentialLease Acquire(AiBackendConfiguration expectedConfiguration);
}

internal interface IPersonalCredentialRotation
{
    // Only after definitive credential rejection, before any accepted/ambiguous work.
    IPersonalCredentialLease AcquireNext(AiBackendConfiguration frozen, Guid rejectedSlot);
}

internal static class PersonalGeminiModels
{
    // Personal voice replay A/B: this synchronous Live route avoids the
    // multi-second input/finalization lag reproduced on the 2.5 preview.
    // Managed model selection remains entirely server-owned.
    internal const string Live = "gemini-3.1-flash-live-preview";
    internal const string Text = "gemini-3.5-flash-lite";
    internal const string Reasoning = "gemini-3.5-flash";

    internal static string For(AiCapability capability) => capability switch
    {
        AiCapability.Live => Live,
        AiCapability.Images or AiCapability.StructuredPlanning => Reasoning,
        AiCapability.Text or AiCapability.BrowserPlanning or AiCapability.AudioWriting => Text,
        _ => throw new ArgumentOutOfRangeException(nameof(capability)),
    };
}

// Only the future typed reservation-rejection decoder may create this from wire data.
// HTTP status/message matching is never enough. No reservation/dispatch occurred.
internal sealed class ManagedAllowanceRefusedException : Exception
{
    internal ManagedAllowanceRefusedException()
        : base("Managed allowance could not reserve this request before provider dispatch.") { }
}
