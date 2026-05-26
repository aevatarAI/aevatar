using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Hooks.BuiltIn;

namespace Aevatar.AI.Core.Tests.Hooks.BuiltIn;

public class BudgetMonitorHookTests
{
    [Fact]
    public async Task OnLLMRequestStart_NoHistoryItem_DoesNothing()
    {
        var hook = new BudgetMonitorHook
        {
            WarningThreshold = 2,
        };

        var ctx = new AIGAgentExecutionHookContext();
        await hook.OnLLMRequestStartAsync(ctx, CancellationToken.None);
    }

    [Fact]
    public async Task OnLLMRequestStart_HistoryBelowOrEqualThreshold_DoesNothing()
    {
        var hook = new BudgetMonitorHook { WarningThreshold = 2 };
        var ctx = new AIGAgentExecutionHookContext();
        ctx.Items["history_count"] = 2;

        await hook.OnLLMRequestStartAsync(ctx, CancellationToken.None);
    }

    [Fact]
    public async Task OnLLMRequestStart_HistoryAboveThresholdTriggersWarningPath()
    {
        var hook = new BudgetMonitorHook { WarningThreshold = 2 };
        var ctx = new AIGAgentExecutionHookContext();
        ctx.Items["history_count"] = 3;

        await hook.OnLLMRequestStartAsync(ctx, CancellationToken.None);
    }

    [Fact]
    public async Task OnLLMRequestEnd_NoUsage_DoesNothing()
    {
        var hook = new BudgetMonitorHook
        {
            TokenWarningThreshold = 2,
        };

        var ctx = new AIGAgentExecutionHookContext
        {
            LLMResponse = new LLMResponse { Content = "ok" },
        };

        await hook.OnLLMRequestEndAsync(ctx, CancellationToken.None);
    }

    [Fact]
    public async Task OnLLMRequestEnd_UsageUnderThreshold_DoesNothing()
    {
        var hook = new BudgetMonitorHook { TokenWarningThreshold = 1000 };
        var ctx = new AIGAgentExecutionHookContext
        {
            LLMResponse = new LLMResponse
            {
                Usage = new TokenUsage(PromptTokens: 30, CompletionTokens: 10, TotalTokens: 40),
                Content = "ok",
            },
        };

        await hook.OnLLMRequestEndAsync(ctx, CancellationToken.None);
    }

    [Fact]
    public async Task OnLLMRequestEnd_UsageOverThreshold_TriggersWarningPath()
    {
        var hook = new BudgetMonitorHook { TokenWarningThreshold = 10 };
        var ctx = new AIGAgentExecutionHookContext
        {
            LLMResponse = new LLMResponse
            {
                Usage = new TokenUsage(PromptTokens: 50, CompletionTokens: 5, TotalTokens: 55),
                Content = "ok",
            },
        };

        await hook.OnLLMRequestEndAsync(ctx, CancellationToken.None);
    }
}
