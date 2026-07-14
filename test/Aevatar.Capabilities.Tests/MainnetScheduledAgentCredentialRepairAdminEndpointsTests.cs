using System.Text.Json;
using Aevatar.Authentication.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Scheduled;
using Aevatar.Mainnet.Host.Api.Scheduled;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetScheduledAgentCredentialRepairAdminEndpointsTests
{
    [Fact]
    public async Task HandleAsync_WithoutAuthorizer_ReturnsServiceUnavailable()
    {
        var result = await ScheduledAgentCredentialRepairAdminEndpoints.HandleAsync(
            Context("token"),
            ValidRequest(),
            null,
            Substitute.For<IUserAgentCatalogCredentialRepairPort>(),
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task HandleAsync_WithoutBearer_ReturnsForbiddenWithoutDispatch()
    {
        var authorizer = Substitute.For<IPlatformAdminAuthorizer>();
        var repairPort = Substitute.For<IUserAgentCatalogCredentialRepairPort>();

        var result = await ScheduledAgentCredentialRepairAdminEndpoints.HandleAsync(
            Context(),
            ValidRequest(),
            authorizer,
            repairPort,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
        await repairPort.DidNotReceiveWithAnyArgs().RepairMissingSecretReferenceAsync(
            default!, default!, default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task HandleAsync_WhenIdentityResolutionFails_ReturnsForbidden()
    {
        var authorizer = Substitute.For<IPlatformAdminAuthorizer>();
        authorizer.ResolveCallerAsync("token", Arg.Any<CancellationToken>())
            .Returns<Task<PlatformCaller>>(_ => throw new InvalidOperationException("identity unavailable"));

        var result = await ScheduledAgentCredentialRepairAdminEndpoints.HandleAsync(
            Context("token"),
            ValidRequest(),
            authorizer,
            Substitute.For<IUserAgentCatalogCredentialRepairPort>(),
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task HandleAsync_WhenIdentityResolutionIsCanceled_PropagatesCancellation()
    {
        var authorizer = Substitute.For<IPlatformAdminAuthorizer>();
        authorizer.ResolveCallerAsync("token", Arg.Any<CancellationToken>())
            .Returns<Task<PlatformCaller>>(_ => throw new OperationCanceledException("identity canceled"));

        var handle = () => ScheduledAgentCredentialRepairAdminEndpoints.HandleAsync(
            Context("token"),
            ValidRequest(),
            authorizer,
            Substitute.For<IUserAgentCatalogCredentialRepairPort>(),
            CancellationToken.None);

        await handle.Should().ThrowAsync<OperationCanceledException>()
            .WithMessage("identity canceled");
    }

    [Fact]
    public async Task HandleAsync_WhenCallerIsNotElevated_ReturnsForbidden()
    {
        var authorizer = ElevatedAuthorizer(isElevated: false);

        var result = await ScheduledAgentCredentialRepairAdminEndpoints.HandleAsync(
            Context("token"),
            ValidRequest(),
            authorizer,
            Substitute.For<IUserAgentCatalogCredentialRepairPort>(),
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task HandleAsync_WithInvalidRequest_ReturnsBadRequestWithoutDispatch()
    {
        var repairPort = Substitute.For<IUserAgentCatalogCredentialRepairPort>();
        var result = await ScheduledAgentCredentialRepairAdminEndpoints.HandleAsync(
            Context("token"),
            ValidRequest() with { RepairReason = " " },
            ElevatedAuthorizer(),
            repairPort,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status400BadRequest);
        await repairPort.DidNotReceiveWithAnyArgs().RepairMissingSecretReferenceAsync(
            default!, default!, default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task HandleAsync_WithElevatedCaller_ReturnsCommittedRepairAndDispatchesNormalizedCommand()
    {
        var repairPort = Substitute.For<IUserAgentCatalogCredentialRepairPort>();
        repairPort.RepairMissingSecretReferenceAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<SecretReference>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(CommittedRepairResult());

        var result = await ScheduledAgentCredentialRepairAdminEndpoints.HandleAsync(
            Context("token"),
            ValidRequest() with
            {
                AgentId = " agent-1 ",
                ApiKeyId = " key-1 ",
                RepairReason = " restore exact reference ",
            },
            ElevatedAuthorizer(),
            repairPort,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status200OK);
        var body = JsonSerializer.Serialize(((IValueHttpResult)result).Value);
        body.Should().Contain("repaired").And.Contain("repair-request-1").And.Contain("repair-command-1");
        await repairPort.Received(1).RepairMissingSecretReferenceAsync(
            "agent-1",
            "key-1",
            Arg.Is<SecretReference>(reference => reference.Ref == "sec-1"),
            "key-1",
            "restore exact reference",
            "admin-1",
            Arg.Is<long>(value => value > 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenActorCommitsRejection_ReturnsConflictWithTypedReason()
    {
        var repairPort = Substitute.For<IUserAgentCatalogCredentialRepairPort>();
        repairPort.RepairMissingSecretReferenceAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<SecretReference>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(CommittedRejectedResult());

        var result = await ScheduledAgentCredentialRepairAdminEndpoints.HandleAsync(
            Context("token"),
            ValidRequest(),
            ElevatedAuthorizer(),
            repairPort,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status409Conflict);
        var body = JsonSerializer.Serialize(((IValueHttpResult)result).Value);
        body.Should().Contain("rejected").And.Contain("AliasConflict");
    }

    private static DefaultHttpContext Context(string? bearerToken = null)
    {
        var context = new DefaultHttpContext();
        if (bearerToken is not null)
            context.Request.Headers.Authorization = $"Bearer {bearerToken}";
        return context;
    }

    private static IPlatformAdminAuthorizer ElevatedAuthorizer(bool isElevated = true)
    {
        var authorizer = Substitute.For<IPlatformAdminAuthorizer>();
        authorizer.ResolveCallerAsync("token", Arg.Any<CancellationToken>())
            .Returns(new PlatformCaller(isElevated, "admin", "admin@example.com", "admin-1"));
        return authorizer;
    }

    private static ScheduledAgentCredentialRepairAdminEndpoints.RepairRequest ValidRequest() =>
        new(
            "agent-1",
            "key-1",
            new SecretReference
            {
                Ref = "sec-1",
                Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
                OwnerScopeKey = "owner-1",
                Version = 1,
                Fingerprint = "sha256:test",
            },
            "restore exact reference");

    private static UserAgentCatalogCredentialRepairResult CommittedRepairResult() =>
        new(
            "repair-request-1",
            Admission(),
            new UserAgentCatalogCredentialRepairOutcome
            {
                Repaired = new UserAgentCatalogCredentialRevocationRepairedEvent
                {
                    RequestId = "repair-request-1",
                    AgentId = "agent-1",
                    ApiKeyId = "key-1",
                },
            });

    private static UserAgentCatalogCredentialRepairResult CommittedRejectedResult() =>
        new(
            "repair-request-1",
            Admission(),
            new UserAgentCatalogCredentialRepairOutcome
            {
                Rejected = new UserAgentCatalogCredentialRevocationRepairRejectedEvent
                {
                    RequestId = "repair-request-1",
                    AgentId = "agent-1",
                    ApiKeyId = "key-1",
                    Reason = UserAgentCatalogCredentialRevocationRepairRejectionReason.AliasConflict,
                },
            });

    private static DispatchAdmission Admission() =>
        new(
            true,
            "repair-command-1",
            DateTimeOffset.UtcNow,
            UserAgentCatalogGAgent.WellKnownId,
            "repair-command-1");

    private static int StatusCode(IResult result) =>
        result is Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult
            ? StatusCodes.Status403Forbidden
            : ((IStatusCodeHttpResult)result).StatusCode ?? StatusCodes.Status200OK;
}
