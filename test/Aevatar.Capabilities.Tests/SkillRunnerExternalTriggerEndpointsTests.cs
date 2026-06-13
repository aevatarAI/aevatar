using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.GAgents.Scheduled;
using Aevatar.Mainnet.Host.Api.Scheduled;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Capabilities.Tests;

public sealed class SkillRunnerExternalTriggerEndpointsTests
{
    [Fact]
    public async Task PostDelivery_WithoutAdmissionAuth_ShouldReturnUnauthorizedAndNotDispatch()
    {
        var port = new RecordingSkillRunnerCommandPort();
        await using var app = await CreateAppAsync(port);

        var response = await app.GetTestClient().PostAsync(
            DeliveryPath("runner-1", "webhook-main"),
            JsonContent("""{"payloadSummary":"ignored"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        port.Admissions.Should().BeEmpty();
    }

    [Fact]
    public async Task PostDelivery_WithUnvalidatedBearer_ShouldReturnUnauthorizedAndNotDispatch()
    {
        // A non-empty bearer is no longer sufficient — the value must resolve to a real
        // NyxID user. The stub resolver returns null for any token other than the known one.
        var port = new RecordingSkillRunnerCommandPort();
        await using var app = await CreateAppAsync(port);
        using var request = new HttpRequestMessage(HttpMethod.Post, DeliveryPath("runner-1", "webhook-main"))
        {
            Content = JsonContent("""{"payloadSummary":"ignored"}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");
        request.Headers.Add("X-Aevatar-Event-Id", "event-1");

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        port.Admissions.Should().BeEmpty();
    }

    [Fact]
    public async Task PostDelivery_WithoutStableEventId_ShouldReturnBadRequestAndNotDispatch()
    {
        var port = new RecordingSkillRunnerCommandPort();
        await using var app = await CreateAppAsync(port);
        using var request = new HttpRequestMessage(HttpMethod.Post, DeliveryPath("runner-1", "webhook-main"))
        {
            Content = JsonContent("""{"payloadSummary":"ignored"}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "delivery-token");

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("missing_event_id");
        port.Admissions.Should().BeEmpty();
    }

    [Fact]
    public async Task PostDelivery_WhenRunnerMissing_ShouldReturnNotFound()
    {
        var port = new RecordingSkillRunnerCommandPort
        {
            MissingRunnerId = "runner-missing",
        };
        await using var app = await CreateAppAsync(port);

        var response = await app.GetTestClient().SendAsync(CreateAuthenticatedRequest(
            DeliveryPath("runner-missing", "webhook-main"),
            "event-1",
            """{"admissionId":"admission-1"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("runner_not_found");
        port.Admissions.Should().ContainSingle();
    }

    [Fact]
    public async Task PostDelivery_WithValidWebhook_ShouldReturnAcceptedOnlyReceipt()
    {
        var port = new RecordingSkillRunnerCommandPort();
        await using var app = await CreateAppAsync(port);

        var response = await app.GetTestClient().SendAsync(CreateAuthenticatedRequest(
            DeliveryPath("runner-1", "webhook-main"),
            "event-1",
            """
            {
              "admissionId": "admission-1",
              "kind": "webhook",
              "receivedAtUtc": "2026-06-06T08:00:00Z",
              "payloadSummary": "delivery summary",
              "payloadRef": "blob://payloads/event-1"
            }
            """));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        document.RootElement.GetProperty("actor_id").GetString().Should().Be("runner-1");
        document.RootElement.GetProperty("command_id").GetString().Should().Be("admission-1");
        document.RootElement.TryGetProperty("execution_result", out _).Should().BeFalse();
        document.RootElement.TryGetProperty("committed", out _).Should().BeFalse();

        var admitted = port.Admissions.Should().ContainSingle().Subject;
        admitted.AgentId.Should().Be("runner-1");
        admitted.Command.Identity.SourceId.Should().Be("webhook-main");
        admitted.Command.Identity.DeliveryId.Should().Be("event-1");
        admitted.Command.Identity.AdmissionId.Should().Be("admission-1");
        admitted.Command.Identity.Kind.Should().Be(ExternalTriggerSourceKind.Webhook);
        admitted.Command.Identity.PayloadSummary.Should().Be("delivery summary");
        admitted.Command.Identity.PayloadRef.Should().Be("blob://payloads/event-1");
    }

    [Fact]
    public async Task PostDelivery_WithUnknownOrDisabledSource_ShouldStillReturnAcceptedReceipt()
    {
        var port = new RecordingSkillRunnerCommandPort();
        await using var app = await CreateAppAsync(port);

        var response = await app.GetTestClient().SendAsync(CreateAuthenticatedRequest(
            DeliveryPath("runner-1", "unknown-source"),
            "event-unknown",
            """{"admissionId":"admission-unknown","kind":"channel_inbound"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var admitted = port.Admissions.Should().ContainSingle().Subject;
        admitted.Command.Identity.SourceId.Should().Be("unknown-source");
        admitted.Command.Identity.Kind.Should().Be(ExternalTriggerSourceKind.ChannelInbound);
    }

    [Fact]
    public void EndpointSource_ShouldNotInjectRuntimeDispatchOrKeepDeliveryCache()
    {
        var source = File.ReadAllText(GetEndpointSourcePath());

        source.Should().NotContain("IActorRuntime");
        source.Should().NotContain("IActorDispatchPort");
        source.Should().NotContain("ConcurrentDictionary");
        source.Should().NotContain("Dictionary<");
    }

    private static async Task<WebApplication> CreateAppAsync(RecordingSkillRunnerCommandPort port)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ISkillRunnerCommandPort>(port);
        builder.Services.AddSingleton<INyxIdCurrentUserResolver>(new StubCurrentUserResolver());

        var app = builder.Build();
        app.MapSkillRunnerExternalTriggerEndpoints();
        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage CreateAuthenticatedRequest(string path, string eventId, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent(json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "delivery-token");
        request.Headers.Add("X-Aevatar-Event-Id", eventId);
        return request;
    }

    private static string DeliveryPath(string runnerId, string sourceId) =>
        $"/api/skill-runners/{runnerId}/external-trigger-sources/{sourceId}/deliveries";

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static string GetEndpointSourcePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "src",
                "Aevatar.Mainnet.Host.Api",
                "Scheduled",
                "SkillRunnerExternalTriggerEndpoints.cs");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate SkillRunnerExternalTriggerEndpoints.cs.");
    }

    private sealed class StubCurrentUserResolver : INyxIdCurrentUserResolver
    {
        public Task<string?> ResolveCurrentUserIdAsync(string nyxIdAccessToken, CancellationToken ct = default) =>
            Task.FromResult<string?>(
                string.Equals(nyxIdAccessToken, "delivery-token", StringComparison.Ordinal)
                    ? "caller-user-1"
                    : null);
    }

    private sealed class RecordingSkillRunnerCommandPort : ISkillRunnerCommandPort
    {
        public string? MissingRunnerId { get; init; }
        public List<(string AgentId, AdmitSkillRunnerExternalTriggerCommand Command)> Admissions { get; } = [];

        public Task InitializeAsync(
            string agentId,
            InitializeSkillRunnerCommand command,
            bool runImmediately,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task TriggerAsync(string agentId, string reason, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DisableAsync(string agentId, string reason, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task EnableAsync(string agentId, string reason, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SkillRunnerExternalTriggerAdmissionReceipt> AdmitExternalTriggerAsync(
            string agentId,
            AdmitSkillRunnerExternalTriggerCommand command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Admissions.Add((agentId, command.Clone()));
            if (string.Equals(agentId, MissingRunnerId, StringComparison.Ordinal))
            {
                throw new SkillRunnerExternalTriggerAdmissionException(
                    SkillRunnerExternalTriggerAdmissionError.RunnerNotFound,
                    agentId);
            }

            return Task.FromResult(new SkillRunnerExternalTriggerAdmissionReceipt(
                agentId,
                command.Identity.AdmissionId,
                command.Identity.AdmissionId,
                command.Identity.AdmissionId,
                command.Identity.SourceId,
                command.Identity.DeliveryId));
        }
    }
}
