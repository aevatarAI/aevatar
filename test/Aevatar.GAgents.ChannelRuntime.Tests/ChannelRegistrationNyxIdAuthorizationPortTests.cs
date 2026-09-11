using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.NyxIdRelay;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Aevatar.GAgents.Channel.NyxIdRelay.VerifiedChannelRegistrationServiceSelection.VerifiedChannelRegistrationAuthorizationPlan;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelRegistrationNyxIdAuthorizationPortTests
{
    [Fact]
    public async Task ReadUserServicesAsync_UsesOwnerTokenAndStrictParser()
    {
        var handler = new RecordingHandler(UserServicesJson());
        var port = CreatePort(handler);

        var result = await port.ReadUserServicesAsync("owner-token", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Value!.Services.Should().ContainSingle().Which.Id.Should().Be("svc-alpha");
        handler.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Method = HttpMethod.Get,
            Path = "/api/v1/user-services",
            Token = "owner-token",
            Body = string.Empty,
        });
    }

    [Fact]
    public async Task PlanApiKeyScopeAsync_PersonalOwner_OmitsTargetOrganizationId()
    {
        var handler = new RecordingHandler(ScopePlanJson());
        var port = CreatePort(handler);

        var result = await port.PlanApiKeyScopeAsync(
            "owner-token",
            ["svc-alpha"],
            null,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Token.Should().Be("owner-token");
        using var body = JsonDocument.Parse(request.Body);
        body.RootElement.GetProperty("selected_service_ids").EnumerateArray()
            .Select(static item => item.GetString()).Should().Equal("svc-alpha");
        body.RootElement.TryGetProperty("target_org_id", out _).Should().BeFalse();
    }

    [Fact]
    public async Task PlanApiKeyScopeAsync_OrganizationOwner_UsesExactOrganizationId()
    {
        var handler = new RecordingHandler(ScopePlanJson(
            ownerType: "organization",
            ownerId: "org-alpha"));
        var port = CreatePort(handler);

        var result = await port.PlanApiKeyScopeAsync(
            "owner-token",
            ["svc-alpha"],
            "org-alpha",
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        using var body = JsonDocument.Parse(handler.Requests.Single().Body);
        body.RootElement.GetProperty("target_org_id").GetString().Should().Be("org-alpha");
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    public async Task ReadUserServicesAsync_MalformedResponse_ReturnsStrictParserFailure(string response)
    {
        var port = CreatePort(new RecordingHandler(response));

        var result = await port.ReadUserServicesAsync("owner-token", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(new NyxIdApiAccessFailure(
            NyxIdApiAccessFailureKind.MalformedResponse,
            "nyxid_user_services_response_malformed"));
    }

    [Fact]
    public async Task PlanApiKeyScopeAsync_ProviderFailure_ReturnsStrictParserFailure()
    {
        var port = CreatePort(new RecordingHandler(
            """{"error":true,"status":403,"body":"{\"error\":\"api_key_scope_plan_denied\",\"error_code\":9004}"}"""));

        var result = await port.PlanApiKeyScopeAsync(
            "owner-token",
            ["svc-alpha"],
            null,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(new NyxIdApiAccessFailure(
            NyxIdApiAccessFailureKind.Forbidden,
            "api_key_scope_plan_denied",
            403,
            9004));
    }

    [Fact]
    public async Task ReadUserServicesAsync_TransportFailure_ReturnsTypedFailure()
    {
        var port = CreatePort(new RecordingHandler(new HttpRequestException("offline")));

        var result = await port.ReadUserServicesAsync("owner-token", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(new NyxIdApiAccessFailure(
            NyxIdApiAccessFailureKind.Transport,
            "nyxid_user_services_failed"));
    }

    [Fact]
    public async Task PlanApiKeyScopeAsync_Cancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var port = CreatePort(new RecordingHandler(ScopePlanJson()));

        var act = () => port.PlanApiKeyScopeAsync(
            "owner-token",
            ["svc-alpha"],
            null,
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void AddNyxIdRelayChannel_RegistersAuthorizationPlannerAndPort()
    {
        var services = new ServiceCollection();

        services.AddNyxIdRelayChannel();

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IChannelRegistrationNyxIdAuthorizationPort) &&
            descriptor.ImplementationType == typeof(ChannelRegistrationNyxIdAuthorizationPort) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(ChannelRegistrationAuthorizationPlanner) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    private static ChannelRegistrationNyxIdAuthorizationPort CreatePort(HttpMessageHandler handler) =>
        new(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler)));

    private static string UserServicesJson() =>
        """
        {"services":[{"id":"svc-alpha","slug":"alpha","is_active":true,"credential_source":{"type":"personal"}}]}
        """;

    private static string ScopePlanJson(
        string ownerType = "personal",
        string ownerId = "owner-alpha") => $$"""
        {
          "authority": "nyxid",
          "contract_version": "1",
          "policy_version": "api-key-scope-v1",
          "authenticated_actor": {"id":"owner-alpha","type":"personal"},
          "intended_key_owner": {"id":"{{ownerId}}","type":"{{ownerType}}"},
          "services": [{
            "user_service_id": "svc-alpha",
            "resource_owner": {"id":"{{ownerId}}","type":"{{ownerType}}"},
            "node_grant": {"type":"not_required"}
          }],
          "allowed_service_ids": ["svc-alpha"],
          "allowed_node_ids": [],
          "evaluated_at": "2026-09-10T00:00:00Z",
          "normalized_grant_digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
          "freshness": {
            "mode": "mutation_revalidated_snapshot",
            "precondition_field": "scope_plan_digest",
            "post_creation_drift": "fail_closed"
          },
          "completeness": {
            "list_complete": true,
            "no_duplicates": true,
            "route_candidate_basis": "active_configured_routes",
            "transient_node_state_excluded": true
          }
        }
        """;

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string Token,
        string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string? _response;
        private readonly Exception? _exception;

        public RecordingHandler(string response) => _response = response;

        public RecordingHandler(Exception exception) => _exception = exception;

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_exception is not null)
                throw _exception;

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                ReadBearerToken(request.Headers.Authorization),
                body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response!, Encoding.UTF8, "application/json"),
            };
        }

        private static string ReadBearerToken(AuthenticationHeaderValue? authorization) =>
            authorization is { Scheme: "Bearer", Parameter: { } token }
                ? token
                : string.Empty;
    }
}
