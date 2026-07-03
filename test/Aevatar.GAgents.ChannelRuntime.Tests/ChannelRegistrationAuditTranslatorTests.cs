using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Channel.Runtime.Audit;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelRegistrationAuditTranslatorTests
{
    [Fact]
    public void AddChannelRuntime_ShouldWireCommittedAuditMaterializerAndTranslators()
    {
        var services = new ServiceCollection();

        services.AddChannelRuntime();
        using var provider = services.BuildServiceProvider();

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IProjectionArtifactMaterializer<ChannelBotRegistrationMaterializationContext>) &&
            IsObservedProjectionArtifactMaterializerFor<
                CommittedAuditArtifactMaterializer<ChannelBotRegistrationMaterializationContext>>(
                descriptor.ImplementationType));
        provider
            .GetRequiredService<CommittedAuditArtifactMaterializer<ChannelBotRegistrationMaterializationContext>>()
            .Should()
            .NotBeNull();
        provider
            .GetServices<IAuditCommittedEventTranslator>()
            .Select(static translator => translator.GetType())
            .Should()
            .Contain([
                typeof(ChannelBotRegisteredAuditTranslator),
                typeof(ChannelBotUnregisteredAuditTranslator),
                typeof(ChannelBotRegistrationRejectedAuditTranslator),
            ]);
    }

    [Theory]
    [MemberData(nameof(ChannelSeedEvents))]
    public void ChannelSeedTranslators_ShouldProduceCommittedAuditRecord(
        IAuditCommittedEventTranslator translator,
        IMessage evt,
        string operationName,
        string targetId)
    {
        var record = translator.Translate(Context(), Any.Pack(evt)).Should().ContainSingle().Subject;

        record.OperationName.Should().Be(operationName);
        record.Outcome.Should().Be(AuditOutcome.Success);
        record.ActorKind.Should().Be(AuditActorKind.System);
        record.Target.Kind.Should().Be("channel_bot_registration");
        record.Target.Id.Should().Be(targetId);
        record.CommittedFactRef.StateVersion.Should().Be(5);
    }

    [Fact]
    public void ChannelTranslator_ShouldReturnZeroRecords_ForWrongEventType()
    {
        var records = new ChannelBotRegisteredAuditTranslator()
            .Translate(Context(), Any.Pack(new StringValue { Value = "wrong" }));

        records.Should().BeEmpty();
    }

    public static IEnumerable<object[]> ChannelSeedEvents()
    {
        yield return
        [
            new ChannelBotRegisteredAuditTranslator(),
            new ChannelBotRegisteredEvent
            {
                Entry = new ChannelBotRegistrationEntry
                {
                    Id = "reg-1",
                    ScopeId = "scope-1",
                    Platform = "lark",
                },
            },
            "channel.bot.registered",
            "reg-1",
        ];
        yield return
        [
            new ChannelBotUnregisteredAuditTranslator(),
            new ChannelBotUnregisteredEvent { RegistrationId = "reg-1" },
            "channel.bot.unregistered",
            "reg-1",
        ];
        yield return
        [
            new ChannelBotRegistrationRejectedAuditTranslator(),
            new ChannelBotRegistrationRejectedEvent
            {
                Platform = "lark",
                RequestedId = "reg-requested",
                Reason = "missing scope",
            },
            "channel.bot.registration.rejected",
            "reg-requested",
        ];
    }

    private static CommittedAuditTranslationContext Context() =>
        new(
            new EventEnvelope { Id = "cmd-1" },
            new CommittedStateEventPublished(),
            new StateEvent
            {
                AgentId = "channel-registration-actor",
                EventId = "event-1",
                Version = 5,
            },
            "channel-registration-actor",
            "type.googleapis.com/test",
            DateTimeOffset.Parse("2026-07-03T09:00:00+00:00"),
            "cmd-1",
            "req-1",
            "corr-1");

    private static bool IsObservedProjectionArtifactMaterializerFor<TMaterializer>(Type? type)
    {
        return type?.IsGenericType == true &&
               type.Name.StartsWith("ObservedProjectionArtifactMaterializer`", StringComparison.Ordinal) &&
               type.GenericTypeArguments.Length == 2 &&
               type.GenericTypeArguments[1] == typeof(TMaterializer);
    }
}
