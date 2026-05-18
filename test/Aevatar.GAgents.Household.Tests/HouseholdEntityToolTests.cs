using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace Aevatar.GAgents.Household.Tests;

public class HouseholdEntityToolMetadataTests
{
    [Fact]
    public void Name_returns_household()
    {
        var tool = CreateTool();
        tool.Name.Should().Be("household");
    }

    [Fact]
    public void Description_mentions_home_automation()
    {
        var tool = CreateTool();
        tool.Description.Should().Contain("home automation");
    }

    [Fact]
    public void ParametersSchema_is_valid_json()
    {
        var tool = CreateTool();
        var action = () => JsonDocument.Parse(tool.ParametersSchema);
        action.Should().NotThrow();
    }

    [Fact]
    public void ParametersSchema_requires_message()
    {
        var tool = CreateTool();
        using var doc = JsonDocument.Parse(tool.ParametersSchema);
        var required = doc.RootElement.GetProperty("required");
        required.EnumerateArray().Should().Contain(e => e.GetString() == "message");
    }

    [Fact]
    public void ParametersSchema_has_household_id_optional()
    {
        var tool = CreateTool();
        using var doc = JsonDocument.Parse(tool.ParametersSchema);
        var props = doc.RootElement.GetProperty("properties");
        props.TryGetProperty("household_id", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_returns_error_when_message_missing()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("""{"household_id":"test"}""");
        result.Should().Contain("error");
        result.Should().Contain("message");
    }

    [Fact]
    public async Task ExecuteAsync_returns_error_for_invalid_json()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("not json");
        result.Should().Contain("error");
    }

    [Fact]
    public async Task ExecuteAsync_dispatches_household_chat_and_returns_accepted_receipt()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var tool = CreateTool(runtime, dispatchPort);

        var result = await tool.ExecuteAsync("""{"message":"turn on warm lights","household_id":"home-1"}""");

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("accepted");
        root.GetProperty("actor_id").GetString().Should().Be("home-1");
        root.GetProperty("message_id").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("propagation").GetString().Should().Contain("accepted_for_dispatch");

        runtime.CreatedActorId.Should().Be("home-1");
        dispatchPort.ActorId.Should().Be("home-1");
        dispatchPort.Envelope.Should().NotBeNull();
        dispatchPort.Envelope!.Payload.Is(HouseholdChatEvent.Descriptor).Should().BeTrue();
        dispatchPort.Envelope.Payload.Unpack<HouseholdChatEvent>().Prompt.Should().Be("turn on warm lights");
        dispatchPort.Envelope.Route.GetTargetActorId().Should().Be("home-1");
    }

    [Fact]
    public void Production_tool_does_not_directly_invoke_or_read_household_actor_state()
    {
        var source = File.ReadAllText(FindRepoFile(
            "agents/Aevatar.GAgents.Household/HouseholdEntityTool.cs"));

        source.Should().NotContain("HandleEventAsync(");
        source.Should().NotContain("actor.Agent");
        source.Should().NotContain("IAgent<HouseholdEntityState>");
    }

    [Fact]
    public void Production_household_reasoning_uses_streaming_chat_chain()
    {
        var source = File.ReadAllText(FindRepoFile(
            "agents/Aevatar.GAgents.Household/HouseholdEntity.cs"));

        source.Should().NotContain("ChatAsync(");
    }

    // Helper — creates tool with null runtime (will fail on dispatch but metadata tests pass)
    private static HouseholdEntityTool CreateTool() =>
        new(null!, null!, new HouseholdEntityToolOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    private static HouseholdEntityTool CreateTool(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort) =>
        new(runtime, dispatchPort, new HouseholdEntityToolOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relativePath} from {AppContext.BaseDirectory}");
    }
}

public class HouseholdEntityToolSourceTests
{
    [Fact]
    public async Task DiscoverToolsAsync_returns_household_tool()
    {
        var source = new HouseholdEntityToolSource(
            null!, // runtime not needed for discovery
            null!, // dispatch port not needed for discovery
            new HouseholdEntityToolOptions());

        var tools = await source.DiscoverToolsAsync();

        tools.Should().HaveCount(1);
        tools[0].Should().BeOfType<HouseholdEntityTool>();
        tools[0].Name.Should().Be("household");
    }
}

internal sealed class RecordingActorRuntime : IActorRuntime
{
    public string? CreatedActorId { get; private set; }

    public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
        where TAgent : IAgent
    {
        CreatedActorId = id;
        return Task.FromResult<IActor>(new RecordingActor(id ?? Guid.NewGuid().ToString("N")));
    }

    public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
    {
        CreatedActorId = id;
        return Task.FromResult<IActor>(new RecordingActor(id ?? Guid.NewGuid().ToString("N")));
    }

    public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

    public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

    public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

    public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class RecordingActorDispatchPort : IActorDispatchPort
{
    public string? ActorId { get; private set; }
    public EventEnvelope? Envelope { get; private set; }

    public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
    {
        ActorId = actorId;
        Envelope = envelope;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingActor(string id) : IActor
{
    public string Id { get; } = id;
    public IAgent Agent { get; } = new RecordingAgent(id);

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
    public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
}

internal sealed class RecordingAgent(string id) : IAgent
{
    public string Id { get; } = id;

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> GetDescriptionAsync() => Task.FromResult(Id);
    public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
        Task.FromResult<IReadOnlyList<System.Type>>([]);
    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public class HouseholdEntityToolOptionsTests
{
    [Fact]
    public void Default_prefix_is_household()
    {
        var options = new HouseholdEntityToolOptions();
        options.ActorIdPrefix.Should().Be("household");
    }

    [Fact]
    public void Prefix_can_be_customized()
    {
        var options = new HouseholdEntityToolOptions { ActorIdPrefix = "home" };
        options.ActorIdPrefix.Should().Be("home");
    }
}
