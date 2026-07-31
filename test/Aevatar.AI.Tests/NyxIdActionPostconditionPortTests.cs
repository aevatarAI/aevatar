using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;

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
    public async Task VerifyAsync_InvalidDigest_ShouldRemainUnavailable()
    {
        var snapshot = ReadySnapshot() with { ContentDigest = "digest-forged" };
        var port = CreatePort(new StubCatalogQueryPort(snapshot));

        var result = await port.VerifyAsync(CatalogInput());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.UnavailableCode);
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
    public async Task VerifyAsync_UnsupportedAction_ShouldFailClosedWithoutCatalogRead()
    {
        var query = new StubCatalogQueryPort(ReadySnapshot());
        var input = CatalogInput();
        input.Action = NyxIdAssistantActionKind.KeyCreate;
        input.Params = new NyxIdAssistantActionParams
        {
            KeyCreate = new NyxIdKeyCreateParams
            {
                Name = "Key Alpha",
                Platform = "api",
            },
        };
        var port = CreatePort(query);

        var result = await port.VerifyAsync(input);

        query.Owners.Should().BeEmpty();
        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.UnsupportedCode);
    }

    private static NyxIdActionPostconditionPort CreatePort(
        INyxIdAuthorizationCatalogQueryPort query) =>
        new(query, new FixedTimeProvider(Now));

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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
