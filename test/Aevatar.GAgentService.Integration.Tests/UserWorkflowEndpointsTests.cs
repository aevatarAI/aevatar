using System.Text;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class UserWorkflowEndpointsTests
{
    [Fact]
    public async Task HandleUpsertWorkflowAsync_ShouldReturnBadRequest_WhenServiceRejectsRequest()
    {
        var result = await UserWorkflowEndpoints.HandleUpsertWorkflowAsync(
            "user-1",
            "approval",
            new UserWorkflowEndpoints.UpsertUserWorkflowHttpRequest(string.Empty),
            BuildCommandPort(),
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("WorkflowYaml is required");
    }

    [Fact]
    public async Task HandleRunWorkflowStreamAsync_ShouldReturnNotFound_WhenActorDoesNotBelongToUser()
    {
        var http = CreateHttpContext();

        await UserWorkflowEndpoints.HandleRunWorkflowStreamAsync(
            http,
            "user-1",
            new UserWorkflowEndpoints.RunUserWorkflowStreamHttpRequest("actor-404", "hello"),
            BuildQueryPort(),
            new FakeCommandInteractionService(),
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        body.Should().Contain("USER_WORKFLOW_NOT_FOUND");
    }

    [Fact]
    public async Task HandleRunWorkflowStreamAsync_ShouldDelegateToWorkflowChatPipeline_WhenOwnershipMatches()
    {
        var queryPort = new FakeServiceLifecycleQueryPort
        {
            ListServicesResult =
            [
                new ServiceCatalogSnapshot(
                    "tenant-a:workflow-app:user:token:approval",
                    "tenant-a",
                    "workflow-app",
                    "user:user-1-token",
                    "approval",
                    "Approval",
                    "rev-1",
                    "rev-1",
                    "dep-1",
                    "definition-actor-1",
                    "active",
                    [],
                    [],
                    DateTimeOffset.UtcNow),
            ],
        };
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = async (request, emitAsync, onAcceptedAsync, ct) =>
            {
                var receipt = new WorkflowChatRunAcceptedReceipt("run-actor-1", "approval", "cmd-1", "corr-1");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);
                await emitAsync(new WorkflowRunEventEnvelope
                {
                    TextMessageContent = new WorkflowTextMessageContentEventPayload
                    {
                        MessageId = "msg-1",
                        Delta = "hello",
                    },
                }, ct);
                return CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                    .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
            },
        };
        var http = CreateHttpContext();

        await UserWorkflowEndpoints.HandleRunWorkflowStreamAsync(
            http,
            "user-1",
            new UserWorkflowEndpoints.RunUserWorkflowStreamHttpRequest(
                "definition-actor-1",
                "hello",
                "session-1",
                new Dictionary<string, string> { ["source"] = "user-api" }),
            BuildQueryPort(queryPort: queryPort),
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("aevatar.run.context");
        body.Should().Contain("\"delta\": \"hello\"");
        interactionService.LastRequest.Should().NotBeNull();
        interactionService.LastRequest!.ActorId.Should().Be("definition-actor-1");
        interactionService.LastRequest.SessionId.Should().Be("session-1");
        interactionService.LastRequest.Metadata.Should().ContainKey("source").WhoseValue.Should().Be("user-api");
    }

    private static IUserWorkflowCommandPort BuildCommandPort(
        FakeServiceCommandPort? commandPort = null,
        FakeServiceLifecycleQueryPort? queryPort = null,
        FakeWorkflowActorBindingReader? bindingReader = null)
    {
        var resolvedQueryPort = queryPort ?? new FakeServiceLifecycleQueryPort();
        var queryService = BuildQueryApplicationService(resolvedQueryPort, bindingReader);
        return new UserWorkflowCommandApplicationService(
            commandPort ?? new FakeServiceCommandPort(),
            resolvedQueryPort,
            queryService,
            Options.Create(new UserWorkflowCapabilityOptions
            {
                TenantId = "tenant-a",
                AppId = "workflow-app",
                NamespacePrefix = "user:",
                DefinitionActorIdPrefix = "user-workflow",
            }));
    }

    private static IUserWorkflowQueryPort BuildQueryPort(
        FakeServiceLifecycleQueryPort? queryPort = null,
        FakeWorkflowActorBindingReader? bindingReader = null) =>
        BuildQueryApplicationService(queryPort, bindingReader);

    private static UserWorkflowQueryApplicationService BuildQueryApplicationService(
        FakeServiceLifecycleQueryPort? queryPort = null,
        FakeWorkflowActorBindingReader? bindingReader = null)
    {
        return new UserWorkflowQueryApplicationService(
            queryPort ?? new FakeServiceLifecycleQueryPort(),
            bindingReader ?? new FakeWorkflowActorBindingReader(),
            Options.Create(new UserWorkflowCapabilityOptions
            {
                TenantId = "tenant-a",
                AppId = "workflow-app",
                NamespacePrefix = "user:",
                DefinitionActorIdPrefix = "user-workflow",
            }));
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddOptions()
                .BuildServiceProvider(),
        };
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static async Task<string> ReadBodyAsync(HttpResponse response)
    {
        response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class FakeCommandInteractionService
        : ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>
    {
        public WorkflowChatRunRequest? LastRequest { get; private set; }

        public Func<WorkflowChatRunRequest, Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask>, Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>?, CancellationToken, Task<CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>>> ResultFactory { get; set; } =
            (_, _, _, _) => Task.FromResult(
                CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                    .Failure(WorkflowChatRunStartError.AgentNotFound));

        public Task<CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>> ExecuteAsync(
            WorkflowChatRunRequest request,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return ResultFactory(request, emitAsync, onAcceptedAsync, ct);
        }
    }

    private sealed class FakeServiceCommandPort : IServiceCommandPort
    {
        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(CreateServiceDefinitionCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(UpdateServiceDefinitionCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(CreateServiceRevisionCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(PrepareServiceRevisionCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(PublishServiceRevisionCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(SetDefaultServingRevisionCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(ActivateServiceRevisionCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(DeactivateServiceDeploymentCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(ReplaceServiceServingTargetsCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(StartServiceRolloutCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(AdvanceServiceRolloutCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(PauseServiceRolloutCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(ResumeServiceRolloutCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(RollbackServiceRolloutCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());

        private static ServiceCommandAcceptedReceipt Accepted() => new("target-actor", "cmd-1", "corr-1");
    }

    private sealed class FakeServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        public readonly Queue<ServiceCatalogSnapshot?> GetServiceResults = new();
        public IReadOnlyList<ServiceCatalogSnapshot> ListServicesResult { get; set; } = [];

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            _ = identity;
            return Task.FromResult(GetServiceResults.Count > 0 ? GetServiceResults.Dequeue() : null);
        }

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default)
        {
            _ = tenantId;
            _ = appId;
            _ = @namespace;
            _ = take;
            return Task.FromResult(ListServicesResult);
        }

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default) => Task.FromResult<ServiceRevisionCatalogSnapshot?>(null);
        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default) => Task.FromResult<ServiceDeploymentCatalogSnapshot?>(null);
    }

    private sealed class FakeWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default) =>
            Task.FromResult<WorkflowActorBinding?>(null);
    }
}
