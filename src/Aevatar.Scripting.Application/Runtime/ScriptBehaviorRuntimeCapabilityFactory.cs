using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;
using Google.Protobuf;

namespace Aevatar.Scripting.Application.Runtime;

public sealed class ScriptBehaviorRuntimeCapabilityFactory : IScriptBehaviorRuntimeCapabilityFactory
{
    private readonly IAICapability _aiCapability;
    private readonly IActorRuntime _runtime;
    private readonly IScriptExecutionProjectionPort _executionProjectionPort;
    private readonly IScriptReadModelQueryPort _readModelQueryPort;
    private readonly IScriptEvolutionProposalPort _proposalPort;
    private readonly IScriptDefinitionCommandPort _definitionCommandPort;
    private readonly IScriptRuntimeProvisioningPort _runtimeProvisioningPort;
    private readonly IScriptRuntimeCommandPort _runtimeCommandPort;
    private readonly IScriptCatalogCommandPort _catalogCommandPort;
    private readonly IScriptAuthorityProjectionPrimingPort _authorityProjectionPrimingPort;

    public ScriptBehaviorRuntimeCapabilityFactory(
        IAICapability aiCapability,
        IActorRuntime runtime,
        IScriptExecutionProjectionPort executionProjectionPort,
        IScriptReadModelQueryPort readModelQueryPort,
        IScriptEvolutionProposalPort proposalPort,
        IScriptDefinitionCommandPort definitionCommandPort,
        IScriptRuntimeProvisioningPort runtimeProvisioningPort,
        IScriptRuntimeCommandPort runtimeCommandPort,
        IScriptCatalogCommandPort catalogCommandPort,
        IScriptAuthorityProjectionPrimingPort authorityProjectionPrimingPort)
    {
        _aiCapability = aiCapability ?? throw new ArgumentNullException(nameof(aiCapability));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _executionProjectionPort = executionProjectionPort ?? throw new ArgumentNullException(nameof(executionProjectionPort));
        _readModelQueryPort = readModelQueryPort ?? throw new ArgumentNullException(nameof(readModelQueryPort));
        _proposalPort = proposalPort ?? throw new ArgumentNullException(nameof(proposalPort));
        _definitionCommandPort = definitionCommandPort ?? throw new ArgumentNullException(nameof(definitionCommandPort));
        _runtimeProvisioningPort = runtimeProvisioningPort ?? throw new ArgumentNullException(nameof(runtimeProvisioningPort));
        _runtimeCommandPort = runtimeCommandPort ?? throw new ArgumentNullException(nameof(runtimeCommandPort));
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
        _authorityProjectionPrimingPort = authorityProjectionPrimingPort ?? throw new ArgumentNullException(nameof(authorityProjectionPrimingPort));
    }

    public IScriptBehaviorRuntimeCapabilities Create(
        ScriptBehaviorRuntimeCapabilityContext context,
        Func<IMessage, TopologyAudience, CancellationToken, Task> publishAsync,
        Func<string, IMessage, CancellationToken, Task> sendToAsync,
        Func<IMessage, CancellationToken, Task> publishToSelfAsync,
        Func<string, TimeSpan, IMessage, CancellationToken, Task<RuntimeCallbackLease>> scheduleSelfSignalAsync,
        Func<RuntimeCallbackLease, CancellationToken, Task> cancelCallbackAsync)
    {
        _ = context;

        return new ScriptBehaviorRuntimeCapabilities(
            context.RunId,
            context.CorrelationId,
            publishAsync,
            sendToAsync,
            publishToSelfAsync,
            scheduleSelfSignalAsync,
            cancelCallbackAsync,
            _aiCapability,
            _runtime,
            _executionProjectionPort,
            _readModelQueryPort,
            _proposalPort,
            _definitionCommandPort,
            _runtimeProvisioningPort,
            _runtimeCommandPort,
            _catalogCommandPort,
            _authorityProjectionPrimingPort);
    }
}
