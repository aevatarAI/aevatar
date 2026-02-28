using Aevatar.AI.Core;
using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;

namespace Aevatar.DynamicRuntime.Core.Agents;

public sealed class ScriptRoleContainerAgent : RoleGAgent
{
    [EventHandler]
    public Task HandleConfigureScriptRoleCapabilitiesAsync(ConfigureScriptRoleCapabilitiesEvent evt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        SetRoleName(evt.ServiceId ?? string.Empty);
        return PersistDomainEventAsync(evt, ct);
    }
}
