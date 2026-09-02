using System.Reflection;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Hosting.DependencyInjection;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Hosting;

public sealed class MissingLlmProviderRunCoreTests
{
    [Fact]
    public async Task RunAsync_ShouldRecordTerminalFailure()
    {
        var core = LoadMissingProviderRunCore();
        var sink = new RecordingLlmRunSink();

        await core.RunAsync(
            new LlmRunCoreRequest(
                new LlmRunRequested
                {
                    ResponseId = "resp-missing-provider",
                    RunId = "run-missing-provider",
                },
                "run-missing-provider",
                "ApiKey"),
            sink);

        sink.Failed.Should().ContainSingle();
        var failed = sink.Failed[0];
        failed.ResponseId.Should().Be("resp-missing-provider");
        failed.RunId.Should().Be("run-missing-provider");
        failed.FailureCode.Should().Be("llm_provider_factory_missing");
        failed.FailureMessage.Should().Contain("ILLMProviderFactory is not registered");
        failed.FailedAt.Should().NotBeNull();
        sink.Completed.Should().BeEmpty();
        sink.Cancelled.Should().BeEmpty();
    }

    private static ILlmRunCore LoadMissingProviderRunCore()
    {
        var assembly = typeof(ServiceCollectionExtensions).Assembly;
        var type = assembly.GetType("Aevatar.GAgentService.Hosting.Responses.MissingLlmProviderRunCore", throwOnError: true)!;
        typeof(ILlmRunCore).IsAssignableFrom(type).Should().BeTrue();

        var instance = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        return instance.Should().BeAssignableTo<ILlmRunCore>().Subject;
    }

    private sealed class RecordingLlmRunSink : ILlmRunSink
    {
        public List<LlmRunCompleted> Completed { get; } = [];
        public List<LlmRunFailed> Failed { get; } = [];
        public List<LlmRunCancelled> Cancelled { get; } = [];

        public Task<LlmRunRecordDecision> RecordStreamChunkObservedAsync(
            LlmStreamChunkObserved observed,
            CancellationToken ct = default) =>
            Task.FromResult(LlmRunRecordDecision.Continue);

        public Task<LlmRunRecordDecision> RecordToolCallObservedAsync(
            LlmToolCallObserved observed,
            CancellationToken ct = default) =>
            Task.FromResult(LlmRunRecordDecision.Continue);

        public Task<LlmRunRecordDecision> RecordRunCompletedAsync(
            LlmRunCompleted completed,
            CancellationToken ct = default)
        {
            Completed.Add(completed.Clone());
            return Task.FromResult(LlmRunRecordDecision.Continue);
        }

        public Task<LlmRunRecordDecision> RecordRunFailedAsync(
            LlmRunFailed failed,
            CancellationToken ct = default)
        {
            Failed.Add(failed.Clone());
            return Task.FromResult(LlmRunRecordDecision.Continue);
        }

        public Task<LlmRunRecordDecision> RecordRunCancelledAsync(
            LlmRunCancelled cancelled,
            CancellationToken ct = default)
        {
            Cancelled.Add(cancelled.Clone());
            return Task.FromResult(LlmRunRecordDecision.Continue);
        }
    }
}
