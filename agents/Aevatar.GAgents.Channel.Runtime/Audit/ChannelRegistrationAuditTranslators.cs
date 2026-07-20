using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Runtime.Audit;

public sealed class ChannelBotRegisteredAuditTranslator : ChannelAuditTranslatorBase<ChannelBotRegisteredEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ChannelBotRegisteredEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ChannelBotRegisteredEvent evt)
    {
        var entry = evt.Entry;
        return ChannelSeed(
            "channel.bot.registered",
            entry?.Id ?? string.Empty,
            entry?.ScopeId ?? string.Empty,
            entry?.Platform ?? string.Empty,
            "Channel bot registration committed.");
    }
}

public sealed class ChannelBotUnregisteredAuditTranslator : ChannelAuditTranslatorBase<ChannelBotUnregisteredEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ChannelBotUnregisteredEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ChannelBotUnregisteredEvent evt) =>
        ChannelSeed(
            "channel.bot.unregistered",
            evt.RegistrationId,
            "",
            "",
            "Channel bot unregistration committed.",
            AuditSensitivityLevel.Restricted,
            true);
}

public sealed class ChannelBotRegistrationRejectedAuditTranslator : ChannelAuditTranslatorBase<ChannelBotRegistrationRejectedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ChannelBotRegistrationRejectedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ChannelBotRegistrationRejectedEvent evt) =>
        ChannelSeed(
            "channel.bot.registration.rejected",
            evt.RequestedId,
            "",
            evt.Platform,
            "Channel bot registration rejected.",
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = evt.Reason ?? string.Empty,
            });
}

public abstract class ChannelAuditTranslatorBase<TEvent> : IAuditCommittedEventTranslator
    where TEvent : class, IMessage<TEvent>, new()
{
    public abstract string EventTypeUrl { get; }

    public IReadOnlyList<AuditRecord> Translate(CommittedAuditTranslationContext context, Any eventPayload)
    {
        if (eventPayload == null || !eventPayload.Is(new TEvent().Descriptor))
            return [];

        var evt = eventPayload.Unpack<TEvent>();
        return [CommittedAuditRecordFactory.CreateSystemRecord(context, BuildSeed(context, evt))];
    }

    protected abstract CommittedAuditSeed BuildSeed(CommittedAuditTranslationContext context, TEvent evt);

    protected static CommittedAuditSeed ChannelSeed(
        string operationName,
        string registrationId,
        string scopeId,
        string platform,
        string resultSummary,
        AuditSensitivityLevel sensitivityLevel = AuditSensitivityLevel.Confidential,
        bool isDestructive = false,
        IReadOnlyDictionary<string, string>? Annotations = null)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["platform"] = platform ?? string.Empty,
        };
        if (Annotations != null)
        {
            foreach (var annotation in Annotations)
                annotations[annotation.Key] = annotation.Value ?? string.Empty;
        }

        return new CommittedAuditSeed(
            operationName,
            "channel_bot_registration",
            registrationId,
            scopeId,
            sensitivityLevel,
            isDestructive,
            ResultSummary: resultSummary,
            Annotations: annotations);
    }
}
