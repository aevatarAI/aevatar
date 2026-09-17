using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.Authentication.Abstractions;
using Aevatar.Bootstrap.Hosting;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.ProjectionRecovery;
using Aevatar.Mainnet.Host.Api.ProjectionRecovery;
using Aevatar.Studio.Application.Studio.ProjectionRecovery;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    public async Task OAuthClientRoute_EndpointAuditMiddlewareAppendsSanitizedRepairEvidence()
    {
        var appender = new RecordingAuditTrailAppender();
        var service = Substitute.For<IAevatarOAuthClientVersionRegressionRepairService>();
        service.RepairAsync(
                Arg.Any<AevatarOAuthClientVersionRegressionRepairRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(OAuthClientAccepted());

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization();
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IAuditTrailAppender>(appender);
        builder.Services.AddSingleton<IAuditActorIdentityHasher>(new StableAuditActorIdentityHasher());
        builder.Services.AddSingleton(ElevatedAuthorizer());
        builder.Services.AddSingleton(service);

        await using var app = builder.Build();
        app.UseRouting();
        app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", "admin-1"),
                new Claim("scope_id", "platform"),
            ], "Test"));
            await next(context);
        });
        app.UseMiddleware<EndpointAuditCaptureMiddleware>();
        app.UseAuthorization();
        app.MapProjectionVersionRegressionRepairAdminEndpoints();
        await app.StartAsync();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(static candidate =>
                candidate.RoutePattern.RawText ==
                "/api/admin/identity/projection-repair/aevatar-oauth-client");
        endpoint.Metadata.GetMetadata<IAuthorizeData>().Should().NotBeNull();
        var audit = endpoint.Metadata.GetMetadata<EndpointAuditMetadata>();
        audit.Should().NotBeNull();
        audit!.OperationName.Should().Be("identity.oauth-client.projection-repair");
        audit.SensitivityLevel.Should().Be(AuditSensitivityLevel.Restricted);

        var request = ValidOAuthClientRequest() with
        {
            RepairReason = "restore regressed identity client replica",
        };
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/admin/identity/projection-repair/aevatar-oauth-client")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BearerSentinel);

        using var response = await app.GetTestClient().SendAsync(httpRequest);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        appender.Records.Should().HaveCount(2);
        appender.Records.Should().OnlyContain(record =>
            record.Target.Kind == "identity-client-projection" &&
            record.Target.Id == "cluster-singleton" &&
            record.RequestSummary != "redacted" &&
            record.Target.Kind != "redacted" &&
            record.Target.Id != "redacted");

        var terminal = appender.Records.Single(record =>
            record.OperationName == "identity.oauth-client.projection-repair");
        var requestIdDigest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(request.RepairRequestId)).AsSpan(0, 16))
            .ToLowerInvariant();
        terminal.RequestSummary.Should()
            .Contain("POST identity-client-projection-repair")
            .And.Contain("apply=true")
            .And.Contain("source_version=2")
            .And.Contain("document_version=3")
            .And.Contain($"repair_request_sha256={requestIdDigest}")
            .And.Contain("reason=restore regressed identity client replica")
            .And.NotContain(request.RepairRequestId)
            .And.NotContain(request.ExpectedDocumentLastEventId)
            .And.NotContain(BearerSentinel);

        var capturedRecords = string.Join('\n', appender.Records.Select(static record => record.ToString()));
        capturedRecords.Should()
            .NotContain(BearerSentinel)
            .And.NotContain(request.RepairRequestId)
            .And.NotContain(request.ExpectedDocumentLastEventId);
    }

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
    public async Task OAuthClient_WithoutBearer_ReturnsForbiddenWithoutServiceIo()
    {
        var service = Substitute.For<IAevatarOAuthClientVersionRegressionRepairService>();

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleOAuthClientAsync(
            Context(),
            ValidOAuthClientRequest(),
            ElevatedAuthorizer(),
            service,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
        await service.DidNotReceiveWithAnyArgs().InspectAsync(default);
        await service.DidNotReceiveWithAnyArgs().RepairAsync(default!, default);
    }

    [Fact]
    public async Task OAuthClient_DryRun_ReturnsOnlyInspectionManifest()
    {
        var service = Substitute.For<IAevatarOAuthClientVersionRegressionRepairService>();
        service.InspectAsync(Arg.Any<CancellationToken>())
            .Returns(OAuthClientInspection());

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleOAuthClientAsync(
            Context(BearerSentinel),
            ValidOAuthClientRequest() with { Apply = false },
            ElevatedAuthorizer(),
            service,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status200OK);
        var body = SerializedBody(result);
        body.Should().Contain("inspection").And.Contain("repairable");
        body.Should().NotContain(BearerSentinel).And.NotContain(CredentialSentinel);
        await service.DidNotReceiveWithAnyArgs().RepairAsync(default!, default);
    }

    [Fact]
    public async Task OAuthClient_ApplyAccepted_ReturnsAcceptedWithoutProjectionSecrets()
    {
        var service = Substitute.For<IAevatarOAuthClientVersionRegressionRepairService>();
        service.RepairAsync(
                Arg.Any<AevatarOAuthClientVersionRegressionRepairRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(OAuthClientAccepted());

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleOAuthClientAsync(
            Context(BearerSentinel),
            ValidOAuthClientRequest(),
            ElevatedAuthorizer(),
            service,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status202Accepted);
        var body = SerializedBody(result);
        body.Should().Contain("accepted").And.Contain("oauth-command-1");
        body.Should()
            .NotContain(BearerSentinel)
            .And.NotContain(CredentialSentinel)
            .And.NotContain(CatalogSentinel);
        await service.Received(1).RepairAsync(
            Arg.Is<AevatarOAuthClientVersionRegressionRepairRequest>(request =>
                request.RequestedBySubjectId == "admin-1" &&
                request.ExpectedActorId == AevatarOAuthClientGAgent.WellKnownId),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("line one\u2028line two")]
    [InlineData("paragraph one\u2029paragraph two")]
    public async Task OAuthClient_UnicodeMultilineRepairReason_ReturnsBadRequestWithoutServiceIo(
        string reason)
    {
        var service = Substitute.For<IAevatarOAuthClientVersionRegressionRepairService>();

        var result = await ProjectionVersionRegressionRepairAdminEndpoints.HandleOAuthClientAsync(
            Context(BearerSentinel),
            ValidOAuthClientRequest() with { RepairReason = reason },
            ElevatedAuthorizer(),
            service,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status400BadRequest);
        await service.DidNotReceiveWithAnyArgs().InspectAsync(default);
        await service.DidNotReceiveWithAnyArgs().RepairAsync(default!, default);
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

    private static ProjectionVersionRegressionRepairAdminEndpoints.OAuthClientRepairRequest
        ValidOAuthClientRequest() =>
        new(
            Apply: true,
            AevatarOAuthClientGAgent.WellKnownId,
            ExpectedSourceStateVersion: 2,
            ExpectedDocumentStateVersion: 3,
            "oauth-event-3",
            "oauth-repair-request-1",
            "repair OAuth client projection regression");

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

    private static AevatarOAuthClientVersionRegressionInspection OAuthClientInspection() =>
        new(
            AevatarOAuthClientGAgent.WellKnownId,
            SourceStateVersion: 2,
            DocumentStateVersion: 3,
            "oauth-event-3",
            AevatarOAuthClientGAgent.WellKnownId,
            Repairable: true,
            BearerSentinel);

    private static AevatarOAuthClientVersionRegressionRepairResult OAuthClientAccepted() =>
        AevatarOAuthClientVersionRegressionRepairResult.Accepted(
            OAuthClientInspection(),
            "oauth-repair-request-1",
            AevatarOAuthClientReplicaDeleteDisposition.Deleted,
            new AevatarOAuthClientProjectionRepublishReceipt(
                AevatarOAuthClientGAgent.WellKnownId,
                "oauth-command-1",
                "oauth-correlation-1"));

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

    private sealed class RecordingAuditTrailAppender : IAuditTrailAppender
    {
        public List<AuditRecord> Records { get; } = [];

        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.FromResult(AuditTrailAppendResult.Appended(
                record.AuditId,
                record.AuditActorId,
                record.OccurredAt.ToDateTimeOffset()));
        }
    }

    private sealed class StableAuditActorIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) =>
            new($"hashed:{canonicalActorKey}", "kid-test");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) =>
            auditActorId == $"hashed:{canonicalActorKey}" &&
            identityKeyId == "kid-test";
    }
}
