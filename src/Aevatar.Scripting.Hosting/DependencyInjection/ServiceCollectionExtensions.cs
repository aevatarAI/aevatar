using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.DependencyInjection;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.Scripting.Application.AI;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Application.Queries;
using Aevatar.Scripting.Application.Runtime;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Serialization;
using Aevatar.Scripting.Core.Schema;
using Aevatar.Scripting.Infrastructure.Compilation;
using Aevatar.Scripting.Infrastructure.Ports;
using Aevatar.Scripting.Infrastructure.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Aevatar.Scripting.Projection.DependencyInjection;

namespace Aevatar.Scripting.Hosting.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddScriptCapability(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _ = configuration;

        services.AddCqrsCore();
        services.TryAddSingleton(new ScriptingInteractionTimeoutOptions());
        services.TryAddSingleton<ScriptSandboxPolicy>();
        services.TryAddSingleton<IScriptProtoCompiler, GrpcToolsScriptProtoCompiler>();
        services.TryAddSingleton<IScriptingActorAddressResolver, DefaultScriptingActorAddressResolver>();
        services.TryAddSingleton<IProtobufMessageCodec, ProtobufMessageCodec>();
        services.TryAddSingleton<IScriptBehaviorCompiler, RoslynScriptBehaviorCompiler>();
        services.TryAddSingleton<IScriptBehaviorArtifactResolver, CachedScriptBehaviorArtifactResolver>();
        services.TryAddSingleton<IScriptReadModelMaterializationCompiler, ScriptReadModelMaterializationCompiler>();
        services.TryAddSingleton<IScriptNativeProjectionBuilder, ScriptNativeProjectionBuilder>();
        services.TryAddSingleton<IScriptReadModelSchemaActivationPolicy, DefaultScriptReadModelSchemaActivationPolicy>();
        services.TryAddSingleton<IScriptEvolutionApplicationService, ScriptEvolutionApplicationService>();
        services.TryAddSingleton<IScriptReadModelQueryApplicationService, ScriptReadModelQueryApplicationService>();
        services.TryAddSingleton<IScriptBehaviorDispatcher, ScriptBehaviorDispatcher>();
        services.TryAddSingleton<IScriptBehaviorRuntimeCapabilityFactory, ScriptBehaviorRuntimeCapabilityFactory>();
        services.TryAddSingleton<IScriptEvolutionPolicyEvaluator, DefaultScriptEvolutionPolicyEvaluator>();
        services.TryAddSingleton<IScriptEvolutionValidationService, RuntimeScriptEvolutionValidationService>();
        services.TryAddSingleton<IScriptCatalogBaselineReader, RuntimeScriptCatalogBaselineReader>();
        services.TryAddSingleton<IScriptPromotionCompensationService, RuntimeScriptPromotionCompensationService>();
        services.TryAddSingleton<IScriptEvolutionRollbackService, RuntimeScriptEvolutionRollbackService>();
        services.TryAddSingleton<RuntimeScriptActorAccessor>();
        services.TryAddSingleton<ICommandTargetResolver<ScriptEvolutionProposal, ScriptEvolutionCommandTarget, ScriptEvolutionStartError>, ScriptEvolutionCommandTargetResolver>();
        services.TryAddSingleton<ICommandTargetBinder<ScriptEvolutionProposal, ScriptEvolutionCommandTarget, ScriptEvolutionStartError>, ScriptEvolutionCommandTargetBinder>();
        services.TryAddSingleton<ICommandEnvelopeFactory<ScriptEvolutionProposal>, ScriptEvolutionEnvelopeFactory>();
        services.TryAddSingleton<ICommandTargetDispatcher<ScriptEvolutionCommandTarget>, ActorCommandTargetDispatcher<ScriptEvolutionCommandTarget>>();
        services.TryAddSingleton<ICommandReceiptFactory<ScriptEvolutionCommandTarget, ScriptEvolutionAcceptedReceipt>, ScriptEvolutionAcceptedReceiptFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<ScriptEvolutionProposal, ScriptEvolutionCommandTarget, ScriptEvolutionAcceptedReceipt, ScriptEvolutionStartError>, DefaultCommandDispatchPipeline<ScriptEvolutionProposal, ScriptEvolutionCommandTarget, ScriptEvolutionAcceptedReceipt, ScriptEvolutionStartError>>();
        services.TryAddSingleton<ICommandDispatchService<ScriptEvolutionProposal, ScriptEvolutionAcceptedReceipt, ScriptEvolutionStartError>, DefaultCommandDispatchService<ScriptEvolutionProposal, ScriptEvolutionCommandTarget, ScriptEvolutionAcceptedReceipt, ScriptEvolutionStartError>>();
        services.TryAddSingleton<ICommandCompletionPolicy<ScriptEvolutionSessionCompletedEvent, ScriptEvolutionInteractionCompletion>, ScriptEvolutionCompletionPolicy>();
        services.TryAddSingleton<ICommandDurableCompletionResolver<ScriptEvolutionAcceptedReceipt, ScriptEvolutionInteractionCompletion>, ScriptEvolutionDurableCompletionResolver>();
        services.TryAddSingleton<ICommandFinalizeEmitter<ScriptEvolutionAcceptedReceipt, ScriptEvolutionInteractionCompletion, ScriptEvolutionSessionCompletedEvent>, NoOpCommandFinalizeEmitter<ScriptEvolutionAcceptedReceipt, ScriptEvolutionInteractionCompletion, ScriptEvolutionSessionCompletedEvent>>();
        services.TryAddSingleton<IEventOutputStream<ScriptEvolutionSessionCompletedEvent, ScriptEvolutionSessionCompletedEvent>, ScriptEvolutionTimedEventOutputStream>();
        services.AddSingleton<ICommandInteractionService<ScriptEvolutionProposal, ScriptEvolutionAcceptedReceipt, ScriptEvolutionStartError, ScriptEvolutionSessionCompletedEvent, ScriptEvolutionInteractionCompletion>>(sp =>
            new DefaultCommandInteractionService<ScriptEvolutionProposal, ScriptEvolutionCommandTarget, ScriptEvolutionAcceptedReceipt, ScriptEvolutionStartError, ScriptEvolutionSessionCompletedEvent, ScriptEvolutionSessionCompletedEvent, ScriptEvolutionInteractionCompletion>(
                sp.GetRequiredService<ICommandDispatchPipeline<ScriptEvolutionProposal, ScriptEvolutionCommandTarget, ScriptEvolutionAcceptedReceipt, ScriptEvolutionStartError>>(),
                sp.GetRequiredService<IEventOutputStream<ScriptEvolutionSessionCompletedEvent, ScriptEvolutionSessionCompletedEvent>>(),
                sp.GetRequiredService<ICommandCompletionPolicy<ScriptEvolutionSessionCompletedEvent, ScriptEvolutionInteractionCompletion>>(),
                sp.GetRequiredService<ICommandFinalizeEmitter<ScriptEvolutionAcceptedReceipt, ScriptEvolutionInteractionCompletion, ScriptEvolutionSessionCompletedEvent>>(),
                sp.GetRequiredService<ICommandDurableCompletionResolver<ScriptEvolutionAcceptedReceipt, ScriptEvolutionInteractionCompletion>>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<DefaultCommandInteractionService<ScriptEvolutionProposal, ScriptEvolutionCommandTarget, ScriptEvolutionAcceptedReceipt, ScriptEvolutionStartError, ScriptEvolutionSessionCompletedEvent, ScriptEvolutionSessionCompletedEvent, ScriptEvolutionInteractionCompletion>>>()));
        services.TryAddSingleton<ICommandTargetDispatcher<ScriptingActorCommandTarget>, ActorCommandTargetDispatcher<ScriptingActorCommandTarget>>();
        services.TryAddSingleton<ICommandReceiptFactory<ScriptingActorCommandTarget, ScriptingCommandAcceptedReceipt>, ScriptingCommandAcceptedReceiptFactory>();
        AddSimpleScriptingCommandDispatch<UpsertScriptDefinitionCommand, UpsertScriptDefinitionCommandTargetResolver, UpsertScriptDefinitionCommandEnvelopeFactory>(services);
        AddSimpleScriptingCommandDispatch<ProvisionScriptRuntimeCommand, ProvisionScriptRuntimeCommandTargetResolver, ProvisionScriptRuntimeCommandEnvelopeFactory>(services);
        AddSimpleScriptingCommandDispatch<RunScriptRuntimeCommand, RunScriptRuntimeCommandTargetResolver, RunScriptRuntimeCommandEnvelopeFactory>(services);
        AddSimpleScriptingCommandDispatch<PromoteScriptCatalogRevisionCommand, PromoteScriptCatalogRevisionCommandTargetResolver, PromoteScriptCatalogRevisionCommandEnvelopeFactory>(services);
        AddSimpleScriptingCommandDispatch<RollbackScriptCatalogRevisionCommand, RollbackScriptCatalogRevisionCommandTargetResolver, RollbackScriptCatalogRevisionCommandEnvelopeFactory>(services);
        services.TryAddSingleton<RuntimeScriptEvolutionInteractionService>();
        services.TryAddSingleton<RuntimeScriptDefinitionCommandService>();
        services.TryAddSingleton<RuntimeScriptProvisioningService>();
        services.TryAddSingleton<RuntimeScriptCommandService>();
        services.TryAddSingleton<RuntimeScriptCatalogCommandService>();
        services.TryAddSingleton<IScriptEvolutionProposalPort>(sp => sp.GetRequiredService<RuntimeScriptEvolutionInteractionService>());
        services.TryAddSingleton<IScriptDefinitionCommandPort>(sp => sp.GetRequiredService<RuntimeScriptDefinitionCommandService>());
        services.TryAddSingleton<IScriptRuntimeProvisioningPort>(sp => sp.GetRequiredService<RuntimeScriptProvisioningService>());
        services.TryAddSingleton<IScriptRuntimeCommandPort>(sp => sp.GetRequiredService<RuntimeScriptCommandService>());
        services.TryAddSingleton<IScriptCatalogCommandPort>(sp => sp.GetRequiredService<RuntimeScriptCatalogCommandService>());
        services.AddProjectionReadModelRuntime();
        services.AddScriptingProjectionComponents();
        services.AddScriptingProjectionReadModelProviders(configuration);
        services.TryAddSingleton<IAICapability>(sp =>
        {
            var roleAgentPort = sp.GetService<IRoleAgentPort>();
            return roleAgentPort == null
                ? new NoopAICapability()
                : new RoleAgentDelegateAICapability(roleAgentPort);
        });
        services.TryAddSingleton<ScriptCapabilityRegistrationsMarker>();

        return services;
    }

    private static void AddSimpleScriptingCommandDispatch<TCommand, TResolver, TEnvelopeFactory>(
        IServiceCollection services)
        where TCommand : class
        where TResolver : class, ICommandTargetResolver<TCommand, ScriptingActorCommandTarget, ScriptingCommandStartError>
        where TEnvelopeFactory : class, ICommandEnvelopeFactory<TCommand>
    {
        services.TryAddSingleton<ICommandTargetResolver<TCommand, ScriptingActorCommandTarget, ScriptingCommandStartError>, TResolver>();
        services.TryAddSingleton<ICommandTargetBinder<TCommand, ScriptingActorCommandTarget, ScriptingCommandStartError>, NoOpCommandTargetBinder<TCommand, ScriptingActorCommandTarget, ScriptingCommandStartError>>();
        services.TryAddSingleton<ICommandEnvelopeFactory<TCommand>, TEnvelopeFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<TCommand, ScriptingActorCommandTarget, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>, DefaultCommandDispatchPipeline<TCommand, ScriptingActorCommandTarget, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>>();
        services.TryAddSingleton<ICommandDispatchService<TCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>, DefaultCommandDispatchService<TCommand, ScriptingActorCommandTarget, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>>();
    }

    public sealed class ScriptCapabilityRegistrationsMarker;
}
