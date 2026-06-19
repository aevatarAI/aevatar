using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;

namespace Aevatar.GAgentService.Core.GAgents;

[GAgent(Kind)]
public sealed class LlmRunExecutionGAgent : GAgentBase
{
    public const string Kind = "gagent.service.llm-run-execution";

    private readonly ILlmRunExecutionService _executionService;

    public LlmRunExecutionGAgent(ILlmRunExecutionService executionService)
    {
        _executionService = executionService ?? throw new ArgumentNullException(nameof(executionService));
        InitializeId();
    }

    [EventHandler]
    public Task HandleExecuteAsync(ExecuteLlmRunRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Command);

        return _executionService.ExecuteAsync(
            new LlmRunExecutorRequest(
                command.SessionActorId,
                command.ResponseId,
                command.RunId,
                command.Command.Clone(),
                string.IsNullOrWhiteSpace(command.OriginPlatform) ? null : command.OriginPlatform),
            CancellationToken.None);
    }
}
