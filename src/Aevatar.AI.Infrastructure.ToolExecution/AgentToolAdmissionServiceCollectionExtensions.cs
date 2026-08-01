using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.Infrastructure.ToolExecution;

public sealed record AgentToolAdmissionLedgerOptions(string KeyPrefix)
{
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(KeyPrefix))
        {
            throw new ArgumentException(
                "Agent tool admission key prefix must be non-empty.",
                nameof(KeyPrefix));
        }
    }
}

public sealed record AgentToolAdmissionPolicy(
    TimeSpan MaximumReplayWindow,
    TimeSpan MaximumFutureClockSkew)
{
    public static readonly TimeSpan MaximumSupportedReplayWindow = TimeSpan.FromDays(30);
    public static readonly TimeSpan DefaultMaximumRequestLifetime = TimeSpan.FromHours(24);

    public static AgentToolAdmissionPolicy Default { get; } = new(
        DefaultMaximumRequestLifetime,
        TimeSpan.FromMinutes(5));

    internal void Validate()
    {
        if (MaximumReplayWindow <= TimeSpan.Zero ||
            MaximumReplayWindow > MaximumSupportedReplayWindow)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumReplayWindow),
                $"Maximum replay window must be in (0, {MaximumSupportedReplayWindow}].");
        }

        if (MaximumFutureClockSkew < TimeSpan.Zero ||
            MaximumFutureClockSkew > MaximumReplayWindow)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumFutureClockSkew),
                "Maximum future clock skew must be non-negative and no greater than the replay window.");
        }
    }
}

public static class AgentToolAdmissionServiceCollectionExtensions
{
    public static IServiceCollection AddInMemoryAgentToolAdmissionLedger(
        this IServiceCollection services,
        AgentToolAdmissionPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        AddPolicy(services, policy);
        services.Replace(ServiceDescriptor.Singleton<
            IAgentToolAdmissionLedger,
            InMemoryAgentToolAdmissionLedger>());
        return services;
    }

    public static IServiceCollection AddGarnetAgentToolAdmissionLedger(
        this IServiceCollection services,
        AgentToolAdmissionLedgerOptions ledgerOptions,
        AgentToolAdmissionPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(ledgerOptions);
        ledgerOptions.Validate();
        AddPolicy(services, policy);
        services.Replace(ServiceDescriptor.Singleton(ledgerOptions));
        services.TryAddSingleton<IAgentToolAdmissionFactStore, GarnetAgentToolAdmissionFactStore>();
        services.Replace(ServiceDescriptor.Singleton<
            IAgentToolAdmissionLedger,
            DistributedAgentToolAdmissionLedger>());
        return services;
    }

    private static void AddPolicy(
        IServiceCollection services,
        AgentToolAdmissionPolicy? policy)
    {
        policy ??= AgentToolAdmissionPolicy.Default;
        policy.Validate();
        services.Replace(ServiceDescriptor.Singleton(policy));
    }
}
