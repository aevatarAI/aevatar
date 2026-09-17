using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Channels;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Google.Protobuf;
using Any = Google.Protobuf.WellKnownTypes.Any;
using Google.Protobuf.WellKnownTypes;
using Aevatar.GAgents.StreamingProxy;
using Aevatar.GAgents.StreamingProxy.Application.Rooms;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static Aevatar.GAgents.StreamingProxy.StreamingProxyEndpoints;

namespace Aevatar.AI.Tests;

public abstract class StreamingProxyTestBase
{
        internal static StreamingProxyGAgent CreateAgent(IServiceProvider provider, string actorId)
        {
            var agent = new StreamingProxyGAgent
            {
                Services = provider,
                EventSourcingBehaviorFactory = provider.GetRequiredService<IEventSourcingBehaviorFactory<StreamingProxyGAgentState>>(),
            };

            AgentCoverageTestSupport.AssignActorId(agent, actorId);
            return agent;
        }

        internal static async Task SeedTwoParticipantLifecycleAsync(StreamingProxyGAgent agent)
        {
            await agent.ActivateAsync();
            await agent.HandleChatRequest(new ChatRequestEvent
            {
                Prompt = "Discuss the roadmap.",
                SessionId = "session-1",
                ScopeId = "scope-1",
                ToolContext = (AgentToolExecutionContext.Empty with
                {
                    Credentials = AgentToolCredentials.Empty with
                    {
                        NyxIdAccessToken = "access-token",
                    },
                }).ToPayload(),
            });
            await agent.HandleChatParticipantsResolvedRequested(new StreamingProxyChatParticipantsResolvedRequested
            {
                SessionId = "session-1",
                Participants =
                {
                    new StreamingProxyChatLifecycleParticipant
                    {
                        ParticipantId = "participant-1",
                        DisplayName = "Participant 1",
                        RoutePreference = "route-a",
                        Model = "model-a",
                    },
                    new StreamingProxyChatLifecycleParticipant
                    {
                        ParticipantId = "participant-2",
                        DisplayName = "Participant 2",
                        RoutePreference = "route-b",
                        Model = "model-b",
                    },
                },
            });
        }

        internal static EventEnvelope CreateTopologyEnvelope(IMessage payload) =>
            new()
            {
                Payload = Any.Pack(payload),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(
                    "streaming-proxy-room",
                    TopologyAudience.Parent),
            }

    ;

        internal static EventEnvelope CreateCommittedEnvelope(
            IMessage payload,
            StreamingProxyGAgentState state,
            long version)
        {
            var eventId = Guid.NewGuid().ToString("N");
            var timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
            return new EventEnvelope
            {
                Id = eventId,
                Timestamp = timestamp,
                Payload = Any.Pack(
                    new CommittedStateEventPublished
                    {
                        StateEvent = new StateEvent
                        {
                            EventId = eventId,
                            Timestamp = timestamp,
                            Version = version,
                            EventType = payload.Descriptor.FullName,
                            EventData = Any.Pack(payload),
                            AgentId = "room-a",
                        },
                StateRoot = Any.Pack(state),
                    }),
            };
        }

        internal static StreamingProxyNyxParticipantCoordinator CreateNyxCoordinator(
            IStreamingProxyRoomCommandService roomCommandService,
            Func<LLMRequest, LLMResponse>? responseFactory = null,
            string? servicesJson = null)
        {
            var httpClient = new HttpClient(new StreamingProxyTestHttpHandler(servicesJson));
            responseFactory ??= request => new LLMResponse
            {
                Content = $"reply from {request.RequestId}",
            };
            var provider = new StubNyxIdChatProviderFactory((request, _) => Task.FromResult(responseFactory(request)));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Cli:App:NyxId:Authority"] = "https://nyx.example.com",
                })
                .Build();

            return new StreamingProxyNyxParticipantCoordinator(
                provider,
                configuration,
                new StubHttpClientFactory(httpClient),
                NullLogger<StreamingProxyNyxParticipantCoordinator>.Instance);
        }

        internal sealed class StubNyxIdChatProviderFactory(
            Func<LLMRequest, CancellationToken, Task<LLMResponse>> buildResponseAsync)
            : ILLMProviderFactory, ILLMProvider
        {
            public string Name => "nyxid";

            public ILLMProvider GetProvider(string name)
            {
                _ = name;
                return this;
            }

            public ILLMProvider GetDefault() => this;

            public IReadOnlyList<string> GetAvailableProviders() => [Name];

            public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                var response = await buildResponseAsync(request, ct);
                if (!string.IsNullOrEmpty(response.Content))
                    yield return new LLMStreamChunk { DeltaContent = response.Content };

                yield return new LLMStreamChunk
                {
                    IsLast = true,
                    Usage = response.Usage,
                    FinishReason = response.FinishReason,
                };
            }
        }

        internal static async Task<(int StatusCode, string Body, string? Location)> ExecuteResultAsync(IResult result)
        {
            var context = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection()
                    .AddLogging()
                    .BuildServiceProvider(),
            };

            await using var body = new MemoryStream();
            context.Response.Body = body;

            await result.ExecuteAsync(context);
            context.Response.Body.Position = 0;
            return (
                context.Response.StatusCode,
                await new StreamReader(context.Response.Body).ReadToEndAsync(),
                context.Response.Headers.Location.ToString());
        }

        internal static DefaultHttpContext CreateScopedHttpContext(string claimedScopeId = "scope-a")
        {
            return new DefaultHttpContext
            {
                RequestServices = new ServiceCollection()
                    .AddLogging()
                    .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
                    .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
                    .BuildServiceProvider(),
                User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                    [
                        new Claim("scope_id", claimedScopeId),
                    ],
                    authenticationType: "TestAuth")),
            };
        }

        internal static string GetRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                directory = directory.Parent;

            return directory?.FullName
                ?? throw new InvalidOperationException("Repository root could not be resolved.");
        }

        internal static async Task<StreamingProxyStreamSignal?> WriteRoomSessionEventAsync(
            StreamingProxyRoomSessionEnvelope envelope,
            object writer)
        {
            var method = typeof(StreamingProxyEndpoints).GetMethod(
                "MapAndWriteRoomSessionEventAsync",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            var result = method.Invoke(null, [envelope, writer])!;
            return result switch
            {
                ValueTask<StreamingProxyStreamSignal?> valueTask => await valueTask,
                Task<StreamingProxyStreamSignal?> task => await task,
                _ => throw new InvalidOperationException($"Unexpected return type: {result.GetType()}"),
            };
        }

        internal static async Task<IResult> InvokeResultAsync(string methodName, params object[] args)
        {
            var method = typeof(StreamingProxyEndpoints).GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Static)!;
            var result = method.Invoke(null, NormalizeEndpointArgs(method, args))
                ?? throw new InvalidOperationException($"Method {methodName} returned null.");

            return result switch
            {
                Task<IResult> task => await task,
                _ => throw new InvalidOperationException($"Unexpected return type: {result.GetType()}"),
            };
        }

        internal static async Task InvokeTaskAsync(object? result)
        {
            result.Should().NotBeNull();

            switch (result)
            {
                case Task task:
                    await task;
                    return;
                case ValueTask valueTask:
                    await valueTask;
                    return;
                default:
                    throw new InvalidOperationException($"Unexpected return type: {result!.GetType()}");
            }
        }

        internal static async Task InvokeTaskAsync(string methodName, params object[] args)
        {
            var method = typeof(StreamingProxyEndpoints).GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Static)!;
            var result = method.Invoke(null, NormalizeEndpointArgs(method, args));
            await InvokeTaskAsync(result);
        }

        internal static object[] NormalizeEndpointArgs(MethodInfo method, object[] args)
        {
            var parameters = method.GetParameters();
            if (parameters.Length == args.Length &&
                ParametersMatchArgs(parameters, args))
            {
                return args;
            }

            return RebuildEndpointArgs(parameters, args.ToList());
        }

        internal static bool ParametersMatchArgs(ParameterInfo[] parameters, object[] args)
        {
            for (var i = 0; i < parameters.Length; i++)
            {
                if (!parameters[i].ParameterType.IsInstanceOfType(args[i]))
                    return false;
            }

            return true;
        }

        internal static object[] RebuildEndpointArgs(
            ParameterInfo[] parameters,
            List<object> args)
        {
            var used = new bool[args.Count];
            var rebuilt = new List<object>(parameters.Length);
            foreach (var parameter in parameters)
            {
                var index = -1;
                for (var i = 0; i < args.Count; i++)
                {
                    if (!used[i] && parameter.ParameterType.IsInstanceOfType(args[i]))
                    {
                        index = i;
                        break;
                    }
                }

                if (index >= 0)
                {
                    used[index] = true;
                    rebuilt.Add(args[index]);
                    continue;
                }

                if (parameter.ParameterType == typeof(IGAgentActorRegistryCommandPort) ||
                    parameter.ParameterType == typeof(IGAgentActorRegistryQueryPort) ||
                    parameter.ParameterType == typeof(IScopeResourceAdmissionPort))
                {
                    var store = args.OfType<StubGAgentActorStore>().FirstOrDefault() ?? new StubGAgentActorStore();
                    rebuilt.Add(store);
                    continue;
                }

                if (parameter.ParameterType == typeof(IStreamingProxyRoomCommandService))
                {
                    rebuilt.Add(args.OfType<IStreamingProxyRoomCommandService>().FirstOrDefault() ?? new StubRoomCommandService());
                    continue;
                }

                if (parameter.ParameterType == typeof(IStreamingProxyRoomSubscriptionObservationPort))
                {
                    rebuilt.Add(args.OfType<IStreamingProxyRoomSubscriptionObservationPort>().FirstOrDefault() ?? new StubRoomSubscriptionObservationPort());
                    continue;
                }

                if (parameter.ParameterType == typeof(ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>))
                {
                    rebuilt.Add(args
                        .OfType<ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>>()
                        .FirstOrDefault() ?? new StubStreamingProxyRoomChatInteractionService());
                    continue;
                }

                if (parameter.ParameterType == typeof(ILoggerFactory))
                {
                    rebuilt.Add(args.OfType<ILoggerFactory>().FirstOrDefault() ?? NullLoggerFactory.Instance);
                    continue;
                }

                if (parameter.ParameterType == typeof(CancellationToken))
                {
                    rebuilt.Add(args.OfType<CancellationToken>().FirstOrDefault());
                    continue;
                }

                throw new InvalidOperationException($"Unable to normalize endpoint argument {parameter.Name}:{parameter.ParameterType.FullName}.");
            }

            return rebuilt.ToArray();
        }

        internal sealed class StubActorRuntime : IActorRuntime
        {
            public StubActorRuntime(IEnumerable<IActor>? initialActors = null)
            {
                if (initialActors is not null)
                {
                    foreach (var actor in initialActors)
                        Actors[actor.Id] = actor;
                }
            }

            public Dictionary<string, IActor> Actors { get; } = [];

            public List<(System.Type agentType, string actorId)> CreateCalls { get; } = [];

            public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(Actors.TryGetValue(id, out var actor) ? actor : null);

            public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
                where TAgent : IAgent => CreateAsync(typeof(TAgent), id, ct);

            public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
            {
                var actorId = id ?? Guid.NewGuid().ToString("N");
                var actor = new StubActor(actorId);
                Actors[actorId] = actor;
                CreateCalls.Add((agentType, actorId));
                return Task.FromResult<IActor>(actor);
            }

            public Task DestroyAsync(string id, CancellationToken ct = default)
            {
                Actors.Remove(id);
                return Task.CompletedTask;
            }

            public Task<bool> ExistsAsync(string id) => Task.FromResult(Actors.ContainsKey(id));
            public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
            public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
        }

        internal sealed class StubActor : IActor
        {
            public StubActor(string id) => Id = id;

            public int HandleEventCalls { get; private set; }
            public List<EventEnvelope> ReceivedEnvelopes { get; } = [];

            public string Id { get; }

            public IAgent Agent => new StubAgent();

            public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
            public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

            public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
            {
                ReceivedEnvelopes.Add(envelope);
                _ = ct;
                HandleEventCalls++;
                return Task.CompletedTask;
            }

            public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

            public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
                Task.FromResult<IReadOnlyList<string>>([]);
        }

        internal sealed class StubActorDispatchPort(IActorRuntime runtime) : IActorDispatchPort
        {
            public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

            public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
            {
                Dispatches.Add((actorId, envelope));
                var actor = await runtime.GetAsync(actorId);
                if (actor is not null)
                    await actor.HandleEventAsync(envelope, ct);
                return DispatchAdmissionFactory.Create(actorId, envelope);
            }
        }

        internal sealed class ThrowingActorDispatchPort(Exception exception) : IActorDispatchPort
        {
            public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
            {
                _ = actorId;
                _ = envelope;
                _ = ct;
                return Task.FromException<DispatchAdmission>(exception);
            }
        }

        internal sealed class StubAgent : IAgent
        {
            public string Id => "agent";
            public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
            public Task<string> GetDescriptionAsync() => Task.FromResult("stub");
            public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);
            public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
            public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        }

        internal sealed class StubStreamingProxyRoomChatInteractionService
            : ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>
        {
            public List<StreamingProxyRoomChatCommand> Commands { get; } = [];
            public List<StreamingProxyRoomSessionEnvelope> Frames { get; } = [];
            public bool WaitForCancellation { get; init; }
            public StreamingProxyRoomChatStartError? Failure { get; init; }
            public Exception? ThrowOnExecute { get; init; }
            public TaskCompletionSource<bool> Started { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<CommandInteractionResult<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyProjectionCompletionStatus>> ExecuteAsync(
                StreamingProxyRoomChatCommand command,
                Func<StreamingProxyRoomSessionEnvelope, CancellationToken, ValueTask> emitAsync,
                Func<StreamingProxyRoomChatAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
                CancellationToken ct = default)
            {
                Commands.Add(command);
                Started.TrySetResult(true);
                if (ThrowOnExecute is not null)
                    throw ThrowOnExecute;

                if (Failure.HasValue)
                {
                    return CommandInteractionResult<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyProjectionCompletionStatus>
                        .Failure(Failure.Value);
                }

                var receipt = new StreamingProxyRoomChatAcceptedReceipt(
                    command.RoomId,
                    "command-id",
                    "correlation-id",
                    command.SessionId);
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);

                if (WaitForCancellation)
                    await WaitUntilCanceledAsync(ct);

                foreach (var frame in Frames)
                    await emitAsync(frame, ct);

                return CommandInteractionResult<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyProjectionCompletionStatus>
                    .Success(
                        receipt,
                        new CommandInteractionFinalizeResult<StreamingProxyProjectionCompletionStatus>(
                            StreamingProxyProjectionCompletionStatus.Completed,
                            true));
            }

            async Task<RealtimeSessionResult<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyProjectionCompletionStatus>>
                IRealtimeSession<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>.ExecuteAsync(
                    StreamingProxyRoomChatCommand inbound,
                    Func<StreamingProxyRoomSessionEnvelope, CancellationToken, ValueTask> emitAsync,
                    Func<StreamingProxyRoomChatAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
                    CancellationToken ct)
            {
                return await ExecuteAsync(inbound, emitAsync, onAcceptedAsync, ct);
            }

            internal static async Task WaitUntilCanceledAsync(CancellationToken ct)
            {
                var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var registration = ct.Register(static state =>
                    ((TaskCompletionSource<bool>)state!).TrySetCanceled(), canceled);
                await canceled.Task;
            }
        }

        internal sealed class StubRoomSubscriptionObservationPort : IStreamingProxyRoomSubscriptionObservationPort
        {
            private IEventSink<StreamingProxyRoomSessionEnvelope>? _sink;

            public List<(string RoomId, IEventSink<StreamingProxyRoomSessionEnvelope> Sink)> AttachCalls { get; } = [];
            public TaskCompletionSource<bool> Attached { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<StreamingProxyRoomSubscriptionObservationAttachment?> AttachAsync(
                string roomId,
                IEventSink<StreamingProxyRoomSessionEnvelope> sink,
                CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                _sink = sink;
                AttachCalls.Add((roomId, sink));
                Attached.TrySetResult(true);
                return Task.FromResult<StreamingProxyRoomSubscriptionObservationAttachment?>(new StreamingProxyRoomSubscriptionObservationAttachment(
                    new StubRoomSessionProjectionLease(
                        roomId,
                        $"room:{roomId}:subscription"),
                    null));
            }

            public Task DetachAndDisposeAsync(
                StreamingProxyRoomSubscriptionObservationAttachment attachment,
                IEventSink<StreamingProxyRoomSessionEnvelope> sink,
                CancellationToken ct = default)
            {
                _ = attachment;
                _ = ct;
                sink.Complete();
                return sink.DisposeAsync().AsTask();
            }

            public async Task PublishAsync(EventEnvelope envelope, CancellationToken ct = default)
            {
                if (_sink == null)
                    throw new InvalidOperationException("Subscription sink is not attached.");

                await _sink.PushAsync(
                    new StreamingProxyRoomSessionEnvelope
                    {
                        Envelope = envelope,
                    },
                ct);
            }
        }

        internal sealed class StubRoomSessionProjectionPort : IStreamingProxyRoomSessionProjectionPort
        {
            private IEventSink<StreamingProxyRoomSessionEnvelope>? _sink;
            private IStreamingProxyRoomSessionProjectionLease? _lease;

            public bool ProjectionEnabled => true;
            public bool ReturnNullLease { get; init; }

            public List<(string actorId, string sessionId)> AttachExistingCalls { get; } = [];
            public List<(string actorId, string subscriptionId)> AttachExistingSubscriptionCalls { get; } = [];
            public List<StreamingProxyRoomSessionEnvelope> Messages { get; } = [];
            public List<IStreamingProxyRoomSessionProjectionLease> AttachedLeases { get; } = [];
            public int AttachCount { get; private set; }
            public int DetachCount { get; private set; }
            public int ReleaseCount { get; private set; }

            public TaskCompletionSource<bool> Attached { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<EventSinkProjectionAttachment<IStreamingProxyRoomSessionProjectionLease>?> AttachExistingChatProjectionAsync(
                string actorId,
                string sessionId,
                IEventSink<StreamingProxyRoomSessionEnvelope> sink,
                CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                AttachExistingCalls.Add((actorId, sessionId));
                if (ReturnNullLease)
                    return null;

                _lease = new StubRoomSessionProjectionLease(actorId, sessionId);
                var liveSinkLease = await AttachLiveSinkAsync(_lease, sink, ct);
                return new EventSinkProjectionAttachment<IStreamingProxyRoomSessionProjectionLease>(_lease, liveSinkLease);
            }

            public async Task<EventSinkProjectionAttachment<IStreamingProxyRoomSessionProjectionLease>?> AttachExistingSubscriptionProjectionAsync(
                string actorId,
                string subscriptionId,
                IEventSink<StreamingProxyRoomSessionEnvelope> sink,
                CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                AttachExistingSubscriptionCalls.Add((actorId, subscriptionId));
                if (ReturnNullLease)
                    return null;

                _lease = new StubRoomSessionProjectionLease(actorId, subscriptionId);
                var liveSinkLease = await AttachLiveSinkAsync(_lease, sink, ct);
                return new EventSinkProjectionAttachment<IStreamingProxyRoomSessionProjectionLease>(_lease, liveSinkLease);
            }

            public Task<IAsyncDisposable?> AttachLiveSinkAsync(
                IStreamingProxyRoomSessionProjectionLease lease,
                IEventSink<StreamingProxyRoomSessionEnvelope> sink,
                CancellationToken ct = default)
            {
                _ = ct;
                AttachCount++;
                _lease = lease;
                _sink = sink;
                AttachedLeases.Add(lease);
                Attached.TrySetResult(true);
                foreach (var message in Messages)
                    sink.Push(message);
                return Task.FromResult<IAsyncDisposable?>(null);
            }

            public Task DetachLiveSinkAsync(
                IAsyncDisposable? liveSinkLease,
                CancellationToken ct = default)
            {
                _ = liveSinkLease;
                _ = ct;
                DetachCount++;
                return Task.CompletedTask;
            }

            public Task ReleaseActorProjectionAsync(
                IStreamingProxyRoomSessionProjectionLease lease,
                CancellationToken ct = default)
            {
                _ = lease;
                _ = ct;
                ReleaseCount++;
                return Task.CompletedTask;
            }

            public async Task PublishAsync(EventEnvelope envelope, CancellationToken ct = default)
            {
                _ = _lease ?? throw new InvalidOperationException("Projection lease was not created.");
                if (_sink == null)
                    throw new InvalidOperationException("Projection sink is not attached.");

                await _sink.PushAsync(
                    new StreamingProxyRoomSessionEnvelope
                    {
                        Envelope = envelope,
                    },
                ct);
            }
        }

        internal sealed class RecordingRoomSessionEventHub
            : IProjectionSessionEventHub<StreamingProxyRoomSessionEnvelope>
        {
            public List<(string RootActorId, string SessionId, StreamingProxyRoomSessionEnvelope Event)> Published { get; } = [];
            public int SubscribeCalls { get; private set; }
            public string? LastRootActorId { get; private set; }
            public string? LastSessionId { get; private set; }

            public Task PublishAsync(
                string rootActorId,
                string sessionId,
                StreamingProxyRoomSessionEnvelope evt,
                CancellationToken ct = default)
            {
                _ = ct;
                Published.Add((rootActorId, sessionId, evt));
                return Task.CompletedTask;
            }

            public Task<IAsyncDisposable> SubscribeAsync(
                string rootActorId,
                string sessionId,
                Func<StreamingProxyRoomSessionEnvelope, ValueTask> handler,
                CancellationToken ct = default)
            {
                SubscribeCalls++;
                LastRootActorId = rootActorId;
                LastSessionId = sessionId;
                _ = handler;
                _ = ct;
                return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
            }
        }

        internal sealed class RecordingRoomSessionReleaseService
            : IProjectionScopeReleaseService<StreamingProxyRoomSessionRuntimeLease>
        {
            public List<StreamingProxyRoomSessionRuntimeLease> Leases { get; } = [];

            public Task ReleaseIfIdleAsync(StreamingProxyRoomSessionRuntimeLease lease, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                Leases.Add(lease);
                return Task.CompletedTask;
            }
        }

        internal static IProjectionScopeAttachExistingLeaseLookup<StreamingProxyRoomSessionRuntimeLease> CreateRoomSessionAttachExistingLookup(
            IActorRuntime runtime) =>
            new ProjectionScopeAttachExistingLeaseLookup<StreamingProxyRoomSessionRuntimeLease, StreamingProxyRoomSessionProjectionContext>(
                runtime,
                request => new StreamingProxyRoomSessionProjectionContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                    SessionId = request.SessionId,
                },
                (_, context) => new StreamingProxyRoomSessionRuntimeLease(context));

        internal sealed class NoopAsyncDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        internal sealed record StubRoomSessionProjectionLease(string ActorId, string SessionId)
            : IStreamingProxyRoomSessionProjectionLease;

        internal sealed class StubGAgentActorStore :
            IGAgentActorRegistryCommandPort,
            IGAgentActorRegistryQueryPort,
            IScopeResourceAdmissionPort
        {
            public List<GAgentActorGroup> Groups { get; } = [];
            public List<(string scopeId, string gagentType, string actorId)> AddedActors { get; } = [];
            public List<(string scopeId, string gagentType, string actorId)> RemovedActors { get; } = [];
            public Exception? UnregisterException { get; init; }
            public ScopeResourceAdmissionResult AdmissionResult { get; init; } = ScopeResourceAdmissionResult.Allowed();

            public Task<GAgentActorRegistrySnapshot> ListActorsAsync(
                string scopeId,
                CancellationToken cancellationToken = default)
                => Task.FromResult(new GAgentActorRegistrySnapshot(
                    scopeId,
                    Groups.AsReadOnly(),
                    1,
                    DateTimeOffset.Parse("2026-04-27T09:30:00Z"),
                    DateTimeOffset.UtcNow));

            public Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
                GAgentActorRegistration registration,
                CancellationToken cancellationToken = default)
            {
                AddedActors.Add((registration.ScopeId, registration.AgentKind, registration.ActorId));
                return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                    registration,
                    GAgentActorRegistryCommandStage.AdmissionVisible));
            }

            public Task<GAgentActorRegistryCommandReceipt> UnregisterActorAsync(
                GAgentActorRegistration registration,
                CancellationToken cancellationToken = default)
            {
                if (UnregisterException is not null)
                    throw UnregisterException;

                RemovedActors.Add((registration.ScopeId, registration.AgentKind, registration.ActorId));
                return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                    registration,
                    GAgentActorRegistryCommandStage.AdmissionRemoved));
            }

            public Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
                ScopeResourceTarget target,
                CancellationToken cancellationToken = default)
                => Task.FromResult(AdmissionResult);
        }

        internal sealed class StubRoomCommandService(
            StreamingProxyRoomCreateResult? result = null)
            : IStreamingProxyRoomCommandService
        {
            public List<StreamingProxyRoomCreateCommand> Commands { get; } = [];
            public List<StreamingProxyRoomPostMessageCommand> PostMessageCommands { get; } = [];
            public List<StreamingProxyRoomJoinCommand> JoinCommands { get; } = [];
            public List<StreamingProxyRoomLeaveCommand> LeaveCommands { get; } = [];
            public List<StreamingProxyRoomTerminalStateCommand> TerminalCommands { get; } = [];
            public List<StreamingProxyRoomParticipantsResolvedCommand> ParticipantsResolvedCommands { get; } = [];
            public List<StreamingProxyRoomParticipantReplyObservedCommand> ReplyObservedCommands { get; } = [];
            public List<StreamingProxyRoomParticipantReplyFailedCommand> ReplyFailedCommands { get; } = [];
            public StreamingProxyRoomPostMessageResult PostMessageResult { get; init; } =
                new(StreamingProxyRoomPostMessageStatus.Accepted);
            public StreamingProxyRoomJoinResult? JoinResult { get; init; }

            public Task<StreamingProxyRoomCreateResult> CreateRoomAsync(
                StreamingProxyRoomCreateCommand command,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Commands.Add(command);
                return Task.FromResult(result ?? new StreamingProxyRoomCreateResult(
                    StreamingProxyRoomCreateStatus.Created,
                    "room-a",
                    "Room A"));
            }

            public Task<StreamingProxyRoomPostMessageResult> PostMessageAsync(
                StreamingProxyRoomPostMessageCommand command,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PostMessageCommands.Add(command);
                return Task.FromResult(PostMessageResult);
            }

            public Task<StreamingProxyRoomJoinResult> JoinAsync(
                StreamingProxyRoomJoinCommand command,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                JoinCommands.Add(command);
                return Task.FromResult(JoinResult ?? new StreamingProxyRoomJoinResult(
                    StreamingProxyRoomJoinStatus.Accepted,
                    command.AgentId?.Trim(),
                    command.DisplayName?.Trim()));
            }

            public Task<StreamingProxyRoomLeaveResult> LeaveAsync(
                StreamingProxyRoomLeaveCommand command,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LeaveCommands.Add(command);
                return Task.FromResult(new StreamingProxyRoomLeaveResult(
                    StreamingProxyRoomLeaveStatus.Accepted,
                    command.AgentId?.Trim()));
            }

            public Task PublishTerminalStateAsync(
                StreamingProxyRoomTerminalStateCommand command,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TerminalCommands.Add(command);
                return Task.CompletedTask;
            }

            public Task SubmitParticipantsResolvedAsync(
                StreamingProxyRoomParticipantsResolvedCommand command,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ParticipantsResolvedCommands.Add(command);
                return Task.CompletedTask;
            }

            public Task SubmitParticipantReplyObservedAsync(
                StreamingProxyRoomParticipantReplyObservedCommand command,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReplyObservedCommands.Add(command);
                return Task.CompletedTask;
            }

            public Task SubmitParticipantReplyFailedAsync(
                StreamingProxyRoomParticipantReplyFailedCommand command,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReplyFailedCommands.Add(command);
                return Task.CompletedTask;
            }
        }

        internal sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => client;
        }

        internal sealed class StreamingProxyTestHttpHandler(string? servicesJson = null) : HttpMessageHandler
        {
            private const string DefaultServicesJson = """
                {
                  "services": [
                    {
                      "user_service_id": "svc-node-a",
                      "service_slug": "openclaw",
                      "display_name": "OpenClaw Node A",
                      "status": "ready",
                      "route_value": "/api/v1/proxy/s/openclaw/node-a",
                      "node_id": "node-a",
                      "allowed": true,
                      "models": ["model-a"]
                    },
                    {
                      "user_service_id": "svc-node-b",
                      "service_slug": "openclaw",
                      "display_name": "OpenClaw Node B",
                      "status": "ready",
                      "route_value": "/api/v1/proxy/s/openclaw/node-b",
                      "node_id": "node-b",
                      "allowed": true,
                      "models": ["model-b"]
                    },
                    {
                      "user_service_id": "svc-node-c",
                      "service_slug": "openclaw",
                      "display_name": "OpenClaw Node C",
                      "status": "ready",
                      "route_value": "/api/v1/proxy/s/openclaw/node-c",
                      "node_id": "node-c",
                      "allowed": true,
                      "models": ["model-c"]
                    }
                  ]
                }
                """;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(servicesJson ?? DefaultServicesJson),
                });
            }
        }

        internal sealed class StubStreamProvider : IStreamProvider
        {
            private readonly Dictionary<string, StubStream> _streams = [];

            public IStream GetStream(string actorId) => GetTypedStream(actorId);

            public StubStream GetTypedStream(string actorId)
            {
                if (!_streams.TryGetValue(actorId, out var stream))
                {
                    stream = new StubStream(actorId);
                    _streams[actorId] = stream;
                }

                return stream;
            }
        }

        internal sealed class StubStream(string streamId) : IStream
        {
            private Func<EventEnvelope, Task>? _envelopeHandler;
            private readonly Dictionary<System.Type, Func<IMessage, Task>> _typedHandlers = [];

            public string StreamId { get; } = streamId;

            public async Task ProduceAsync<T>(T message, CancellationToken ct = default)
                where T : IMessage
            {
                ct.ThrowIfCancellationRequested();
                if (message is EventEnvelope envelope && _envelopeHandler is not null)
                {
                    await _envelopeHandler(envelope);
                    return;
                }

                if (_typedHandlers.TryGetValue(typeof(T), out var handler))
                    await handler(message);
            }

            public Task<IAsyncDisposable> SubscribeAsync<T>(
                Func<T, Task> handler,
                CancellationToken ct = default)
                where T : IMessage, new()
            {
                ct.ThrowIfCancellationRequested();
                if (typeof(T) == typeof(EventEnvelope))
                    _envelopeHandler = envelope => ((Func<EventEnvelope, Task>)(object)handler)(envelope);
                else
                    _typedHandlers[typeof(T)] = message => handler((T)message);

                return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
            }

            public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default) => Task.CompletedTask;
            public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default) => Task.CompletedTask;
            public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
        }

        internal sealed class StubActorEventSubscriptionProvider(StubStreamProvider streams) : IActorEventSubscriptionProvider
        {
            private readonly TaskCompletionSource _subscriptionsReady =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _subscriptionCount;

            public Task WaitUntilSubscriptionsReadyAsync() => _subscriptionsReady.Task;

            public async Task<IAsyncDisposable> SubscribeAsync<TMessage>(
                string actorId,
                Func<TMessage, Task> handler,
                CancellationToken ct = default)
                where TMessage : class, IMessage, new()
            {
                var subscription = await streams.GetTypedStream(actorId).SubscribeAsync(handler, ct);
                if (Interlocked.Increment(ref _subscriptionCount) >= 2)
                    _subscriptionsReady.TrySetResult();
                return subscription;
            }
        }

        internal sealed class StubTerminalQueryPort : IStreamingProxyChatSessionTerminalQueryPort
        {
            private readonly StreamingProxyChatSessionTerminalSnapshot? _snapshot;

            public StubTerminalQueryPort(StreamingProxyChatSessionTerminalStatus? status = null)
            {
                if (!status.HasValue)
                    return;

                _snapshot = new StreamingProxyChatSessionTerminalSnapshot
                {
                    RootActorId = "room-a",
                    SessionId = "session-123",
                    Status = status.Value,
                };
            }

            public int QueryCount { get; private set; }

            public Task<StreamingProxyChatSessionTerminalSnapshot?> GetAsync(
                string rootActorId,
                string sessionId,
                CancellationToken ct = default)
            {
                _ = rootActorId;
                _ = sessionId;
                _ = ct;
                QueryCount++;
                return Task.FromResult(_snapshot);
            }
        }

        internal sealed class StubRoomParticipantsQueryPort : IStreamingProxyRoomParticipantsQueryPort
        {
            private readonly StreamingProxyRoomParticipantsSnapshot? _snapshot;
            public List<string> Queries { get; } = [];

            public StubRoomParticipantsQueryPort(StreamingProxyRoomParticipantsSnapshot? snapshot = null)
            {
                _snapshot = snapshot;
            }

            public Task<StreamingProxyRoomParticipantsSnapshot?> GetAsync(
                string rootActorId,
                CancellationToken ct = default)
            {
                Queries.Add(rootActorId);
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(_snapshot);
            }
        }

        internal sealed class RecordingProjectionWriteDispatcher<TReadModel>
            : IProjectionWriteDispatcher<TReadModel>
            where TReadModel : class, IProjectionReadModel
        {
            public List<TReadModel> Upserts { get; } = [];
            public List<string> Deletes { get; } = [];

            public Task<ProjectionWriteResult> UpsertAsync(
                TReadModel readModel,
                CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                Upserts.Add(readModel);
                return Task.FromResult(ProjectionWriteResult.Applied());
            }

            public Task<ProjectionWriteResult> DeleteAsync(
                string id,
                CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                Deletes.Add(id);
                return Task.FromResult(ProjectionWriteResult.Applied());
            }
        }

        internal sealed class TestHostEnvironment : IHostEnvironment
        {
            public string EnvironmentName { get; set; } = Environments.Production;
            public string ApplicationName { get; set; } = "StreamingProxyTestBase";
            public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
            public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
                new Microsoft.Extensions.FileProviders.NullFileProvider();
        }
}
