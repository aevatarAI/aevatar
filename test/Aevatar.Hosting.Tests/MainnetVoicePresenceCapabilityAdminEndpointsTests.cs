using System.Net;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Voice;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Mainnet.Host.Api.Voice;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Hosting.Tests;

public sealed class MainnetVoicePresenceCapabilityAdminEndpointsTests
{
    private const string Scope = "5d0d7b72-acff-49af-bb1b-9f30bbb7c102";
    private const string ActorId = "role-voice";
    private const string AgentKind = "ai.role-agent";
    private const string ModuleName = "voice_presence";

    [Fact]
    public async Task PutEnable_ShouldAuthorizeTargetAndDispatchAcceptedVoicePresenceCommand()
    {
        var commandPort = new RecordingVoicePresenceCapabilityCommandPort();
        var registry = StaticRegistry();
        var admission = new RecordingAdmissionPort { Result = ScopeResourceAdmissionResult.Allowed() };
        await using var app = await CreateAppAsync(commandPort, registry, admission);
        var client = app.GetTestClient();

        using var request = JsonPut("""
        {
          "pcmSampleRateHz": 16000,
          "remoteAudioSupport": "VOICE_REMOTE_AUDIO_SUPPORT_LOCAL_ONLY",
          "sessionDefaults": {
            "voice": "verse",
            "instructions": "stay concise",
            "sampleRateHz": 12000
          }
        }
        """);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, body);
        body.Should().Contain("accepted-command");
        body.Should().Contain("accepted-correlation");
        body.Should().Contain("accepted for dispatch");
        registry.ListCalls.Should().Equal([Scope]);
        admission.Targets.Should().ContainSingle().Which.Should().Be(new ScopeResourceTarget(
            Scope,
            ScopeResourceKind.GAgentActor,
            AgentKind,
            ActorId,
            ScopeResourceOperation.Stream));
        commandPort.Requests.Should().ContainSingle();
        var (actorId, command) = commandPort.Requests[0];
        actorId.Should().Be(ActorId);
        command.ModuleName.Should().Be(ModuleName);
        command.PcmSampleRateHz.Should().Be(16000);
        command.RemoteAudioSupport.Should().Be(VoiceRemoteAudioSupport.LocalOnly);
        command.SessionDefaults.Voice.Should().Be("verse");
        command.SessionDefaults.SampleRateHz.Should().Be(12000);
    }

    [Fact]
    public async Task PutEnable_ShouldAllowOmittedBodyAndApplyPathModuleName()
    {
        var commandPort = new RecordingVoicePresenceCapabilityCommandPort();
        await using var app = await CreateAppAsync(commandPort);
        var client = app.GetTestClient();

        using var request = JsonPut(string.Empty);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        commandPort.Requests.Should().ContainSingle();
        commandPort.Requests[0].Request.ModuleName.Should().Be(ModuleName);
        commandPort.Requests[0].Request.PcmSampleRateHz.Should()
            .Be(VoicePresenceEnableRequests.DefaultPcmSampleRateHz);
        commandPort.Requests[0].Request.RemoteAudioSupport.Should().Be(VoiceRemoteAudioSupport.Supported);
    }

    [Theory]
    [InlineData("?agentKind=ai.role-agent", """{"moduleName":"other"}""", HttpStatusCode.BadRequest, "module_name_mismatch")]
    [InlineData("", """{}""", HttpStatusCode.BadRequest, "agent_kind_required")]
    [InlineData("?agentKind=ai.role-agent", "{", HttpStatusCode.BadRequest, "invalid_body")]
    public async Task PutEnable_ShouldRejectInvalidRequestsWithoutDispatch(
        string query,
        string bodyJson,
        HttpStatusCode expectedStatus,
        string expectedError)
    {
        var commandPort = new RecordingVoicePresenceCapabilityCommandPort();
        await using var app = await CreateAppAsync(commandPort);
        var client = app.GetTestClient();
        using var request = JsonPut(bodyJson, query);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(expectedStatus, body);
        body.Should().Contain(expectedError);
        commandPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PutEnable_ShouldReturnNotFoundWhenTargetActorIsNotRegistered()
    {
        var commandPort = new RecordingVoicePresenceCapabilityCommandPort();
        await using var app = await CreateAppAsync(
            commandPort,
            new RecordingRegistryQueryPort([]));
        var client = app.GetTestClient();

        using var request = JsonPut("{}");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, body);
        body.Should().Contain("actor_not_found");
        commandPort.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ScopeResourceAdmissionStatus.Denied, HttpStatusCode.Forbidden, "actor_scope_denied")]
    [InlineData(ScopeResourceAdmissionStatus.ScopeMismatch, HttpStatusCode.Forbidden, "actor_scope_denied")]
    [InlineData(ScopeResourceAdmissionStatus.NotFound, HttpStatusCode.NotFound, "actor_not_found")]
    [InlineData(ScopeResourceAdmissionStatus.Unavailable, HttpStatusCode.ServiceUnavailable, "admission_unavailable")]
    public async Task PutEnable_ShouldMapAdmissionFailuresWithoutDispatch(
        ScopeResourceAdmissionStatus status,
        HttpStatusCode expectedStatus,
        string expectedError)
    {
        var commandPort = new RecordingVoicePresenceCapabilityCommandPort();
        await using var app = await CreateAppAsync(
            commandPort,
            admission: new RecordingAdmissionPort { Result = new ScopeResourceAdmissionResult(status) });
        var client = app.GetTestClient();

        using var request = JsonPut("{}");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(expectedStatus, body);
        body.Should().Contain(expectedError);
        commandPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PutEnable_ShouldReturnUnavailableWhenRegistryQueryFails()
    {
        var commandPort = new RecordingVoicePresenceCapabilityCommandPort();
        await using var app = await CreateAppAsync(
            commandPort,
            new RecordingRegistryQueryPort(throwOnList: true));
        var client = app.GetTestClient();

        using var request = JsonPut("{}");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, body);
        body.Should().Contain("admission_unavailable");
        commandPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PutEnable_ShouldReturnUnavailableWhenVoiceCommandProviderMissing()
    {
        var commandPort = new RecordingVoicePresenceCapabilityCommandPort();
        await using var app = await CreateAppAsync(commandPort, registerCommandPort: false);
        var client = app.GetTestClient();

        using var request = JsonPut("{}");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, body);
        body.Should().Contain("voice_not_configured");
        commandPort.Requests.Should().BeEmpty();
    }

    private static HttpRequestMessage JsonPut(string bodyJson, string query = "?agentKind=ai.role-agent") =>
        new(
            HttpMethod.Put,
            $"/api/scopes/{Scope}/gagent-actors/{ActorId}/voice-presence/modules/{ModuleName}{query}")
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
        };

    private static async Task<WebApplication> CreateAppAsync(
        RecordingVoicePresenceCapabilityCommandPort commandPort,
        RecordingRegistryQueryPort? registry = null,
        RecordingAdmissionPort? admission = null,
        bool registerCommandPort = true)
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
        builder.Services.AddSingleton<IGAgentActorRegistryQueryPort>(registry ?? StaticRegistry());
        builder.Services.AddSingleton<IScopeResourceAdmissionPort>(
            admission ?? new RecordingAdmissionPort { Result = ScopeResourceAdmissionResult.Allowed() });
        if (registerCommandPort)
            builder.Services.AddSingleton<IVoicePresenceCapabilityCommandPort>(commandPort);

        var app = builder.Build();
        app.MapVoicePresenceCapabilityAdminEndpoints();
        await app.StartAsync();
        return app;
    }

    private static RecordingRegistryQueryPort StaticRegistry() =>
        new([
            new GAgentActorGroup(AgentKind, [ActorId]),
        ]);

    private sealed class RecordingVoicePresenceCapabilityCommandPort : IVoicePresenceCapabilityCommandPort
    {
        public List<(string ActorId, VoicePresenceEnableRequested Request)> Requests { get; } = [];

        public Task<VoicePresenceCapabilityEnableReceipt> EnableAsync(
            string actorId,
            VoicePresenceEnableRequested request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add((actorId, request.Clone()));
            return Task.FromResult(new VoicePresenceCapabilityEnableReceipt(
                actorId,
                request.ModuleName,
                "accepted-command",
                "accepted-correlation",
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class RecordingRegistryQueryPort(
        IReadOnlyList<GAgentActorGroup>? groups = null,
        bool throwOnList = false) : IGAgentActorRegistryQueryPort
    {
        private readonly IReadOnlyList<GAgentActorGroup> _groups = groups ?? [];
        private readonly bool _throwOnList = throwOnList;
        public List<string> ListCalls { get; } = [];

        public Task<GAgentActorRegistrySnapshot> ListActorsAsync(
            string scopeId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_throwOnList)
                throw new InvalidOperationException("registry unavailable");

            ListCalls.Add(scopeId);
            return Task.FromResult(new GAgentActorRegistrySnapshot(
                scopeId,
                _groups,
                StateVersion: 1,
                UpdatedAt: DateTimeOffset.UtcNow,
                ObservedAt: DateTimeOffset.UtcNow));
        }
    }

    private sealed class RecordingAdmissionPort : IScopeResourceAdmissionPort
    {
        public ScopeResourceAdmissionResult Result { get; init; } = ScopeResourceAdmissionResult.NotFound();
        public List<ScopeResourceTarget> Targets { get; } = [];

        public Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
            ScopeResourceTarget target,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Targets.Add(target);
            return Task.FromResult(Result);
        }
    }

    private sealed class AlwaysSucceedAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}
