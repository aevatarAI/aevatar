using Aevatar.Maker.Application.Abstractions.Runs;
using Aevatar.Maker.Application.DependencyInjection;
using Aevatar.Maker.Application.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Maker.Application.Tests;

public class MakerRunApplicationServiceTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldDelegateToExecutionPort()
    {
        var request = new MakerRunRequest(
            WorkflowYaml: "yaml-1",
            WorkflowName: "direct",
            Input: "hello",
            ActorId: "actor-1",
            Timeout: TimeSpan.FromSeconds(5),
            DestroyActorAfterRun: true);
        var expected = new MakerRunExecutionResult(
            new MakerRunStarted("actor-1", "direct", "cmd-1", DateTimeOffset.UtcNow),
            Output: "ok",
            Success: true,
            TimedOut: false,
            Error: null);
        var executionPort = new FakeMakerRunExecutionPort
        {
            NextResult = expected,
        };
        var service = new MakerRunApplicationService(executionPort);
        using var cts = new CancellationTokenSource();

        var actual = await service.ExecuteAsync(request, cts.Token);

        actual.Should().Be(expected);
        executionPort.Calls.Should().ContainSingle();
        executionPort.Calls[0].Request.Should().Be(request);
        executionPort.Calls[0].CancellationToken.Should().Be(cts.Token);
    }
}

public class MakerApplicationServiceCollectionExtensionsTests
{
    [Fact]
    public void AddMakerApplication_ShouldRegisterApplicationServiceAndFallbackExecutionPort()
    {
        var services = new ServiceCollection();

        services.AddMakerApplication();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMakerRunApplicationService>()
            .Should()
            .BeOfType<MakerRunApplicationService>();
        provider.GetRequiredService<IMakerRunExecutionPort>()
            .Should()
            .NotBeNull();
    }

    [Fact]
    public async Task AddMakerApplication_WhenExecutionPortIsNotConfigured_ShouldUseFallbackPort()
    {
        var services = new ServiceCollection();
        services.AddMakerApplication();
        using var provider = services.BuildServiceProvider();
        var executionPort = provider.GetRequiredService<IMakerRunExecutionPort>();

        var act = () => executionPort.ExecuteAsync(
            new MakerRunRequest("yaml-1", "direct", "hello"),
            CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*IMakerRunExecutionPort is not configured*");
    }

    [Fact]
    public void AddMakerApplication_ShouldNotOverridePreconfiguredExecutionPort()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMakerRunExecutionPort, PreconfiguredExecutionPort>();

        services.AddMakerApplication();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMakerRunExecutionPort>()
            .Should()
            .BeOfType<PreconfiguredExecutionPort>();
    }
}

internal sealed class FakeMakerRunExecutionPort : IMakerRunExecutionPort
{
    public List<ExecutionCall> Calls { get; } = [];

    public MakerRunExecutionResult NextResult { get; set; } = new(
        new MakerRunStarted("actor-default", "workflow-default", "cmd-default", DateTimeOffset.UtcNow),
        Output: "",
        Success: true,
        TimedOut: false,
        Error: null);

    public Task<MakerRunExecutionResult> ExecuteAsync(MakerRunRequest request, CancellationToken ct = default)
    {
        Calls.Add(new ExecutionCall(request, ct));
        return Task.FromResult(NextResult);
    }

    internal sealed record ExecutionCall(MakerRunRequest Request, CancellationToken CancellationToken);
}

internal sealed class PreconfiguredExecutionPort : IMakerRunExecutionPort
{
    public Task<MakerRunExecutionResult> ExecuteAsync(MakerRunRequest request, CancellationToken ct = default)
    {
        _ = request;
        _ = ct;

        return Task.FromResult(
            new MakerRunExecutionResult(
                new MakerRunStarted("actor-preconfigured", "direct", "cmd-preconfigured", DateTimeOffset.UtcNow),
                Output: "preconfigured",
                Success: true,
                TimedOut: false,
                Error: null));
    }
}
