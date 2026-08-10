using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using ProtoValue = Google.Protobuf.WellKnownTypes.Value;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatReadBackExpectedValueSourceCompatibilityTests
{
    [Theory]
    [InlineData(0, true, AgentToolReadBackExpectedValueSource.FrozenValue, 1)]
    [InlineData(1, false, AgentToolReadBackExpectedValueSource.ProviderResourceId, 2)]
    [InlineData(1, true, AgentToolReadBackExpectedValueSource.FrozenValue, 1)]
    [InlineData(2, false, AgentToolReadBackExpectedValueSource.ProviderResourceId, 2)]
    public void HistoricalWirePayload_ShouldRemainValidAcrossMapperPolicyVerificationAndReload(
        int rawSource,
        bool hasExpectedValue,
        AgentToolReadBackExpectedValueSource expectedDomainSource,
        int expectedCanonicalRawSource)
    {
        var wireAdmission = ParseWireAdmission(rawSource, hasExpectedValue);
        ((int)wireAdmission.ReadBack.Assertion.ExpectedValueSource).Should().Be(rawSource);

        var mapped = AgentToolOperationAdmissionPayloadMapper.FromPayload(wireAdmission);
        mapped.Should().NotBeNull();
        mapped!.ReadBack.Should().NotBeNull();
        mapped.ReadBack!.Assertion.ExpectedValueSource.Should().Be(expectedDomainSource);
        var rewritten = AgentToolOperationAdmissionPayloadMapper.ToPayload(mapped);
        ((int)rewritten.ReadBack.Assertion.ExpectedValueSource).Should()
            .Be(expectedCanonicalRawSource);
        var canonicalWire = wireAdmission.Clone();
        AgentToolReadBackExpectedValueSourcePayloadCanonicalizer.TryCanonicalize(
                canonicalWire.ReadBack.Assertion,
                out var canonicalAssertion)
            .Should().BeTrue();
        canonicalWire.ReadBack.Assertion = canonicalAssertion;

        var effectSafety = new NyxIdChatToolCallSafety
        {
            IsReadOnly = false,
            IsDestructive = false,
            MayChangeExternalState = true,
        };
        NyxIdChatOperationAdmissionPolicy.IsValid(wireAdmission, effectSafety).Should().BeTrue();
        NyxIdChatOperationAdmissionPolicy.Matches(wireAdmission, canonicalWire).Should().BeTrue();

        var projection = ProjectionFor(wireAdmission.ReadBack.ReadOperation);
        NyxIdChatToolVerificationPort.TryEvaluate(
                projection,
                wireAdmission.ReadBack.ReadOperation,
                wireAdmission.ReadBack.Assertion,
                "resource-alpha",
                out var matched)
            .Should().BeTrue();
        matched.Should().BeTrue();

        var reloaded = NyxIdChatTurnGAgentState.Parser.ParseFrom(new NyxIdChatTurnGAgentState
        {
            OperationAdmission = wireAdmission,
            RecoveryReadBack = wireAdmission.ReadBack,
        }.ToByteArray());
        ((int)reloaded.RecoveryReadBack.Assertion.ExpectedValueSource).Should().Be(rawSource);
        NyxIdChatOperationAdmissionPolicy.IsValidReadBack(reloaded.RecoveryReadBack)
            .Should().BeTrue();
        NyxIdChatOperationAdmissionPolicy.Matches(reloaded.OperationAdmission, canonicalWire)
            .Should().BeTrue();
        NyxIdChatToolVerificationPort.TryEvaluate(
                projection,
                reloaded.RecoveryReadBack.ReadOperation,
                reloaded.RecoveryReadBack.Assertion,
                "resource-alpha",
                out matched)
            .Should().BeTrue();
        matched.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, true)]
    public void ConflictingWirePayload_ShouldFailClosed(
        int rawSource,
        bool hasExpectedValue)
    {
        var wireAdmission = ParseWireAdmission(rawSource, hasExpectedValue);
        var effectSafety = new NyxIdChatToolCallSafety
        {
            IsReadOnly = false,
            IsDestructive = false,
            MayChangeExternalState = true,
        };

        AgentToolOperationAdmissionPayloadMapper.FromPayload(wireAdmission).Should().BeNull();
        NyxIdChatOperationAdmissionPolicy.IsValid(wireAdmission, effectSafety).Should().BeFalse();
        NyxIdChatToolVerificationPort.TryEvaluate(
                ProjectionFor(wireAdmission.ReadBack.ReadOperation),
                wireAdmission.ReadBack.ReadOperation,
                wireAdmission.ReadBack.Assertion,
                "resource-alpha",
                out _)
            .Should().BeFalse();

        var reloaded = NyxIdChatTurnGAgentState.Parser.ParseFrom(new NyxIdChatTurnGAgentState
        {
            RecoveryReadBack = wireAdmission.ReadBack,
        }.ToByteArray());
        NyxIdChatOperationAdmissionPolicy.IsValidReadBack(reloaded.RecoveryReadBack)
            .Should().BeFalse();
    }

    private static AgentToolOperationAdmissionPayload ParseWireAdmission(
        int rawSource,
        bool hasExpectedValue)
    {
        var admission = EffectAdmission();
        admission.ReadBack.Assertion.ExpectedValueSource =
            (AgentToolReadBackExpectedValueSourcePayload)rawSource;
        admission.ReadBack.Assertion.ExpectedValue = hasExpectedValue
            ? ProtoValue.ForString("resource-alpha")
            : null;
        return AgentToolOperationAdmissionPayload.Parser.ParseFrom(admission.ToByteArray());
    }

    private static AgentToolOperationAdmissionPayload EffectAdmission() => new()
    {
        ServiceInstanceId = "connected-service-alpha",
        ServiceSlug = "service-slug-alpha",
        PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
        {
            EndpointId = "effect-endpoint-alpha",
        },
        AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
        HttpMethod = "POST",
        PathTemplate = "/resources",
        ContractDigest = new string('b', 64),
        CatalogDigest = $"sha256:{new string('a', 64)}",
        ExecutionPolicy = new AgentToolOperationExecutionPolicyPayload
        {
            Risk = AgentToolOperationRiskPayload.Write,
            Approval = AgentToolOperationApprovalPayload.Required,
            EnforcementOwner = AgentToolOperationEnforcementOwnerPayload.Aevatar,
            AllowedExecutionModes =
            {
                AgentToolOperationExecutionModePayload.Interactive,
            },
        },
        ReadBack = new AgentToolOperationReadBackPayload
        {
            ReadOperation = ReadAdmission(),
            Arguments = new Struct(),
            Assertion = new AgentToolReadBackAssertionPayload
            {
                Match = AgentToolReadBackMatchPayload.Equals,
                JsonPointer = "/resource_id",
            },
            CheckName = "resource-visible",
        },
    };

    private static AgentToolOperationAdmissionPayload ReadAdmission() => new()
    {
        ServiceInstanceId = "connected-service-alpha",
        ServiceSlug = "service-slug-alpha",
        PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
        {
            EndpointId = "read-endpoint-alpha",
        },
        AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
        HttpMethod = "GET",
        PathTemplate = "/resources/{resource_id}",
        ContractDigest = new string('c', 64),
        CatalogDigest = $"sha256:{new string('a', 64)}",
        ExecutionPolicy = new AgentToolOperationExecutionPolicyPayload
        {
            Risk = AgentToolOperationRiskPayload.ReadOnly,
            Approval = AgentToolOperationApprovalPayload.None,
            EnforcementOwner = AgentToolOperationEnforcementOwnerPayload.Aevatar,
            AllowedExecutionModes =
            {
                AgentToolOperationExecutionModePayload.Interactive,
            },
        },
    };

    private static string ProjectionFor(AgentToolOperationAdmissionPayload readOperation)
    {
        var admission = AgentToolOperationAdmissionPayloadMapper.FromPayload(readOperation)!;
        return new JsonObject
        {
            ["kind"] = "connected_service_read_projection",
            ["status"] = "succeeded",
            ["provenance"] = new JsonObject
            {
                ["source_kind"] = "nyxid_connected_service",
                ["operation_selector_digest"] = AgentToolOperationSelector.ComputeDigest(admission),
            },
            ["data"] = new JsonObject
            {
                ["resource_id"] = "resource-alpha",
            },
        }.ToJsonString();
    }
}
