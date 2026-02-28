using Aevatar.AI.Abstractions.Agents;
using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.DynamicRuntime.Core.Adapters;

public sealed class ScriptRoleCapabilityAdapter : IScriptRoleCapabilityAdapter
{
    private readonly IScriptRoleEntrypoint _entrypoint;
    private string _roleName = "dynamic-script-role";

    public ScriptRoleCapabilityAdapter(IScriptRoleEntrypoint entrypoint, ScriptRoleCapabilitySnapshot snapshot, string? agentId = null)
    {
        _entrypoint = entrypoint ?? throw new ArgumentNullException(nameof(entrypoint));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Id = string.IsNullOrWhiteSpace(agentId) ? $"dynamic:role-adapter:{snapshot.ServiceId}" : agentId;
    }

    public string Id { get; }
    public ScriptRoleCapabilitySnapshot Snapshot { get; }

    public void SetRoleName(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            _roleName = name;
    }

    public Task ConfigureAsync(RoleAgentConfig config, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _ = config;
        return Task.CompletedTask;
    }

    public Task<string> ExecuteAsync(EventEnvelope envelope, CancellationToken ct = default)
        => ExecuteEnvelopeAsync(envelope ?? throw new ArgumentNullException(nameof(envelope)), ct);

    public async Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _ = await _entrypoint.HandleEventAsync(envelope, ct);
    }

    private async Task<string> ExecuteEnvelopeAsync(EventEnvelope envelope, CancellationToken ct)
    {
        var result = await _entrypoint.HandleEventAsync(envelope, ct);
        return result.Output ?? string.Empty;
    }

    public Task<string> GetDescriptionAsync() => Task.FromResult($"ScriptRoleCapabilityAdapter[{_roleName}]");

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
