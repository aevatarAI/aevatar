using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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

    // ── Subscriber broadcasting ──
    private readonly ConcurrentDictionary<Guid, Channel<ResearchSseMessage>> _subscribers = new();

    /// <summary>
    /// Subscribe to the research event stream. Returns a channel reader
    /// that receives all events broadcast by the running loop.
    /// Call <see cref="Unsubscribe"/> when done.
    /// </summary>
    public (Guid Id, ChannelReader<ResearchSseMessage> Reader) Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<ResearchSseMessage>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        _subscribers[id] = channel;
        logger.LogDebug("SSE subscriber {Id} registered ({Count} total)", id, _subscribers.Count);
        return (id, channel.Reader);
    }

    /// <summary>
    /// Remove a subscriber. Completes its channel so the reader stops.
    /// </summary>
    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
            logger.LogDebug("SSE subscriber {Id} removed ({Count} remaining)", id, _subscribers.Count);
        }
    }

    private void Broadcast(ResearchSseEventType type, object payload)
    {
        var msg = new ResearchSseMessage(type, payload);
        foreach (var (id, channel) in _subscribers)
        {
            if (!channel.Writer.TryWrite(msg))
            {
                logger.LogDebug("Subscriber {Id} channel full, message dropped", id);
            }
        }
    }

    /// <summary>
    /// Starts the research loop. Runs indefinitely until Stop() is called.
    /// Events are broadcast to all subscribers.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
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
            Broadcast(ResearchSseEventType.LOOP_STARTED, ResearchSsePayloads.LoopStarted(0));

            while (!loopCt.IsCancellationRequested)
            {
                var round = ++CurrentRound;

                logger.LogInformation("Research loop round {Round} starting", round);
                Broadcast(ResearchSseEventType.ROUND_START, ResearchSsePayloads.RoundStart(round));

                // Step 1: Read blue graph
                var snapshot = await readService.GetBlueSnapshotAsync(loopCt);
                logger.LogInformation("Round {Round}: read {NodeCount} blue nodes, {EdgeCount} blue edges",
                    round, snapshot.Nodes.Count, snapshot.Edges.Count);
                Broadcast(ResearchSseEventType.GRAPH_READ,
                    ResearchSsePayloads.GraphRead(round, snapshot.Nodes.Count));

                // Step 2: Build prompt with abstracts
                var prompt = BuildResearchPrompt(snapshot, opts.NodesPerRound);

                // Step 3: Call LLM with retry
                Broadcast(ResearchSseEventType.LLM_CALL_START, ResearchSsePayloads.LlmCallStart(round));

                NodePurgeResult? result = null;
                for (var attempt = 1; attempt <= opts.LlmMaxRetries; attempt++)
                {
                    logger.LogInformation("Round {Round}: LLM attempt {Attempt}/{Max}",
                        round, attempt, opts.LlmMaxRetries);

                    var llmOutput = await ExecuteWorkflowAsync(ResearcherWorkflow, prompt, round, loopCt);
                    if (llmOutput is null)
                    {
                        logger.LogWarning("Round {Round}: LLM returned no output on attempt {Attempt}", round, attempt);
                        Broadcast(ResearchSseEventType.VALIDATION_FAILED,
                            ResearchSsePayloads.ValidationFailed(round, attempt, ["LLM returned no output"]));
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
                        Broadcast(ResearchSseEventType.VALIDATION_FAILED,
                            ResearchSsePayloads.ValidationFailed(round, attempt, [$"JSON parse error: {ex.Message}"]));
                        continue;
                    }

                    if (parsed is null || parsed.BlueNodes.Count == 0)
                    {
                        logger.LogWarning("Round {Round}: empty result on attempt {Attempt}", round, attempt);
                        Broadcast(ResearchSseEventType.VALIDATION_FAILED,
                            ResearchSsePayloads.ValidationFailed(round, attempt, ["Empty blue_nodes"]));
                        continue;
                    }

                    // Validate
                    var errors = UploadPipelineService.ValidateNodePurgeResult(parsed);
                    if (errors.Count > 0)
                    {
                        logger.LogWarning("Round {Round}: validation failed on attempt {Attempt}: {Errors}",
                            round, attempt, string.Join("; ", errors));
                        Broadcast(ResearchSseEventType.VALIDATION_FAILED,
                            ResearchSsePayloads.ValidationFailed(round, attempt, errors));
                        continue;
                    }

                    result = parsed;
                    break;
                }

                if (result is null)
                {
                    logger.LogWarning("Round {Round}: all LLM attempts exhausted, skipping round", round);
                    Broadcast(ResearchSseEventType.ROUND_DONE,
                        ResearchSsePayloads.RoundDone(round, snapshot.Nodes.Count));
                    continue;
                }

                Broadcast(ResearchSseEventType.LLM_CALL_DONE,
                    ResearchSsePayloads.LlmCallDone(round, result.BlueNodes.Count, result.BlueEdges.Count));

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
                Broadcast(ResearchSseEventType.GRAPH_WRITE_DONE,
                    ResearchSsePayloads.GraphWriteDone(round, blueUuids.Count, edgesWritten));

                var totalBlueNodes = snapshot.Nodes.Count + blueUuids.Count;
                Broadcast(ResearchSseEventType.ROUND_DONE,
                    ResearchSsePayloads.RoundDone(round, totalBlueNodes));

                // Brief pause between rounds to allow GC recovery and avoid tight-looping
                await Task.Delay(TimeSpan.FromSeconds(2), loopCt);
            }

            logger.LogInformation("Research loop stopped after {Rounds} rounds (cancellation requested)", CurrentRound);
            Broadcast(ResearchSseEventType.LOOP_STOPPED,
                ResearchSsePayloads.LoopStopped(CurrentRound, "Stop requested"));
        }
        catch (OperationCanceledException) when (loopCt.IsCancellationRequested)
        {
            logger.LogInformation("Research loop cancelled after {Rounds} rounds", CurrentRound);
            Broadcast(ResearchSseEventType.LOOP_STOPPED,
                ResearchSsePayloads.LoopStopped(CurrentRound, "Stop requested"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Research loop failed at round {Round}", CurrentRound);
            Broadcast(ResearchSseEventType.LOOP_ERROR,
                ResearchSsePayloads.LoopError(CurrentRound, ex.Message));
            throw;
        }
        finally
        {
            // Complete all subscriber channels so readers finish
            foreach (var (_, channel) in _subscribers)
                channel.Writer.TryComplete();

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

    /// <summary>
    /// Hard limit on prompt size in characters to prevent LLM context overflow
    /// and Orleans message size errors. ~800K chars ≈ ~200K tokens.
    /// </summary>
    private const int MaxPromptChars = 800_000;

    private static string BuildResearchPrompt(BlueGraphSnapshot snapshot, int nodesPerRound)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Current Graph State ({snapshot.Nodes.Count} blue nodes)");
        sb.AppendLine();

        var includedCount = 0;
        var truncated = false;
        foreach (var node in snapshot.Nodes)
        {
            var nodeAbstract = "";
            if (node.Properties.TryGetValue("abstract", out var abstractEl) && abstractEl.ValueKind == JsonValueKind.String)
                nodeAbstract = abstractEl.GetString() ?? "";

            var line = $"- [{node.Type}] {node.Id}: {nodeAbstract}\n";

            // Hard cutoff: stop adding nodes if we'd exceed the limit
            if (sb.Length + line.Length > MaxPromptChars - 500) // reserve 500 chars for task section
            {
                truncated = true;
                break;
            }

            sb.Append(line);
            includedCount++;
        }

        if (truncated)
        {
            sb.AppendLine();
            sb.AppendLine($"... ({snapshot.Nodes.Count - includedCount} more nodes omitted due to context limit)");
        }

        sb.AppendLine();
        sb.AppendLine("## Task");
        sb.AppendLine();
        sb.AppendLine($"Generate {nodesPerRound} new blue nodes that fill knowledge gaps in the graph above.");
        sb.AppendLine("Focus on missing proofs, definitions, or theorems that would strengthen the graph.");

        return sb.ToString();
    }

    private async Task<string?> ExecuteWorkflowAsync(
        string workflowName,
        string prompt,
        int round,
        CancellationToken ct)
    {
        string? lastMessage = null;

        await workflowRunService.ExecuteAsync(
            new WorkflowChatRunRequest(prompt, workflowName, null),
            async (frame, token) =>
            {
                if (frame.Type == "TEXT_MESSAGE_CONTENT" && frame.Delta is not null)
                {
                    lastMessage = (lastMessage ?? "") + frame.Delta;
                    Broadcast(ResearchSseEventType.LLM_TOKEN,
                        ResearchSsePayloads.LlmToken(round, frame.Delta));
                }
                else if (frame.Type == "RUN_FINISHED" && frame.Result is JsonElement je)
                {
                    lastMessage = je.GetRawText();
                }
            },
            ct: ct);

        return lastMessage;
    }
}
