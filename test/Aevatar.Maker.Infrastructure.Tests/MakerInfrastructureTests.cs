using Aevatar.Maker.Application.Abstractions.Runs;
using Aevatar.Maker.Infrastructure.CapabilityApi;
using Aevatar.Maker.Infrastructure.DependencyInjection;
using Aevatar.Maker.Infrastructure.Runs;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Maker.Infrastructure.Tests;

public class WorkflowMakerRunExecutionPortTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldMapRequestAndResultThroughWorkflowCapability()
    {
        var startedAt = DateTimeOffset.Parse("2026-02-20T00:00:00+00:00");
        var workflowCapability = new FakeRunnableWorkflowActorCapability
        {
            NextResult = new RunnableWorkflowActorResult(
                ActorId: "actor-9",
                WorkflowName: "workflow-9",
                CommandId: "cmd-9",
                StartedAt: startedAt,
                Output: "done",
                Success: true,
                TimedOut: false,
                Error: null),
        };
        var executionPort = new WorkflowMakerRunExecutionPort(workflowCapability);
        var request = new MakerRunRequest(
            WorkflowYaml: "yaml-9",
            WorkflowName: "workflow-9",
            Input: "prompt",
            ActorId: "actor-9",
            Timeout: TimeSpan.FromSeconds(15),
            DestroyActorAfterRun: true);
        using var cts = new CancellationTokenSource();

        var result = await executionPort.ExecuteAsync(request, cts.Token);

        workflowCapability.LastRequest.Should().Be(
            new RunnableWorkflowActorRequest(
                Input: request.Input,
                WorkflowName: request.WorkflowName,
                WorkflowYaml: request.WorkflowYaml,
                ActorId: request.ActorId,
                Timeout: request.Timeout,
                DestroyActorAfterRun: request.DestroyActorAfterRun));
        workflowCapability.LastCancellationToken.Should().Be(cts.Token);
        workflowCapability.LastEmitAsync.Should().BeNull();
        result.Should().BeEquivalentTo(
            new MakerRunExecutionResult(
                new MakerRunStarted("actor-9", "workflow-9", "cmd-9", startedAt),
                Output: "done",
                Success: true,
                TimedOut: false,
                Error: null));
    }

    [Fact]
    public async Task ExecuteAsync_WhenRequestIsNull_ShouldThrowArgumentNullException()
    {
        var executionPort = new WorkflowMakerRunExecutionPort(new FakeRunnableWorkflowActorCapability());

        var act = () => executionPort.ExecuteAsync(null!, CancellationToken.None);

        var error = await act.Should().ThrowAsync<ArgumentNullException>();
        error.Which.ParamName.Should().Be("request");
    }
}

public class MakerInfrastructureServiceCollectionExtensionsTests
{
    [Fact]
    public void AddMakerInfrastructure_ShouldRegisterWorkflowExecutionAdapter()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRunnableWorkflowActorCapability>(new FakeRunnableWorkflowActorCapability());

        services.AddMakerInfrastructure(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMakerRunExecutionPort>()
            .Should()
            .BeOfType<WorkflowMakerRunExecutionPort>();
        provider.GetRequiredService<IMakerRunApplicationService>()
            .Should()
            .NotBeNull();
    }

    [Fact]
    public void AddMakerCapability_ShouldRegisterWorkflowExecutionAdapter()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRunnableWorkflowActorCapability>(new FakeRunnableWorkflowActorCapability());

        services.AddMakerCapability(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMakerRunExecutionPort>()
            .Should()
            .BeOfType<WorkflowMakerRunExecutionPort>();
    }
}

public class MakerCapabilityEndpointsTests
{
    [Fact]
    public void MapMakerCapabilityEndpoints_ShouldRegisterMakerRunsRoute()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IMakerRunApplicationService>(new FakeMakerRunApplicationService());
        var app = builder.Build();
        var routeBuilder = (IEndpointRouteBuilder)app;

        var mapped = app.MapMakerCapabilityEndpoints();
        var routes = routeBuilder.DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .ToList();

        mapped.Should().BeSameAs(app);
        routes.Should().Contain("/api/maker/runs");
    }
}

internal sealed class FakeRunnableWorkflowActorCapability : IRunnableWorkflowActorCapability
{
    public RunnableWorkflowActorRequest? LastRequest { get; private set; }
    public Func<WorkflowOutputFrame, CancellationToken, ValueTask>? LastEmitAsync { get; private set; }
    public CancellationToken LastCancellationToken { get; private set; }

    public RunnableWorkflowActorResult NextResult { get; set; } = new(
        ActorId: "actor-default",
        WorkflowName: "workflow-default",
        CommandId: "cmd-default",
        StartedAt: DateTimeOffset.UtcNow,
        Output: "",
        Success: true,
        TimedOut: false,
        Error: null);

    public Task<RunnableWorkflowActorResult> RunAsync(
        RunnableWorkflowActorRequest request,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask>? emitAsync = null,
        CancellationToken ct = default)
    {
        LastRequest = request;
        LastEmitAsync = emitAsync;
        LastCancellationToken = ct;
        return Task.FromResult(NextResult);
    }
}

internal sealed class FakeMakerRunApplicationService : IMakerRunApplicationService
{
    public Task<MakerRunExecutionResult> ExecuteAsync(MakerRunRequest request, CancellationToken ct = default)
    {
        _ = request;
        _ = ct;

        return Task.FromResult(
            new MakerRunExecutionResult(
                new MakerRunStarted("actor", "workflow", "cmd", DateTimeOffset.UtcNow),
                Output: "",
                Success: true,
                TimedOut: false,
                Error: null));
    }
}
