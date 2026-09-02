using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Projection.Audit;
using Aevatar.Scripting.Projection.DependencyInjection;
using Aevatar.Scripting.Projection.Orchestration;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Scripting.Core.Tests;

public sealed class ScriptCommittedAuditTranslatorTests
{
    [Fact]
    public void AddScriptingProjectionComponents_ShouldWireCommittedAuditMaterializersAndTranslators()
    {
        var services = new ServiceCollection();

        services.AddScriptingProjectionComponents();

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IProjectionArtifactMaterializer<ScriptAuthorityProjectionContext>) &&
            IsObservedProjectionArtifactMaterializerFor<
                CommittedAuditArtifactMaterializer<ScriptAuthorityProjectionContext>>(descriptor.ImplementationType));
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IProjectionArtifactMaterializer<ScriptEvolutionMaterializationContext>) &&
            IsObservedProjectionArtifactMaterializerFor<
                CommittedAuditArtifactMaterializer<ScriptEvolutionMaterializationContext>>(descriptor.ImplementationType));
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IProjectionArtifactMaterializer<ScriptExecutionMaterializationContext>) &&
            IsObservedProjectionArtifactMaterializerFor<
                CommittedAuditArtifactMaterializer<ScriptExecutionMaterializationContext>>(descriptor.ImplementationType));

        using var provider = services.BuildServiceProvider();
        provider
            .GetServices<IAuditCommittedEventTranslator>()
            .Select(static translator => translator.GetType())
            .Should()
            .Contain([
                typeof(ScriptDefinitionUpsertedAuditTranslator),
                typeof(ScriptCatalogRevisionPromotedAuditTranslator),
                typeof(ScriptCatalogRollbackRequestedAuditTranslator),
                typeof(ScriptCatalogRolledBackAuditTranslator),
                typeof(ScriptEvolutionSessionCompletedAuditTranslator),
                typeof(ScriptRunOutcomeRecordedAuditTranslator),
            ]);
    }

    [Fact]
    public void RunOutcomeTranslator_ShouldRecordSucceededWithoutErrorBody()
    {
        var record = new ScriptRunOutcomeRecordedAuditTranslator()
            .Translate(Context(), Any.Pack(new ScriptRunOutcomeRecordedEvent
            {
                ScriptRunId = "run-7",
                Status = ScriptRunOutcomeStatus.Succeeded,
                ScriptId = "greeter",
                ScriptRevision = "rev-2",
                CommandId = "cmd-1",
                CorrelationId = "corr-1",
                CommittedFactCount = 3,
                ScopeId = "scope-1",
            }))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("script.run.outcome");
        record.Target.Kind.Should().Be("script_run");
        record.Target.Id.Should().Be("run-7");
        record.ScopeId.Should().Be("scope-1");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Confidential);
        record.Annotations.Should().Contain("status", "succeeded");
        record.Annotations.Should().Contain("has_error", "false");
        record.Annotations.Should().Contain("committed_fact_count", "3");
    }

    [Fact]
    public void RunOutcomeTranslator_ShouldRecordFailureClassNotErrorBody()
    {
        var record = new ScriptRunOutcomeRecordedAuditTranslator()
            .Translate(Context(), Any.Pack(new ScriptRunOutcomeRecordedEvent
            {
                ScriptRunId = "run-8",
                Status = ScriptRunOutcomeStatus.Failed,
                Error = "sensitive business payload leaked in error text",
                ScriptId = "greeter",
                ScopeId = "scope-1",
            }))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("script.run.outcome");
        record.Target.Id.Should().Be("run-8");
        record.Annotations.Should().Contain("status", "failed");
        record.Annotations.Should().Contain("has_error", "true");
        record.Annotations.Values.Should().NotContain(value =>
            value.Contains("sensitive business payload", StringComparison.Ordinal));
        record.ResultSummary.Should().NotContain("sensitive business payload");
    }

    [Fact]
    public void DefinitionUpsertedTranslator_ShouldProduceRecordWithScopeAndRevision()
    {
        var record = new ScriptDefinitionUpsertedAuditTranslator()
            .Translate(Context(), Any.Pack(new ScriptDefinitionUpsertedEvent
            {
                ScriptId = "greeter",
                ScriptRevision = "rev-2",
                SourceHash = "sha-abc",
                ScopeId = "scope-1",
            }))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("script.definition.upserted");
        record.Target.Kind.Should().Be("script_definition");
        record.Target.Id.Should().Be("greeter");
        record.ScopeId.Should().Be("scope-1");
        record.Annotations.Should().Contain("revision", "rev-2");
        record.Annotations.Should().Contain("source_hash", "sha-abc");
    }

    [Fact]
    public void CatalogRolledBackTranslator_ShouldBeDestructiveAndRestricted()
    {
        var record = new ScriptCatalogRolledBackAuditTranslator()
            .Translate(Context(), Any.Pack(new ScriptCatalogRolledBackEvent
            {
                ScriptId = "greeter",
                TargetRevision = "rev-1",
                PreviousRevision = "rev-2",
                ProposalId = "prop-9",
                ScopeId = "scope-1",
            }))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("script.catalog.rolled-back");
        record.Target.Id.Should().Be("greeter");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Restricted);
        record.Annotations.Should().Contain("is_destructive", "true");
        record.Annotations.Should().Contain("target_revision", "rev-1");
        record.Annotations.Should().Contain("previous_revision", "rev-2");
    }

    [Fact]
    public void EvolutionSessionCompletedTranslator_ShouldRecordDecisionAsRestricted()
    {
        var record = new ScriptEvolutionSessionCompletedAuditTranslator()
            .Translate(Context(), Any.Pack(new ScriptEvolutionSessionCompletedEvent
            {
                ProposalId = "prop-9",
                Accepted = false,
                Status = "rejected",
                FailureReason = "candidate failed validation",
                DefinitionActorId = "def-actor",
                CatalogActorId = "cat-actor",
                ScopeId = "scope-1",
            }))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("script.evolution.session.completed");
        record.Target.Kind.Should().Be("script_evolution_session");
        record.Target.Id.Should().Be("prop-9");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Restricted);
        record.Annotations.Should().Contain("accepted", "false");
        record.Annotations.Should().Contain("status", "rejected");
        record.Annotations.Should().Contain("failure_reason", "candidate failed validation");
    }

    [Fact]
    public void Translator_ShouldReturnZeroRecords_ForWrongEventType()
    {
        new ScriptDefinitionUpsertedAuditTranslator()
            .Translate(Context(), Any.Pack(new StringValue { Value = "wrong" }))
            .Should()
            .BeEmpty();
    }

    private static CommittedAuditTranslationContext Context() =>
        new(
            new EventEnvelope { Id = "cmd-1" },
            new CommittedStateEventPublished(),
            new StateEvent { AgentId = "script-actor", EventId = "event-1", Version = 5 },
            "script-actor",
            "type.googleapis.com/test",
            DateTimeOffset.Parse("2026-07-10T09:00:00+00:00"),
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
}
