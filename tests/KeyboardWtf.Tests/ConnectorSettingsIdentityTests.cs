namespace KeyboardWtf.Tests;

using KeyboardWtf.Services.PaidStaging;
using Xunit;

public sealed class ConnectorSettingsIdentityTests
{
    [Fact]
    public void Signed_out_has_no_identity_and_different_owners_invalidate_same_name_rows()
    {
        Assert.Equal("", PaidAccountService.ConnectorIdentityFor(null));
        Assert.Equal("", PaidAccountService.ConnectorIdentityFor(""));
        var first = PaidAccountService.ConnectorIdentityFor("private-session-one");
        Assert.Matches("^[a-f0-9]{64}$", first);
        Assert.Equal(first, PaidAccountService.ConnectorIdentityFor("private-session-one"));
        Assert.NotEqual(first, PaidAccountService.ConnectorIdentityFor("private-session-two"));
        Assert.DoesNotContain("private-session", first);
    }
}
