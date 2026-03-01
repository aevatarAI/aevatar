using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.DynamicRuntime.Application;
using Aevatar.DynamicRuntime.Infrastructure;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using FluentAssertions;
using Any = Google.Protobuf.WellKnownTypes.Any;
using Struct = Google.Protobuf.WellKnownTypes.Struct;
using StringValue = Google.Protobuf.WellKnownTypes.StringValue;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;
using Xunit;

namespace Aevatar.DynamicRuntime.Application.Tests;

public sealed class DynamicRuntimeApplicationServiceTests
{
    [Fact]
    public async Task RegisterActivateAndExecute_ShouldPersistServiceStateAndRunScript()
    {
        var (service, serviceStateStore) = CreateService();

        var script = """
using System;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        var text = envelope.Payload.Is(StringValue.Descriptor)
            ? envelope.Payload.Unpack<StringValue>().Value
            : string.Empty;
        return Task.FromResult(new ScriptRoleExecutionResult($"echo:{text}"));
    }
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
            new ExecuteContainerRequest("container.echo.1", "svc.echo", CreateJsonEnvelope("""{"text":"hello"}""")),
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
        runSnapshot.Result.Should().Be("""echo:{"text":"hello"}""");

        state.Should().NotBeNull();
        state!.ScriptCode.Should().Contain("ScriptEntrypoint");
        state.Status.Should().Be(DynamicServiceStatus.Active.ToString());
    }

    [Fact]
    public async Task ExecuteContainer_ShouldInjectStructuredRunMetadataIntoScriptInput()
    {
        var (service, _) = CreateService();

        var script = """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        var text = envelope.Payload.Is(StringValue.Descriptor)
            ? envelope.Payload.Unpack<StringValue>().Value
            : string.Empty;
        var runId = envelope.Metadata.TryGetValue("run_id", out var run) ? run : string.Empty;
        var serviceId = envelope.Metadata.TryGetValue("service_id", out var service) ? service : string.Empty;
        var messageType = envelope.Metadata.TryGetValue("message_type", out var type) ? type : string.Empty;
        return Task.FromResult(new ScriptRoleExecutionResult($"{text}|{runId}|{serviceId}|{messageType}|{envelope.CorrelationId}"));
    }
}

var entrypoint = new ScriptEntrypoint();
""";

        await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.meta",
                "v1",
                script,
                "ScriptEntrypoint",
                DynamicServiceMode.Hybrid,
                [],
                [],
                "cap:meta"),
            new DynamicCommandContext("idem-meta-register"));
        await service.ActivateServiceAsync("svc.meta", new DynamicCommandContext("idem-meta-activate", "1"));
        await service.CreateContainerAsync(
            new CreateContainerRequest("container.meta.1", "stack.meta", "meta", "svc.meta", "sha256:meta", "role.meta.1"),
            new DynamicCommandContext("idem-meta-create"));
        await service.StartContainerAsync("container.meta.1", new DynamicCommandContext("idem-meta-start"));

        await service.ExecuteContainerAsync(
            new ExecuteContainerRequest("container.meta.1", "svc.meta", CreateJsonEnvelope("""{"text":"hello"}""", "run-meta"), "run-meta"),
            new DynamicCommandContext("idem-meta-exec"));

        var run = await service.GetRunAsync("run-meta");
        run.Should().NotBeNull();
        run!.Result.Should().Be("""{"text":"hello"}|run-meta|svc.meta||run-meta""");
    }

    [Fact]
    public async Task ExecuteContainer_WithRetryPolicy_ShouldSucceedAfterTransientFailure()
    {
        var (service, _) = CreateService();

        var script = """
using System;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        var attempt = envelope.Metadata.TryGetValue("run_attempt", out var value) ? value : "0";
        if (attempt == "1")
            throw new InvalidOperationException("transient");
        return Task.FromResult(new ScriptRoleExecutionResult("retry-ok"));
    }
}

var entrypoint = new ScriptEntrypoint();
""";

        await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.retry",
                "v1",
                script,
                "ScriptEntrypoint",
                DynamicServiceMode.Hybrid,
                [],
                [],
                "cap:retry"),
            new DynamicCommandContext("idem-retry-register"));
        await service.ActivateServiceAsync("svc.retry", new DynamicCommandContext("idem-retry-activate", "1"));
        await service.CreateContainerAsync(
            new CreateContainerRequest("container.retry.1", "stack.retry", "retry", "svc.retry", "sha256:retry", "role.retry.1"),
            new DynamicCommandContext("idem-retry-create"));
        await service.StartContainerAsync("container.retry.1", new DynamicCommandContext("idem-retry-start"));

        var execute = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest(
                "container.retry.1",
                "svc.retry",
                CreateJsonEnvelope("""{"ticket":"r1"}"""),
                TimeoutMs: 5_000,
                MaxRetries: 1,
                RetryBackoffMs: 10),
            new DynamicCommandContext("idem-retry-exec"));

        var run = await service.GetRunAsync(execute.AggregateId.Replace("dynamic:run:", string.Empty, StringComparison.Ordinal));
        execute.Status.Should().Be("SUCCEEDED");
        run.Should().NotBeNull();
        run!.Status.Should().Be("Succeeded");
        run.Result.Should().Be("retry-ok");
    }

    [Fact]
    public async Task ExecuteContainer_WithTimeoutPolicy_ShouldEmitTimedOutStatus()
    {
        var (service, _) = CreateService(scriptExecutionService: new BlockingScriptExecutionService());

        var script = """
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        => Task.FromResult(new ScriptRoleExecutionResult("noop"));
}

var entrypoint = new ScriptEntrypoint();
""";

        await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.timeout",
                "v1",
                script,
                "ScriptEntrypoint",
                DynamicServiceMode.Hybrid,
                [],
                [],
                "cap:timeout"),
            new DynamicCommandContext("idem-timeout-register"));
        await service.ActivateServiceAsync("svc.timeout", new DynamicCommandContext("idem-timeout-activate", "1"));
        await service.CreateContainerAsync(
            new CreateContainerRequest("container.timeout.1", "stack.timeout", "timeout", "svc.timeout", "sha256:timeout", "role.timeout.1"),
            new DynamicCommandContext("idem-timeout-create"));
        await service.StartContainerAsync("container.timeout.1", new DynamicCommandContext("idem-timeout-start"));

        var execute = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest(
                "container.timeout.1",
                "svc.timeout",
                CreateJsonEnvelope("""{"ticket":"t1"}"""),
                TimeoutMs: 50,
                MaxRetries: 0,
                RetryBackoffMs: 10),
            new DynamicCommandContext("idem-timeout-exec"));

        var run = await service.GetRunAsync(execute.AggregateId.Replace("dynamic:run:", string.Empty, StringComparison.Ordinal));
        execute.Status.Should().Be("TIMED_OUT");
        run.Should().NotBeNull();
        run!.Status.Should().Be("TimedOut");
    }

    [Fact]
    public async Task ExecuteContainer_ShouldDispatchScriptOutputEnvelopeToEventService()
    {
        var (service, _) = CreateService();

        var producerScript = """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Google.Protobuf.WellKnownTypes;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public async Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        await ScriptRoleAgentContext.Current.PublishAsync(
            new StringValue { Value = "ping" },
            ct: ct);
        return new ScriptRoleExecutionResult("producer");
    }
}

var entrypoint = new ScriptEntrypoint();
""";

        var consumerScript = """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Google.Protobuf.WellKnownTypes;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        var text = envelope.Payload.Is(StringValue.Descriptor)
            ? envelope.Payload.Unpack<StringValue>().Value
            : string.Empty;
        return Task.FromResult(new ScriptRoleExecutionResult($"consumer:{text}"));
    }
}

var entrypoint = new ScriptEntrypoint();
""";

        await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.dispatch.producer",
                "v1",
                producerScript,
                "ScriptEntrypoint",
                DynamicServiceMode.Hybrid,
                [],
                [],
                "cap:dispatch:producer"),
            new DynamicCommandContext("idem-dispatch-register-producer"));
        await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.dispatch.consumer",
                "v1",
                consumerScript,
                "ScriptEntrypoint",
                DynamicServiceMode.Event,
                [],
                ["evt.dispatch.consumer"],
                "cap:dispatch:consumer"),
            new DynamicCommandContext("idem-dispatch-register-consumer"));

        await service.ActivateServiceAsync("svc.dispatch.producer", new DynamicCommandContext("idem-dispatch-activate-producer", "1"));
        await service.ActivateServiceAsync("svc.dispatch.consumer", new DynamicCommandContext("idem-dispatch-activate-consumer", "1"));

        await service.CreateContainerAsync(
            new CreateContainerRequest("ctr.dispatch.producer.1", "stack.dispatch", "producer", "svc.dispatch.producer", "sha256:dispatch-producer", "role.dispatch.producer.1"),
            new DynamicCommandContext("idem-dispatch-create-producer"));
        await service.CreateContainerAsync(
            new CreateContainerRequest("ctr.dispatch.consumer.1", "stack.dispatch", "consumer", "svc.dispatch.consumer", "sha256:dispatch-consumer", "role.dispatch.consumer.1"),
            new DynamicCommandContext("idem-dispatch-create-consumer"));

        await service.StartContainerAsync("ctr.dispatch.producer.1", new DynamicCommandContext("idem-dispatch-start-producer"));
        await service.StartContainerAsync("ctr.dispatch.consumer.1", new DynamicCommandContext("idem-dispatch-start-consumer"));

        var producerRun = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest("ctr.dispatch.producer.1", "svc.dispatch.producer", CreateJsonEnvelope("""{"request":"dispatch"}""")),
            new DynamicCommandContext("idem-dispatch-exec-producer"));
        producerRun.Status.Should().Be("SUCCEEDED");

        var consumerRuns = await service.GetContainerRunsAsync("ctr.dispatch.consumer.1");
        consumerRuns.Should().NotBeEmpty();
        consumerRuns.Should().Contain(item => string.Equals(item.Status, "Succeeded", StringComparison.Ordinal));
        consumerRuns.Should().Contain(item => item.Result == "consumer:ping");
    }

    [Fact]
    public async Task ExecuteContainer_WhenScriptUsesRemovedReadModelApi_ShouldFailWithCompilationError()
    {
        var (service, _) = CreateService();

        var script = """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public async Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        await ScriptRoleAgentContext.Current.UpsertReadModelDocumentAsync("orders", "O-1001", new StringValue { Value = "forbidden" }, ct: ct);
        return new ScriptRoleExecutionResult("ok");
    }
}

var entrypoint = new ScriptEntrypoint();
""";

        await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.removed.readmodel.api",
                "v1",
                script,
                "ScriptEntrypoint",
                DynamicServiceMode.Event,
                [],
                ["event.removed.readmodel.api"],
                "cap:removed:readmodel:api"),
            new DynamicCommandContext("idem-removed-readmodel-api-register"));
        await service.ActivateServiceAsync("svc.removed.readmodel.api", new DynamicCommandContext("idem-removed-readmodel-api-activate", "1"));
        await service.CreateContainerAsync(
            new CreateContainerRequest("container.removed.readmodel.api", "stack.removed", "removed", "svc.removed.readmodel.api", "sha256:removed", "role.removed"),
            new DynamicCommandContext("idem-removed-readmodel-api-create"));
        await service.StartContainerAsync("container.removed.readmodel.api", new DynamicCommandContext("idem-removed-readmodel-api-start"));

        var exec = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest("container.removed.readmodel.api", "svc.removed.readmodel.api", CreateJsonEnvelope("""{"ticket":"x"}"""), "run-removed-readmodel-api"),
            new DynamicCommandContext("idem-removed-readmodel-api-exec"));

        var run = await service.GetRunAsync("run-removed-readmodel-api");

        exec.Status.Should().Be("FAILED");
        run.Should().NotBeNull();
        run!.Status.Should().Be("Failed");
        run.Error.Should().Contain("UpsertReadModelDocumentAsync");
        run.Error.Should().Contain("CS1061");
    }

    [Fact]
    public async Task ExecuteContainer_WhenScriptChangesCustomStateSchema_ShouldFailWithSchemaConflict()
    {
        var (service, _) = CreateService();

        var script = """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Google.Protobuf.WellKnownTypes;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public async Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        await ScriptRoleAgentContext.Current.SetStateAsync(new Int64Value { Value = 42 }, ct);
        return new ScriptRoleExecutionResult("ok");
    }
}

var entrypoint = new ScriptEntrypoint();
""";

        await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.state.schema",
                "v1",
                script,
                "ScriptEntrypoint",
                DynamicServiceMode.Event,
                [],
                ["event.state.schema"],
                "cap:state:schema",
                Any.Pack(new StringValue { Value = "state-v1" })),
            new DynamicCommandContext("idem-state-schema-register"));
        await service.ActivateServiceAsync("svc.state.schema", new DynamicCommandContext("idem-state-schema-activate", "1"));
        await service.CreateContainerAsync(
            new CreateContainerRequest("container.state.schema", "stack.state", "state", "svc.state.schema", "sha256:state", "role.state"),
            new DynamicCommandContext("idem-state-schema-create"));
        await service.StartContainerAsync("container.state.schema", new DynamicCommandContext("idem-state-schema-start"));

        var exec = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest("container.state.schema", "svc.state.schema", CreateJsonEnvelope("""{"ticket":"schema"}"""), "run-state-schema"),
            new DynamicCommandContext("idem-state-schema-exec"));

        var run = await service.GetRunAsync("run-state-schema");
        var serviceSnapshot = await service.GetServiceDefinitionAsync("svc.state.schema");

        exec.Status.Should().Be("FAILED");
        run.Should().NotBeNull();
        run!.Status.Should().Be("Failed");
        run.Error.Should().Contain("SCRIPT_STATE_SCHEMA_CONFLICT");
        serviceSnapshot.Should().NotBeNull();
        serviceSnapshot!.CustomState.Should().NotBeNull();
        serviceSnapshot.CustomState!.Is(StringValue.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteContainer_ShouldPersistAllScriptRuntimeCapabilitiesFromScript()
    {
        var (service, _, envelopeBusStateStore) = CreateServiceWithDiagnostics();

        var script = """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Google.Protobuf.WellKnownTypes;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public async Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        var stateAny = await ScriptRoleAgentContext.Current.GetStateAsync(ct);
        var previousState = stateAny != null && stateAny.Is(Struct.Descriptor)
            ? (stateAny.Unpack<Struct>().Fields.TryGetValue("seed", out var seed) ? seed.StringValue : "none")
            : "none";

        var nextState = new Struct();
        nextState.Fields["previous_state"] = Value.ForString(previousState);
        nextState.Fields["last_order_id"] = Value.ForString("O-2001");
        await ScriptRoleAgentContext.Current.SetStateAsync(nextState, ct);

        await ScriptRoleAgentContext.Current.PublishAsync(
            new StringValue { Value = "event:orders-indexed" },
            EventDirection.Both,
            new Dictionary<string, string>
            {
                ["script_event"] = "orders_indexed",
            },
            ct);

        return new ScriptRoleExecutionResult("ok");
    }
}

var entrypoint = new ScriptEntrypoint();
""";

        await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.all.capabilities",
                "v1",
                script,
                "ScriptEntrypoint",
                DynamicServiceMode.Event,
                [],
                ["event.all.capabilities"],
                "cap:all:capabilities",
                Any.Pack(new Struct
                {
                    Fields =
                    {
                        ["seed"] = new Google.Protobuf.WellKnownTypes.Value { StringValue = "seed-v1" },
                    },
                })),
            new DynamicCommandContext("idem-all-cap-register"));
        await service.ActivateServiceAsync("svc.all.capabilities", new DynamicCommandContext("idem-all-cap-activate", "1"));
        await service.CreateContainerAsync(
            new CreateContainerRequest("container.all.capabilities", "stack.all", "all", "svc.all.capabilities", "sha256:all", "role.all"),
            new DynamicCommandContext("idem-all-cap-create"));
        await service.StartContainerAsync("container.all.capabilities", new DynamicCommandContext("idem-all-cap-start"));

        var exec = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest("container.all.capabilities", "svc.all.capabilities", CreateJsonEnvelope("""{"order":"O-2001"}"""), "run-all-capabilities"),
            new DynamicCommandContext("idem-all-cap-exec"));

        var run = await service.GetRunAsync("run-all-capabilities");
        var serviceSnapshot = await service.GetServiceDefinitionAsync("svc.all.capabilities");

        exec.Status.Should().Be("SUCCEEDED");
        run.Should().NotBeNull();
        run!.Status.Should().Be("Succeeded");

        serviceSnapshot.Should().NotBeNull();
        serviceSnapshot!.CustomState.Should().NotBeNull();
        serviceSnapshot.CustomState!.Is(Struct.Descriptor).Should().BeTrue();
        var customState = serviceSnapshot.CustomState.Unpack<Struct>();
        customState.Fields["previous_state"].StringValue.Should().Be("seed-v1");
        customState.Fields["last_order_id"].StringValue.Should().Be("O-2001");

        var published = await GetPublishedEnvelopesAsync(envelopeBusStateStore);
        published.Any(item =>
            item.Envelope.Metadata.TryGetValue("script_event", out var scriptEvent) &&
            string.Equals(scriptEvent, "orders_indexed", StringComparison.Ordinal))
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task ExecuteContainer_WhenScriptDefinesReadModelAtRuntime_ShouldFail()
    {
        var (service, _) = CreateService();

        var script = """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public async Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        await ScriptRoleAgentContext.Current.DefineReadModelAsync(
            "orders",
            "order_id",
            new Dictionary<string, string>
            {
                ["order_id"] = "string",
                ["status"] = "string",
            },
            new[] { "status" },
            ct);
        return new ScriptRoleExecutionResult("ok");
    }
}

var entrypoint = new ScriptEntrypoint();
""";

        await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.schema.forbidden",
                "v1",
                script,
                "ScriptEntrypoint",
                DynamicServiceMode.Event,
                [],
                ["event.schema.forbidden"],
                "cap:schema:forbidden"),
            new DynamicCommandContext("idem-schema-forbidden-register"));
        await service.ActivateServiceAsync("svc.schema.forbidden", new DynamicCommandContext("idem-schema-forbidden-activate", "1"));
        await service.CreateContainerAsync(
            new CreateContainerRequest("container.schema.forbidden", "stack.schema", "schema", "svc.schema.forbidden", "sha256:schema", "role.schema"),
            new DynamicCommandContext("idem-schema-forbidden-create"));
        await service.StartContainerAsync("container.schema.forbidden", new DynamicCommandContext("idem-schema-forbidden-start"));

        var exec = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest("container.schema.forbidden", "svc.schema.forbidden", CreateJsonEnvelope("""{"ticket":"schema"}"""), "run-schema-forbidden"),
            new DynamicCommandContext("idem-schema-forbidden-exec"));
        var run = await service.GetRunAsync("run-schema-forbidden");

        exec.Status.Should().Be("FAILED");
        run.Should().NotBeNull();
        run!.Status.Should().Be("Failed");
        run.Error.Should().Contain("DefineReadModelAsync");
        run.Error.Should().Contain("CS1061");
    }

    [Fact]
    public async Task ExecuteContainer_WhenScriptDefinesReadModelRelationAtRuntime_ShouldFail()
    {
        var (service, _) = CreateService();

        var script = """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public async Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        await ScriptRoleAgentContext.Current.DefineReadModelRelationAsync(
            "order_customer",
            "orders",
            "customers",
            "customer_id",
            "customer_id",
            ct);
        return new ScriptRoleExecutionResult("ok");
    }
}

var entrypoint = new ScriptEntrypoint();
""";

        await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.relation.forbidden",
                "v1",
                script,
                "ScriptEntrypoint",
                DynamicServiceMode.Event,
                [],
                ["event.relation.forbidden"],
                "cap:relation:forbidden"),
            new DynamicCommandContext("idem-relation-forbidden-register"));
        await service.ActivateServiceAsync("svc.relation.forbidden", new DynamicCommandContext("idem-relation-forbidden-activate", "1"));
        await service.CreateContainerAsync(
            new CreateContainerRequest("container.relation.forbidden", "stack.relation", "relation", "svc.relation.forbidden", "sha256:relation", "role.relation"),
            new DynamicCommandContext("idem-relation-forbidden-create"));
        await service.StartContainerAsync("container.relation.forbidden", new DynamicCommandContext("idem-relation-forbidden-start"));

        var exec = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest("container.relation.forbidden", "svc.relation.forbidden", CreateJsonEnvelope("""{"ticket":"relation"}"""), "run-relation-forbidden"),
            new DynamicCommandContext("idem-relation-forbidden-exec"));
        var run = await service.GetRunAsync("run-relation-forbidden");

        exec.Status.Should().Be("FAILED");
        run.Should().NotBeNull();
        run!.Status.Should().Be("Failed");
        run.Error.Should().Contain("DefineReadModelRelationAsync");
        run.Error.Should().Contain("CS1061");
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
    public Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.FromResult(new ScriptRoleExecutionResult("ok"));
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
            new ExecuteContainerRequest("container.cancel", "svc.cancel", CreateJsonEnvelope("""{"command":"cancel"}"""), "run-cancel"),
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
        var (service, _, envelopeBusStateStore) = CreateServiceWithDiagnostics();

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

        var leases = await GetEnvelopeLeasesAsync(envelopeBusStateStore);
        leases.Should().HaveCount(2);
        leases.Select(item => item.ServiceName).Should().BeEquivalentTo(["svc-event", "svc-hybrid"]);
    }

    [Fact]
    public async Task ActivateService_EventMode_ShouldSubscribeEnvelopeLease()
    {
        var (service, _, envelopeBusStateStore) = CreateServiceWithDiagnostics();

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
    public Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        var text = envelope.Payload.Is(StringValue.Descriptor)
            ? envelope.Payload.Unpack<StringValue>().Value
            : string.Empty;
        return Task.FromResult(new ScriptRoleExecutionResult(text));
    }
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

        var leases = await GetEnvelopeLeasesAsync(envelopeBusStateStore);
        leases.Should().ContainSingle(item =>
            string.Equals(item.StackId, "_services", StringComparison.Ordinal) &&
            string.Equals(item.ServiceName, "svc.subscription", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MultiAgentBusinessSimulation_ShouldAlignDockerLikeSemantics()
    {
        var (service, _, envelopeBusStateStore) = CreateServiceWithDiagnostics();

        var gatewayScript = """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        var text = envelope.Payload.Is(StringValue.Descriptor)
            ? envelope.Payload.Unpack<StringValue>().Value
            : string.Empty;
        return Task.FromResult(new ScriptRoleExecutionResult($"{{\"agent\":\"gateway\",\"llm\":\"intent_parsed\",\"input\":{text}}}"));
    }
}
var entrypoint = new ScriptEntrypoint();
""";

        var plannerScript = """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        var text = envelope.Payload.Is(StringValue.Descriptor)
            ? envelope.Payload.Unpack<StringValue>().Value
            : string.Empty;
        return Task.FromResult(new ScriptRoleExecutionResult($"{{\"agent\":\"planner\",\"llm\":\"plan_created\",\"upstream\":{text}}}"));
    }
}
var entrypoint = new ScriptEntrypoint();
""";

        var workerScript = """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        var text = envelope.Payload.Is(StringValue.Descriptor)
            ? envelope.Payload.Unpack<StringValue>().Value
            : string.Empty;
        return Task.FromResult(new ScriptRoleExecutionResult($"{{\"agent\":\"worker\",\"llm\":\"task_executed\",\"payload\":{text}}}"));
    }
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
            new ExecuteContainerRequest("ctr.gateway.1", "svc.gateway", CreateJsonEnvelope("""{"order_id":"1001","user":"alice"}""")),
            new DynamicCommandContext("idem-run-gateway"));
        var gatewaySnapshot = await service.GetRunAsync(gatewayRun.AggregateId.Replace("dynamic:run:", string.Empty, StringComparison.Ordinal));
        gatewaySnapshot.Should().NotBeNull();
        gatewaySnapshot!.Status.Should().Be("Succeeded");
        gatewaySnapshot.Result.Should().Contain("\"agent\":\"gateway\"");

        var plannerRun = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest("ctr.planner.1", "svc.planner", CreateJsonEnvelope(gatewaySnapshot.Result)),
            new DynamicCommandContext("idem-run-planner"));
        var plannerSnapshot = await service.GetRunAsync(plannerRun.AggregateId.Replace("dynamic:run:", string.Empty, StringComparison.Ordinal));
        plannerSnapshot.Should().NotBeNull();
        plannerSnapshot!.Status.Should().Be("Succeeded");
        plannerSnapshot.Result.Should().Contain("\"agent\":\"planner\"");

        var workerRun1 = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest("ctr.worker.1", "svc.worker", CreateJsonEnvelope(plannerSnapshot.Result)),
            new DynamicCommandContext("idem-run-worker-1"));
        var workerRun2 = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest("ctr.worker.2", "svc.worker", CreateJsonEnvelope(plannerSnapshot.Result)),
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

        var leases = await GetEnvelopeLeasesAsync(envelopeBusStateStore);
        leases.Should().Contain(item => string.Equals(item.StackId, "stack.order", StringComparison.Ordinal) && string.Equals(item.ServiceName, "gateway", StringComparison.Ordinal));
        leases.Should().Contain(item => string.Equals(item.StackId, "stack.order", StringComparison.Ordinal) && string.Equals(item.ServiceName, "planner", StringComparison.Ordinal));
        leases.Should().NotContain(item => string.Equals(item.StackId, "stack.order", StringComparison.Ordinal) && string.Equals(item.ServiceName, "worker", StringComparison.Ordinal));

        var published = await GetPublishedEnvelopesAsync(envelopeBusStateStore);
        published.Should().NotBeEmpty();
        published.Select(item => item.Envelope.Metadata["type_url"]).Should().Contain(typeUrl => typeUrl.Contains("ScriptBuildPublishedEvent", StringComparison.Ordinal));
        published.Select(item => item.Envelope.Metadata["type_url"]).Should().Contain(typeUrl => typeUrl.Contains("ScriptComposeServiceRolledOutEvent", StringComparison.Ordinal));
    }

    private static EventEnvelope CreateJsonEnvelope(string value, string? correlationId = null)
    {
        var payload = Any.Pack(new StringValue { Value = value ?? string.Empty });
        var resolvedCorrelationId = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString("N")
            : correlationId;
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = payload,
            PublisherId = "dynamic-runtime.test",
            Direction = EventDirection.Self,
            CorrelationId = resolvedCorrelationId,
            Metadata =
            {
                ["type_url"] = payload.TypeUrl,
                ["trace_id"] = Guid.NewGuid().ToString("N"),
                ["correlation_id"] = resolvedCorrelationId,
                ["causation_id"] = Guid.NewGuid().ToString("N"),
                ["dedup_key"] = $"{payload.TypeUrl}:{Guid.NewGuid():N}",
                ["occurred_at"] = DateTime.UtcNow.ToString("O"),
            },
        };
    }

    private static (DynamicRuntimeApplicationService Service, IStateStore<ScriptServiceDefinitionState> ServiceStateStore) CreateService(
        IDynamicScriptExecutionService? scriptExecutionService = null)
    {
        var (service, serviceStateStore, _, _) = CreateServiceCore(scriptExecutionService);
        return (service, serviceStateStore);
    }

    private static (
        DynamicRuntimeApplicationService Service,
        IStateStore<ScriptServiceDefinitionState> ServiceStateStore,
        IStateStore<ScriptEnvelopeBusState> EnvelopeBusStateStore) CreateServiceWithDiagnostics(
        IDynamicScriptExecutionService? scriptExecutionService = null)
    {
        var (service, serviceStateStore, _, envelopeBusStateStore) = CreateServiceCore(scriptExecutionService);
        return (service, serviceStateStore, envelopeBusStateStore);
    }

    private static (
        DynamicRuntimeApplicationService Service,
        IStateStore<ScriptServiceDefinitionState> ServiceStateStore,
        InMemoryDynamicRuntimeReadStore ReadStore) CreateServiceWithReadStore(
        IDynamicScriptExecutionService? scriptExecutionService = null)
    {
        var (service, serviceStateStore, readStore, _) = CreateServiceCore(scriptExecutionService);
        return (service, serviceStateStore, readStore);
    }

    private static (
        DynamicRuntimeApplicationService Service,
        IStateStore<ScriptServiceDefinitionState> ServiceStateStore,
        InMemoryDynamicRuntimeReadStore ReadStore,
        TestStateStore<ScriptEnvelopeBusState> EnvelopeBusStateStore) CreateServiceCore(
        IDynamicScriptExecutionService? scriptExecutionService = null)
    {
        var runtime = new FakeActorRuntime();
        var store = new InMemoryDynamicRuntimeReadStore();
        var serviceStateStore = new TestStateStore<ScriptServiceDefinitionState>();
        var idempotencyStateStore = new TestStateStore<ScriptIdempotencyState>();
        var aggregateVersionStateStore = new TestStateStore<ScriptAggregateVersionState>();
        var envelopeBusStateStore = new TestStateStore<ScriptEnvelopeBusState>();

        scriptExecutionService ??= new RoslynDynamicScriptExecutionService(
            new DefaultScriptCompilationPolicy(),
            new DefaultScriptAssemblyLoadPolicy(),
            new DefaultScriptSandboxPolicy(),
            new DefaultScriptResourceQuotaPolicy());
        var eventProjector = new DynamicRuntimeEventProjector(store);
        var sideEffectPlanner = new ScriptSideEffectPlanner();
        var service = new DynamicRuntimeApplicationService(
            runtime,
            store,
            serviceStateStore,
            idempotencyStateStore,
            aggregateVersionStateStore,
            envelopeBusStateStore,
            new PassthroughEventDeduplicator(),
            new DefaultImageReferenceResolver(),
            new DefaultScriptComposeSpecValidator(),
            new DefaultScriptComposeReconcilePort(store),
            new DefaultAgentBuildPlanPort(),
            new DefaultAgentBuildPolicyPort(),
            new DefaultAgentBuildExecutionPort(),
            new DefaultServiceModePolicyPort(),
            new DefaultBuildApprovalPort(),
            scriptExecutionService,
            sideEffectPlanner,
            eventProjector);

        return (service, serviceStateStore, store, envelopeBusStateStore);
    }

    private static async Task<IReadOnlyList<EnvelopeSubscribeRequest>> GetEnvelopeLeasesAsync(
        IStateStore<ScriptEnvelopeBusState> envelopeBusStateStore)
    {
        var state = await envelopeBusStateStore.LoadAsync("dynamic-runtime:envelope-bus")
            ?? new ScriptEnvelopeBusState();
        return state.Leases.Values
            .Select(item => new EnvelopeSubscribeRequest(
                item.StackId,
                item.ServiceName,
                item.SubscriberId,
                item.LeaseId,
                item.MaxInFlight))
            .ToArray();
    }

    private static async Task<IReadOnlyList<ScriptEventEnvelope>> GetPublishedEnvelopesAsync(
        IStateStore<ScriptEnvelopeBusState> envelopeBusStateStore)
    {
        var state = await envelopeBusStateStore.LoadAsync("dynamic-runtime:envelope-bus")
            ?? new ScriptEnvelopeBusState();
        return state.Envelopes
            .OrderBy(item => item.Key, Comparer<long>.Default)
            .Select(item => TryHydrateEnvelope(item.Value))
            .Where(item => item != null)
            .Cast<ScriptEventEnvelope>()
            .ToArray();
    }

    private static ScriptEventEnvelope? TryHydrateEnvelope(ScriptEventEnvelopeState state)
    {
        if (state == null || state.Envelope == null || !state.Envelope.Is(EventEnvelope.Descriptor))
            return null;

        return new ScriptEventEnvelope(
            state.EnvelopeId,
            state.StackId,
            state.ServiceName,
            state.InstanceSelector,
            state.Envelope.Unpack<EventEnvelope>());
    }

    private sealed class BlockingScriptExecutionService : IDynamicScriptExecutionService
    {
        public async Task<DynamicScriptExecutionResult> ExecuteAsync(DynamicScriptExecutionRequest request, CancellationToken ct = default)
        {
            _ = request;
            var wait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => wait.TrySetCanceled(ct));
            await wait.Task;
            return new DynamicScriptExecutionResult(false, string.Empty, Error: "UNREACHABLE");
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
