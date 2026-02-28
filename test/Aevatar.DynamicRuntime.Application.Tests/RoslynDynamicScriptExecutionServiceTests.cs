using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.DynamicRuntime.Infrastructure;
using Aevatar.AI.Abstractions.LLMProviders;
using FluentAssertions;
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

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public Task<string> HandleAsync(ScriptRoleRequest input, CancellationToken ct = default)
        => Task.FromResult($""echo:{input.Text}"");
}

var entrypoint = new ScriptEntrypoint();
";

        var result = await sut.ExecuteAsync(new DynamicScriptExecutionRequest(script, "hello"));

        result.Success.Should().BeTrue();
        result.Output.Should().Be("echo:hello");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldAllowScriptToCallInjectedLlmClient()
    {
        var llmClient = new ScriptRoleAgentLlmClient(new FakeProviderFactory());
        var capabilities = new ScriptRoleAgentCapabilities(llmClient);
        var sut = new RoslynDynamicScriptExecutionService(
            new DefaultScriptCompilationPolicy(),
            new DefaultScriptAssemblyLoadPolicy(),
            new DefaultScriptSandboxPolicy(),
            new DefaultScriptResourceQuotaPolicy(),
            capabilities);

        var script = @"
using System.Threading;
using System.Threading.Tasks;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public async Task<string> HandleAsync(ScriptRoleRequest input, CancellationToken ct = default)
    {
        var llm = await ScriptRoleAgentContext.Current.RoleAgent.ChatAsync(""classify:"" + input.Text, systemPrompt: ""refund-router"", ct: ct);
        return ""llm=>"" + llm;
    }
}

var entrypoint = new ScriptEntrypoint();
";

        var result = await sut.ExecuteAsync(new DynamicScriptExecutionRequest(script, "order-42"));

        result.Success.Should().BeTrue();
        result.Output.Should().Be("llm=>provider:default|system:refund-router|prompt:classify:order-42");
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

public sealed class ScriptEntrypoint : IScriptRoleEntrypoint
{
    public Task<string> HandleAsync(ScriptRoleRequest input, CancellationToken ct = default)
    {
        var source = input.Metadata != null && input.Metadata.TryGetValue(""source"", out var from) ? from : string.Empty;
        return Task.FromResult($""text={input.Text};json={input.Json};source={source};type={input.MessageType};correlation={input.CorrelationId}"");
    }
}

var entrypoint = new ScriptEntrypoint();
";

        var input = new ScriptRoleRequest(
            "hello",
            "{\"orderId\":42}",
            new Dictionary<string, string> { ["source"] = "api" },
            CorrelationId: "corr-42",
            CausationId: "cause-42",
            MessageType: "order.created");
        var result = await sut.ExecuteAsync(new DynamicScriptExecutionRequest(script, input));

        result.Success.Should().BeTrue();
        result.Output.Should().Be("text=hello;json={\"orderId\":42};source=api;type=order.created;correlation=corr-42");
        result.Error.Should().BeNull();
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
}
