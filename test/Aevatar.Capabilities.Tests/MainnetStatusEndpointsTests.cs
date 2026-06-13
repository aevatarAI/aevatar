using System.Net;
using System.Text.Json;
using Aevatar.GAgents.StatusDashboard;
using Aevatar.Mainnet.Host.Api.Status;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetStatusEndpointsTests
{
    [Fact]
    public async Task GetStatusJson_ShouldIncludeTwoHourHistoryAndAvailability()
    {
        var lastCheckAt = DateTimeOffset.Parse("2026-05-21T10:01:00+00:00");
        var document = new HealthProbeTargetDocument
        {
            Id = "responses-api-auth-gate",
            Slug = "responses-api-auth-gate",
            DisplayName = "Responses API auth gate",
            Category = "feature",
            ProbeKind = "http_status",
            IntervalSeconds = 60,
            Enabled = true,
            Status = HealthOutcomeStatus.Down,
            LatencyMs = 31,
            Detail = "http_500",
            ErrorMessage = "upstream failed",
            LastCheckAt = Timestamp.FromDateTimeOffset(lastCheckAt),
            LastSuccessAt = Timestamp.FromDateTimeOffset(lastCheckAt.AddMinutes(-1)),
        };
        document.RecentOutcomes.Add(new HealthProbeOutcome
        {
            Status = HealthOutcomeStatus.Ok,
            LatencyMs = 18,
            Detail = "http_401",
            ObservedAt = Timestamp.FromDateTimeOffset(lastCheckAt.AddMinutes(-1)),
        });
        document.RecentOutcomes.Add(new HealthProbeOutcome
        {
            Status = HealthOutcomeStatus.Down,
            LatencyMs = 31,
            Detail = "http_500",
            ErrorMessage = "upstream failed",
            ObservedAt = Timestamp.FromDateTimeOffset(lastCheckAt),
        });

        await using var app = await CreateAppAsync([document]);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/status");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        root.GetProperty("overall").GetString().Should().Be("down");
        root.GetProperty("counts").GetProperty("total").GetInt32().Should().Be(1);
        root.GetProperty("counts").GetProperty("down").GetInt32().Should().Be(1);

        var target = root.GetProperty("targets")[0];
        target.GetProperty("availability_percent").GetDouble().Should().Be(50);
        target.GetProperty("interval_seconds").GetInt32().Should().Be(60);
        var history = target.GetProperty("history");
        history.GetArrayLength().Should().Be(2);
        history[0].GetProperty("status").GetString().Should().Be("ok");
        history[1].GetProperty("status").GetString().Should().Be("down");
        history[1].GetProperty("error_message").GetString().Should().Be("upstream failed");

        var html = await client.GetStringAsync("/status");
        html.Should().Contain("Aevatar Status");
        html.Should().Contain("repeat(120");
        html.Should().Contain("target-details");
        html.Should().Contain("aria-expanded");
    }

    private static async Task<WebApplication> CreateAppAsync(IReadOnlyList<HealthProbeTargetDocument> documents)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IHealthStatusQueryPort>(new InMemoryStatusQueryPort(documents));

        var app = builder.Build();
        app.MapStatusEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class InMemoryStatusQueryPort : IHealthStatusQueryPort
    {
        private readonly IReadOnlyList<HealthProbeTargetDocument> _documents;

        public InMemoryStatusQueryPort(IReadOnlyList<HealthProbeTargetDocument> documents)
        {
            _documents = documents;
        }

        public Task<IReadOnlyList<HealthProbeTargetDocument>> ListAllAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_documents);
        }

        public Task<HealthProbeTargetDocument?> GetBySlugAsync(string slug, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_documents.FirstOrDefault(d => d.Slug == slug));
        }
    }
}
