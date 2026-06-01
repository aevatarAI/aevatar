using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Workflow.Core.Connectors;
using FluentAssertions;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "ConnectorRegistry")]
public sealed class ConnectorRegistryDisposalTests
{
    [Fact]
    public async Task RegisterAsync_WhenOwnedConnectorIsReplaced_ShouldDisposePreviousConnector()
    {
        var registry = new ConfiguredConnectorRegistry();
        var previous = new RecordingConnector("mcp");
        var current = new RecordingConnector("mcp");

        await registry.RegisterAsync(ConnectorRegistration.Owned(previous));
        await registry.RegisterAsync(ConnectorRegistration.Owned(current));

        previous.DisposeCount.Should().Be(1);
        current.DisposeCount.Should().Be(0);
        registry.TryGet("mcp", out var resolved).Should().BeTrue();
        resolved.Should().BeSameAs(current);
    }

    [Fact]
    public async Task DisposeAsync_ShouldDisposeCurrentOwnedConnectorsAndClearRegistry()
    {
        var registry = new ConfiguredConnectorRegistry();
        var first = new RecordingConnector("first");
        var second = new RecordingConnector("second");

        await registry.RegisterAsync(ConnectorRegistration.Owned(first));
        await registry.RegisterAsync(ConnectorRegistration.Owned(second));
        await registry.DisposeAsync();
        await registry.DisposeAsync();

        first.DisposeCount.Should().Be(1);
        second.DisposeCount.Should().Be(1);
        registry.ListNames().Should().BeEmpty();
    }

    [Fact]
    public async Task DisposeAsync_ShouldNotDisposeExternallyOwnedConnectors()
    {
        var registry = new ConfiguredConnectorRegistry();
        var external = new RecordingConnector("external");

        await registry.RegisterAsync(ConnectorRegistration.External(external));
        await registry.DisposeAsync();

        external.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task RegisterAsync_AfterDispose_ShouldThrow()
    {
        var registry = new ConfiguredConnectorRegistry();
        await registry.DisposeAsync();

        var act = async () => await registry.RegisterAsync(ConnectorRegistration.Owned(new RecordingConnector("late")));

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Dispose_ShouldDisposeCurrentOwnedConnectorsAndClearRegistry()
    {
        var registry = new ConfiguredConnectorRegistry();
        var first = new SyncRecordingConnector("first");
        var second = new SyncRecordingConnector("second");

        await registry.RegisterAsync(ConnectorRegistration.Owned(first));
        await registry.RegisterAsync(ConnectorRegistration.Owned(second));

        registry.Dispose();
        registry.Dispose();

        first.DisposeCount.Should().Be(1);
        second.DisposeCount.Should().Be(1);
        registry.ListNames().Should().BeEmpty();
    }

    [Fact]
    public async Task DisposeAsync_WhenSameConnectorRegisteredUnderMultipleNames_ShouldDisposeItOnce()
    {
        var registry = new ConfiguredConnectorRegistry();
        var connector = new MutableNameRecordingConnector("first");

        await registry.RegisterAsync(ConnectorRegistration.Owned(connector));
        connector.Name = "second";
        await registry.RegisterAsync(ConnectorRegistration.Owned(connector));

        await registry.DisposeAsync();

        connector.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task Dispose_WhenSameConnectorRegisteredUnderMultipleNames_ShouldDisposeItOnce()
    {
        var registry = new ConfiguredConnectorRegistry();
        var connector = new MutableNameSyncRecordingConnector("first");

        await registry.RegisterAsync(ConnectorRegistration.Owned(connector));
        connector.Name = "second";
        await registry.RegisterAsync(ConnectorRegistration.Owned(connector));

        registry.Dispose();

        connector.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task RegisterAsync_WhenReplacingWithSameInstance_ShouldNotDisposeConnector()
    {
        var registry = new ConfiguredConnectorRegistry();
        var connector = new RecordingConnector("mcp");

        await registry.RegisterAsync(ConnectorRegistration.Owned(connector));
        await registry.RegisterAsync(ConnectorRegistration.Owned(connector));

        connector.DisposeCount.Should().Be(0);
    }

    private sealed class RecordingConnector(string name) : IConnector, IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public string Name { get; } = name;

        public string Type => "test";

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            _ = request;
            _ = ct;
            return Task.FromResult(new ConnectorResponse { Success = true });
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SyncRecordingConnector(string name) : IConnector, IDisposable
    {
        public int DisposeCount { get; private set; }

        public string Name { get; } = name;

        public string Type => "test";

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            _ = request;
            _ = ct;
            return Task.FromResult(new ConnectorResponse { Success = true });
        }

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class MutableNameRecordingConnector(string name) : IConnector, IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public string Name { get; set; } = name;

        public string Type => "test";

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            _ = request;
            _ = ct;
            return Task.FromResult(new ConnectorResponse { Success = true });
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableNameSyncRecordingConnector(string name) : IConnector, IDisposable
    {
        public int DisposeCount { get; private set; }

        public string Name { get; set; } = name;

        public string Type => "test";

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            _ = request;
            _ = ct;
            return Task.FromResult(new ConnectorResponse { Success = true });
        }

        public void Dispose()
        {
            DisposeCount++;
        }
    }
}
