using Aevatar.GAgents.Channel.NyxIdRelay;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public class NyxApiResponseHelperTests
{
    [Fact]
    public async Task TryRollbackAsync_EmptyDeleteResponse_DoesNotReportFailure()
    {
        var logger = new RecordingLogger();

        await NyxApiResponseHelper.TryRollbackAsync(
            () => Task.FromResult(string.Empty),
            "channel_bot",
            "bot-1",
            logger);

        logger.Messages.Should().BeEmpty();
    }

    [Fact]
    public void NormalizePublicFailureReason_DoesNotTreatNonBotConflictAsBotAlreadyExists()
    {
        NyxApiResponseHelper.NormalizePublicFailureReason(
                "conversation_route_id_request_failed nyx_status=409 body=route conflict")
            .Should().Be("provisioning_failed");
    }

    [Theory]
    [InlineData("missing_nyx_channel_bot_id")]
    [InlineData("invalid_channel_bot_detail")]
    [InlineData("channel_bot_platform_mismatch")]
    [InlineData("channel_bot_not_found_or_forbidden")]
    [InlineData("channel_bot_not_adoptable")]
    public void NormalizePublicFailureReason_PreservesAdoptionCodesWithoutProviderText(string code)
    {
        NyxApiResponseHelper.NormalizePublicFailureReason(code + " provider-secret").Should().Be(code);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
