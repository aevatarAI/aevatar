using System.Diagnostics;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Foundation.Runtime.Observability;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Aevatar.GAgents.Channel.Protocol.Tests;

// iter85/cluster-085: verifies channel tracing stays on the canonical source and tag family.
public sealed class ChannelTracingSmokeTests
{
    [Fact]
    public void ChannelDiagnosticsTags_ShouldMatchDocumentedDottedChannelKeys()
    {
        ChannelDiagnostics.Tags.ActivityId.ShouldBe("aevatar.channel.activity_id");
        ChannelDiagnostics.Tags.ProviderEventId.ShouldBe("aevatar.channel.provider_event_id");
        ChannelDiagnostics.Tags.CanonicalKey.ShouldBe("aevatar.channel.canonical_key");
        ChannelDiagnostics.Tags.BotInstanceId.ShouldBe("aevatar.channel.bot_instance_id");
        ChannelDiagnostics.Tags.SentActivityId.ShouldBe("aevatar.channel.sent_activity_id");
        ChannelDiagnostics.Tags.RetryCount.ShouldBe("aevatar.channel.retry_count");
        ChannelDiagnostics.Tags.RawPayloadBlobRef.ShouldBe("aevatar.channel.raw_payload_blob_ref");
        ChannelDiagnostics.Tags.AuthPrincipal.ShouldBe("aevatar.channel.auth_principal");
        ChannelDiagnostics.Tags.ChannelId.ShouldBe("aevatar.channel.id");
    }

    [Fact]
    public async Task TracingMiddleware_EmitsPipelineInvokeSpanWithLiteralChannelTagFamily()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ChannelDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);
        ChannelDiagnostics.ActivitySourceName.ShouldBe(AevatarActivitySource.ActivitySourceName);

        var pipeline = new MiddlewarePipelineBuilder()
            .Use(new TracingMiddleware())
            .Use(new SentActivityTagMiddleware("sent-1"))
            .Build(new ServiceCollection().BuildServiceProvider());

        var context = new MiddlewarePipelineTests.StubTurnContext();
        context.Activity.RawPayloadBlobRef = "blob://raw/payload-1";

        await pipeline.InvokeAsync(
            context,
            () => Task.CompletedTask,
            CancellationToken.None);

        spans.ShouldHaveSingleItem();
        var span = spans[0];
        span.Source.Name.ShouldBe(AevatarActivitySource.ActivitySourceName);
        span.OperationName.ShouldBe(ChannelDiagnostics.Spans.PipelineInvoke);
        span.Status.ShouldBe(ActivityStatusCode.Ok);

        var tags = span.TagObjects.ToDictionary(pair => pair.Key, pair => pair.Value);
        tags["aevatar.channel.activity_id"].ShouldBe("act-1");
        tags["aevatar.channel.provider_event_id"].ShouldBe("blob://raw/payload-1");
        tags["aevatar.channel.canonical_key"].ShouldBe("slack:team:channel");
        tags["aevatar.channel.bot_instance_id"].ShouldBe("ops-bot");
        tags["aevatar.channel.sent_activity_id"].ShouldBe("sent-1");
        tags["aevatar.channel.retry_count"].ShouldBe(TracingMiddleware.DefaultRetryCount);
        tags["aevatar.channel.raw_payload_blob_ref"].ShouldBe("blob://raw/payload-1");
        tags["aevatar.channel.auth_principal"].ShouldBe("bot:reg-1");
        tags["aevatar.channel.id"].ShouldBe("slack");

        foreach (var legacyKey in LegacyBareChannelTagKeys)
        {
            tags.ShouldNotContainKey(legacyKey);
        }
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

    private sealed class SentActivityTagMiddleware : IChannelMiddleware
    {
        private readonly string _sentActivityId;

        public SentActivityTagMiddleware(string sentActivityId)
        {
            _sentActivityId = sentActivityId;
        }

        public Task InvokeAsync(ITurnContext context, Func<Task> next, CancellationToken ct)
        {
            Activity.Current?.SetTag(ChannelDiagnostics.Tags.SentActivityId, _sentActivityId);
            return next();
        }
    }

    private static readonly string[] LegacyBareChannelTagKeys =
    [
        "activity_id",
        "provider_event_id",
        "canonical_key",
        "bot_instance_id",
        "sent_activity_id",
        "retry_count",
        "raw_payload_blob_ref",
        "auth_principal",
        "channel_id",
        "id",
    ];
}
