using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Device;
using Aevatar.GAgents.Device.Audit;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Device;

public sealed class DeviceRegistrationAuditTranslatorTests
{
    [Fact]
    public void AddDeviceRegistration_ShouldWireCommittedAuditMaterializerAndTranslators()
    {
        var services = new ServiceCollection();

        services.AddDeviceRegistration();
        using var provider = services.BuildServiceProvider();

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IProjectionArtifactMaterializer<DeviceRegistrationMaterializationContext>) &&
            IsObservedProjectionArtifactMaterializerFor<
                CommittedAuditArtifactMaterializer<DeviceRegistrationMaterializationContext>>(
                descriptor.ImplementationType));
        provider
            .GetServices<IAuditCommittedEventTranslator>()
            .Select(static translator => translator.GetType())
            .Should()
            .Contain([
                typeof(DeviceRegisteredAuditTranslator),
                typeof(DeviceUnregisteredAuditTranslator),
            ]);
    }

    [Fact]
    public void DeviceRegisteredTranslator_ShouldProduceRecord_AndNotLeakHmacKey()
    {
        var evt = new DeviceRegisteredEvent
        {
            Entry = new DeviceRegistrationEntry
            {
                Id = "reg-1",
                ScopeId = "scope-1",
                Description = "front door sensor",
                HmacKey = "top-secret-hmac-signing-key",
            },
        };

        var record = new DeviceRegisteredAuditTranslator()
            .Translate(Context(), Any.Pack(evt))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("device.registration.registered");
        record.Target.Kind.Should().Be("device_registration");
        record.Target.Id.Should().Be("reg-1");
        record.ScopeId.Should().Be("scope-1");
        record.Annotations.Should().Contain("description", "front door sensor");
        record.ToString().Should().NotContain("top-secret-hmac-signing-key");
    }

    [Fact]
    public void DeviceUnregisteredTranslator_ShouldBeDestructiveAndRestricted()
    {
        var record = new DeviceUnregisteredAuditTranslator()
            .Translate(Context(), Any.Pack(new DeviceUnregisteredEvent { RegistrationId = "reg-1" }))
            .Should()
            .ContainSingle()
            .Subject;

        record.OperationName.Should().Be("device.registration.unregistered");
        record.Target.Id.Should().Be("reg-1");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Restricted);
        record.Annotations.Should().Contain("is_destructive", "true");
    }

    [Fact]
    public void DeviceTranslator_ShouldReturnZeroRecords_ForWrongEventType()
    {
        new DeviceRegisteredAuditTranslator()
            .Translate(Context(), Any.Pack(new StringValue { Value = "wrong" }))
            .Should()
            .BeEmpty();
    }

    private static CommittedAuditTranslationContext Context() =>
        new(
            new EventEnvelope { Id = "cmd-1" },
            new CommittedStateEventPublished(),
            new StateEvent { AgentId = "device-registration-actor", EventId = "event-1", Version = 5 },
            "device-registration-actor",
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
