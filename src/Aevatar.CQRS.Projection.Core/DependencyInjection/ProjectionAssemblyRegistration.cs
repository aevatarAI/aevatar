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
        Type reducerMarkerContract,
        Type projectorMarkerContract,
        Type reducerGenericContract,
        Type projectorGenericContract)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(reducerMarkerContract);
        ArgumentNullException.ThrowIfNull(projectorMarkerContract);
        ArgumentNullException.ThrowIfNull(reducerGenericContract);
        ArgumentNullException.ThrowIfNull(projectorGenericContract);

        RegisterMarkerAndGenericContracts(
            services,
            assembly,
            reducerMarkerContract,
            reducerGenericContract);
        RegisterMarkerAndGenericContracts(
            services,
            assembly,
            projectorMarkerContract,
            projectorGenericContract);
    }

    private static void RegisterMarkerAndGenericContracts(
        IServiceCollection services,
        Assembly assembly,
        Type markerContract,
        Type genericContract)
    {
        var implementationTypes = GetLoadableTypes(assembly)
            .Where(x =>
                x is { IsClass: true, IsAbstract: false } &&
                !x.ContainsGenericParameters &&
                (x.IsPublic || x.IsNestedPublic) &&
                markerContract.IsAssignableFrom(x))
            .OrderBy(x => x.FullName, StringComparer.Ordinal)
            .ToList();

        foreach (var implementationType in implementationTypes)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton(markerContract, implementationType));
            RegisterGenericContracts(services, implementationType, genericContract);
        }
    }

    private static void RegisterGenericContracts(
        IServiceCollection services,
        Type implementationType,
        Type genericContract)
    {
        var contractTypes = implementationType.GetInterfaces()
            .Where(x =>
                x.IsGenericType &&
                x.GetGenericTypeDefinition() == genericContract)
            .Distinct()
            .ToList();

        foreach (var contractType in contractTypes)
            services.TryAddEnumerable(ServiceDescriptor.Singleton(contractType, implementationType));
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
