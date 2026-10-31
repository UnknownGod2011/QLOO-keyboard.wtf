using KeyboardWtf.Services.PaidStaging;

namespace KeyboardWtf.Services.Updates;

internal static class PublicUpdateEligibility
{
    internal static bool IsAllowed()
    {
#if KEYBOARD_WTF_PAID_PRODUCTION_CANDIDATE
        // Candidate symbols include the Personal candidate. No public feed can
        // replace its separate app/profile identity, regardless of environment.
        return false;
#else
        // Ordinary production ignores staging environment overrides; an actual
        // isolated staging runtime preserves registrations and stays off the
        // canonical public update channel.
        return !PaidStagingConfiguration.FromEnvironment().PreserveExistingRegistrations;
#endif
    }
}
