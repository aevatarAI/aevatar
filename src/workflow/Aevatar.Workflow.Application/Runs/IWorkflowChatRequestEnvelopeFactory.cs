using Aevatar.Foundation.Abstractions;

namespace Aevatar.Workflow.Application.Runs;

public interface IWorkflowChatRequestEnvelopeFactory
{
    EventEnvelope Create(string prompt, string runId);
}
