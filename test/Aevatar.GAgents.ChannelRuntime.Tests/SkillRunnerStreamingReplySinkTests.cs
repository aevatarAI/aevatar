using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class SkillRunnerStreamingReplySinkTests
{
    [Fact]
    public async Task DispatchAsync_ShouldSendThroughChannelNeutralPort()
    {
        var port = new RecordingOutboundDeliveryPort();
        using var sink = CreateSink(port);

        await sink.DispatchAsync("hello", isFinal: false, CancellationToken.None);

        port.Requests.Should().ContainSingle();
        port.Requests[0].Text.Should().Be("hello");
        port.Requests[0].Style.Should().Be(SkillRunnerOutboundDeliveryStyle.Text);
        sink.PlatformMessageId.Should().Be("platform-1");
        sink.ChunksEmitted.Should().Be(1);
    }

    [Fact]
    public async Task DispatchAsync_WithExistingPlatformMessageId_ShouldUpdateThroughChannelNeutralPort()
    {
        var port = new RecordingOutboundDeliveryPort();
        using var sink = CreateSink(port);

        await sink.DispatchAsync("hello", isFinal: false, CancellationToken.None);
        await sink.DispatchAsync("hello world", isFinal: true, CancellationToken.None);

        port.Requests.Should().ContainSingle();
        port.UpdateRequests.Should().ContainSingle();
        port.UpdateRequests[0].Request.Text.Should().Be("hello world");
        port.UpdateRequests[0].PlatformMessageId.Should().Be("platform-1");
        port.UpdateRequests[0].IsFinal.Should().BeTrue();
        sink.PlatformMessageId.Should().Be("platform-update-1");
        sink.ChunksEmitted.Should().Be(2);
    }

    [Fact]
    public async Task DispatchAsync_WhenMidStreamSendThrows_ShouldRetryOnLaterSnapshot()
    {
        var port = new RecordingOutboundDeliveryPort { FailNext = true };
        using var sink = CreateSink(port);

        await sink.DispatchAsync("partial", isFinal: false, CancellationToken.None);
        await sink.DispatchAsync("complete", isFinal: true, CancellationToken.None);

        port.Requests.Should().HaveCount(2);
        port.Requests[0].Text.Should().Be("partial");
        port.Requests[1].Text.Should().Be("complete");
        sink.PlatformMessageId.Should().Be("platform-2");
        sink.ChunksEmitted.Should().Be(1);
    }

    [Fact]
    public async Task DispatchAsync_WhenFinalSendThrows_ShouldPropagate()
    {
        var port = new RecordingOutboundDeliveryPort { FailNext = true };
        using var sink = CreateSink(port);

        Func<Task> act = () => sink.DispatchAsync("final", isFinal: true, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*send rejected*");
    }

    [Fact]
    public void TruncateForLark_ShouldCapPayloadWithMarker()
    {
        var oversized = new string('A', SkillRunnerStreamingReplySink.MaxLarkTextLength + 5_000);

        var sent = SkillRunnerStreamingReplySink.TruncateForLark(oversized);

        sent.Length.Should().Be(SkillRunnerStreamingReplySink.MaxLarkTextLength);
        sent.Should().EndWith("...[truncated]");
    }

    private static SkillRunnerStreamingReplySink CreateSink(RecordingOutboundDeliveryPort port) =>
        new(
            port,
            new SkillRunnerOutboundDeliveryRequest(
                "agent-1",
                new SkillRunnerOutboundConfig
                {
                    ConversationId = "conversation-1",
                    NyxProviderSlug = "provider-1",
                    NyxApiKey = "nyx-api-key",
                },
                Text: string.Empty,
                SkillRunnerOutboundDeliveryStyle.Text),
            logger: null);

    private sealed class RecordingOutboundDeliveryPort : ISkillRunnerOutboundDeliveryPort
    {
        public List<SkillRunnerOutboundDeliveryRequest> Requests { get; } = [];
        public List<(SkillRunnerOutboundDeliveryRequest Request, string PlatformMessageId, bool IsFinal)> UpdateRequests { get; } = [];

        public bool FailNext { get; set; }

        public Task<SkillRunnerOutboundDeliveryReceipt> SendAsync(
            SkillRunnerOutboundDeliveryRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("send rejected");
            }

            return Task.FromResult(new SkillRunnerOutboundDeliveryReceipt(
                $"sent-{Requests.Count}",
                $"platform-{Requests.Count}",
                ComposeCapability.Exact));
        }

        public Task<SkillRunnerOutboundDeliveryReceipt> UpdateAsync(
            SkillRunnerOutboundDeliveryRequest request,
            string platformMessageId,
            bool isFinal,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            UpdateRequests.Add((request, platformMessageId, isFinal));
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("send rejected");
            }

            return Task.FromResult(new SkillRunnerOutboundDeliveryReceipt(
                $"sent-update-{UpdateRequests.Count}",
                $"platform-update-{UpdateRequests.Count}",
                ComposeCapability.Exact));
        }
    }
}
