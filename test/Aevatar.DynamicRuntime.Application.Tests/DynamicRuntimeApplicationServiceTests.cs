using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.DynamicRuntime.Application;
using Aevatar.DynamicRuntime.Infrastructure;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using FluentAssertions;
using Xunit;

namespace Aevatar.DynamicRuntime.Application.Tests;

public sealed class DynamicRuntimeApplicationServiceTests
{
    [Fact]
    public async Task RegisterActivateAndExecute_ShouldPersistServiceStateAndRunScript()
    {
        var (service, serviceStateStore) = CreateService();

        var script = """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public Task<string> HandleAsync(string input, CancellationToken ct = default)
        => Task.FromResult($"echo:{input}");
}

var entrypoint = new ScriptEntrypoint();
""";

        await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.echo",
                "v1",
                script,
                "ScriptEntrypoint",
                DynamicServiceMode.Hybrid,
                ["https://example.com/echo"],
                ["event.echo"],
                "cap:v1"),
            new DynamicCommandContext("idem-service-register"));

        await service.ActivateServiceAsync("svc.echo", new DynamicCommandContext("idem-service-activate", "1"));

        await service.CreateContainerAsync(
            new CreateContainerRequest(
                "container.echo.1",
                "stack.echo",
                "echo",
                "svc.echo",
                "sha256:echo-v1",
                "role.echo.1"),
            new DynamicCommandContext("idem-container-create"));

        await service.StartContainerAsync("container.echo.1", new DynamicCommandContext("idem-container-start"));

        var execResult = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest("container.echo.1", "svc.echo", "hello"),
            new DynamicCommandContext("idem-container-exec"));

        var serviceSnapshot = await service.GetServiceDefinitionAsync("svc.echo");
        var runSnapshot = await service.GetRunAsync(execResult.AggregateId.Replace("dynamic:run:", string.Empty, StringComparison.Ordinal));
        var state = await serviceStateStore.LoadAsync("dynamic:service:svc.echo");

        execResult.Status.Should().Be("SUCCEEDED");
        serviceSnapshot.Should().NotBeNull();
        serviceSnapshot!.Status.Should().Be(DynamicServiceStatus.Active);
        serviceSnapshot.ScriptCode.Should().Contain("ScriptEntrypoint");

        runSnapshot.Should().NotBeNull();
        runSnapshot!.Status.Should().Be("Succeeded");
        runSnapshot.Result.Should().Be("echo:hello");

        state.Should().NotBeNull();
        state!.ScriptCode.Should().Contain("ScriptEntrypoint");
        state.Status.Should().Be(DynamicServiceStatus.Active.ToString());
    }

    [Fact]
    public async Task ApplyComposeAsync_ShouldPersistComposeYaml()
    {
        var (service, _) = CreateService();

        var result = await service.ApplyComposeAsync(
            new ComposeApplyYamlRequest(
                "stack-a",
                "spec-digest-a",
                """
services:
  svc-a:
    image: img-a:latest
""",
                3,
                [new ComposeServiceSpec("svc-a", "img-a:latest", 2, DynamicServiceMode.Hybrid)]),
            new DynamicCommandContext("idem-compose-1", "0"));

        var snapshot = await service.GetStackAsync("stack-a");

        result.Status.Should().Be("APPLIED");
        snapshot.Should().NotBeNull();
        snapshot!.ComposeYaml.Should().Contain("services:");
        snapshot.DesiredGeneration.Should().Be(3);
        snapshot.ObservedGeneration.Should().Be(3);

        var services = await service.GetComposeServicesAsync("stack-a");
        var events = await service.GetComposeEventsAsync("stack-a");
        services.Should().ContainSingle(item => item.ServiceName == "svc-a" && item.ReplicasDesired == 2);
        events.Should().Contain(item => item.EventType == "ComposeApplied");
    }

    [Fact]
    public async Task BuildImageAsync_ShouldSupportTagAndDigestQuery()
    {
        var (service, _) = CreateService();

        await service.BuildImageAsync(
            new BuildImageRequest("img-build", "source:v1", "stable"),
            new DynamicCommandContext("idem-img-build"));

        var tag = await service.GetImageTagAsync("img-build", "stable");
        var digest = await service.GetImageDigestAsync("img-build", tag!.Digest);

        tag.Should().NotBeNull();
        digest.Should().NotBeNull();
        digest!.Exists.Should().BeTrue();
    }

    [Fact]
    public async Task CancelRun_WhenTerminal_ShouldThrow()
    {
        var (service, _) = CreateService();

        await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.cancel",
                "v1",
                """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public Task<string> HandleAsync(string input, CancellationToken ct = default) => Task.FromResult("ok");
}
var entrypoint = new ScriptEntrypoint();
""",
                "ScriptEntrypoint",
                DynamicServiceMode.Event,
                [],
                ["event.cancel"],
                "cap"),
            new DynamicCommandContext("idem-cancel-register"));
        await service.ActivateServiceAsync("svc.cancel", new DynamicCommandContext("idem-cancel-activate", "1"));
        await service.CreateContainerAsync(
            new CreateContainerRequest("container.cancel", "stack.cancel", "cancel", "svc.cancel", "sha256:cancel", "role.cancel"),
            new DynamicCommandContext("idem-cancel-create"));
        await service.StartContainerAsync("container.cancel", new DynamicCommandContext("idem-cancel-start"));

        var runResult = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest("container.cancel", "svc.cancel", "any", "run-cancel"),
            new DynamicCommandContext("idem-cancel-exec"));
        runResult.Status.Should().Be("SUCCEEDED");

        var act = () => service.CancelRunAsync("run-cancel", "stop", new DynamicCommandContext("idem-cancel-run"));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("RUN_ALREADY_TERMINAL");
    }

    [Fact]
    public async Task DuplicateIdempotencyKeySamePayload_ShouldReturnFirstResult()
    {
        var (service, _) = CreateService();
        var ctx = new DynamicCommandContext("idem-dup", "0");

        var first = await service.PublishImageAsync(new PublishImageRequest("img-dup", "v1", "sha256:dup"), ctx);
        var second = await service.PublishImageAsync(new PublishImageRequest("img-dup", "v1", "sha256:dup"), ctx);

        second.Should().Be(first);
    }

    [Fact]
    public async Task IfMatchVersionConflict_ShouldThrow()
    {
        var (service, _) = CreateService();

        await service.PublishImageAsync(
            new PublishImageRequest("img-ver", "latest", "sha256:ver"),
            new DynamicCommandContext("idem-ver-1", "0"));

        var act = () => service.PublishImageAsync(
            new PublishImageRequest("img-ver", "stable", "sha256:ver2"),
            new DynamicCommandContext("idem-ver-2", "999"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("VERSION_CONFLICT");
    }

    [Fact]
    public async Task SameIdempotencyKeyDifferentPayload_ShouldThrowPayloadMismatch()
    {
        var (service, _) = CreateService();
        var context = new DynamicCommandContext("idem-mismatch", "0");

        await service.PublishImageAsync(
            new PublishImageRequest("img-mismatch", "stable", "sha256:one"),
            context);

        var act = () => service.PublishImageAsync(
            new PublishImageRequest("img-mismatch", "stable", "sha256:two"),
            context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("IDEMPOTENCY_PAYLOAD_MISMATCH");
    }

    [Fact]
    public async Task ApplyComposeWithoutIfMatch_ShouldThrowVersionConflict()
    {
        var (service, _) = CreateService();

        var act = () => service.ApplyComposeAsync(
            new ComposeApplyYamlRequest(
                "stack-if-match",
                "spec-if-match",
                "services: {}",
                1,
                [new ComposeServiceSpec("svc", "img:v1", 1, DynamicServiceMode.Hybrid)]),
            new DynamicCommandContext("idem-compose-if-match"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("VERSION_CONFLICT");
    }

    [Fact]
    public async Task RolloutComposeService_ShouldPinImageToDigest()
    {
        var (service, _) = CreateService();

        await service.ApplyComposeAsync(
            new ComposeApplyYamlRequest(
                "stack-rollout",
                "spec-rollout",
                "services: {}",
                1,
                [new ComposeServiceSpec("svc-rollout", "svc-rollout:v1", 1, DynamicServiceMode.Hybrid)]),
            new DynamicCommandContext("idem-rollout-apply", "0"));

        var result = await service.RolloutComposeServiceAsync(
            new ComposeServiceRolloutRequest("stack-rollout", "svc-rollout", "svc-rollout:v2"),
            new DynamicCommandContext("idem-rollout", "0"));

        var serviceSnapshot = (await service.GetComposeServicesAsync("stack-rollout"))
            .Single(item => item.ServiceName == "svc-rollout");
        var events = await service.GetComposeEventsAsync("stack-rollout");

        result.Status.Should().Be("ROLLED_OUT");
        serviceSnapshot.ImageRef.Should().StartWith("sha256:");
        serviceSnapshot.RolloutStatus.Should().Be("RolledOut");
        events.Should().Contain(item => item.EventType == "ComposeServiceRolledOut" && item.Details.Contains("sha256:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteBuild_ShouldPublishImageAndDeployComposeService()
    {
        var (service, _) = CreateService();

        await service.ApplyComposeAsync(
            new ComposeApplyYamlRequest(
                "stack-build",
                "spec-build",
                "services: {}",
                1,
                [new ComposeServiceSpec("svc-build", "svc-build:v1", 1, DynamicServiceMode.Hybrid)]),
            new DynamicCommandContext("idem-build-apply", "0"));

        await service.SubmitBuildPlanAsync(
            new SubmitBuildPlanRequest("build-job-1", "stack-build", "svc-build", "source:v2"),
            new DynamicCommandContext("idem-build-plan"));
        await service.ValidateBuildAsync("build-job-1", new DynamicCommandContext("idem-build-validate"));
        var approved = await service.ApproveBuildAsync("build-job-1", new DynamicCommandContext("idem-build-approve", "2"));

        var executed = await service.ExecuteBuildAsync("build-job-1", new DynamicCommandContext("idem-build-execute", approved.ETag));
        var buildSnapshot = await service.GetBuildJobAsync("build-job-1");
        var serviceSnapshot = (await service.GetComposeServicesAsync("stack-build"))
            .Single(item => item.ServiceName == "svc-build");
        var latestTag = await service.GetImageTagAsync("stack-build/svc-build", "latest");
        var jobTag = await service.GetImageTagAsync("stack-build/svc-build", "build-job-1");

        executed.Status.Should().Be("EXECUTED");
        buildSnapshot.Should().NotBeNull();
        buildSnapshot!.ResultImageDigest.Should().StartWith("sha256:");
        serviceSnapshot.ImageRef.Should().Be(buildSnapshot.ResultImageDigest);
        serviceSnapshot.RolloutStatus.Should().Be("RolledOut");
        latestTag.Should().NotBeNull();
        latestTag!.Digest.Should().Be(buildSnapshot.ResultImageDigest);
        jobTag.Should().NotBeNull();
        jobTag!.Digest.Should().Be(buildSnapshot.ResultImageDigest);
    }

    [Fact]
    public async Task CreateContainer_ShouldResolveImageRefToDigest()
    {
        var (service, _) = CreateService();

        await service.CreateContainerAsync(
            new CreateContainerRequest(
                "container.digest.1",
                "stack.digest",
                "svc.digest",
                "svc.digest",
                "svc.digest:latest",
                "role.digest.1"),
            new DynamicCommandContext("idem-container-digest"));

        var snapshot = await service.GetContainerAsync("container.digest.1");
        snapshot.Should().NotBeNull();
        snapshot!.ImageDigest.Should().StartWith("sha256:");
    }

    [Fact]
    public async Task ApplyCompose_EventAndHybridModes_ShouldSubscribeEnvelopeLease()
    {
        var subscriber = new RecordingEnvelopeSubscriberPort();
        var (service, _) = CreateService(subscriber);

        await service.ApplyComposeAsync(
            new ComposeApplyYamlRequest(
                "stack-subscription",
                "spec-subscription",
                "services: {}",
                1,
                [
                    new ComposeServiceSpec("svc-daemon", "svc-daemon:v1", 1, DynamicServiceMode.Daemon),
                    new ComposeServiceSpec("svc-event", "svc-event:v1", 0, DynamicServiceMode.Event),
                    new ComposeServiceSpec("svc-hybrid", "svc-hybrid:v1", 1, DynamicServiceMode.Hybrid),
                ]),
            new DynamicCommandContext("idem-subscription", "0"));

        subscriber.Requests.Should().HaveCount(2);
        subscriber.Requests.Select(item => item.ServiceName).Should().BeEquivalentTo(["svc-event", "svc-hybrid"]);
    }

    [Fact]
    public async Task ActivateService_EventMode_ShouldSubscribeEnvelopeLease()
    {
        var subscriber = new RecordingEnvelopeSubscriberPort();
        var (service, _) = CreateService(subscriber);

        await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.subscription",
                "v1",
                """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public Task<string> HandleAsync(string input, CancellationToken ct = default) => Task.FromResult(input);
}
var entrypoint = new ScriptEntrypoint();
""",
                "ScriptEntrypoint",
                DynamicServiceMode.Event,
                [],
                ["event.subscription"],
                "cap:subscription"),
            new DynamicCommandContext("idem-service-subscription-register"));

        await service.ActivateServiceAsync("svc.subscription", new DynamicCommandContext("idem-service-subscription-activate", "1"));

        subscriber.Requests.Should().ContainSingle(item =>
            string.Equals(item.StackId, "_services", StringComparison.Ordinal) &&
            string.Equals(item.ServiceName, "svc.subscription", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MultiAgentBusinessSimulation_ShouldAlignDockerLikeSemantics()
    {
        var subscriber = new RecordingEnvelopeSubscriberPort();
        var publisher = new RecordingEnvelopePublisherPort();
        var (service, _) = CreateService(subscriber, publisher);

        var gatewayScript = """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public Task<string> HandleAsync(string input, CancellationToken ct = default)
        => Task.FromResult($"{{\"agent\":\"gateway\",\"llm\":\"intent_parsed\",\"input\":\"{input}\"}}");
}
var entrypoint = new ScriptEntrypoint();
""";

        var plannerScript = """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public Task<string> HandleAsync(string input, CancellationToken ct = default)
        => Task.FromResult($"{{\"agent\":\"planner\",\"llm\":\"plan_created\",\"upstream\":{input}}}");
}
var entrypoint = new ScriptEntrypoint();
""";

        var workerScript = """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public Task<string> HandleAsync(string input, CancellationToken ct = default)
        => Task.FromResult($"{{\"agent\":\"worker\",\"llm\":\"task_executed\",\"payload\":{input}}}");
}
var entrypoint = new ScriptEntrypoint();
""";

        var registerGateway = await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.gateway",
                "v1",
                gatewayScript,
                "ScriptEntrypoint",
                DynamicServiceMode.Hybrid,
                ["https://gateway.internal/chat"],
                ["evt.gateway.request"],
                "cap:gateway:v1"),
            new DynamicCommandContext("idem-register-gateway"));

        var registerPlanner = await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.planner",
                "v1",
                plannerScript,
                "ScriptEntrypoint",
                DynamicServiceMode.Event,
                [],
                ["evt.plan.request"],
                "cap:planner:v1"),
            new DynamicCommandContext("idem-register-planner"));

        var registerWorker = await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.worker",
                "v1",
                workerScript,
                "ScriptEntrypoint",
                DynamicServiceMode.Daemon,
                ["https://worker.internal/task"],
                [],
                "cap:worker:v1"),
            new DynamicCommandContext("idem-register-worker"));

        await service.ActivateServiceAsync("svc.gateway", new DynamicCommandContext("idem-activate-gateway", registerGateway.ETag));
        await service.ActivateServiceAsync("svc.planner", new DynamicCommandContext("idem-activate-planner", registerPlanner.ETag));
        await service.ActivateServiceAsync("svc.worker", new DynamicCommandContext("idem-activate-worker", registerWorker.ETag));

        var applyResult = await service.ApplyComposeAsync(
            new ComposeApplyYamlRequest(
                "stack.order",
                "spec:stack.order:v1",
                """
services:
  gateway:
    image: svc.gateway:v1
  planner:
    image: svc.planner:v1
  worker:
    image: svc.worker:v1
""",
                1,
                [
                    new ComposeServiceSpec("gateway", "svc.gateway:v1", 1, DynamicServiceMode.Hybrid),
                    new ComposeServiceSpec("planner", "svc.planner:v1", 0, DynamicServiceMode.Event),
                    new ComposeServiceSpec("worker", "svc.worker:v1", 2, DynamicServiceMode.Daemon),
                ]),
            new DynamicCommandContext("idem-compose-apply-order", "0"));

        applyResult.Status.Should().Be("APPLIED");
        var stack = await service.GetStackAsync("stack.order");
        stack.Should().NotBeNull();
        stack!.DesiredGeneration.Should().Be(1);
        stack.ObservedGeneration.Should().Be(1);

        await service.CreateContainerAsync(
            new CreateContainerRequest("ctr.gateway.1", "stack.order", "gateway", "svc.gateway", "svc.gateway:v1", "role.gateway.1"),
            new DynamicCommandContext("idem-create-ctr-gateway-1"));
        await service.CreateContainerAsync(
            new CreateContainerRequest("ctr.planner.1", "stack.order", "planner", "svc.planner", "svc.planner:v1", "role.planner.1"),
            new DynamicCommandContext("idem-create-ctr-planner-1"));
        await service.CreateContainerAsync(
            new CreateContainerRequest("ctr.worker.1", "stack.order", "worker", "svc.worker", "svc.worker:v1", "role.worker.1"),
            new DynamicCommandContext("idem-create-ctr-worker-1"));
        await service.CreateContainerAsync(
            new CreateContainerRequest("ctr.worker.2", "stack.order", "worker", "svc.worker", "svc.worker:v1", "role.worker.2"),
            new DynamicCommandContext("idem-create-ctr-worker-2"));

        await service.StartContainerAsync("ctr.gateway.1", new DynamicCommandContext("idem-start-ctr-gateway-1"));
        await service.StartContainerAsync("ctr.planner.1", new DynamicCommandContext("idem-start-ctr-planner-1"));
        await service.StartContainerAsync("ctr.worker.1", new DynamicCommandContext("idem-start-ctr-worker-1"));
        await service.StartContainerAsync("ctr.worker.2", new DynamicCommandContext("idem-start-ctr-worker-2"));

        var gatewayRun = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest("ctr.gateway.1", "svc.gateway", "order_id=1001&user=alice"),
            new DynamicCommandContext("idem-run-gateway"));
        var gatewaySnapshot = await service.GetRunAsync(gatewayRun.AggregateId.Replace("dynamic:run:", string.Empty, StringComparison.Ordinal));
        gatewaySnapshot.Should().NotBeNull();
        gatewaySnapshot!.Status.Should().Be("Succeeded");
        gatewaySnapshot.Result.Should().Contain("\"agent\":\"gateway\"");

        var plannerRun = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest("ctr.planner.1", "svc.planner", gatewaySnapshot.Result),
            new DynamicCommandContext("idem-run-planner"));
        var plannerSnapshot = await service.GetRunAsync(plannerRun.AggregateId.Replace("dynamic:run:", string.Empty, StringComparison.Ordinal));
        plannerSnapshot.Should().NotBeNull();
        plannerSnapshot!.Status.Should().Be("Succeeded");
        plannerSnapshot.Result.Should().Contain("\"agent\":\"planner\"");

        var workerRun1 = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest("ctr.worker.1", "svc.worker", plannerSnapshot.Result),
            new DynamicCommandContext("idem-run-worker-1"));
        var workerRun2 = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest("ctr.worker.2", "svc.worker", plannerSnapshot.Result),
            new DynamicCommandContext("idem-run-worker-2"));

        var workerSnapshot1 = await service.GetRunAsync(workerRun1.AggregateId.Replace("dynamic:run:", string.Empty, StringComparison.Ordinal));
        var workerSnapshot2 = await service.GetRunAsync(workerRun2.AggregateId.Replace("dynamic:run:", string.Empty, StringComparison.Ordinal));
        workerSnapshot1.Should().NotBeNull();
        workerSnapshot2.Should().NotBeNull();
        workerSnapshot1!.Result.Should().Contain("\"agent\":\"worker\"");
        workerSnapshot2!.Result.Should().Contain("\"agent\":\"worker\"");

        var workerContainer1 = await service.GetContainerAsync("ctr.worker.1");
        var workerContainer2 = await service.GetContainerAsync("ctr.worker.2");
        workerContainer1.Should().NotBeNull();
        workerContainer2.Should().NotBeNull();
        workerContainer1!.ImageDigest.Should().StartWith("sha256:");
        workerContainer2!.ImageDigest.Should().Be(workerContainer1.ImageDigest);

        await service.SubmitBuildPlanAsync(
            new SubmitBuildPlanRequest("build.worker.v2", "stack.order", "worker", "source:worker:v2"),
            new DynamicCommandContext("idem-build-plan-worker-v2"));
        var validateResult = await service.ValidateBuildAsync("build.worker.v2", new DynamicCommandContext("idem-build-validate-worker-v2"));
        var approveResult = await service.ApproveBuildAsync("build.worker.v2", new DynamicCommandContext("idem-build-approve-worker-v2", validateResult.ETag));
        var executeResult = await service.ExecuteBuildAsync("build.worker.v2", new DynamicCommandContext("idem-build-execute-worker-v2", approveResult.ETag));

        executeResult.Status.Should().Be("EXECUTED");
        var buildSnapshot = await service.GetBuildJobAsync("build.worker.v2");
        buildSnapshot.Should().NotBeNull();
        buildSnapshot!.ResultImageDigest.Should().StartWith("sha256:");

        var workerService = (await service.GetComposeServicesAsync("stack.order"))
            .Single(item => string.Equals(item.ServiceName, "worker", StringComparison.Ordinal));
        workerService.ImageRef.Should().Be(buildSnapshot.ResultImageDigest);
        workerService.RolloutStatus.Should().Be("RolledOut");

        var workerImageLatest = await service.GetImageTagAsync("stack.order/worker", "latest");
        var workerImageBuildTag = await service.GetImageTagAsync("stack.order/worker", "build.worker.v2");
        workerImageLatest.Should().NotBeNull();
        workerImageBuildTag.Should().NotBeNull();
        workerImageLatest!.Digest.Should().Be(buildSnapshot.ResultImageDigest);
        workerImageBuildTag!.Digest.Should().Be(buildSnapshot.ResultImageDigest);

        subscriber.Requests.Should().Contain(item => string.Equals(item.StackId, "stack.order", StringComparison.Ordinal) && string.Equals(item.ServiceName, "gateway", StringComparison.Ordinal));
        subscriber.Requests.Should().Contain(item => string.Equals(item.StackId, "stack.order", StringComparison.Ordinal) && string.Equals(item.ServiceName, "planner", StringComparison.Ordinal));
        subscriber.Requests.Should().NotContain(item => string.Equals(item.StackId, "stack.order", StringComparison.Ordinal) && string.Equals(item.ServiceName, "worker", StringComparison.Ordinal));

        publisher.Published.Should().NotBeEmpty();
        publisher.Published.Select(item => item.Envelope.Metadata["type_url"]).Should().Contain(typeUrl => typeUrl.Contains("ScriptBuildPublishedEvent", StringComparison.Ordinal));
        publisher.Published.Select(item => item.Envelope.Metadata["type_url"]).Should().Contain(typeUrl => typeUrl.Contains("ScriptComposeServiceRolledOutEvent", StringComparison.Ordinal));
    }

    private static (DynamicRuntimeApplicationService Service, IStateStore<ScriptServiceDefinitionState> ServiceStateStore) CreateService(
        IEventEnvelopeSubscriberPort? envelopeSubscriberPort = null,
        IEventEnvelopePublisherPort? envelopePublisherPort = null)
    {
        var runtime = new FakeActorRuntime();
        var store = new InMemoryDynamicRuntimeReadStore();
        var serviceStateStore = new InMemoryScriptServiceDefinitionStateStore();
        envelopeSubscriberPort ??= new InMemoryEventEnvelopeSubscriberPort();
        envelopePublisherPort ??= new InMemoryEventEnvelopePublisherPort();
        return (
            new DynamicRuntimeApplicationService(
                runtime,
                store,
                serviceStateStore,
                new InMemoryIdempotencyPort(),
                new InMemoryConcurrencyTokenPort(),
                new DefaultImageReferenceResolver(),
                new DefaultScriptComposeSpecValidator(),
                new DefaultScriptComposeReconcilePort(),
                new DefaultAgentBuildPlanPort(),
                new DefaultAgentBuildPolicyPort(),
                new DefaultAgentBuildExecutionPort(),
                new DefaultServiceModePolicyPort(),
                new DefaultBuildApprovalPort(),
                envelopePublisherPort,
                envelopeSubscriberPort,
                new InMemoryEventEnvelopeDedupPort(),
                new RoslynDynamicScriptExecutionService(
                    new DefaultScriptCompilationPolicy(),
                    new DefaultScriptAssemblyLoadPolicy(),
                    new DefaultScriptSandboxPolicy(),
                    new DefaultScriptResourceQuotaPolicy())),
            serviceStateStore);
    }

    private sealed class RecordingEnvelopePublisherPort : IEventEnvelopePublisherPort
    {
        public List<ScriptEventEnvelope> Published { get; } = [];

        public Task PublishAsync(ScriptEventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Published.Add(envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEnvelopeSubscriberPort : IEventEnvelopeSubscriberPort
    {
        public List<EnvelopeSubscribeRequest> Requests { get; } = [];

        public Task<EnvelopeLeaseResult> SubscribeAsync(EnvelopeSubscribeRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new EnvelopeLeaseResult(true, request.LeaseId));
        }
    }

    private sealed class FakeActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent
        {
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? Guid.NewGuid().ToString("N");
            var actor = new FakeActor(actorId);
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? Guid.NewGuid().ToString("N");
            var actor = new FakeActor(actorId);
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) => Task.FromResult(_actors.GetValueOrDefault(id));

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RestoreAllAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeActor : IActor
    {
        public FakeActor(string id)
        {
            Id = id;
            Agent = new FakeAgent();
        }

        public string Id { get; }
        public IAgent Agent { get; }

        public Task ActivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeAgent : IAgent
    {
        public string Id => string.Empty;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult("fake-agent");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
