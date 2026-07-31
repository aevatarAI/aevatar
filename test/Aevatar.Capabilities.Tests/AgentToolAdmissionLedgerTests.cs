using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Mainnet.Host.Api.Responses;
using FluentAssertions;

namespace Aevatar.Capabilities.Tests;

public sealed class AgentToolAdmissionLedgerTests
{
    [Fact]
    public async Task TryStartAsync_ShouldDistinguishFirstDuplicateAndConflict()
    {
        var store = new RecordingAdmissionFactStore();
        var ledger = new DistributedAgentToolAdmissionLedger(store);
        var fact = CreateFact();
        var conflictingFact = fact.Clone();
        conflictingFact.ToolName = "different_tool";

        (await ledger.TryStartAsync(fact)).Status.Should().Be(AgentToolAdmissionStatus.Started);
        (await ledger.TryStartAsync(fact.Clone())).Status.Should().Be(AgentToolAdmissionStatus.Duplicate);
        (await ledger.TryStartAsync(conflictingFact))
            .Status.Should().Be(AgentToolAdmissionStatus.Conflict);

        store.Keys.Should().ContainSingle()
            .Which.Should().StartWith("aevatar:mainnet:agent-tool-admission:v1:")
            .And.NotContain(fact.AdmissionId);
        store.Values.Should().ContainSingle().Which.Should().HaveCount(32);
    }

    [Fact]
    public async Task TryStartAsync_WhenStoreThrows_ShouldFailClosed()
    {
        var ledger = new DistributedAgentToolAdmissionLedger(
            new RecordingAdmissionFactStore { ThrowOnAdd = true });

        var result = await ledger.TryStartAsync(CreateFact());

        result.Status.Should().Be(AgentToolAdmissionStatus.StoreUnavailable);
        result.SafeMessage.Should().Be(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task InMemoryLedger_ShouldApplyTheSameFactSemantics()
    {
        var ledger = new InMemoryAgentToolAdmissionLedger();
        var fact = CreateFact();
        var conflictingFact = fact.Clone();
        conflictingFact.ArgumentsSha256 = new string('f', 64);

        (await ledger.TryStartAsync(fact)).Status.Should().Be(AgentToolAdmissionStatus.Started);
        (await ledger.TryStartAsync(fact.Clone())).Status.Should().Be(AgentToolAdmissionStatus.Duplicate);
        (await ledger.TryStartAsync(conflictingFact))
            .Status.Should().Be(AgentToolAdmissionStatus.Conflict);
    }

    private static AgentToolAdmissionFact CreateFact() => new()
    {
        AdmissionId = "tool:v1:admission:sensitive-id",
        RequestId = "request-1",
        ToolCallId = "call-1",
        ToolName = "test_tool",
        ArgumentsSha256 = new string('a', 64),
    };

    private sealed class RecordingAdmissionFactStore : IAgentToolAdmissionFactStore
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

        public bool ThrowOnAdd { get; init; }
        public IReadOnlyCollection<string> Keys => _values.Keys;
        public IReadOnlyCollection<byte[]> Values => _values.Values;

        public Task<bool> SetIfAbsentAsync(
            string key,
            ReadOnlyMemory<byte> value,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (ThrowOnAdd)
                throw new InvalidOperationException("offline");

            if (_values.ContainsKey(key))
                return Task.FromResult(false);
            _values.Add(key, value.ToArray());
            return Task.FromResult(true);
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value.ToArray() : null);
        }
    }
}
