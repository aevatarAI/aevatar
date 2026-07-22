using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.CodexExecution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.Infrastructure.ChronoSandbox;

internal interface IChronoSandboxCodexClient
{
    Task<CodexExecutionResult> ExecuteAsync(
        CodexExecutionRequest request,
        CancellationToken ct = default);
}

internal sealed class ManagedCodexExecutionException : Exception
{
    public ManagedCodexExecutionException(CodexExecutionFailure failure)
        : base(failure?.Message)
    {
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    public CodexExecutionFailure Failure { get; }
}

internal sealed class ChronoSandboxCodexExecutionAdapter(
    IOptions<ManagedCodexOptions> options,
    IChronoSandboxCodexClient client,
    ILogger<ChronoSandboxCodexExecutionAdapter> logger) : ICodexExecutionPort
{
    private readonly ManagedCodexOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly IChronoSandboxCodexClient _client =
        client ?? throw new ArgumentNullException(nameof(client));
    private readonly ILogger<ChronoSandboxCodexExecutionAdapter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public CodexExecutionTarget.TargetOneofCase TargetKind =>
        CodexExecutionTarget.TargetOneofCase.ManagedSandbox;

    public async IAsyncEnumerable<CodexExecutionEvent> ExecuteAsync(
        CodexExecutionRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return CodexExecutionEvent.Started();

        if (!_options.Enabled)
        {
            yield return CodexExecutionEvent.Failed(new CodexExecutionFailure(
                CodexExecutionFailureKind.TargetNotConfigured,
                "managed_target_disabled",
                "Managed Codex execution is disabled."));
            yield break;
        }

        CodexExecutionEvent terminalEvent;
        try
        {
            var result = await _client.ExecuteAsync(request, ct).ConfigureAwait(false);
            terminalEvent = CodexExecutionEvent.Completed(result);
        }
        catch (ManagedCodexExecutionException exception)
        {
            _logger.LogWarning(
                "Managed Codex execution failed with code {FailureCode}",
                exception.Failure.Code);
            terminalEvent = CodexExecutionEvent.Failed(exception.Failure);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            terminalEvent = CodexExecutionEvent.Failed(new CodexExecutionFailure(
                CodexExecutionFailureKind.Cancelled,
                "managed_execution_cancelled",
                "Managed Codex execution was cancelled."));
        }
        catch (OperationCanceledException)
        {
            terminalEvent = CodexExecutionEvent.Failed(new CodexExecutionFailure(
                CodexExecutionFailureKind.TimedOut,
                "managed_proxy_timeout",
                "Managed Codex proxy request timed out."));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Managed Codex execution failed with exception type {ExceptionType}",
                exception.GetType().Name);
            terminalEvent = CodexExecutionEvent.Failed(new CodexExecutionFailure(
                CodexExecutionFailureKind.TerminalFailure,
                "managed_execution_failed",
                "Managed Codex execution failed."));
        }

        yield return terminalEvent;
    }
}
