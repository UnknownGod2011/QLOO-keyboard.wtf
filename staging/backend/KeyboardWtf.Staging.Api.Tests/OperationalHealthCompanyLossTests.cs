using KeyboardWtf.Staging.Api.Services;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class OperationalHealthCompanyLossTests
{
    [Fact]
    public void Legacy_spend_and_missing_documents_keep_their_existing_total()
    {
        Assert.Equal(0, OperationalHealthService.ReadCommitted(new Dictionary<string, object>()));
        Assert.Equal(30, OperationalHealthService.ReadCommitted(new Dictionary<string, object>
        {
            ["reservedCreditMicros"] = 10L, ["settledCreditMicros"] = 20L,
        }));
    }

    [Fact]
    public void Moving_a_hold_to_company_exposure_does_not_hide_spend_from_alerts()
    {
        var data = new Dictionary<string, object>
        {
            ["reservedCreditMicros"] = 10L, ["settledCreditMicros"] = 20L,
        };
        var before = OperationalHealthService.ReadCommitted(data);
        data["reservedCreditMicros"] = 0L;
        data["companyLossExposureCreditMicros"] = 10L;
        Assert.Equal(before, OperationalHealthService.ReadCommitted(data));
        Assert.Equal(20L, data["settledCreditMicros"]); // Exposure is not a charge.
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData("10")]
    [InlineData(true)]
    public void Invalid_exposure_is_not_silently_ignored(object value)
    {
        Assert.Throws<InvalidOperationException>(() => OperationalHealthService.ReadCommitted(
            new Dictionary<string, object> { ["companyLossExposureCreditMicros"] = value }));
    }

    [Fact]
    public void Overflow_cannot_wrap_a_spend_alert_to_a_small_number()
    {
        Assert.Throws<OverflowException>(() => OperationalHealthService.ReadCommitted(new Dictionary<string, object>
        {
            ["settledCreditMicros"] = long.MaxValue, ["companyLossExposureCreditMicros"] = 1L,
        }));
    }
}
