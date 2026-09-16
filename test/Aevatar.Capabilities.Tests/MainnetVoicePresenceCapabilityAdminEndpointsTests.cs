using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Voice;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.VoicePresence;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgents.NyxidChat.Voice;
using Aevatar.Mainnet.Host.Api.Voice;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetVoicePresenceCapabilityAdminEndpointsTests
{
    private const string ScopeId = "scope-voice";
    private const string ActorId = "role-agent-voice";
    private const string AgentKind = "ai.role-agent";
    private const string ModuleName = "voice_presence";

    [Fact]
    public async Task Enable_ShouldAuthorizeUseAndReturnAcceptedEchoWithoutPuttingScopeOrKindInReceipt()
    {
        var commandPort = new RecordingVoicePresenceCommandPort();
        var admissionPort = new RecordingAdmissionPort();
        await using var app = await CreateAppAsync(commandPort, admissionPort);
        var client = app.GetTestClient();

        using var request = CreateEnableRequest("""
        {
          "module_name": "voice_presence",
          "voice_session_defaults": {
            "sample_rate_hz": 16000,
            "voice": "alloy"
          }
        }
        """);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.EnumerateObject().Select(static p => p.Name).Should().Equal(
            "scope_id",
            "actor_id",
            "agent_kind",
            "module_name",
            "command_id",
            "correlation_id",
            "stage",
            "note");
        document.RootElement.GetProperty("scope_id").GetString().Should().Be(ScopeId);
        document.RootElement.GetProperty("actor_id").GetString().Should().Be(ActorId);
        document.RootElement.GetProperty("agent_kind").GetString().Should().Be(AgentKind);
        document.RootElement.GetProperty("module_name").GetString().Should().Be(ModuleName);
        document.RootElement.GetProperty("stage").GetString().Should().Be("accepted_for_dispatch");

        admissionPort.Targets.Should().ContainSingle();
        admissionPort.Targets[0].Should().Be(new ScopeResourceTarget(
            ScopeId,
            ScopeResourceKind.GAgentActor,
            AgentKind,
            ActorId,
            ScopeResourceOperation.Use));

        commandPort.Calls.Should().ContainSingle();
        commandPort.Calls[0].ActorId.Should().Be(ActorId);
        commandPort.Calls[0].Command.ModuleName.Should().Be(ModuleName);
        commandPort.Calls[0].Command.RemoteAudioSupport.Should().Be(VoiceRemoteAudioSupport.Supported);
        commandPort.Calls[0].Command.VoiceSessionDefaults.SampleRateHz.Should().Be(16000);
        commandPort.Calls[0].Command.VoiceSessionDefaults.Voice.Should().Be("alloy");
    }

    [Fact]
    public async Task ProvisionDefaultVoiceAgent_ShouldUseDedicatedKindAndOpenAIRealtimeModule()
    {
        var voiceAgentService = new RecordingVoiceAgentCommandService();
        await using var app = await CreateAppAsync(
            new RecordingVoicePresenceCommandPort(),
            new RecordingAdmissionPort(),
            voiceAgentService: voiceAgentService);
        var client = app.GetTestClient();

        var response = await client.PostAsync($"/api/scopes/{ScopeId}/voice-agents", content: null);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("actorId").GetString().Should().Be("nyxid-voice-alpha");
        document.RootElement.GetProperty("agentKind").GetString().Should().Be(NyxIdVoiceServiceDefaults.GAgentKind);
        document.RootElement.GetProperty("moduleName").GetString().Should().Be(
            NyxIdVoiceServiceDefaults.OpenAIRealtimeModuleName);
        document.RootElement.GetProperty("statusUrl").GetString().Should().Contain(
            $"agentKind={NyxIdVoiceServiceDefaults.GAgentKind}");
        voiceAgentService.ProvisionCommands.Should().ContainSingle().Which.Should().Be(
            new NyxIdVoiceAgentProvisionCommand(
                ScopeId,
                NyxIdVoiceServiceDefaults.OpenAIRealtimeModuleName));
    }

    [Theory]
    [InlineData((int)VoiceRemoteAudioSupport.LocalOnly)]
    [InlineData((int)VoiceRemoteAudioSupport.Unavailable)]
    public async Task Enable_ShouldRejectUnsupportedRemoteAudioSupportBeforeAdmission(int remoteAudioSupport)
    {
        var commandPort = new RecordingVoicePresenceCommandPort();
        var admissionPort = new RecordingAdmissionPort();
        await using var app = await CreateAppAsync(commandPort, admissionPort);
        var client = app.GetTestClient();

        using var request = CreateEnableRequest($$"""
        {
          "module_name": "voice_presence",
          "remote_audio_support": {{remoteAudioSupport}},
          "voice_session_defaults": {}
        }
        """);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("invalid_input");
        admissionPort.Targets.Should().BeEmpty();
        commandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Enable_ShouldReturnVoiceNotConfiguredWhenNoVoiceModuleExists()
    {
        var commandPort = new RecordingVoicePresenceCommandPort();
        var admissionPort = new RecordingAdmissionPort();
        await using var app = await CreateAppAsync(commandPort, admissionPort, registerVoiceModule: false);
        var client = app.GetTestClient();

        using var request = CreateEnableRequest("""
        {
          "module_name": "voice_presence",
          "voice_session_defaults": {}
        }
        """);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, body);
        body.Should().Contain("voice_not_configured");
        commandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Enable_ShouldReturnUnknownModuleWhenModuleNameIsNotRegistered()
    {
        var commandPort = new RecordingVoicePresenceCommandPort();
        var admissionPort = new RecordingAdmissionPort();
        await using var app = await CreateAppAsync(commandPort, admissionPort);
        var client = app.GetTestClient();

        using var request = CreateEnableRequest("""
        {
          "module_name": "missing_voice",
          "voice_session_defaults": {}
        }
        """);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, body);
        body.Should().Contain("unknown_module");
        commandPort.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ScopeResourceAdmissionStatus.Denied, HttpStatusCode.Forbidden, "admission_denied")]
    [InlineData(ScopeResourceAdmissionStatus.ScopeMismatch, HttpStatusCode.Forbidden, "admission_denied")]
    [InlineData(ScopeResourceAdmissionStatus.NotFound, HttpStatusCode.NotFound, "actor_not_found")]
    [InlineData(ScopeResourceAdmissionStatus.Unavailable, HttpStatusCode.ServiceUnavailable, "admission_unavailable")]
    public async Task Enable_ShouldMapAdmissionFailures(
        ScopeResourceAdmissionStatus status,
        HttpStatusCode expectedStatus,
        string expectedError)
    {
        var commandPort = new RecordingVoicePresenceCommandPort();
        var admissionPort = new RecordingAdmissionPort { Status = status };
        await using var app = await CreateAppAsync(commandPort, admissionPort);
        var client = app.GetTestClient();

        using var request = CreateEnableRequest("""
        {
          "module_name": "voice_presence",
          "voice_session_defaults": {}
        }
        """);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(expectedStatus, body);
        body.Should().Contain(expectedError);
        commandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Enable_ShouldMapCommandActorNotFoundToNotFound()
    {
        var commandPort = new RecordingVoicePresenceCommandPort
        {
            Exception = new VoicePresenceCapabilityCommandException(
                VoicePresenceCapabilityCommandError.ActorNotFound,
                "missing actor"),
        };
        await using var app = await CreateAppAsync(commandPort, new RecordingAdmissionPort());
        var client = app.GetTestClient();

        using var request = CreateEnableRequest("""
        {
          "module_name": "voice_presence",
          "voice_session_defaults": {}
        }
        """);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, body);
        body.Should().Contain("actor_not_found");
    }

    [Fact]
    public async Task Enable_ShouldMapCommandDispatchFailureToServiceUnavailable()
    {
        var commandPort = new RecordingVoicePresenceCommandPort
        {
            Exception = new VoicePresenceCapabilityCommandException(
                VoicePresenceCapabilityCommandError.DispatchFailed,
                "dispatch failed"),
        };
        await using var app = await CreateAppAsync(commandPort, new RecordingAdmissionPort());
        var client = app.GetTestClient();

        using var request = CreateEnableRequest("""
        {
          "module_name": "voice_presence",
          "voice_session_defaults": {}
        }
        """);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, body);
        body.Should().Contain("command_dispatch_failed");
    }

    [Fact]
    public async Task Get_ShouldAuthorizeUseAndReturnMaterializedCapability()
    {
        var admissionPort = new RecordingAdmissionPort();
        var queryPort = new RecordingCapabilityQueryPort
        {
            Snapshot = new VoicePresenceCapabilitySnapshot(
                ActorId,
                ModuleName,
                7,
                "event-voice",
                DateTimeOffset.Parse("2026-08-09T03:30:00Z"),
                Initialized: true,
                TransportAttached: false,
                PcmSampleRateHz: 24000,
                ActiveSessionId: null,
                LeaseExpiresAt: null,
                VoiceRemoteAudioSupport.Supported),
        };
        await using var app = await CreateAppAsync(
            new RecordingVoicePresenceCommandPort(),
            admissionPort,
            queryPort: queryPort);
        var client = app.GetTestClient();

        var response = await client.GetAsync(
            $"/api/scopes/{ScopeId}/gagent-actors/{ActorId}/voice-presence" +
            $"?agentKind={AgentKind}&moduleName={ModuleName}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("actorId").GetString().Should().Be(ActorId);
        document.RootElement.GetProperty("agentKind").GetString().Should().Be(AgentKind);
        document.RootElement.GetProperty("moduleName").GetString().Should().Be(ModuleName);
        document.RootElement.GetProperty("stateVersion").GetInt64().Should().Be(7);
        document.RootElement.GetProperty("initialized").GetBoolean().Should().BeTrue();
        queryPort.Calls.Should().ContainSingle().Which.Should().Be((ActorId, ModuleName));
        admissionPort.Targets.Should().ContainSingle().Which.Operation.Should().Be(ScopeResourceOperation.Use);
    }

    [Fact]
    public async Task Get_WhenCapabilityIsNotMaterialized_ShouldReturnNotFound()
    {
        await using var app = await CreateAppAsync(
            new RecordingVoicePresenceCommandPort(),
            new RecordingAdmissionPort(),
            queryPort: new RecordingCapabilityQueryPort());
        var client = app.GetTestClient();

        var response = await client.GetAsync(
            $"/api/scopes/{ScopeId}/gagent-actors/{ActorId}/voice-presence" +
            $"?agentKind={AgentKind}&moduleName={ModuleName}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, body);
        body.Should().Contain("voice_capability_not_materialized");
    }

    [Theory]
    [InlineData("", "empty_body")]
    [InlineData("{", "invalid_body")]
    [InlineData("{}", "module_name_required")]
    public async Task Enable_ShouldRejectInvalidInputWithoutAdmission(string bodyJson, string expectedError)
    {
        var commandPort = new RecordingVoicePresenceCommandPort();
        var admissionPort = new RecordingAdmissionPort();
        await using var app = await CreateAppAsync(commandPort, admissionPort);
        var client = app.GetTestClient();

        using var request = CreateEnableRequest(bodyJson);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain(expectedError);
        admissionPort.Targets.Should().BeEmpty();
        commandPort.Calls.Should().BeEmpty();
    }

    private static HttpRequestMessage CreateEnableRequest(string bodyJson)
    {
        return new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/scopes/{ScopeId}/gagent-actors/{ActorId}/voice-presence/enable?agentKind={AgentKind}")
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
        };
    }

    private static async Task<WebApplication> CreateAppAsync(
        RecordingVoicePresenceCommandPort commandPort,
        RecordingAdmissionPort admissionPort,
        bool registerVoiceModule = true,
        bool knownAgentKind = true,
        RecordingCapabilityQueryPort? queryPort = null,
        RecordingVoiceAgentCommandService? voiceAgentService = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aevatar:Authentication:Enabled"] = "false",
        });
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, AlwaysSucceedAuthHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IVoicePresenceCapabilityCommandPort>(commandPort);
        builder.Services.AddSingleton<IVoicePresenceCapabilityQueryPort>(
            queryPort ?? new RecordingCapabilityQueryPort());
        builder.Services.AddSingleton<IScopeResourceAdmissionPort>(admissionPort);
        builder.Services.AddSingleton<IAgentKindRegistry>(new StaticAgentKindRegistry(knownAgentKind));
        builder.Services.AddSingleton<INyxIdVoiceAgentCommandService>(
            voiceAgentService ?? new RecordingVoiceAgentCommandService());
        if (registerVoiceModule)
        {
            builder.Services.AddSingleton(new VoicePresenceModuleRegistration(
                [ModuleName, NyxIdVoiceServiceDefaults.OpenAIRealtimeModuleName],
                _ => throw new InvalidOperationException("module creation is not part of endpoint admission tests")));
        }

        var app = builder.Build();
        app.MapDefaultVoiceAgentEndpoints();
        app.MapVoicePresenceCapabilityAdminEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class RecordingVoicePresenceCommandPort : IVoicePresenceCapabilityCommandPort
    {
        public List<(string ActorId, VoicePresenceEnableRequested Command)> Calls { get; } = [];

        public VoicePresenceCapabilityCommandException? Exception { get; init; }

        public Task<VoicePresenceCapabilityAcceptedReceipt> EnableAsync(
            string actorId,
            VoicePresenceEnableRequested command,
            CancellationToken ct = default)
        {
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

    private sealed class RecordingAdmissionPort : IScopeResourceAdmissionPort
    {
        public List<ScopeResourceTarget> Targets { get; } = [];

        public ScopeResourceAdmissionStatus Status { get; init; } = ScopeResourceAdmissionStatus.Allowed;

        public Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
            ScopeResourceTarget target,
            CancellationToken cancellationToken = default)
        {
            Targets.Add(target);
            return Task.FromResult(new ScopeResourceAdmissionResult(Status));
        }
    }

    private sealed class RecordingCapabilityQueryPort : IVoicePresenceCapabilityQueryPort
    {
        public List<(string ActorId, string? ModuleName)> Calls { get; } = [];
        public VoicePresenceCapabilitySnapshot? Snapshot { get; init; }

        public Task<VoicePresenceCapabilitySnapshot?> GetAsync(
            string actorId,
            string? moduleName,
            CancellationToken ct = default)
        {
            Calls.Add((actorId, moduleName));
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class RecordingVoiceAgentCommandService : INyxIdVoiceAgentCommandService
    {
        public List<NyxIdVoiceAgentProvisionCommand> ProvisionCommands { get; } = [];

        public Task<NyxIdVoiceAgentProvisionResult> ProvisionAsync(
            NyxIdVoiceAgentProvisionCommand command,
            CancellationToken ct = default)
        {
            ProvisionCommands.Add(command);
            return Task.FromResult(new NyxIdVoiceAgentProvisionResult(
                NyxIdVoiceAgentProvisionStatus.Accepted,
                "nyxid-voice-alpha",
                command.ModuleName,
                "cmd-voice-agent",
                "corr-voice-agent",
                "accepted_for_dispatch"));
        }

        public Task<NyxIdVoiceAgentDeleteResult> DeleteAsync(
            NyxIdVoiceAgentDeleteCommand command,
            CancellationToken ct = default) =>
            Task.FromResult(new NyxIdVoiceAgentDeleteResult(NyxIdVoiceAgentDeleteStatus.Deleted));
    }

    private sealed class StaticAgentKindRegistry(bool known) : IAgentKindRegistry
    {
        public AgentImplementation Resolve(string kind) =>
            TryResolve(kind, out var implementation)
                ? implementation
                : throw new UnknownAgentKindException(kind);

        public bool TryResolve(string kind, out AgentImplementation implementation)
        {
            implementation = known
                ? new AgentImplementation(
                    _ => throw new InvalidOperationException("Activation is not used by endpoint admission tests."),
                    typeof(Google.Protobuf.WellKnownTypes.Empty),
                    new AgentImplementationMetadata(kind, typeof(RoleGAgentMarker).FullName!))
                : default!;
            return known;
        }

        public bool TryGetKindForAgentType(Type agentType, out string kind)
        {
            kind = AgentKind;
            return known && agentType == typeof(RoleGAgentMarker);
        }

        public bool TryGetKind(AgentImplementation implementation, out string kind)
        {
            kind = AgentKind;
            return known;
        }
    }

    private sealed class RoleGAgentMarker;

    private sealed class AlwaysSucceedAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}
