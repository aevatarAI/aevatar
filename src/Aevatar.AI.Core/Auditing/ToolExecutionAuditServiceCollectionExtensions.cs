using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Core.Middleware;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Core.Auditing;

public static class ToolExecutionAuditServiceCollectionExtensions
{
    public static IServiceCollection AddToolExecutionAuditObserver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(static descriptor => descriptor.ServiceType == typeof(ToolExecutionAuditObserverRegistration)))
            return services;

        services.AddSingleton<ToolExecutionAuditObserverRegistration>();
        services.TryAddSingleton(static serviceProvider => new ToolAuditRecordFactory(
            serviceProvider.GetRequiredService<IAuditActorIdentityHasher>(),
            serviceProvider.GetService<TimeProvider>()));
        services.AddSingleton<IToolCallMiddleware>(static serviceProvider =>
        {
            var appender = serviceProvider.GetService<IAuditTrailAppender>();
            var identityHasher = serviceProvider.GetService<IAuditActorIdentityHasher>();
            if (appender is null || identityHasher is null)
                return NullToolExecutionAuditMiddleware.Instance;

            return new ToolExecutionAuditMiddleware(
                appender,
                serviceProvider.GetRequiredService<ToolAuditRecordFactory>(),
                serviceProvider.GetService<ILogger<ToolExecutionAuditMiddleware>>());
        });
        return services;
    }
}

internal sealed class ToolExecutionAuditObserverRegistration;

internal sealed class NullToolExecutionAuditMiddleware : IToolCallMiddleware
{
    public static NullToolExecutionAuditMiddleware Instance { get; } = new();

    private NullToolExecutionAuditMiddleware()
    {
    }

    public Task InvokeAsync(ToolCallContext context, Func<Task> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return next();
    }
}
