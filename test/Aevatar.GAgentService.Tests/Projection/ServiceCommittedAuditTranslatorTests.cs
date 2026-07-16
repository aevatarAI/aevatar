using Aevatar.AI.Abstractions;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.Schedules;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Audit;
using Aevatar.GAgentService.Projection.DependencyInjection;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceCommittedAuditTranslatorTests
{
    [Fact]
    public void AddGAgentServiceProjection_ShouldWireCommittedAuditMaterializersAndTranslators()
    {
        var services = new ServiceCollection();

        services.AddGAgentServiceProjection();
        using var provider = services.BuildServiceProvider();

        AssertCommittedAuditMaterializerRegistered<ServiceCatalogProjectionContext>(services, provider);
        AssertCommittedAuditMaterializerRegistered<ServiceDeploymentCatalogProjectionContext>(services, provider);
        AssertCommittedAuditMaterializerRegistered<ServiceRevisionCatalogProjectionContext>(services, provider);
        AssertCommittedAuditMaterializerRegistered<ServiceServingSetProjectionContext>(services, provider);
        AssertCommittedAuditMaterializerRegistered<ServiceRolloutProjectionContext>(services, provider);
        AssertCommittedAuditMaterializerRegistered<ServiceRunCurrentStateProjectionContext>(services, provider);
        AssertCommittedAuditMaterializerRegistered<ScheduledDispatchProjectionContext>(services, provider);
        AssertCommittedAuditMaterializerRegistered<GAgentRunTerminalProjectionContext>(services, provider);
        provider
            .GetServices<IAuditCommittedEventTranslator>()
            .Select(static translator => translator.GetType())
            .Should()
            .Contain(typeof(RoleChatSessionCompletedAuditTranslator));
        provider
            .GetServices<IAuditCommittedEventTranslator>()
            .Select(static translator => translator.GetType())
            .Should()
            .Contain([
                typeof(ServiceRegistrationSucceededAuditTranslator),
                typeof(ServiceRegistrationFailedAuditTranslator),
                typeof(ServiceRegistrationRetiredAuditTranslator),
                typeof(ServiceRevisionPublishedAuditTranslator),
                typeof(DefaultServingRevisionChangedAuditTranslator),
                typeof(ServiceDeploymentActivatedAuditTranslator),
                typeof(ServiceDeploymentDeactivatedAuditTranslator),
                typeof(ScheduledDispatchConfiguredAuditTranslator),
                typeof(ScheduledDispatchEnabledAuditTranslator),
                typeof(ScheduledDispatchDisabledAuditTranslator),
                typeof(ScheduledDispatchDeletedAuditTranslator),
                typeof(ServiceServingSetUpdatedAuditTranslator),
                typeof(ServiceRolloutStartedAuditTranslator),
                typeof(ServiceRolloutStageAdvancedAuditTranslator),
                typeof(ServiceRolloutPausedAuditTranslator),
                typeof(ServiceRolloutResumedAuditTranslator),
                typeof(ServiceRolloutCompletedAuditTranslator),
                typeof(ServiceRolloutRolledBackAuditTranslator),
                typeof(ServiceRolloutFailedAuditTranslator),
                typeof(ServiceDefinitionCreatedAuditTranslator),
                typeof(ServiceDefinitionUpdatedAuditTranslator),
                typeof(ServiceRevisionCreatedAuditTranslator),
                typeof(ServiceRevisionRetiredAuditTranslator),
                typeof(ServiceRunRegisteredAuditTranslator),
                typeof(ServiceRunStatusUpdatedAuditTranslator),
                typeof(ScheduledDispatchFireDispatchedAuditTranslator),
                typeof(ScheduledDispatchFireFailedAuditTranslator),
            ]);
    }

    [Theory]
    [MemberData(nameof(ServiceSeedEvents))]
    public void ServiceSeedTranslators_ShouldProduceCommittedAuditRecord(
        IAuditCommittedEventTranslator translator,
        IMessage evt,
        string operationName,
        string targetKind,
        string targetId,
        ExpectedAuditFields expected)
    {
        var record = Translate(translator, evt);

        record.OperationName.Should().Be(operationName);
        AssertLifecycle(record, translator);
        record.ActorKind.Should().Be(AuditActorKind.System);
        record.Target.Kind.Should().Be(targetKind);
        record.Target.Id.Should().Be(targetId);
        record.ScopeId.Should().Be(expected.ScopeId);
        record.SensitivityLevel.Should().Be(expected.SensitivityLevel);
        record.Correlation.CommandId.Should().Be(expected.CommandId);
        record.Correlation.RequestId.Should().Be(expected.RequestId);
        record.Correlation.TraceId.Should().BeEmpty();
        record.Correlation.CorrelationId.Should().Be("corr-1");
        record.CommittedFactRef.StateVersion.Should().Be(17);
        AssertDestructiveAnnotation(record, expected.IsDestructive);
        AssertAnnotations(record, expected.Annotations);
    }

    [Fact]
    public void ServiceTranslator_ShouldReturnZeroRecords_ForWrongEventType()
    {
        var translator = new ServiceRevisionPublishedAuditTranslator();
        var records = translator.Translate(Context(), Any.Pack(new StringValue { Value = "wrong" }));

        records.Should().BeEmpty();
    }

    [Fact]
    public void ServiceRunRegisteredTranslator_ShouldRecordRunFacts()
    {
        var evt = new ServiceRunRegisteredEvent
        {
            Record = new ServiceRunRecord
            {
                RunId = "run-1",
                ServiceId = "svc-a",
                ScopeId = "scope-1",
                CommandId = "cmd-run",
                CorrelationId = "corr-run",
                TargetActorId = "target-1",
                ScheduleId = "sched-1",
                Status = ServiceRunStatus.Completed,
            },
        };

        var record = Translate(new ServiceRunRegisteredAuditTranslator(), evt);

        record.OperationName.Should().Be("service.run.registered");
        record.Target.Kind.Should().Be("service_run");
        record.Target.Id.Should().Be("run-1");
        record.ScopeId.Should().Be("scope-1");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Confidential);
        record.Correlation.CommandId.Should().Be("cmd-run");
        record.Correlation.CorrelationId.Should().Be("corr-run");
        record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Succeeded);
        record.Annotations.Should().Contain("service_id", "svc-a");
        record.Annotations.Should().Contain("status", "completed");
        record.Annotations.Should().Contain("target_actor_id", "target-1");
        record.Annotations.Should().Contain("schedule_id", "sched-1");
    }

    [Fact]
    public void ServiceRunStatusUpdatedTranslator_ShouldRecordStatusWithoutErrorBody()
    {
        var evt = new ServiceRunStatusUpdatedEvent
        {
            RunId = "run-1",
            Status = ServiceRunStatus.Failed,
            LastOutput = "sensitive business output body",
            LastError = "compactSecretToken123",
        };

        var record = Translate(new ServiceRunStatusUpdatedAuditTranslator(), evt);

        record.OperationName.Should().Be("service.run.status-updated");
        record.Target.Kind.Should().Be("service_run");
        record.Target.Id.Should().Be("run-1");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Confidential);
        record.Annotations.Should().Contain("status", "failed");
        record.Outcome.Should().Be(AuditOutcome.Error);
        record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Failed);
        record.Failure.Code.Should().Be("service_run_failed");
        record.Annotations.Should().NotContainKey("error_class");
        record.Annotations.Should().NotContainKey("last_output");
        record.Annotations.Should().NotContainKey("last_error");
        record.ToString().Should().NotContain("compactSecretToken123");
        record.Annotations.Values.Should().NotContain(value => value.Contains("business output", StringComparison.Ordinal));
        record.ResultSummary.Should().NotContain("business output");
    }

    [Fact]
    public void ScheduledDispatchFireDispatchedTranslator_ShouldRecordDispatchFacts()
    {
        var evt = new ScheduledDispatchFireDispatchedEvent
        {
            IdempotencyKey = "idem-1",
            TargetActorId = "target-1",
            CommandId = "cmd-fire",
            CorrelationId = "corr-fire",
            Manual = true,
        };

        var record = Translate(new ScheduledDispatchFireDispatchedAuditTranslator(), evt);

        record.OperationName.Should().Be("scheduled.dispatch.fire.dispatched");
        record.Target.Kind.Should().Be("scheduled_dispatch");
        record.Target.Id.Should().Be("actor-1");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Confidential);
        record.Correlation.CommandId.Should().Be("cmd-fire");
        record.Correlation.CorrelationId.Should().Be("corr-fire");
        record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Accepted);
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Unspecified);
        record.Annotations.Should().Contain("target_actor_id", "target-1");
        record.Annotations.Should().Contain("idempotency_key", "idem-1");
        record.Annotations.Should().Contain("manual", "true");
    }

    [Fact]
    public void ScheduledDispatchFireFailedTranslator_ShouldOmitErrorBody()
    {
        var evt = new ScheduledDispatchFireFailedEvent
        {
            IdempotencyKey = "idem-1",
            Error = "compactSecretToken123",
            Manual = false,
        };

        var record = Translate(new ScheduledDispatchFireFailedAuditTranslator(), evt);

        record.OperationName.Should().Be("scheduled.dispatch.fire.failed");
        record.Target.Kind.Should().Be("scheduled_dispatch");
        record.Target.Id.Should().Be("actor-1");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Confidential);
        record.Annotations.Should().NotContainKey("error_class");
        record.Annotations.Should().Contain("idempotency_key", "idem-1");
        record.Annotations.Should().Contain("manual", "false");
        record.Outcome.Should().Be(AuditOutcome.Error);
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Failed);
        record.Failure.Code.Should().Be("scheduled_dispatch_failed");
        record.ToString().Should().NotContain("compactSecretToken123");
    }

    [Fact]
    public void RoleChatSessionCompletedTranslator_ShouldRecordSafeSessionFactsWithoutContent()
    {
        var evt = new RoleChatSessionCompletedEvent
        {
            SessionId = "session-1",
            Model = "gpt-4o",
            RoleId = "role-7",
            Content = "sensitive assistant response body must not leak",
            ReasoningContent = "secret chain of thought must not leak",
            Prompt = "confidential user prompt must not leak",
            ContentEmitted = true,
            Usage = new TokenUsagePayload
            {
                PromptTokens = 11,
                CompletionTokens = 22,
                TotalTokens = 33,
            },
        };
        evt.ToolCalls.Add(new ToolCallEvent { CallId = "call-1" });
        evt.ToolReceipts.Add(new AgentToolReceipt { ToolName = "search" });
        evt.ToolReceipts.Add(new AgentToolReceipt { ToolName = "fetch" });

        var record = Translate(new RoleChatSessionCompletedAuditTranslator(), evt);

        record.OperationName.Should().Be("ai.role-session.completed");
        record.Target.Kind.Should().Be("ai_role_session");
        record.Target.Id.Should().Be("session-1");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Internal);
        record.Annotations.Should().Contain("model", "gpt-4o");
        record.Annotations.Should().Contain("role_id", "role-7");
        record.Annotations.Should().Contain("tool_call_count", "1");
        record.Annotations.Should().Contain("tool_receipt_count", "2");
        record.Annotations.Should().Contain("content_emitted", "true");
        record.Annotations.Should().Contain("prompt_token_count", "11");
        record.Annotations.Should().Contain("completion_token_count", "22");
        record.Annotations.Should().Contain("total_token_count", "33");
        record.Annotations.Should().NotContainKey("content");
        record.Annotations.Should().NotContainKey("prompt");
        record.Annotations.Should().NotContainKey("reasoning_content");
        record.Annotations.Values.Should().NotContain(value => value.Contains("must not leak", StringComparison.Ordinal));
        record.ResultSummary.Should().NotContain("must not leak");
    }

    [Fact]
    public void RoleChatSessionCompletedTranslator_ShouldReturnZeroRecords_ForWrongEventType()
    {
        var translator = new RoleChatSessionCompletedAuditTranslator();

        var records = translator.Translate(Context(), Any.Pack(new StringValue { Value = "wrong" }));

        records.Should().BeEmpty();
    }

    public static IEnumerable<object[]> ServiceSeedEvents()
    {
        var identity = new ServiceIdentity
        {
            TenantId = "tenant-a",
            AppId = "app-a",
            Namespace = "default",
            ServiceId = "svc-a",
        };

        yield return
        [
            new ServiceRegistrationSucceededAuditTranslator(),
            new ServiceRegistrationSucceededEvent
            {
                Identity = identity,
                NyxidServiceId = "nyx-svc",
                NyxidSlug = "slug",
                CredentialKid = "kid-1",
            },
            "service.registration.succeeded",
            "service",
            "svc-a",
            ServiceExpected(
                annotations: ServiceAnnotations(
                    identity,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["credential_kid"] = "kid-1",
                    })),
        ];
        yield return
        [
            new ServiceRegistrationFailedAuditTranslator(),
            new ServiceRegistrationFailedEvent
            {
                Identity = identity,
                LastError = "provider rejected",
                CredentialKid = "kid-1",
            },
            "service.registration.failed",
            "service",
            "svc-a",
            ServiceExpected(
                annotations: ServiceAnnotations(
                    identity,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["credential_kid"] = "kid-1",
                    })),
        ];
        yield return
        [
            new ServiceRegistrationRetiredAuditTranslator(),
            new ServiceRegistrationRetiredEvent
            {
                Identity = identity,
                NyxidServiceId = "nyx-svc",
                NyxidSlug = "slug",
            },
            "service.registration.retired",
            "service",
            "svc-a",
            ServiceExpected(
                AuditSensitivityLevel.Restricted,
                true,
                ServiceAnnotations(identity)),
        ];
        yield return
        [
            new ServiceRevisionPublishedAuditTranslator(),
            new ServiceRevisionPublishedEvent
            {
                Identity = identity,
                RevisionId = "rev-1",
            },
            "service.revision.published",
            "service",
            "rev-1",
            ServiceExpected(annotations: ServiceAnnotations(identity)),
        ];
        yield return
        [
            new DefaultServingRevisionChangedAuditTranslator(),
            new DefaultServingRevisionChangedEvent
            {
                Identity = identity,
                RevisionId = "rev-1",
            },
            "service.default-serving.changed",
            "service",
            "rev-1",
            ServiceExpected(annotations: ServiceAnnotations(identity)),
        ];
        yield return
        [
            new ServiceDeploymentActivatedAuditTranslator(),
            new ServiceDeploymentActivatedEvent
            {
                Identity = identity,
                DeploymentId = "dep-1",
                RevisionId = "rev-1",
                PrimaryActorId = "actor-1",
            },
            "service.deployment.activated",
            "service",
            "dep-1",
            ServiceExpected(annotations: ServiceAnnotations(identity)),
        ];
        yield return
        [
            new ServiceDeploymentDeactivatedAuditTranslator(),
            new ServiceDeploymentDeactivatedEvent
            {
                Identity = identity,
                DeploymentId = "dep-1",
                RevisionId = "rev-1",
            },
            "service.deployment.deactivated",
            "service",
            "dep-1",
            ServiceExpected(
                AuditSensitivityLevel.Restricted,
                true,
                ServiceAnnotations(identity)),
        ];
        yield return
        [
            new ScheduledDispatchConfiguredAuditTranslator(),
            new ScheduledDispatchConfiguredEvent
            {
                ScheduleId = "schedule-1",
                DisplayName = "Daily",
                Enabled = true,
                Headers =
                {
                    ["command_id"] = "cmd-from-event",
                    ["request_id"] = "req-from-event",
                    ["scope_id"] = "scope-from-event",
                },
            },
            "scheduled.dispatch.configured",
            "scheduled_dispatch",
            "schedule-1",
            new ExpectedAuditFields(
                "scope-from-event",
                AuditSensitivityLevel.Confidential,
                false,
                "cmd-from-event",
                "req-from-event",
                EmptyAnnotations),
        ];
        yield return
        [
            new ScheduledDispatchEnabledAuditTranslator(),
            new ScheduledDispatchEnabledEvent { Reason = "resume" },
            "scheduled.dispatch.enabled",
            "scheduled_dispatch",
            "actor-1",
            DefaultExpected,
        ];
        yield return
        [
            new ScheduledDispatchDisabledAuditTranslator(),
            new ScheduledDispatchDisabledEvent { Reason = "pause" },
            "scheduled.dispatch.disabled",
            "scheduled_dispatch",
            "actor-1",
            RestrictedDestructiveExpected,
        ];
        yield return
        [
            new ScheduledDispatchDeletedAuditTranslator(),
            new ScheduledDispatchDeletedEvent { Reason = "cleanup" },
            "scheduled.dispatch.deleted",
            "scheduled_dispatch",
            "actor-1",
            RestrictedDestructiveExpected,
        ];
        yield return
        [
            new ServiceServingSetUpdatedAuditTranslator(),
            new ServiceServingSetUpdatedEvent
            {
                Identity = identity,
                Generation = 3,
                RolloutId = "rollout-1",
                Reason = "promote",
            },
            "service.serving_set.updated",
            "service",
            "svc-a",
            ServiceExpected(
                AuditSensitivityLevel.Restricted,
                false,
                ServiceAnnotations(
                    identity,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["generation"] = "3",
                        ["rollout_id"] = "rollout-1",
                        ["target_count"] = "0",
                        ["reason"] = "promote",
                    })),
        ];
        yield return
        [
            new ServiceRolloutStartedAuditTranslator(),
            new ServiceRolloutStartedEvent
            {
                Identity = identity,
                Plan = new ServiceRolloutPlanSpec
                {
                    RolloutId = "rollout-1",
                    DisplayName = "Canary",
                },
            },
            "service.rollout.started",
            "service",
            "rollout-1",
            ServiceExpected(
                AuditSensitivityLevel.Restricted,
                false,
                ServiceAnnotations(
                    identity,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["rollout_id"] = "rollout-1",
                        ["stage_count"] = "0",
                    })),
        ];
        yield return
        [
            new ServiceRolloutStageAdvancedAuditTranslator(),
            new ServiceRolloutStageAdvancedEvent
            {
                Identity = identity,
                RolloutId = "rollout-1",
                StageIndex = 2,
                StageId = "stage-2",
            },
            "service.rollout.stage_advanced",
            "service",
            "rollout-1",
            ServiceExpected(
                AuditSensitivityLevel.Restricted,
                false,
                ServiceAnnotations(
                    identity,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["rollout_id"] = "rollout-1",
                        ["stage_index"] = "2",
                        ["stage_id"] = "stage-2",
                    })),
        ];
        yield return
        [
            new ServiceRolloutPausedAuditTranslator(),
            new ServiceRolloutPausedEvent
            {
                Identity = identity,
                RolloutId = "rollout-1",
                Reason = "manual hold",
            },
            "service.rollout.paused",
            "service",
            "rollout-1",
            ServiceExpected(
                AuditSensitivityLevel.Restricted,
                false,
                ServiceAnnotations(
                    identity,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["rollout_id"] = "rollout-1",
                        ["reason"] = "manual hold",
                    })),
        ];
        yield return
        [
            new ServiceRolloutResumedAuditTranslator(),
            new ServiceRolloutResumedEvent
            {
                Identity = identity,
                RolloutId = "rollout-1",
            },
            "service.rollout.resumed",
            "service",
            "rollout-1",
            ServiceExpected(
                AuditSensitivityLevel.Restricted,
                false,
                ServiceAnnotations(
                    identity,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["rollout_id"] = "rollout-1",
                    })),
        ];
        yield return
        [
            new ServiceRolloutCompletedAuditTranslator(),
            new ServiceRolloutCompletedEvent
            {
                Identity = identity,
                RolloutId = "rollout-1",
            },
            "service.rollout.completed",
            "service",
            "rollout-1",
            ServiceExpected(
                AuditSensitivityLevel.Restricted,
                false,
                ServiceAnnotations(
                    identity,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["rollout_id"] = "rollout-1",
                    })),
        ];
        yield return
        [
            new ServiceRolloutRolledBackAuditTranslator(),
            new ServiceRolloutRolledBackEvent
            {
                Identity = identity,
                RolloutId = "rollout-1",
                Reason = "regression",
            },
            "service.rollout.rolled_back",
            "service",
            "rollout-1",
            ServiceExpected(
                AuditSensitivityLevel.Restricted,
                true,
                ServiceAnnotations(
                    identity,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["rollout_id"] = "rollout-1",
                        ["reason"] = "regression",
                        ["target_count"] = "0",
                    })),
        ];
        yield return
        [
            new ServiceRolloutFailedAuditTranslator(),
            new ServiceRolloutFailedEvent
            {
                Identity = identity,
                RolloutId = "rollout-1",
                FailureReason = "activation error",
            },
            "service.rollout.failed",
            "service",
            "rollout-1",
            ServiceExpected(
                AuditSensitivityLevel.Restricted,
                false,
                ServiceAnnotations(
                    identity,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["rollout_id"] = "rollout-1",
                    })),
        ];
        yield return
        [
            new ServiceDefinitionCreatedAuditTranslator(),
            new ServiceDefinitionCreatedEvent
            {
                Spec = new ServiceDefinitionSpec
                {
                    Identity = identity,
                    DisplayName = "My Service",
                },
            },
            "service.definition.created",
            "service",
            "svc-a",
            ServiceExpected(annotations: ServiceAnnotations(identity)),
        ];
        yield return
        [
            new ServiceDefinitionUpdatedAuditTranslator(),
            new ServiceDefinitionUpdatedEvent
            {
                Spec = new ServiceDefinitionSpec
                {
                    Identity = identity,
                    DisplayName = "My Service",
                },
            },
            "service.definition.updated",
            "service",
            "svc-a",
            ServiceExpected(annotations: ServiceAnnotations(identity)),
        ];
        yield return
        [
            new ServiceRevisionCreatedAuditTranslator(),
            new ServiceRevisionCreatedEvent
            {
                Spec = new ServiceRevisionSpec
                {
                    Identity = identity,
                    RevisionId = "rev-1",
                },
            },
            "service.revision.created",
            "service",
            "rev-1",
            ServiceExpected(annotations: ServiceAnnotations(identity)),
        ];
        yield return
        [
            new ServiceRevisionRetiredAuditTranslator(),
            new ServiceRevisionRetiredEvent
            {
                Identity = identity,
                RevisionId = "rev-1",
            },
            "service.revision.retired",
            "service",
            "rev-1",
            ServiceExpected(
                AuditSensitivityLevel.Restricted,
                true,
                ServiceAnnotations(identity)),
        ];
    }

    private static IReadOnlyDictionary<string, string> EmptyAnnotations { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static ExpectedAuditFields DefaultExpected { get; } =
        new(
            "",
            AuditSensitivityLevel.Confidential,
            false,
            "command-1",
            "request-1",
            EmptyAnnotations);

    private static ExpectedAuditFields RestrictedDestructiveExpected { get; } =
        new(
            "",
            AuditSensitivityLevel.Restricted,
            true,
            "command-1",
            "request-1",
            EmptyAnnotations);

    private static ExpectedAuditFields ServiceExpected(
        AuditSensitivityLevel sensitivityLevel = AuditSensitivityLevel.Confidential,
        bool isDestructive = false,
        IReadOnlyDictionary<string, string>? annotations = null) =>
        new(
            "tenant-a/app-a",
            sensitivityLevel,
            isDestructive,
            "command-1",
            "request-1",
            annotations ?? EmptyAnnotations);

    private static IReadOnlyDictionary<string, string> ServiceAnnotations(
        ServiceIdentity identity,
        IReadOnlyDictionary<string, string>? extraAnnotations = null)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tenant_id"] = identity.TenantId,
            ["app_id"] = identity.AppId,
            ["namespace"] = identity.Namespace,
            ["service_id"] = identity.ServiceId,
        };
        if (extraAnnotations != null)
        {
            foreach (var pair in extraAnnotations)
                annotations[pair.Key] = pair.Value;
        }

        return annotations;
    }

    private static void AssertDestructiveAnnotation(AuditRecord record, bool isDestructive)
    {
        if (isDestructive)
            record.Annotations.Should().Contain("is_destructive", "true");
        else
            record.Annotations.Should().NotContainKey("is_destructive");
    }

    private static void AssertAnnotations(
        AuditRecord record,
        IReadOnlyDictionary<string, string> expectedAnnotations)
    {
        foreach (var annotation in expectedAnnotations)
            record.Annotations.Should().Contain(annotation.Key, annotation.Value);
    }

    private static void AssertLifecycle(AuditRecord record, IAuditCommittedEventTranslator translator)
    {
        if (translator is ServiceRegistrationFailedAuditTranslator or ServiceRolloutFailedAuditTranslator)
        {
            record.Outcome.Should().Be(AuditOutcome.Error);
            record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
            record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Failed);
            record.Failure.Should().NotBeNull();
            record.Failure.SanitizedMessage.Should().Be(record.Failure.Code);
            record.Annotations.Should().NotContainKey("error_summary");
            record.Annotations.Should().NotContainKey("failure_reason");
            return;
        }

        if (translator is ServiceRolloutStartedAuditTranslator or
            ServiceRolloutStageAdvancedAuditTranslator or
            ServiceRolloutPausedAuditTranslator or
            ServiceRolloutResumedAuditTranslator)
        {
            record.Outcome.Should().Be(AuditOutcome.Accepted);
            record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Running);
            record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Unspecified);
            return;
        }

        record.Outcome.Should().Be(AuditOutcome.Success);
        record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Succeeded);
    }

    private static AuditRecord Translate(IAuditCommittedEventTranslator translator, IMessage evt)
    {
        var records = translator.Translate(Context(), Any.Pack(evt));
        return records.Should().ContainSingle().Subject;
    }

    private static CommittedAuditTranslationContext Context() =>
        new(
            new EventEnvelope
            {
                Id = "envelope-command-id",
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = "corr-1",
                },
            },
            new CommittedStateEventPublished(),
            new StateEvent
            {
                AgentId = "actor-1",
                EventId = "state-event-1",
                Version = 17,
            },
            "actor-1",
            "type.googleapis.com/test",
            DateTimeOffset.Parse("2026-07-03T09:00:00+00:00"),
            "command-1",
            "request-1",
            "corr-1");

    private static void AssertCommittedAuditMaterializerRegistered<TContext>(
        IServiceCollection services,
        IServiceProvider provider)
        where TContext : class, IProjectionMaterializationContext
    {
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionArtifactMaterializer<TContext>) &&
            IsObservedProjectionArtifactMaterializerFor<CommittedAuditArtifactMaterializer<TContext>>(
                descriptor.ImplementationType));
        provider
            .GetRequiredService<CommittedAuditArtifactMaterializer<TContext>>()
            .Should()
            .NotBeNull();
    }

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
        string CommandId,
        string RequestId,
        IReadOnlyDictionary<string, string> Annotations);
}
