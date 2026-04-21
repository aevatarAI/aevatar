using System.Diagnostics;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Aevatar.GAgents.Channel.Protocol.Tests;

public sealed class ChannelTracingSmokeTests
{
    [Fact]
    public async Task TracingMiddleware_EmitsPipelineInvokeSpanWithMandatoryDimensions()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ChannelDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var pipeline = new MiddlewarePipelineBuilder()
            .Use(new TracingMiddleware())
            .Build(new ServiceCollection().BuildServiceProvider());

        await pipeline.InvokeAsync(
            new MiddlewarePipelineTests.StubTurnContext(),
            () => Task.CompletedTask,
            CancellationToken.None);

        spans.ShouldHaveSingleItem();
        var span = spans[0];
        span.OperationName.ShouldBe(ChannelDiagnostics.Spans.PipelineInvoke);
        span.Status.ShouldBe(ActivityStatusCode.Ok);

        var tags = span.TagObjects.ToDictionary(pair => pair.Key, pair => pair.Value);
        tags.ShouldContainKey(ChannelDiagnostics.Tags.ActivityId);
        tags[ChannelDiagnostics.Tags.ActivityId].ShouldBe("act-1");
        tags.ShouldContainKey(ChannelDiagnostics.Tags.CanonicalKey);
        tags[ChannelDiagnostics.Tags.CanonicalKey].ShouldBe("slack:team:channel");
        tags.ShouldContainKey(ChannelDiagnostics.Tags.BotInstanceId);
        tags[ChannelDiagnostics.Tags.BotInstanceId].ShouldBe("ops-bot");
        tags.ShouldContainKey(ChannelDiagnostics.Tags.ChannelId);
        tags[ChannelDiagnostics.Tags.ChannelId].ShouldBe("slack");
        tags.ShouldContainKey(ChannelDiagnostics.Tags.RetryCount);
        tags[ChannelDiagnostics.Tags.RetryCount].ShouldBe(TracingMiddleware.DefaultRetryCount);
        tags.ShouldContainKey(ChannelDiagnostics.Tags.AuthPrincipal);
        tags[ChannelDiagnostics.Tags.AuthPrincipal].ShouldBe("bot:reg-1");
    }

    [Fact]
    public async Task TracingMiddleware_WhenDownstreamThrows_MarksSpanErrorAndRethrows()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ChannelDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var pipeline = new MiddlewarePipelineBuilder()
            .Use(new TracingMiddleware())
            .Use(new ThrowingMiddleware())
            .Build(new ServiceCollection().BuildServiceProvider());

        await Should.ThrowAsync<InvalidOperationException>(
            () => pipeline.InvokeAsync(
                new MiddlewarePipelineTests.StubTurnContext(),
                () => Task.CompletedTask,
                CancellationToken.None));

        spans.ShouldHaveSingleItem();
        spans[0].Status.ShouldBe(ActivityStatusCode.Error);
    }

    private sealed class ThrowingMiddleware : IChannelMiddleware
    {
        public Task InvokeAsync(ITurnContext context, Func<Task> next, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }
}
