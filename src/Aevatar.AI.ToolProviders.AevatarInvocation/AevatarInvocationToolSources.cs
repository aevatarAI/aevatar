using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.AevatarInvocation;

public sealed class InvokeGAgentToolSource : IAgentToolSource
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public InvokeGAgentToolSource(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>([new InvokeGAgentTool(_dispatcher)]);
}

public sealed class InvokeTeamToolSource : IAgentToolSource
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public InvokeTeamToolSource(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>([new InvokeTeamTool(_dispatcher)]);
}

public sealed class StartWorkflowToolSource : IAgentToolSource
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public StartWorkflowToolSource(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>([new StartWorkflowTool(_dispatcher)]);
}

public sealed class ObserveRunToolSource : IAgentToolSource
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public ObserveRunToolSource(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>([new ObserveRunTool(_dispatcher)]);
}

public sealed class QueryReadModelToolSource : IAgentToolSource
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public QueryReadModelToolSource(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>([new QueryReadModelTool(_dispatcher)]);
}

internal sealed class InvokeGAgentTool : IAevatarInvocationTool
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public InvokeGAgentTool(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public string Name => "aevatar_invoke_gagent";

    public string Description =>
        "Invoke a single Aevatar GAgent by actor_id or caller-scoped actor_name with a typed chat payload.";

    public string ParametersSchema => AevatarInvocationToolSchemas.InvokeGAgent;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        _dispatcher.InvokeGAgentAsync(argumentsJson, ct);
}

internal sealed class InvokeTeamTool : IAevatarInvocationTool
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public InvokeTeamTool(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public string Name => "aevatar_invoke_team";

    public string Description =>
        "Invoke a Studio team entry endpoint by team_id and endpoint_id with a typed chat payload.";

    public string ParametersSchema => AevatarInvocationToolSchemas.InvokeTeam;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        _dispatcher.InvokeTeamAsync(argumentsJson, ct);
}

internal sealed class StartWorkflowTool : IAevatarInvocationTool
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public StartWorkflowTool(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public string Name => "aevatar_start_workflow";

    public string Description =>
        "Start an Aevatar workflow by workflow_id with typed inputs. " +
        "When use_skill returns inline workflow_yamls, pass that bundle in workflow_yamls instead of treating the YAMLs as ordinary text.";

    public string ParametersSchema => AevatarInvocationToolSchemas.StartWorkflow;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        _dispatcher.StartWorkflowAsync(argumentsJson, ct);
}

internal sealed class ObserveRunTool : IAevatarInvocationTool
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public ObserveRunTool(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public string Name => "aevatar_observe_run";

    public string Description =>
        "Observe a previously accepted Aevatar run by run_id through existing run and projection read models.";

    public string ParametersSchema => AevatarInvocationToolSchemas.ObserveRun;

    public bool IsReadOnly => true;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        _dispatcher.ObserveRunAsync(argumentsJson, ct);
}

internal sealed class QueryReadModelTool : IAevatarInvocationTool
{
    private readonly AevatarInvocationDispatcher _dispatcher;

    public QueryReadModelTool(AevatarInvocationDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public string Name => "aevatar_query_readmodel";

    public string Description =>
        "Read one of the closed-set Aevatar current-state read models by name and typed query.";

    public string ParametersSchema => AevatarInvocationToolSchemas.QueryReadModel;

    public bool IsReadOnly => true;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        _dispatcher.QueryReadModelAsync(argumentsJson, ct);
}
