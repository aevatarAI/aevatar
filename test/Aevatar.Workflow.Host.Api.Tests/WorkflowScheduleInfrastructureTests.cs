using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Application.Schedules;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.Schedules;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowScheduleInfrastructureTests
{
    [Fact]
    public void MapWorkflowCapabilityEndpoints_ShouldRegisterScheduleRoutes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        using var app = builder.Build();

        app.MapGroup("/api").MapWorkflowScheduleEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .Where(x => x != null)
            .ToHashSet(StringComparer.Ordinal);

        routes.Should().Contain("/api/workflow-schedules/");
        routes.Should().Contain("/api/workflow-schedules/{scheduleId}");
        routes.Should().Contain("/api/workflow-schedules/{scheduleId}:enable");
        routes.Should().Contain("/api/workflow-schedules/{scheduleId}:disable");
        routes.Should().Contain("/api/workflow-schedules/{scheduleId}:run-now");
        routes.Should().Contain("/api/workflow-schedules/preview");
    }

    [Fact]
    public void AddWorkflowCapabilityServices_ShouldRegisterScheduleStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkflowSchedules:Store:StorePath"] = "/tmp/aevatar-workflow-schedules.pb",
            })
            .Build();

        services.AddWorkflowCapability(configuration);

        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowScheduleStore) &&
            x.ImplementationType == typeof(FileWorkflowScheduleStore));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IWorkflowScheduleApplicationService) &&
            x.ImplementationType == typeof(WorkflowScheduleApplicationService));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IHostedService) &&
            x.ImplementationType == typeof(WorkflowScheduleDispatcherHostedService));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<WorkflowScheduleStoreOptions>>().Value.StorePath
            .Should().Be("/tmp/aevatar-workflow-schedules.pb");
    }

    [Fact]
    public async Task FileWorkflowScheduleStore_ShouldRoundTripDefinitionsAndRuns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"workflow-schedules-{Guid.NewGuid():N}.pb");
        try
        {
            var store = new FileWorkflowScheduleStore(
                Options.Create(new WorkflowScheduleStoreOptions { StorePath = path }));
            var definition = new WorkflowScheduleDefinition(
                "schedule-1",
                "Schedule One",
                "0 9 * * *",
                "UTC",
                WorkflowScheduleStatus.Enabled,
                new WorkflowScheduleTarget(
                    "hello",
                    WorkflowChatSource.CatalogWorkflow("direct"),
                    Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["source"] = "test",
                    },
                    Auth: new WorkflowScheduleAuth(new WorkflowScheduleNyxIdCredentialSource(
                        new WorkflowScheduleNyxIdSubjectRef("lark", "tenant-1", "user-1"),
                        "urn:nyxid:scope:proxy"))),
                DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"),
                DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"),
                DateTimeOffset.Parse("2026-01-01T09:00:00+00:00"));
            var run = new WorkflowScheduleRunRecord(
                "run-1",
                "schedule-1",
                DateTimeOffset.Parse("2026-01-01T09:00:00+00:00"),
                DateTimeOffset.Parse("2026-01-01T09:00:01+00:00"),
                "schedule:schedule-1:fire:2026-01-01T09:00:00.0000000+00:00",
                WorkflowScheduleFireStatus.Accepted,
                "cmd-1",
                "corr-1",
                "actor-1");

            await store.AddAsync(definition);
            await store.AddRunAsync(run);

            var reloaded = new FileWorkflowScheduleStore(
                Options.Create(new WorkflowScheduleStoreOptions { StorePath = path }));
            var storedDefinition = await reloaded.GetAsync("schedule-1");
            var storedRun = await reloaded.GetRunAsync(run.IdempotencyKey);

            storedDefinition.Should().BeEquivalentTo(definition, options => options
                .Excluding(x => x.Path.EndsWith(".Headers", StringComparison.Ordinal)));
            storedDefinition!.Target.Headers.Should().BeEmpty();
            storedRun.Should().BeEquivalentTo(run);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
