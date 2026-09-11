using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Integration.AI;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelRegistrationAuthorityAdmissionTests
{
    [Fact]
    public void ExplicitRegistrationDeliveryCredential_ShouldUseUnifiedAgentKey()
    {
        var registration = CreateExplicitRegistration();

        var enabled = ChannelWorkflowResultDeliveryCapability.TryGetDeliveryCredential(
            registration,
            out var apiKeyId,
            out var secretReference);

        enabled.Should().BeTrue();
        apiKeyId.Should().Be(registration.ChannelAgentKey.ApiKeyId);
        secretReference.Should().Be(registration.ChannelAgentKey.SecretReference);
    }

    [Fact]
    public void WorkflowAdmissionCallSite_ShouldSurviveTypedAndProtobufMapping()
    {
        const string callSiteId = "workflow-alpha/invoke-service";
        var mapped = WorkflowOperationAdmissionToolContextMapper.Map(
            CreateWorkflowAdmission(callSiteId));

        mapped.Should().NotBeNull();
        mapped!.CallSiteId.Should().Be(callSiteId);

        var payload = AgentToolOperationAdmissionPayloadMapper.ToPayload(mapped);
        payload.CallSiteId.Should().Be(callSiteId);
        AgentToolOperationAdmissionPayload.Descriptor
            .FindFieldByName("call_site_id")!
            .FieldNumber.Should().Be(18);

        var restored = AgentToolOperationAdmissionPayloadMapper.FromPayload(
            AgentToolOperationAdmissionPayload.Parser.ParseFrom(payload.ToByteArray()));

        restored.Should().NotBeNull();
        restored!.CallSiteId.Should().Be(callSiteId);
    }

    [Fact]
    public async Task AdmitAsync_ExplicitBusinessListedAndGrantedTarget_ShouldAllow()
    {
        var registration = CreateExplicitRegistration();
        var fixture = CreateAdmissionFixture([registration]);

        var result = await fixture.Port.AdmitAsync(CreateRequest(
            registration,
            serviceInstanceId: "svc-business"));

        result.Allowed.Should().BeTrue();
        result.Reason.Should().Be(ChannelRegistrationAuthorityAdmissionReason.Allowed);
        await fixture.Query.Received(1).ListSnapshotsByNyxAgentApiKeyIdAsync(
            registration.ChannelAgentKey.ApiKeyId,
            Arg.Any<CancellationToken>());
        await fixture.Query.DidNotReceiveWithAnyArgs().ListByNyxAgentApiKeyIdAsync(
            default!,
            default);
        await fixture.Query.DidNotReceiveWithAnyArgs().GetByNyxAgentApiKeyIdAsync(
            default!,
            default);
        await fixture.Resolver.DidNotReceiveWithAnyArgs().IsAuthorizedDependencyAsync(
            default!,
            default!,
            default!,
            default);
    }

    [Fact]
    public async Task AdmitAsync_UsesVersionedRegistrationSnapshotForDecision()
    {
        var registration = CreateExplicitRegistration();
        var fixture = CreateAdmissionFixture([registration], stateVersion: 23);

        var result = await fixture.Port.AdmitAsync(CreateRequest(
            registration,
            serviceInstanceId: "svc-business"));

        result.Allowed.Should().BeTrue();
        await fixture.Query.Received(1).ListSnapshotsByNyxAgentApiKeyIdAsync(
            registration.ChannelAgentKey.ApiKeyId,
            Arg.Any<CancellationToken>());
        await fixture.Query.DidNotReceiveWithAnyArgs().ListByNyxAgentApiKeyIdAsync(
            default!,
            default);
    }

    [Fact]
    public async Task AdmitAsync_WithoutPositiveRegistrationStateVersion_ShouldDenyAuthorityUnavailable()
    {
        var registration = CreateExplicitRegistration();
        var fixture = CreateAdmissionFixture([registration], stateVersion: 0);

        var result = await fixture.Port.AdmitAsync(CreateRequest(
            registration,
            serviceInstanceId: "svc-business"));

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be(ChannelRegistrationAuthorityAdmissionReason.AuthorityUnavailable);
        await fixture.Resolver.DidNotReceiveWithAnyArgs().IsAuthorizedDependencyAsync(
            default!,
            default!,
            default!,
            default);
    }

    [Fact]
    public async Task AdmitAsync_ExplicitConfiguredDependencyAtExactCallSiteAndGrantedTarget_ShouldAllow()
    {
        const string callSiteId = "workflow-alpha/invoke-service";
        var registration = CreateExplicitRegistration();
        var fixture = CreateAdmissionFixture([registration]);
        fixture.Resolver.IsAuthorizedDependencyAsync(
                registration.ScopeId,
                callSiteId,
                "svc-dependency",
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var result = await fixture.Port.AdmitAsync(CreateRequest(
            registration,
            serviceInstanceId: "svc-dependency",
            callSiteId));

        result.Allowed.Should().BeTrue();
        result.Reason.Should().Be(ChannelRegistrationAuthorityAdmissionReason.Allowed);
        await fixture.Resolver.Received(1).IsAuthorizedDependencyAsync(
            registration.ScopeId,
            callSiteId,
            "svc-dependency",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdmitAsync_ExplicitGrantOnlyTarget_ShouldDenyTargetNotAuthorized()
    {
        var registration = CreateExplicitRegistration();
        var fixture = CreateAdmissionFixture([registration]);

        var result = await fixture.Port.AdmitAsync(CreateRequest(
            registration,
            serviceInstanceId: "svc-grant-only",
            callSiteId: "workflow-alpha/invoke-service"));

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be(ChannelRegistrationAuthorityAdmissionReason.TargetNotAuthorized);
    }

    [Fact]
    public async Task AdmitAsync_ExplicitDependencyAtWrongCallSite_ShouldDenyTargetNotAuthorized()
    {
        const string configuredCallSiteId = "workflow-alpha/invoke-service";
        var registration = CreateExplicitRegistration();
        var fixture = CreateAdmissionFixture([registration]);
        fixture.Resolver.IsAuthorizedDependencyAsync(
                registration.ScopeId,
                configuredCallSiteId,
                "svc-dependency",
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var result = await fixture.Port.AdmitAsync(CreateRequest(
            registration,
            serviceInstanceId: "svc-dependency",
            callSiteId: "workflow-alpha/other-call-site"));

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be(ChannelRegistrationAuthorityAdmissionReason.TargetNotAuthorized);
        await fixture.Resolver.Received(1).IsAuthorizedDependencyAsync(
            registration.ScopeId,
            "workflow-alpha/other-call-site",
            "svc-dependency",
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" workflow-alpha/invoke-service")]
    [InlineData("workflow-alpha/invoke-service ")]
    public async Task AdmitAsync_ExplicitDependencyWithoutCanonicalCallSite_ShouldDenyWithoutResolver(
        string? callSiteId)
    {
        var registration = CreateExplicitRegistration();
        var fixture = CreateAdmissionFixture([registration]);
        fixture.Resolver.IsAuthorizedDependencyAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var result = await fixture.Port.AdmitAsync(CreateRequest(
            registration,
            serviceInstanceId: "svc-dependency",
            callSiteId));

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be(ChannelRegistrationAuthorityAdmissionReason.TargetNotAuthorized);
        await fixture.Resolver.DidNotReceiveWithAnyArgs().IsAuthorizedDependencyAsync(
            default!,
            default!,
            default!,
            default);
    }

    [Fact]
    public async Task AdmitAsync_ExplicitDependencyOutsideActualKeyGrant_ShouldDenyTargetNotGranted()
    {
        const string callSiteId = "workflow-alpha/invoke-service";
        var registration = CreateExplicitRegistration();
        var fixture = CreateAdmissionFixture([registration]);
        fixture.Resolver.IsAuthorizedDependencyAsync(
                registration.ScopeId,
                callSiteId,
                "svc-not-granted",
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var result = await fixture.Port.AdmitAsync(CreateRequest(
            registration,
            serviceInstanceId: "svc-not-granted",
            callSiteId));

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be(ChannelRegistrationAuthorityAdmissionReason.TargetNotGranted);
        await fixture.Resolver.DidNotReceiveWithAnyArgs().IsAuthorizedDependencyAsync(
            default!,
            default!,
            default!,
            default);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" svc-business")]
    [InlineData("svc-unknown")]
    public async Task AdmitAsync_BlankNonCanonicalOrUnknownTarget_ShouldDenyTargetNotGranted(
        string serviceInstanceId)
    {
        var registration = CreateExplicitRegistration();
        var fixture = CreateAdmissionFixture([registration]);

        var result = await fixture.Port.AdmitAsync(CreateRequest(
            registration,
            serviceInstanceId,
            callSiteId: "workflow-alpha/invoke-service"));

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be(ChannelRegistrationAuthorityAdmissionReason.TargetNotGranted);
    }

    [Fact]
    public async Task AdmitAsync_InvalidAuthorizationContract_ShouldDenyAuthorizationContractInvalid()
    {
        var registration = CreateExplicitRegistration();
        registration.ChannelAgentKey.Grant.ScopePlanDigest = string.Empty;
        var fixture = CreateAdmissionFixture([registration]);

        var result = await fixture.Port.AdmitAsync(CreateRequest(
            registration,
            serviceInstanceId: "svc-business"));

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be(
            ChannelRegistrationAuthorityAdmissionReason.AuthorizationContractInvalid);
    }

    [Fact]
    public async Task AdmitAsync_WithoutRegistration_ShouldDenyRegistrationMissing()
    {
        var registration = CreateExplicitRegistration();
        var fixture = CreateAdmissionFixture([]);

        var result = await fixture.Port.AdmitAsync(CreateRequest(
            registration,
            serviceInstanceId: "svc-business"));

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be(ChannelRegistrationAuthorityAdmissionReason.RegistrationMissing);
    }

    [Fact]
    public async Task AdmitAsync_WithMultipleRegistrations_ShouldDenyRegistrationAmbiguous()
    {
        var registration = CreateExplicitRegistration();
        var duplicate = registration.Clone();
        duplicate.Id = "registration-beta";
        var fixture = CreateAdmissionFixture([registration, duplicate]);

        var result = await fixture.Port.AdmitAsync(CreateRequest(
            registration,
            serviceInstanceId: "svc-business"));

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be(ChannelRegistrationAuthorityAdmissionReason.RegistrationAmbiguous);
    }

    [Fact]
    public async Task AdmitAsync_WhenSubjectDoesNotMatchRegistrationApiKey_ShouldDenyDescriptorMismatch()
    {
        var registration = CreateExplicitRegistration();
        var credential = CreateCredential(registration);
        credential.SubjectId = "key-other";
        var fixture = CreateAdmissionFixture([registration]);

        var result = await fixture.Port.AdmitAsync(new ChannelRegistrationAuthorityAdmissionRequest(
            credential,
            CreateOperation("svc-business")));

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be(
            ChannelRegistrationAuthorityAdmissionReason.CredentialDescriptorMismatch);
        await fixture.Query.Received(1).ListSnapshotsByNyxAgentApiKeyIdAsync(
            "key-other",
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("ref")]
    [InlineData("purpose")]
    [InlineData("owner_scope_key")]
    [InlineData("version")]
    [InlineData("fingerprint")]
    [InlineData("created_at_unix_ms")]
    [InlineData("expires_at_unix_ms")]
    public async Task AdmitAsync_WhenNestedSecretDescriptorDiffers_ShouldDenyDescriptorMismatch(
        string field)
    {
        var registration = CreateExplicitRegistration();
        var credential = CreateCredential(registration);
        MutateSecretReference(credential.SecretReference, field);
        var fixture = CreateAdmissionFixture([registration]);

        var result = await fixture.Port.AdmitAsync(new ChannelRegistrationAuthorityAdmissionRequest(
            credential,
            CreateOperation("svc-business")));

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be(
            ChannelRegistrationAuthorityAdmissionReason.CredentialDescriptorMismatch);
    }

    [Fact]
    public async Task AdmitAsync_WithoutNestedSecretDescriptor_ShouldDenyDescriptorMismatch()
    {
        var registration = CreateExplicitRegistration();
        var credential = CreateCredential(registration);
        credential.SecretReference = null;
        var fixture = CreateAdmissionFixture([registration]);

        var result = await fixture.Port.AdmitAsync(new ChannelRegistrationAuthorityAdmissionRequest(
            credential,
            CreateOperation("svc-business")));

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be(
            ChannelRegistrationAuthorityAdmissionReason.CredentialDescriptorMismatch);
    }

    [Theory]
    [InlineData("source_kind")]
    [InlineData("ref")]
    [InlineData("purpose")]
    [InlineData("owner_scope_key")]
    public async Task AdmitAsync_WhenSourceKindOrTopLevelAliasIsMalformed_ShouldDenyDescriptorMismatch(
        string field)
    {
        var registration = CreateExplicitRegistration();
        var credential = CreateCredential(registration);
        switch (field)
        {
            case "source_kind":
                credential.SourceKind = DurableCallerCredentialSourceKind.NyxIdChat;
                break;
            case "ref":
                credential.Ref = "vault://channel/key-other";
                break;
            case "purpose":
                credential.Purpose = "channel.other-agent-key";
                break;
            case "owner_scope_key":
                credential.OwnerScopeKey = "scope-other";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }

        var fixture = CreateAdmissionFixture([registration]);
        var result = await fixture.Port.AdmitAsync(new ChannelRegistrationAuthorityAdmissionRequest(
            credential,
            CreateOperation("svc-business")));

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be(
            ChannelRegistrationAuthorityAdmissionReason.CredentialDescriptorMismatch);
    }

    [Fact]
    public async Task AdmitAsync_ValidNyxIdDefaultRegistration_ShouldAllowAfterExactDescriptorValidation()
    {
        var registration = CreateDefaultRegistration();
        var fixture = CreateAdmissionFixture([registration]);

        var result = await fixture.Port.AdmitAsync(CreateRequest(
            registration,
            serviceInstanceId: "svc-default"));

        result.Allowed.Should().BeTrue();
        result.Reason.Should().Be(ChannelRegistrationAuthorityAdmissionReason.Allowed);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("dependency")]
    public async Task AdmitAsync_WhenAuthoritySourceThrows_PropagatesOriginalException(
        string source)
    {
        var registration = CreateExplicitRegistration();
        var expectedException =
            new InvalidOperationException("provider body contains key-secret-123");
        var query = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        query.ListSnapshotsByNyxAgentApiKeyIdAsync(
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(source == "query"
                ? Task.FromException<IReadOnlyList<ChannelBotRegistrationSnapshot>>(
                    expectedException)
                : Task.FromResult<IReadOnlyList<ChannelBotRegistrationSnapshot>>(
                    [new ChannelBotRegistrationSnapshot(registration, 17)]));
        var resolver = Substitute.For<IChannelRegistrationCallSiteDependencyResolver>();
        resolver.IsAuthorizedDependencyAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(
                expectedException));
        var port = new ChannelRegistrationAuthorityAdmissionPort(
            query,
            resolver);

        var act = () => port.AdmitAsync(CreateRequest(
            registration,
            serviceInstanceId: source == "query" ? "svc-private-target" : "svc-dependency",
            callSiteId: "workflow-alpha/private-call-site"));

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Should().BeSameAs(expectedException);
    }

    [Fact]
    public async Task AdmitAsync_TrueHistoricalRegistration_ShouldAllowAfterExactLegacyDescriptorValidation()
    {
        var registration = CreateHistoricalRegistration();
        var fixture = CreateAdmissionFixture([registration]);

        var result = await fixture.Port.AdmitAsync(CreateRequest(
            registration,
            serviceInstanceId: "svc-legacy"));

        result.Allowed.Should().BeTrue();
        result.Reason.Should().Be(ChannelRegistrationAuthorityAdmissionReason.Allowed);
    }

    [Fact]
    public async Task AdmitAsync_HistoricalRegistrationWithIncompleteDescriptor_ShouldDenyDescriptorMismatch()
    {
        var registration = CreateHistoricalRegistration();
        registration.WorkflowResultDeliveryCredential.Fingerprint = string.Empty;
        var fixture = CreateAdmissionFixture([registration]);

        var result = await fixture.Port.AdmitAsync(CreateRequest(
            registration,
            serviceInstanceId: "svc-legacy"));

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Be(
            ChannelRegistrationAuthorityAdmissionReason.CredentialDescriptorMismatch);
    }

    [Fact]
    public async Task AddChannelRuntime_ShouldResolveAuthorityPortAndDenyByDefaultDependencyResolver()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>());

        services.AddChannelRuntime();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IChannelRegistrationAuthorityAdmissionPort>()
            .Should().BeOfType<ChannelRegistrationAuthorityAdmissionPort>();
        var resolver = provider.GetRequiredService<IChannelRegistrationCallSiteDependencyResolver>();
        var allowed = await resolver.IsAuthorizedDependencyAsync(
            "scope-alpha",
            "workflow-alpha/invoke-service",
            "svc-dependency");
        allowed.Should().BeFalse();
    }

    [Fact]
    public void AddChannelRuntime_ShouldPreservePreRegisteredDependencyResolver()
    {
        var services = new ServiceCollection();
        var customResolver = Substitute.For<IChannelRegistrationCallSiteDependencyResolver>();
        services.AddSingleton(customResolver);

        services.AddChannelRuntime();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IChannelRegistrationCallSiteDependencyResolver>()
            .Should().BeSameAs(customResolver);
    }

    private static WorkflowCapabilityInvocationAdmission CreateWorkflowAdmission(string callSiteId) =>
        new()
        {
            CallSiteId = callSiteId,
            Capability = new ExternalWorkflowCapabilityRef
            {
                NyxIdUserService = new NyxIdUserServiceCapabilityRef
                {
                    UserServiceId = "svc-alpha",
                    ServiceSlugSnapshot = "service-alpha",
                    EndpointId = "invoke-service",
                    HttpMethod = "POST",
                    PathTemplate = "/invoke",
                    ContractDigest = "digest-alpha",
                    ExecutionPolicy = new NyxIdOperationExecutionPolicy
                    {
                        Risk = NyxIdOperationRisk.Write,
                        Approval = NyxIdOperationApproval.Required,
                        EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
                        AllowedExecutionModes = { ExternalCapabilityExecutionMode.Interactive },
                    },
                },
            },
        };

    private static AdmissionFixture CreateAdmissionFixture(
        IReadOnlyList<ChannelBotRegistrationEntry> registrations,
        long stateVersion = 17)
    {
        var query = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        query.ListSnapshotsByNyxAgentApiKeyIdAsync(
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationSnapshot>>(
                registrations.Select(registration =>
                    new ChannelBotRegistrationSnapshot(registration, stateVersion)).ToArray()));
        var resolver = Substitute.For<IChannelRegistrationCallSiteDependencyResolver>();
        return new AdmissionFixture(
            new ChannelRegistrationAuthorityAdmissionPort(query, resolver),
            query,
            resolver);
    }

    private static ChannelRegistrationAuthorityAdmissionRequest CreateRequest(
        ChannelBotRegistrationEntry registration,
        string serviceInstanceId,
        string? callSiteId = "") =>
        new(
            CreateCredential(registration),
            CreateOperation(serviceInstanceId, callSiteId));

    private static DurableCallerCredentialRef CreateCredential(
        ChannelBotRegistrationEntry registration)
    {
        var apiKeyId = registration.ChannelAgentKey?.ApiKeyId ?? registration.NyxAgentApiKeyId;
        var reference = registration.ChannelAgentKey?.SecretReference ??
                        registration.WorkflowResultDeliveryCredential;
        return new DurableCallerCredentialRef
        {
            Ref = reference.Ref,
            Purpose = reference.Purpose,
            OwnerScopeKey = reference.OwnerScopeKey,
            SubjectId = apiKeyId,
            SourceKind = DurableCallerCredentialSourceKind.ChannelRegistration,
            SecretReference = reference.Clone(),
        };
    }

    private static AgentToolOperationAdmission CreateOperation(
        string serviceInstanceId,
        string? callSiteId = "") =>
        new(
            serviceInstanceId,
            "service-alpha",
            new AgentToolOperationIdentity.PublishedEndpoint("endpoint-alpha"),
            AgentToolOperationAuthorizationBasis.PublishedContract,
            "POST",
            "/invoke",
            "contract-alpha",
            [],
            null,
            AgentToolOperationResponsePolicy.TextOnly,
            AgentToolOperationExecutionPolicy.Unspecified,
            CallSiteId: callSiteId!);

    private static void MutateSecretReference(SecretReference reference, string field)
    {
        switch (field)
        {
            case "ref":
                reference.Ref += "-other";
                break;
            case "purpose":
                reference.Purpose = "channel.other-agent-key";
                break;
            case "owner_scope_key":
                reference.OwnerScopeKey = "scope-other";
                break;
            case "version":
                reference.Version++;
                break;
            case "fingerprint":
                reference.Fingerprint += "-other";
                break;
            case "created_at_unix_ms":
                reference.CreatedAtUnixMs++;
                break;
            case "expires_at_unix_ms":
                reference.ExpiresAtUnixMs++;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }
    }

    private static ChannelBotRegistrationEntry CreateExplicitRegistration()
    {
        var reference = CreateCompleteReference("scope-alpha", "key-alpha");
        return new ChannelBotRegistrationEntry
        {
            Id = "registration-alpha",
            ScopeId = "scope-alpha",
            NyxAgentApiKeyId = "key-alpha",
            WorkflowResultDeliveryCredential = reference.Clone(),
            AuthorizationMode = ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist,
            RegistrationServiceAllowlist = new ChannelRegistrationServiceAllowlist
            {
                ServiceIds = { "svc-business" },
            },
            ChannelAgentKey = new ChannelAgentKeyCredential
            {
                ApiKeyId = "key-alpha",
                SecretReference = reference,
                Grant = new ChannelAgentKeyGrantSnapshot
                {
                    AllowAllServices = false,
                    AllowAllNodes = false,
                    ScopePlanDigest = "sha256:" + new string('a', 64),
                    AllowedServiceIds =
                    {
                        "svc-business",
                        "svc-dependency",
                        "svc-grant-only",
                    },
                },
            },
        };
    }

    private static ChannelBotRegistrationEntry CreateDefaultRegistration()
    {
        var reference = CreateCompleteReference("scope-default", "key-default");
        return new ChannelBotRegistrationEntry
        {
            Id = "registration-default",
            ScopeId = "scope-default",
            NyxAgentApiKeyId = "key-default",
            WorkflowResultDeliveryCredential = reference.Clone(),
            AuthorizationMode = ChannelRegistrationAuthorizationMode.NyxidDefault,
            ChannelAgentKey = new ChannelAgentKeyCredential
            {
                ApiKeyId = "key-default",
                SecretReference = reference,
                Grant = new ChannelAgentKeyGrantSnapshot
                {
                    AllowAllServices = true,
                    AllowAllNodes = true,
                },
            },
        };
    }

    private static ChannelBotRegistrationEntry CreateHistoricalRegistration() =>
        new()
        {
            Id = "registration-legacy",
            ScopeId = "scope-legacy",
            NyxAgentApiKeyId = "key-legacy",
            WorkflowResultDeliveryCredential = CreateCompleteReference(
                "scope-legacy",
                "key-legacy"),
        };

    private static SecretReference CreateCompleteReference(string scopeId, string apiKeyId) =>
        new()
        {
            Ref = $"vault://channel/{apiKeyId}",
            Purpose = CredentialSecretPurposes.ChannelNyxIdAgentKey,
            Fingerprint = $"fingerprint-{apiKeyId}",
            Version = 1,
            OwnerScopeKey = scopeId,
            CreatedAtUnixMs = 1,
            ExpiresAtUnixMs = 0,
        };

    private sealed record AdmissionFixture(
        IChannelRegistrationAuthorityAdmissionPort Port,
        IChannelBotRegistrationQueryByNyxIdentityPort Query,
        IChannelRegistrationCallSiteDependencyResolver Resolver);
}
