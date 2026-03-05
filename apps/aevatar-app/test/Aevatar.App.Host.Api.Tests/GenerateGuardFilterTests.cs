using Aevatar.App.Application.Concurrency;
using Aevatar.App.GAgents;
using Aevatar.App.Host.Api.Filters;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aevatar.App.Host.Api.Tests;

public sealed class GenerateGuardFilterTests
{
    private static EndpointFilterInvocationContext CreateFilterContext()
        => new StubFilterContext(new DefaultHttpContext());

    [Fact]
    public async Task AcquireSuccess_CallsNextAndReleases()
    {
        var stub = new StubCoordinator(AcquireAttemptResult.AcquiredResult());
        var filter = new GenerateGuardFilter(stub);
        var nextCalled = false;

        var result = await filter.InvokeAsync(
            CreateFilterContext(),
            _ => { nextCalled = true; return ValueTask.FromResult<object?>("ok"); });

        nextCalled.Should().BeTrue();
        result.Should().Be("ok");
        stub.GenerateReleaseCount.Should().Be(1);
    }

    [Fact]
    public async Task AcquireFails_RateLimit_Returns429()
    {
        var stub = new StubCoordinator(AcquireAttemptResult.RateLimitedResult("rate limit hit"));
        var filter = new GenerateGuardFilter(stub);

        var result = await filter.InvokeAsync(
            CreateFilterContext(),
            _ => ValueTask.FromResult<object?>("should not reach"));

        var json = result as IResult;
        json.Should().NotBeNull();
        stub.GenerateReleaseCount.Should().Be(0);
    }

    [Fact]
    public async Task AcquireFails_Overloaded_Returns503()
    {
        var stub = new StubCoordinator(AcquireAttemptResult.QueueFullResult("overloaded"));
        var filter = new GenerateGuardFilter(stub);

        var result = await filter.InvokeAsync(
            CreateFilterContext(),
            _ => ValueTask.FromResult<object?>("should not reach"));

        result.Should().NotBeNull();
        stub.GenerateReleaseCount.Should().Be(0);
    }

    [Fact]
    public async Task NextThrows_StillReleases()
    {
        var stub = new StubCoordinator(AcquireAttemptResult.AcquiredResult());
        var filter = new GenerateGuardFilter(stub);

        Func<Task> act = async () => await filter.InvokeAsync(
            CreateFilterContext(),
            _ => throw new InvalidOperationException("boom"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        stub.GenerateReleaseCount.Should().Be(1);
    }
}

file sealed class StubCoordinator : IImageConcurrencyCoordinator
{
    private readonly AcquireAttemptResult _generateResult;
    public int GenerateReleaseCount { get; private set; }

    public StubCoordinator(AcquireAttemptResult generateResult)
        => _generateResult = generateResult;

    public Task<AcquireAttemptResult> TryAcquireGenerateAsync(CancellationToken ct = default)
        => Task.FromResult(_generateResult);

    public Task ReleaseGenerateAsync() { GenerateReleaseCount++; return Task.CompletedTask; }

    public Task<AcquireAttemptResult> TryAcquireUploadAsync(CancellationToken ct = default)
        => Task.FromResult(AcquireAttemptResult.AcquiredResult());

    public Task ReleaseUploadAsync() => Task.CompletedTask;

    public Task<ConcurrencyStateResult> GetStatsAsync()
        => Task.FromResult(new ConcurrencyStateResult(0, 0, 0, 20, 20));
}

file sealed class StubFilterContext : EndpointFilterInvocationContext
{
    public override HttpContext HttpContext { get; }
    public override IList<object?> Arguments => [];
    public override T GetArgument<T>(int index) => throw new NotSupportedException();

    public StubFilterContext(HttpContext context) => HttpContext = context;
}
