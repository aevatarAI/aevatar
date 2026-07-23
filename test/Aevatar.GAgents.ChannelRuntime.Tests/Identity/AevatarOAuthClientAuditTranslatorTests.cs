using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Audit;
using Aevatar.GAgents.Channel.Identity.DependencyInjection;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class AevatarOAuthClientAuditTranslatorTests
{
    [Fact]
    public void AddChannelIdentity_ShouldWireOAuthClientCommittedAuditMaterializerAndTranslators()
    {
        var services = new ServiceCollection();

        services.AddChannelIdentity(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IProjectionArtifactMaterializer<AevatarOAuthClientMaterializationContext>) &&
            IsObservedProjectionArtifactMaterializerFor<
                CommittedAuditArtifactMaterializer<AevatarOAuthClientMaterializationContext>>(
                descriptor.ImplementationType));
        provider
            .GetRequiredService<CommittedAuditArtifactMaterializer<AevatarOAuthClientMaterializationContext>>()
            .Should()
            .NotBeNull();
        provider
            .GetServices<IAuditCommittedEventTranslator>()
            .Select(static translator => translator.GetType())
            .Should()
            .Contain([
                typeof(AevatarOAuthClientProvisionedAuditTranslator),
                typeof(AevatarOAuthClientHmacKeyRotatedAuditTranslator),
                typeof(AevatarOAuthClientBrokerCapabilityObservedAuditTranslator),
                typeof(AevatarOAuthClientDriftReconciledAuditTranslator),
            ]);
    }

    [Theory]
    [MemberData(nameof(OAuthClientSeedEvents))]
    public void OAuthClientTranslators_ShouldProduceCommittedAuditRecord(
        IAuditCommittedEventTranslator translator,
        IMessage evt,
        string operationName,
        AuditSensitivityLevel sensitivityLevel,
        IReadOnlyDictionary<string, string> expectedAnnotations)
    {
        var record = translator.Translate(Context(), Any.Pack(evt)).Should().ContainSingle().Subject;

        record.OperationName.Should().Be(operationName);
        record.Outcome.Should().Be(AuditOutcome.Success);
        record.ActorKind.Should().Be(AuditActorKind.System);
        record.CapturePlane.Should().Be(AuditCapturePlane.ProjectionArtifact);
        record.Target.Kind.Should().Be("aevatar_oauth_client");
        record.Target.Id.Should().Be(AevatarOAuthClientGAgent.WellKnownId);
        record.SensitivityLevel.Should().Be(sensitivityLevel);
        record.CommittedFactRef.StateVersion.Should().Be(7);
        foreach (var annotation in expectedAnnotations)
            record.Annotations.Should().Contain(annotation.Key, annotation.Value);
    }

    [Fact]
    public void HmacKeyRotatedTranslator_ShouldNotLeakKeyMaterial()
    {
        var evt = new AevatarOAuthClientHmacKeyRotatedEvent
        {
            HmacKid = "v2",
            PreviousHmacKid = "v1",
            HmacKey = ByteString.CopyFromUtf8("super-secret-key-bytes"),
            PreviousHmacKey = ByteString.CopyFromUtf8("older-secret-key-bytes"),
        };

        var record = new AevatarOAuthClientHmacKeyRotatedAuditTranslator()
            .Translate(Context(), Any.Pack(evt))
            .Should()
            .ContainSingle()
            .Subject;

        record.Annotations.Should().Contain("hmac_kid", "v2");
        record.Annotations.Should().Contain("previous_hmac_kid", "v1");
        record.Annotations.Values.Should().NotContain("super-secret-key-bytes");
        record.Annotations.Values.Should().NotContain("older-secret-key-bytes");
        record.Annotations.Keys.Should().NotContain(static key =>
            key.Contains("hmac_key", StringComparison.Ordinal));
    }

    [Fact]
    public void OAuthClientTranslator_ShouldReturnZeroRecords_ForWrongEventType()
    {
        var records = new AevatarOAuthClientProvisionedAuditTranslator()
            .Translate(Context(), Any.Pack(new StringValue { Value = "wrong" }));

        records.Should().BeEmpty();
    }

    public static IEnumerable<object[]> OAuthClientSeedEvents()
    {
        yield return
        [
            new AevatarOAuthClientProvisionedAuditTranslator(),
            new AevatarOAuthClientProvisionedEvent
            {
                ClientId = "client-abc",
                NyxidAuthority = "https://nyxid.example",
            },
            "identity.oauth-client.provisioned",
            AuditSensitivityLevel.Confidential,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = "client-abc",
                ["nyxid_authority"] = "https://nyxid.example",
            },
        ];
        yield return
        [
            new AevatarOAuthClientHmacKeyRotatedAuditTranslator(),
            new AevatarOAuthClientHmacKeyRotatedEvent { HmacKid = "v2", PreviousHmacKid = "v1" },
            "identity.oauth-client.hmac-key.rotated",
            AuditSensitivityLevel.Restricted,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["hmac_kid"] = "v2",
                ["previous_hmac_kid"] = "v1",
            },
        ];
        yield return
        [
            new AevatarOAuthClientBrokerCapabilityObservedAuditTranslator(),
            new AevatarOAuthClientBrokerCapabilityObservedEvent { ObservedAtUnix = 1_700_000_000 },
            "identity.oauth-client.broker-capability.observed",
            AuditSensitivityLevel.Confidential,
            new Dictionary<string, string>(StringComparer.Ordinal),
        ];
        yield return
        [
            new AevatarOAuthClientDriftReconciledAuditTranslator(),
            new AevatarOAuthClientDriftReconciledEvent
            {
                DriftKind = "redirect_uri",
                ActiveClientId = "client-abc",
            },
            "identity.oauth-client.drift.reconciled",
            AuditSensitivityLevel.Confidential,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["drift_kind"] = "redirect_uri",
                ["active_client_id"] = "client-abc",
            },
        ];
    }

    private static CommittedAuditTranslationContext Context() =>
        new(
            new EventEnvelope { Id = "cmd-1" },
            new CommittedStateEventPublished(),
            new StateEvent
            {
                AgentId = AevatarOAuthClientGAgent.WellKnownId,
                EventId = "event-1",
                Version = 7,
            },
            AevatarOAuthClientGAgent.WellKnownId,
            "type.googleapis.com/test",
            DateTimeOffset.Parse("2026-07-09T09:00:00+00:00"),
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
