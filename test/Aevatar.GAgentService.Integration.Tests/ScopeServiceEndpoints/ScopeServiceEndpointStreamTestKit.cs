using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.ScopeScripts;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.AGUI.Contracts;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using AiTextContentEvent = Aevatar.AI.Abstractions.TextMessageContentEvent;
using AiTextEndEvent = Aevatar.AI.Abstractions.TextMessageEndEvent;

namespace Aevatar.GAgentService.Integration.Tests;

public abstract class ScopeServiceEndpointStreamTestKit
{
    protected static readonly MethodInfo HandleGAgentStreamMethod = typeof(ScopeServiceEndpoints)
        .GetMethod("HandleStaticGAgentChatStreamAsync", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("HandleStaticGAgentChatStreamAsync not found.");

    protected static readonly MethodInfo HandleScriptingStreamMethod = typeof(ScopeServiceEndpoints)
        .GetMethod("HandleScriptingServiceChatStreamAsync", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("HandleScriptingServiceChatStreamAsync not found.");

    protected static readonly MethodInfo HandleDraftRunMethod = typeof(ScopeGAgentEndpoints)
        .GetMethod("HandleDraftRunAsync", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("HandleDraftRunAsync not found.");

    protected static DefaultHttpContext CreateHttpContext()
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        http.RequestServices = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = "false",
                })
                .Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
            .BuildServiceProvider();
        return http;
    }

    protected sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Aevatar.GAgentService.Integration.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    protected static ServiceInvocationResolvedTarget CreateStaticTarget(string actorTypeName, string primaryActorId)
    {
        var identity = new ServiceIdentity
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "default",
            ServiceId = "svc",
        };

        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = identity.Clone(),
            RevisionId = "rev-1",
            ImplementationKind = ServiceImplementationKind.Static,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                StaticPlan = new StaticServiceDeploymentPlan
                {
                    ActorTypeName = actorTypeName,
                    PreferredActorId = primaryActorId,
                },
            },
        };
        artifact.Endpoints.Add(new ServiceEndpointDescriptor
        {
            EndpointId = "chat",
            DisplayName = "chat",
            Kind = ServiceEndpointKind.Chat,
            RequestTypeUrl = "type.googleapis.com/aevatar.ai.ChatRequestEvent",
        });

        return new ServiceInvocationResolvedTarget(
            new ServiceInvocationResolvedService(
                "svc-key",
                "rev-1",
                "dep-1",
                primaryActorId,
                "Active",
                []),
            artifact,
            artifact.Endpoints[0]);
    }

    protected static ServiceInvocationResolvedTarget CreateScriptingTarget(string primaryActorId)
    {
        var identity = new ServiceIdentity
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "default",
            ServiceId = "svc",
        };

        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = identity.Clone(),
            RevisionId = "rev-1",
            ImplementationKind = ServiceImplementationKind.Scripting,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                ScriptingPlan = new ScriptingServiceDeploymentPlan
                {
                    Revision = "rev-1",
                    DefinitionActorId = "definition-1",
                },
            },
        };
        artifact.Endpoints.Add(new ServiceEndpointDescriptor
        {
            EndpointId = "chat",
            DisplayName = "chat",
            Kind = ServiceEndpointKind.Chat,
            RequestTypeUrl = "type.googleapis.com/aevatar.ai.ChatRequestEvent",
        });

        return new ServiceInvocationResolvedTarget(
            new ServiceInvocationResolvedService(
                "svc-key",
                "rev-1",
                "dep-1",
                primaryActorId,
                "Active",
                []),
            artifact,
            artifact.Endpoints[0]);
    }

    protected static Task InvokeDraftRunAsync(
        HttpContext http,
        string scopeId,
        ScopeGAgentEndpoints.GAgentDraftRunHttpRequest request,
        IGAgentDraftRunInteractionPort interactionPort,
        CancellationToken ct) =>
        InvokePrivateTaskAsync(
            HandleDraftRunMethod,
            http,
            scopeId,
            request,
            interactionPort,
            NullLoggerFactory.Instance,
            ct);

    protected static Task InvokeStaticStreamAsync(
        HttpContext http,
        ServiceInvocationResolvedTarget target,
        string prompt,
        string? actorId,
        string? sessionId,
        string scopeId,
        IReadOnlyDictionary<string, string>? headers,
        IReadOnlyList<ScopeServiceEndpoints.StreamContentPartHttpRequest>? inputParts,
        IStaticGAgentStreamInvocationPort<AGUIEvent> staticGAgentStreamInvocationPort,
        CancellationToken ct) =>
        InvokePrivateTaskAsync(
            HandleGAgentStreamMethod,
            http,
            prompt,
            actorId,
            sessionId,
            headers,
            inputParts,
            "rev-1",
            new ServiceInvocationRequest
            {
                Identity = target.Artifact.Identity.Clone(),
                EndpointId = target.Endpoint.EndpointId,
                RevisionId = target.Artifact.RevisionId,
                Caller = new ServiceInvocationCaller(),
            },
            staticGAgentStreamInvocationPort,
            ct);

    protected static Task InvokeScriptingStreamAsync(
        HttpContext http,
        ServiceInvocationResolvedTarget target,
        string prompt,
        string? sessionId,
        string scopeId,
        IReadOnlyDictionary<string, string>? headers,
        ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus> interactionService,
        CancellationToken ct) =>
        InvokePrivateTaskAsync(
            HandleScriptingStreamMethod,
            http,
            target,
            prompt,
            sessionId,
            scopeId,
            "svc-default",
            headers,
            interactionService,
            new ServiceInvocationRequest(),
            ct);

    protected sealed class StubStaticGAgentStreamInvocationPort : IStaticGAgentStreamInvocationPort<AGUIEvent>
    {
        public List<StaticGAgentStreamInvocationRequest> Requests { get; } = [];

        public Func<
            StaticGAgentStreamInvocationRequest,
            Func<AGUIEvent, CancellationToken, ValueTask>,
            Func<StaticGAgentStreamAcceptedReceipt, CancellationToken, ValueTask>?,
            CancellationToken,
            Task<StaticGAgentStreamInvocationResult>>? ResultFactory { get; set; }

        public async Task<StaticGAgentStreamInvocationResult> InvokeAsync(
            StaticGAgentStreamInvocationRequest request,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<StaticGAgentStreamAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            if (ResultFactory != null)
                return await ResultFactory(request, emitAsync, onAcceptedAsync, ct);

            var actorId = request.Input.PreferredActorId ?? "actor-1";
            var receipt = new StaticGAgentStreamAcceptedReceipt(
                new ServiceInvocationAcceptedReceipt
                {
                    ServiceKey = "svc-key",
                    DeploymentId = "dep-1",
                    TargetActorId = actorId,
                    EndpointId = request.EndpointId,
                    CommandId = "cmd-static-1",
                    CorrelationId = "corr-static-1",
                },
                new GAgentDraftRunAcceptedReceipt(
                    actorId,
                    typeof(StreamTestAgent).AssemblyQualifiedName!,
                    "cmd-static-1",
                    "corr-static-1",
                    request.Input.SessionId ?? string.Empty));

            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);

            return new StaticGAgentStreamInvocationResult(
                receipt,
                GAgentDraftRunStartError.None,
                GAgentDraftRunCompletionStatus.RunFinished,
                CompletionObserved: true);
        }
    }

    protected sealed class FailingAfterAcceptedDraftRunInteractionPort : IGAgentDraftRunInteractionPort
    {
        public List<GAgentDraftRunInteractionRequest> Requests { get; } = [];

        public async Task<CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>> ExecuteAsync(
            GAgentDraftRunInteractionRequest request,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<GAgentDraftRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(emitAsync);
            Requests.Add(request);

            if (onAcceptedAsync != null)
            {
                await onAcceptedAsync(
                    new GAgentDraftRunAcceptedReceipt(
                        request.PreferredActorId ?? "actor-1",
                        request.AgentKind,
                        "cmd-1",
                        "corr-1"),
                    ct);
            }

            throw new InvalidOperationException("dispatch failed");
        }
    }

    protected static async Task InvokePrivateTaskAsync(MethodInfo method, params object?[] args)
    {
        var result = method.Invoke(null, args);
        switch (result)
        {
            case Task task:
                await task;
                return;
            case ValueTask valueTask:
                await valueTask;
                return;
            default:
                throw new InvalidOperationException($"Unexpected return type: {result?.GetType().FullName}");
        }
    }

    protected static async Task<string> ReadBodyAsync(DefaultHttpContext http)
    {
        http.Response.Body.Position = 0;
        return await new StreamReader(http.Response.Body).ReadToEndAsync();
    }

    protected sealed class StubScriptServiceAguiProjectionPort : IScriptServiceAguiProjectionPort
    {
        public List<AGUIEvent> Messages { get; } = [];

        public bool ProjectionEnabled => true;

        public async Task<EventSinkProjectionAttachment<IScriptServiceAguiProjectionLease>?> AttachExistingRunProjectionAsync(
            string actorId,
            string runId,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            var lease = new StubScriptServiceAguiProjectionLease(actorId, runId);
            var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct);
            return new EventSinkProjectionAttachment<IScriptServiceAguiProjectionLease>(lease, liveSinkLease);
        }

        public async Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IScriptServiceAguiProjectionLease lease,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = ct;

            foreach (var message in Messages)
            {
                try
                {
                    await sink.PushAsync(message, CancellationToken.None);
                }
                catch (EventSinkCompletedException)
                {
                    break;
                }
            }

            return null;
        }

        public Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default)
        {
            _ = liveSinkLease;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            IScriptServiceAguiProjectionLease lease,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = ct;
            return Task.CompletedTask;
        }
    }

    protected sealed record StubScriptServiceAguiProjectionLease(string ActorId, string RunId) : IScriptServiceAguiProjectionLease;

    protected sealed class StubScriptServiceRunInteractionService
        : ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>
    {
        public List<ScriptServiceRunCommand> Commands { get; } = [];

        public List<AGUIEvent> Messages { get; } = [];

        public ScriptServiceRunStartError? StartError { get; init; }

        public Exception? ThrowAfterAccepted { get; init; }

        public async Task<CommandInteractionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>> ExecuteAsync(
            ScriptServiceRunCommand command,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<ScriptServiceRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            if (StartError != null)
                return CommandInteractionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>.Failure(StartError);

            var receipt = new ScriptServiceRunAcceptedReceipt(
                command.RuntimeActorId,
                command.RunId,
                command.CommandId,
                command.CorrelationId);
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);

            if (ThrowAfterAccepted != null)
                throw ThrowAfterAccepted;

            var completion = ScriptServiceRunCompletionStatus.Incomplete;
            var completed = false;
            foreach (var message in Messages)
            {
                await emitAsync(message, ct);
                if (message.EventCase == AGUIEvent.EventOneofCase.RunFinished)
                {
                    completion = ScriptServiceRunCompletionStatus.RunFinished;
                    completed = true;
                    break;
                }

                if (message.EventCase == AGUIEvent.EventOneofCase.RunError)
                {
                    completion = ScriptServiceRunCompletionStatus.RunError;
                    completed = true;
                    break;
                }
            }

            return CommandInteractionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>.Success(
                receipt,
                new CommandInteractionFinalizeResult<ScriptServiceRunCompletionStatus>(completion, completed));
        }

        async Task<RealtimeSessionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>>
            IRealtimeSession<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>.ExecuteAsync(
                ScriptServiceRunCommand inbound,
                Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
                Func<ScriptServiceRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
                CancellationToken ct)
        {
            return await ExecuteAsync(inbound, emitAsync, onAcceptedAsync, ct);
        }
    }

    protected sealed class RecordingProjectionSessionEventHub : Aevatar.CQRS.Projection.Core.Abstractions.IProjectionSessionEventHub<AGUIEvent>
    {
        public List<(string ScopeId, string SessionId, AGUIEvent Event)> Published { get; } = [];

        public Task PublishAsync(string scopeId, string sessionId, AGUIEvent evt, CancellationToken ct = default)
        {
            _ = ct;
            Published.Add((scopeId, sessionId, evt));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(string scopeId, string sessionId, Func<AGUIEvent, ValueTask> handler, CancellationToken ct = default)
        {
            _ = scopeId;
            _ = sessionId;
            _ = handler;
            _ = ct;
            return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
        }
    }

    protected sealed class RecordingScriptExecutionSessionEventHub
        : Aevatar.CQRS.Projection.Core.Abstractions.IProjectionSessionEventHub<AGUIEvent>
    {
        public List<(string ScopeId, string SessionId, AGUIEvent Event)> Published { get; } = [];

        public Task PublishAsync(string scopeId, string sessionId, AGUIEvent evt, CancellationToken ct = default)
        {
            _ = ct;
            Published.Add((scopeId, sessionId, evt));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(string scopeId, string sessionId, Func<AGUIEvent, ValueTask> handler, CancellationToken ct = default)
        {
            _ = scopeId;
            _ = sessionId;
            _ = handler;
            _ = ct;
            return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
        }
    }

    protected sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    protected sealed class StubSubscriptionProvider : IActorEventSubscriptionProvider
    {
        public List<EventEnvelope> Messages { get; } = [];

        public async Task<IAsyncDisposable> SubscribeAsync<TMessage>(
            string actorId,
            Func<TMessage, Task> handler,
            CancellationToken ct = default)
            where TMessage : class, IMessage, new()
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();

            if (typeof(TMessage) == typeof(EventEnvelope))
            {
                foreach (var message in Messages)
                    await handler((TMessage)(object)message);
            }

            return new NoopAsyncDisposable();
        }
    }

    protected sealed class StreamTestAgent : IAgent
    {
        public string Id => "stream-test-agent";
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stream-test-agent");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
