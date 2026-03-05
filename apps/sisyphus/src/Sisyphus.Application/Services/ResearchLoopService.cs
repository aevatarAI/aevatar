using System.Text;
using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sisyphus.Application.Models;
using Sisyphus.Application.Models.Graph;
using Sisyphus.Application.Models.Research;
using Sisyphus.Application.Models.Upload;

namespace Sisyphus.Application.Services;

public sealed class ResearchLoopService(
    ChronoGraphReadService readService,
    ChronoGraphWriteService writeService,
    GraphIdProvider graphIdProvider,
    IWorkflowRunCommandService workflowRunService,
    IOptions<ResearchOptions> researchOptions,
    ILogger<ResearchLoopService> logger)
{
    private const string ResearcherWorkflow = "sisyphus_researcher_v2";

    private CancellationTokenSource? _loopCts;
    private readonly Lock _lock = new();

    public bool IsRunning { get; private set; }
    public int CurrentRound { get; private set; }

    /// <summary>
    /// Starts the research loop. Runs indefinitely until Stop() is called.
    /// </summary>
    public async Task RunAsync(
        Func<ResearchSseEventType, object, CancellationToken, ValueTask> emitSseAsync,
        CancellationToken ct)
    {
        lock (_lock)
        {
            if (IsRunning)
                throw new InvalidOperationException("Research loop is already running");

            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            IsRunning = true;
            CurrentRound = 0;
        }

        var opts = researchOptions.Value;
        var loopCt = _loopCts.Token;

        try
        {
            logger.LogInformation("Research loop started");
            await emitSseAsync(ResearchSseEventType.LOOP_STARTED,
                ResearchSsePayloads.LoopStarted(0), loopCt);

            while (!loopCt.IsCancellationRequested)
            {
                var round = ++CurrentRound;

                logger.LogInformation("Research loop round {Round} starting", round);
                await emitSseAsync(ResearchSseEventType.ROUND_START,
                    ResearchSsePayloads.RoundStart(round), loopCt);

                // Step 1: Read blue graph
                var snapshot = await readService.GetBlueSnapshotAsync(loopCt);
                logger.LogInformation("Round {Round}: read {NodeCount} blue nodes, {EdgeCount} blue edges",
                    round, snapshot.Nodes.Count, snapshot.Edges.Count);
                await emitSseAsync(ResearchSseEventType.GRAPH_READ,
                    ResearchSsePayloads.GraphRead(round, snapshot.Nodes.Count), loopCt);

                // Step 2: Build prompt with abstracts
                var prompt = BuildResearchPrompt(snapshot, opts.NodesPerRound);

                // Step 3: Call LLM with retry
                await emitSseAsync(ResearchSseEventType.LLM_CALL_START,
                    ResearchSsePayloads.LlmCallStart(round), loopCt);

                NodePurgeResult? result = null;
                for (var attempt = 1; attempt <= opts.LlmMaxRetries; attempt++)
                {
                    logger.LogInformation("Round {Round}: LLM attempt {Attempt}/{Max}",
                        round, attempt, opts.LlmMaxRetries);

                    var llmOutput = await ExecuteWorkflowAsync(ResearcherWorkflow, prompt, loopCt);
                    if (llmOutput is null)
                    {
                        logger.LogWarning("Round {Round}: LLM returned no output on attempt {Attempt}", round, attempt);
                        await emitSseAsync(ResearchSseEventType.VALIDATION_FAILED,
                            ResearchSsePayloads.ValidationFailed(round, attempt, ["LLM returned no output"]), loopCt);
                        continue;
                    }

                    var json = UploadPipelineService.ExtractJson(llmOutput);
                    NodePurgeResult? parsed;
                    try
                    {
                        parsed = JsonSerializer.Deserialize<NodePurgeResult>(json);
                    }
                    catch (JsonException ex)
                    {
                        logger.LogWarning(ex, "Round {Round}: JSON parse failed on attempt {Attempt}", round, attempt);
                        await emitSseAsync(ResearchSseEventType.VALIDATION_FAILED,
                            ResearchSsePayloads.ValidationFailed(round, attempt, [$"JSON parse error: {ex.Message}"]), loopCt);
                        continue;
                    }

                    if (parsed is null || parsed.BlueNodes.Count == 0)
                    {
                        logger.LogWarning("Round {Round}: empty result on attempt {Attempt}", round, attempt);
                        await emitSseAsync(ResearchSseEventType.VALIDATION_FAILED,
                            ResearchSsePayloads.ValidationFailed(round, attempt, ["Empty blue_nodes"]), loopCt);
                        continue;
                    }

                    // Validate
                    var errors = UploadPipelineService.ValidateNodePurgeResult(parsed);
                    if (errors.Count > 0)
                    {
                        logger.LogWarning("Round {Round}: validation failed on attempt {Attempt}: {Errors}",
                            round, attempt, string.Join("; ", errors));
                        await emitSseAsync(ResearchSseEventType.VALIDATION_FAILED,
                            ResearchSsePayloads.ValidationFailed(round, attempt, errors), loopCt);
                        continue;
                    }

                    result = parsed;
                    break;
                }

                if (result is null)
                {
                    logger.LogWarning("Round {Round}: all LLM attempts exhausted, skipping round", round);
                    await emitSseAsync(ResearchSseEventType.ROUND_DONE,
                        ResearchSsePayloads.RoundDone(round, snapshot.Nodes.Count), loopCt);
                    continue;
                }

                await emitSseAsync(ResearchSseEventType.LLM_CALL_DONE,
                    ResearchSsePayloads.LlmCallDone(round, result.BlueNodes.Count, result.BlueEdges.Count), loopCt);

                // Step 4: Write blue nodes to graph
                var graphId = await graphIdProvider.WaitWriteAsync(loopCt);

                var blueNodeProps = result.BlueNodes.Select(bn => new Dictionary<string, object>
                {
                    ["type"] = bn.Type,
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["abstract"] = bn.Abstract,
                        ["body"] = bn.Body,
                        [SisyphusStatus.PropertyName] = SisyphusStatus.Purified,
                    },
                }).ToList();

                var blueUuids = await writeService.CreateNodesAsync(graphId, blueNodeProps, loopCt);

                // Build temp_id -> uuid mapping for edge creation
                var tempIdToUuid = new Dictionary<string, string>();
                for (var i = 0; i < result.BlueNodes.Count && i < blueUuids.Count; i++)
                    tempIdToUuid[result.BlueNodes[i].TempId] = blueUuids[i];

                // Write blue edges
                var edgesWritten = 0;
                if (result.BlueEdges.Count > 0)
                {
                    var edgeProps = result.BlueEdges
                        .Where(be => be.Source is not null && be.Target is not null
                            && tempIdToUuid.ContainsKey(be.Source) && tempIdToUuid.ContainsKey(be.Target))
                        .Select(be => new Dictionary<string, object>
                        {
                            ["source"] = tempIdToUuid[be.Source!],
                            ["target"] = tempIdToUuid[be.Target!],
                            ["type"] = be.EdgeType,
                            ["properties"] = new Dictionary<string, object>
                            {
                                [SisyphusStatus.PropertyName] = SisyphusStatus.Purified,
                            },
                        }).ToList();

                    if (edgeProps.Count > 0)
                    {
                        await writeService.CreateEdgesAsync(graphId, edgeProps, loopCt);
                        edgesWritten = edgeProps.Count;
                    }
                }

                logger.LogInformation("Round {Round}: wrote {Nodes} nodes, {Edges} edges",
                    round, blueUuids.Count, edgesWritten);
                await emitSseAsync(ResearchSseEventType.GRAPH_WRITE_DONE,
                    ResearchSsePayloads.GraphWriteDone(round, blueUuids.Count, edgesWritten), loopCt);

                var totalBlueNodes = snapshot.Nodes.Count + blueUuids.Count;
                await emitSseAsync(ResearchSseEventType.ROUND_DONE,
                    ResearchSsePayloads.RoundDone(round, totalBlueNodes), loopCt);

                // Brief pause between rounds to allow GC recovery and avoid tight-looping
                await Task.Delay(TimeSpan.FromSeconds(2), loopCt);
            }

            logger.LogInformation("Research loop stopped after {Rounds} rounds (cancellation requested)", CurrentRound);
            await emitSseAsync(ResearchSseEventType.LOOP_STOPPED,
                ResearchSsePayloads.LoopStopped(CurrentRound, "Stop requested"), CancellationToken.None);
        }
        catch (OperationCanceledException) when (loopCt.IsCancellationRequested)
        {
            logger.LogInformation("Research loop cancelled after {Rounds} rounds", CurrentRound);
            try
            {
                await emitSseAsync(ResearchSseEventType.LOOP_STOPPED,
                    ResearchSsePayloads.LoopStopped(CurrentRound, "Stop requested"), CancellationToken.None);
            }
            catch { /* SSE write may fail if client disconnected */ }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Research loop failed at round {Round}", CurrentRound);
            try
            {
                await emitSseAsync(ResearchSseEventType.LOOP_ERROR,
                    ResearchSsePayloads.LoopError(CurrentRound, ex.Message), CancellationToken.None);
            }
            catch { /* SSE write may fail */ }
            throw;
        }
        finally
        {
            lock (_lock)
            {
                IsRunning = false;
                _loopCts?.Dispose();
                _loopCts = null;
            }
        }
    }

    /// <summary>
    /// Signals the running loop to stop.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!IsRunning || _loopCts is null)
                return;

            logger.LogInformation("Research loop stop requested at round {Round}", CurrentRound);
            _loopCts.Cancel();
        }
    }

    private static string BuildResearchPrompt(BlueGraphSnapshot snapshot, int nodesPerRound)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Current Graph State ({snapshot.Nodes.Count} blue nodes)");
        sb.AppendLine();

        foreach (var node in snapshot.Nodes)
        {
            var nodeAbstract = "";
            if (node.Properties.TryGetValue("abstract", out var abstractEl) && abstractEl.ValueKind == JsonValueKind.String)
                nodeAbstract = abstractEl.GetString() ?? "";

            sb.AppendLine($"- [{node.Type}] {node.Id}: {nodeAbstract}");
        }

        sb.AppendLine();
        sb.AppendLine("## Task");
        sb.AppendLine();
        sb.AppendLine($"Generate {nodesPerRound} new blue nodes that fill knowledge gaps in the graph above.");
        sb.AppendLine("Focus on missing proofs, definitions, or theorems that would strengthen the graph.");

        return sb.ToString();
    }

    private async Task<string?> ExecuteWorkflowAsync(string workflowName, string prompt, CancellationToken ct)
    {
        string? lastMessage = null;

        await workflowRunService.ExecuteAsync(
            new WorkflowChatRunRequest(prompt, workflowName, null),
            (frame, token) =>
            {
                if (frame.Type == "TEXT_MESSAGE_CONTENT" && frame.Delta is not null)
                    lastMessage = (lastMessage ?? "") + frame.Delta;
                else if (frame.Type == "RUN_FINISHED" && frame.Result is JsonElement je)
                    lastMessage = je.GetRawText();
                return ValueTask.CompletedTask;
            },
            ct: ct);

        return lastMessage;
    }
}
