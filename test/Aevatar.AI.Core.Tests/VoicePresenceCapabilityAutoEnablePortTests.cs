using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.Voice;
using Aevatar.AI.Abstractions.Voice;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests;

public sealed class VoicePresenceCapabilityAutoEnablePortTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task TryAutoEnableAsync_WhenActorIdIsBlank_ShouldSkipWithoutCallingDependencies(string actorId)
    {
        var commandPort = new RecordingCommandPort();
        var probe = new RecordingActorKindProbe("nyxid.chat");
        var registry = new RecordingAgentKindRegistry(knownKind: "nyxid.chat");
        var sut = new VoicePresenceCapabilityAutoEnablePort(commandPort, probe, registry);

        var enabled = await sut.TryAutoEnableAsync(actorId, "voice_presence");

        enabled.Should().BeFalse();
        commandPort.EnableCalls.Should().Be(0);
        probe.ActorIds.Should().BeEmpty();
    }

    [Fact]
    public async Task TryAutoEnableAsync_WhenRuntimeTypeServicesAreMissing_ShouldFailClosed()
    {
        var commandPort = new RecordingCommandPort();
        var sut = new VoicePresenceCapabilityAutoEnablePort(commandPort);

        var enabled = await sut.TryAutoEnableAsync(" actor-1 ", null);

        enabled.Should().BeFalse();
        commandPort.EnableCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task TryAutoEnableAsync_WhenRuntimeKindIsUnavailable_ShouldSkip(string? runtimeKind)
    {
        var commandPort = new RecordingCommandPort();
        var probe = new RecordingActorKindProbe(runtimeKind);
        var registry = new RecordingAgentKindRegistry(knownKind: "nyxid.chat");
        var sut = new VoicePresenceCapabilityAutoEnablePort(commandPort, probe, registry);

        var enabled = await sut.TryAutoEnableAsync(" actor-1 ", null);

        enabled.Should().BeFalse();
        commandPort.EnableCalls.Should().Be(0);
        probe.ActorIds.Should().ContainSingle().Which.Should().Be("actor-1");
    }

    [Fact]
    public async Task TryAutoEnableAsync_WhenRuntimeKindIsNotRegistered_ShouldSkip()
    {
        var commandPort = new RecordingCommandPort();
        var probe = new RecordingActorKindProbe("nyxid.chat");
        var registry = new RecordingAgentKindRegistry(knownKind: "other.kind");
        var sut = new VoicePresenceCapabilityAutoEnablePort(commandPort, probe, registry);

        var enabled = await sut.TryAutoEnableAsync("actor-1", "voice_presence_openai");

        enabled.Should().BeFalse();
        commandPort.EnableCalls.Should().Be(0);
        registry.TryResolveKinds.Should().ContainSingle().Which.Should().Be("nyxid.chat");
    }

    [Fact]
    public async Task TryAutoEnableAsync_WhenKindIsRegistered_ShouldCommitEnableWithDefaultModule()
    {
        var commandPort = new RecordingCommandPort();
        var probe = new RecordingActorKindProbe("nyxid.chat");
        var registry = new RecordingAgentKindRegistry(knownKind: "nyxid.chat");
        var sut = new VoicePresenceCapabilityAutoEnablePort(commandPort, probe, registry);

        var enabled = await sut.TryAutoEnableAsync(" actor-1 ", " ");

        enabled.Should().BeTrue();
        commandPort.EnableCalls.Should().Be(1);
        commandPort.ActorIds.Should().ContainSingle().Which.Should().Be("actor-1");
        commandPort.Commands.Should().ContainSingle().Which.Should().Match<VoicePresenceEnableRequested>(command =>
            command.ModuleName == "voice_presence" &&
            command.RemoteAudioSupport == VoiceRemoteAudioSupport.Supported &&
            command.VoiceSessionDefaults != null);
    }

    [Fact]
    public async Task TryAutoEnableAsync_WhenCommandPortRejectsEnable_ShouldReturnFalse()
    {
        var commandPort = new RecordingCommandPort
        {
            Exception = new VoicePresenceCapabilityCommandException(
                VoicePresenceCapabilityCommandError.ActorNotFound,
                "missing"),
        };
        var sut = new VoicePresenceCapabilityAutoEnablePort(
            commandPort,
            new RecordingActorKindProbe("nyxid.chat"),
            new RecordingAgentKindRegistry(knownKind: "nyxid.chat"));

        var enabled = await sut.TryAutoEnableAsync("actor-1", "voice_presence_openai");

        enabled.Should().BeFalse();
        commandPort.Commands.Should().ContainSingle().Which.ModuleName.Should().Be("voice_presence_openai");
    }

    [Fact]
    public async Task TryAutoEnableAsync_WhenCancellationIsRequested_ShouldPropagateCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var sut = new VoicePresenceCapabilityAutoEnablePort(
            new RecordingCommandPort(),
            new ThrowingActorKindProbe(new OperationCanceledException(cts.Token)),
            new RecordingAgentKindRegistry(knownKind: "nyxid.chat"));

        var act = () => sut.TryAutoEnableAsync("actor-1", null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TryAutoEnableAsync_WhenUnexpectedExceptionOccurs_ShouldReturnFalse()
    {
        var sut = new VoicePresenceCapabilityAutoEnablePort(
            new RecordingCommandPort(),
            new ThrowingActorKindProbe(new InvalidOperationException("runtime unavailable")),
            new RecordingAgentKindRegistry(knownKind: "nyxid.chat"));

        var enabled = await sut.TryAutoEnableAsync("actor-1", null);

        enabled.Should().BeFalse();
    }

    private sealed class RecordingCommandPort : IVoicePresenceCapabilityCommandPort
    {
        public Exception? Exception { get; init; }

        public int EnableCalls { get; private set; }

        public List<string> ActorIds { get; } = [];

        public List<VoicePresenceEnableRequested> Commands { get; } = [];

        public Task<VoicePresenceCapabilityAcceptedReceipt> EnableAsync(
            string actorId,
            VoicePresenceEnableRequested command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            EnableCalls++;
            ActorIds.Add(actorId);
            Commands.Add(command.Clone());
            if (Exception != null)
                return Task.FromException<VoicePresenceCapabilityAcceptedReceipt>(Exception);

            return Task.FromResult(new VoicePresenceCapabilityAcceptedReceipt(
                ActorId: actorId,
                ModuleName: command.ModuleName,
                CommandId: "command-1",
                CorrelationId: "correlation-1",
                Stage: "accepted"));
        }
    }

    private sealed class RecordingActorKindProbe(string? runtimeKind) : IActorKindProbe
    {
        public List<string> ActorIds { get; } = [];

        public Task<string?> GetRuntimeAgentKindAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ActorIds.Add(actorId);
            return Task.FromResult(runtimeKind);
        }
    }

    private sealed class ThrowingActorKindProbe(Exception exception) : IActorKindProbe
    {
        public Task<string?> GetRuntimeAgentKindAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            return Task.FromException<string?>(exception);
        }
    }

    private sealed class RecordingAgentKindRegistry(string knownKind) : IAgentKindRegistry
    {
        public List<string> TryResolveKinds { get; } = [];

        public AgentImplementation Resolve(string kind)
        {
            if (!TryResolve(kind, out var implementation))
                throw new UnknownAgentKindException(kind);

            return implementation;
        }

        public bool TryResolve(string kind, out AgentImplementation implementation)
        {
            TryResolveKinds.Add(kind);
            if (string.Equals(kind, knownKind, StringComparison.Ordinal))
            {
                implementation = CreateImplementation();
                return true;
            }

            implementation = null!;
            return false;
        }

        public bool TryGetKindForAgentType(Type agentType, out string kind)
        {
            _ = agentType;
            kind = string.Empty;
            return false;
        }

        public bool TryGetKind(AgentImplementation implementation, out string kind)
        {
            _ = implementation;
            kind = knownKind;
            return true;
        }

        private static AgentImplementation CreateImplementation() =>
            new(
                static _ => throw new InvalidOperationException("Factory is not used by these tests."),
                typeof(TestAgentState),
                new AgentImplementationMetadata("nyxid.chat", "TestAgent"));
    }

    private sealed class TestAgentState
    {
    }
}
