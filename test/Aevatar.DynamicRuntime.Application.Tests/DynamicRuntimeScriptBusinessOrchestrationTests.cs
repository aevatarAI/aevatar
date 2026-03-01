using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.DynamicRuntime.Application;
using Aevatar.DynamicRuntime.Infrastructure;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using FluentAssertions;
using Any = Google.Protobuf.WellKnownTypes.Any;
using StringValue = Google.Protobuf.WellKnownTypes.StringValue;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;
using Xunit;

namespace Aevatar.DynamicRuntime.Application.Tests;

public sealed class DynamicRuntimeScriptBusinessOrchestrationTests
{
    [Theory]
    [InlineData("{\"ticket_id\":\"R-1001\",\"amount\":299,\"reason\":\"size_mismatch\",\"tier\":\"gold\"}", "auto_refund", "refund_approved")]
    [InlineData("{\"ticket_id\":\"R-1002\",\"amount\":1899,\"reason\":\"fraud_suspected\",\"tier\":\"silver\"}", "manual_review", "manual_review_required")]
    public async Task RefundBusiness_ShouldRunWithPureScripts_AndRoleAgentLlmCapability(
        string businessInput,
        string expectedDecision,
        string expectedAction)
    {
        var llmProviderFactory = new RefundWorkflowFakeProviderFactory();
        var (service, _, envelopeBusStateStore) = CreateService(llmProviderFactory);

        var intakeRegistered = await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.refund.intake",
                "v1",
                IntakeScript,
                "ScriptEntrypoint",
                DynamicServiceMode.Hybrid,
                ["https://refund.local/intake"],
                ["evt.refund.intake"],
                "cap:refund:intake:v1"),
            new DynamicCommandContext("idem-refund-register-intake"));
        var policyRegistered = await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.refund.policy",
                "v1",
                PolicyScript,
                "ScriptEntrypoint",
                DynamicServiceMode.Event,
                [],
                ["evt.refund.policy"],
                "cap:refund:policy:v1"),
            new DynamicCommandContext("idem-refund-register-policy"));
        var settlementRegistered = await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.refund.settlement",
                "v1",
                SettlementScript,
                "ScriptEntrypoint",
                DynamicServiceMode.Daemon,
                ["https://refund.local/settlement"],
                [],
                "cap:refund:settlement:v1"),
            new DynamicCommandContext("idem-refund-register-settlement"));
        var notifyRegistered = await service.RegisterServiceAsync(
            new RegisterServiceDefinitionRequest(
                "svc.refund.notify",
                "v1",
                NotifyScript,
                "ScriptEntrypoint",
                DynamicServiceMode.Event,
                [],
                ["evt.refund.notify"],
                "cap:refund:notify:v1"),
            new DynamicCommandContext("idem-refund-register-notify"));

        await service.ActivateServiceAsync("svc.refund.intake", new DynamicCommandContext("idem-refund-activate-intake", intakeRegistered.ETag));
        await service.ActivateServiceAsync("svc.refund.policy", new DynamicCommandContext("idem-refund-activate-policy", policyRegistered.ETag));
        await service.ActivateServiceAsync("svc.refund.settlement", new DynamicCommandContext("idem-refund-activate-settlement", settlementRegistered.ETag));
        await service.ActivateServiceAsync("svc.refund.notify", new DynamicCommandContext("idem-refund-activate-notify", notifyRegistered.ETag));

        var apply = await service.ApplyComposeAsync(
            new ComposeApplyYamlRequest(
                "stack.refund",
                "spec:refund:v1",
                """
services:
  intake:
    image: svc.refund.intake:v1
  policy:
    image: svc.refund.policy:v1
  settlement:
    image: svc.refund.settlement:v1
  notify:
    image: svc.refund.notify:v1
""",
                1,
                [
                    new ComposeServiceSpec("intake", "svc.refund.intake:v1", 1, DynamicServiceMode.Hybrid),
                    new ComposeServiceSpec("policy", "svc.refund.policy:v1", 0, DynamicServiceMode.Event),
                    new ComposeServiceSpec("settlement", "svc.refund.settlement:v1", 2, DynamicServiceMode.Daemon),
                    new ComposeServiceSpec("notify", "svc.refund.notify:v1", 0, DynamicServiceMode.Event),
                ]),
            new DynamicCommandContext("idem-refund-compose-apply", "0"));
        apply.Status.Should().Be("APPLIED");

        await service.CreateContainerAsync(
            new CreateContainerRequest("ctr.refund.intake.1", "stack.refund", "intake", "svc.refund.intake", "svc.refund.intake:v1", "role.refund.intake.1"),
            new DynamicCommandContext("idem-refund-container-create-intake-1"));
        await service.CreateContainerAsync(
            new CreateContainerRequest("ctr.refund.policy.1", "stack.refund", "policy", "svc.refund.policy", "svc.refund.policy:v1", "role.refund.policy.1"),
            new DynamicCommandContext("idem-refund-container-create-policy-1"));
        await service.CreateContainerAsync(
            new CreateContainerRequest("ctr.refund.settlement.1", "stack.refund", "settlement", "svc.refund.settlement", "svc.refund.settlement:v1", "role.refund.settlement.1"),
            new DynamicCommandContext("idem-refund-container-create-settlement-1"));
        await service.CreateContainerAsync(
            new CreateContainerRequest("ctr.refund.settlement.2", "stack.refund", "settlement", "svc.refund.settlement", "svc.refund.settlement:v1", "role.refund.settlement.2"),
            new DynamicCommandContext("idem-refund-container-create-settlement-2"));
        await service.CreateContainerAsync(
            new CreateContainerRequest("ctr.refund.notify.1", "stack.refund", "notify", "svc.refund.notify", "svc.refund.notify:v1", "role.refund.notify.1"),
            new DynamicCommandContext("idem-refund-container-create-notify-1"));

        await service.StartContainerAsync("ctr.refund.intake.1", new DynamicCommandContext("idem-refund-container-start-intake-1"));
        await service.StartContainerAsync("ctr.refund.policy.1", new DynamicCommandContext("idem-refund-container-start-policy-1"));
        await service.StartContainerAsync("ctr.refund.settlement.1", new DynamicCommandContext("idem-refund-container-start-settlement-1"));
        await service.StartContainerAsync("ctr.refund.settlement.2", new DynamicCommandContext("idem-refund-container-start-settlement-2"));
        await service.StartContainerAsync("ctr.refund.notify.1", new DynamicCommandContext("idem-refund-container-start-notify-1"));

        var intakeRun = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest("ctr.refund.intake.1", "svc.refund.intake", CreateJsonEnvelope(businessInput), "run.refund.intake"),
            new DynamicCommandContext("idem-refund-run-intake"));
        intakeRun.Status.Should().Be("SUCCEEDED");
        var intakeSnapshot = await service.GetRunAsync("run.refund.intake");
        intakeSnapshot.Should().NotBeNull();
        var intakeJson = ParseJson(intakeSnapshot!.Result);
        intakeJson.GetProperty("stage").GetString().Should().Be("intake");
        intakeJson.GetProperty("llm_result").GetString().Should().NotBeNullOrWhiteSpace();

        var policyRun = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest("ctr.refund.policy.1", "svc.refund.policy", CreateJsonEnvelope(intakeSnapshot.Result), "run.refund.policy"),
            new DynamicCommandContext("idem-refund-run-policy"));
        policyRun.Status.Should().Be("SUCCEEDED");
        var policySnapshot = await service.GetRunAsync("run.refund.policy");
        policySnapshot.Should().NotBeNull();
        var policyJson = ParseJson(policySnapshot!.Result);
        policyJson.GetProperty("stage").GetString().Should().Be("policy");
        policyJson.GetProperty("decision").GetString().Should().Be(expectedDecision);
        policyJson.GetProperty("llm_result").GetString().Should().NotBeNullOrWhiteSpace();

        var settlementContainerId = expectedDecision == "auto_refund"
            ? "ctr.refund.settlement.1"
            : "ctr.refund.settlement.2";
        var settlementRun = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest(settlementContainerId, "svc.refund.settlement", CreateJsonEnvelope(policySnapshot.Result), "run.refund.settlement"),
            new DynamicCommandContext("idem-refund-run-settlement"));
        settlementRun.Status.Should().Be("SUCCEEDED");
        var settlementSnapshot = await service.GetRunAsync("run.refund.settlement");
        settlementSnapshot.Should().NotBeNull();
        var settlementJson = ParseJson(settlementSnapshot!.Result);
        settlementJson.GetProperty("stage").GetString().Should().Be("settlement");
        settlementJson.GetProperty("action").GetString().Should().Be(expectedAction);
        settlementJson.GetProperty("llm_result").GetString().Should().NotBeNullOrWhiteSpace();

        var notifyRun = await service.ExecuteContainerAsync(
            new ExecuteContainerRequest("ctr.refund.notify.1", "svc.refund.notify", CreateJsonEnvelope(settlementSnapshot.Result), "run.refund.notify"),
            new DynamicCommandContext("idem-refund-run-notify"));
        notifyRun.Status.Should().Be("SUCCEEDED");
        var notifySnapshot = await service.GetRunAsync("run.refund.notify");
        notifySnapshot.Should().NotBeNull();
        var notifyJson = ParseJson(notifySnapshot!.Result);
        notifyJson.GetProperty("stage").GetString().Should().Be("notify");
        notifyJson.GetProperty("customer_message").GetString().Should().NotBeNullOrWhiteSpace();

        var settlementContainer1 = await service.GetContainerAsync("ctr.refund.settlement.1");
        var settlementContainer2 = await service.GetContainerAsync("ctr.refund.settlement.2");
        settlementContainer1.Should().NotBeNull();
        settlementContainer2.Should().NotBeNull();
        settlementContainer1!.ImageDigest.Should().StartWith("sha256:");
        settlementContainer2!.ImageDigest.Should().Be(settlementContainer1.ImageDigest);

        var leases = await GetEnvelopeLeasesAsync(envelopeBusStateStore);
        var stackSubscriptions = leases
            .Where(item => string.Equals(item.StackId, "stack.refund", StringComparison.Ordinal))
            .Select(item => item.ServiceName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        stackSubscriptions.Should().Contain("intake");
        stackSubscriptions.Should().Contain("policy");
        stackSubscriptions.Should().Contain("notify");
        stackSubscriptions.Should().NotContain("settlement");

        var published = await GetPublishedEnvelopesAsync(envelopeBusStateStore);
        published.Should().NotBeEmpty();
        published.Should().Contain(item =>
            string.Equals(item.StackId, "stack.refund", StringComparison.Ordinal) &&
            string.Equals(item.ServiceName, "intake", StringComparison.Ordinal));
        published.Any(item =>
            item.Envelope.Metadata.TryGetValue("type_url", out var typeUrl) &&
            typeUrl.EndsWith("ScriptRunCompletedEvent", StringComparison.Ordinal)).Should().BeTrue();
        published.Any(item =>
            item.Envelope.Metadata.TryGetValue("type_url", out var typeUrl) &&
            typeUrl.EndsWith("ScriptBuildApprovedEvent", StringComparison.Ordinal)).Should().BeTrue();
        llmProviderFactory.ChatCallCount.Should().BeGreaterThan(0);
    }

    private static JsonElement ParseJson(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static EventEnvelope CreateJsonEnvelope(string value)
    {
        var payload = Any.Pack(new StringValue { Value = value ?? string.Empty });
        var correlationId = Guid.NewGuid().ToString("N");
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = payload,
            PublisherId = "dynamic-runtime.test",
            Direction = EventDirection.Self,
            CorrelationId = correlationId,
            Metadata =
            {
                ["type_url"] = payload.TypeUrl,
                ["trace_id"] = Guid.NewGuid().ToString("N"),
                ["correlation_id"] = correlationId,
                ["causation_id"] = Guid.NewGuid().ToString("N"),
                ["dedup_key"] = $"{payload.TypeUrl}:{Guid.NewGuid():N}",
                ["occurred_at"] = DateTime.UtcNow.ToString("O"),
            },
        };
    }

    private static (
        DynamicRuntimeApplicationService Service,
        IStateStore<ScriptServiceDefinitionState> ServiceStateStore,
        IStateStore<ScriptEnvelopeBusState> EnvelopeBusStateStore) CreateService(
        ILLMProviderFactory? llmProviderFactory = null)
    {
        llmProviderFactory ??= new RefundWorkflowFakeProviderFactory();

        var runtime = new FakeActorRuntime();
        var readStore = new InMemoryDynamicRuntimeReadStore();
        var serviceStateStore = new TestStateStore<ScriptServiceDefinitionState>();
        var idempotencyStateStore = new TestStateStore<ScriptIdempotencyState>();
        var aggregateVersionStateStore = new TestStateStore<ScriptAggregateVersionState>();
        var envelopeBusStateStore = new TestStateStore<ScriptEnvelopeBusState>();
        var eventProjector = new DynamicRuntimeEventProjector(readStore);
        var sideEffectPlanner = new ScriptSideEffectPlanner();
        var chatClient = new ScriptRoleAgentChatClient(llmProviderFactory);
        var app = new DynamicRuntimeApplicationService(
            runtime,
            readStore,
            serviceStateStore,
            idempotencyStateStore,
            aggregateVersionStateStore,
            envelopeBusStateStore,
            new PassthroughEventDeduplicator(),
            new DefaultImageReferenceResolver(),
            new DefaultScriptComposeSpecValidator(),
            new DefaultScriptComposeReconcilePort(readStore),
            new DefaultAgentBuildPlanPort(),
            new DefaultAgentBuildPolicyPort(),
            new DefaultAgentBuildExecutionPort(),
            new DefaultServiceModePolicyPort(),
            new DefaultBuildApprovalPort(),
            new RoslynDynamicScriptExecutionService(
                new DefaultScriptCompilationPolicy(),
                new DefaultScriptAssemblyLoadPolicy(),
                new DefaultScriptSandboxPolicy(),
                new DefaultScriptResourceQuotaPolicy(),
                chatClient),
            sideEffectPlanner,
            eventProjector);
        return (app, serviceStateStore, envelopeBusStateStore);
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

    private sealed class RefundWorkflowFakeProviderFactory : ILLMProviderFactory
    {
        private readonly RefundWorkflowFakeProvider _defaultProvider;

        public RefundWorkflowFakeProviderFactory()
        {
            _defaultProvider = new RefundWorkflowFakeProvider("refund-default", this);
        }

        public int ChatCallCount { get; private set; }

        public ILLMProvider GetProvider(string name) => new RefundWorkflowFakeProvider(name, this);

        public ILLMProvider GetDefault() => _defaultProvider;

        public IReadOnlyList<string> GetAvailableProviders() => ["refund-default"];

        public void IncrementCalls() => ChatCallCount++;
    }

    private sealed class RefundWorkflowFakeProvider : ILLMProvider
    {
        private readonly RefundWorkflowFakeProviderFactory _factory;

        public RefundWorkflowFakeProvider(string name, RefundWorkflowFakeProviderFactory factory)
        {
            Name = name;
            _factory = factory;
        }

        public string Name { get; }

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _factory.IncrementCalls();

            var prompt = request.Messages.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;
            var content = ResolveContent(prompt);
            return Task.FromResult(new LLMResponse
            {
                Content = content,
            });
        }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(LLMRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var response = await ChatAsync(request, ct);
            yield return new LLMStreamChunk
            {
                DeltaContent = response.Content,
                IsLast = true,
            };
        }

        private static string ResolveContent(string prompt)
        {
            if (prompt.StartsWith("intake|", StringComparison.Ordinal))
            {
                var raw = prompt["intake|".Length..];
                var isHigh = raw.Contains("amount=1899", StringComparison.OrdinalIgnoreCase) ||
                             raw.Contains("\"amount\":1899", StringComparison.OrdinalIgnoreCase) ||
                             raw.Contains("\"reason\":\"fraud_suspected\"", StringComparison.OrdinalIgnoreCase) ||
                             raw.Contains("fraud", StringComparison.OrdinalIgnoreCase);
                return isHigh
                    ? "risk_hint=high;llm_result=llm_refund_risk_high"
                    : "risk_hint=low;llm_result=llm_refund_risk_low";
            }

            if (prompt.StartsWith("policy|", StringComparison.Ordinal))
            {
                var raw = prompt["policy|".Length..];
                var high = raw.Contains("risk_hint=high", StringComparison.OrdinalIgnoreCase) ||
                           raw.Contains("\"risk_hint\":\"high\"", StringComparison.OrdinalIgnoreCase);
                return high
                    ? "decision=manual_review;llm_result=llm_refund_manual_review"
                    : "decision=auto_refund;llm_result=llm_refund_auto_approve";
            }

            if (prompt.StartsWith("settlement|", StringComparison.Ordinal))
            {
                var raw = prompt["settlement|".Length..];
                var auto = raw.Contains("decision=auto_refund", StringComparison.OrdinalIgnoreCase) ||
                           raw.Contains("\"decision\":\"auto_refund\"", StringComparison.OrdinalIgnoreCase);
                return auto
                    ? "action=refund_approved;llm_result=llm_settlement_execute_refund"
                    : "action=manual_review_required;llm_result=llm_settlement_hold_for_manual";
            }

            if (prompt.StartsWith("notify|", StringComparison.Ordinal))
            {
                var raw = prompt["notify|".Length..];
                var approved = raw.Contains("action=refund_approved", StringComparison.OrdinalIgnoreCase) ||
                               raw.Contains("\"action\":\"refund_approved\"", StringComparison.OrdinalIgnoreCase);
                return approved
                    ? "customer_message=Your refund has been approved.;llm_result=llm_notify_positive"
                    : "customer_message=Your refund request requires manual review.;llm_result=llm_notify_waiting";
            }

            return "llm_result=unknown";
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
            _ = agentType;
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
            _ = parentId;
            _ = childId;
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = childId;
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
            _ = envelope;
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
            _ = envelope;
            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult("fake-agent");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

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

    private const string IntakeScript = """
using System;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Google.Protobuf.WellKnownTypes;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public async Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        var text = envelope.Payload.Is(StringValue.Descriptor)
            ? envelope.Payload.Unpack<StringValue>().Value
            : string.Empty;
        var llm = await ScriptRoleAgentContext.Current.ChatAsync("intake|" + text, systemPrompt: "refund-intake", ct: ct);
        var riskHint = ParseField(llm, "risk_hint");
        var llmResult = ParseField(llm, "llm_result");
        return new ScriptRoleExecutionResult("{\"stage\":\"intake\",\"llm_result\":\"" + Escape(llmResult) + "\",\"risk_hint\":\"" + Escape(riskHint) + "\",\"raw\":\"" + Escape(text) + "\"}");
    }

    private static string ParseField(string input, string key)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var segments = input.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var pair = segments[i].Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && string.Equals(pair[0], key, StringComparison.OrdinalIgnoreCase))
                return pair[1];
        }

        return string.Empty;
    }

    private static string Escape(string value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
}

var entrypoint = new ScriptEntrypoint();
""";

    private const string PolicyScript = """
using System;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Google.Protobuf.WellKnownTypes;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public async Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        var text = envelope.Payload.Is(StringValue.Descriptor)
            ? envelope.Payload.Unpack<StringValue>().Value
            : string.Empty;
        var llm = await ScriptRoleAgentContext.Current.ChatAsync("policy|" + text, systemPrompt: "refund-policy", ct: ct);
        var decision = ParseField(llm, "decision");
        var llmResult = ParseField(llm, "llm_result");
        return new ScriptRoleExecutionResult("{\"stage\":\"policy\",\"llm_result\":\"" + Escape(llmResult) + "\",\"decision\":\"" + Escape(decision) + "\",\"upstream\":\"" + Escape(text) + "\"}");
    }

    private static string ParseField(string input, string key)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;
        var segments = input.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var pair = segments[i].Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && string.Equals(pair[0], key, StringComparison.OrdinalIgnoreCase))
                return pair[1];
        }
        return string.Empty;
    }

    private static string Escape(string value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
}

var entrypoint = new ScriptEntrypoint();
""";

    private const string SettlementScript = """
using System;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Google.Protobuf.WellKnownTypes;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public async Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        var text = envelope.Payload.Is(StringValue.Descriptor)
            ? envelope.Payload.Unpack<StringValue>().Value
            : string.Empty;
        var llm = await ScriptRoleAgentContext.Current.ChatAsync("settlement|" + text, systemPrompt: "refund-settlement", ct: ct);
        var action = ParseField(llm, "action");
        var llmResult = ParseField(llm, "llm_result");
        return new ScriptRoleExecutionResult("{\"stage\":\"settlement\",\"llm_result\":\"" + Escape(llmResult) + "\",\"action\":\"" + Escape(action) + "\",\"upstream\":\"" + Escape(text) + "\"}");
    }

    private static string ParseField(string input, string key)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;
        var segments = input.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var pair = segments[i].Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && string.Equals(pair[0], key, StringComparison.OrdinalIgnoreCase))
                return pair[1];
        }
        return string.Empty;
    }

    private static string Escape(string value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
}

var entrypoint = new ScriptEntrypoint();
""";

    private const string NotifyScript = """
using System;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Google.Protobuf.WellKnownTypes;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public async Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        var text = envelope.Payload.Is(StringValue.Descriptor)
            ? envelope.Payload.Unpack<StringValue>().Value
            : string.Empty;
        var llm = await ScriptRoleAgentContext.Current.ChatAsync("notify|" + text, systemPrompt: "refund-notify", ct: ct);
        var message = ParseField(llm, "customer_message");
        var llmResult = ParseField(llm, "llm_result");
        await ScriptRoleAgentContext.Current.PublishAsync(new ScriptBuildApprovedEvent { BuildJobId = "script-notify-approved" }, ct: ct);
        return new ScriptRoleExecutionResult("{\"stage\":\"notify\",\"llm_result\":\"" + Escape(llmResult) + "\",\"customer_message\":\"" + Escape(message) + "\"}");
    }

    private static string ParseField(string input, string key)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;
        var segments = input.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var pair = segments[i].Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && string.Equals(pair[0], key, StringComparison.OrdinalIgnoreCase))
                return pair[1];
        }
        return string.Empty;
    }

    private static string Escape(string value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
}

var entrypoint = new ScriptEntrypoint();
""";
}
