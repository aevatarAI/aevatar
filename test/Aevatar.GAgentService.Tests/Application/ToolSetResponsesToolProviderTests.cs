using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ToolSetResponsesToolProviderTests
{
    private static readonly ResponsesToolProviderContext ToolProviderContext = new(
        AgentToolExecutionContext.Empty with
        {
            Caller = new AgentToolCallerContext("scope-1", "owner-1", "response-1"),
        });

    [Fact]
    public void Constructor_WhenSourcesIsNull_ShouldThrowArgumentNullException()
    {
        ((Action)(() => new ToolSetResponsesToolProvider(null!, new RecordingLogger())))
            .Should().Throw<ArgumentNullException>()
            .WithParameterName("sources");
    }

    [Fact]
    public async Task GetAdditiveToolsAsync_ShouldAggregateToolsFromAllSourcesInOrder()
    {
        var provider = new ToolSetResponsesToolProvider(
            [
                new StaticToolSource([new StaticTool("nyxid_services")]),
                new StaticToolSource([new StaticTool("invoke_service"), new StaticTool("use_skill")]),
            ],
            new RecordingLogger());

        var tools = await provider.GetAdditiveToolsAsync(ToolProviderContext);

        tools.Select(static tool => tool.Name)
            .Should().Equal("invoke_service", "nyxid_services", "use_skill");
    }

    [Fact]
    public async Task GetAdditiveToolsAsync_WhenNoSources_ShouldReturnEmptyList()
    {
        var provider = new ToolSetResponsesToolProvider([], new RecordingLogger());

        var tools = await provider.GetAdditiveToolsAsync(ToolProviderContext);

        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAdditiveToolsAsync_ShouldPublishRequestToolContextToSourcesDuringDiscovery()
    {
        var capturingSource = new ContextCapturingToolSource();
        var provider = new ToolSetResponsesToolProvider([capturingSource], new RecordingLogger());

        // No ambient context before the call; the provider must push the request context.
        AgentToolRequestContext.Current.Should().BeNull();

        await provider.GetAdditiveToolsAsync(ToolProviderContext);

        capturingSource.CapturedScopeId.Should().Be("scope-1");
        capturingSource.CapturedOwnerSubject.Should().Be("owner-1");
        capturingSource.CapturedResponseId.Should().Be("response-1");

        // Context is restored (popped) after discovery completes.
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task GetAdditiveToolsAsync_WhenSourceDiscoveryFails_ShouldFailClosedAndLogTypedReason()
    {
        var logger = new RecordingLogger();
        var provider = new ToolSetResponsesToolProvider(
            [
                new FaultingToolSource(),
                new StaticToolSource([new StaticTool("use_skill")]),
            ],
            logger);

        var act = () => provider.GetAdditiveToolsAsync(ToolProviderContext).AsTask();

        var exception = await act.Should().ThrowAsync<AgentToolDiscoveryException>();
        exception.Which.Failure.Code.Should().Be(AgentToolDiscoveryFailureCode.SourceFailed);
        logger.Messages.Should().Contain(message =>
            message.Contains("route tool discovery failed closed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetAdditiveToolsAsync_WhenAllSourcesFail_ShouldReturnFirstTypedFailure()
    {
        var logger = new RecordingLogger();
        var provider = new ToolSetResponsesToolProvider(
            [new FaultingToolSource(), new FaultingToolSource()],
            logger);

        var act = () => provider.GetAdditiveToolsAsync(ToolProviderContext).AsTask();

        var exception = await act.Should().ThrowAsync<AgentToolDiscoveryException>();
        exception.Which.Failure.Code.Should().Be(AgentToolDiscoveryFailureCode.SourceFailed);
        logger.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task GetAdditiveToolsAsync_WhenLoggerIsNull_ShouldFailClosedWithoutNullReference()
    {
        // logger == null must not NRE when a source fails (factory passes NullLogger.Instance).
        var provider = new ToolSetResponsesToolProvider(
            [new FaultingToolSource(), new StaticToolSource([new StaticTool("use_skill")])],
            logger: null!);

        var act = () => provider.GetAdditiveToolsAsync(ToolProviderContext).AsTask();

        await act.Should().ThrowAsync<AgentToolDiscoveryException>();
    }

    [Fact]
    public async Task GetAdditiveToolsAsync_WhenSourceObservesCancellation_ShouldRethrowOperationCanceled()
    {
        // Cancellation propagation is source-cooperative: a source that observes the token and
        // throws OperationCanceledException while the token is cancelled is rethrown, not swallowed.
        var provider = new ToolSetResponsesToolProvider(
            [new CancellationObservingToolSource()],
            new RecordingLogger());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => provider.GetAdditiveToolsAsync(ToolProviderContext, cts.Token).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetAdditiveToolsAsync_WhenTokenCancelledButSourceFailsWithoutObservingIt_ShouldFailClosed()
    {
        // The provider does not pre-emptively probe the token; a source that fails with a
        // non-cancellation exception is swallowed even when the token is already cancelled.
        var logger = new RecordingLogger();
        var provider = new ToolSetResponsesToolProvider(
            [new FaultingToolSource()],
            logger);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => provider.GetAdditiveToolsAsync(ToolProviderContext, cts.Token).AsTask();

        await act.Should().ThrowAsync<AgentToolDiscoveryException>();
        logger.Messages.Should().Contain(message =>
            message.Contains("route tool discovery failed closed", StringComparison.Ordinal));
    }

    private sealed class StaticToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class FaultingToolSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<IAgentTool>>(
                new InvalidOperationException("source discovery failed"));
    }

    private sealed class CancellationObservingToolSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);
        }
    }

    private sealed class ContextCapturingToolSource : IAgentToolSource
    {
        public string? CapturedScopeId { get; private set; }

        public string? CapturedOwnerSubject { get; private set; }

        public string? CapturedResponseId { get; private set; }

        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            CapturedScopeId = AgentToolRequestContext.ScopeId;
            CapturedOwnerSubject = AgentToolRequestContext.OwnerSubject;
            CapturedResponseId = AgentToolRequestContext.ResponseId;
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);
        }
    }

    private sealed class StaticTool(string name) : IAgentTool
    {
        public string Name { get; } = name;

        public string Description => $"{Name} description";

        public string ParametersSchema => """{"type":"object"}""";

        public bool IsReadOnly => true;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
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
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
