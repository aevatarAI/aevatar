using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Voice;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.Voice;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdVoiceAgentCommandServiceTests
{
    [Fact]
    public async Task ProvisionAsync_ShouldCreateVoiceRoleRegisterOwnershipAndEnableCapability()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations);
        var registry = new RecordingRegistryPort(operations);
        var admission = new RecordingAdmissionPort();
        var voice = new RecordingVoiceCommandPort(operations);
        var service = CreateService(runtime, registry, admission, voice);

        var result = await service.ProvisionAsync(new NyxIdVoiceAgentProvisionCommand(
            "scope-alpha",
            NyxIdVoiceServiceDefaults.OpenAIRealtimeModuleName));

        result.Status.Should().Be(NyxIdVoiceAgentProvisionStatus.Accepted);
        result.ActorId.Should().StartWith(NyxIdVoiceServiceDefaults.ActorIdPrefix + "-");
        result.ModuleName.Should().Be(NyxIdVoiceServiceDefaults.OpenAIRealtimeModuleName);
        runtime.CreateCalls.Should().ContainSingle().Which.Should().Be((typeof(NyxIdVoiceGAgent), result.ActorId));
        typeof(NyxIdVoiceGAgent).Should().BeDerivedFrom<Aevatar.AI.Core.RoleGAgent>();
        registry.Registered.Should().ContainSingle().Which.Should().Be(new GAgentActorRegistration(
            "scope-alpha",
            NyxIdVoiceServiceDefaults.GAgentKind,
            result.ActorId));
        registry.Registered[0].AgentKind.Should().NotBe(NyxIdChatServiceDefaults.GAgentKind);
        voice.Calls.Should().ContainSingle();
        voice.Calls[0].ActorId.Should().Be(result.ActorId);
        voice.Calls[0].Command.ModuleName.Should().Be(NyxIdVoiceServiceDefaults.OpenAIRealtimeModuleName);
        voice.Calls[0].Command.RemoteAudioSupport.Should().Be(VoiceRemoteAudioSupport.Supported);
        operations.Should().ContainInOrder(
            $"runtime:create:{result.ActorId}",
            $"registry:register:{result.ActorId}",
            $"voice:enable:{result.ActorId}");
    }

    [Fact]
    public async Task ProvisionAsync_WhenVoiceDispatchFails_ShouldUnregisterBeforeDestroyingActor()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations);
        var registry = new RecordingRegistryPort(operations);
        var admission = new RecordingAdmissionPort();
        var voice = new RecordingVoiceCommandPort(operations)
        {
            Exception = new InvalidOperationException("dispatch failed"),
        };
        var service = CreateService(runtime, registry, admission, voice);

        var result = await service.ProvisionAsync(new NyxIdVoiceAgentProvisionCommand(
            "scope-alpha",
            NyxIdVoiceServiceDefaults.OpenAIRealtimeModuleName));

        result.Status.Should().Be(NyxIdVoiceAgentProvisionStatus.Failed);
        registry.Unregistered.Should().ContainSingle().Which.ActorId.Should().Be(result.ActorId);
        runtime.Destroyed.Should().ContainSingle().Which.Should().Be(result.ActorId);
        operations.Should().ContainInOrder(
            $"voice:enable:{result.ActorId}",
            $"registry:unregister:{result.ActorId}",
            $"runtime:destroy:{result.ActorId}");
    }

    [Fact]
    public async Task DeleteAsync_ShouldAuthorizeExactVoiceKindBeforeUnregisterAndDestroy()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations);
        var registry = new RecordingRegistryPort(operations);
        var admission = new RecordingAdmissionPort();
        var service = CreateService(
            runtime,
            registry,
            admission,
            new RecordingVoiceCommandPort(operations));

        var result = await service.DeleteAsync(new NyxIdVoiceAgentDeleteCommand(
            "scope-alpha",
            "nyxid-voice-alpha"));

        result.Status.Should().Be(NyxIdVoiceAgentDeleteStatus.Deleted);
        admission.Targets.Should().ContainSingle().Which.Should().Be(new ScopeResourceTarget(
            "scope-alpha",
            ScopeResourceKind.GAgentActor,
            NyxIdVoiceServiceDefaults.GAgentKind,
            "nyxid-voice-alpha",
            ScopeResourceOperation.Delete));
        registry.Unregistered.Should().ContainSingle().Which.AgentKind.Should().Be(NyxIdVoiceServiceDefaults.GAgentKind);
        runtime.Destroyed.Should().ContainSingle().Which.Should().Be("nyxid-voice-alpha");
    }

    private static NyxIdVoiceAgentCommandService CreateService(
        IActorRuntime runtime,
        IGAgentActorRegistryCommandPort registry,
        IScopeResourceAdmissionPort admission,
        IVoicePresenceCapabilityCommandPort voice) =>
        new(runtime, registry, admission, voice, NullLogger<NyxIdVoiceAgentCommandService>.Instance);

    private sealed class RecordingActorRuntime(List<string> operations) : IActorRuntime
    {
        public List<(Type Type, string Id)> CreateCalls { get; } = [];
        public List<string> Destroyed { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? throw new InvalidOperationException("actor id is required");
            operations.Add($"runtime:create:{actorId}");
            CreateCalls.Add((agentType, actorId));
            return Task.FromResult<IActor>(new RecordingActor(actorId));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            operations.Add($"runtime:destroy:{id}");
            Destroyed.Add(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);
        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingRegistryPort(List<string> operations) : IGAgentActorRegistryCommandPort
    {
        public List<GAgentActorRegistration> Registered { get; } = [];
        public List<GAgentActorRegistration> Unregistered { get; } = [];

        public Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            operations.Add($"registry:register:{registration.ActorId}");
            Registered.Add(registration);
            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionVisible));
        }

        public Task<GAgentActorRegistryCommandReceipt> UnregisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            operations.Add($"registry:unregister:{registration.ActorId}");
            Unregistered.Add(registration);
            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionRemoved));
        }
    }

    private sealed class RecordingAdmissionPort : IScopeResourceAdmissionPort
    {
        public List<ScopeResourceTarget> Targets { get; } = [];

        public Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
            ScopeResourceTarget target,
            CancellationToken cancellationToken = default)
        {
            Targets.Add(target);
            return Task.FromResult(ScopeResourceAdmissionResult.Allowed());
        }
    }

    private sealed class RecordingVoiceCommandPort(List<string> operations)
        : IVoicePresenceCapabilityCommandPort
    {
        public List<(string ActorId, VoicePresenceEnableRequested Command)> Calls { get; } = [];
        public Exception? Exception { get; init; }

        public Task<VoicePresenceCapabilityAcceptedReceipt> EnableAsync(
            string actorId,
            VoicePresenceEnableRequested command,
            CancellationToken ct = default)
        {
            operations.Add($"voice:enable:{actorId}");
            Calls.Add((actorId, command.Clone()));
            if (Exception is not null)
                throw Exception;
            return Task.FromResult(new VoicePresenceCapabilityAcceptedReceipt(
                actorId,
                command.ModuleName,
                "cmd-voice",
                "corr-voice",
                "accepted_for_dispatch"));
        }
    }

    private sealed class RecordingActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new RecordingAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingAgent : IAgent
    {
        public string Id => "recording";
        public Task<string> GetDescriptionAsync() => Task.FromResult("recording");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}
