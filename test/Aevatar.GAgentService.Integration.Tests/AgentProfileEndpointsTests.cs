using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Application.AgentProfiles;
using Aevatar.GAgentService.Hosting.Endpoints;
using Google.Protobuf;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class AgentProfileEndpointsTests
{
    private const string ScopeId = "scope-alpha";
    private const string SubjectId = "subject-alpha";
    private const string OwnerHandle = "owner-alpha";
    private const string ProfileSlug = "profile-alpha";
    private const string SecretBearer = "bearer-secret-must-never-leak";
    private const string SecretInstructions = "owner-authored-prompt-must-never-leak-from-safe-responses";
    private const string SecretSkillBody = "sealed-skill-body-must-never-leak";

    [Fact]
    public async Task Routes_ShouldExposeExactAgentProfileInventory()
    {
        await using var host = await EndpointTestHost.StartAsync();

        host.AgentProfileRoutes.Should().BeEquivalentTo(
        [
            "DELETE /api/scopes/{scopeId}/agent-profiles/{profileSlug}/draft/skills/{bindingId}",
            "GET /api/agent-profiles/{ownerHandle}/{profileSlug}",
            "GET /api/scopes/{scopeId}/agent-profiles/{profileSlug}",
            "POST /api/scopes/{scopeId}/agent-profiles",
            "POST /api/scopes/{scopeId}/agent-profiles/{profileSlug}:publish",
            "POST /api/scopes/{scopeId}/agent-profiles/{profileSlug}:validate",
            "PUT /api/scopes/{scopeId}/agent-profiles/{profileSlug}/draft",
            "PUT /api/scopes/{scopeId}/agent-profiles/{profileSlug}/draft/skills/{bindingId}",
        ]);
    }

    [Theory]
    [InlineData("POST", "/api/scopes/scope-alpha/agent-profiles")]
    [InlineData("GET", "/api/agent-profiles/owner-alpha/profile-alpha")]
    public async Task AuthEnabledRoutes_WhenUnauthenticated_ShouldReturnUnauthorized(
        string method,
        string route)
    {
        await using var host = await EndpointTestHost.StartAsync(authenticationEnabled: true);
        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(CreateBody());
            request.Headers.TryAddWithoutValidation("Idempotency-Key", "create-alpha");
        }

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        host.TotalServiceCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("scope-beta")]
    [InlineData("scope-alpha,scope-beta")]
    public async Task ScopeRoute_WhenAuthenticatedScopeIsMissingMismatchedOrAmbiguous_ShouldReturnForbidden(
        string? scopeClaims)
    {
        await using var host = await EndpointTestHost.StartAsync(authenticationEnabled: true);
        using var request = CreateRequest(HttpMethod.Get, ManagementRoute(), scopeClaims: scopeClaims);

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        host.TotalServiceCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("subject-alpha,subject-beta")]
    public async Task ManagementRoute_WhenSubjectIsMissingOrAmbiguous_ShouldReturnUnauthorized(
        string? subjectClaims)
    {
        await using var host = await EndpointTestHost.StartAsync(authenticationEnabled: true);
        using var request = CreateRequest(
            HttpMethod.Get,
            ManagementRoute(),
            subjectClaims: subjectClaims);

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        host.TotalServiceCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(null, "sub", null)]
    [InlineData("scope-alpha,scope-beta", "sub", "subject-alpha")]
    [InlineData("scope-alpha", null, null)]
    [InlineData("scope-alpha", "uid", "subject-alpha,subject-beta")]
    public async Task Discovery_WhenScopeOrSubjectIsMissingOrAmbiguous_ShouldReturnUnauthorized(
        string? scopeClaims,
        string? subjectClaimType,
        string? subjectClaims)
    {
        await using var host = await EndpointTestHost.StartAsync(authenticationEnabled: true);
        using var request = CreateRequest(
            HttpMethod.Get,
            DiscoveryRoute(),
            scopeClaims: scopeClaims,
            subjectClaimType: subjectClaimType,
            subjectClaims: subjectClaims);

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        host.TotalServiceCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("uid")]
    [InlineData(ClaimTypes.NameIdentifier)]
    [InlineData("sub")]
    [InlineData("user_id")]
    public async Task CallerContext_ShouldAcceptCanonicalSubjectClaimCandidates(string claimType)
    {
        await using var host = await EndpointTestHost.StartAsync(authenticationEnabled: true);
        using var request = CreateRequest(
            HttpMethod.Get,
            ManagementRoute(),
            subjectClaimType: claimType);

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var caller = host.QueryService.OwnedCalls.Should().ContainSingle().Which.Caller;
        caller.Owner.IdentityProvider.Should().Be("nyxid");
        caller.Owner.SubjectId.Should().Be(SubjectId);
        caller.ScopeId.Should().Be(ScopeId);
    }

    [Fact]
    public async Task CallerContext_WhenSameSubjectAppearsInMultipleCandidateClaims_ShouldAcceptOneAuthority()
    {
        await using var host = await EndpointTestHost.StartAsync(authenticationEnabled: true);
        using var request = CreateRequest(HttpMethod.Get, ManagementRoute());
        request.Headers.Add("X-Test-Claim-uid", SubjectId);
        request.Headers.Add("X-Test-Claim-user_id", SubjectId);

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.QueryService.OwnedCalls.Should().ContainSingle()
            .Which.Caller.Owner.SubjectId.Should().Be(SubjectId);
    }

    [Theory]
    [InlineData("preferred_username")]
    [InlineData("username")]
    [InlineData("name")]
    public async Task Create_WhenHandleIsOmitted_ShouldProposeUsernameHint(string claimType)
    {
        await using var host = await EndpointTestHost.StartAsync(authenticationEnabled: true);
        using var request = CreateRequest(HttpMethod.Post, CollectionRoute());
        request.Content = JsonContent.Create(CreateBody(ownerHandle: null));
        request.Headers.Add("Idempotency-Key", "create-alpha");
        request.Headers.Add(ClaimHeaderName(claimType), "proposed-handle");

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var call = host.CommandService.CreateCalls.Should().ContainSingle().Which;
        call.Caller.Username.Should().Be("proposed-handle");
        call.Request.OwnerHandle.Should().BeNull();
    }

    [Fact]
    public async Task Create_WhenExplicitHandleIsProvided_ShouldKeepItSeparateFromUsernameHint()
    {
        await using var host = await EndpointTestHost.StartAsync(authenticationEnabled: true);
        using var request = CreateRequest(HttpMethod.Post, CollectionRoute());
        request.Content = JsonContent.Create(CreateBody(ownerHandle: "explicit-handle"));
        request.Headers.Add("Idempotency-Key", "create-alpha");
        request.Headers.Add("X-Test-Claim-preferred_username", "proposed-handle");

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var call = host.CommandService.CreateCalls.Should().ContainSingle().Which;
        call.Caller.Username.Should().Be("proposed-handle");
        call.Request.OwnerHandle.Should().Be("explicit-handle");
    }

    [Fact]
    public async Task Create_WhenUsernameHintsAreAmbiguous_ShouldNotTurnThemIntoAuthority()
    {
        await using var host = await EndpointTestHost.StartAsync(authenticationEnabled: true);
        using var request = CreateRequest(HttpMethod.Post, CollectionRoute());
        request.Content = JsonContent.Create(CreateBody(ownerHandle: "explicit-handle"));
        request.Headers.Add("Idempotency-Key", "create-alpha");
        request.Headers.Add("X-Test-Claim-preferred_username", "handle-a");
        request.Headers.Add("X-Test-Claim-username", "handle-b");

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var caller = host.CommandService.CreateCalls.Should().ContainSingle().Which.Caller;
        caller.Username.Should().BeNull();
        caller.Owner.SubjectId.Should().Be(SubjectId);
    }

    [Fact]
    public async Task CallerContext_ShouldPropagateBearerOnlyToTransientApplicationContext()
    {
        await using var host = await EndpointTestHost.StartAsync(authenticationEnabled: true);
        using var request = CreateRequest(HttpMethod.Post, ValidateRoute());
        request.Headers.Authorization = new("Bearer", SecretBearer);

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.CommandService.ValidateCalls.Should().ContainSingle()
            .Which.Caller.NyxIdAccessToken.Should().Be(SecretBearer);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(SecretBearer);
    }

    [Fact]
    public async Task DevelopmentDisabledAuthentication_ShouldNotBlockManagementRoute()
    {
        await using var host = await EndpointTestHost.StartAsync(authenticationEnabled: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, ManagementRoute());

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var caller = host.QueryService.OwnedCalls.Should().ContainSingle().Which.Caller;
        caller.ScopeId.Should().Be(ScopeId);
        caller.Owner.IdentityProvider.Should().Be("nyxid");
        caller.Owner.SubjectId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DevelopmentDisabledAuthentication_ShouldNotBlockDiscoveryRoute()
    {
        await using var host = await EndpointTestHost.StartAsync(authenticationEnabled: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, DiscoveryRoute());

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var caller = host.QueryService.DiscoveryCalls.Should().ContainSingle().Which.Caller;
        caller.ScopeId.Should().NotBeNullOrWhiteSpace();
        caller.Owner.IdentityProvider.Should().Be("nyxid");
        caller.Owner.SubjectId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Create_WhenIdempotencyKeyIsMissing_ShouldReturnBadRequestWithoutCallingService()
    {
        await using var host = await EndpointTestHost.StartAsync();
        using var request = CreateRequest(HttpMethod.Post, CollectionRoute());
        request.Content = JsonContent.Create(CreateBody());

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.TotalServiceCalls.Should().Be(0);
    }

    [Fact]
    public async Task Create_ShouldPreserveIdempotencyKeyAndReturnOnlyAcceptedReceiptFields()
    {
        await using var host = await EndpointTestHost.StartAsync();
        using var request = CreateRequest(HttpMethod.Post, CollectionRoute());
        request.Content = JsonContent.Create(CreateBody());
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "key:with:transport-punctuation");

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandService.CreateCalls.Should().ContainSingle()
            .Which.IdempotencyKey.Should().Be("key:with:transport-punctuation");
        var json = await ReadJsonAsync(response);
        json.RootElement.EnumerateObject().Select(static property => property.Name).Should().BeEquivalentTo(
            "accepted",
            "ackStage",
            "operationId",
            "commandId",
            "correlationId",
            "actorId",
            "profileId",
            "resourceUrl");
        json.RootElement.GetProperty("accepted").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("ackStage").GetString().Should().Be("accepted");
    }

    [Theory]
    [InlineData("PUT", "/api/scopes/scope-alpha/agent-profiles/profile-alpha/draft")]
    [InlineData("PUT", "/api/scopes/scope-alpha/agent-profiles/profile-alpha/draft/skills/binding-alpha")]
    [InlineData("DELETE", "/api/scopes/scope-alpha/agent-profiles/profile-alpha/draft/skills/binding-alpha")]
    [InlineData("POST", "/api/scopes/scope-alpha/agent-profiles/profile-alpha:publish")]
    public async Task VersionedMutations_WhenIfMatchIsMissing_ShouldReturnPreconditionRequired(
        string method,
        string route)
    {
        await using var host = await EndpointTestHost.StartAsync();
        using var request = CreateRequest(new HttpMethod(method), route);
        if (method == "PUT")
            request.Content = JsonContent.Create(route.Contains("/skills/", StringComparison.Ordinal)
                ? SkillBody()
                : DraftBody());

        using var response = await host.Client.SendAsync(request);

        ((int)response.StatusCode).Should().Be(StatusCodes.Status428PreconditionRequired);
        host.TotalServiceCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("W/\"agent-profile-v14\"")]
    [InlineData("*")]
    [InlineData("\"agent-profile-v14\", \"agent-profile-v15\"")]
    [InlineData("\"agent-profile-v-1\"")]
    [InlineData("\"agent-profile-v+1\"")]
    [InlineData("\"agent-profile-v01\"")]
    [InlineData("\"agent-profile-v9223372036854775808\"")]
    [InlineData("\"agent-profile-v\"")]
    [InlineData("agent-profile-v14")]
    [InlineData("\"profile-v14\"")]
    [InlineData("\"agent-profile-v14\" trailing")]
    public async Task VersionedMutation_WhenIfMatchIsInvalid_ShouldReturnBadRequest(string ifMatch)
    {
        await using var host = await EndpointTestHost.StartAsync();
        using var request = CreateRequest(HttpMethod.Post, PublishRoute());
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.TotalServiceCalls.Should().Be(0);
    }

    [Fact]
    public async Task VersionedMutation_ShouldParseExactStrongEtagAndAllowOptionalIdempotencyKey()
    {
        await using var host = await EndpointTestHost.StartAsync();
        using var request = CreateRequest(HttpMethod.Post, PublishRoute());
        request.Headers.TryAddWithoutValidation("If-Match", "\"agent-profile-v14\"");

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var call = host.CommandService.PublishCalls.Should().ContainSingle().Which;
        call.ExpectedVersion.Should().Be(14);
        call.IdempotencyKey.Should().BeNull();
    }

    [Theory]
    [InlineData(BodyRouteKind.Create, InvalidJsonKind.Malformed)]
    [InlineData(BodyRouteKind.Create, InvalidJsonKind.Unmapped)]
    [InlineData(BodyRouteKind.Draft, InvalidJsonKind.Malformed)]
    [InlineData(BodyRouteKind.Draft, InvalidJsonKind.Unmapped)]
    [InlineData(BodyRouteKind.Skill, InvalidJsonKind.Malformed)]
    [InlineData(BodyRouteKind.Skill, InvalidJsonKind.Unmapped)]
    public async Task ReviewOrdering_WrongScope_ShouldPrecedeBodyBinding(
        BodyRouteKind routeKind,
        InvalidJsonKind jsonKind)
    {
        await using var host = await EndpointTestHost.StartAsync();
        using var request = BodyRequest(
            routeKind,
            InvalidJson(jsonKind),
            scopeClaims: "scope-other",
            ifMatch: "\"agent-profile-v14\"");

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("SCOPE_ACCESS_DENIED");
        host.TotalServiceCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(BodyRouteKind.Draft, InvalidJsonKind.Malformed)]
    [InlineData(BodyRouteKind.Draft, InvalidJsonKind.Unmapped)]
    [InlineData(BodyRouteKind.Skill, InvalidJsonKind.Malformed)]
    [InlineData(BodyRouteKind.Skill, InvalidJsonKind.Unmapped)]
    public async Task ReviewOrdering_MissingIfMatch_ShouldPrecedeBodyBinding(
        BodyRouteKind routeKind,
        InvalidJsonKind jsonKind)
    {
        await using var host = await EndpointTestHost.StartAsync();
        using var request = BodyRequest(routeKind, InvalidJson(jsonKind), ifMatch: null);

        using var response = await host.Client.SendAsync(request);

        ((int)response.StatusCode).Should().Be(StatusCodes.Status428PreconditionRequired);
        (await response.Content.ReadAsStringAsync()).Should().Contain("AGENT_PROFILE_IF_MATCH_REQUIRED");
        host.TotalServiceCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(BodyRouteKind.Draft)]
    [InlineData(BodyRouteKind.Skill)]
    public async Task ReviewOrdering_InvalidIfMatch_ShouldPrecedeBodyBinding(BodyRouteKind routeKind)
    {
        await using var host = await EndpointTestHost.StartAsync();
        using var request = BodyRequest(
            routeKind,
            InvalidJson(InvalidJsonKind.Malformed),
            ifMatch: "W/\"agent-profile-v14\"");

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("INVALID_AGENT_PROFILE_IF_MATCH");
        host.TotalServiceCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(BodyRouteKind.Create, InvalidJsonKind.Malformed)]
    [InlineData(BodyRouteKind.Create, InvalidJsonKind.Unmapped)]
    [InlineData(BodyRouteKind.Draft, InvalidJsonKind.Malformed)]
    [InlineData(BodyRouteKind.Draft, InvalidJsonKind.Unmapped)]
    [InlineData(BodyRouteKind.Skill, InvalidJsonKind.Malformed)]
    [InlineData(BodyRouteKind.Skill, InvalidJsonKind.Unmapped)]
    public async Task ReviewOrdering_ValidGates_InvalidBodyShouldRemainSafeBadRequest(
        BodyRouteKind routeKind,
        InvalidJsonKind jsonKind)
    {
        await using var host = await EndpointTestHost.StartAsync();
        using var request = BodyRequest(
            routeKind,
            InvalidJson(jsonKind),
            ifMatch: "\"agent-profile-v14\"");

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("INVALID_AGENT_PROFILE_HTTP_BODY");
        host.TotalServiceCalls.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(ReviewInvalidStructureBodies))]
    public async Task ReviewStructure_NullOrMissingRequiredMember_ShouldReturnSafeBadRequest(
        BodyRouteKind routeKind,
        string caseName,
        string json)
    {
        await using var host = await EndpointTestHost.StartAsync();
        using var request = BodyRequest(
            routeKind,
            json,
            ifMatch: "\"agent-profile-v14\"");

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, caseName);
        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.Should().Contain("INVALID_AGENT_PROFILE_HTTP_BODY", caseName);
        responseBody.Should().NotContain(SecretInstructions);
        responseBody.Should().NotContain(SecretSkillBody);
        host.TotalServiceCalls.Should().Be(0, caseName);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("ownerSubject")]
    [InlineData("scopeId")]
    [InlineData("profileId")]
    [InlineData("system")]
    [InlineData("publishedRevision")]
    [InlineData("sealedSkill")]
    [InlineData("skillBindings")]
    [InlineData("skills")]
    public async Task Create_WhenBodyContainsForgedOrUnmappedField_ShouldReturnBadRequest(string field)
    {
        await using var host = await EndpointTestHost.StartAsync();
        var body = CreateBody().ToDictionary(static pair => pair.Key, static pair => pair.Value);
        body[field] = field == "publishedRevision" ? 7 : "forged";
        using var request = CreateRequest(HttpMethod.Post, CollectionRoute());
        request.Content = JsonContent.Create(body);
        request.Headers.Add("Idempotency-Key", "create-alpha");

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.TotalServiceCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("skillBody")]
    [InlineData("skillName")]
    [InlineData("latest")]
    [InlineData("metadata")]
    public async Task SkillPut_WhenBodyContainsAliasOrUnmappedField_ShouldReturnBadRequest(string field)
    {
        await using var host = await EndpointTestHost.StartAsync();
        var body = SkillBody();
        body[field] = field == "latest" ? true : SecretSkillBody;
        using var request = CreateRequest(HttpMethod.Put, SkillRoute());
        request.Content = JsonContent.Create(body);
        request.Headers.TryAddWithoutValidation("If-Match", "\"agent-profile-v14\"");

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.TotalServiceCalls.Should().Be(0);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(SecretSkillBody);
    }

    [Fact]
    public async Task SkillPut_ShouldMapOnlyActivationAndFourExactReferenceFields()
    {
        await using var host = await EndpointTestHost.StartAsync();
        using var request = CreateRequest(HttpMethod.Put, SkillRoute());
        request.Content = JsonContent.Create(SkillBody());
        request.Headers.TryAddWithoutValidation("If-Match", "\"agent-profile-v14\"");
        request.Headers.Add("Idempotency-Key", "skill-key-alpha");

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var call = host.CommandService.UpsertCalls.Should().ContainSingle().Which;
        call.ProfileSlug.Should().Be(ProfileSlug);
        call.BindingId.Should().Be("binding-alpha");
        call.ExpectedVersion.Should().Be(14);
        call.IdempotencyKey.Should().Be("skill-key-alpha");
        call.Request.ActivationMode.Should().Be(AgentProfileSkillActivationMode.Routed);
        call.Request.Skill.Should().BeEquivalentTo(new ExactOrnnSkillReference
        {
            SkillGuid = "2d05bf2e-88ee-4f76-9998-728ba2f9db10",
            LiteralVersion = "1.4",
            ExpectedName = "xiaomi-home-control",
            ExpectedPublisherId = "publisher-123",
        });
        call.Request.GetType().GetProperties().Select(static property => property.Name)
            .Should().BeEquivalentTo("ActivationMode", "Skill");
    }

    [Fact]
    public async Task ManagementGet_ShouldEmitStrongAuthorityEtagAndExplicitOwnerSafeJson()
    {
        await using var host = await EndpointTestHost.StartAsync();
        using var request = CreateRequest(HttpMethod.Get, ManagementRoute());

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag.Should().NotBeNull();
        response.Headers.ETag!.IsWeak.Should().BeFalse();
        response.Headers.ETag.Tag.Should().Be("\"agent-profile-v14\"");
        var text = await response.Content.ReadAsStringAsync();
        text.Should().Contain(SecretInstructions);
        text.Should().NotContain(SecretBearer);
        text.Should().NotContain(SecretSkillBody);
        using var json = JsonDocument.Parse(text);
        json.RootElement.GetProperty("authorityStateVersion").GetInt64().Should().Be(14);
        json.RootElement.GetProperty("draft").GetProperty("skillBindings").GetArrayLength().Should().Be(1);
        json.RootElement.TryGetProperty("identity", out _).Should().BeFalse();
        json.RootElement.TryGetProperty("owner", out _).Should().BeFalse();
        json.RootElement.TryGetProperty("lastEventId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ManagementGet_WhenHidden_ShouldReturnNotFound()
    {
        await using var host = await EndpointTestHost.StartAsync();
        host.QueryService.OwnedResult = null;
        using var request = CreateRequest(HttpMethod.Get, ManagementRoute());

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        host.QueryService.OwnedCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task Discovery_ShouldReturnExactlySafePublishedCatalogFields()
    {
        await using var host = await EndpointTestHost.StartAsync();
        using var request = CreateRequest(HttpMethod.Get, DiscoveryRoute());

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain(SecretInstructions);
        text.Should().NotContain(SecretSkillBody);
        text.Should().NotContain(SecretBearer);
        using var json = JsonDocument.Parse(text);
        json.RootElement.EnumerateObject().Select(static property => property.Name).Should().BeEquivalentTo(
            "reference",
            "displayName",
            "purpose",
            "publishedRevision",
            "available");
        json.RootElement.GetProperty("reference").EnumerateObject()
            .Select(static property => property.Name)
            .Should().BeEquivalentTo("ownerHandle", "profileSlug");
        var call = host.QueryService.DiscoveryCalls.Should().ContainSingle().Which;
        call.Reference.OwnerHandle.Should().Be(OwnerHandle);
        call.Reference.ProfileSlug.Should().Be(ProfileSlug);
        call.Caller.ScopeId.Should().Be(ScopeId);
    }

    [Fact]
    public async Task Discovery_WhenInaccessible_ShouldReturnNotFound()
    {
        await using var host = await EndpointTestHost.StartAsync();
        host.QueryService.DiscoveryResult = null;
        using var request = CreateRequest(HttpMethod.Get, DiscoveryRoute());

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        host.QueryService.DiscoveryCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task Validate_ShouldReturnSafeTypedReportWithOk()
    {
        await using var host = await EndpointTestHost.StartAsync();
        using var request = CreateRequest(HttpMethod.Post, ValidateRoute());

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain(SecretInstructions);
        text.Should().NotContain(SecretSkillBody);
        text.Should().NotContain(SecretBearer);
        using var json = JsonDocument.Parse(text);
        json.RootElement.EnumerateObject().Select(static property => property.Name).Should().BeEquivalentTo(
            "valid",
            "draftRevision",
            "draftDigest",
            "diagnostics",
            "resolvedSkills");
        json.RootElement.GetProperty("resolvedSkills")[0].EnumerateObject()
            .Select(static property => property.Name)
            .Should().BeEquivalentTo("bindingId", "exactReference", "contentSha256");
        host.CommandService.ValidateCalls.Should().ContainSingle();
    }

    [Theory]
    [InlineData(MutationKind.Create)]
    [InlineData(MutationKind.UpdateDraft)]
    [InlineData(MutationKind.UpsertSkill)]
    [InlineData(MutationKind.RemoveSkill)]
    [InlineData(MutationKind.Publish)]
    public async Task Mutations_WhenTask6Accepts_ShouldReturnAcceptedOnly(MutationKind kind)
    {
        await using var host = await EndpointTestHost.StartAsync();
        using var request = MutationRequest(kind);

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.TotalServiceCalls.Should().Be(1);
        using var json = await ReadJsonAsync(response);
        json.RootElement.GetProperty("accepted").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("ackStage").GetString().Should().Be("accepted");
    }

    [Fact]
    public async Task Mutation_WhenReceiptIsNotAccepted_ShouldFailClosed()
    {
        await using var host = await EndpointTestHost.StartAsync();
        host.CommandService.Receipt = host.CommandService.Receipt with
        {
            Accepted = false,
            AckStage = "rejected",
        };
        using var request = MutationRequest(MutationKind.Publish);

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.Accepted);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var json = await ReadJsonAsync(response);
        json.RootElement.GetProperty("code").GetString().Should().Be("AGENT_PROFILE_DISPATCH_REJECTED");
        json.RootElement.TryGetProperty("ackStage", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("accepted-false")]
    [InlineData("ack-stage")]
    [InlineData("operation-empty")]
    [InlineData("operation-whitespace")]
    [InlineData("command-empty")]
    [InlineData("command-whitespace")]
    [InlineData("correlation-empty")]
    [InlineData("correlation-whitespace")]
    [InlineData("actor-empty")]
    [InlineData("actor-whitespace")]
    [InlineData("profile-empty")]
    [InlineData("profile-whitespace")]
    [InlineData("resource-empty")]
    [InlineData("resource-whitespace")]
    [InlineData("resource-absolute")]
    [InlineData("resource-wrong-scope")]
    [InlineData("resource-wrong-slug")]
    [InlineData("resource-query")]
    [InlineData("resource-fragment")]
    [InlineData("resource-traversal")]
    [InlineData("resource-encoded-slash")]
    [InlineData("resource-encoded-backslash")]
    [InlineData("resource-trailing-slash")]
    [InlineData("resource-double-slash")]
    public async Task ReviewReceipt_InvalidAcceptedReceipt_ShouldFailClosedWithoutLocation(string caseName)
    {
        await using var host = await EndpointTestHost.StartAsync();
        host.CommandService.Receipt = InvalidReceipt(host.CommandService.Receipt, caseName);
        using var request = MutationRequest(MutationKind.Publish);

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, caseName);
        response.Headers.Location.Should().BeNull(caseName);
        using var json = await ReadJsonAsync(response);
        json.RootElement.EnumerateObject().Select(static property => property.Name)
            .Should().Equal("code");
        json.RootElement.GetProperty("code").GetString()
            .Should().Be("AGENT_PROFILE_DISPATCH_REJECTED");
        host.TotalServiceCalls.Should().Be(1, caseName);
    }

    [Fact]
    public async Task ReviewReceipt_ValidAcceptedReceipt_ShouldUseCanonicalSameResourceLocation()
    {
        await using var host = await EndpointTestHost.StartAsync();
        using var request = MutationRequest(MutationKind.Publish);

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.OriginalString.Should().Be(ManagementRoute());
        using var json = await ReadJsonAsync(response);
        json.RootElement.EnumerateObject().Select(static property => property.Name).Should().BeEquivalentTo(
            "accepted",
            "ackStage",
            "operationId",
            "commandId",
            "correlationId",
            "actorId",
            "profileId",
            "resourceUrl");
        json.RootElement.GetProperty("resourceUrl").GetString().Should().Be(ManagementRoute());
        json.RootElement.GetProperty("ackStage").GetString().Should().Be("accepted");
    }

    [Theory]
    [InlineData(FailureKind.Request, HttpStatusCode.BadRequest)]
    [InlineData(FailureKind.NotFound, HttpStatusCode.NotFound)]
    [InlineData(FailureKind.Stale, HttpStatusCode.PreconditionFailed)]
    [InlineData(FailureKind.AuthenticationRequired, HttpStatusCode.Unauthorized)]
    [InlineData(FailureKind.PublishValidation, HttpStatusCode.UnprocessableEntity)]
    [InlineData(FailureKind.DependencyUnavailable, HttpStatusCode.ServiceUnavailable)]
    [InlineData(FailureKind.DispatchRejected, HttpStatusCode.ServiceUnavailable)]
    public async Task TypedTask6Failures_ShouldMapToSafeStatus(
        FailureKind failure,
        HttpStatusCode expectedStatus)
    {
        await using var host = await EndpointTestHost.StartAsync();
        host.CommandService.PublishFailure = CreateFailure(failure);
        using var request = MutationRequest(MutationKind.Publish);

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(expectedStatus);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(FailureCode(failure));
        body.Should().NotContain(SecretBearer);
        body.Should().NotContain(SecretInstructions);
        body.Should().NotContain(SecretSkillBody);
        body.Should().NotContain("remote exception body");
    }

    [Fact]
    public async Task Handlers_ShouldPassRequestCancellationTokenToSingleServiceCall()
    {
        await using var host = await EndpointTestHost.StartAsync();
        using var request = CreateRequest(HttpMethod.Get, ManagementRoute());

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.QueryService.OwnedCalls.Should().ContainSingle()
            .Which.CancellationToken.CanBeCanceled.Should().BeTrue();
        host.TotalServiceCalls.Should().Be(1);
    }

    public static IEnumerable<object[]> ReviewInvalidStructureBodies()
    {
        foreach (var routeKind in Enum.GetValues<BodyRouteKind>())
        {
            yield return [routeKind, "body-null", "null"];
            foreach (var path in RequiredBodyPaths(routeKind))
            {
                yield return [
                    routeKind,
                    $"{path}-missing",
                    MutateBody(routeKind, path, remove: true),
                ];
                yield return [
                    routeKind,
                    $"{path}-null",
                    MutateBody(routeKind, path, remove: false),
                ];
            }

            if (routeKind is BodyRouteKind.Create or BodyRouteKind.Draft)
            {
                yield return [
                    routeKind,
                    "toolPolicy.toolNames-null-element",
                    AddNullArrayElement(routeKind, "toolPolicy.toolNames"),
                ];
                yield return [
                    routeKind,
                    "toolPolicy.toolSetRefs-null-element",
                    AddNullArrayElement(routeKind, "toolPolicy.toolSetRefs"),
                ];
            }
        }
    }

    private static HttpRequestMessage MutationRequest(MutationKind kind)
    {
        var request = kind switch
        {
            MutationKind.Create => CreateRequest(HttpMethod.Post, CollectionRoute()),
            MutationKind.UpdateDraft => CreateRequest(HttpMethod.Put, DraftRoute()),
            MutationKind.UpsertSkill => CreateRequest(HttpMethod.Put, SkillRoute()),
            MutationKind.RemoveSkill => CreateRequest(HttpMethod.Delete, SkillRoute()),
            MutationKind.Publish => CreateRequest(HttpMethod.Post, PublishRoute()),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        if (kind == MutationKind.Create)
        {
            request.Content = JsonContent.Create(CreateBody());
            request.Headers.Add("Idempotency-Key", "create-alpha");
        }
        else
        {
            request.Headers.TryAddWithoutValidation("If-Match", "\"agent-profile-v14\"");
            if (kind == MutationKind.UpdateDraft)
                request.Content = JsonContent.Create(DraftBody());
            else if (kind == MutationKind.UpsertSkill)
                request.Content = JsonContent.Create(SkillBody());
        }

        return request;
    }

    private static HttpRequestMessage BodyRequest(
        BodyRouteKind routeKind,
        string json,
        string? scopeClaims = ScopeId,
        string? ifMatch = null)
    {
        var request = routeKind switch
        {
            BodyRouteKind.Create => CreateRequest(
                HttpMethod.Post,
                CollectionRoute(),
                scopeClaims: scopeClaims),
            BodyRouteKind.Draft => CreateRequest(
                HttpMethod.Put,
                DraftRoute(),
                scopeClaims: scopeClaims),
            BodyRouteKind.Skill => CreateRequest(
                HttpMethod.Put,
                SkillRoute(),
                scopeClaims: scopeClaims),
            _ => throw new ArgumentOutOfRangeException(nameof(routeKind), routeKind, null),
        };
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (routeKind == BodyRouteKind.Create)
            request.Headers.Add("Idempotency-Key", "create-alpha");
        else if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return request;
    }

    private static string InvalidJson(InvalidJsonKind kind) => kind switch
    {
        InvalidJsonKind.Malformed => "{\"unexpected\":",
        InvalidJsonKind.Unmapped => "{\"unexpected\":true}",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static IReadOnlyList<string> RequiredBodyPaths(BodyRouteKind routeKind) => routeKind switch
    {
        BodyRouteKind.Create =>
        [
            "profileSlug",
            "displayName",
            "purpose",
            "instructions",
            "toolPolicy",
            "toolPolicy.mode",
            "toolPolicy.toolNames",
            "toolPolicy.toolSetRefs",
        ],
        BodyRouteKind.Draft =>
        [
            "displayName",
            "purpose",
            "instructions",
            "toolPolicy",
            "toolPolicy.mode",
            "toolPolicy.toolNames",
            "toolPolicy.toolSetRefs",
        ],
        BodyRouteKind.Skill =>
        [
            "activationMode",
            "skill",
            "skill.skillGuid",
            "skill.literalVersion",
            "skill.expectedName",
            "skill.expectedPublisherId",
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(routeKind), routeKind, null),
    };

    private static string MutateBody(BodyRouteKind routeKind, string path, bool remove)
    {
        var root = BodyNode(routeKind);
        var (container, propertyName) = ResolveJsonProperty(root, path);
        if (remove)
            container.Remove(propertyName);
        else
            container[propertyName] = null;
        return root.ToJsonString();
    }

    private static string AddNullArrayElement(BodyRouteKind routeKind, string path)
    {
        var root = BodyNode(routeKind);
        var (container, propertyName) = ResolveJsonProperty(root, path);
        var array = container[propertyName] as JsonArray
            ?? throw new InvalidOperationException($"'{path}' is not a JSON array.");
        array.Add((JsonNode?)null);
        return root.ToJsonString();
    }

    private static JsonObject BodyNode(BodyRouteKind routeKind)
    {
        object body = routeKind switch
        {
            BodyRouteKind.Create => CreateBody(),
            BodyRouteKind.Draft => DraftBody(),
            BodyRouteKind.Skill => SkillBody(),
            _ => throw new ArgumentOutOfRangeException(nameof(routeKind), routeKind, null),
        };
        return JsonSerializer.SerializeToNode(body)?.AsObject()
            ?? throw new InvalidOperationException("The test body could not be serialized.");
    }

    private static (JsonObject Container, string PropertyName) ResolveJsonProperty(
        JsonObject root,
        string path)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var container = root;
        foreach (var segment in segments[..^1])
        {
            container = container[segment] as JsonObject
                ?? throw new InvalidOperationException($"'{path}' has no object container.");
        }

        return (container, segments[^1]);
    }

    private static AgentProfileAcceptedReceipt InvalidReceipt(
        AgentProfileAcceptedReceipt valid,
        string caseName) => caseName switch
    {
        "accepted-false" => valid with { Accepted = false },
        "ack-stage" => valid with { AckStage = "committed" },
        "operation-empty" => valid with { OperationId = string.Empty },
        "operation-whitespace" => valid with { OperationId = " " },
        "command-empty" => valid with { CommandId = string.Empty },
        "command-whitespace" => valid with { CommandId = " " },
        "correlation-empty" => valid with { CorrelationId = string.Empty },
        "correlation-whitespace" => valid with { CorrelationId = " " },
        "actor-empty" => valid with { ActorId = string.Empty },
        "actor-whitespace" => valid with { ActorId = " " },
        "profile-empty" => valid with { ProfileId = string.Empty },
        "profile-whitespace" => valid with { ProfileId = " " },
        "resource-empty" => valid with { ResourceUrl = string.Empty },
        "resource-whitespace" => valid with { ResourceUrl = " " },
        "resource-absolute" => valid with { ResourceUrl = "https://evil.example/api/scopes/scope-alpha/agent-profiles/profile-alpha" },
        "resource-wrong-scope" => valid with { ResourceUrl = "/api/scopes/scope-other/agent-profiles/profile-alpha" },
        "resource-wrong-slug" => valid with { ResourceUrl = "/api/scopes/scope-alpha/agent-profiles/profile-other" },
        "resource-query" => valid with { ResourceUrl = $"{ManagementRoute()}?secret=value" },
        "resource-fragment" => valid with { ResourceUrl = $"{ManagementRoute()}#fragment" },
        "resource-traversal" => valid with { ResourceUrl = "/api/scopes/scope-alpha/agent-profiles/../profile-alpha" },
        "resource-encoded-slash" => valid with { ResourceUrl = "/api/scopes/scope-alpha/agent-profiles/profile%2Falpha" },
        "resource-encoded-backslash" => valid with { ResourceUrl = "/api/scopes/scope-alpha/agent-profiles/profile%5Calpha" },
        "resource-trailing-slash" => valid with { ResourceUrl = $"{ManagementRoute()}/" },
        "resource-double-slash" => valid with { ResourceUrl = "/api/scopes//scope-alpha/agent-profiles/profile-alpha" },
        _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, null),
    };

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string route,
        string? scopeClaims = ScopeId,
        string? subjectClaimType = "sub",
        string? subjectClaims = SubjectId)
    {
        var request = new HttpRequestMessage(method, route);
        request.Headers.Add("X-Test-Authenticated", "true");
        if (scopeClaims is not null)
            request.Headers.Add("X-Test-Claim-scope_id", scopeClaims);
        if (subjectClaimType is not null && subjectClaims is not null)
            request.Headers.Add(ClaimHeaderName(subjectClaimType), subjectClaims);
        return request;
    }

    private static string ClaimHeaderName(string claimType) =>
        string.Equals(claimType, ClaimTypes.NameIdentifier, StringComparison.Ordinal)
            ? "X-Test-Claim-nameidentifier"
            : $"X-Test-Claim-{claimType}";

    private static Dictionary<string, object?> CreateBody(string? ownerHandle = OwnerHandle) =>
        new(StringComparer.Ordinal)
        {
            ["profileSlug"] = ProfileSlug,
            ["ownerHandle"] = ownerHandle,
            ["displayName"] = "Profile Alpha",
            ["purpose"] = "Safe owner purpose",
            ["instructions"] = SecretInstructions,
            ["toolPolicy"] = ToolPolicyBody(),
        };

    private static Dictionary<string, object?> DraftBody() =>
        new(StringComparer.Ordinal)
        {
            ["displayName"] = "Profile Alpha Updated",
            ["purpose"] = "Updated purpose",
            ["instructions"] = SecretInstructions,
            ["toolPolicy"] = ToolPolicyBody(),
        };

    private static Dictionary<string, object?> ToolPolicyBody() =>
        new(StringComparer.Ordinal)
        {
            ["mode"] = "INHERIT_ROUTE_MAXIMUM",
            ["toolNames"] = Array.Empty<string>(),
            ["toolSetRefs"] = Array.Empty<string>(),
        };

    private static Dictionary<string, object?> SkillBody() =>
        new(StringComparer.Ordinal)
        {
            ["activationMode"] = "ROUTED",
            ["skill"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["skillGuid"] = "2d05bf2e-88ee-4f76-9998-728ba2f9db10",
                ["literalVersion"] = "1.4",
                ["expectedName"] = "xiaomi-home-control",
                ["expectedPublisherId"] = "publisher-123",
            },
        };

    private static AgentProfileApplicationException CreateFailure(FailureKind failure)
    {
        var diagnostics = new[]
        {
            new AgentProfileSafeDiagnostic
            {
                Code = failure == FailureKind.PublishValidation
                    ? "ORNN_EXACT_REFERENCE_MISMATCH"
                    : FailureCode(failure),
                Message = $"safe diagnostic; remote exception body; {SecretSkillBody}",
                Path = "draft.skillBindings[0]",
            },
        };
        return failure switch
        {
            FailureKind.Request => new AgentProfileRequestException("INVALID_AGENT_PROFILE_REQUEST", diagnostics),
            FailureKind.NotFound => new AgentProfileNotFoundException(),
            FailureKind.Stale => new AgentProfilePreconditionException(13, 14),
            FailureKind.AuthenticationRequired => new AgentProfileAuthenticationRequiredException(diagnostics),
            FailureKind.PublishValidation => new AgentProfilePublishValidationException(diagnostics),
            FailureKind.DependencyUnavailable => new AgentProfileDependencyUnavailableException(diagnostics),
            FailureKind.DispatchRejected => new AgentProfileDispatchRejectedException(),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
        };
    }

    private static string FailureCode(FailureKind failure) => failure switch
    {
        FailureKind.Request => "INVALID_AGENT_PROFILE_REQUEST",
        FailureKind.NotFound => "AGENT_PROFILE_NOT_FOUND",
        FailureKind.Stale => "AGENT_PROFILE_STALE_VERSION",
        FailureKind.AuthenticationRequired => "ORNN_ACCESS_TOKEN_REQUIRED",
        FailureKind.PublishValidation => "ORNN_EXACT_REFERENCE_MISMATCH",
        FailureKind.DependencyUnavailable => "ORNN_DEPENDENCY_UNAVAILABLE",
        FailureKind.DispatchRejected => "AGENT_PROFILE_DISPATCH_REJECTED",
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
    };

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static string CollectionRoute() => $"/api/scopes/{ScopeId}/agent-profiles";
    private static string ManagementRoute() => $"{CollectionRoute()}/{ProfileSlug}";
    private static string DraftRoute() => $"{ManagementRoute()}/draft";
    private static string SkillRoute() => $"{DraftRoute()}/skills/binding-alpha";
    private static string ValidateRoute() => $"{ManagementRoute()}:validate";
    private static string PublishRoute() => $"{ManagementRoute()}:publish";
    private static string DiscoveryRoute() => $"/api/agent-profiles/{OwnerHandle}/{ProfileSlug}";

    public enum MutationKind
    {
        Create,
        UpdateDraft,
        UpsertSkill,
        RemoveSkill,
        Publish,
    }

    public enum BodyRouteKind
    {
        Create,
        Draft,
        Skill,
    }

    public enum InvalidJsonKind
    {
        Malformed,
        Unmapped,
    }

    public enum FailureKind
    {
        Request,
        NotFound,
        Stale,
        AuthenticationRequired,
        PublishValidation,
        DependencyUnavailable,
        DispatchRejected,
    }

    private sealed class EndpointTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private EndpointTestHost(
            WebApplication app,
            HttpClient client,
            RecordingAgentProfileCommandService commandService,
            RecordingAgentProfileQueryService queryService,
            IReadOnlyList<string> agentProfileRoutes)
        {
            _app = app;
            Client = client;
            CommandService = commandService;
            QueryService = queryService;
            AgentProfileRoutes = agentProfileRoutes;
        }

        public HttpClient Client { get; }
        public RecordingAgentProfileCommandService CommandService { get; }
        public RecordingAgentProfileQueryService QueryService { get; }
        public IReadOnlyList<string> AgentProfileRoutes { get; }
        public int TotalServiceCalls => CommandService.TotalCalls + QueryService.TotalCalls;

        public static async Task<EndpointTestHost> StartAsync(bool authenticationEnabled = true)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["Aevatar:Authentication:Enabled"] = authenticationEnabled ? "true" : "false";
            builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            builder.Services.AddAuthorization();
            var commandService = new RecordingAgentProfileCommandService();
            var queryService = new RecordingAgentProfileQueryService();
            builder.Services.AddSingleton<IAgentProfileCommandService>(commandService);
            builder.Services.AddSingleton<IAgentProfileQueryService>(queryService);

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGAgentServiceEndpoints();
            var agentProfileRoutes = ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(static source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Where(static endpoint => endpoint.RoutePattern.RawText?.Contains("agent-profiles", StringComparison.Ordinal) == true)
                .SelectMany(static endpoint =>
                    (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                        .Select(method => $"{method} {endpoint.RoutePattern.RawText}"))
                .OrderBy(static route => route, StringComparer.Ordinal)
                .ToArray();
            await app.StartAsync();

            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Server addresses are unavailable.");
            var client = new HttpClient
            {
                BaseAddress = new Uri(addresses.Addresses.Single()),
            };
            return new EndpointTestHost(app, client, commandService, queryService, agentProfileRoutes);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "AgentProfileTest";

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Authenticated", out var authenticatedValues) ||
                !bool.TryParse(authenticatedValues.ToString(), out var authenticated) ||
                !authenticated)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = Request.Headers
                .Where(static header => header.Key.StartsWith("X-Test-Claim-", StringComparison.OrdinalIgnoreCase))
                .SelectMany(static header => header.Value.ToString()
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(value => new Claim(
                        string.Equals(
                            header.Key["X-Test-Claim-".Length..],
                            "nameidentifier",
                            StringComparison.OrdinalIgnoreCase)
                                ? ClaimTypes.NameIdentifier
                                : header.Key["X-Test-Claim-".Length..],
                        value)))
                .ToArray();
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }

    private sealed class RecordingAgentProfileCommandService : IAgentProfileCommandService
    {
        public AgentProfileAcceptedReceipt Receipt { get; set; } = new(
            true,
            "accepted",
            "operation-alpha",
            "command-alpha",
            "correlation-alpha",
            "actor-alpha",
            "prof-alpha",
            ManagementRoute());

        public Exception? PublishFailure { get; set; }
        public List<CreateCall> CreateCalls { get; } = [];
        public List<UpdateDraftCall> UpdateCalls { get; } = [];
        public List<UpsertCall> UpsertCalls { get; } = [];
        public List<RemoveCall> RemoveCalls { get; } = [];
        public List<ValidateCall> ValidateCalls { get; } = [];
        public List<PublishCall> PublishCalls { get; } = [];
        public int TotalCalls => CreateCalls.Count + UpdateCalls.Count + UpsertCalls.Count +
            RemoveCalls.Count + ValidateCalls.Count + PublishCalls.Count;

        public Task<AgentProfileAcceptedReceipt> CreateAsync(
            AgentProfileCallerContext caller,
            CreateAgentProfileRequest request,
            string idempotencyKey,
            CancellationToken ct = default)
        {
            CreateCalls.Add(new(caller, request, idempotencyKey, ct));
            return Task.FromResult(Receipt);
        }

        public Task<AgentProfileAcceptedReceipt> UpdateDraftAsync(
            AgentProfileCallerContext caller,
            string profileSlug,
            long expectedAuthorityStateVersion,
            UpdateAgentProfileDraftRequest request,
            string? idempotencyKey,
            CancellationToken ct = default)
        {
            UpdateCalls.Add(new(caller, profileSlug, expectedAuthorityStateVersion, request, idempotencyKey, ct));
            return Task.FromResult(Receipt);
        }

        public Task<AgentProfileAcceptedReceipt> UpsertSkillBindingAsync(
            AgentProfileCallerContext caller,
            string profileSlug,
            string bindingId,
            long expectedAuthorityStateVersion,
            UpsertAgentProfileSkillBindingRequest request,
            string? idempotencyKey,
            CancellationToken ct = default)
        {
            UpsertCalls.Add(new(caller, profileSlug, bindingId, expectedAuthorityStateVersion, request, idempotencyKey, ct));
            return Task.FromResult(Receipt);
        }

        public Task<AgentProfileAcceptedReceipt> RemoveSkillBindingAsync(
            AgentProfileCallerContext caller,
            string profileSlug,
            string bindingId,
            long expectedAuthorityStateVersion,
            string? idempotencyKey,
            CancellationToken ct = default)
        {
            RemoveCalls.Add(new(caller, profileSlug, bindingId, expectedAuthorityStateVersion, idempotencyKey, ct));
            return Task.FromResult(Receipt);
        }

        public Task<AgentProfileValidationReport> ValidateAsync(
            AgentProfileCallerContext caller,
            string profileSlug,
            CancellationToken ct = default)
        {
            ValidateCalls.Add(new(caller, profileSlug, ct));
            return Task.FromResult(ValidationReport());
        }

        public Task<AgentProfileAcceptedReceipt> PublishAsync(
            AgentProfileCallerContext caller,
            string profileSlug,
            long expectedAuthorityStateVersion,
            string? idempotencyKey,
            CancellationToken ct = default)
        {
            PublishCalls.Add(new(caller, profileSlug, expectedAuthorityStateVersion, idempotencyKey, ct));
            return PublishFailure is null
                ? Task.FromResult(Receipt)
                : Task.FromException<AgentProfileAcceptedReceipt>(PublishFailure);
        }
    }

    private sealed class RecordingAgentProfileQueryService : IAgentProfileQueryService
    {
        public AgentProfileManagementSnapshot? OwnedResult { get; set; } = ManagementSnapshot();
        public AgentProfileDiscoverySnapshot? DiscoveryResult { get; set; } = DiscoverySnapshot();
        public List<OwnedCall> OwnedCalls { get; } = [];
        public List<DiscoveryCall> DiscoveryCalls { get; } = [];
        public int TotalCalls => OwnedCalls.Count + DiscoveryCalls.Count;

        public Task<AgentProfileManagementSnapshot?> GetOwnedAsync(
            AgentProfileCallerContext caller,
            string profileSlug,
            CancellationToken ct = default)
        {
            OwnedCalls.Add(new(caller, profileSlug, ct));
            return Task.FromResult(OwnedResult?.DeepClone());
        }

        public Task<AgentProfileDiscoverySnapshot?> ResolveVisibleAsync(
            AgentProfileCallerContext caller,
            AgentProfileReference reference,
            CancellationToken ct = default)
        {
            DiscoveryCalls.Add(new(caller, reference.Clone(), ct));
            return Task.FromResult(DiscoveryResult?.DeepClone());
        }
    }

    private static AgentProfileManagementSnapshot ManagementSnapshot()
    {
        var identity = new AgentProfileIdentity
        {
            ProfileId = "prof-alpha",
            Owner = new AgentProfileOwnerIdentity
            {
                User = new AgentProfileUserOwnerIdentity
                {
                    IdentityProvider = "nyxid",
                    SubjectId = SubjectId,
                },
            },
            OwningScopeId = ScopeId,
            Reference = Reference(),
        };
        var draft = new AgentProfileContent
        {
            DisplayName = "Profile Alpha",
            Purpose = "Safe owner purpose",
            Instructions = SecretInstructions,
            ToolPolicy = new AgentProfileToolPolicy
            {
                Mode = AgentProfileToolPolicyMode.InheritRouteMaximum,
            },
        };
        draft.SkillBindings.Add(new AgentProfileSkillBinding
        {
            BindingId = "binding-alpha",
            ActivationMode = AgentProfileSkillActivationMode.Routed,
            Skill = ExactReference(),
        });
        return new AgentProfileManagementSnapshot(
            14,
            "event-alpha",
            identity,
            draft,
            4,
            Digest(1),
            3,
            Digest(2),
            Digest(3),
            new AgentProfileMutationOutcome
            {
                Operation = new AgentProfileOperationFact
                {
                    OperationId = "operation-alpha",
                    CommandId = "command-alpha",
                    CorrelationId = "correlation-alpha",
                    InputSha256 = Digest(4),
                },
                Status = AgentProfileMutationStatus.Applied,
                DraftRevision = 4,
                DraftSha256 = Digest(1),
                PublishedRevision = 3,
                PublishedSnapshotSha256 = Digest(2),
            });
    }

    private static AgentProfileValidationReport ValidationReport() => new(
        true,
        4,
        Digest(1),
        [new AgentProfileSafeDiagnostic { Code = "SAFE_DIAGNOSTIC", Message = "Safe message", Path = "draft" }],
        [new AgentProfileSkillResolutionSummary("binding-alpha", ExactReference(), Digest(5))]);

    private static AgentProfileDiscoverySnapshot DiscoverySnapshot() =>
        new(Reference(), "Published Alpha", "Safe published purpose", 3, true);

    private static AgentProfileReference Reference() => new()
    {
        OwnerHandle = OwnerHandle,
        ProfileSlug = ProfileSlug,
    };

    private static ExactOrnnSkillReference ExactReference() => new()
    {
        SkillGuid = "2d05bf2e-88ee-4f76-9998-728ba2f9db10",
        LiteralVersion = "1.4",
        ExpectedName = "xiaomi-home-control",
        ExpectedPublisherId = "publisher-123",
    };

    private static ByteString Digest(byte value) =>
        ByteString.CopyFrom(Enumerable.Repeat(value, 32).ToArray());

    private sealed record CreateCall(
        AgentProfileCallerContext Caller,
        CreateAgentProfileRequest Request,
        string IdempotencyKey,
        CancellationToken CancellationToken);

    private sealed record UpdateDraftCall(
        AgentProfileCallerContext Caller,
        string ProfileSlug,
        long ExpectedVersion,
        UpdateAgentProfileDraftRequest Request,
        string? IdempotencyKey,
        CancellationToken CancellationToken);

    private sealed record UpsertCall(
        AgentProfileCallerContext Caller,
        string ProfileSlug,
        string BindingId,
        long ExpectedVersion,
        UpsertAgentProfileSkillBindingRequest Request,
        string? IdempotencyKey,
        CancellationToken CancellationToken);

    private sealed record RemoveCall(
        AgentProfileCallerContext Caller,
        string ProfileSlug,
        string BindingId,
        long ExpectedVersion,
        string? IdempotencyKey,
        CancellationToken CancellationToken);

    private sealed record ValidateCall(
        AgentProfileCallerContext Caller,
        string ProfileSlug,
        CancellationToken CancellationToken);

    private sealed record PublishCall(
        AgentProfileCallerContext Caller,
        string ProfileSlug,
        long ExpectedVersion,
        string? IdempotencyKey,
        CancellationToken CancellationToken);

    private sealed record OwnedCall(
        AgentProfileCallerContext Caller,
        string ProfileSlug,
        CancellationToken CancellationToken);

    private sealed record DiscoveryCall(
        AgentProfileCallerContext Caller,
        AgentProfileReference Reference,
        CancellationToken CancellationToken);
}
