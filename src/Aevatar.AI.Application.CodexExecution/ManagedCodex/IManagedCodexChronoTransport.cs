using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.GAgents.Channel.Identity.Abstractions;

namespace Aevatar.AI.Application.CodexExecution;

public interface IManagedCodexChronoTransport
{
    Task<CodexExecutionResult> ExecuteAsync(
        CodexExecutionRequest request,
        ManagedCodexCredentialDescriptor credential,
        CancellationToken ct = default);
}

public sealed class ManagedCodexTransportException : Exception
{
    public ManagedCodexTransportException(CodexExecutionFailure failure)
        : base(failure?.Message)
    {
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    public CodexExecutionFailure Failure { get; }
}
