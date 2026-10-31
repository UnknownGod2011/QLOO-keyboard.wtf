using KeyboardWtf.Staging.Api.Connectors;

namespace KeyboardWtf.Staging.Api.Services;

public sealed class MaintenanceRunner
{
    private readonly PaymentReconciliationService _payments;
    private readonly UsageReconciliationService _usage;
    private readonly LedgerInvariantService _invariants;
    private readonly OperationalHealthService _health;
    private readonly ProviderCostReconciliationService _providerCosts;
    private readonly AccountDeletionMaintenanceService _deletions;
    private readonly ILogger<MaintenanceRunner> _logger;
    private readonly ConnectorMaintenance? _connectors;

    public MaintenanceRunner(
        PaymentReconciliationService payments,
        UsageReconciliationService usage,
        LedgerInvariantService invariants,
        OperationalHealthService health,
        ProviderCostReconciliationService providerCosts,
        AccountDeletionMaintenanceService deletions,
        ILogger<MaintenanceRunner> logger,
        ConnectorMaintenance? connectors = null)
    {
        _payments = payments;
        _usage = usage;
        _invariants = invariants;
        _health = health;
        _providerCosts = providerCosts;
        _deletions = deletions;
        _logger = logger;
        _connectors = connectors;
    }

    public async Task RunAsync(string job, CancellationToken cancellationToken)
    {
        switch (job.Trim().ToLowerInvariant())
        {
            case "connector-oauth":
                if (_connectors is null) throw new InvalidOperationException("Connector cleanup is not configured.");
                await _connectors.RunAsync(cancellationToken);
                _logger.LogInformation("Connector-only maintenance command completed");
                return; // No payment/AI maintenance or unrelated health changes.
            case "payments":
                await _payments.RunAsync(cancellationToken);
                break;
            case "usage":
                await _usage.RunAsync(cancellationToken);
                break;
            case "invariants":
                await _invariants.RunAsync(cancellationToken);
                break;
            case "provider-cost":
                await _providerCosts.RunAsync(cancellationToken);
                break;
            case "account-deletion":
                await _deletions.RunAsync(cancellationToken);
                break;
            case "all":
                await _payments.RunAsync(cancellationToken);
                await _usage.RunAsync(cancellationToken);
                await _invariants.RunAsync(cancellationToken);
                await _providerCosts.RunAsync(cancellationToken);
                await _deletions.RunAsync(cancellationToken);
                break;
            default:
                throw new ArgumentException("Unknown maintenance job. Use payments, usage, invariants, provider-cost, account-deletion, or all.");
        }
        await _health.CheckAsync(cancellationToken);
        _logger.LogInformation("Maintenance command completed job={Job}", job);
    }

    internal static string? ParseJob(IEnumerable<string> args)
    {
        const string prefix = "--maintenance-job=";
        return args.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?[prefix.Length..];
    }
}
