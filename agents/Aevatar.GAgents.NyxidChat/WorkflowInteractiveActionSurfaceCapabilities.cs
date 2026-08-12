using System.Collections.Frozen;
using Aevatar.AI.Abstractions;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

internal sealed record WorkflowInteractiveActionSurfaceCapability(
    string WireAction,
    Func<
        NyxIdAssistantActionRegistry,
        WorkflowInteractiveActionParams,
        NyxIdAssistantActionValidation> Resolve);

internal static class WorkflowInteractiveActionSurfaceCapabilities
{
    private const string ActionUnsupported = "NYXID_ACTION_UNSUPPORTED";
    private const string ParamsInvalid = "NYXID_ACTION_PARAMS_INVALID";

    public static FrozenDictionary<
        WorkflowInteractiveActionParams.ActionParamsOneofCase,
        WorkflowInteractiveActionSurfaceCapability> Registrations { get; } =
        new Dictionary<
            WorkflowInteractiveActionParams.ActionParamsOneofCase,
            WorkflowInteractiveActionSurfaceCapability>
        {
            [WorkflowInteractiveActionParams.ActionParamsOneofCase.CatalogService] = new(
                "service.connect",
                static (registry, actionParams) => registry.ResolveCatalogServiceConnect(
                    actionParams.CatalogService.ServiceSlug,
                    actionParams.CatalogService.RequestedScopes)),
            [WorkflowInteractiveActionParams.ActionParamsOneofCase.KeyCreate] = new(
                "key.create",
                static (registry, actionParams) => registry.ResolveKeyCreate(
                    BuildKeyCreateRequirement(actionParams.KeyCreate))),
        }.ToFrozenDictionary();

    public static NyxIdAssistantActionValidation Resolve(
        NyxIdAssistantActionRegistry registry,
        WorkflowInteractiveActionRequestWirePayload request)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(request);
        var actionParams = request.Params;
        if (actionParams is null ||
            !Registrations.TryGetValue(actionParams.ActionParamsCase, out var capability))
        {
            throw new NyxIdAssistantActionRegistryException(
                ActionUnsupported,
                "The workflow interactive action is not enabled on this surface.");
        }

        if (!string.Equals(request.Action, capability.WireAction, StringComparison.Ordinal))
        {
            throw new NyxIdAssistantActionRegistryException(
                ParamsInvalid,
                "The workflow interactive action does not match its typed params variant.");
        }

        return capability.Resolve(registry, actionParams);
    }

    private static NyxIdKeyCreateActionRequirement BuildKeyCreateRequirement(
        WorkflowInteractiveKeyCreateActionParams actionParams)
    {
        var requirement = new NyxIdKeyCreateActionRequirement
        {
            Name = actionParams.Name,
            Platform = actionParams.Platform,
        };
        requirement.AllowedServiceIds.Add(actionParams.AllowedServiceIds);
        return requirement;
    }
}
