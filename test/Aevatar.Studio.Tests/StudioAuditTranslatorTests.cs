using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.GAgents.StudioTeam;
using Aevatar.Studio.Projection.Audit;
using Aevatar.Studio.Projection.DependencyInjection;
using Aevatar.Studio.Projection.Orchestration;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class StudioAuditTranslatorTests
{
    [Fact]
    public void AddStudioProjectionComponents_ShouldWireCommittedAuditMaterializerAndTranslators()
    {
        var services = new ServiceCollection();

        services.AddStudioProjectionComponents();
        using var provider = services.BuildServiceProvider();

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IProjectionArtifactMaterializer<StudioMaterializationContext>) &&
            IsObservedProjectionArtifactMaterializerFor<
                CommittedAuditArtifactMaterializer<StudioMaterializationContext>>(
                descriptor.ImplementationType));
        provider
            .GetRequiredService<CommittedAuditArtifactMaterializer<StudioMaterializationContext>>()
            .Should()
            .NotBeNull();
        provider
            .GetServices<IAuditCommittedEventTranslator>()
            .Select(static translator => translator.GetType())
            .Should()
            .Contain([
                typeof(StudioMemberCreatedAuditTranslator),
                typeof(StudioMemberImplementationUpdatedAuditTranslator),
                typeof(StudioMemberReassignedAuditTranslator),
                typeof(StudioMemberDeletedAuditTranslator),
                typeof(StudioTeamCreatedAuditTranslator),
                typeof(StudioTeamUpdatedAuditTranslator),
                typeof(StudioTeamArchivedAuditTranslator),
            ]);
    }

    [Theory]
    [MemberData(nameof(StudioSeedEvents))]
    public void StudioSeedTranslators_ShouldProduceCommittedAuditRecord(
        IAuditCommittedEventTranslator translator,
        IMessage evt,
        string operationName,
        string targetKind,
        string targetId,
        ExpectedAuditFields expected)
    {
        var record = translator.Translate(Context(), Any.Pack(evt)).Should().ContainSingle().Subject;

        record.OperationName.Should().Be(operationName);
        record.Outcome.Should().Be(AuditOutcome.Success);
        record.ActorKind.Should().Be(AuditActorKind.System);
        record.Target.Kind.Should().Be(targetKind);
        record.Target.Id.Should().Be(targetId);
        record.ScopeId.Should().Be(expected.ScopeId);
        record.SensitivityLevel.Should().Be(expected.SensitivityLevel);
        record.Correlation.CommandId.Should().Be("cmd-1");
        record.Correlation.RequestId.Should().Be("req-1");
        record.Correlation.TraceId.Should().Be("corr-1");
        record.CommittedFactRef.StateVersion.Should().Be(9);
        AssertDestructiveAnnotation(record, expected.IsDestructive);
        foreach (var annotation in expected.Annotations)
            record.Annotations.Should().Contain(annotation.Key, annotation.Value);
    }

    [Fact]
    public void StudioTranslator_ShouldReturnZeroRecords_ForWrongEventType()
    {
        new StudioMemberCreatedAuditTranslator()
            .Translate(Context(), Any.Pack(new StringValue { Value = "wrong" }))
            .Should()
            .BeEmpty();
    }

    public static IEnumerable<object[]> StudioSeedEvents()
    {
        yield return
        [
            new StudioMemberCreatedAuditTranslator(),
            new StudioMemberCreatedEvent
            {
                MemberId = "m-alpha",
                ScopeId = "scope-alpha",
                DisplayName = "member",
                PublishedServiceId = "svc-alpha",
            },
            "studio.member.created",
            "studio_member",
            "m-alpha",
            new ExpectedAuditFields(
                "scope-alpha",
                AuditSensitivityLevel.Confidential,
                false,
                EmptyAnnotations),
        ];
        yield return
        [
            new StudioMemberImplementationUpdatedAuditTranslator(),
            new StudioMemberImplementationUpdatedEvent
            {
                ImplementationKind = StudioMemberImplementationKind.Workflow,
            },
            "studio.member.implementation.updated",
            "studio_member",
            "studio-member-actor",
            new ExpectedAuditFields(
                "",
                AuditSensitivityLevel.Confidential,
                false,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["implementation_kind"] = StudioMemberImplementationKind.Workflow.ToString(),
                }),
        ];
        yield return
        [
            new StudioMemberReassignedAuditTranslator(),
            new StudioMemberReassignedEvent
            {
                MemberId = "m-alpha",
                ScopeId = "scope-alpha",
                FromTeamId = "team-old",
                ToTeamId = "team-alpha",
            },
            "studio.member.reassigned",
            "studio_member",
            "m-alpha",
            new ExpectedAuditFields(
                "scope-alpha",
                AuditSensitivityLevel.Confidential,
                false,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["from_team_id"] = "team-old",
                    ["to_team_id"] = "team-alpha",
                }),
        ];
        yield return
        [
            new StudioMemberDeletedAuditTranslator(),
            new StudioMemberDeletedEvent
            {
                MemberId = "m-alpha",
                ScopeId = "scope-alpha",
                PreviousTeamId = "team-old",
                PublishedServiceId = "svc-alpha",
            },
            "studio.member.deleted",
            "studio_member",
            "m-alpha",
            new ExpectedAuditFields(
                "scope-alpha",
                AuditSensitivityLevel.Restricted,
                true,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["previous_team_id"] = "team-old",
                    ["published_service_id"] = "svc-alpha",
                }),
        ];
        yield return
        [
            new StudioTeamCreatedAuditTranslator(),
            new StudioTeamCreatedEvent
            {
                TeamId = "team-alpha",
                ScopeId = "scope-alpha",
                DisplayName = "Team",
            },
            "studio.team.created",
            "studio_team",
            "team-alpha",
            new ExpectedAuditFields(
                "scope-alpha",
                AuditSensitivityLevel.Confidential,
                false,
                EmptyAnnotations),
        ];
        yield return
        [
            new StudioTeamUpdatedAuditTranslator(),
            new StudioTeamUpdatedEvent
            {
                TeamId = "team-alpha",
                ScopeId = "scope-alpha",
                DisplayName = "Team 2",
            },
            "studio.team.updated",
            "studio_team",
            "team-alpha",
            new ExpectedAuditFields(
                "scope-alpha",
                AuditSensitivityLevel.Confidential,
                false,
                EmptyAnnotations),
        ];
        yield return
        [
            new StudioTeamArchivedAuditTranslator(),
            new StudioTeamArchivedEvent
            {
                TeamId = "team-alpha",
                ScopeId = "scope-alpha",
            },
            "studio.team.archived",
            "studio_team",
            "team-alpha",
            new ExpectedAuditFields(
                "scope-alpha",
                AuditSensitivityLevel.Restricted,
                true,
                EmptyAnnotations),
        ];
    }

    private static IReadOnlyDictionary<string, string> EmptyAnnotations { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static void AssertDestructiveAnnotation(AuditRecord record, bool isDestructive)
    {
        if (isDestructive)
            record.Annotations.Should().Contain("is_destructive", "true");
        else
            record.Annotations.Should().NotContainKey("is_destructive");
    }

    private static CommittedAuditTranslationContext Context() =>
        new(
            new EventEnvelope { Id = "cmd-1" },
            new CommittedStateEventPublished(),
            new StateEvent
            {
                AgentId = "studio-member-actor",
                EventId = "event-1",
                Version = 9,
            },
            "studio-member-actor",
            "type.googleapis.com/test",
            DateTimeOffset.Parse("2026-07-03T09:00:00+00:00"),
            "cmd-1",
            "req-1",
            "corr-1");

    private static bool IsObservedProjectionArtifactMaterializerFor<TMaterializer>(System.Type? type)
    {
        return type?.IsGenericType == true &&
               type.Name.StartsWith("ObservedProjectionArtifactMaterializer`", StringComparison.Ordinal) &&
               type.GenericTypeArguments.Length == 2 &&
               type.GenericTypeArguments[1] == typeof(TMaterializer);
    }

    public sealed record ExpectedAuditFields(
        string ScopeId,
        AuditSensitivityLevel SensitivityLevel,
        bool IsDestructive,
        IReadOnlyDictionary<string, string> Annotations);
}
