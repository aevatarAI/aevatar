using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using AppWorkflowCallerCredential = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential;
using AppWorkflowCallerNyxIdAuthority = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerNyxIdAuthority;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowChatRunRequestSeedTests
{
    [Fact]
    public void WorkflowChatRunRequest_ShouldExposeContextSeedsAndHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["trace"] = "trace-1",
        };
        var request = new WorkflowChatRunRequest(
            Prompt: "hello",
            Source: WorkflowChatSource.CatalogWorkflow("direct"),
            ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
            Headers: headers,
            CommandIdSeed: "cmd-1",
            CorrelationIdSeed: "corr-1");

        var seed = request.Should().BeAssignableTo<ICommandContextSeed>().Subject;

        seed.CommandId.Should().Be("cmd-1");
        seed.CorrelationId.Should().Be("corr-1");
        seed.Headers.Should().BeSameAs(headers);
    }

    [Fact]
    public void WorkflowChatRunRequest_ShouldNotSerializeInternalTargets()
    {
        var request = new WorkflowChatRunRequest(
            Prompt: "hello",
            Source: WorkflowChatSource.CatalogWorkflow("direct"),
            ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
            TargetSeed: new WorkflowRunTargetSeed(
                ActorId: "run-1",
                WorkflowNameForRun: "direct",
                CreatedActorIds: ["definition-1", "run-1"],
                Source: WorkflowChatSource.CatalogWorkflow("direct")),
            CompletionNotificationTarget: new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCompletionNotificationTarget(
                ActorId: "delivery-actor-1",
                DeliveryId: "delivery-1",
                ExpiresAtUnixMs: 1710000000000),
            ResolvedDefinitionBinding: new WorkflowDefinitionBinding(
                "definition-internal",
                "direct",
                "name: direct\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExternalCapabilityExecutionMode.Interactive,
                SourceKind: "service_revision",
                CapabilityAdmissionPlan: new WorkflowCapabilityAdmissionPlan
                {
                    AdmissionDigest = "internal-digest",
                },
                WorkflowId: "workflow-internal",
                RevisionId: "revision-internal"),
            CurrentTurnId: "turn-internal");

        var json = JsonSerializer.Serialize(request);

        json.Should().NotContain("TargetSeed");
        json.Should().NotContain("CompletionNotificationTarget");
        json.Should().NotContain("ResolvedDefinitionBinding");
        json.Should().NotContain("CurrentTurnId");
        json.Should().NotContain("run-1");
        json.Should().NotContain("delivery-actor-1");
        json.Should().NotContain("definition-internal");
        json.Should().NotContain("internal-digest");
        json.Should().NotContain("workflow-internal");
        json.Should().NotContain("turn-internal");
    }

    [Fact]
    public void WorkflowChatRequestEnvelopeFactory_ShouldMapExternalIngressAsTypedProto()
    {
        var factory = new WorkflowChatRequestEnvelopeFactory();
        var request = new WorkflowChatRunRequest(
            Prompt: "hello",
            Source: WorkflowChatSource.CatalogWorkflow("direct"),
            ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
            ExternalIngress: new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowExternalIngressContext(
                RouteKey: "invoice",
                SourceId: "lark",
                DeliveryId: "delivery-1",
                ReceivedAtUnixMs: 1710000000000,
                ContentType: "application/json",
                PayloadFingerprint: "abc",
                AuthScheme: "hmac-sha256",
                PrincipalSubject: "lark"));

        var envelope = factory.CreateEnvelope(
            request,
            new CommandContext(
                "cmd-1",
                "corr-1",
                "target-1",
                new Dictionary<string, string>(StringComparer.Ordinal)));
        var payload = envelope.Payload.Unpack<WorkflowChatRequestEvent>();

        payload.ExternalIngress.RouteKey.Should().Be("invoice");
        payload.ExternalIngress.SourceId.Should().Be("lark");
        payload.ExternalIngress.DeliveryId.Should().Be("delivery-1");
        payload.ExternalIngress.ReceivedAtUnixMs.Should().Be(1710000000000);
        payload.ExternalIngress.ContentType.Should().Be("application/json");
        payload.ExternalIngress.PayloadFingerprint.Should().Be("abc");
        payload.ExternalIngress.AuthScheme.Should().Be("hmac-sha256");
        payload.ExternalIngress.PrincipalSubject.Should().Be("lark");
        payload.Metadata.Should().NotContainKey("external_ingress");
    }

    [Fact]
    public void WorkflowChatRequestEnvelopeFactory_ShouldMapExactUnattendedAuthorityWithoutBearer()
    {
        var authorization = new WorkflowUnattendedEffectAuthorization
        {
            SchemaVersion = WorkflowUnattendedEffectAuthorizationIntegrity.SchemaVersion,
            DefinitionActorId = "definition-alpha",
            ScopeId = "scope-alpha",
            WorkflowId = "workflow-alpha",
            RevisionId = "revision-alpha",
            DefinitionVersion = 7,
            AuthorizationDigest = "sha256:authorization-alpha",
        };
        var request = new WorkflowChatRunRequest(
            Prompt: "start",
            Source: WorkflowChatSource.DefinitionActor("definition-alpha", "workflow-alpha"),
            CallerCredential: new AppWorkflowCallerCredential(
                BearerToken: null,
                NyxIdAuthority: new AppWorkflowCallerNyxIdAuthority(
                    "nyxid",
                    "tenant-alpha",
                    "user-alpha",
                    "proxy",
                    "binding-alpha"),
                Kind: NyxIdCallerCredentialKind.ProxyDelegation,
                SourceReadableUserBearerToken: null,
                UnattendedEffectAuthorization: authorization),
            ExpectedExecutionMode: ExternalCapabilityExecutionMode.Durable,
            ExternalIngress: new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowExternalIngressContext(
                "route-alpha",
                "nyxid",
                "delivery-alpha",
                1710000000000,
                "application/json",
                "sha256:payload-alpha",
                "hmac-sha256",
                "nyxid"));

        var payload = new WorkflowChatRequestEnvelopeFactory().CreateEnvelope(
            request,
            new CommandContext(
                "command-alpha",
                "correlation-alpha",
                "target-alpha",
                new Dictionary<string, string>(StringComparer.Ordinal)))
            .Payload.Unpack<WorkflowChatRequestEvent>();

        payload.CallerCredential.BearerToken.Should().BeEmpty();
        payload.CallerCredential.SourceReadableUserBearerToken.Should().BeEmpty();
        payload.CallerCredential.Kind.Should().Be(NyxIdCallerCredentialKind.ProxyDelegation);
        payload.CallerCredential.NyxIdAuthority.BindingId.Should().Be("binding-alpha");
        payload.CallerCredential.UnattendedEffectAuthorization.AuthorizationDigest
            .Should().Be("sha256:authorization-alpha");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void WorkflowChatRequestEnvelopeFactory_ShouldRejectIncompleteUnattendedAuthority(
        bool includeBindingId,
        bool includeAuthorization)
    {
        var request = new WorkflowChatRunRequest(
            Prompt: "start",
            Source: WorkflowChatSource.DefinitionActor("definition-alpha", "workflow-alpha"),
            CallerCredential: new AppWorkflowCallerCredential(
                BearerToken: null,
                NyxIdAuthority: new AppWorkflowCallerNyxIdAuthority(
                    "nyxid",
                    "tenant-alpha",
                    "user-alpha",
                    "proxy",
                    includeBindingId ? "binding-alpha" : null),
                Kind: NyxIdCallerCredentialKind.ProxyDelegation,
                SourceReadableUserBearerToken: null,
                UnattendedEffectAuthorization: includeAuthorization
                    ? new WorkflowUnattendedEffectAuthorization
                    {
                        SchemaVersion = WorkflowUnattendedEffectAuthorizationIntegrity.SchemaVersion,
                    }
                    : null),
            ExpectedExecutionMode: ExternalCapabilityExecutionMode.Durable);

        FluentActions.Invoking(() => new WorkflowChatRequestEnvelopeFactory().CreateEnvelope(
                request,
                new CommandContext(
                    "command-alpha",
                    "correlation-alpha",
                    "target-alpha",
                    new Dictionary<string, string>(StringComparer.Ordinal))))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WorkflowChatRequestEnvelopeFactory_ShouldMapCompletionNotificationTargetAsTypedProto()
    {
        var factory = new WorkflowChatRequestEnvelopeFactory();
        var request = new WorkflowChatRunRequest(
            Prompt: "hello",
            Source: WorkflowChatSource.CatalogWorkflow("direct"),
            ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
            CompletionNotificationTarget: new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCompletionNotificationTarget(
                ActorId: "delivery-actor-1",
                DeliveryId: "delivery-1",
                ExpiresAtUnixMs: 1710000000000));

        var envelope = factory.CreateEnvelope(
            request,
            new CommandContext(
                "cmd-1",
                "corr-1",
                "target-1",
                new Dictionary<string, string>(StringComparer.Ordinal)));
        var payload = envelope.Payload.Unpack<WorkflowChatRequestEvent>();

        payload.CompletionNotificationTarget.ActorId.Should().Be("delivery-actor-1");
        payload.CompletionNotificationTarget.DeliveryId.Should().Be("delivery-1");
        payload.CompletionNotificationTarget.ExpiresAtUnixMs.Should().Be(1710000000000);
        payload.Metadata.Should().NotContainKey("completion_notification_target");
    }

    [Fact]
    public void WorkflowChatRequestEnvelopeFactory_ShouldMapConversationExecutionContextAsTypedProto()
    {
        var factory = new WorkflowChatRequestEnvelopeFactory();
        var request = new WorkflowChatRunRequest(
            Prompt: "team01",
            Source: WorkflowChatSource.CatalogWorkflow("direct"),
            ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
            ConversationContext: new WorkflowConversationExecutionContext(
                ScopeId: "scope-a",
                ConversationId: "conversation-alpha",
                StateVersion: 7,
                Messages:
                [
                    new WorkflowConversationExecutionMessage(
                        Sequence: 1,
                        TurnId: "turn-1",
                        Role: WorkflowConversationExecutionRole.User,
                        Content: "Create a workflow that generates fund analysis reports."),
                    new WorkflowConversationExecutionMessage(
                        Sequence: 2,
                        TurnId: "turn-1",
                        Role: WorkflowConversationExecutionRole.Assistant,
                        Content: "Choose a Team: team01 or team02."),
                ],
                Truncated: false,
                MaxMessageCount: 24,
                CurrentTurnId: "turn-current"));

        var envelope = factory.CreateEnvelope(
            request,
            new CommandContext(
                "cmd-1",
                "corr-1",
                "target-1",
                new Dictionary<string, string>(StringComparer.Ordinal)));
        var payload = envelope.Payload.Unpack<WorkflowChatRequestEvent>();

        payload.ConversationContext.ScopeId.Should().Be("scope-a");
        payload.ConversationContext.ConversationId.Should().Be("conversation-alpha");
        payload.ConversationContext.StateVersion.Should().Be(7);
        payload.ConversationContext.Truncated.Should().BeFalse();
        payload.ConversationContext.MaxMessageCount.Should().Be(24);
        payload.ConversationContext.CurrentTurnId.Should().Be("turn-current");
        payload.ConversationContext.Messages.Select(static message => (message.Sequence, message.TurnId, message.Role, message.Content))
            .Should()
            .Equal(
                (1, "turn-1", WorkflowConversationRole.User, "Create a workflow that generates fund analysis reports."),
                (2, "turn-1", WorkflowConversationRole.Assistant, "Choose a Team: team01 or team02."));
        payload.Metadata.Should().NotContainKey("conversation_context");
    }

    [Fact]
    public void WorkflowChatRequestEnvelopeFactory_ShouldMapCurrentTurnIdWithoutConversationContext()
    {
        var factory = new WorkflowChatRequestEnvelopeFactory();
        var request = new WorkflowChatRunRequest(
            Prompt: "connect twitter",
            Source: WorkflowChatSource.CatalogWorkflow("studio"),
            ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
            CurrentTurnId: "turn-studio-alpha");

        var envelope = factory.CreateEnvelope(
            request,
            new CommandContext(
                "command-alpha",
                "correlation-alpha",
                "target-alpha",
                new Dictionary<string, string>(StringComparer.Ordinal)));
        var payload = envelope.Payload.Unpack<WorkflowChatRequestEvent>();

        payload.CurrentTurnId.Should().Be("turn-studio-alpha");
        payload.ConversationContext.Should().BeNull();
    }
}
