using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aevatar.Foundation.Abstractions.Runtime;
using Google.Protobuf.WellKnownTypes;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

internal sealed class OrleansRuntimeFleetMembershipSnapshotSource
    : IRuntimeFleetMembershipSnapshotSource
{
    private readonly IClusterMembershipService _membership;
    private readonly IClusterManifestProvider _manifests;
    private readonly TimeProvider _clock;
    private readonly OrleansRuntimeFleetMembershipOptions _options;

    public OrleansRuntimeFleetMembershipSnapshotSource(
        IClusterMembershipService membership,
        IClusterManifestProvider manifests,
        TimeProvider? clock = null,
        OrleansRuntimeFleetMembershipOptions? options = null)
    {
        _membership = membership ?? throw new ArgumentNullException(nameof(membership));
        _manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        _clock = clock ?? TimeProvider.System;
        _options = options ?? new OrleansRuntimeFleetMembershipOptions();
    }

    public Task<RuntimeFleetMembershipSnapshot?> GetCurrentAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var membership = _membership.CurrentSnapshot;
        var manifests = _manifests.Current;
        var now = _clock.GetUtcNow();
        var active = membership.Members.Values
            .Where(static member => member.Status == SiloStatus.Active)
            .OrderBy(static member => member.SiloAddress.Endpoint.ToString(), StringComparer.Ordinal)
            .ThenBy(static member => member.SiloAddress.Generation)
            .ToArray();
        if (active.Length == 0)
            return Task.FromResult<RuntimeFleetMembershipSnapshot?>(null);
        if (!HasExactActiveManifestSet(
                active.Select(static member => member.SiloAddress),
                manifests.Silos.Keys))
        {
            return Task.FromResult<RuntimeFleetMembershipSnapshot?>(null);
        }

        var evidence = active.Select(member => ReadManifestEvidence(member, manifests)).ToArray();
        var deploymentRevision = ResolveFleetDeploymentRevision(evidence);
        var result = new RuntimeFleetMembershipSnapshot
        {
            MembershipEpoch = membership.Version.Value,
            DeploymentRevision = deploymentRevision,
            ObservedAt = Timestamp.FromDateTimeOffset(now),
            ValidUntil = Timestamp.FromDateTimeOffset(now + _options.EvidenceTtl),
        };
        foreach (var item in evidence)
        {
            var runtimeMember = new RuntimeFleetMember
            {
                MemberId = item.MemberId,
                Incarnation = item.Incarnation,
                DeploymentRevision = deploymentRevision,
            };
            runtimeMember.Capabilities.Add(item.Capabilities);
            result.ActiveMembers.Add(runtimeMember);
        }

        result.MembershipDigest = RuntimeFleetMembershipDigest.Compute(result);
        return Task.FromResult<RuntimeFleetMembershipSnapshot?>(result);
    }

    internal static bool HasExactActiveManifestSet(
        IEnumerable<SiloAddress> activeMembership,
        IEnumerable<SiloAddress> manifestSilos)
    {
        ArgumentNullException.ThrowIfNull(activeMembership);
        ArgumentNullException.ThrowIfNull(manifestSilos);
        return activeMembership.ToHashSet().SetEquals(manifestSilos);
    }

    private static ManifestEvidence ReadManifestEvidence(
        ClusterMember member,
        ClusterManifest clusterManifest)
    {
        var memberId = member.SiloAddress.Endpoint.ToString();
        var incarnation = member.SiloAddress.Generation.ToString(CultureInfo.InvariantCulture);
        if (!clusterManifest.Silos.TryGetValue(member.SiloAddress, out var siloManifest))
            return new ManifestEvidence(memberId, incarnation, "legacy-unadvertised", []);

        var marker = siloManifest.Grains.Values
            .Select(static grain => grain.Properties)
            .SingleOrDefault(properties =>
                properties.ContainsKey(RuntimeFleetCapabilityManifest.DeploymentRevisionProperty));
        if (marker == null ||
            !marker.TryGetValue(
                RuntimeFleetCapabilityManifest.DeploymentRevisionProperty,
                out var binaryRevision) ||
            string.IsNullOrWhiteSpace(binaryRevision))
        {
            return new ManifestEvidence(memberId, incarnation, "legacy-unadvertised", []);
        }

        var capabilities = new List<RuntimeFleetMemberCapability>();
        foreach (var capability in System.Enum.GetValues<RuntimeFleetCapability>())
        {
            if (capability == RuntimeFleetCapability.Unspecified)
                continue;
            if (!marker.TryGetValue(
                    RuntimeFleetCapabilityManifest.ContractIdProperty(capability),
                    out var contractId) ||
                !marker.TryGetValue(
                    RuntimeFleetCapabilityManifest.ReaderVersionProperty(capability),
                    out var versionRaw) ||
                !int.TryParse(
                    versionRaw,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var readerVersion) ||
                readerVersion <= 0 ||
                string.IsNullOrWhiteSpace(contractId))
            {
                continue;
            }

            capabilities.Add(new RuntimeFleetMemberCapability
            {
                Capability = capability,
                ReaderContractVersion = readerVersion,
                ContractId = contractId,
            });
        }

        return new ManifestEvidence(memberId, incarnation, binaryRevision, capabilities);
    }

    private static string ResolveFleetDeploymentRevision(
        IReadOnlyList<ManifestEvidence> evidence)
    {
        var revisions = evidence
            .Select(static item => item.BinaryRevision)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static revision => revision, StringComparer.Ordinal)
            .ToArray();
        if (revisions.Length == 1)
            return revisions[0];

        var payload = Encoding.UTF8.GetBytes(string.Join("\n", evidence
            .OrderBy(static item => item.MemberId, StringComparer.Ordinal)
            .ThenBy(static item => item.Incarnation, StringComparer.Ordinal)
            .Select(static item => $"{item.MemberId}\0{item.Incarnation}\0{item.BinaryRevision}")));
        return "mixed:" + Convert.ToHexStringLower(SHA256.HashData(payload));
    }

    private sealed record ManifestEvidence(
        string MemberId,
        string Incarnation,
        string BinaryRevision,
        IReadOnlyList<RuntimeFleetMemberCapability> Capabilities);
}

internal sealed class OrleansRuntimeLocalMembershipIdentityReader
    : IRuntimeLocalMembershipIdentityReader
{
    private readonly IRuntimeFleetMembershipSnapshotSource _source;
    private readonly ILocalSiloDetails _localSilo;

    public OrleansRuntimeLocalMembershipIdentityReader(
        IRuntimeFleetMembershipSnapshotSource source,
        ILocalSiloDetails localSilo)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _localSilo = localSilo ?? throw new ArgumentNullException(nameof(localSilo));
    }

    public async ValueTask<RuntimeLocalMembershipIdentity?> GetCurrentAsync(
        CancellationToken ct = default)
    {
        var snapshot = await _source.GetCurrentAsync(ct);
        if (snapshot == null)
            return null;

        var memberId = _localSilo.SiloAddress.Endpoint.ToString();
        var incarnation = _localSilo.SiloAddress.Generation.ToString(CultureInfo.InvariantCulture);
        var exact = snapshot.ActiveMembers.SingleOrDefault(member =>
            string.Equals(member.MemberId, memberId, StringComparison.Ordinal) &&
            string.Equals(member.Incarnation, incarnation, StringComparison.Ordinal));
        return exact == null
            ? null
            : new RuntimeLocalMembershipIdentity(
                snapshot.MembershipEpoch,
                snapshot.MembershipDigest,
                snapshot.DeploymentRevision,
                memberId,
                incarnation);
    }
}

internal sealed record OrleansRuntimeFleetMembershipOptions
{
    public TimeSpan EvidenceTtl { get; init; } = TimeSpan.FromSeconds(30);
}
