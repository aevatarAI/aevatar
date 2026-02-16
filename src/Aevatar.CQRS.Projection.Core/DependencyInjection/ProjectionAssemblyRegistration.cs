using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;

namespace Aevatar.CQRS.Projection.Core.DependencyInjection;

/// <summary>
/// Shared helpers for registering projection reducer/projector extensions from assemblies.
/// </summary>
public static class ProjectionAssemblyRegistration
{
    public static void RegisterProjectionExtensionsFromAssembly(
        IServiceCollection services,
        Assembly assembly,
        Type reducerMarkerAbstraction,
        Type projectorMarkerAbstraction,
        Type reducerGenericAbstraction,
        Type projectorGenericAbstraction)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(reducerMarkerAbstraction);
        ArgumentNullException.ThrowIfNull(projectorMarkerAbstraction);
        ArgumentNullException.ThrowIfNull(reducerGenericAbstraction);
        ArgumentNullException.ThrowIfNull(projectorGenericAbstraction);

        RegisterMarkerAndGenericAbstractions(
            services,
            assembly,
            reducerMarkerAbstraction,
            reducerGenericAbstraction);
        RegisterMarkerAndGenericAbstractions(
            services,
            assembly,
            projectorMarkerAbstraction,
            projectorGenericAbstraction);
    }

    private static void RegisterMarkerAndGenericAbstractions(
        IServiceCollection services,
        Assembly assembly,
        Type markerAbstraction,
        Type genericAbstraction)
    {
        var implementationTypes = GetLoadableTypes(assembly)
            .Where(x =>
                x is { IsClass: true, IsAbstract: false } &&
                !x.ContainsGenericParameters &&
                (x.IsPublic || x.IsNestedPublic) &&
                markerAbstraction.IsAssignableFrom(x))
            .OrderBy(x => x.FullName, StringComparer.Ordinal)
            .ToList();

        foreach (var implementationType in implementationTypes)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton(markerAbstraction, implementationType));
            RegisterGenericAbstractions(services, implementationType, genericAbstraction);
        }
    }

    private static void RegisterGenericAbstractions(
        IServiceCollection services,
        Type implementationType,
        Type genericAbstraction)
    {
        var abstractionTypes = implementationType.GetInterfaces()
            .Where(x =>
                x.IsGenericType &&
                x.GetGenericTypeDefinition() == genericAbstraction)
            .Distinct()
            .ToList();

        foreach (var abstractionType in abstractionTypes)
            services.TryAddEnumerable(ServiceDescriptor.Singleton(abstractionType, implementationType));
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(x => x != null)!;
        }
    }
}
