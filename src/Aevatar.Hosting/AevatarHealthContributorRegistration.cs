using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Hosting;

public sealed class AevatarHealthContributorRegistration
{
    public required string Name { get; init; }

    public string Category { get; init; } = "dependency";

    public bool Critical { get; init; } = true;

    public IReadOnlyList<string> RequiredRoutes { get; init; } = Array.Empty<string>();

    public Func<IServiceProvider, CancellationToken, ValueTask<AevatarHealthContributorResult>>? ProbeAsync { get; init; }
}

public static class AevatarHealthContributorServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarHealthContributor(
        this IServiceCollection services,
        AevatarHealthContributorRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.Name);

        var existingRegistration = FindRegistration(services, registration.Name);
        if (existingRegistration != null)
        {
            if (!AreEquivalent(existingRegistration, registration))
            {
                throw new InvalidOperationException(
                    $"Health contributor '{registration.Name}' is already registered with a different probe.");
            }

            return services;
        }

        services.AddSingleton(registration);
        return services;
    }

    private static AevatarHealthContributorRegistration? FindRegistration(
        IServiceCollection services,
        string name)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType != typeof(AevatarHealthContributorRegistration))
                continue;

            if (descriptor.ImplementationInstance is not AevatarHealthContributorRegistration registration)
                continue;

            if (string.Equals(registration.Name, name, StringComparison.OrdinalIgnoreCase))
                return registration;
        }

        return null;
    }

    private static bool AreEquivalent(
        AevatarHealthContributorRegistration left,
        AevatarHealthContributorRegistration right) =>
        string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Category, right.Category, StringComparison.Ordinal) &&
        left.Critical == right.Critical &&
        left.RequiredRoutes.SequenceEqual(right.RequiredRoutes, StringComparer.Ordinal) &&
        AreEquivalent(left.ProbeAsync, right.ProbeAsync);

    private static bool AreEquivalent(
        Func<IServiceProvider, CancellationToken, ValueTask<AevatarHealthContributorResult>>? left,
        Func<IServiceProvider, CancellationToken, ValueTask<AevatarHealthContributorResult>>? right)
    {
        if (left == null || right == null)
            return left == right;

        return Equals(left.Method, right.Method) &&
               ReferenceEquals(left.Target, right.Target);
    }
}
