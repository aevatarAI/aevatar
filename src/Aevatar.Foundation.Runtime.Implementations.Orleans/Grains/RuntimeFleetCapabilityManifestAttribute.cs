using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aevatar.Foundation.Abstractions.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class RuntimeFleetCapabilityManifestAttribute
    : Attribute, IGrainPropertiesProviderAttribute
{
    public void Populate(
        IServiceProvider services,
        Type grainClass,
        GrainType grainType,
        Dictionary<string, string> properties)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(grainClass);
        ArgumentNullException.ThrowIfNull(properties);

        var advertisedBy = new Dictionary<RuntimeFleetCapability, Type>();
        var manifest = new List<(RuntimeFleetMemberCapability Capability, Type ReaderImplementationType)>();
        foreach (var provider in services
                     .GetServices<IRuntimeFleetCapabilityAdvertisement>())
        {
            if (!provider.IsAvailable)
                continue;

            var capability = provider.GetCapability()?.Clone() ??
                throw new InvalidOperationException(
                    $"{provider.GetType().FullName} returned no runtime fleet capability advertisement.");
            RuntimeFleetCapabilityManifest.Validate(capability, provider.GetType());
            capability.ContractId = capability.ContractId.Trim();
            var readerImplementationType = provider.GetReaderImplementationType() ??
                throw new InvalidOperationException(
                    $"{provider.GetType().FullName} returned no fleet capability reader implementation marker.");

            if (!advertisedBy.TryAdd(capability.Capability, provider.GetType()))
            {
                throw new InvalidOperationException(
                    $"Runtime fleet capability '{capability.Capability}' is advertised by both " +
                    $"'{advertisedBy[capability.Capability].FullName}' and " +
                    $"'{provider.GetType().FullName}'. Each capability must have one manifest provider.");
            }

            manifest.Add((capability, readerImplementationType));
            properties[RuntimeFleetCapabilityManifest.ContractIdProperty(capability.Capability)] =
                capability.ContractId;
            properties[RuntimeFleetCapabilityManifest.ReaderVersionProperty(capability.Capability)] =
                capability.ReaderContractVersion.ToString(CultureInfo.InvariantCulture);
        }

        properties[RuntimeFleetCapabilityManifest.DeploymentRevisionProperty] =
            RuntimeFleetCapabilityManifest.ResolveDeploymentRevision(grainClass, manifest);
    }
}

internal static class RuntimeFleetCapabilityManifest
{
    internal const string DeploymentRevisionProperty =
        "aevatar.runtime.deployment-revision";

    internal static string ContractIdProperty(RuntimeFleetCapability capability) =>
        $"aevatar.runtime.capability.{(int)capability}.contract-id";

    internal static string ReaderVersionProperty(RuntimeFleetCapability capability) =>
        $"aevatar.runtime.capability.{(int)capability}.reader-contract-version";

    internal static string ResolveDeploymentRevision(
        Type grainClass,
        IEnumerable<(RuntimeFleetMemberCapability Capability, Type ReaderImplementationType)> manifest)
    {
        ArgumentNullException.ThrowIfNull(grainClass);
        ArgumentNullException.ThrowIfNull(manifest);

        var entries = manifest
            .Select(static entry =>
            {
                ArgumentNullException.ThrowIfNull(entry.Capability);
                ArgumentNullException.ThrowIfNull(entry.ReaderImplementationType);
                return entry;
            })
            .OrderBy(static entry => entry.Capability.Capability)
            .ThenBy(static entry => entry.Capability.ContractId, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Capability.ReaderContractVersion)
            .ThenBy(
                static entry => entry.ReaderImplementationType.FullName,
                StringComparer.Ordinal)
            .ToArray();
        var payload = new StringBuilder();
        AppendField(payload, "runtime-fleet-manifest-v1");
        AppendTypeIdentity(payload, "runtime", grainClass);
        foreach (var entry in entries)
        {
            AppendField(
                payload,
                ((int)entry.Capability.Capability).ToString(CultureInfo.InvariantCulture));
            AppendField(payload, entry.Capability.ContractId.Trim());
            AppendField(
                payload,
                entry.Capability.ReaderContractVersion.ToString(CultureInfo.InvariantCulture));
            AppendTypeIdentity(payload, "reader", entry.ReaderImplementationType);
        }

        return "manifest-v1:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())));
    }

    private static void AppendTypeIdentity(StringBuilder payload, string role, Type type)
    {
        AppendField(payload, role);
        AppendField(payload, type.Assembly.GetName().Name ?? string.Empty);
        AppendField(payload, type.Module.Name);
        AppendField(payload, type.Module.ModuleVersionId.ToString("N"));
        AppendField(payload, type.FullName ?? type.Name);
    }

    private static void AppendField(StringBuilder payload, string value) =>
        payload
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append(';');

    internal static void Validate(RuntimeFleetMemberCapability capability, Type providerType)
    {
        if (capability.Capability == RuntimeFleetCapability.Unspecified ||
            !System.Enum.IsDefined(capability.Capability) ||
            capability.ReaderContractVersion <= 0 ||
            string.IsNullOrWhiteSpace(capability.ContractId))
        {
            throw new InvalidOperationException(
                $"{providerType.FullName} returned an invalid runtime fleet capability advertisement.");
        }
    }
}
