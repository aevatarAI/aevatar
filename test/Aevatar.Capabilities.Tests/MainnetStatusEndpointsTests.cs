using System.Net;
using System.Text.Json;
using Aevatar.BackendConsole.Hosting;
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
        var snapshot = new HealthProbeOperationalSnapshot
        {
            Target = new HealthProbeTargetDescriptor
            {
                Slug = "responses-api-auth-gate",
                DisplayName = "Responses API auth gate",
                Category = "feature",
                Severity = "critical",
                ProbeKind = "http_status",
                IntervalSeconds = 60,
                Enabled = true,
            },
            LastOutcome = new HealthProbeOutcome
            {
                Status = HealthOutcomeStatus.Down,
                LatencyMs = 31,
                Detail = "http_500",
                ErrorMessage = "upstream failed",
                ObservedAt = Timestamp.FromDateTimeOffset(lastCheckAt),
            },
            LastCheckAt = Timestamp.FromDateTimeOffset(lastCheckAt),
            LastSuccessAt = Timestamp.FromDateTimeOffset(lastCheckAt.AddMinutes(-1)),
        };
        snapshot.RecentOutcomes.Add(new HealthProbeOutcome
        {
            Status = HealthOutcomeStatus.Ok,
            LatencyMs = 18,
            Detail = "http_401",
            ObservedAt = Timestamp.FromDateTimeOffset(lastCheckAt.AddMinutes(-1)),
        });
        snapshot.RecentOutcomes.Add(new HealthProbeOutcome
        {
            Status = HealthOutcomeStatus.Down,
            LatencyMs = 31,
            Detail = "http_500",
            ErrorMessage = "upstream failed",
            ObservedAt = Timestamp.FromDateTimeOffset(lastCheckAt),
        });

        await using var app = await CreateAppAsync([snapshot]);
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

    [Fact]
    public async Task GetStatusJson_NonCriticalDown_DegradesWithoutBlackingOut()
    {
        await using var app = await CreateAppAsync(
        [
            Doc("self-readiness", "critical", HealthOutcomeStatus.Ok),
            Doc("nyxid-http-health", "standard", HealthOutcomeStatus.Down),
            Doc("llm-completion-canary", "canary", HealthOutcomeStatus.Unknown),
        ]);

        using var json = JsonDocument.Parse(await app.GetTestClient().GetStringAsync("/api/status"));
        // A non-critical down degrades; an unconfigured canary (unknown) is excluded — so the
        // board is honest "degraded", never a false blanket "down".
        json.RootElement.GetProperty("overall").GetString().Should().Be("degraded");
    }

    [Fact]
    public async Task GetStatusJson_CriticalDown_IsDown()
    {
        await using var app = await CreateAppAsync(
        [
            Doc("self-readiness", "critical", HealthOutcomeStatus.Down),
            Doc("studio-health", "standard", HealthOutcomeStatus.Ok),
        ]);

        using var json = JsonDocument.Parse(await app.GetTestClient().GetStringAsync("/api/status"));
        json.RootElement.GetProperty("overall").GetString().Should().Be("down");
    }

    [Fact]
    public async Task GetStatusJson_UnconfiguredCanary_DoesNotForceRed()
    {
        await using var app = await CreateAppAsync(
        [
            Doc("self-readiness", "critical", HealthOutcomeStatus.Ok),
            Doc("llm-completion-canary", "canary", HealthOutcomeStatus.Unknown),
        ]);

        using var json = JsonDocument.Parse(await app.GetTestClient().GetStringAsync("/api/status"));
        json.RootElement.GetProperty("overall").GetString().Should().Be("ok");
    }

    private static HealthProbeOperationalSnapshot Doc(string slug, string severity, HealthOutcomeStatus status) =>
        new()
        {
            Target = new HealthProbeTargetDescriptor
            {
                Slug = slug,
                DisplayName = slug,
                Category = "feature",
                Severity = severity,
                ProbeKind = "http_status",
                IntervalSeconds = 60,
                Enabled = true,
            },
            LastOutcome = new HealthProbeOutcome { Status = status },
        };

    private static async Task<WebApplication> CreateAppAsync(IReadOnlyList<HealthProbeOperationalSnapshot> snapshots)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IHealthStatusQueryPort>(new InMemoryStatusQueryPort(snapshots));
        builder.Services.AddBackendConsoleStaticAssets(builder.Configuration);

        var app = builder.Build();
        app.MapStatusEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class InMemoryStatusQueryPort : IHealthStatusQueryPort
    {
        private readonly IReadOnlyList<HealthProbeOperationalSnapshot> _snapshots;

        public InMemoryStatusQueryPort(IReadOnlyList<HealthProbeOperationalSnapshot> snapshots)
        {
            _snapshots = snapshots;
        }

        public Task<IReadOnlyList<HealthProbeOperationalSnapshot>> ListAllAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_snapshots);
        }

        public Task<HealthProbeOperationalSnapshot?> GetBySlugAsync(string slug, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_snapshots.FirstOrDefault(d => d.Target.Slug == slug));
        }
    }
}
