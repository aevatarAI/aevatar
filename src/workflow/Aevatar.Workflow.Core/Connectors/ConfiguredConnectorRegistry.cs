using System.Collections.Immutable;
using Aevatar.Foundation.Abstractions.Connectors;

namespace Aevatar.Workflow.Core.Connectors;

/// <summary>
/// Startup-initialized connector registry.
/// <para>
/// Connectors are registered during application bootstrap (before the host
/// starts processing requests) and are read-only thereafter. The registry
/// uses <see cref="ImmutableDictionary{TKey,TValue}"/> for lock-free,
/// thread-safe reads after initialization.
/// </para>
/// <para>
/// <b>Multi-node deployments</b>: each node must be configured with the
/// same set of connectors (typically via identical <c>connectors.json</c>
/// configuration). The registry does not provide cross-node consistency;
/// it relies on consistent configuration across all nodes.
/// </para>
/// </summary>
public sealed class ConfiguredConnectorRegistry : IConnectorRegistry, IDisposable
{
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private ImmutableDictionary<string, ConnectorSlot> _connectors =
        ImmutableDictionary.Create<string, ConnectorSlot>(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private int _disposeStarted;

    /// <inheritdoc />
    public async ValueTask RegisterAsync(ConnectorRegistration registration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);

        ConnectorSlot? previousSlot = null;
        await _mutationGate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var connector = registration.Connector;
            if (_connectors.TryGetValue(connector.Name, out var existing))
                previousSlot = existing;

            // Refactor (iter87/cluster-087):
            //   Old pattern: Register replaced a disposable connector in-place, leaving MCP HttpClient/session resources with no owner.
            //   New principle: registry-owned connector slots are async lifecycle entries; replacement swaps first, then awaits old disposal.
            _connectors = _connectors.SetItem(
                connector.Name,
                new ConnectorSlot(connector, registration.Ownership));
        }
        finally
        {
            _mutationGate.Release();
        }

        if (previousSlot is not null && !ReferenceEquals(previousSlot.Connector, registration.Connector))
            await DisposeSlotAsync(previousSlot);
    }

    /// <inheritdoc />
    public bool TryGet(string name, out IConnector? connector)
    {
        var ok = _connectors.TryGetValue(name, out var found);
        connector = found?.Connector;
        return ok;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListNames()
    {
        return _connectors.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        ImmutableDictionary<string, ConnectorSlot> snapshot;

        await _mutationGate.WaitAsync();
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            snapshot = _connectors;
            _connectors = ImmutableDictionary.Create<string, ConnectorSlot>(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _mutationGate.Release();
        }

        var disposedConnectors = new HashSet<IConnector>(ReferenceEqualityComparer.Instance);
        foreach (var slot in snapshot.Values)
        {
            if (!disposedConnectors.Add(slot.Connector))
                continue;

            await DisposeSlotAsync(slot);
        }

        _mutationGate.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        ImmutableDictionary<string, ConnectorSlot> snapshot;

        _mutationGate.Wait();
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            snapshot = _connectors;
            _connectors = ImmutableDictionary.Create<string, ConnectorSlot>(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _mutationGate.Release();
        }

        var disposedConnectors = new HashSet<IConnector>(ReferenceEqualityComparer.Instance);
        foreach (var slot in snapshot.Values)
        {
            if (!disposedConnectors.Add(slot.Connector))
                continue;

            DisposeSlot(slot);
        }

        _mutationGate.Dispose();
    }

    private static async ValueTask DisposeSlotAsync(ConnectorSlot slot)
    {
        if (slot.Ownership != ConnectorOwnership.RegistryOwned)
            return;

        switch (slot.Connector)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private static void DisposeSlot(ConnectorSlot slot)
    {
        if (slot.Ownership != ConnectorOwnership.RegistryOwned)
            return;

        if (slot.Connector is IDisposable disposable)
            disposable.Dispose();
    }

    private sealed record ConnectorSlot(IConnector Connector, ConnectorOwnership Ownership);
}
