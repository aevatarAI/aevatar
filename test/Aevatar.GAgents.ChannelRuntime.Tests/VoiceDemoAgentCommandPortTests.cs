using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Direct coverage for the voice demo command port used by Mainnet bootstrap.
/// </summary>
// Refactor (iter34/cluster-005-mainnet-host-direct-actor-runtime):
//   Old pattern: Voice bootstrap endpoint built InitializeRoleAgentEvent envelopes with runtime dependencies in Host.
//   New principle: command-port tests own initialization envelope assertions; Host endpoint tests only verify admission into the port.
public sealed class VoiceDemoAgentCommandPortTests
{
    [Fact]
    public async Task EnsureAsync_DispatchesInitializationEnvelopeAndReturnsAcceptedReceipt()
    {
        var actorRuntime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        using var provider = CreateProvider(actorRuntime, dispatchPort);
        var commandPort = provider.GetRequiredService<IVoiceDemoAgentCommandPort>();
        var expectedActorId = BuildExpectedDemoActorId("scope-1");

        var receipt = await commandPort.EnsureAsync(" scope-1 ", " voice_presence_openai ");

        actorRuntime.CreatedActors.Should().ContainSingle()
            .Which.Should().Be((expectedActorId, typeof(NyxIdChatGAgent)));
        dispatchPort.Dispatches.Should().ContainSingle();
        var (actorId, envelope) = dispatchPort.Dispatches[0];
        actorId.Should().Be(expectedActorId);
        envelope.Id.Should().NotBeNullOrWhiteSpace();
        envelope.Route.PublisherActorId.Should().Be("voice-demo-bootstrap");
        envelope.Route.Direct.TargetActorId.Should().Be(expectedActorId);
        envelope.Propagation.CorrelationId.Should().Be(envelope.Id);
        envelope.Runtime.Deduplication.OperationId.Should().Be(envelope.Id);

        var initialize = envelope.Payload.Unpack<InitializeRoleAgentEvent>();
        initialize.RoleId.Should().Be("voice-demo");
        initialize.RoleName.Should().Be("Voice Demo Agent");
        initialize.ProviderName.Should().Be(NyxIdChatServiceDefaults.ProviderName);
        initialize.SystemPrompt.Should().Contain("Aevatar voice demo agent");
        initialize.MaxHistoryMessages.Should().Be(16);
        initialize.EventModules.Should().Be("voice_presence_openai");
        receipt.Should().Be(new VoiceDemoAgentCommandAcceptedReceipt(
            expectedActorId,
            envelope.Id,
            envelope.Id));
    }

    [Theory]
    [InlineData(null, "voice_presence_openai", "scopeId")]
    [InlineData("", "voice_presence_openai", "scopeId")]
    [InlineData("   ", "voice_presence_openai", "scopeId")]
    [InlineData("scope-1", null, "voiceModuleName")]
    [InlineData("scope-1", "", "voiceModuleName")]
    [InlineData("scope-1", "   ", "voiceModuleName")]
    public async Task EnsureAsync_RejectsMissingCommandInputs(
        string? scopeId,
        string? voiceModuleName,
        string expectedParameterName)
    {
        using var provider = CreateProvider(new RecordingActorRuntime(), new RecordingActorDispatchPort());
        var commandPort = provider.GetRequiredService<IVoiceDemoAgentCommandPort>();

        var act = () => commandPort.EnsureAsync(scopeId!, voiceModuleName!);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName(expectedParameterName);
    }

    private static string BuildExpectedDemoActorId(string scopeId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(scopeId.Trim()));
        var hash = Convert.ToHexString(bytes)[..16].ToLowerInvariant();
        return $"{NyxIdChatServiceDefaults.ActorIdPrefix}-voice-demo-{hash}";
    }

    private static ServiceProvider CreateProvider(
        RecordingActorRuntime actorRuntime,
        RecordingActorDispatchPort dispatchPort)
    {
        return new ServiceCollection()
            .AddSingleton<IActorRuntime>(actorRuntime)
            .AddSingleton<IActorDispatchPort>(dispatchPort)
            .AddNyxIdChat()
            .BuildServiceProvider();
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public List<(string ActorId, Type AgentType)> CreatedActors { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            CreatedActors.Add((id, agentType));
            return Task.FromResult<IActor>(new StubActor(id));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(new StubActor(id));
        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
            return Task.CompletedTask;
        }
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent => throw new NotSupportedException();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
