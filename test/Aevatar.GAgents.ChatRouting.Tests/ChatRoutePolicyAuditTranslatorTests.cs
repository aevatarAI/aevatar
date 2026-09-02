using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatRouting.Audit;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChatRouting.Tests;

/// <summary>
/// Guards the committed-fact audit wiring for the chat route policy aggregate:
/// the audit materializer is registered for
/// <see cref="ChatRoutePolicyMaterializationContext"/> and the
/// <see cref="ChatRoutePolicyUpdatedAuditTranslator"/> produces the expected
/// system record from a committed <c>ChatRoutePolicyUpdated</c> event.
/// </summary>
public sealed class ChatRoutePolicyAuditTranslatorTests
{
    [Fact]
    public void AddChatRoutingAgents_WiresChatRoutePolicyCommittedAuditMaterializerAndTranslator()
    {
        var services = new ServiceCollection().AddChatRoutingAgents();
        using var provider = services.BuildServiceProvider();

        provider
            .GetRequiredService<CommittedAuditArtifactMaterializer<ChatRoutePolicyMaterializationContext>>()
            .Should()
            .NotBeNull();
        provider
            .GetServices<IAuditCommittedEventTranslator>()
            .Select(static translator => translator.GetType())
            .Should()
            .Contain(typeof(ChatRoutePolicyUpdatedAuditTranslator));
    }

    [Fact]
    public void ChatRoutePolicyUpdatedTranslator_ShouldProduceCommittedAuditRecord()
    {
        var translator = new ChatRoutePolicyUpdatedAuditTranslator();
        var evt = new ChatRoutePolicyUpdated
        {
            State = new ChatRoutePolicyState
            {
                PolicyId = "chat-route-policy:scope-1",
                OwnerScope = new OwnerScope { RegistrationScopeId = "scope-1" },
                DefaultTarget = new ChatRouteAction { Reject = new Reject { Reason = "closed" } },
                Version = 3,
                Rules = { new ChatRouteRule { RuleId = "r1" }, new ChatRouteRule { RuleId = "r2" } },
            },
        };

        var records = translator.Translate(Context(), Any.Pack(evt));

        var record = records.Should().ContainSingle().Subject;
        record.OperationName.Should().Be("chat.route-policy.updated");
        record.Outcome.Should().Be(AuditOutcome.Success);
        record.ActorKind.Should().Be(AuditActorKind.System);
        record.Target.Kind.Should().Be("chat_route_policy");
        record.Target.Id.Should().Be("chat-route-policy:scope-1");
        record.ScopeId.Should().Be("scope-1");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Confidential);
        record.Annotations.Should().NotContainKey("is_destructive");
        record.Annotations.Should().Contain("policy_version", "3");
        record.Annotations.Should().Contain("rule_count", "2");
    }

    [Fact]
    public void ChatRoutePolicyUpdatedTranslator_ShouldReturnZeroRecords_ForWrongEventType()
    {
        var translator = new ChatRoutePolicyUpdatedAuditTranslator();

        var records = translator.Translate(Context(), Any.Pack(new StringValue { Value = "wrong" }));

        records.Should().BeEmpty();
    }

    private static CommittedAuditTranslationContext Context() =>
        new(
            new EventEnvelope
            {
                Id = "envelope-command-id",
                Propagation = new EnvelopePropagation { CorrelationId = "corr-1" },
            },
            new CommittedStateEventPublished(),
            new StateEvent
            {
                AgentId = "chat-route-policy:scope-1",
                EventId = "state-event-1",
                Version = 3,
            },
            "chat-route-policy:scope-1",
            "type.googleapis.com/aevatar.chat_routing.v1.ChatRoutePolicyUpdated",
            DateTimeOffset.Parse("2026-07-10T09:00:00+00:00"),
            "command-1",
            "request-1",
            "corr-1");
}
