using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Audit.Core.Identity;
using Aevatar.Audit.Core.Projection;
using Aevatar.Audit.Core.Sanitization;
using Aevatar.Audit.Core.Stores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Aevatar.Audit.Core.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAuditTrailCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AuditActorIdentityHasherOptions>()
            .Bind(configuration.GetSection(AuditActorIdentityHasherOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton<IValidateOptions<AuditActorIdentityHasherOptions>, AuditActorIdentityHasherOptionsValidator>();
        services.TryAddSingleton<IAuditActorIdentityHasher, AuditActorIdentityHasher>();
        services.TryAddSingleton<AuditRecordSanitizer>();

        return services;
    }

    public static IServiceCollection AddInMemoryAuditTrailForDevelopment(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<InMemoryAuditTrailStore>();
        services.TryAddSingleton<IAuditTrailAppender>(static sp => sp.GetRequiredService<InMemoryAuditTrailStore>());
        services.TryAddSingleton<IAuditTrailQueryPort>(static sp => sp.GetRequiredService<InMemoryAuditTrailStore>());
        services.TryAddSingleton<IAuditTrailArtifactStore>(static sp => sp.GetRequiredService<InMemoryAuditTrailStore>());

        return services;
    }
}
