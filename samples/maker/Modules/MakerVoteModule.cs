using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Workflows.Core;
using Aevatar.Foundation.Abstractions.EventModules;

namespace Aevatar.Sample.Maker.Modules;

/// <summary>
/// MAKER voting primitive:
/// first-to-ahead-by-k with red-flag filtering by response length.
/// </summary>
public sealed class MakerVoteModule : IEventModule
{
    public string Name => "maker_vote";
    public int Priority => 6;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.TypeUrl?.Contains("StepRequestEvent") == true;

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
        if (!string.Equals(request.StepType, "maker_vote", StringComparison.OrdinalIgnoreCase))
            return;

        var k = request.Parameters.TryGetValue("k", out var kRaw) && int.TryParse(kRaw, out var parsedK) ? parsedK : 1;
        var maxLen = request.Parameters.TryGetValue("max_response_length", out var mlRaw) && int.TryParse(mlRaw, out var parsedMl)
            ? parsedMl
            : 2200;

        var rawCandidates = request.Input.Split("\n---\n", StringSplitOptions.RemoveEmptyEntries);
        if (rawCandidates.Length == 0)
        {
            await PublishFailureAsync(ctx, request, "No candidates provided for voting", ct, new Dictionary<string, string>
            {
                ["maker_vote.total_candidates"] = "0",
                ["maker_vote.red_flagged"] = "0",
                ["maker_vote.valid_candidates"] = "0",
                ["maker_vote.k"] = k.ToString(),
                ["maker_vote.max_response_length"] = maxLen.ToString(),
            });
            return;
        }

        var valid = new List<string>(rawCandidates.Length);
        var flagged = 0;
        foreach (var item in rawCandidates)
        {
            var candidate = item.Trim();
            if (candidate.Length == 0) continue;
            if (candidate.Length > maxLen)
            {
                flagged++;
                continue;
            }
            valid.Add(candidate);
        }

        if (valid.Count == 0)
        {
            await PublishFailureAsync(
                ctx,
                request,
                $"All {rawCandidates.Length} candidates were red-flagged (max_response_length={maxLen})",
                ct,
                new Dictionary<string, string>
                {
                    ["maker_vote.total_candidates"] = rawCandidates.Length.ToString(),
                    ["maker_vote.red_flagged"] = flagged.ToString(),
                    ["maker_vote.valid_candidates"] = "0",
                    ["maker_vote.k"] = k.ToString(),
                    ["maker_vote.max_response_length"] = maxLen.ToString(),
                });
            return;
        }

        var voteCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var candidate in valid)
        {
            voteCounts.TryGetValue(candidate, out var count);
            voteCounts[candidate] = count + 1;
        }

        var sorted = voteCounts.OrderByDescending(x => x.Value).ToList();
        var topVotes = sorted[0].Value;
        var runnerUpVotes = sorted.Count > 1 ? sorted[1].Value : 0;
        var useMajorityFallback = topVotes - runnerUpVotes < k;
        var winner = sorted[0].Key;

        var completed = new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true,
            Output = winner,
        };
        completed.Metadata["maker_vote.total_candidates"] = rawCandidates.Length.ToString();
        completed.Metadata["maker_vote.red_flagged"] = flagged.ToString();
        completed.Metadata["maker_vote.valid_candidates"] = valid.Count.ToString();
        completed.Metadata["maker_vote.k"] = k.ToString();
        completed.Metadata["maker_vote.max_response_length"] = maxLen.ToString();
        completed.Metadata["maker_vote.top_votes"] = topVotes.ToString();
        completed.Metadata["maker_vote.runner_up_votes"] = runnerUpVotes.ToString();
        completed.Metadata["maker_vote.used_majority_fallback"] = useMajorityFallback.ToString();

        await ctx.PublishAsync(completed, EventDirection.Self, ct);
    }

    private static async Task PublishFailureAsync(
        IEventHandlerContext ctx,
        StepRequestEvent request,
        string error,
        CancellationToken ct,
        Dictionary<string, string>? metadata = null)
    {
        var completed = new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = false,
            Error = error,
        };
        if (metadata != null)
        {
            foreach (var (key, value) in metadata)
                completed.Metadata[key] = value;
        }

        await ctx.PublishAsync(completed, EventDirection.Self, ct);
    }
}
