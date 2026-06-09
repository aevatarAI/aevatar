using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.RunForks;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;

namespace Aevatar.Workflow.Application.RunForks;

internal sealed class WorkflowForkRunEnvelopeFactory : ICommandEnvelopeFactory<WorkflowForkRunCommand>
{
    private readonly ICommandEnvelopeFactory<WorkflowChatRunRequest> _chatEnvelopeFactory;

    public WorkflowForkRunEnvelopeFactory(ICommandEnvelopeFactory<WorkflowChatRunRequest> chatEnvelopeFactory)
    {
        _chatEnvelopeFactory = chatEnvelopeFactory ?? throw new ArgumentNullException(nameof(chatEnvelopeFactory));
    }

    public EventEnvelope CreateEnvelope(WorkflowForkRunCommand command, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return _chatEnvelopeFactory.CreateEnvelope(command.ToWorkflowChatRunRequest(), context);
    }
}
