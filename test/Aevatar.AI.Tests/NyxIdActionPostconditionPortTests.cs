using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public sealed class NyxIdActionPostconditionPortTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task VerifyAsync_ExactCatalogService_ShouldReturnVerifiedTypedResource()
    {
        var query = new StubCatalogQueryPort(ReadySnapshot());
        var port = CreatePort(query);

        var result = await port.VerifyAsync(CatalogInput());

        query.Owners.Should().ContainSingle().Which.Should().BeEquivalentTo(
            PersonalOwner());
        result.ActionRequestId.Should().Be("action-alpha");
        result.Disposition.Should().Be(NyxIdChatActionDisposition.Completed);
        result.Verified.Should().BeTrue();
        result.Resource.ResourceCase.Should().Be(
            NyxIdChatSafeResourceRef.ResourceOneofCase.UserService);
        result.Resource.UserService.UserServiceId.Should().Be("service-alpha");
        result.FailureCode.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyAsync_StateChangeWake_ShouldDeriveCompletionOnlyFromTypedReadModel()
    {
        var input = CatalogInput();
        input.ReportedDisposition = NyxIdChatActionDisposition.Unspecified;
        input.ResourceHint = null;
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()));

        var result = await port.VerifyAsync(input);

        result.Verified.Should().BeTrue();
        result.Disposition.Should().Be(NyxIdChatActionDisposition.Completed);
        result.Resource.UserService.UserServiceId.Should().Be("service-alpha");
    }

    [Fact]
    public async Task VerifyAsync_MissingSnapshotWithoutResourceHint_ShouldFailClosedWithoutThrowing()
    {
        var input = CatalogInput();
        input.ResourceHint = null;
        var port = CreatePort(new StubCatalogQueryPort(null));

        var result = await port.VerifyAsync(input);

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.UnavailableCode);
        result.Resource.Should().BeNull();
    }

    [Fact]
    public async Task VerifyAsync_StaleSnapshot_ShouldRemainUnverified()
    {
        var snapshot = ReadySnapshot() with { FreshUntilUtc = Now };
        var port = CreatePort(new StubCatalogQueryPort(snapshot));

        var result = await port.VerifyAsync(CatalogInput());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.StaleCode);
    }

    [Fact]
    public async Task VerifyAsync_ExactFreshService_ShouldIgnoreStaleOwnerCatalogStamp()
    {
        var service = ServiceWithAuthorityWindow(
            "service-alpha",
            "api-github",
            Now.AddMinutes(-1),
            Now.AddMinutes(5));
        var snapshot = ReadySnapshot(service) with { FreshUntilUtc = Now };
        var port = CreatePort(new StubCatalogQueryPort(snapshot));

        var result = await port.VerifyAsync(CatalogInput());

        result.Verified.Should().BeTrue();
        result.Resource.UserService.UserServiceId.Should().Be("service-alpha");
    }

    [Fact]
    public async Task VerifyAsync_ExactStaleService_ShouldRemainUnverified()
    {
        var service = ServiceWithAuthorityWindow(
            "service-alpha",
            "api-github",
            Now.AddMinutes(-1),
            Now);
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot(service)));

        var result = await port.VerifyAsync(CatalogInput());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.StaleCode);
    }

    [Fact]
    public async Task VerifyAsync_InvalidDigest_ShouldRemainUnavailable()
    {
        var snapshot = ReadySnapshot() with { ContentDigest = "digest-forged" };
        var port = CreatePort(new StubCatalogQueryPort(snapshot));

        var result = await port.VerifyAsync(CatalogInput());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.UnavailableCode);
    }

    [Fact]
    public async Task VerifyAsync_ValidDigestWithGatewayLLMTarget_ShouldVerify()
    {
        var snapshot = ReadySnapshot();
        var gatewayTarget = new NyxIdAuthorizationLLMTargetEvidence
        {
            RouteKind = LLMRouteKind.Gateway,
            RouteValue = "gateway",
            ModelCatalog = new LLMModelCatalog
            {
                Certainty = LLMModelCatalogCertainty.Enumerated,
                DefaultModelId = "gpt-5.5",
                ModelIds = { "gpt-5.5" },
            },
        };
        snapshot = snapshot with
        {
            ContentDigest = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
                snapshot.Owner,
                snapshot.Services,
                gatewayTarget),
            GatewayLLMTarget = gatewayTarget,
        };
        var port = CreatePort(new StubCatalogQueryPort(snapshot));

        var result = await port.VerifyAsync(CatalogInput());

        result.Verified.Should().BeTrue();
        result.FailureCode.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyAsync_MismatchedResourceId_ShouldRemainUnverified()
    {
        var input = CatalogInput();
        input.ResourceHint.UserService.UserServiceId = "service-other";
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()));

        var result = await port.VerifyAsync(input);

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.MismatchCode);
    }

    [Fact]
    public async Task VerifyAsync_MismatchedCatalogSlug_ShouldRemainUnverified()
    {
        var input = CatalogInput();
        input.Params.CatalogServiceConnect.ServiceSlug = "api-slack";
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()));

        var result = await port.VerifyAsync(input);

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.MismatchCode);
    }

    [Fact]
    public async Task VerifyAsync_MultipleCatalogMatchesWithoutHint_ShouldRemainAmbiguous()
    {
        var input = CatalogInput();
        input.ResourceHint = null;
        var snapshot = ReadySnapshot(
            Service("service-alpha", "api-github"),
            Service("service-beta", "api-github"));
        var port = CreatePort(new StubCatalogQueryPort(snapshot));

        var result = await port.VerifyAsync(input);

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.AmbiguousCode);
    }

    [Fact]
    public async Task VerifyAsync_CustomServiceWithoutExactTypedHint_ShouldFailClosed()
    {
        var input = CatalogInput();
        input.ResourceHint = null;
        input.Params = new NyxIdAssistantActionParams
        {
            CustomServiceConnect = new NyxIdCustomServiceConnectParams
            {
                Name = "Internal API",
                EndpointUrl = "https://api.internal.example.com/",
                AuthMethod = "bearer",
            },
        };
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()));

        var result = await port.VerifyAsync(input);

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.MismatchCode);
    }

    [Fact]
    public async Task VerifyAsync_ServiceReauthorizeExactProviderEvidence_ShouldVerify()
    {
        var input = ReauthorizeInput();
        var query = new StubCatalogQueryPort(ReadySnapshot());
        var evidence = new StubActionEvidenceReadPort
        {
            UserService = AuthorizedService(),
        };
        var port = CreatePort(query, evidence);

        var result = await port.VerifyAsync(input, ReadContext());

        query.Owners.Should().BeEmpty();
        evidence.UserServiceReads.Should().ContainSingle().Which.Should().Be("service-alpha");
        evidence.BearerTokens.Should().ContainSingle().Which.Should().Be("bearer-secret");
        result.Verified.Should().BeTrue();
        result.Resource.UserService.UserServiceId.Should().Be("service-alpha");
        result.ToString().Should().NotContain("bearer-secret");
    }

    [Fact]
    public async Task VerifyAsync_ServiceReauthorizeMissingRequestedScope_ShouldMismatch()
    {
        var evidence = new StubActionEvidenceReadPort
        {
            UserService = AuthorizedService() with { GrantedScopes = ["read:user"] },
        };
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()), evidence);

        var result = await port.VerifyAsync(ReauthorizeInput(), ReadContext());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.MismatchCode);
    }

    [Theory]
    [InlineData(NyxIdOAuthConnectionStatus.Expired)]
    [InlineData(NyxIdOAuthConnectionStatus.Unspecified)]
    public async Task VerifyAsync_ServiceReauthorizeWithoutActiveOAuthConnection_ShouldMismatch(
        NyxIdOAuthConnectionStatus connectionStatus)
    {
        var evidence = new StubActionEvidenceReadPort
        {
            UserService = AuthorizedService() with { OAuthConnectionStatus = connectionStatus },
        };
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()), evidence);

        var result = await port.VerifyAsync(ReauthorizeInput(), ReadContext());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.MismatchCode);
    }

    [Fact]
    public async Task VerifyAsync_ServiceReauthorizeMissingAuthorizationTimestamp_ShouldBeStale()
    {
        var evidence = new StubActionEvidenceReadPort
        {
            UserService = AuthorizedService() with { LastAuthorizedAtUtc = null },
        };
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()), evidence);

        var result = await port.VerifyAsync(ReauthorizeInput(), ReadContext());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.StaleCode);
    }

    [Fact]
    public async Task VerifyAsync_ServiceReauthorizeEvidenceBeforeRequest_ShouldBeStale()
    {
        var evidence = new StubActionEvidenceReadPort
        {
            UserService = AuthorizedService() with { LastAuthorizedAtUtc = Now.AddMinutes(-3) },
        };
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()), evidence);

        var result = await port.VerifyAsync(ReauthorizeInput(), ReadContext());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.StaleCode);
    }

    [Fact]
    public async Task VerifyAsync_ServiceReauthorizeMismatchedHint_ShouldFailBeforeRead()
    {
        var input = ReauthorizeInput();
        input.ResourceHint!.UserService.UserServiceId = "service-other";
        var query = new StubCatalogQueryPort(ReadySnapshot());
        var port = CreatePort(query);

        var result = await port.VerifyAsync(input);

        query.Owners.Should().BeEmpty();
        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.MismatchCode);
    }

    [Fact]
    public async Task VerifyAsync_ServiceReauthorizeKeyHint_ShouldFailBeforeRead()
    {
        var input = ReauthorizeInput();
        input.ResourceHint = new NyxIdChatSafeResourceRef
        {
            Key = new NyxIdChatKeyRef { KeyId = "key-alpha" },
        };
        var query = new StubCatalogQueryPort(ReadySnapshot());
        var port = CreatePort(query);

        var result = await port.VerifyAsync(input);

        query.Owners.Should().BeEmpty();
        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.MismatchCode);
    }

    [Fact]
    public async Task VerifyAsync_ServiceAccessReviewExactMcpVisibility_ShouldVerify()
    {
        var query = new StubCatalogQueryPort(ReadySnapshot());
        var evidence = new StubActionEvidenceReadPort
        {
            ServiceAccess = new NyxIdServiceAccessEvidence(
                "service-alpha",
                "api-github"),
        };
        var port = CreatePort(query, evidence);

        var result = await port.VerifyAsync(ServiceAccessReviewInput(), ReadContext());

        query.Owners.Should().BeEmpty();
        evidence.ServiceAccessReads.Should().ContainSingle().Which.Should().Be(
            ("service-alpha", "api-github"));
        evidence.BearerTokens.Should().ContainSingle().Which.Should().Be("bearer-secret");
        result.Verified.Should().BeTrue();
        result.Resource.UserService.UserServiceId.Should().Be("service-alpha");
        result.ToString().Should().NotContain("bearer-secret");
    }

    [Fact]
    public async Task VerifyAsync_KeyCreateExactProviderEvidence_ShouldVerify()
    {
        var query = new StubCatalogQueryPort(ReadySnapshot());
        var evidence = new StubActionEvidenceReadPort
        {
            AgentApiKey = AgentKey(),
        };
        var port = CreatePort(query, evidence);

        var result = await port.VerifyAsync(KeyCreateInput(), ReadContext());

        query.Owners.Should().BeEmpty();
        evidence.AgentKeyReads.Should().ContainSingle().Which.Should().Be("key-alpha");
        result.Verified.Should().BeTrue();
        result.Resource.Key.KeyId.Should().Be("key-alpha");
    }

    [Fact]
    public async Task VerifyAsync_KeyCreateHumanSessionDelegation_ShouldVerify()
    {
        var evidence = new StubActionEvidenceReadPort
        {
            AgentApiKey = AgentKey(),
        };
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()), evidence);

        var result = await port.VerifyAsync(KeyCreateInput(), HumanDelegationReadContext());

        result.Verified.Should().BeTrue();
        evidence.BearerTokens.Should().ContainSingle().Which.Should().Be("delegated-secret");
        result.ToString().Should().NotContain("delegated-secret");
    }

    [Fact]
    public async Task VerifyAsync_KeyCreateRawProxyDelegation_ShouldNotRead()
    {
        var evidence = new StubActionEvidenceReadPort
        {
            AgentApiKey = AgentKey(),
        };
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()), evidence);
        var context = ReadContext();
        context.Credentials.NyxIdCredentialKind =
            AgentToolNyxIdCredentialKindPayload.ProxyDelegation;

        var result = await port.VerifyAsync(KeyCreateInput(), context);

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.UnavailableCode);
        evidence.AgentKeyReads.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyAsync_KeyCreatePlatformMismatch_ShouldFailClosed()
    {
        var evidence = new StubActionEvidenceReadPort
        {
            AgentApiKey = AgentKey() with { Platform = "claude-code" },
        };
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()), evidence);

        var result = await port.VerifyAsync(KeyCreateInput(), ReadContext());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.MismatchCode);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("all-services")]
    [InlineData("all-nodes")]
    [InlineData("node")]
    public async Task VerifyAsync_KeyCreateBroaderThanRequested_ShouldFailClosed(string variant)
    {
        var key = AgentKey();
        key = variant switch
        {
            "scope" => key with { Scopes = ["proxy", "write"] },
            "all-services" => key with { AllowAllServices = true },
            "all-nodes" => key with { AllowAllNodes = true },
            "node" => key with { AllowedNodeIds = ["node-alpha"] },
            _ => throw new InvalidOperationException(),
        };
        var evidence = new StubActionEvidenceReadPort { AgentApiKey = key };
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()), evidence);

        var result = await port.VerifyAsync(KeyCreateInput(), ReadContext());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.MismatchCode);
    }

    [Fact]
    public async Task VerifyAsync_KeyCreateWithoutExactServiceScope_ShouldRejectBeforeRead()
    {
        var input = KeyCreateInput();
        input.Params.KeyCreate.AllowedServiceIds.Clear();
        var evidence = new StubActionEvidenceReadPort { AgentApiKey = AgentKey() };
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()), evidence);

        var result = await port.VerifyAsync(input, ReadContext());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.InvalidInputCode);
        evidence.AgentKeyReads.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyAsync_KeyCreateEvidenceBeforeRequest_ShouldBeStale()
    {
        var evidence = new StubActionEvidenceReadPort
        {
            AgentApiKey = AgentKey() with { CreatedAtUtc = Now.AddMinutes(-3) },
        };
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()), evidence);

        var result = await port.VerifyAsync(KeyCreateInput(), ReadContext());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.StaleCode);
    }

    [Fact]
    public async Task VerifyAsync_KeyRotateWithoutProviderLineage_ShouldRemainUncertain()
    {
        var evidence = new StubActionEvidenceReadPort
        {
            AgentApiKey = AgentKey("key-beta"),
        };
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()), evidence);

        var result = await port.VerifyAsync(KeyRotateInput(), ReadContext());

        result.Verified.Should().BeFalse();
        result.Disposition.Should().Be(NyxIdChatActionDisposition.Completed);
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.LineageUnavailableCode);
        result.Resource.Key.KeyId.Should().Be("key-beta");
    }

    [Fact]
    public async Task VerifyAsync_KeyRotateExactVersionedLineage_ShouldVerify()
    {
        var evidence = new StubActionEvidenceReadPort
        {
            AgentApiKey = AgentKey("key-beta") with
            {
                VersionEvidence = new NyxIdApiKeyVersionEvidence(
                    "key-alpha",
                    2,
                    Now.AddMinutes(-1)),
            },
        };
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()), evidence);

        var result = await port.VerifyAsync(KeyRotateInput(), ReadContext());

        result.Verified.Should().BeTrue();
        result.Resource.Key.KeyId.Should().Be("key-beta");
    }

    [Fact]
    public async Task VerifyAsync_KeyRotateLineageWithoutUpdateTimestamp_ShouldVerify()
    {
        // A projection row may serialize updated_at as null; the monotonic
        // state_version plus the exact predecessor identity stay authoritative.
        var evidence = new StubActionEvidenceReadPort
        {
            AgentApiKey = AgentKey("key-beta") with
            {
                VersionEvidence = new NyxIdApiKeyVersionEvidence(
                    "key-alpha",
                    2,
                    null),
            },
        };
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()), evidence);

        var result = await port.VerifyAsync(KeyRotateInput(), ReadContext());

        result.Verified.Should().BeTrue();
        result.Resource.Key.KeyId.Should().Be("key-beta");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task VerifyAsync_KeyRotateNonPositiveAuthorityVersion_ShouldFailClosed(
        long stateVersion)
    {
        var evidence = new StubActionEvidenceReadPort
        {
            AgentApiKey = AgentKey("key-beta") with
            {
                VersionEvidence = new NyxIdApiKeyVersionEvidence(
                    "key-alpha",
                    stateVersion,
                    Now.AddMinutes(-1)),
            },
        };
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()), evidence);

        var result = await port.VerifyAsync(KeyRotateInput(), ReadContext());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.LineageUnavailableCode);
    }

    [Fact]
    public async Task VerifyAsync_KeyRotateLineageBeforeRequest_ShouldBeStale()
    {
        var evidence = new StubActionEvidenceReadPort
        {
            AgentApiKey = AgentKey("key-beta") with
            {
                VersionEvidence = new NyxIdApiKeyVersionEvidence(
                    "key-alpha",
                    2,
                    Now.AddMinutes(-3)),
            },
        };
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()), evidence);

        var result = await port.VerifyAsync(KeyRotateInput(), ReadContext());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.StaleCode);
    }

    [Fact]
    public async Task VerifyAsync_KeyRotateOldSuccessorUpdatedAfterRequest_ShouldBeStale()
    {
        var evidence = new StubActionEvidenceReadPort
        {
            AgentApiKey = AgentKey("key-beta") with
            {
                CreatedAtUtc = Now.AddMinutes(-3),
                VersionEvidence = new NyxIdApiKeyVersionEvidence(
                    "key-alpha",
                    3,
                    Now.AddMinutes(-1)),
            },
        };
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()), evidence);

        var result = await port.VerifyAsync(KeyRotateInput(), ReadContext());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.StaleCode);
    }

    [Fact]
    public async Task VerifyAsync_ProviderActionWithoutRequestedAt_ShouldRejectBeforeRead()
    {
        var input = KeyCreateInput();
        input.RequestedAt = null;
        var evidence = new StubActionEvidenceReadPort { AgentApiKey = AgentKey() };
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()), evidence);

        var result = await port.VerifyAsync(input, ReadContext());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.InvalidInputCode);
        evidence.AgentKeyReads.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyAsync_ProviderActionWithoutTransientAuthority_ShouldNotRead()
    {
        var evidence = new StubActionEvidenceReadPort { AgentApiKey = AgentKey() };
        var port = CreatePort(new StubCatalogQueryPort(ReadySnapshot()), evidence);

        var result = await port.VerifyAsync(KeyCreateInput());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.UnavailableCode);
        evidence.AgentKeyReads.Should().BeEmpty();
    }

    private static NyxIdActionPostconditionPort CreatePort(
        INyxIdAuthorizationCatalogQueryPort query,
        INyxIdActionEvidenceReadPort? evidence = null) =>
        new(query, evidence, new FixedTimeProvider(Now));

    private static NyxIdChatActionPostconditionInput CatalogInput() => new()
    {
        ScopeId = "scope-alpha",
        OwnerSubject = "owner-alpha",
        OriginTurnId = "turn-origin-alpha",
        ActionRequestId = "action-alpha",
        Action = NyxIdAssistantActionKind.ServiceConnect,
        ReportedDisposition = NyxIdChatActionDisposition.Completed,
        ResourceHint = new NyxIdChatSafeResourceRef
        {
            UserService = new NyxIdChatUserServiceRef
            {
                UserServiceId = "service-alpha",
            },
        },
        Params = new NyxIdAssistantActionParams
        {
            CatalogServiceConnect = new NyxIdCatalogServiceConnectParams
            {
                ServiceSlug = "api-github",
            },
        },
    };

    private static NyxIdChatActionPostconditionInput ReauthorizeInput() => new()
    {
        ScopeId = "scope-alpha",
        OwnerSubject = "owner-alpha",
        OriginTurnId = "turn-origin-alpha",
        ActionRequestId = "action-alpha",
        Action = NyxIdAssistantActionKind.ServiceReauthorize,
        ReportedDisposition = NyxIdChatActionDisposition.Completed,
        RequestedAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(-2)),
        ResourceHint = new NyxIdChatSafeResourceRef
        {
            UserService = new NyxIdChatUserServiceRef
            {
                UserServiceId = "service-alpha",
            },
        },
        Params = new NyxIdAssistantActionParams
        {
            ServiceReauthorize = new NyxIdServiceReauthorizeParams
            {
                UserServiceId = "service-alpha",
                RequestedScopes = { "repo" },
            },
        },
    };

    private static NyxIdChatActionPostconditionInput ServiceAccessReviewInput() => new()
    {
        ScopeId = "scope-alpha",
        OwnerSubject = "owner-alpha",
        OriginTurnId = "turn-origin-alpha",
        ActionRequestId = "action-alpha",
        Action = NyxIdAssistantActionKind.ServiceAccessReview,
        ReportedDisposition = NyxIdChatActionDisposition.Completed,
        ResourceHint = new NyxIdChatSafeResourceRef
        {
            UserService = new NyxIdChatUserServiceRef
            {
                UserServiceId = "service-alpha",
            },
        },
        Params = new NyxIdAssistantActionParams
        {
            ServiceAccessReview = new NyxIdServiceAccessReviewParams
            {
                UserServiceId = "service-alpha",
                ServiceSlug = "api-github",
                ResourceUri =
                    "https://nyx-api.chrono-ai.fun/api/v1/proxy/s/api-github",
            },
        },
    };

    private static NyxIdChatActionPostconditionInput KeyCreateInput() => new()
    {
        ScopeId = "scope-alpha",
        OwnerSubject = "owner-alpha",
        OriginTurnId = "turn-origin-alpha",
        ActionRequestId = "action-alpha",
        Action = NyxIdAssistantActionKind.KeyCreate,
        ReportedDisposition = NyxIdChatActionDisposition.Completed,
        RequestedAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(-2)),
        ResourceHint = new NyxIdChatSafeResourceRef
        {
            Key = new NyxIdChatKeyRef { KeyId = "key-alpha" },
        },
        Params = new NyxIdAssistantActionParams
        {
            KeyCreate = new NyxIdKeyCreateParams
            {
                Name = "Key Alpha",
                Platform = "codex",
                AllowedServiceIds = { "service-alpha", "service-beta" },
            },
        },
    };

    private static NyxIdChatActionPostconditionInput KeyRotateInput() => new()
    {
        ScopeId = "scope-alpha",
        OwnerSubject = "owner-alpha",
        OriginTurnId = "turn-origin-alpha",
        ActionRequestId = "action-alpha",
        Action = NyxIdAssistantActionKind.KeyRotate,
        ReportedDisposition = NyxIdChatActionDisposition.Completed,
        RequestedAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(-2)),
        ResourceHint = new NyxIdChatSafeResourceRef
        {
            Key = new NyxIdChatKeyRef { KeyId = "key-beta" },
        },
        Params = new NyxIdAssistantActionParams
        {
            KeyRotate = new NyxIdKeyRotateParams { KeyId = "key-alpha" },
        },
    };

    private static AgentToolExecutionContextPayload ReadContext() => new()
    {
        Caller = new AgentToolCallerContextPayload
        {
            ScopeId = "scope-alpha",
            OwnerSubject = "owner-alpha",
        },
        Credentials = new AgentToolCredentialsPayload
        {
            NyxIdAccessToken = "bearer-secret",
            NyxIdCredentialKind = AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
        },
    };

    private static AgentToolExecutionContextPayload HumanDelegationReadContext() => new()
    {
        Caller = new AgentToolCallerContextPayload
        {
            ScopeId = "scope-alpha",
            OwnerScopeId = "scope-alpha",
            OwnerSubject = "owner-alpha",
        },
        Credentials = new AgentToolCredentialsPayload
        {
            NyxIdAccessToken = "delegated-secret",
            NyxIdCredentialKind = AgentToolNyxIdCredentialKindPayload.ProxyDelegation,
        },
        NyxIdAuthority = new AgentToolNyxIdAuthorityContextPayload
        {
            Platform = "nyxid",
            ExternalUserId = "owner-alpha",
            Scope = "proxy",
        },
        InvocationSurface = AgentToolInvocationSurfacePayload.HumanSession,
        Chat = new AgentChatInvocationContextPayload
        {
            Surface = AgentChatInvocationSurfacePayload.NyxidAssistant,
        },
    };

    private static NyxIdUserServiceAuthorizationEvidence AuthorizedService() => new(
        "service-alpha",
        "credential-alpha",
        true,
        NyxIdUserServiceCredentialStatus.Active,
        NyxIdOAuthConnectionStatus.Active,
        ["read:user", "repo"],
        Now.AddMinutes(-1),
        null);

    private static NyxIdAgentApiKeyEvidence AgentKey(string keyId = "key-alpha") => new(
        keyId,
        ["proxy"],
        "codex",
        true,
        ["service-beta", "service-alpha"],
        false,
        [],
        false,
        Now.AddMinutes(-1),
        null);

    private static AuthorizationOwnerIdentity PersonalOwner() => new()
    {
        Authority = NyxIdAuthorizationAuthorities.NyxId,
        OwnerKind = AuthorizationOwnerKind.Personal,
        OwnerSubject = "owner-alpha",
    };

    private static NyxIdAuthorizationCatalogSnapshot ReadySnapshot(
        params NyxIdAuthorizationServiceEvidence[] services)
    {
        if (services.Length == 0)
            services = [Service("service-alpha", "api-github")];
        var owner = PersonalOwner();
        return new NyxIdAuthorizationCatalogSnapshot(
            owner,
            17,
            Now.AddMinutes(-1),
            Now.AddMinutes(5),
            "nyxid-authorization-catalog/v1",
            "nyxid-authorization-policy/v1",
            Now.AddMinutes(-1),
            NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(owner, services),
            services,
            Activated: true);
    }

    private static NyxIdAuthorizationServiceEvidence Service(
        string userServiceId,
        string serviceSlug) => new()
        {
            UserServiceId = userServiceId,
            ServiceSlug = serviceSlug,
            DisplayName = serviceSlug,
            Access = NyxIdAuthorizationAccess.Permitted,
            ResourceOwner = PersonalOwner(),
        };

    private static NyxIdAuthorizationServiceEvidence ServiceWithAuthorityWindow(
        string userServiceId,
        string serviceSlug,
        DateTimeOffset observedAt,
        DateTimeOffset freshUntil)
    {
        var service = Service(userServiceId, serviceSlug);
        service.ObservedAt = Timestamp.FromDateTimeOffset(observedAt);
        service.FreshUntil = Timestamp.FromDateTimeOffset(freshUntil);
        service.EvaluatedAt = Timestamp.FromDateTimeOffset(observedAt);
        service.AuthorityContractVersion = "scope-plan-contract/v1";
        service.AuthorityPolicyVersion = "scope-plan-policy/v1";
        return service;
    }

    private sealed class StubCatalogQueryPort(
        NyxIdAuthorizationCatalogSnapshot? snapshot)
        : INyxIdAuthorizationCatalogQueryPort
    {
        public List<AuthorizationOwnerIdentity> Owners { get; } = [];

        public Task<NyxIdAuthorizationCatalogSnapshot?> GetAsync(
            AuthorizationOwnerIdentity owner,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Owners.Add(owner.Clone());
            return Task.FromResult(snapshot);
        }
    }

    private sealed class StubActionEvidenceReadPort : INyxIdActionEvidenceReadPort
    {
        public NyxIdUserServiceAuthorizationEvidence? UserService { get; init; }
        public NyxIdServiceAccessEvidence? ServiceAccess { get; init; }
        public NyxIdAgentApiKeyEvidence? AgentApiKey { get; init; }
        public List<string> UserServiceReads { get; } = [];
        public List<(string UserServiceId, string ServiceSlug)> ServiceAccessReads { get; } = [];
        public List<string> AgentKeyReads { get; } = [];
        public List<string> BearerTokens { get; } = [];

        public Task<NyxIdApiAccessResult<NyxIdUserServiceAuthorizationEvidence>>
            GetUserServiceAuthorizationAsync(
                string bearerToken,
                string userServiceId,
                CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            BearerTokens.Add(bearerToken);
            UserServiceReads.Add(userServiceId);
            return Task.FromResult(new NyxIdApiAccessResult<
                NyxIdUserServiceAuthorizationEvidence>(UserService, null));
        }

        public Task<NyxIdApiAccessResult<NyxIdServiceAccessEvidence>>
            GetServiceAccessAsync(
                string bearerToken,
                string userServiceId,
                string serviceSlug,
                CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            BearerTokens.Add(bearerToken);
            ServiceAccessReads.Add((userServiceId, serviceSlug));
            return Task.FromResult(new NyxIdApiAccessResult<NyxIdServiceAccessEvidence>(
                ServiceAccess,
                null));
        }

        public Task<NyxIdApiAccessResult<NyxIdAgentApiKeyEvidence>> GetAgentApiKeyAsync(
            string bearerToken,
            string keyId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            BearerTokens.Add(bearerToken);
            AgentKeyReads.Add(keyId);
            return Task.FromResult(new NyxIdApiAccessResult<NyxIdAgentApiKeyEvidence>(
                AgentApiKey,
                null));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
