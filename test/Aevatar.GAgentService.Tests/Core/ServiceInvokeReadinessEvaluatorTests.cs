using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.Services;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ServiceInvokeReadinessEvaluatorTests
{
    private readonly ServiceInvokeReadinessEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_ShouldReturnReady_WhenActiveTargetRevisionAndPreparedEndpointExist()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var entries = _evaluator.Evaluate(
            [GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")],
            [Target("dep-1", "r1", "actor-1", "chat")],
            new Dictionary<string, ServiceRevisionRecordState>(StringComparer.Ordinal)
            {
                ["r1"] = PreparedRevision(identity, "r1", "chat"),
            });

        entries.Should().ContainSingle();
        entries[0].ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Ready);
        entries[0].UnavailableReason.Should().Be(ServiceInvokeUnavailableReason.Unspecified);
        entries[0].SelectedRevisionId.Should().Be("r1");
        entries[0].SelectedDeploymentId.Should().Be("dep-1");
        entries[0].SelectedActorId.Should().Be("actor-1");
    }

    [Fact]
    public void Evaluate_ShouldReturnServingTargetMissing_WhenNoActiveTargetMatchesEndpoint()
    {
        var entries = _evaluator.Evaluate(
            [GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")],
            [Target("dep-1", "r1", "actor-1", "other")],
            new Dictionary<string, ServiceRevisionRecordState>(StringComparer.Ordinal));

        entries.Should().ContainSingle();
        entries[0].ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unavailable);
        entries[0].UnavailableReason.Should().Be(ServiceInvokeUnavailableReason.ServingTargetMissing);
        entries[0].SelectedRevisionId.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ServiceRevisionStatus.Created)]
    [InlineData(ServiceRevisionStatus.PreparationFailed)]
    [InlineData(ServiceRevisionStatus.Retired)]
    public void Evaluate_ShouldReturnRevisionNotPrepared_WhenSelectedRevisionIsNotPrepared(ServiceRevisionStatus status)
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var entries = _evaluator.Evaluate(
            [GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")],
            [Target("dep-1", "r1", "actor-1", "chat")],
            new Dictionary<string, ServiceRevisionRecordState>(StringComparer.Ordinal)
            {
                ["r1"] = new()
                {
                    Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
                    Status = status,
                },
            });

        entries.Should().ContainSingle();
        entries[0].ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unavailable);
        entries[0].UnavailableReason.Should().Be(ServiceInvokeUnavailableReason.RevisionNotPrepared);
        entries[0].SelectedRevisionId.Should().Be("r1");
    }

    [Fact]
    public void Evaluate_ShouldReturnPreparedArtifactMissing_WhenArtifactDoesNotContainEndpoint()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var entries = _evaluator.Evaluate(
            [GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")],
            [Target("dep-1", "r1", "actor-1", "chat")],
            new Dictionary<string, ServiceRevisionRecordState>(StringComparer.Ordinal)
            {
                ["r1"] = PreparedRevision(identity, "r1", "other"),
            });

        entries.Should().ContainSingle();
        entries[0].ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unavailable);
        entries[0].UnavailableReason.Should().Be(ServiceInvokeUnavailableReason.PreparedArtifactMissing);
        entries[0].SelectedRevisionId.Should().Be("r1");
    }

    [Fact]
    public void Evaluate_ShouldOnlyUseCanonicalStatusesAndReasons()
    {
        var statuses = Enum.GetNames<ServiceInvokeReadinessStatus>();
        var reasons = Enum.GetNames<ServiceInvokeUnavailableReason>();

        statuses.Should().BeEquivalentTo("Unspecified", "Ready", "Unavailable");
        reasons.Should().BeEquivalentTo(
            "Unspecified",
            "ServingTargetMissing",
            "PreparedArtifactMissing",
            "RevisionNotPrepared");
    }

    private static ServiceServingTargetSpec Target(
        string deploymentId,
        string revisionId,
        string actorId,
        params string[] endpointIds)
    {
        var target = new ServiceServingTargetSpec
        {
            DeploymentId = deploymentId,
            RevisionId = revisionId,
            PrimaryActorId = actorId,
            AllocationWeight = 100,
            ServingState = ServiceServingState.Active,
        };
        target.EnabledEndpointIds.Add(endpointIds);
        return target;
    }

    private static ServiceRevisionRecordState PreparedRevision(
        ServiceIdentity identity,
        string revisionId,
        params string[] endpointIds) =>
        new()
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, revisionId),
            Status = ServiceRevisionStatus.Prepared,
            PreparedArtifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                revisionId,
                endpointIds
                    .Select(endpointId => GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: endpointId))
                    .ToArray()),
        };
}
