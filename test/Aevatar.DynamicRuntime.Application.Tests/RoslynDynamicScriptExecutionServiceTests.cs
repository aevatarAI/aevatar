using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.DynamicRuntime.Infrastructure;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace Aevatar.DynamicRuntime.Application.Tests;

public sealed class RoslynDynamicScriptExecutionServiceTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldRunCSharpScriptEntrypoint()
    {
        var sut = new RoslynDynamicScriptExecutionService(
            new DefaultScriptCompilationPolicy(),
            new DefaultScriptAssemblyLoadPolicy(),
            new DefaultScriptSandboxPolicy(),
            new DefaultScriptResourceQuotaPolicy());

        var script = @"
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
        return Task.FromResult(new ScriptRoleExecutionResult($""echo:{text}""));
    }
}

var entrypoint = new ScriptEntrypoint();
";

        var result = await sut.ExecuteAsync(new DynamicScriptExecutionRequest(script, CreateExecutionEnvelope("""{"text":"hello"}""")));

        result.Success.Should().BeTrue(result.Error ?? "script should execute successfully");
        result.Output.Should().Be("""echo:{"text":"hello"}""");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldAllowScriptToCallInjectedLlmClient()
    {
        var llmClient = new ScriptRoleAgentChatClient(new FakeProviderFactory());
        var sut = new RoslynDynamicScriptExecutionService(
            new DefaultScriptCompilationPolicy(),
            new DefaultScriptAssemblyLoadPolicy(),
            new DefaultScriptSandboxPolicy(),
            new DefaultScriptResourceQuotaPolicy(),
            llmClient);

        var script = @"
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
        var llm = await ScriptRoleAgentContext.Current.ChatAsync(""classify:"" + text, systemPrompt: ""refund-router"", ct: ct);
        return new ScriptRoleExecutionResult(""llm=>"" + llm);
    }
}

var entrypoint = new ScriptEntrypoint();
";

        var result = await sut.ExecuteAsync(new DynamicScriptExecutionRequest(script, CreateExecutionEnvelope("""{"order_id":"42"}""")));

        result.Success.Should().BeTrue();
        result.Output.Should().Be("""llm=>provider:default|system:refund-router|prompt:classify:{"order_id":"42"}""");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPassStructuredInputToScriptEntrypoint()
    {
        var sut = new RoslynDynamicScriptExecutionService(
            new DefaultScriptCompilationPolicy(),
            new DefaultScriptAssemblyLoadPolicy(),
            new DefaultScriptSandboxPolicy(),
            new DefaultScriptResourceQuotaPolicy());

        var script = @"
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
        var source = envelope.Metadata.TryGetValue(""source"", out var from) ? from : string.Empty;
        var messageType = envelope.Metadata.TryGetValue(""message_type"", out var type) ? type : string.Empty;
        return Task.FromResult(new ScriptRoleExecutionResult($""text={text};source={source};type={messageType};correlation={envelope.CorrelationId}""));
    }
}

var entrypoint = new ScriptEntrypoint();
";

        var result = await sut.ExecuteAsync(new DynamicScriptExecutionRequest(
            script,
            CreateExecutionEnvelope("""{"text":"hello"}""", correlationId: "corr-42", causationId: "cause-42", messageType: "order.created", metadata: new Dictionary<string, string> { ["source"] = "api" })));

        result.Success.Should().BeTrue();
        result.Output.Should().Be("""text={"text":"hello"};source=api;type=order.created;correlation=corr-42""");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenStateSetTwice_ShouldFailFast()
    {
        var sut = new RoslynDynamicScriptExecutionService(
            new DefaultScriptCompilationPolicy(),
            new DefaultScriptAssemblyLoadPolicy(),
            new DefaultScriptSandboxPolicy(),
            new DefaultScriptResourceQuotaPolicy());

        var script = @"
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Google.Protobuf.WellKnownTypes;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public async Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        await ScriptRoleAgentContext.Current.SetStateAsync(new StringValue { Value = ""first"" }, ct);
        await ScriptRoleAgentContext.Current.SetStateAsync(new StringValue { Value = ""second"" }, ct);
        return new ScriptRoleExecutionResult(""ok"");
    }
}

var entrypoint = new ScriptEntrypoint();
";

        var result = await sut.ExecuteAsync(new DynamicScriptExecutionRequest(script, CreateExecutionEnvelope("""{"type":"state-double-set"}""")));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("State can be set at most once per script run.");
    }

    [Fact]
    public async Task ExecuteAsync_WhenRuntimeDefinesReadModel_ShouldFailFast()
    {
        var sut = new RoslynDynamicScriptExecutionService(
            new DefaultScriptCompilationPolicy(),
            new DefaultScriptAssemblyLoadPolicy(),
            new DefaultScriptSandboxPolicy(),
            new DefaultScriptResourceQuotaPolicy());

        var script = @"
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public async Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        await ScriptRoleAgentContext.Current.DefineReadModelAsync(
            ""orders"",
            ""order_id"",
            new Dictionary<string, string>
            {
                [""order_id""] = ""string"",
                [""status""] = ""string""
            },
            new[] { ""status"" },
            ct);
        return new ScriptRoleExecutionResult(""ok"");
    }
}

var entrypoint = new ScriptEntrypoint();
";

        var result = await sut.ExecuteAsync(new DynamicScriptExecutionRequest(script, CreateExecutionEnvelope("""{"type":"schema-forbidden"}""")));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("DefineReadModelAsync");
        result.Error.Should().Contain("CS1061");
    }

    [Fact]
    public async Task ExecuteAsync_WhenRuntimeDefinesReadModelRelation_ShouldFailFast()
    {
        var sut = new RoslynDynamicScriptExecutionService(
            new DefaultScriptCompilationPolicy(),
            new DefaultScriptAssemblyLoadPolicy(),
            new DefaultScriptSandboxPolicy(),
            new DefaultScriptResourceQuotaPolicy());

        var script = @"
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public async Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        await ScriptRoleAgentContext.Current.DefineReadModelRelationAsync(
            ""order_customer"",
            ""orders"",
            ""customers"",
            ""customer_id"",
            ""customer_id"",
            ct);
        return new ScriptRoleExecutionResult(""ok"");
    }
}

var entrypoint = new ScriptEntrypoint();
";

        var result = await sut.ExecuteAsync(new DynamicScriptExecutionRequest(script, CreateExecutionEnvelope("""{"type":"relation-forbidden"}""")));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("DefineReadModelRelationAsync");
        result.Error.Should().Contain("CS1061");
    }

    [Fact]
    public async Task ExecuteAsync_WhenScriptUsesRemovedReadModelApi_ShouldFailCompilation()
    {
        var sut = new RoslynDynamicScriptExecutionService(
            new DefaultScriptCompilationPolicy(),
            new DefaultScriptAssemblyLoadPolicy(),
            new DefaultScriptSandboxPolicy(),
            new DefaultScriptResourceQuotaPolicy());

        var script = @"
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public async Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        await ScriptRoleAgentContext.Current.DefineReadModelAsync(""orders"", ""order_id"", ct: ct);
        return new ScriptRoleExecutionResult(""should-not-compile"");
    }
}

var entrypoint = new ScriptEntrypoint();
";

        var result = await sut.ExecuteAsync(new DynamicScriptExecutionRequest(script, CreateExecutionEnvelope("""{"type":"schema-catch"}""")));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("DefineReadModelAsync");
        result.Error.Should().Contain("CS1061");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSupportRuntimeCapabilitiesInSingleScript()
    {
        var llmClient = new ScriptRoleAgentChatClient(new FakeProviderFactory());
        var sut = new RoslynDynamicScriptExecutionService(
            new DefaultScriptCompilationPolicy(),
            new DefaultScriptAssemblyLoadPolicy(),
            new DefaultScriptSandboxPolicy(),
            new DefaultScriptResourceQuotaPolicy(),
            llmClient);

        var script = @"
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Google.Protobuf.WellKnownTypes;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public async Task<ScriptRoleExecutionResult> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        var input = envelope.Payload.Is(StringValue.Descriptor)
            ? envelope.Payload.Unpack<StringValue>().Value
            : string.Empty;

        var stateAny = await ScriptRoleAgentContext.Current.GetStateAsync(ct);
        var previousState = stateAny != null && stateAny.Is(StringValue.Descriptor)
            ? stateAny.Unpack<StringValue>().Value
            : ""none"";

        var llm = await ScriptRoleAgentContext.Current.ChatAsync(""triage:"" + input, systemPrompt: ""runtime-capabilities"", ct: ct);

        var nextState = new Struct();
        nextState.Fields[""previous_state""] = Value.ForString(previousState);
        nextState.Fields[""last_order_id""] = Value.ForString(""O-1001"");
        await ScriptRoleAgentContext.Current.SetStateAsync(nextState, ct);

        await ScriptRoleAgentContext.Current.PublishAsync(
            new StringValue { Value = ""event:order-indexed"" },
            EventDirection.Both,
            new Dictionary<string, string>
            {
                [""script_event""] = ""orders_indexed"",
            },
            ct);

        return new ScriptRoleExecutionResult(""done|"" + llm + ""|"" + previousState);
    }
}

var entrypoint = new ScriptEntrypoint();
";

        var result = await sut.ExecuteAsync(new DynamicScriptExecutionRequest(
            script,
            CreateExecutionEnvelope("""{"order_id":"O-1001"}"""),
            CustomState: Any.Pack(new StringValue { Value = "seed-v1" })));

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
        result.Output.Should().Be("""done|provider:default|system:runtime-capabilities|prompt:triage:{"order_id":"O-1001"}|seed-v1""");

        result.PublishedEvents.Should().NotBeNull();
        result.PublishedEvents!.Should().ContainSingle();
        var published = result.PublishedEvents.Single();
        published.Direction.Should().Be(EventDirection.Both);
        published.Payload.Is(StringValue.Descriptor).Should().BeTrue();
        published.Payload.Unpack<StringValue>().Value.Should().Be("event:order-indexed");
        published.Metadata["script_event"].Should().Be("orders_indexed");

        result.CustomState.Should().NotBeNull();
        result.CustomState!.Is(Struct.Descriptor).Should().BeTrue();
        var state = result.CustomState.Unpack<Struct>();
        state.Fields["previous_state"].StringValue.Should().Be("seed-v1");
        state.Fields["last_order_id"].StringValue.Should().Be("O-1001");
    }

    private sealed class FakeProviderFactory : ILLMProviderFactory
    {
        private readonly ILLMProvider _provider = new FakeProvider("default");

        public ILLMProvider GetProvider(string name) => new FakeProvider(name);

        public ILLMProvider GetDefault() => _provider;

        public IReadOnlyList<string> GetAvailableProviders() => ["default"];
    }

    private sealed class FakeProvider : ILLMProvider
    {
        public FakeProvider(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var prompt = request.Messages.LastOrDefault(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;
            var system = request.Messages.FirstOrDefault(message => string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;
            return Task.FromResult(new LLMResponse
            {
                Content = $"provider:{Name}|system:{system}|prompt:{prompt}",
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
    }

    private static EventEnvelope CreateExecutionEnvelope(
        string text,
        string? correlationId = null,
        string? causationId = null,
        string? messageType = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var payload = Any.Pack(new StringValue { Value = text ?? string.Empty });
        var corr = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId;
        var cause = string.IsNullOrWhiteSpace(causationId) ? Guid.NewGuid().ToString("N") : causationId;

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = payload,
            PublisherId = "dynamic-runtime.test",
            Direction = EventDirection.Self,
            CorrelationId = corr,
            Metadata =
            {
                ["type_url"] = payload.TypeUrl,
                ["correlation_id"] = corr,
                ["causation_id"] = cause,
                ["message_type"] = messageType ?? string.Empty,
            },
        };

        if (metadata != null)
        {
            foreach (var pair in metadata)
                envelope.Metadata[pair.Key] = pair.Value;
        }

        return envelope;
    }
}
