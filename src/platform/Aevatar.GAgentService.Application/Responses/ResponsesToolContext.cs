using System.Threading;
using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.GAgentService.Application.Responses;

public static class ResponsesToolContext
{
    private static readonly AsyncLocal<ResponsesToolChoiceHintPlan?> CurrentHint = new();

    public static IDisposable Push(ResponsesToolChoiceHintPlan? hint)
    {
        var previous = CurrentHint.Value;
        CurrentHint.Value = hint;
        return new Scope(previous);
    }

    public static ToolCall ApplyToolChoiceHint(ToolCall call) =>
        CurrentHint.Value?.Apply(call) ?? call;

    private sealed class Scope : IDisposable
    {
        private readonly ResponsesToolChoiceHintPlan? _previous;

        public Scope(ResponsesToolChoiceHintPlan? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            CurrentHint.Value = _previous;
        }
    }
}
