using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.RunForks;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.RunForks;

internal sealed class WorkflowForkRunCommandEnvelopeFactory
    : ICommandTargetEnvelopeFactory<WorkflowForkRunCommand, WorkflowForkRunCommandTarget>
{
    private readonly ICommandEnvelopeFactory<WorkflowChatRunRequest> _chatEnvelopeFactory;

    public WorkflowForkRunCommandEnvelopeFactory(
        ICommandEnvelopeFactory<WorkflowChatRunRequest> chatEnvelopeFactory)
    {
        _chatEnvelopeFactory = chatEnvelopeFactory ?? throw new ArgumentNullException(nameof(chatEnvelopeFactory));
    }

    public EventEnvelope CreateEnvelope(
        WorkflowForkRunCommand command,
        WorkflowForkRunCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return _chatEnvelopeFactory.CreateEnvelope(target.PreparedRequest, context);
    }
}
