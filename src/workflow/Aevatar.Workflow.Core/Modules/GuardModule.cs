using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Data validation / assertion module.
/// Runs a configurable check against the input. On failure, applies <c>on_fail</c> strategy.
/// Supported checks: not_empty, json_valid, regex, max_length, contains.
/// </summary>
public sealed class GuardModule : IEventModule
{
    public string Name => "guard";
    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
        if (request.StepType != "guard") return;

        var check = request.Parameters.GetValueOrDefault("check", "not_empty");
        var input = request.Input ?? "";
        var (passed, reason) = RunCheck(check, input, request.Parameters);
        var onFail = request.Parameters.GetValueOrDefault("on_fail", "fail");

        if (passed)
        {
            ctx.Logger.LogInformation("Guard {StepId}: check={Check} passed", request.StepId, check);
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId, RunId = request.RunId, Success = true, Output = input,
            }, EventDirection.Self, ct);
            return;
        }

        ctx.Logger.LogWarning("Guard {StepId}: check={Check} failed: {Reason}", request.StepId, check, reason);

        if (onFail == "skip")
        {
            var completed = new StepCompletedEvent
            {
                StepId = request.StepId, RunId = request.RunId, Success = true, Output = input,
            };
            completed.Metadata["guard.skipped"] = "true";
            completed.Metadata["guard.reason"] = reason;
            await ctx.PublishAsync(completed, EventDirection.Self, ct);
        }
        else if (onFail == "branch" && request.Parameters.TryGetValue("branch_target", out var target))
        {
            var completed = new StepCompletedEvent
            {
                StepId = request.StepId, RunId = request.RunId, Success = true, Output = input,
            };
            completed.Metadata["branch"] = target;
            completed.Metadata["guard.reason"] = reason;
            await ctx.PublishAsync(completed, EventDirection.Self, ct);
        }
        else
        {
            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId, RunId = request.RunId, Success = false, Error = $"guard check '{check}' failed: {reason}",
            }, EventDirection.Self, ct);
        }
    }

    private static (bool Passed, string Reason) RunCheck(string check, string input, IReadOnlyDictionary<string, string> parameters)
    {
        switch (check.ToLowerInvariant())
        {
            case "not_empty":
                return string.IsNullOrWhiteSpace(input)
                    ? (false, "input is empty")
                    : (true, "");

            case "json_valid":
                try { JsonDocument.Parse(input); return (true, ""); }
                catch (JsonException ex) { return (false, ex.Message); }

            case "regex":
                var pattern = parameters.GetValueOrDefault("pattern", "");
                if (string.IsNullOrEmpty(pattern)) return (false, "missing regex pattern parameter");
                try { return Regex.IsMatch(input, pattern) ? (true, "") : (false, $"input does not match /{pattern}/"); }
                catch (RegexParseException ex) { return (false, $"invalid regex: {ex.Message}"); }

            case "max_length":
                if (!int.TryParse(parameters.GetValueOrDefault("max", "0"), out var max) || max <= 0)
                    return (false, "missing or invalid 'max' parameter");
                return input.Length <= max ? (true, "") : (false, $"length {input.Length} exceeds max {max}");

            case "contains":
                var keyword = parameters.GetValueOrDefault("keyword", "");
                return string.IsNullOrEmpty(keyword)
                    ? (false, "missing 'keyword' parameter")
                    : input.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                        ? (true, "")
                        : (false, $"input does not contain '{keyword}'");

            default:
                return (false, $"unknown check type: {check}");
        }
    }
}
