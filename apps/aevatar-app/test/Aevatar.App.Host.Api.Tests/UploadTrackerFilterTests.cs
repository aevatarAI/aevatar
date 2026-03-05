using Aevatar.App.Application.Concurrency;
using Aevatar.App.GAgents;
using Aevatar.App.Host.Api.Filters;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.App.Host.Api.Tests;

public sealed class UploadTrackerFilterTests
{
    private static EndpointFilterInvocationContext CreateFilterContext()
        => new StubUploadFilterContext(new DefaultHttpContext());

    [Fact]
    public async Task AcquireSuccess_CallsNextAndReleases()
    {
        var stub = new StubUploadCoordinator(AcquireAttemptResult.AcquiredResult());
        var filter = new UploadTrackerFilter(stub);
        var nextCalled = false;

        var result = await filter.InvokeAsync(
            CreateFilterContext(),
            _ => { nextCalled = true; return ValueTask.FromResult<object?>("ok"); });

        nextCalled.Should().BeTrue();
        result.Should().Be("ok");
        stub.UploadReleaseCount.Should().Be(1);
    }

    [Fact]
    public async Task AcquireFails_Returns503()
    {
        var stub = new StubUploadCoordinator(AcquireAttemptResult.QueueFullResult("too many uploads"));
        var filter = new UploadTrackerFilter(stub);

        var result = await filter.InvokeAsync(
            CreateFilterContext(),
            _ => ValueTask.FromResult<object?>("should not reach"));

        result.Should().NotBeNull();
        stub.UploadReleaseCount.Should().Be(0);
    }

    [Fact]
    public async Task NextThrows_StillReleases()
    {
        var stub = new StubUploadCoordinator(AcquireAttemptResult.AcquiredResult());
        var filter = new UploadTrackerFilter(stub);

        Func<Task> act = async () => await filter.InvokeAsync(
            CreateFilterContext(),
            _ => throw new InvalidOperationException("boom"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        stub.UploadReleaseCount.Should().Be(1);
    }
}

file sealed class StubUploadCoordinator : IImageConcurrencyCoordinator
{
    private readonly AcquireAttemptResult _uploadResult;
    public int UploadReleaseCount { get; private set; }

    public StubUploadCoordinator(AcquireAttemptResult uploadResult)
        => _uploadResult = uploadResult;

    public Task<AcquireAttemptResult> TryAcquireGenerateAsync(CancellationToken ct = default)
        => Task.FromResult(AcquireAttemptResult.AcquiredResult());

    public Task ReleaseGenerateAsync() => Task.CompletedTask;

    public Task<AcquireAttemptResult> TryAcquireUploadAsync(CancellationToken ct = default)
        => Task.FromResult(_uploadResult);

    public Task ReleaseUploadAsync() { UploadReleaseCount++; return Task.CompletedTask; }

    public Task<ConcurrencyStateResult> GetStatsAsync()
        => Task.FromResult(new ConcurrencyStateResult(0, 0, 0, 20, 20));
}

file sealed class StubUploadFilterContext : EndpointFilterInvocationContext
{
    public override HttpContext HttpContext { get; }
    public override IList<object?> Arguments => [];
    public override T GetArgument<T>(int index) => throw new NotSupportedException();

    public StubUploadFilterContext(HttpContext context) => HttpContext = context;
}
