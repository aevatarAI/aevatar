using System.Security.Claims;
using System.Text.Json;
using Aevatar.Authentication.Abstractions;
using Aevatar.GAgentService.Abstractions.CatalogSkills;
using Aevatar.Mainnet.Host.Api.CatalogSkills;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Aevatar.Capabilities.Tests;

public sealed class CatalogRecommendedSkillAdminEndpointTests
{
    [Fact]
    public async Task UpdateAsync_CompletesInRequestWithoutIdempotencyKey()
    {
        var http = Context();
        var application = Application();
        var result = await Update(http, application);

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(200);
        http.Response.Headers.Location.Should().BeEmpty();
        var body = Body(result);
        body.Should().Contain("\"status\":\"updated\"").And.Contain("\"catalogReferenceUpdated\":true")
            .And.NotContain("stateVersion").And.NotContain("canResume");
        await application.Received(1).UpdateAsync(Arg.Is<CatalogRecommendedSkillUpdateRequest>(request =>
            request.AdministratorId == "admin-a" && request.CatalogServiceId == "catalog-a" && request.SkillId == "skill-a" &&
            request.ExpectedVersion == "1.0" && request.NewVersion == "1.1"),
            Arg.Is<CatalogRecommendedSkillUpdateExecutionCredential>(credential => credential.BearerToken == "administrator-token"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_AwaitsApplicationResult()
    {
        var application = Substitute.For<ICatalogRecommendedSkillUpdateApplicationService>();
        var completion = new TaskCompletionSource<CatalogRecommendedSkillUpdateOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        application.UpdateAsync(Arg.Any<CatalogRecommendedSkillUpdateRequest>(),
            Arg.Any<CatalogRecommendedSkillUpdateExecutionCredential>(), Arg.Any<CancellationToken>()).Returns(completion.Task);
        var pending = Update(Context(), application);
        pending.IsCompleted.Should().BeFalse();
        completion.SetResult(Completed(new() { SkillId = "skill-a", NewVersion = "1.1" }));
        ((IStatusCodeHttpResult)await pending).StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task UpdateAsync_UsesNewCorrelationIdForEveryRequest()
    {
        var application = Application();
        var first = await Update(Context(), application);
        var second = await Update(Context(), application);
        using var firstBody = JsonDocument.Parse(Body(first));
        using var secondBody = JsonDocument.Parse(Body(second));
        var firstId = firstBody.RootElement.GetProperty("operationId").GetString();
        var secondId = secondBody.RootElement.GetProperty("operationId").GetString();
        Guid.TryParseExact(firstId, "D", out _).Should().BeTrue();
        Guid.TryParseExact(secondId, "D", out _).Should().BeTrue();
        firstId.Should().NotBe(secondId);
    }

    [Theory]
    [InlineData(false, false, "", "", 401)]
    [InlineData(true, false, "admin-a", PlatformAdminGrantSources.AllowedUserId, 403)]
    [InlineData(true, true, "", PlatformAdminGrantSources.AllowedUserId, 403)]
    [InlineData(true, true, "admin-a", PlatformAdminGrantSources.AllowedEmail, 403)]
    [InlineData(true, true, "admin-a", PlatformAdminGrantSources.NyxIdPlatformRole, 403)]
    public async Task UpdateAsync_RejectsUnauthorizedBeforeApplication(bool authenticated, bool elevated,
        string administratorId, string grant, int expectedStatus)
    {
        var application = Substitute.For<ICatalogRecommendedSkillUpdateApplicationService>();
        var result = await CatalogRecommendedSkillAdminEndpoints.UpdateAsync(Context(authenticated), "catalog-a", "skill-a",
            new("1.0", "1.1"), Authorizer(new(elevated, "admin", "", administratorId, grant)), application, CancellationToken.None);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(expectedStatus);
        application.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_MissingBodyDoesNotRunApplication()
    {
        var application = Substitute.For<ICatalogRecommendedSkillUpdateApplicationService>();
        var result = await CatalogRecommendedSkillAdminEndpoints.UpdateAsync(Context(), "catalog-a", "skill-a",
            null, Admin(), application, CancellationToken.None);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(400);
        application.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void Request_RejectsCallerProvidedContentOrDocumentIdentity()
    {
        var parse = () => JsonSerializer.Deserialize<CatalogSkillUpdateRequest>(
            """{"expectedVersion":"1.0","newVersion":"1.1","documentUrl":"https://private.invalid"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        parse.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData(CatalogRecommendedSkillUpdateErrorCode.ExpectedVersionConflict, 409)]
    [InlineData(CatalogRecommendedSkillUpdateErrorCode.VersionOccupied, 409)]
    [InlineData(CatalogRecommendedSkillUpdateErrorCode.InvalidVersion, 400)]
    [InlineData(CatalogRecommendedSkillUpdateErrorCode.Forbidden, 403)]
    public async Task UpdateAsync_ReportsTypedFailure(CatalogRecommendedSkillUpdateErrorCode code, int expectedStatus)
    {
        var application = Application(outcome =>
        {
            outcome.Status = CatalogRecommendedSkillUpdateStatus.Failed;
            outcome.Stage = CatalogRecommendedSkillUpdateStage.ValidateVersion;
            outcome.Publication = null;
            outcome.Reference = null;
            outcome.Failure = new() { Code = code, Stage = outcome.Stage, SafeMessage = "Safe failure." };
        });
        var result = await Update(Context(), application);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(expectedStatus);
        Body(result).Should().Contain("validate_version");
    }

    [Fact]
    public async Task UpdateAsync_DoesNotClaimUpdatedWithoutVerifiedReference()
    {
        var result = await Update(Context(), Application(outcome => outcome.Reference = null));
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(502);
        Body(result).Should().NotContain("\"status\":\"updated\"");
    }

    [Fact]
    public async Task UpdateAsync_PartialPublicationKeepsActualFactsWithoutResumability()
    {
        var application = Application(outcome =>
        {
            outcome.Status = CatalogRecommendedSkillUpdateStatus.Failed;
            outcome.Stage = CatalogRecommendedSkillUpdateStage.PersistReference;
            outcome.Reference = null;
            outcome.Failure = new() { Code = CatalogRecommendedSkillUpdateErrorCode.ReferenceFailed, SafeMessage = "Reference unavailable." };
        });
        var result = await Update(Context(), application);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(502);
        Body(result).Should().Contain("\"publicationConfirmed\":true").And.Contain("\"catalogReferenceUpdated\":false")
            .And.Contain("\"version\":\"1.1\"").And.NotContain("canResume");
    }

    [Fact]
    public async Task UpdateAsync_ReportsUncertainPublication()
    {
        var application = Application(outcome =>
        {
            outcome.Status = CatalogRecommendedSkillUpdateStatus.Uncertain;
            outcome.Stage = CatalogRecommendedSkillUpdateStage.PublishVersion;
            outcome.Publication = null;
            outcome.Reference = null;
            outcome.Failure = new() { Code = CatalogRecommendedSkillUpdateErrorCode.PublicationUncertain, SafeMessage = "Check Ornn before another update." };
        });
        var result = await Update(Context(), application);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(409);
        Body(result).Should().Contain("\"status\":\"uncertain\"").And.Contain("\"publicationConfirmed\":false");
    }

    private static ICatalogRecommendedSkillUpdateApplicationService Application(Action<CatalogRecommendedSkillUpdateOutcome>? customize = null)
    {
        var application = Substitute.For<ICatalogRecommendedSkillUpdateApplicationService>();
        application.UpdateAsync(Arg.Any<CatalogRecommendedSkillUpdateRequest>(),
            Arg.Any<CatalogRecommendedSkillUpdateExecutionCredential>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var outcome = Completed(call.Arg<CatalogRecommendedSkillUpdateRequest>().Clone());
            customize?.Invoke(outcome);
            return outcome;
        });
        return application;
    }
    private static CatalogRecommendedSkillUpdateOutcome Completed(CatalogRecommendedSkillUpdateRequest request) => new()
    {
        Request = request, Status = CatalogRecommendedSkillUpdateStatus.Updated, Stage = CatalogRecommendedSkillUpdateStage.VerifyReference,
        Publication = new() { SkillId = "skill-a", Version = "1.1", ManifestDigest = new string('a', 64), IsPublic = true, ConsumerReadable = true },
        Reference = new() { Version = "1.1", ManifestDigest = new string('a', 64), SkillsRevision = 4, Confirmed = true },
    };
    private static Task<IResult> Update(HttpContext http, ICatalogRecommendedSkillUpdateApplicationService application) =>
        CatalogRecommendedSkillAdminEndpoints.UpdateAsync(http, "catalog-a", "skill-a", new("1.0", "1.1"), Admin(), application, CancellationToken.None);
    private static string Body(IResult result) => JsonSerializer.Serialize(((IValueHttpResult)result).Value);
    private static IPlatformAdminAuthorizer Admin() => Authorizer(new(true, "admin", "", "admin-a", PlatformAdminGrantSources.AllowedUserId));
    private static IPlatformAdminAuthorizer Authorizer(PlatformCaller caller)
    {
        var authorizer = Substitute.For<IPlatformAdminAuthorizer>();
        authorizer.ResolveCallerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(caller);
        return authorizer;
    }
    private static DefaultHttpContext Context(bool authenticated = true)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(authenticated ? new ClaimsIdentity([new Claim("sub", "admin-a")], "test") : new ClaimsIdentity());
        context.Request.Headers.Authorization = "Bearer administrator-token";
        return context;
    }
}
