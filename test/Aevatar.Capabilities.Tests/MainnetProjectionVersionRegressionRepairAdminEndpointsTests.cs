using System.Text;
using System.Text.Json;
using Aevatar.Authentication.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Mainnet.Host.Api.ProjectionRecovery;
using Aevatar.Studio.Application.Studio.ProjectionRecovery;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetProjectionVersionRegressionRepairAdminEndpointsTests
{
    private const string BearerSentinel = "BEARER-SENTINEL-DO-NOT-SERIALIZE-7f37c65b";
    private const string CredentialSentinel =
        "CREDENTIAL-SENTINEL-DO-NOT-SERIALIZE-1d0fc476";
    private const string CatalogSentinel =
        "CATALOG-CONTENTS-SENTINEL-DO-NOT-SERIALIZE-65ad61e8";
    private const string ExceptionSentinel =
        $"downstream-failure {BearerSentinel} {CredentialSentinel} {CatalogSentinel}";

    [Fact]
    public async Task Workspace_WithoutAuthorizer_ReturnsServiceUnavailable()
    {
        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleWorkspaceAsync(
            Context(BearerSentinel),
            ValidWorkspaceRequest(),
            authorizer: null,
            Substitute.For<IStudioWorkspaceVersionRegressionRepairService>(),
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Catalog_WithoutAuthorizer_ReturnsServiceUnavailable()
    {
        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleCatalogAsync(
            Context(BearerSentinel),
            ValidCatalogRequest(),
            authorizer: null,
            Substitute.For<INyxIdAuthorizationCatalogVersionRegressionRepairService>(),
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Workspace_WithoutBearer_ReturnsForbiddenWithoutServiceIo()
    {
        var service = Substitute.For<IStudioWorkspaceVersionRegressionRepairService>();

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleWorkspaceAsync(
            Context(),
            ValidWorkspaceRequest(),
            ElevatedAuthorizer(),
            service,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
        await service.DidNotReceiveWithAnyArgs().InspectAsync(default!, default);
        await service.DidNotReceiveWithAnyArgs().RepairAsync(default!, default);
    }

    [Fact]
    public async Task Catalog_WithoutBearer_ReturnsForbiddenWithoutServiceIo()
    {
        var service = Substitute.For<INyxIdAuthorizationCatalogVersionRegressionRepairService>();

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleCatalogAsync(
            Context(),
            ValidCatalogRequest(),
            ElevatedAuthorizer(),
            service,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
        await service.DidNotReceiveWithAnyArgs().InspectPersonalAsync(default!, default);
        await service.DidNotReceiveWithAnyArgs().RepairPersonalAsync(default!, default);
    }

    [Fact]
    public async Task Workspace_WhenIdentityResolutionFails_ReturnsForbidden()
    {
        var authorizer = FailingAuthorizer(new InvalidOperationException("identity unavailable"));

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleWorkspaceAsync(
            Context(BearerSentinel),
            ValidWorkspaceRequest(),
            authorizer,
            Substitute.For<IStudioWorkspaceVersionRegressionRepairService>(),
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Catalog_WhenIdentityResolutionFails_ReturnsForbidden()
    {
        var authorizer = FailingAuthorizer(new InvalidOperationException("identity unavailable"));

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleCatalogAsync(
            Context(BearerSentinel),
            ValidCatalogRequest(),
            authorizer,
            Substitute.For<INyxIdAuthorizationCatalogVersionRegressionRepairService>(),
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Workspace_WhenIdentityResolutionThrowsUncanceledOperationCanceled_ReturnsForbidden()
    {
        var authorizer = FailingAuthorizer(new OperationCanceledException("workspace identity canceled"));

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleWorkspaceAsync(
            Context(BearerSentinel),
            ValidWorkspaceRequest(),
            authorizer,
            Substitute.For<IStudioWorkspaceVersionRegressionRepairService>(),
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Workspace_WhenRequestCancellationIsSignaled_PropagatesIdentityCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var authorizer = FailingAuthorizer(new OperationCanceledException("workspace identity canceled"));

        var handle = () => ProjectionVersionRegressionRepairAdminEndpoints.HandleWorkspaceAsync(
            Context(BearerSentinel),
            ValidWorkspaceRequest(),
            authorizer,
            Substitute.For<IStudioWorkspaceVersionRegressionRepairService>(),
            cancellation.Token);

        await handle.Should().ThrowAsync<OperationCanceledException>()
            .WithMessage("workspace identity canceled");
        await authorizer.Received(1)
            .ResolveCallerAsync(BearerSentinel, cancellation.Token);
    }

    [Fact]
    public async Task Catalog_WhenServiceThrowsUncanceledOperationCanceled_ReturnsServiceUnavailable()
    {
        var service = Substitute.For<INyxIdAuthorizationCatalogVersionRegressionRepairService>();
        service.RepairPersonalAsync(
                Arg.Any<NyxIdAuthorizationCatalogVersionRegressionRepairRequest>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<NyxIdAuthorizationCatalogVersionRegressionRepairResult>>(
                _ => throw new OperationCanceledException("catalog repair canceled"));

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleCatalogAsync(
            Context(BearerSentinel),
            ValidCatalogRequest(),
            ElevatedAuthorizer(),
            service,
            CancellationToken.None);

        await AssertSanitizedServiceUnavailableAsync(result);
    }

    [Fact]
    public async Task Catalog_WhenRequestCancellationIsSignaled_PropagatesServiceCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = Substitute.For<INyxIdAuthorizationCatalogVersionRegressionRepairService>();
        service.RepairPersonalAsync(
                Arg.Any<NyxIdAuthorizationCatalogVersionRegressionRepairRequest>(),
                cancellation.Token)
            .Returns<Task<NyxIdAuthorizationCatalogVersionRegressionRepairResult>>(
                _ => throw new OperationCanceledException("catalog repair canceled"));

        var handle = () => ProjectionVersionRegressionRepairAdminEndpoints.HandleCatalogAsync(
            Context(BearerSentinel),
            ValidCatalogRequest(),
            ElevatedAuthorizer(),
            service,
            cancellation.Token);

        await handle.Should().ThrowAsync<OperationCanceledException>()
            .WithMessage("catalog repair canceled");
    }

    [Fact]
    public async Task Workspace_WhenCallerIsNotElevated_ReturnsForbidden()
    {
        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleWorkspaceAsync(
            Context(BearerSentinel),
            ValidWorkspaceRequest(),
            ElevatedAuthorizer(isElevated: false),
            Substitute.For<IStudioWorkspaceVersionRegressionRepairService>(),
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Catalog_WhenCallerIsNotElevated_ReturnsForbidden()
    {
        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleCatalogAsync(
            Context(BearerSentinel),
            ValidCatalogRequest(),
            ElevatedAuthorizer(isElevated: false),
            Substitute.For<INyxIdAuthorizationCatalogVersionRegressionRepairService>(),
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Workspace_WithoutRepairService_ReturnsServiceUnavailable()
    {
        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleWorkspaceAsync(
            Context(BearerSentinel),
            ValidWorkspaceRequest(),
            ElevatedAuthorizer(),
            service: null,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Catalog_WithoutRepairService_ReturnsServiceUnavailable()
    {
        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleCatalogAsync(
            Context(BearerSentinel),
            ValidCatalogRequest(),
            ElevatedAuthorizer(),
            service: null,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Theory]
    [InlineData("expected_actor_id")]
    [InlineData("expected_source_state_version")]
    [InlineData("expected_document_state_version")]
    [InlineData("document_not_ahead")]
    [InlineData("expected_document_last_event_id")]
    [InlineData("repair_request_id")]
    [InlineData("repair_reason")]
    public async Task Workspace_WithInvalidApplyManifest_ReturnsBadRequestWithoutServiceIo(
        string invalidField)
    {
        var service = Substitute.For<IStudioWorkspaceVersionRegressionRepairService>();
        var request = InvalidWorkspaceApplyRequest(invalidField);

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleWorkspaceAsync(
            Context(BearerSentinel),
            request,
            ElevatedAuthorizer(),
            service,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status400BadRequest);
        await service.DidNotReceiveWithAnyArgs().InspectAsync(default!, default);
        await service.DidNotReceiveWithAnyArgs().RepairAsync(default!, default);
    }

    [Theory]
    [InlineData("expected_actor_id")]
    [InlineData("expected_source_state_version")]
    [InlineData("expected_document_state_version")]
    [InlineData("document_not_ahead")]
    [InlineData("expected_document_last_event_id")]
    [InlineData("repair_request_id")]
    [InlineData("repair_reason")]
    public async Task Catalog_WithInvalidApplyManifest_ReturnsBadRequestWithoutServiceIo(
        string invalidField)
    {
        var service = Substitute.For<INyxIdAuthorizationCatalogVersionRegressionRepairService>();
        var request = InvalidCatalogApplyRequest(invalidField);

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleCatalogAsync(
            Context(BearerSentinel),
            request,
            ElevatedAuthorizer(),
            service,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status400BadRequest);
        await service.DidNotReceiveWithAnyArgs().InspectPersonalAsync(default!, default);
        await service.DidNotReceiveWithAnyArgs().RepairPersonalAsync(default!, default);
    }

    [Fact]
    public async Task Workspace_Inspection_DoesNotRequireApplyManifestAndCallsInspectionOnly()
    {
        using var source = new CancellationTokenSource();
        var service = Substitute.For<IStudioWorkspaceVersionRegressionRepairService>();
        service.InspectAsync("scope-1", source.Token).Returns(WorkspaceInspection());
        var request = ValidWorkspaceRequest() with
        {
            Apply = false,
            ExpectedActorId = string.Empty,
            ExpectedSourceStateVersion = 0,
            ExpectedDocumentStateVersion = 0,
            ExpectedDocumentLastEventId = string.Empty,
            RepairRequestId = string.Empty,
            RepairReason = string.Empty,
        };

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleWorkspaceAsync(
            Context(BearerSentinel),
            request,
            ElevatedAuthorizer(),
            service,
            source.Token);

        StatusCode(result).Should().Be(StatusCodes.Status200OK);
        await service.Received(1).InspectAsync("scope-1", source.Token);
        await service.DidNotReceiveWithAnyArgs().RepairAsync(default!, default);
    }

    [Fact]
    public async Task Catalog_Inspection_DoesNotRequireApplyManifestAndCallsInspectionOnly()
    {
        using var source = new CancellationTokenSource();
        var service = Substitute.For<INyxIdAuthorizationCatalogVersionRegressionRepairService>();
        service.InspectPersonalAsync("admin-1", source.Token).Returns(CatalogInspection());
        var request = ValidCatalogRequest() with
        {
            Apply = false,
            ExpectedActorId = string.Empty,
            ExpectedSourceStateVersion = 0,
            ExpectedDocumentStateVersion = 0,
            ExpectedDocumentLastEventId = string.Empty,
            RepairRequestId = string.Empty,
            RepairReason = string.Empty,
        };

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleCatalogAsync(
            Context(BearerSentinel),
            request,
            ElevatedAuthorizer(),
            service,
            source.Token);

        StatusCode(result).Should().Be(StatusCodes.Status200OK);
        await service.Received(1).InspectPersonalAsync("admin-1", source.Token);
        await service.DidNotReceiveWithAnyArgs().RepairPersonalAsync(default!, default);
    }

    [Fact]
    public async Task Workspace_InspectionException_ReturnsSanitizedServiceUnavailable()
    {
        var service = Substitute.For<IStudioWorkspaceVersionRegressionRepairService>();
        service.InspectAsync("scope-1", Arg.Any<CancellationToken>())
            .Returns<Task<StudioWorkspaceVersionRegressionInspection>>(
                _ => throw DownstreamException());
        var request = ValidWorkspaceRequest() with { Apply = false };

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleWorkspaceAsync(
            Context(BearerSentinel),
            request,
            ElevatedAuthorizer(),
            service,
            CancellationToken.None);

        await AssertSanitizedServiceUnavailableAsync(result);
        await service.DidNotReceiveWithAnyArgs().RepairAsync(default!, default);
    }

    [Fact]
    public async Task Workspace_ApplyException_ReturnsSanitizedServiceUnavailable()
    {
        var service = Substitute.For<IStudioWorkspaceVersionRegressionRepairService>();
        service.RepairAsync(
                Arg.Any<StudioWorkspaceVersionRegressionRepairRequest>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<StudioWorkspaceVersionRegressionRepairResult>>(
                _ => throw DownstreamException());

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleWorkspaceAsync(
            Context(BearerSentinel),
            ValidWorkspaceRequest(),
            ElevatedAuthorizer(),
            service,
            CancellationToken.None);

        await AssertSanitizedServiceUnavailableAsync(result);
    }

    [Fact]
    public async Task Catalog_InspectionException_ReturnsSanitizedServiceUnavailable()
    {
        var service = Substitute.For<INyxIdAuthorizationCatalogVersionRegressionRepairService>();
        service.InspectPersonalAsync("admin-1", Arg.Any<CancellationToken>())
            .Returns<Task<NyxIdAuthorizationCatalogVersionRegressionInspection>>(
                _ => throw DownstreamException());
        var request = ValidCatalogRequest() with { Apply = false };

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleCatalogAsync(
            Context(BearerSentinel),
            request,
            ElevatedAuthorizer(),
            service,
            CancellationToken.None);

        await AssertSanitizedServiceUnavailableAsync(result);
        await service.DidNotReceiveWithAnyArgs().RepairPersonalAsync(default!, default);
    }

    [Fact]
    public async Task Catalog_ApplyException_ReturnsSanitizedServiceUnavailable()
    {
        var service = Substitute.For<INyxIdAuthorizationCatalogVersionRegressionRepairService>();
        service.RepairPersonalAsync(
                Arg.Any<NyxIdAuthorizationCatalogVersionRegressionRepairRequest>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<NyxIdAuthorizationCatalogVersionRegressionRepairResult>>(
                _ => throw DownstreamException());

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleCatalogAsync(
            Context(BearerSentinel),
            ValidCatalogRequest(),
            ElevatedAuthorizer(),
            service,
            CancellationToken.None);

        await AssertSanitizedServiceUnavailableAsync(result);
    }

    [Fact]
    public async Task Workspace_Accepted_PropagatesExpectedActorAndReturnsAcceptedWithoutVisibilityClaim()
    {
        using var source = new CancellationTokenSource();
        var service = Substitute.For<IStudioWorkspaceVersionRegressionRepairService>();
        service.RepairAsync(
                Arg.Any<StudioWorkspaceVersionRegressionRepairRequest>(),
                source.Token)
            .Returns(WorkspaceAccepted());
        var request = ValidWorkspaceRequest() with { ExpectedActorId = " workspace-actor-1 " };

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleWorkspaceAsync(
            Context(BearerSentinel),
            request,
            ElevatedAuthorizer(),
            service,
            source.Token);

        StatusCode(result).Should().Be(StatusCodes.Status202Accepted);
        await service.Received(1).RepairAsync(
            Arg.Is<StudioWorkspaceVersionRegressionRepairRequest>(mapped =>
                mapped.ScopeId == request.ScopeId &&
                mapped.ExpectedActorId == " workspace-actor-1 " &&
                mapped.ExpectedSourceStateVersion == request.ExpectedSourceStateVersion &&
                mapped.ExpectedDocumentStateVersion == request.ExpectedDocumentStateVersion &&
                mapped.ExpectedDocumentLastEventId == request.ExpectedDocumentLastEventId &&
                mapped.RepairRequestId == request.RepairRequestId &&
                mapped.RepairReason == request.RepairReason &&
                mapped.RequestedBySubjectId == "admin-1"),
            source.Token);
        var body = SerializedBody(result);
        body.Should().Contain("accepted").And.Contain("workspace-command-1");
        body.ToLowerInvariant().Should().NotContain("visibility")
            .And.NotContain("ready")
            .And.NotContain(BearerSentinel);
    }

    [Fact]
    public async Task Workspace_Conflict_ReturnsConflict()
    {
        var service = Substitute.For<IStudioWorkspaceVersionRegressionRepairService>();
        service.RepairAsync(
                Arg.Any<StudioWorkspaceVersionRegressionRepairRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(WorkspaceConflict());

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleWorkspaceAsync(
            Context(BearerSentinel),
            ValidWorkspaceRequest(),
            ElevatedAuthorizer(),
            service,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status409Conflict);
        SerializedBody(result).Should().Contain("conflict").And.NotContain(BearerSentinel);
    }

    [Fact]
    public async Task Catalog_Ready_PropagatesExpectedActorBearerAndSameCallerSubjectsWithoutSerializingBearer()
    {
        using var source = new CancellationTokenSource();
        var service = Substitute.For<INyxIdAuthorizationCatalogVersionRegressionRepairService>();
        service.RepairPersonalAsync(
                Arg.Any<NyxIdAuthorizationCatalogVersionRegressionRepairRequest>(),
                source.Token)
            .Returns(CatalogResult(NyxIdAuthorizationCatalogVersionRegressionRepairStatus.Ready));
        var request = ValidCatalogRequest() with { ExpectedActorId = " catalog-actor-1 " };

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleCatalogAsync(
            Context(BearerSentinel),
            request,
            ElevatedAuthorizer(),
            service,
            source.Token);

        StatusCode(result).Should().Be(StatusCodes.Status200OK);
        await service.Received(1).RepairPersonalAsync(
            Arg.Is<NyxIdAuthorizationCatalogVersionRegressionRepairRequest>(mapped =>
                mapped.VerifiedOwnerSubject == "admin-1" &&
                mapped.RequestedBySubjectId == "admin-1" &&
                mapped.ExpectedActorId == " catalog-actor-1 " &&
                mapped.BearerToken == BearerSentinel &&
                mapped.ExpectedSourceStateVersion == request.ExpectedSourceStateVersion &&
                mapped.ExpectedDocumentStateVersion == request.ExpectedDocumentStateVersion &&
                mapped.ExpectedDocumentLastEventId == request.ExpectedDocumentLastEventId &&
                mapped.RepairRequestId == request.RepairRequestId &&
                mapped.RepairReason == request.RepairReason),
            source.Token);
        var body = SerializedBody(result);
        body.Should().Contain("ready").And.Contain("observed");
        body.Should().NotContain(BearerSentinel);
    }

    [Fact]
    public async Task Catalog_ProjectionPending_ReturnsAccepted()
    {
        var service = Substitute.For<INyxIdAuthorizationCatalogVersionRegressionRepairService>();
        service.RepairPersonalAsync(
                Arg.Any<NyxIdAuthorizationCatalogVersionRegressionRepairRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(CatalogResult(NyxIdAuthorizationCatalogVersionRegressionRepairStatus.ProjectionPending));

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleCatalogAsync(
            Context(BearerSentinel),
            ValidCatalogRequest(),
            ElevatedAuthorizer(),
            service,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status202Accepted);
        SerializedBody(result).Should().Contain("projection_pending").And.NotContain(BearerSentinel);
    }

    [Fact]
    public async Task Catalog_Conflict_ReturnsConflict()
    {
        var service = Substitute.For<INyxIdAuthorizationCatalogVersionRegressionRepairService>();
        service.RepairPersonalAsync(
                Arg.Any<NyxIdAuthorizationCatalogVersionRegressionRepairRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(CatalogResult(NyxIdAuthorizationCatalogVersionRegressionRepairStatus.Conflict));

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleCatalogAsync(
            Context(BearerSentinel),
            ValidCatalogRequest(),
            ElevatedAuthorizer(),
            service,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status409Conflict);
        SerializedBody(result).Should().Contain("conflict").And.NotContain(BearerSentinel);
    }

    [Fact]
    public async Task Catalog_DownstreamFailure_ReturnsServiceUnavailableWithoutSerializingFailureDetail()
    {
        var service = Substitute.For<INyxIdAuthorizationCatalogVersionRegressionRepairService>();
        service.RepairPersonalAsync(
                Arg.Any<NyxIdAuthorizationCatalogVersionRegressionRepairRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(CatalogResult(NyxIdAuthorizationCatalogVersionRegressionRepairStatus.Failed));

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleCatalogAsync(
            Context(BearerSentinel),
            ValidCatalogRequest(),
            ElevatedAuthorizer(),
            service,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status503ServiceUnavailable);
        SerializedBody(result).Should().Contain("failed").And.NotContain(BearerSentinel);
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
        authorizer.ResolveCallerAsync(BearerSentinel, Arg.Any<CancellationToken>())
            .Returns(new PlatformCaller(isElevated, "admin", "admin@example.com", "admin-1"));
        return authorizer;
    }

    private static IPlatformAdminAuthorizer FailingAuthorizer(Exception exception)
    {
        var authorizer = Substitute.For<IPlatformAdminAuthorizer>();
        authorizer.ResolveCallerAsync(BearerSentinel, Arg.Any<CancellationToken>())
            .Returns<Task<PlatformCaller>>(_ => throw exception);
        return authorizer;
    }

    private static Exception DownstreamException() =>
        new InvalidOperationException(ExceptionSentinel);

    private static ProjectionVersionRegressionRepairAdminEndpoints.WorkspaceRepairRequest ValidWorkspaceRequest() =>
        new(
            "scope-1",
            Apply: true,
            "workspace-actor-1",
            ExpectedSourceStateVersion: 7,
            ExpectedDocumentStateVersion: 12,
            "workspace-event-12",
            "workspace-repair-request-1",
            "repair workspace projection regression");

    private static ProjectionVersionRegressionRepairAdminEndpoints.CatalogRepairRequest ValidCatalogRequest() =>
        new(
            Apply: true,
            "catalog-actor-1",
            ExpectedSourceStateVersion: 7,
            ExpectedDocumentStateVersion: 12,
            "catalog-event-12",
            "catalog-repair-request-1",
            "repair catalog projection regression");

    private static ProjectionVersionRegressionRepairAdminEndpoints.WorkspaceRepairRequest
        InvalidWorkspaceApplyRequest(string invalidField)
    {
        var request = ValidWorkspaceRequest();
        return invalidField switch
        {
            "expected_actor_id" => request with { ExpectedActorId = " " },
            "expected_source_state_version" => request with { ExpectedSourceStateVersion = 0 },
            "expected_document_state_version" => request with { ExpectedDocumentStateVersion = 0 },
            "document_not_ahead" => request with { ExpectedDocumentStateVersion = 7 },
            "expected_document_last_event_id" => request with { ExpectedDocumentLastEventId = " " },
            "repair_request_id" => request with { RepairRequestId = " " },
            "repair_reason" => request with { RepairReason = " " },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField)),
        };
    }

    private static ProjectionVersionRegressionRepairAdminEndpoints.CatalogRepairRequest
        InvalidCatalogApplyRequest(string invalidField)
    {
        var request = ValidCatalogRequest();
        return invalidField switch
        {
            "expected_actor_id" => request with { ExpectedActorId = " " },
            "expected_source_state_version" => request with { ExpectedSourceStateVersion = 0 },
            "expected_document_state_version" => request with { ExpectedDocumentStateVersion = 0 },
            "document_not_ahead" => request with { ExpectedDocumentStateVersion = 7 },
            "expected_document_last_event_id" => request with { ExpectedDocumentLastEventId = " " },
            "repair_request_id" => request with { RepairRequestId = " " },
            "repair_reason" => request with { RepairReason = " " },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField)),
        };
    }

    private static StudioWorkspaceVersionRegressionInspection WorkspaceInspection() =>
        new(
            "scope-1",
            "workspace-actor-1",
            SourceStateVersion: 7,
            DocumentStateVersion: 12,
            "workspace-event-12",
            "workspace-actor-1",
            Repairable: true,
            BearerSentinel);

    private static StudioWorkspaceVersionRegressionRepairResult WorkspaceAccepted() =>
        StudioWorkspaceVersionRegressionRepairResult.Accepted(
            WorkspaceInspection(),
            "workspace-repair-request-1",
            StudioWorkspaceReplicaDeleteDisposition.Deleted,
            new StudioWorkspaceProjectionRepublishReceipt(
                "workspace-actor-1",
                "workspace-command-1",
                "workspace-correlation-1"));

    private static StudioWorkspaceVersionRegressionRepairResult WorkspaceConflict() =>
        StudioWorkspaceVersionRegressionRepairResult.Conflict(
            WorkspaceInspection(),
            "workspace-repair-request-1",
            BearerSentinel,
            StudioWorkspaceReplicaDeleteDisposition.DocumentChanged);

    private static NyxIdAuthorizationCatalogVersionRegressionInspection CatalogInspection() =>
        new(
            "admin-1",
            "catalog-actor-1",
            SourceStateVersion: 7,
            DocumentStateVersion: 12,
            "catalog-event-12",
            "catalog-actor-1",
            Repairable: true,
            BearerSentinel);

    private static NyxIdAuthorizationCatalogVersionRegressionRepairResult CatalogResult(
        NyxIdAuthorizationCatalogVersionRegressionRepairStatus status)
    {
        var visibilityStatus = status == NyxIdAuthorizationCatalogVersionRegressionRepairStatus.ProjectionPending
            ? NyxIdAuthorizationCatalogVisibilityStatus.ProjectionPending
            : NyxIdAuthorizationCatalogVisibilityStatus.Ready;
        return new NyxIdAuthorizationCatalogVersionRegressionRepairResult(
            status,
            CatalogInspection(),
            "catalog-repair-request-1",
            NyxIdAuthorizationCatalogReplicaDeleteDisposition.Deleted,
            new NyxIdAuthorizationCatalogRefreshResult(
                status == NyxIdAuthorizationCatalogVersionRegressionRepairStatus.Failed
                    ? NyxIdAuthorizationCatalogRefreshStatus.Failed
                    : NyxIdAuthorizationCatalogRefreshStatus.Observed,
                BearerSentinel,
                StateVersion: 7),
            new NyxIdAuthorizationCatalogVisibilityResult(
                visibilityStatus,
                RequiredStateVersion: 7,
                VisibleStateVersion: visibilityStatus == NyxIdAuthorizationCatalogVisibilityStatus.Ready ? 7 : 6,
                BearerSentinel),
            BearerSentinel);
    }

    private static string SerializedBody(IResult result) =>
        JsonSerializer.Serialize(((IValueHttpResult)result).Value);

    private static async Task AssertSanitizedServiceUnavailableAsync(IResult result)
    {
        using var requestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        using var responseBody = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = requestServices,
        };
        context.Response.Body = responseBody;

        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        var body = Encoding.UTF8.GetString(responseBody.ToArray());
        body.Should().BeEmpty();
        body.Should()
            .NotContain(ExceptionSentinel)
            .And.NotContain(BearerSentinel)
            .And.NotContain(CredentialSentinel)
            .And.NotContain(CatalogSentinel);
    }

    private static int StatusCode(IResult result) =>
        result is Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult
            ? StatusCodes.Status403Forbidden
            : ((IStatusCodeHttpResult)result).StatusCode ?? StatusCodes.Status200OK;
}
