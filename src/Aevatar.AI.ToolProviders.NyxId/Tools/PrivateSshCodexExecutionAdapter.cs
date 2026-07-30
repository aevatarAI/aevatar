using System.Runtime.CompilerServices;
using System.Text;
using Aevatar.AI.Abstractions.CodexExecution;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

internal sealed class PrivateSshCodexExecutionAdapter(INyxIdSshCommandExecutor executor)
    : ICodexExecutionPort
{
    private readonly INyxIdSshCommandExecutor _executor =
        executor ?? throw new ArgumentNullException(nameof(executor));

    public CodexExecutionTarget.TargetOneofCase TargetKind =>
        CodexExecutionTarget.TargetOneofCase.PrivateSsh;

    public async IAsyncEnumerable<CodexExecutionEvent> ExecuteAsync(
        CodexExecutionRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Target.TargetCase != TargetKind || request.Target.PrivateSsh == null)
            throw new ArgumentException("Private SSH adapter received a different target kind.", nameof(request));

        yield return CodexExecutionEvent.Started();

        var target = request.Target.PrivateSsh;
        var output = await _executor.ExecuteAsync(
            new NyxIdSshCommandRequest(
                target.Service,
                target.Principal,
                BuildCommand(Encoding.UTF8.GetBytes(request.Prompt)),
                request.TimeoutSeconds),
            ct).ConfigureAwait(false);

        yield return CodexExecutionEvent.Completed(new CodexExecutionResult(output));
    }

    internal static string BuildCommand(ReadOnlySpan<byte> promptBytes)
    {
        var encodedPrompt = Convert.ToBase64String(promptBytes);
        return $"p='{encodedPrompt}'; {{ printf '%s' \"$p\" | base64 --decode 2>/dev/null || printf '%s' \"$p\" | base64 -D; }} | codex exec -";
    }
}
